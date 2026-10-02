namespace BudgetPlaner.Domain.Entities;

/// <summary>Polster (Rücklagentopf) mit optionalem Ziel (F-040). Der Stand ergibt sich aus den Transaktionen.</summary>
public sealed class BufferEntry : HouseholdEntity
{
    public string Name { get; set; } = "";

    /// <summary>Zielbetrag; <c>null</c> = nur Stand anzeigen (F-041).</summary>
    public decimal? TargetAmount { get; set; }

    /// <summary>Optionales Wunschdatum → benötigte Monatsrate wird berechnet.</summary>
    public DateOnly? TargetDate { get; set; }

    /// <summary>Geld liegt auf separatem Konto → Zuweisungen sind im Cashflow ein Abfluss.</summary>
    public bool SeparateAccount { get; set; } = true;

    /// <summary>Notgroschen/Finanzpolster – zählt für die Polster-Ampel.</summary>
    public bool IsEmergencyFund { get; set; }

    public string? Color { get; set; }
    public string? Icon { get; set; }
    public int SortIndex { get; set; }
    public string? Note { get; set; }
}

/// <summary>Zuweisung oder Entnahme (F-040, F-042) – dokumentiert Datum, Betrag und Grund.</summary>
public sealed class BufferTransaction : HouseholdEntity
{
    public Guid BufferId { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>Betrag (immer positiv; die Richtung ergibt sich aus <see cref="Type"/>).</summary>
    public decimal Amount { get; set; }

    public BufferTransactionType Type { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Wirkt auf Unverplant der Periode: Zuweisung verringert, Entnahme („ins Budget“) erhöht es.
    /// Anfangsbestände und „direkt ausgegebene“ Entnahmen wirken nicht.
    /// </summary>
    public bool AffectsBudget { get; set; } = true;

    /// <summary>Quelle bei automatischer Buchung aus einem Sparplan.</summary>
    public Guid? SourceEntryId { get; set; }
    public Period? SourcePeriod { get; set; }
}
