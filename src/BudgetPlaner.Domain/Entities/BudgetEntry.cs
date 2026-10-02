namespace BudgetPlaner.Domain.Entities;

/// <summary>
/// Wiederkehrende oder einmalige Budgetposition (Einnahme, Ausgabe oder Polster-Zuweisung; F-010, F-011, F-013).
/// Der Grundbetrag gilt ab <see cref="StartPeriod"/>; spätere Änderungen stehen in <see cref="BudgetEntryChange"/>,
/// Abweichungen einzelner Monate und Ist-Werte in <see cref="BudgetEntryMonth"/>.
/// </summary>
public sealed class BudgetEntry : HouseholdEntity
{
    public Guid CategoryId { get; set; }
    public EntryKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>Grundbetrag (immer positiv).</summary>
    public decimal Amount { get; set; }

    public Rhythm Rhythm { get; set; } = Rhythm.Monthly;

    /// <summary>Erste (Fälligkeits-)Periode; verankert auch den Zyklus nicht-monatlicher Rhythmen.</summary>
    public Period StartPeriod { get; set; }

    /// <summary>Letzte Periode (inklusive); <c>null</c> = unbegrenzt.</summary>
    public Period? EndPeriod { get; set; }

    /// <summary>Abbuchungstag 1–31 (F-013); <c>null</c> = Periodenbeginn.</summary>
    public int? DebitDay { get; set; }

    /// <summary>Fixkosten (true) oder variabel (false) – F-011.</summary>
    public bool IsFixed { get; set; }

    /// <summary>Reihenfolge innerhalb der Kategorie (F-015).</summary>
    public int SortIndex { get; set; }

    /// <summary>Verknüpfte Schuld (Rate oder Zusatztilgung).</summary>
    public Guid? DebtId { get; set; }
    public DebtRole DebtRole { get; set; }

    /// <summary>Ziel-Polster bei <see cref="EntryKind.Allocation"/>.</summary>
    public Guid? BufferId { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>Betragsänderung bzw. Pause „ab diesem Monat“ (Delta-Editing, B-03; Pausieren F-011).</summary>
public sealed class BudgetEntryChange : HouseholdEntity
{
    public Guid EntryId { get; set; }
    public Period FromPeriod { get; set; }
    public decimal Amount { get; set; }
    public bool IsPaused { get; set; }
}

/// <summary>
/// Monatsbezogene Daten einer Position: Abweichung „nur dieser Monat“, Aussetzen, Ist-Betrag und
/// Abhaken als bezahlt/erhalten (D-02).
/// </summary>
public sealed class BudgetEntryMonth : HouseholdEntity
{
    public Guid EntryId { get; set; }
    public Period Period { get; set; }

    /// <summary>Planbetrag nur für diesen Monat (überschreibt Grundbetrag/Änderungen).</summary>
    public decimal? PlannedAmount { get; set; }

    /// <summary>Position in diesem Monat ausgesetzt.</summary>
    public bool IsSkipped { get; set; }

    /// <summary>Tatsächlicher Betrag; <c>null</c> = Ist entspricht Soll.</summary>
    public decimal? ActualAmount { get; set; }

    /// <summary>Als bezahlt (Ausgabe) bzw. erhalten (Einnahme) abgehakt.</summary>
    public bool IsDone { get; set; }

    public DateTimeOffset? DoneAt { get; set; }
    public string? Note { get; set; }
}
