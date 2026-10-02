using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Client.Services;

/// <summary>Geltungsbereich einer Betragsänderung (B-03 Delta-Editing).</summary>
public enum EditScope
{
    /// <summary>Grundbetrag für alle Monate.</summary>
    All = 0,
    /// <summary>Ab dem gewählten Monat.</summary>
    FromMonth = 1,
    /// <summary>Nur im gewählten Monat.</summary>
    ThisMonth = 2,
}

public sealed record RatePlan(decimal Amount, int? DebitDay, Guid CategoryId, Period From);

/// <summary>Fachliche Aktionen der Oberfläche; alles wirkt sofort lokal und wird im Hintergrund synchronisiert.</summary>
public sealed class Actions(DataStore store, ToastService toasts, Loc l, ApiClient api, AppSession session)
{
    private HouseholdIndex Ix => store.Data.Index;
    private PeriodCalendar Cal => store.Data.Index.Calendar;
    private Period Current => store.Engine.CurrentPeriod;

    // =============================================================== Positionen

    public BudgetEntry NewEntry(EntryKind kind, Guid categoryId, Period start) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        CategoryId = categoryId,
        StartPeriod = start,
        Rhythm = Rhythm.Monthly,
        SortIndex = Ix.Entries.Count == 0 ? 10 : Ix.Entries.Max(e => e.SortIndex) + 10,
        IsFixed = kind == EntryKind.Income,
        CreatedBy = session.User?.Id,
    };

    /// <summary>Betrag einer Position im Monat (Abweichung „nur dieser Monat“ hat Vorrang).</summary>
    public decimal NominalAmount(BudgetEntry e, Period p)
    {
        var state = EntrySchedule.State(Ix, e, p);
        return state.IsDue ? state.Nominal : EntrySchedule.ScheduledAmount(Ix, e, p).Amount;
    }

    /// <summary>Hat die Position Monate vor <paramref name="p"/>? Dann fragt der Dialog nach „nur dieser / ab diesem Monat“.</summary>
    public bool HasHistoryBefore(BudgetEntry e, Period p) => store.Find<BudgetEntry>(e.Id) is not null && p > e.StartPeriod;

    public async Task SaveEntryAsync(BudgetEntry edited, decimal amount, Period period, EditScope scope)
    {
        var existing = store.Find<BudgetEntry>(edited.Id);
        var changes = new List<Change>();
        if (existing is null)
        {
            edited.Amount = amount;
            changes.Add(Change.Save(edited));
        }
        else
        {
            var nominal = NominalAmount(existing, period);
            if (amount == nominal) changes.Add(Change.Save(edited));
            else if (scope == EditScope.ThisMonth)
            {
                var month = MonthRecord(existing, period);
                month.PlannedAmount = amount;
                changes.Add(Change.Save(edited));
                changes.Add(Change.Save(month));
            }
            else if (scope == EditScope.All || period <= existing.StartPeriod)
            {
                edited.Amount = amount;
                changes.Add(Change.Save(edited));
                if (scope == EditScope.All)
                    foreach (var c in Ix.ChangesByEntry.GetValueOrDefault(existing.Id) ?? [])
                        changes.Add(Change.Remove(c));
            }
            else
            {
                changes.Add(Change.Save(edited));
                changes.AddRange(AmountFrom(existing, period, amount, paused: false));
            }
            // Eine „nur dieser Monat“-Abweichung im bearbeiteten Monat wird bei „ab“ aufgehoben.
            if (scope != EditScope.ThisMonth && Ix.MonthByEntryPeriod.TryGetValue((existing.Id, period.Index), out var m) && m.PlannedAmount is not null)
            {
                var copy = m.CloneAs<BudgetEntryMonth>();
                copy.PlannedAmount = null;
                changes.Add(Change.Save(copy));
            }
        }
        await store.MutateAsync(changes);
    }

    private List<Change> AmountFrom(BudgetEntry e, Period from, decimal amount, bool paused)
    {
        var changes = new List<Change>();
        foreach (var later in (Ix.ChangesByEntry.GetValueOrDefault(e.Id) ?? []).Where(c => c.FromPeriod > from))
            changes.Add(Change.Remove(later));
        changes.Add(Change.Save(new BudgetEntryChange
        {
            Id = DeterministicId.ForEntryChange(e.Id, from), EntryId = e.Id, FromPeriod = from, Amount = amount, IsPaused = paused,
        }));
        return changes;
    }

    private BudgetEntryMonth MonthRecord(BudgetEntry e, Period p) =>
        Ix.MonthByEntryPeriod.TryGetValue((e.Id, p.Index), out var m)
            ? m.CloneAs<BudgetEntryMonth>()
            : new BudgetEntryMonth { Id = DeterministicId.ForEntryMonth(e.Id, p), EntryId = e.Id, Period = p };

    /// <summary>Löschen entfernt die Position aus allen Berechnungen (F-010) – 30 Sekunden rückgängig machbar (B-04).</summary>
    public async Task DeleteEntryAsync(BudgetEntry e)
    {
        var related = new List<HouseholdEntity> { e };
        related.AddRange(Ix.ChangesByEntry.GetValueOrDefault(e.Id) ?? []);
        related.AddRange(store.Data.EntryMonths.Where(m => m.EntryId == e.Id && !m.IsDeleted));
        var copies = related.Select(x => x.CloneAs<HouseholdEntity>()).ToList();
        await store.DeleteAsync([.. related]);
        toasts.Undo(l.T("entry.deleted", e.Name), l["action.undo"], async () =>
        {
            foreach (var c in copies) c.IsDeleted = false;
            await store.SaveAsync([.. copies]);
            toasts.Success(l["entry.restored"]);
        });
    }

    /// <summary>„Beenden ab Monat“ – Historie bleibt erhalten (I-15).</summary>
    public async Task EndEntryAsync(BudgetEntry e, Period from)
    {
        if (from <= e.StartPeriod)
        {
            await DeleteEntryAsync(e);
            return;
        }
        var copy = e.CloneAs<BudgetEntry>();
        copy.EndPeriod = from.Previous;
        await store.SaveAsync(copy);
        toasts.Undo(l.T("entry.ended", e.Name, l.Period(from.Previous)), l["action.undo"], () => store.SaveAsync(e.CloneAs<BudgetEntry>()));
    }

    /// <summary>Pausieren (F-011): nur dieser Monat oder ab Monat bis auf Weiteres.</summary>
    public async Task PauseAsync(BudgetEntry e, Period p, bool onlyThisMonth)
    {
        if (onlyThisMonth)
        {
            var m = MonthRecord(e, p);
            m.IsSkipped = true;
            await store.SaveAsync(m);
            return;
        }
        var amount = EntrySchedule.ScheduledAmount(Ix, e, p).Amount;
        await store.MutateAsync(AmountFrom(e, p, amount, paused: true));
    }

    public async Task ResumeAsync(BudgetEntry e, Period p)
    {
        var changes = new List<Change>();
        if (Ix.MonthByEntryPeriod.TryGetValue((e.Id, p.Index), out var m) && m.IsSkipped)
        {
            var copy = m.CloneAs<BudgetEntryMonth>();
            copy.IsSkipped = false;
            changes.Add(Change.Save(copy));
        }
        var (amount, paused) = EntrySchedule.ScheduledAmount(Ix, e, p);
        if (paused)
        {
            var same = (Ix.ChangesByEntry.GetValueOrDefault(e.Id) ?? []).FirstOrDefault(c => c.FromPeriod == p && c.IsPaused);
            if (same is not null && p > e.StartPeriod) changes.Add(Change.Remove(same));
            changes.AddRange(AmountFrom(e, p, amount, paused: false));
        }
        await store.MutateAsync(changes);
    }

    /// <summary>Abhaken als bezahlt/erhalten (D-02).</summary>
    public async Task SetDoneAsync(BudgetEntry e, Period p, bool done)
    {
        var m = MonthRecord(e, p);
        m.IsDone = done;
        m.DoneAt = done ? DateTimeOffset.UtcNow : null;
        await store.SaveAsync(m);
    }

    /// <summary>Ist-Betrag erfassen (null = Ist entspricht Soll).</summary>
    public async Task SetActualAsync(BudgetEntry e, Period p, decimal? actual, bool markDone = true)
    {
        var m = MonthRecord(e, p);
        m.ActualAmount = actual;
        if (actual is not null && markDone && !m.IsDone)
        {
            m.IsDone = true;
            m.DoneAt = DateTimeOffset.UtcNow;
        }
        await store.SaveAsync(m);
    }

    /// <summary>Neue Reihenfolge speichern (F-015) – bleibt nach Reload und für alle Mitglieder erhalten.</summary>
    public async Task ReorderAsync(IReadOnlyList<BudgetEntry> ordered)
    {
        var changes = new List<Change>();
        var start = ordered.Count == 0 ? 0 : ordered.Min(e => e.SortIndex);
        for (var i = 0; i < ordered.Count; i++)
        {
            var target = start + i * 10;
            if (ordered[i].SortIndex == target) continue;
            var copy = ordered[i].CloneAs<BudgetEntry>();
            copy.SortIndex = target;
            changes.Add(Change.Save(copy));
        }
        if (changes.Count > 0) await store.MutateAsync(changes);
    }

    public async Task MoveAsync(BudgetEntry e, IReadOnlyList<BudgetEntry> list, int delta)
    {
        var items = list.ToList();
        var i = items.FindIndex(x => x.Id == e.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= items.Count) return;
        (items[i], items[j]) = (items[j], items[i]);
        await ReorderAsync(items);
    }

    // =============================================================== Kategorien

    public async Task SaveCategoryAsync(Category c) => await store.SaveAsync(c);

    /// <summary>Löschen hängt Positionen auf „Sonstiges“ um (F-012).</summary>
    public async Task DeleteCategoryAsync(Category c)
    {
        var fallback = DefaultCategories.FallbackFor(Ix, c.Type);
        var affected = new HashSet<Guid> { c.Id };
        var changes = new List<Change>();
        foreach (var child in Ix.Categories.Where(x => x.ParentId == c.Id))
        {
            affected.Add(child.Id);
            changes.Add(Change.Remove(child));
        }
        if (fallback is not null)
            foreach (var e in Ix.Entries.Where(x => affected.Contains(x.CategoryId)))
            {
                var copy = e.CloneAs<BudgetEntry>();
                copy.CategoryId = fallback.Id;
                changes.Add(Change.Save(copy));
            }
        changes.Add(Change.Remove(c));
        await store.MutateAsync(changes);
    }

    public int EntriesInCategory(Guid categoryId) =>
        Ix.Entries.Count(e => e.CategoryId == categoryId || Ix.CategoryById.GetValueOrDefault(e.CategoryId)?.ParentId == categoryId);

    public async Task ReorderCategoriesAsync(IReadOnlyList<Category> ordered)
    {
        var changes = new List<Change>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SortIndex == i * 10) continue;
            var copy = ordered[i].CloneAs<Category>();
            copy.SortIndex = i * 10;
            changes.Add(Change.Save(copy));
        }
        if (changes.Count > 0) await store.MutateAsync(changes);
    }

    // =============================================================== Monatsabschluss (I-01)

    public async Task CloseMonthAsync(Period p, string action, Guid? bufferId, decimal? closingBalance)
    {
        var engine = store.Engine;
        var month = engine.Month(p);
        var end = Cal.End(p);
        var first = new List<Change>();
        if (action == "buffer" && bufferId is { } bid && month.Unverplant != 0)
        {
            if (month.Unverplant > 0)
                first.Add(Change.Save(new BufferTransaction
                {
                    BufferId = bid, Date = end, Amount = month.Unverplant, Type = BufferTransactionType.Allocation, AffectsBudget = true,
                    Reason = $"{l["month.closeReason"]} {l.Period(p)}",
                }));
            else
            {
                var available = engine.Buffers.Status(bid).Balance;
                var amount = Math.Min(-month.Unverplant, available);
                if (amount > 0)
                    first.Add(Change.Save(new BufferTransaction
                    {
                        BufferId = bid, Date = end, Amount = amount, Type = BufferTransactionType.Withdrawal, AffectsBudget = true,
                        Reason = $"{l["month.compensateReason"]} {l.Period(p)}",
                    }));
            }
        }
        if (closingBalance is { } balance)
            first.Add(Change.Save(new BalanceCheckpoint { Date = end < DataStore.Today ? end : DataStore.Today, Amount = balance, Note = l.Period(p) }));
        if (first.Count > 0) await store.MutateAsync(first);

        var fresh = store.Engine;
        var unverplant = fresh.Month(p).Unverplant;
        var carry = action == "carry" ? unverplant : 0;
        var snapshot = BudgetCalculator.CreateSnapshot(fresh, p, carry, action == "buffer" ? bufferId : null, closingBalance, DateTimeOffset.UtcNow, session.User?.Id);
        await store.SaveAsync(snapshot);
        toasts.Success(l.T("month.closed", l.Period(p)));
    }

    public async Task ReopenMonthAsync(Period p)
    {
        var snap = store.Find<MonthSnapshot>(DeterministicId.ForSnapshot(store.Data.Household.Id, p));
        if (snap is not null) await store.DeleteAsync(snap);
        toasts.Info(l.T("month.reopened", l.Period(p)));
    }

    // =============================================================== Polster (F-040–F-042)

    public Category SavingCategory => Ix.Categories.FirstOrDefault(c => c.DefaultKey == "saving")
                                      ?? Ix.Categories.First(c => c.ParentId is null && c.Group == BudgetGroup.Savings);

    public async Task SaveBufferAsync(BufferEntry b, decimal? openingBalance, decimal? monthlyPlan, int? planDay, Period planFrom)
    {
        var isNew = store.Find<BufferEntry>(b.Id) is null;
        var changes = new List<Change> { Change.Save(b) };
        if (isNew && openingBalance is > 0)
            changes.Add(Change.Save(new BufferTransaction
            {
                BufferId = b.Id, Date = DataStore.Today, Amount = openingBalance.Value, Type = BufferTransactionType.Allocation, AffectsBudget = false,
                Reason = l["buffer.openingBalance"],
            }));
        await store.MutateAsync(changes);
        if (monthlyPlan is not null) await SetBufferPlanAsync(b, monthlyPlan.Value, planDay, planFrom);
    }

    /// <summary>Sparplan = monatliche Zuweisungsposition im Budget.</summary>
    public async Task SetBufferPlanAsync(BufferEntry b, decimal amount, int? day, Period from)
    {
        var plans = (Ix.EntriesByBuffer.GetValueOrDefault(b.Id) ?? []).Where(e => e.EndPeriod is null || e.EndPeriod >= from).ToList();
        if (plans.Count == 0)
        {
            if (amount <= 0) return;
            var entry = NewEntry(EntryKind.Allocation, SavingCategory.Id, from);
            entry.Name = b.Name;
            entry.Amount = amount;
            entry.BufferId = b.Id;
            entry.DebitDay = day;
            entry.IsFixed = true;
            await store.SaveAsync(entry);
            return;
        }
        var plan = plans[0];
        if (amount <= 0)
        {
            await EndEntryAsync(plan, from);
            return;
        }
        var copy = plan.CloneAs<BudgetEntry>();
        copy.DebitDay = day;
        copy.Name = b.Name;
        await SaveEntryAsync(copy, amount, from, EditScope.FromMonth);
    }

    public async Task AllocateAsync(BufferEntry b, decimal amount, DateOnly date, string? reason, bool affectsBudget = true) =>
        await store.SaveAsync(new BufferTransaction
        {
            BufferId = b.Id, Date = date, Amount = amount, Type = BufferTransactionType.Allocation, AffectsBudget = affectsBudget, Reason = reason,
        });

    /// <summary>Entnahme max. bis zum Stand, Grund ist Pflicht (F-042).</summary>
    public async Task<string?> WithdrawAsync(BufferEntry b, decimal amount, DateOnly date, string reason, bool toBudget)
    {
        if (string.IsNullOrWhiteSpace(reason)) return l["err.buffer.reasonRequired"];
        var balance = store.Engine.Buffers.Status(b.Id).Balance;
        if (amount > balance) return l.T("err.buffer.withdrawalExceeds", l.Money(balance));
        if (amount <= 0) return l["err.amount.required"];
        await store.SaveAsync(new BufferTransaction
        {
            BufferId = b.Id, Date = date, Amount = amount, Type = BufferTransactionType.Withdrawal, AffectsBudget = toBudget, Reason = reason.Trim(),
        });
        return null;
    }

    public async Task DeleteBufferTransactionAsync(BufferTransaction t)
    {
        var copy = t.CloneAs<BufferTransaction>();
        await store.DeleteAsync(t);
        toasts.Undo(l["buffer.txDeleted"], l["action.undo"], () => { copy.IsDeleted = false; return store.SaveAsync(copy); });
    }

    public async Task DeleteBufferAsync(BufferEntry b)
    {
        var changes = new List<Change> { Change.Remove(b) };
        changes.AddRange(store.Data.BufferTransactions.Where(t => t.BufferId == b.Id && !t.IsDeleted && t.SourceEntryId is null).Select(Change.Remove));
        foreach (var e in Ix.Entries.Where(x => x.BufferId == b.Id))
        {
            if (e.StartPeriod >= Current) changes.Add(Change.Remove(e));
            else
            {
                var copy = e.CloneAs<BudgetEntry>();
                copy.EndPeriod = Current.Previous;
                changes.Add(Change.Save(copy));
            }
        }
        await store.MutateAsync(changes);
        toasts.Success(l.T("buffer.deleted", b.Name));
    }

    // =============================================================== Schulden (F-020–F-022)

    public Guid DefaultRateCategory(DebtType type)
    {
        var key = type switch
        {
            DebtType.Mortgage => "housing",
            DebtType.Leasing => "mobility",
            _ => DefaultCategories.Needs,
        };
        return (Ix.Categories.FirstOrDefault(c => c.DefaultKey == key) ?? Ix.Categories.First(c => c.Type == CategoryType.Expense)).Id;
    }

    public Guid ExtraCategory => (Ix.Categories.FirstOrDefault(c => c.DefaultKey == "debt-payoff") ?? SavingCategory).Id;

    public BudgetEntry? LinkedEntry(DebtEntry d, DebtRole role) =>
        (Ix.EntriesByDebt.GetValueOrDefault(d.Id) ?? []).Where(e => e.DebtRole == role).OrderByDescending(e => e.StartPeriod).FirstOrDefault();

    /// <summary>Schuld speichern; Rate und Zusatztilgung werden als verknüpfte Budgetpositionen geführt (B-07).</summary>
    public async Task SaveDebtAsync(DebtEntry d, RatePlan? rate, RatePlan? extra)
    {
        if (rate is not null) d.MonthlyRate = rate.Amount;
        await store.SaveAsync(d);
        await LinkAsync(d, DebtRole.Rate, rate, isFixed: true);
        await LinkAsync(d, DebtRole.Extra, extra, isFixed: false);
    }

    private async Task LinkAsync(DebtEntry d, DebtRole role, RatePlan? plan, bool isFixed)
    {
        var existing = LinkedEntry(d, role);
        if (plan is null || plan.Amount <= 0)
        {
            if (existing is not null && (existing.EndPeriod is null || existing.EndPeriod >= Current)) await EndEntryAsync(existing, Current);
            return;
        }
        var name = role == DebtRole.Rate ? d.Name : $"{l["debt.extraPrefix"]} {d.Name}";
        if (existing is null)
        {
            var entry = NewEntry(EntryKind.Expense, plan.CategoryId, plan.From);
            entry.Name = name;
            entry.Amount = plan.Amount;
            entry.DebitDay = plan.DebitDay ?? d.DebitDay;
            entry.DebtId = d.Id;
            entry.DebtRole = role;
            entry.IsFixed = isFixed;
            await store.SaveAsync(entry);
            return;
        }
        var copy = existing.CloneAs<BudgetEntry>();
        copy.Name = name;
        copy.CategoryId = plan.CategoryId;
        copy.DebitDay = plan.DebitDay ?? d.DebitDay;
        copy.EndPeriod = null;
        await SaveEntryAsync(copy, plan.Amount, Period.Max(plan.From, existing.StartPeriod), EditScope.FromMonth);
    }

    /// <summary>Tilgung verbuchen (F-021); optional als Ausgabe im Budget des Monats (wird dann nicht doppelt gebucht).</summary>
    public async Task BookPaymentAsync(DebtEntry d, decimal amount, DateOnly date, bool isExtra, bool inBudget, string? note)
    {
        var payment = new DebtPayment
        {
            DebtId = d.Id, Date = date, Amount = amount, IsExtra = isExtra, InterestRateApplied = isExtra ? null : d.InterestRate, Note = note,
        };
        var changes = new List<Change>();
        if (inBudget)
        {
            var period = Cal.PeriodOf(date);
            var entry = NewEntry(EntryKind.Expense, isExtra ? ExtraCategory : DefaultRateCategory(d.Type), period);
            entry.Name = isExtra ? $"{l["debt.extraPrefix"]} {d.Name}" : d.Name;
            entry.Amount = amount;
            entry.Rhythm = Rhythm.Once;
            entry.DebitDay = date.Day;
            entry.DebtId = d.Id;
            entry.DebtRole = isExtra ? DebtRole.Extra : DebtRole.Rate;
            payment.SourceEntryId = entry.Id;
            payment.SourcePeriod = period;
            changes.Add(Change.Save(entry));
            changes.Add(Change.Save(new BudgetEntryMonth { Id = DeterministicId.ForEntryMonth(entry.Id, period), EntryId = entry.Id, Period = period, IsDone = true, DoneAt = DateTimeOffset.UtcNow }));
        }
        changes.Add(Change.Save(payment));
        await store.MutateAsync(changes);
        toasts.Success(l["debt.paymentBooked"]);
    }

    /// <summary>Zahlung löschen; eine dafür angelegte einmalige Budgetposition wird mitgelöscht.</summary>
    public async Task DeletePaymentAsync(DebtPayment p)
    {
        var copy = p.CloneAs<DebtPayment>();
        var source = p.SourceEntryId is { } sid ? store.Find<BudgetEntry>(sid) : null;
        if (source is not { Rhythm: Rhythm.Once }) source = null;
        var sourceCopy = source?.CloneAs<BudgetEntry>();
        var changes = new List<Change> { Change.Remove(p) };
        if (source is not null) changes.Add(Change.Remove(source));
        await store.MutateAsync(changes);
        toasts.Undo(l["debt.paymentDeleted"], l["action.undo"], () =>
        {
            copy.IsDeleted = false;
            var restore = new List<Change> { Change.Save(copy) };
            if (sourceCopy is not null) { sourceCopy.IsDeleted = false; restore.Add(Change.Save(sourceCopy)); }
            return store.MutateAsync(restore);
        });
    }

    public async Task DeleteDebtAsync(DebtEntry d, bool deleteLinkedEntries)
    {
        var changes = new List<Change> { Change.Remove(d) };
        changes.AddRange(store.Data.DebtPayments.Where(p => p.DebtId == d.Id && !p.IsDeleted).Select(Change.Remove));
        foreach (var e in Ix.Entries.Where(x => x.DebtId == d.Id))
        {
            if (deleteLinkedEntries) changes.Add(Change.Remove(e));
            else
            {
                var copy = e.CloneAs<BudgetEntry>();
                copy.DebtId = null;
                copy.DebtRole = DebtRole.None;
                changes.Add(Change.Save(copy));
            }
        }
        await store.MutateAsync(changes);
        toasts.Success(l.T("debt.deleted", d.Name));
    }

    // =============================================================== Kontostand (I-03)

    public Task SaveCheckpointAsync(BalanceCheckpoint c) => store.SaveAsync(c);

    public async Task DeleteCheckpointAsync(BalanceCheckpoint c)
    {
        var copy = c.CloneAs<BalanceCheckpoint>();
        await store.DeleteAsync(c);
        toasts.Undo(l["cashflow.checkpointDeleted"], l["action.undo"], () => { copy.IsDeleted = false; return store.SaveAsync(copy); });
    }

    // =============================================================== Haushalt

    public async Task CompleteOnboardingAsync()
    {
        var h = (Household)Clone(store.Data.Household);
        h.OnboardingCompletedAt = DateTimeOffset.UtcNow;
        await store.MutateAsync([Change.Household(h)]);
    }

    public async Task<HouseholdInfoDto> UpdateHouseholdAsync(HouseholdSettingsUpdate update) =>
        await api.PatchAsync<HouseholdInfoDto>("api/v1/household", update);

    private static object Clone(object o) =>
        System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(o, o.GetType(), global::BudgetPlaner.Shared.JsonDefaults.Options), o.GetType(), global::BudgetPlaner.Shared.JsonDefaults.Options)!;
}
