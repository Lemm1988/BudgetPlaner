using System.Text.Json;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Shared.Contracts;

/// <summary>
/// Delta-Abgleich (F-131/F-132): alle Datensätze mit Version &gt; <c>since</c>, inkl. Tombstones (IsDeleted).
/// Bei <see cref="Full"/> ersetzt der Client seinen lokalen Bestand vollständig.
/// </summary>
public sealed class SyncPullResponse
{
    public long Seq { get; set; }
    public bool Full { get; set; }
    public Household? Household { get; set; }
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

    public int Count => Categories.Count + Entries.Count + EntryChanges.Count + EntryMonths.Count + Debts.Count + DebtPayments.Count
                        + Buffers.Count + BufferTransactions.Count + Snapshots.Count + Checkpoints.Count + (Household is null ? 0 : 1);
}

/// <summary>Eine lokale Änderung aus der Outbox. <see cref="Base"/> ist der zuletzt vom Server bekannte Stand (3-Wege-Merge).</summary>
public sealed record SyncOperation(Guid OpId, string Type, Guid Id, string Action, JsonElement? Data, JsonElement? Base);

public sealed record SyncPushRequest(string? DeviceId, List<SyncOperation> Operations);

public enum SyncOpStatus
{
    Applied = 0,
    NoChange = 1,

    /// <summary>Gleichzeitige Änderungen verschiedener Felder wurden zusammengeführt.</summary>
    Merged = 2,

    /// <summary>Gleiches Feld gleichzeitig geändert – spätere Änderung gewinnt, alter Wert im Änderungsverlauf.</summary>
    Conflict = 3,

    Rejected = 4,
}

public sealed record SyncOpResult(Guid OpId, SyncOpStatus Status, string? ErrorKey = null, string? Error = null);

public sealed record SyncPushResponse(long Seq, List<SyncOpResult> Results);

/// <summary>Namen der synchronisierten Entitätstypen.</summary>
public static class SyncTypes
{
    public const string Household = "household";
    public const string Category = "category";
    public const string Entry = "entry";
    public const string EntryChange = "entryChange";
    public const string EntryMonth = "entryMonth";
    public const string Debt = "debt";
    public const string DebtPayment = "debtPayment";
    public const string Buffer = "buffer";
    public const string BufferTransaction = "bufferTransaction";
    public const string Snapshot = "snapshot";
    public const string Checkpoint = "checkpoint";

    public static readonly IReadOnlyDictionary<string, Type> ClrTypes = new Dictionary<string, Type>
    {
        [Household] = typeof(Household),
        [Category] = typeof(Category),
        [Entry] = typeof(BudgetEntry),
        [EntryChange] = typeof(BudgetEntryChange),
        [EntryMonth] = typeof(BudgetEntryMonth),
        [Debt] = typeof(DebtEntry),
        [DebtPayment] = typeof(DebtPayment),
        [Buffer] = typeof(BufferEntry),
        [BufferTransaction] = typeof(BufferTransaction),
        [Snapshot] = typeof(MonthSnapshot),
        [Checkpoint] = typeof(BalanceCheckpoint),
    };

    public static string NameOf(object entity) => entity switch
    {
        global::BudgetPlaner.Domain.Entities.Household _ => Household,
        global::BudgetPlaner.Domain.Entities.Category _ => Category,
        BudgetEntry _ => Entry,
        BudgetEntryChange _ => EntryChange,
        BudgetEntryMonth _ => EntryMonth,
        DebtEntry _ => Debt,
        global::BudgetPlaner.Domain.Entities.DebtPayment _ => DebtPayment,
        BufferEntry _ => Buffer,
        global::BudgetPlaner.Domain.Entities.BufferTransaction _ => BufferTransaction,
        MonthSnapshot _ => Snapshot,
        BalanceCheckpoint _ => Checkpoint,
        _ => throw new ArgumentException($"Unbekannter Typ {entity.GetType().Name}"),
    };
}

/// <summary>Import mit Spaltenzuordnung (F-133).</summary>
public sealed record ImportPreviewResponse(
    Guid ImportId,
    string FileName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    int RowCount,
    IReadOnlyDictionary<string, int> SuggestedMapping,
    string SuggestedTarget);

public sealed record ImportMappingRequest(
    Guid ImportId,
    string Target,
    Dictionary<string, int> Mapping,
    bool FirstRowIsHeader = true);

public sealed record ImportRowResult(int RowNumber, bool Valid, IReadOnlyList<string> Errors, IReadOnlyDictionary<string, string?> Values);

public sealed record ImportValidationResponse(int Total, int ValidCount, int InvalidCount, IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportCommitRequest(ImportMappingRequest Mapping, List<int> SkipRows);

public sealed record ImportCommitResponse(int Imported, int Skipped);

/// <summary>Zielfelder des Imports je Zieltyp.</summary>
public static class ImportFields
{
    public const string TargetEntries = "entries";
    public const string TargetDebts = "debts";
    public const string TargetBuffers = "buffers";

    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        [TargetEntries] = ["name", "amount", "kind", "category", "rhythm", "debitDay", "isFixed", "startPeriod", "endPeriod", "note"],
        [TargetDebts] = ["name", "creditor", "type", "originalAmount", "balance", "interestRate", "monthlyRate", "debitDay", "note"],
        [TargetBuffers] = ["name", "targetAmount", "balance", "isEmergencyFund", "note"],
    };

    public static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>
    {
        [TargetEntries] = ["name", "amount"],
        [TargetDebts] = ["name", "balance"],
        [TargetBuffers] = ["name"],
    };
}
