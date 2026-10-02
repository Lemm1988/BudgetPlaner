using System.Text.Json.Serialization;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>
/// Vollständige Daten eines Haushalts im Speicher. Client (offline) und Server rechnen darauf mit derselben Logik.
/// </summary>
public sealed class HouseholdData
{
    private HouseholdIndex? _index;

    public Household Household { get; set; } = new();
    public List<Member> Members { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<BudgetEntry> Entries { get; set; } = [];
    public List<BudgetEntryChange> EntryChanges { get; set; } = [];
    public List<BudgetEntryMonth> EntryMonths { get; set; } = [];
    public List<DebtEntry> Debts { get; set; } = [];
    public List<DebtPayment> DebtPayments { get; set; } = [];
    public List<BufferEntry> Buffers { get; set; } = [];
    public List<BufferTransaction> BufferTransactions { get; set; } = [];
    public List<MonthSnapshot> Snapshots { get; set; } = [];
    public List<BalanceCheckpoint> Checkpoints { get; set; } = [];

    /// <summary>Nachschlage-Indizes (nur aktive Datensätze); nach Änderungen <see cref="Invalidate"/> aufrufen.</summary>
    [JsonIgnore]
    public HouseholdIndex Index => _index ??= new HouseholdIndex(this);

    public void Invalidate() => _index = null;

    /// <summary>Flache Kopie mit eigenen Listen (Elemente geteilt) – Basis für Szenarien (F-075).</summary>
    public HouseholdData ShallowCopy() => new()
    {
        Household = Household,
        Members = [.. Members],
        Categories = [.. Categories],
        Entries = [.. Entries],
        EntryChanges = [.. EntryChanges],
        EntryMonths = [.. EntryMonths],
        Debts = [.. Debts],
        DebtPayments = [.. DebtPayments],
        Buffers = [.. Buffers],
        BufferTransactions = [.. BufferTransactions],
        Snapshots = [.. Snapshots],
        Checkpoints = [.. Checkpoints],
    };

    /// <summary>Alle Haushaltsentitäten (für Sync/Export).</summary>
    public IEnumerable<HouseholdEntity> AllEntities() =>
        Categories.Cast<HouseholdEntity>()
            .Concat(Entries).Concat(EntryChanges).Concat(EntryMonths)
            .Concat(Debts).Concat(DebtPayments)
            .Concat(Buffers).Concat(BufferTransactions)
            .Concat(Snapshots).Concat(Checkpoints);
}

/// <summary>Vorberechnete Nachschlagetabellen für schnelle Berechnungen (NFR: 10.000 Positionen).</summary>
public sealed class HouseholdIndex
{
    public HouseholdIndex(HouseholdData d)
    {
        Calendar = new PeriodCalendar(d.Household.MonthStartRules);

        Categories = d.Categories.Where(c => !c.IsDeleted).OrderBy(c => c.SortIndex).ToList();
        CategoryById = Categories.ToDictionary(c => c.Id);
        RootByCategory = new Dictionary<Guid, Category>(Categories.Count);
        foreach (var c in Categories) RootByCategory[c.Id] = ResolveRoot(c);

        Entries = d.Entries.Where(e => !e.IsDeleted).OrderBy(e => e.SortIndex).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        EntryById = Entries.ToDictionary(e => e.Id);
        AllEntryById = new Dictionary<Guid, BudgetEntry>(d.Entries.Count);
        foreach (var e in d.Entries) AllEntryById[e.Id] = e;

        ChangesByEntry = d.EntryChanges.Where(c => !c.IsDeleted)
            .GroupBy(c => c.EntryId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.FromPeriod.Index).ToArray());

        MonthByEntryPeriod = new Dictionary<(Guid, int), BudgetEntryMonth>(d.EntryMonths.Count);
        foreach (var m in d.EntryMonths)
            if (!m.IsDeleted) MonthByEntryPeriod[(m.EntryId, m.Period.Index)] = m;

        Debts = d.Debts.Where(x => !x.IsDeleted).OrderBy(x => x.SortIndex).ThenBy(x => x.Name).ToList();
        DebtById = Debts.ToDictionary(x => x.Id);
        var debtById = DebtById;
        PaymentsByDebt = d.DebtPayments.Where(p => !p.IsDeleted && debtById.ContainsKey(p.DebtId))
            .GroupBy(p => p.DebtId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Date).ThenBy(p => p.IsExtra).ThenBy(p => p.CreatedAt).ToArray());
        PaymentSources = [];
        foreach (var p in d.DebtPayments)
            if (p.SourceEntryId is { } se && p.SourcePeriod is { } sp) PaymentSources.Add((se, sp.Index));

        Buffers = d.Buffers.Where(x => !x.IsDeleted).OrderBy(x => x.SortIndex).ThenBy(x => x.Name).ToList();
        BufferById = Buffers.ToDictionary(x => x.Id);
        var bufferById = BufferById;
        Transactions = d.BufferTransactions.Where(t => !t.IsDeleted && bufferById.ContainsKey(t.BufferId))
            .OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
        TransactionsByBuffer = Transactions.GroupBy(t => t.BufferId).ToDictionary(g => g.Key, g => g.ToArray());
        TransactionSources = [];
        foreach (var t in d.BufferTransactions)
            if (t.SourceEntryId is { } se && t.SourcePeriod is { } sp) TransactionSources.Add((se, sp.Index));

        SnapshotByPeriod = new Dictionary<int, MonthSnapshot>();
        foreach (var s in d.Snapshots)
            if (!s.IsDeleted) SnapshotByPeriod[s.Period.Index] = s;

        Checkpoints = d.Checkpoints.Where(c => !c.IsDeleted).OrderBy(c => c.Date).ThenBy(c => c.CreatedAt).ToList();

        EntryInfos = new List<EntryInfo>(Entries.Count);
        EntryInfoById = new Dictionary<Guid, EntryInfo>(Entries.Count);
        var monthsByEntry = new Dictionary<Guid, Dictionary<int, BudgetEntryMonth>>();
        foreach (var m in MonthByEntryPeriod.Values)
        {
            if (!monthsByEntry.TryGetValue(m.EntryId, out var dict)) monthsByEntry[m.EntryId] = dict = [];
            dict[m.Period.Index] = m;
        }
        foreach (var e in Entries)
        {
            var root = RootOf(e.CategoryId);
            var info = new EntryInfo(e, root?.Id,
                e.Kind == EntryKind.Allocation ? BudgetGroup.Savings : root?.Group ?? BudgetGroup.None,
                ChangesByEntry.GetValueOrDefault(e.Id), monthsByEntry.GetValueOrDefault(e.Id));
            EntryInfos.Add(info);
            EntryInfoById[e.Id] = info;
        }

        EntriesByDebt = Entries.Where(e => e.DebtId is not null).GroupBy(e => e.DebtId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        EntriesByBuffer = Entries.Where(e => e.BufferId is not null && e.Kind == EntryKind.Allocation)
            .GroupBy(e => e.BufferId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public PeriodCalendar Calendar { get; }
    public List<Category> Categories { get; }
    public Dictionary<Guid, Category> CategoryById { get; }
    public Dictionary<Guid, Category> RootByCategory { get; }
    public List<BudgetEntry> Entries { get; }

    /// <summary>Aktive Positionen mit vorberechneten Zuordnungen (gleiche Reihenfolge wie <see cref="Entries"/>).</summary>
    public List<EntryInfo> EntryInfos { get; }
    public Dictionary<Guid, EntryInfo> EntryInfoById { get; }
    public Dictionary<Guid, BudgetEntry> EntryById { get; }
    public Dictionary<Guid, BudgetEntry> AllEntryById { get; }
    public Dictionary<Guid, BudgetEntryChange[]> ChangesByEntry { get; }
    public Dictionary<(Guid, int), BudgetEntryMonth> MonthByEntryPeriod { get; }
    public List<DebtEntry> Debts { get; }
    public Dictionary<Guid, DebtEntry> DebtById { get; }
    public Dictionary<Guid, DebtPayment[]> PaymentsByDebt { get; }

    /// <summary>Bereits gebuchte (auch gelöschte = unterdrückte) automatische Zahlungen (Position, Periode).</summary>
    public HashSet<(Guid, int)> PaymentSources { get; }

    public List<BufferEntry> Buffers { get; }
    public Dictionary<Guid, BufferEntry> BufferById { get; }
    public List<BufferTransaction> Transactions { get; }
    public Dictionary<Guid, BufferTransaction[]> TransactionsByBuffer { get; }
    public HashSet<(Guid, int)> TransactionSources { get; }
    public Dictionary<int, MonthSnapshot> SnapshotByPeriod { get; }
    public List<BalanceCheckpoint> Checkpoints { get; }
    public Dictionary<Guid, BudgetEntry[]> EntriesByDebt { get; }
    public Dictionary<Guid, BudgetEntry[]> EntriesByBuffer { get; }

    public Category? RootOf(Guid categoryId) => RootByCategory.GetValueOrDefault(categoryId);

    public BudgetGroup GroupOf(Guid categoryId) => RootOf(categoryId)?.Group ?? BudgetGroup.None;

    public bool IsClosed(Period p) => SnapshotByPeriod.ContainsKey(p.Index);

    private Category ResolveRoot(Category c)
    {
        var current = c;
        for (var depth = 0; depth < 16 && current.ParentId is { } pid && CategoryById.TryGetValue(pid, out var parent); depth++)
            current = parent;
        return current;
    }
}

/// <summary>Position mit vorberechneter Oberkategorie, Gruppe, Änderungen und Monatswerten (Performance).</summary>
public sealed class EntryInfo(BudgetEntry entry, Guid? rootId, BudgetGroup group, BudgetEntryChange[]? changes,
    Dictionary<int, BudgetEntryMonth>? months)
{
    public BudgetEntry Entry { get; } = entry;
    public Guid? RootId { get; } = rootId;
    public BudgetGroup Group { get; } = group;
    public BudgetEntryChange[]? Changes { get; } = changes;
    public Dictionary<int, BudgetEntryMonth>? Months { get; } = months;
}
