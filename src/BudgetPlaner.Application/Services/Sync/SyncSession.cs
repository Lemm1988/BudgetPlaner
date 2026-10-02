using System.Collections;
using System.Text.Json;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;

namespace BudgetPlaner.Application.Services.Sync;

/// <summary>Eine Änderung (aus Sync-Push, REST-API oder internem Dienst).</summary>
public sealed record EntityMutation(Guid OpId, string Type, Guid Id, bool Delete, object? Data, object? Base, long? ExpectedVersion = null)
{
    public static EntityMutation Upsert(HouseholdEntity entity, long? expectedVersion = null) =>
        new(Guid.NewGuid(), SyncTypes.NameOf(entity), entity.Id, false, entity, null, expectedVersion);

    public static EntityMutation Remove(string type, Guid id, long? expectedVersion = null) =>
        new(Guid.NewGuid(), type, id, true, null, null, expectedVersion);

    public static EntityMutation Settings(Household household) =>
        new(Guid.NewGuid(), SyncTypes.Household, household.Id, false, household, null);
}

/// <summary>
/// Arbeitsbereich eines Schreibvorgangs: wendet Änderungen auf eine vollständige Kopie der Haushaltsdaten an,
/// validiert, führt Kaskaden und Nachverarbeitung aus und merkt sich alles, was gespeichert werden muss.
/// </summary>
public sealed class SyncSession
{
    private readonly HouseholdData _data;
    private readonly Dictionary<(string, Guid), HouseholdEntity> _lookup = [];
    private readonly HashSet<HouseholdEntity> _added = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HouseholdEntity> _modified = new(ReferenceEqualityComparer.Instance);
    private readonly List<ChangeLogEntry> _logs = [];
    private readonly HashSet<Guid> _touchedDebts = [];
    private readonly Household _originalHousehold;

    public SyncSession(HouseholdData data, UserContext? ctx, DateTimeOffset now, DateOnly today)
    {
        _data = data;
        Ctx = ctx;
        Now = now;
        Today = today;
        _originalHousehold = (Household)EntityMerge.Clone(data.Household);
        foreach (var e in data.AllEntities()) _lookup[(SyncTypes.NameOf(e), e.Id)] = e;
    }

    public HouseholdData Data => _data;
    public UserContext? Ctx { get; }
    public DateTimeOffset Now { get; }
    public DateOnly Today { get; }
    public Guid HouseholdId => _data.Household.Id;
    public bool HouseholdChanged { get; private set; }
    public IReadOnlyCollection<HouseholdEntity> Added => _added;
    public IReadOnlyCollection<HouseholdEntity> Modified => _modified;
    public IReadOnlyList<ChangeLogEntry> Logs => _logs;
    public List<string> ConflictLabels { get; } = [];
    public List<DebtEntry> PaidOffDebts { get; } = [];
    public bool HasChanges => _added.Count > 0 || _modified.Count > 0 || HouseholdChanged;
    private string Lang => Ctx?.Language ?? Texts.German;
    private Guid? UserId => Ctx?.UserId;

    public HouseholdEngine Engine()
    {
        _data.Invalidate();
        return new HouseholdEngine(_data, Today);
    }

    public T? Find<T>(Guid id) where T : HouseholdEntity =>
        _lookup.TryGetValue((SyncTypes.ClrTypes.First(kv => kv.Value == typeof(T)).Key, id), out var e) && !e.IsDeleted ? (T)e : null;

    // ------------------------------------------------------------------ Anwenden

    public SyncOpResult Apply(EntityMutation m)
    {
        try
        {
            if (!SyncTypes.ClrTypes.ContainsKey(m.Type)) throw AppException.BadRequest("err.sync.unknownType");
            if (m.Type == SyncTypes.Household) return ApplyHousehold(m);
            return m.Delete ? ApplyDelete(m) : ApplyUpsert(m);
        }
        catch (AppException ex) when (ex.StatusCode != 412)
        {
            return new SyncOpResult(m.OpId, SyncOpStatus.Rejected, ex.ErrorKey, Texts.Get(Lang, ex.ErrorKey, ex.Args));
        }
    }

    private SyncOpResult ApplyUpsert(EntityMutation m)
    {
        if (m.Data is not HouseholdEntity incoming) throw AppException.BadRequest("err.validation");
        var id = NormalizeId(incoming, m.Id);
        _lookup.TryGetValue((m.Type, id), out var existing);
        if (m.ExpectedVersion is { } ev && existing is not null && existing.Version != ev) throw AppException.PreconditionFailed();

        if (incoming is MonthSnapshot snap) return ApplySnapshot(m, snap, existing as MonthSnapshot, id);

        if (existing is null)
        {
            var entity = (HouseholdEntity)EntityMerge.Clone(incoming);
            entity.Id = id;
            entity.HouseholdId = HouseholdId;
            entity.CreatedAt = incoming.CreatedAt == default ? Now : incoming.CreatedAt;
            entity.UpdatedAt = Now;
            entity.UpdatedBy = UserId;
            entity.IsDeleted = false;
            entity.IsDemo = false;
            Validate(entity, null);
            AddEntity(entity);
            Log("create", entity, null, entity, false);
            return new SyncOpResult(m.OpId, SyncOpStatus.Applied);
        }

        if (existing.IsDeleted)
        {
            // Bearbeitung nach Löschung → Datensatz wiederherstellen (kein Datenverlust).
            var revived = (HouseholdEntity)EntityMerge.Clone(incoming);
            EntityMerge.CopyServerFields(existing, revived);
            revived.IsDeleted = false;
            revived.UpdatedBy = UserId;
            Validate(revived, existing);
            var before = Json(existing);
            Replace(existing, revived);
            Log("restore", revived, before, revived, true);
            ConflictLabels.Add(EntityMerge.LabelOf(revived));
            return new SyncOpResult(m.OpId, SyncOpStatus.Conflict);
        }

        var target = (HouseholdEntity)EntityMerge.Clone(existing);
        var merge = EntityMerge.Merge(target, incoming, m.Base);
        if (!merge.Changed) return new SyncOpResult(m.OpId, SyncOpStatus.NoChange);
        target.UpdatedBy = UserId;
        Validate(target, existing);
        var beforeJson = Json(existing);
        Replace(existing, target);
        Log("update", target, beforeJson, target, merge.Conflict);
        if (merge.Conflict) ConflictLabels.Add(EntityMerge.LabelOf(target));
        var merged = m.Base is HouseholdEntity b && b.Version < existing.Version;
        return new SyncOpResult(m.OpId, merge.Conflict ? SyncOpStatus.Conflict : merged ? SyncOpStatus.Merged : SyncOpStatus.Applied);
    }

    private SyncOpResult ApplyDelete(EntityMutation m)
    {
        if (!_lookup.TryGetValue((m.Type, m.Id), out var existing) || existing.IsDeleted)
            return new SyncOpResult(m.OpId, SyncOpStatus.NoChange);
        if (m.ExpectedVersion is { } ev && existing.Version != ev) throw AppException.PreconditionFailed();

        switch (existing)
        {
            case Category c:
                if (c.IsSystem) throw AppException.BadRequest("err.category.system");
                DeleteCategory(c);
                break;
            case BudgetEntry e:
                MarkDeleted(e);
                foreach (var ch in _data.EntryChanges.Where(x => x.EntryId == e.Id && !x.IsDeleted).ToList()) MarkDeleted(ch, log: false);
                foreach (var mo in _data.EntryMonths.Where(x => x.EntryId == e.Id && !x.IsDeleted).ToList()) MarkDeleted(mo, log: false);
                break;
            case DebtEntry d:
                MarkDeleted(d);
                foreach (var p in _data.DebtPayments.Where(x => x.DebtId == d.Id && !x.IsDeleted).ToList()) MarkDeleted(p, log: false);
                foreach (var e in _data.Entries.Where(x => x.DebtId == d.Id && !x.IsDeleted).ToList())
                    Modify(e, x => { x.DebtId = null; x.DebtRole = DebtRole.None; });
                break;
            case BufferEntry b:
                MarkDeleted(b);
                foreach (var t in _data.BufferTransactions.Where(x => x.BufferId == b.Id && !x.IsDeleted).ToList()) MarkDeleted(t, log: false);
                EndAllocationEntries(b.Id);
                break;
            case DebtPayment p:
                MarkDeleted(p);
                _touchedDebts.Add(p.DebtId);
                break;
            default:
                MarkDeleted(existing);
                break;
        }
        return new SyncOpResult(m.OpId, SyncOpStatus.Applied);
    }

    private SyncOpResult ApplyHousehold(EntityMutation m)
    {
        if (m.Delete || m.Data is not Household incoming) throw AppException.BadRequest("err.validation");
        var current = _data.Household;
        var target = (Household)EntityMerge.Clone(current);
        var merge = EntityMerge.Merge(target, incoming, m.Base);
        if (!merge.Changed) return new SyncOpResult(m.OpId, SyncOpStatus.NoChange);
        var ownerOnly = merge.ChangedFields.Any(f => f != nameof(Household.OnboardingCompletedAt));
        if (ownerOnly && Ctx is not null && !Ctx.IsOwner) throw AppException.Forbidden();

        target.Name = (target.Name ?? "").Trim();
        if (target.Name.Length == 0) throw AppException.BadRequest("err.name.required");
        if (Fmt.Currencies.All(c => c.Code != target.Currency)) throw AppException.BadRequest("err.currency.invalid");

        // F-101: Monatsstart-Änderungen wirken nur auf künftige Perioden.
        var currentPeriod = new PeriodCalendar(current.MonthStartRules).PeriodOf(Today);
        var kept = current.MonthStartRules.Where(r => r.FromPeriod <= currentPeriod).ToList();
        var future = target.MonthStartRules.Where(r => r.FromPeriod > currentPeriod).ToList();
        if (future.Any(r => r.Day is < 1 or > 28)) throw AppException.BadRequest("err.monthStart.range");
        target.MonthStartRules = [.. kept.Concat(future).GroupBy(r => r.FromPeriod).Select(g => g.Last()).OrderBy(r => r.FromPeriod.Index)];
        if (target.MonthStartRules.Count == 0) target.MonthStartRules = current.MonthStartRules;

        target.UpdatedBy = UserId;
        target.UpdatedAt = Now;
        _logs.Add(NewLog("update", SyncTypes.Household, target.Id, Json(current), Json(target), merge.Conflict, target.Name));
        _data.Household = target;
        HouseholdChanged = true;
        return new SyncOpResult(m.OpId, merge.Conflict ? SyncOpStatus.Conflict : SyncOpStatus.Applied);
    }

    private SyncOpResult ApplySnapshot(EntityMutation m, MonthSnapshot incoming, MonthSnapshot? existing, Guid id)
    {
        var engine = Engine();
        var start = engine.Calendar.Start(incoming.Period);
        if (existing is { IsDeleted: false })
        {
            // Nur der erfasste Kontostand ist nachträglich änderbar.
            if (EntityMerge.ValueEquals(existing.ClosingBalance, incoming.ClosingBalance)) return new SyncOpResult(m.OpId, SyncOpStatus.NoChange);
            Modify(existing, s => s.ClosingBalance = incoming.ClosingBalance);
            return new SyncOpResult(m.OpId, SyncOpStatus.Applied);
        }
        if (start > Today) throw AppException.BadRequest("err.month.notStarted");

        Guid? bufferId = incoming.CarryOverBufferId is { } bid && Find<BufferEntry>(bid) is not null ? bid : null;
        var unverplant = engine.Month(incoming.Period).Unverplant;
        var carry = bufferId is not null ? 0 : incoming.CarryOver != 0 ? unverplant : 0;
        var snapshot = BudgetCalculator.CreateSnapshot(engine, incoming.Period, carry, bufferId, incoming.ClosingBalance, Now, UserId);
        snapshot.Id = id;
        if (existing is not null)
        {
            snapshot.CreatedAt = existing.CreatedAt;
            snapshot.IsDemo = existing.IsDemo;
            Replace(existing, snapshot);
        }
        else AddEntity(snapshot);
        Log("create", snapshot, null, snapshot, false);
        return new SyncOpResult(m.OpId, SyncOpStatus.Applied);
    }

    // ------------------------------------------------------------------ Validierung

    private void Validate(HouseholdEntity entity, HouseholdEntity? before)
    {
        switch (entity)
        {
            case Category c:
            {
                c.Name = string.IsNullOrWhiteSpace(c.Name) ? null : c.Name.Trim();
                if (before is Category old)
                {
                    c.IsSystem = old.IsSystem;
                    c.DefaultKey = old.DefaultKey;
                    if (old.IsSystem) { c.ParentId = old.ParentId; c.Type = old.Type; }
                }
                else { c.IsSystem = false; c.DefaultKey = null; }
                if (c.Name is null && c.DefaultKey is null) throw AppException.BadRequest("err.name.required");
                if (c.ParentId is { } pid)
                {
                    var parent = Find<Category>(pid);
                    if (parent is null || parent.Id == c.Id || parent.ParentId is not null || parent.Type != c.Type)
                        throw AppException.BadRequest("err.category.invalid");
                    c.Group = parent.Group;
                }
                else if (c.Type == CategoryType.Income) c.Group = BudgetGroup.None;
                if (before is Category oldCat && oldCat.Group != c.Group)
                    foreach (var child in _data.Categories.Where(x => x.ParentId == c.Id && !x.IsDeleted).ToList())
                        Modify(child, x => x.Group = c.Group);
                break;
            }
            case BudgetEntry e:
            {
                e.Name = (e.Name ?? "").Trim();
                if (e.Name.Length == 0) throw AppException.BadRequest("err.name.required");
                if (e.Amount < 0) throw AppException.BadRequest("err.amount.negative");
                if (e.DebitDay is < 1 or > 31) throw AppException.BadRequest("err.debitDay.range");
                if (e.EndPeriod is { } end && end < e.StartPeriod) throw AppException.BadRequest("err.entry.period");
                var cat = Find<Category>(e.CategoryId) ?? throw AppException.BadRequest("err.category.invalid");
                var expected = e.Kind == EntryKind.Income ? CategoryType.Income : CategoryType.Expense;
                if (cat.Type != expected) throw AppException.BadRequest("err.category.type");
                if (e.Kind == EntryKind.Allocation)
                {
                    if (e.BufferId is not { } b || Find<BufferEntry>(b) is null) throw AppException.BadRequest("err.buffer.invalid");
                }
                else e.BufferId = null;
                if (e.DebtId is { } d && Find<DebtEntry>(d) is null) e.DebtId = null;
                if (e.DebtId is null) e.DebtRole = DebtRole.None;
                if (e.DebtId is not null) _touchedDebts.Add(e.DebtId.Value);
                break;
            }
            case BudgetEntryChange c:
                if (Find<BudgetEntry>(c.EntryId) is null) throw AppException.BadRequest("err.entry.invalid");
                if (c.Amount < 0) throw AppException.BadRequest("err.amount.negative");
                break;
            case BudgetEntryMonth mo:
            {
                if (Find<BudgetEntry>(mo.EntryId) is null) throw AppException.BadRequest("err.entry.invalid");
                if (mo.PlannedAmount < 0 || mo.ActualAmount < 0) throw AppException.BadRequest("err.amount.negative");
                if (_data.Snapshots.Any(s => !s.IsDeleted && s.Period == mo.Period))
                    throw AppException.BadRequest("err.month.closed", Fmt.PeriodName(mo.Period, Lang));
                if (mo.IsDone && mo.DoneAt is null) mo.DoneAt = Now;
                if (!mo.IsDone) mo.DoneAt = null;
                break;
            }
            case DebtEntry d:
                d.Name = (d.Name ?? "").Trim();
                if (d.Name.Length == 0) throw AppException.BadRequest("err.name.required");
                if (d.OpeningBalance < 0 || d.OriginalAmount < 0 || d.MonthlyRate < 0 || d.ResidualValue < 0 || d.InterestRate is < 0 or > 100)
                    throw AppException.BadRequest("err.amount.negative");
                if (d.DebitDay is < 1 or > 31) throw AppException.BadRequest("err.debitDay.range");
                if (d.OriginalAmount < d.OpeningBalance) d.OriginalAmount = d.OpeningBalance;
                _touchedDebts.Add(d.Id);
                break;
            case DebtPayment p:
            {
                var debt = Find<DebtEntry>(p.DebtId) ?? throw AppException.BadRequest("err.debt.invalid");
                if (p.Amount <= 0) throw AppException.BadRequest("err.amount.required");
                if (before is null && p.InterestRateApplied is null && !p.IsExtra) p.InterestRateApplied = debt.InterestRate;
                _touchedDebts.Add(p.DebtId);
                break;
            }
            case BufferEntry b:
                b.Name = (b.Name ?? "").Trim();
                if (b.Name.Length == 0) throw AppException.BadRequest("err.name.required");
                if (b.TargetAmount < 0) throw AppException.BadRequest("err.amount.negative");
                break;
            case BufferTransaction t:
            {
                if (Find<BufferEntry>(t.BufferId) is null) throw AppException.BadRequest("err.buffer.invalid");
                if (t.Amount <= 0) throw AppException.BadRequest("err.amount.required");
                t.Reason = string.IsNullOrWhiteSpace(t.Reason) ? null : t.Reason.Trim();
                if (t.Type == BufferTransactionType.Withdrawal)
                {
                    if (t.Reason is null) throw AppException.BadRequest("err.buffer.reasonRequired");
                    var available = BalanceWithout(t.BufferId, before?.Id ?? t.Id);
                    if (t.Amount > available) throw AppException.BadRequest("err.buffer.withdrawalExceeds", Fmt.Money(available, _data.Household.Currency, Lang));
                }
                break;
            }
            case BalanceCheckpoint:
                break;
        }
    }

    /// <summary>Aktueller Polsterstand ohne eine bestimmte Transaktion (F-042: Entnahme max. bis zum Stand).</summary>
    private decimal BalanceWithout(Guid bufferId, Guid transactionId)
    {
        var existing = _data.BufferTransactions.FirstOrDefault(x => x.Id == transactionId && !x.IsDeleted);
        if (existing is not null) existing.IsDeleted = true;
        try { return Engine().Buffers.Status(bufferId).Balance; }
        finally
        {
            if (existing is not null) existing.IsDeleted = false;
            _data.Invalidate();
        }
    }

    private Guid NormalizeId(HouseholdEntity e, Guid id) => e switch
    {
        BudgetEntryMonth m => DeterministicId.ForEntryMonth(m.EntryId, m.Period),
        BudgetEntryChange c => DeterministicId.ForEntryChange(c.EntryId, c.FromPeriod),
        MonthSnapshot s => DeterministicId.ForSnapshot(HouseholdId, s.Period),
        _ => id == Guid.Empty ? Guid.NewGuid() : id,
    };

    // ------------------------------------------------------------------ Kaskaden

    private void DeleteCategory(Category c)
    {
        var fallback = DefaultCategories.FallbackFor(Engine().Index, c.Type);
        var affected = new List<Guid> { c.Id };
        foreach (var child in _data.Categories.Where(x => x.ParentId == c.Id && !x.IsDeleted).ToList())
        {
            affected.Add(child.Id);
            MarkDeleted(child, log: false);
        }
        MarkDeleted(c);
        if (fallback is null) return;
        foreach (var e in _data.Entries.Where(x => !x.IsDeleted && affected.Contains(x.CategoryId)).ToList())
            Modify(e, x => x.CategoryId = fallback.Id);
    }

    private void EndAllocationEntries(Guid bufferId)
    {
        var current = Engine().CurrentPeriod;
        foreach (var e in _data.Entries.Where(x => !x.IsDeleted && x.BufferId == bufferId).ToList())
        {
            if (e.StartPeriod >= current) MarkDeleted(e, log: false);
            else Modify(e, x => x.EndPeriod = x.EndPeriod is { } old && old < current.Previous ? old : current.Previous);
        }
    }

    // ------------------------------------------------------------------ Nachverarbeitung

    /// <summary>Fällige Raten/Sparpläne verbuchen, Zins-/Tilgungsanteile neu berechnen, Tilgung erkennen.</summary>
    public void PostProcess(Guid? userId = null)
    {
        var engine = Engine();
        var booked = BookingMaterializer.Materialize(engine, Now, userId ?? UserId);
        foreach (var p in booked.Payments) { AddEntity(p); _touchedDebts.Add(p.DebtId); }
        foreach (var t in booked.Transactions) AddEntity(t);

        foreach (var debtId in _touchedDebts)
        {
            if (!_lookup.TryGetValue((SyncTypes.Debt, debtId), out var de) || de is not DebtEntry debt || debt.IsDeleted) continue;
            var payments = _data.DebtPayments.Where(p => p.DebtId == debtId && !p.IsDeleted).ToList();
            foreach (var changed in BookingMaterializer.RecomputeSplits(debt, payments))
                if (!_added.Contains(changed)) _modified.Add(changed);
        }

        engine = Engine();
        foreach (var debt in engine.Index.Debts)
        {
            var status = engine.Debts.Status(debt.Id);
            if (!status.IsPaidOff || status.PaidOffDate is not { } paidDate || status.Payments.Count == 0) continue;
            var payoffPeriod = engine.Calendar.PeriodOf(paidDate);
            var linked = (engine.Index.EntriesByDebt.GetValueOrDefault(debt.Id) ?? [])
                .Where(e => e.DebtRole != DebtRole.None && (e.EndPeriod is null || e.EndPeriod > payoffPeriod) && e.StartPeriod <= payoffPeriod)
                .ToList();
            if (linked.Count == 0) continue;
            foreach (var e in linked) Modify(e, x => x.EndPeriod = payoffPeriod);
            if (!debt.IsDemo) PaidOffDebts.Add(debt);
        }
        _data.Invalidate();
    }

    // ------------------------------------------------------------------ Hilfen

    public void AddEntity(HouseholdEntity entity)
    {
        entity.HouseholdId = HouseholdId;
        if (entity.CreatedAt == default) entity.CreatedAt = Now;
        entity.UpdatedAt = Now;
        ListOf(entity).Add(entity);
        _lookup[(SyncTypes.NameOf(entity), entity.Id)] = entity;
        _added.Add(entity);
    }

    /// <summary>Server-seitige Änderung eines bestehenden Datensatzes (mit Verlaufseintrag).</summary>
    public void Modify<T>(T entity, Action<T> change, bool log = true) where T : HouseholdEntity
    {
        var before = log ? Json(entity) : null;
        change(entity);
        entity.UpdatedAt = Now;
        if (!_added.Contains(entity)) _modified.Add(entity);
        if (log) Log("update", entity, before, entity, false);
    }

    public void MarkDeleted(HouseholdEntity entity, bool log = true)
    {
        var before = log ? Json(entity) : null;
        entity.IsDeleted = true;
        entity.UpdatedAt = Now;
        entity.UpdatedBy = UserId;
        if (!_added.Contains(entity)) _modified.Add(entity);
        if (log) Log("delete", entity, before, null, false);
    }

    private void Replace(HouseholdEntity existing, HouseholdEntity replacement)
    {
        var list = ListOf(existing);
        var index = -1;
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], existing)) { index = i; break; }
        if (index >= 0) list[index] = replacement; else list.Add(replacement);
        _lookup[(SyncTypes.NameOf(replacement), replacement.Id)] = replacement;
        replacement.UpdatedAt = Now;
        if (_added.Remove(existing)) _added.Add(replacement);
        else
        {
            _modified.Remove(existing);
            _modified.Add(replacement);
        }
    }

    private IList ListOf(HouseholdEntity e) => e switch
    {
        Category => _data.Categories,
        BudgetEntry => _data.Entries,
        BudgetEntryChange => _data.EntryChanges,
        BudgetEntryMonth => _data.EntryMonths,
        DebtEntry => _data.Debts,
        DebtPayment => _data.DebtPayments,
        BufferEntry => _data.Buffers,
        BufferTransaction => _data.BufferTransactions,
        MonthSnapshot => _data.Snapshots,
        BalanceCheckpoint => _data.Checkpoints,
        _ => throw new ArgumentException(e.GetType().Name),
    };

    private void Log(string action, HouseholdEntity entity, string? before, HouseholdEntity? after, bool conflict) =>
        _logs.Add(NewLog(action, SyncTypes.NameOf(entity), entity.Id, before, after is null ? null : Json(after), conflict, EntityMerge.LabelOf(entity)));

    private ChangeLogEntry NewLog(string action, string type, Guid id, string? before, string? after, bool conflict, string label) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = HouseholdId,
        EntityType = type,
        EntityId = id,
        Action = action,
        UserId = UserId,
        CreatedAt = Now,
        BeforeJson = before,
        AfterJson = after,
        IsConflict = conflict,
        Label = label.Length > 200 ? label[..200] : label,
    };

    private static string Json(object entity) => JsonSerializer.Serialize(entity, entity.GetType(), JsonDefaults.Options);

    public Household OriginalHousehold => _originalHousehold;
}
