using System.Text.Json;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Client.Services;

/// <summary>Eine lokale Änderung (Upsert, Löschung oder Haushaltseinstellung).</summary>
public sealed record Change(HouseholdEntity? Upsert = null, string? DeleteType = null, Guid DeleteId = default, Household? Settings = null)
{
    public static Change Save(HouseholdEntity e) => new(Upsert: e);
    public static Change Remove(HouseholdEntity e) => new(DeleteType: SyncTypes.NameOf(e), DeleteId: e.Id);
    public static Change Household(Household h) => new(Settings: h);
}

/// <summary>
/// Lokaler Datenbestand (offline-first, F-132): alle Haushaltsdaten im Speicher und in IndexedDB, Änderungen werden
/// sofort angewendet und über eine Outbox synchronisiert (F-131). Für jede Änderung wird die zuletzt vom Server
/// bekannte Version als Basis mitgeschickt (3-Wege-Merge).
/// </summary>
/// <summary>Gespeicherter Outbox-Eintrag mit Sendereihenfolge.</summary>
public sealed record OutboxItem(long Order, SyncOperation Op);

public sealed class DataStore(LocalDb db, AppSession session)
{
    private readonly Dictionary<(string Type, Guid Id), HouseholdEntity> _all = [];
    private readonly Dictionary<(string Type, Guid Id), SyncOperation> _outbox = [];
    // Sendereihenfolge der Outbox: Abhängigkeiten (z. B. Polster vor Sparplan-Position) müssen in Erzeugungsreihenfolge
    // beim Server ankommen. Ersetzte (zusammengefasste) Änderungen behalten ihren ursprünglichen Platz.
    private readonly Dictionary<(string Type, Guid Id), long> _outboxOrder = [];
    private long _nextOrder;
    private readonly Dictionary<(string Type, Guid Id), SyncOperation> _inflight = [];
    private Household _household = new();
    private List<Member> _members = [];
    private HouseholdEngine? _engine;
    private DateOnly _engineDay;

    public HouseholdData Data { get; private set; } = new();
    public long Seq { get; private set; }
    public int Revision { get; private set; }
    public bool IsLoaded { get; private set; }
    public int PendingCount => _outbox.Count + _inflight.Count;

    public event Action? Changed;

    /// <summary>Lokale Änderung angelegt → Synchronisation anstoßen.</summary>
    public event Action? Mutated;

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Rechenkern auf dem aktuellen Stand (wird bei Datenänderung oder Datumswechsel neu erzeugt).</summary>
    public HouseholdEngine Engine
    {
        get
        {
            var today = Today;
            if (_engine is null || _engineDay != today)
            {
                _engine = new HouseholdEngine(Data, today);
                _engineDay = today;
            }
            return _engine;
        }
    }

    public Member? Me => Data.Members.FirstOrDefault(m => m.UserId == session.User?.Id);

    // ------------------------------------------------------------------ Laden

    public async Task LoadAsync()
    {
        _all.Clear();
        _outbox.Clear();
        _outboxOrder.Clear();
        _nextOrder = 0;
        _inflight.Clear();
        var userId = session.User?.Id.ToString();
        var owner = await db.GetAsync(LocalDb.Meta, "owner");
        if (owner is not null && owner != userId)
        {
            await db.ClearAllAsync();
            owner = null;
        }
        if (owner is null)
        {
            Seq = 0;
            _household = new Household();
            _members = [];
            Rebuild();
            return;
        }

        Seq = long.TryParse(await db.GetAsync(LocalDb.Meta, "seq"), out var s) ? s : 0;
        _household = Deserialize<Household>(await db.GetAsync(LocalDb.Meta, "household")) ?? new Household();
        _members = Deserialize<List<Member>>(await db.GetAsync(LocalDb.Meta, "members")) ?? [];
        foreach (var item in await db.GetAllAsync(LocalDb.Entities))
        {
            var sep = item.K.IndexOf(':');
            if (sep <= 0 || !SyncTypes.ClrTypes.TryGetValue(item.K[..sep], out var clr)) continue;
            if (JsonSerializer.Deserialize(item.V, clr, JsonDefaults.Options) is HouseholdEntity e) _all[(item.K[..sep], e.Id)] = e;
        }
        foreach (var item in await db.GetAllAsync(LocalDb.Outbox))
        {
            var stored = Deserialize<OutboxItem>(item.V);
            var op = stored?.Op ?? Deserialize<SyncOperation>(item.V);
            if (op is null || op.Type is null) continue;
            var key = (op.Type, op.Id);
            _outbox[key] = op;
            _outboxOrder[key] = stored?.Op is not null ? stored.Order : ++_nextOrder;
            _nextOrder = Math.Max(_nextOrder, _outboxOrder[key]);
        }
        Rebuild();
    }

    /// <summary>Lokale Daten verwerfen (Abmelden, Haushaltswechsel).</summary>
    public async Task ResetAsync()
    {
        _all.Clear();
        _outbox.Clear();
        _outboxOrder.Clear();
        _nextOrder = 0;
        _inflight.Clear();
        _household = new Household();
        _members = [];
        Seq = 0;
        await db.ClearAllAsync();
        Rebuild();
    }

    // ------------------------------------------------------------------ Server → lokal

    public async Task ApplyPullAsync(SyncPullResponse r)
    {
        var puts = new List<DbItem>();
        var deletes = new List<string>();
        if (r.Full)
        {
            foreach (var key in _all.Keys.ToList())
            {
                if (IsPending(key)) continue;
                _all.Remove(key);
                deletes.Add(KeyOf(key));
            }
        }
        void Apply(IEnumerable<HouseholdEntity> list)
        {
            foreach (var e in list)
            {
                var key = (SyncTypes.NameOf(e), e.Id);
                if (IsPending(key)) continue;
                if (e.IsDeleted && !KeepTombstone(e))
                {
                    if (_all.Remove(key)) deletes.Add(KeyOf(key));
                    continue;
                }
                _all[key] = e;
                puts.Add(new DbItem(KeyOf(key), Serialize(e)));
            }
        }
        Apply(r.Categories);
        Apply(r.Entries);
        Apply(r.EntryChanges);
        Apply(r.EntryMonths);
        Apply(r.Debts);
        Apply(r.DebtPayments);
        Apply(r.Buffers);
        Apply(r.BufferTransactions);
        Apply(r.Snapshots);
        Apply(r.Checkpoints);
        if (r.Household is not null && !IsPending((SyncTypes.Household, r.Household.Id))) _household = r.Household;
        if (r.Members.Count > 0) _members = r.Members;
        Seq = r.Seq;

        await db.DeleteManyAsync(LocalDb.Entities, deletes);
        await db.PutManyAsync(LocalDb.Entities, puts);
        await db.PutAsync(LocalDb.Meta, "seq", Seq.ToString());
        await db.PutAsync(LocalDb.Meta, "household", Serialize(_household));
        await db.PutAsync(LocalDb.Meta, "members", JsonSerializer.Serialize(_members, JsonDefaults.Options));
        await db.PutAsync(LocalDb.Meta, "owner", session.User?.Id.ToString() ?? "");
        if (r.Full || puts.Count > 0 || deletes.Count > 0 || r.Household is not null) Rebuild();
    }

    // ------------------------------------------------------------------ Lokale Änderungen

    public Task SaveAsync(params HouseholdEntity[] entities) => MutateAsync(entities.Select(Change.Save));

    public Task DeleteAsync(params HouseholdEntity[] entities) => MutateAsync(entities.Select(Change.Remove));

    public async Task MutateAsync(IEnumerable<Change> changes)
    {
        var puts = new List<DbItem>();
        var deletes = new List<string>();
        var outboxPuts = new List<DbItem>();
        var outboxDeletes = new List<string>();
        var now = DateTimeOffset.UtcNow;

        foreach (var c in changes)
        {
            if (c.Settings is { } settings)
            {
                var key = (SyncTypes.Household, settings.Id);
                var baseline = BaseFor(key, _household);
                settings.UpdatedAt = now;
                _household = settings;
                Enqueue(key, new SyncOperation(Guid.NewGuid(), SyncTypes.Household, settings.Id, "upsert", Element(settings), baseline));
                continue;
            }
            if (c.Upsert is { } e)
            {
                var type = SyncTypes.NameOf(e);
                if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
                var key = (type, e.Id);
                _all.TryGetValue(key, out var existing);
                var baseline = BaseFor(key, existing);
                e.HouseholdId = _household.Id;
                if (e.CreatedAt == default) e.CreatedAt = existing?.CreatedAt ?? now;
                e.UpdatedAt = now;
                e.UpdatedBy = session.User?.Id;
                e.IsDeleted = false;
                if (existing is not null)
                {
                    e.Version = existing.Version;
                    e.IsDemo = existing.IsDemo;
                }
                _all[key] = e;
                puts.Add(new DbItem(KeyOf(key), Serialize(e)));
                Enqueue(key, new SyncOperation(Guid.NewGuid(), type, e.Id, "upsert", Element(e), baseline));
                continue;
            }
            if (c.DeleteType is { } dt)
            {
                var key = (dt, c.DeleteId);
                if (!_all.TryGetValue(key, out var existing)) continue;
                // Nie beim Server angekommen → einfach verwerfen.
                if (_outbox.TryGetValue(key, out var pending) && pending.Base is null && !_inflight.ContainsKey(key))
                {
                    RemoveFromOutbox(key);
                    _all.Remove(key);
                    deletes.Add(KeyOf(key));
                    outboxDeletes.Add(KeyOf(key));
                    continue;
                }
                var baseline = BaseFor(key, existing);
                if (KeepTombstone(existing))
                {
                    var tomb = existing.CloneAs<HouseholdEntity>();
                    tomb.IsDeleted = true;
                    _all[key] = tomb;
                    puts.Add(new DbItem(KeyOf(key), Serialize(tomb)));
                }
                else
                {
                    _all.Remove(key);
                    deletes.Add(KeyOf(key));
                }
                Enqueue(key, new SyncOperation(Guid.NewGuid(), dt, c.DeleteId, "delete", null, baseline));
            }
        }

        foreach (var (key, op) in _outbox) outboxPuts.Add(new DbItem(KeyOf(key), JsonSerializer.Serialize(new OutboxItem(_outboxOrder.GetValueOrDefault(key), op), JsonDefaults.Options)));
        await db.DeleteManyAsync(LocalDb.Entities, deletes);
        await db.PutManyAsync(LocalDb.Entities, puts);
        await db.DeleteManyAsync(LocalDb.Outbox, outboxDeletes);
        await db.PutManyAsync(LocalDb.Outbox, outboxPuts);
        await db.PutAsync(LocalDb.Meta, "household", Serialize(_household));
        await db.PutAsync(LocalDb.Meta, "owner", session.User?.Id.ToString() ?? "");
        Rebuild();
        Mutated?.Invoke();
    }

    /// <summary>Basis = zuletzt vom Server bekannter Stand (bei ausstehender Änderung deren Basis).</summary>
    private JsonElement? BaseFor((string, Guid) key, object? current)
    {
        if (_outbox.TryGetValue(key, out var pending)) return pending.Base;
        if (_inflight.TryGetValue(key, out var flying)) return flying.Data;
        return current is null ? null : Element(current);
    }

    private void Enqueue((string, Guid) key, SyncOperation op)
    {
        if (!_outboxOrder.ContainsKey(key)) _outboxOrder[key] = ++_nextOrder;
        _outbox[key] = op;
    }

    private void RemoveFromOutbox((string, Guid) key)
    {
        _outbox.Remove(key);
        _outboxOrder.Remove(key);
    }

    // ------------------------------------------------------------------ Outbox → Server

    public List<SyncOperation> TakeBatch(int max = 200)
    {
        var batch = new List<SyncOperation>();
        foreach (var key in _outbox.Keys.OrderBy(k => _outboxOrder.GetValueOrDefault(k)).Take(max).ToList())
        {
            var op = _outbox[key];
            RemoveFromOutbox(key);
            _inflight[key] = op;
            batch.Add(op);
        }
        return batch;
    }

    /// <summary>Antwort verarbeiten; abgelehnte Änderungen werden lokal zurückgenommen.</summary>
    public async Task<List<SyncOpResult>> CompletePushAsync(IReadOnlyList<SyncOperation> batch, SyncPushResponse response)
    {
        var rejected = new List<SyncOpResult>();
        var puts = new List<DbItem>();
        var deletes = new List<string>();
        var outboxDeletes = new List<string>();
        foreach (var op in batch)
        {
            var key = (op.Type, op.Id);
            _inflight.Remove(key);
            var result = response.Results.FirstOrDefault(r => r.OpId == op.OpId);
            if (!_outbox.ContainsKey(key)) outboxDeletes.Add(KeyOf(key));
            if (result?.Status != SyncOpStatus.Rejected || _outbox.ContainsKey(key)) continue;
            rejected.Add(result);
            if (op.Type == SyncTypes.Household)
            {
                if (op.Base is { } hb && hb.Deserialize<Household>(JsonDefaults.Options) is { } restored) _household = restored;
                continue;
            }
            if (op.Base is { } b && SyncTypes.ClrTypes.TryGetValue(op.Type, out var clr) && b.Deserialize(clr, JsonDefaults.Options) is HouseholdEntity original)
            {
                _all[key] = original;
                puts.Add(new DbItem(KeyOf(key), Serialize(original)));
            }
            else if (_all.Remove(key)) deletes.Add(KeyOf(key));
        }
        await db.DeleteManyAsync(LocalDb.Outbox, outboxDeletes);
        await db.DeleteManyAsync(LocalDb.Entities, deletes);
        await db.PutManyAsync(LocalDb.Entities, puts);
        if (rejected.Count > 0) Rebuild();
        return rejected;
    }

    /// <summary>Übertragung fehlgeschlagen (offline) → Änderungen zurück in die Outbox.</summary>
    public void FailPush(IReadOnlyList<SyncOperation> batch)
    {
        var first = (_outboxOrder.Count == 0 ? _nextOrder + 1 : _outboxOrder.Values.Min()) - batch.Count;
        for (var i = 0; i < batch.Count; i++)
        {
            var op = batch[i];
            var key = (op.Type, op.Id);
            _inflight.Remove(key);
            _outbox[key] = _outbox.TryGetValue(key, out var newer) ? newer with { Base = op.Base } : op;
            _outboxOrder[key] = first + i;
        }
    }

    public bool HasOutbox => _outbox.Count > 0;

    // ------------------------------------------------------------------ Hilfen

    public T? Find<T>(Guid id) where T : HouseholdEntity =>
        _all.TryGetValue((SyncTypes.ClrTypes.First(kv => kv.Value == typeof(T)).Key, id), out var e) && !e.IsDeleted ? (T)e : null;

    /// <summary>Datenänderung von außen signalisieren (z. B. Sprachwechsel).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    private bool IsPending((string, Guid) key) => _outbox.ContainsKey(key) || _inflight.ContainsKey(key);

    private static bool KeepTombstone(HouseholdEntity e) =>
        e is DebtPayment { SourceEntryId: not null } or BufferTransaction { SourceEntryId: not null };

    private void Rebuild()
    {
        var values = _all.Values;
        Data = new HouseholdData
        {
            Household = _household,
            Members = _members,
            Categories = [.. values.OfType<Category>()],
            Entries = [.. values.OfType<BudgetEntry>()],
            EntryChanges = [.. values.OfType<BudgetEntryChange>()],
            EntryMonths = [.. values.OfType<BudgetEntryMonth>()],
            Debts = [.. values.OfType<DebtEntry>()],
            DebtPayments = [.. values.OfType<DebtPayment>()],
            Buffers = [.. values.OfType<BufferEntry>()],
            BufferTransactions = [.. values.OfType<BufferTransaction>()],
            Snapshots = [.. values.OfType<MonthSnapshot>()],
            Checkpoints = [.. values.OfType<BalanceCheckpoint>()],
        };
        _engine = null;
        IsLoaded = _household.Id != Guid.Empty;
        Revision++;
        Changed?.Invoke();
    }

    private static string KeyOf((string Type, Guid Id) key) => $"{key.Type}:{key.Id}";
    private static string Serialize(object e) => JsonSerializer.Serialize(e, e.GetType(), JsonDefaults.Options);
    private static JsonElement Element(object e) => JsonSerializer.SerializeToElement(e, e.GetType(), JsonDefaults.Options);

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options); }
        catch (JsonException) { return null; }
    }
}
