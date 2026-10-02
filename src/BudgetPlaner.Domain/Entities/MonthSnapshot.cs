namespace BudgetPlaner.Domain.Entities;

/// <summary>Eingefrorener Monatsabschluss (I-01). Abgeschlossene Perioden sind schreibgeschützt.</summary>
public sealed class MonthSnapshot : HouseholdEntity
{
    public Period Period { get; set; }
    public DateTimeOffset ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }

    public decimal TotalIncome { get; set; }
    public decimal TotalExpenses { get; set; }

    /// <summary>Alle Polster-Zuweisungen (Positionen + manuell).</summary>
    public decimal TotalAllocations { get; set; }

    /// <summary>Manuelle Zuweisungen mit Budgetwirkung (ohne Sparplan-Positionen).</summary>
    public decimal ManualAllocations { get; set; }

    /// <summary>Entnahmen ins Budget.</summary>
    public decimal TotalWithdrawals { get; set; }

    public decimal CarryIn { get; set; }
    public decimal Unverplant { get; set; }

    /// <summary>In die Folgeperiode übertragener Rest (0, wenn einem Polster zugewiesen).</summary>
    public decimal CarryOver { get; set; }

    public Guid? CarryOverBufferId { get; set; }

    /// <summary>Tatsächlicher Kontostand am Periodenende (optional).</summary>
    public decimal? ClosingBalance { get; set; }

    public List<SnapshotLine> Lines { get; set; } = [];
}

/// <summary>Positionszeile eines Monatsabschlusses.</summary>
public sealed class SnapshotLine
{
    public Guid EntryId { get; set; }
    public string Name { get; set; } = "";
    public EntryKind Kind { get; set; }
    public Guid CategoryId { get; set; }
    public BudgetGroup Group { get; set; }
    public Rhythm Rhythm { get; set; }
    public decimal Planned { get; set; }
    public decimal Actual { get; set; }
    public bool IsFixed { get; set; }
    public bool IsDone { get; set; }
    public int? DebitDay { get; set; }
    public Guid? DebtId { get; set; }
    public DebtRole DebtRole { get; set; }
    public Guid? BufferId { get; set; }
}

/// <summary>Kontostand-Abgleich (I-03): Stand am Ende des Tages <see cref="Date"/>.</summary>
public sealed class BalanceCheckpoint : HouseholdEntity
{
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}
