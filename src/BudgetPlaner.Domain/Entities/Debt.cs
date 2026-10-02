namespace BudgetPlaner.Domain.Entities;

/// <summary>Verbindlichkeit (F-020) inkl. Kreditart, Zins, Rate und Laufzeit.</summary>
public sealed class DebtEntry : HouseholdEntity
{
    /// <summary>Bezeichnung, z. B. „Autokredit“.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gläubiger, z. B. Bank oder Person.</summary>
    public string? Creditor { get; set; }

    public DebtType Type { get; set; }

    /// <summary>Ursprüngliche Summe (für den Fortschrittsbalken, F-022).</summary>
    public decimal OriginalAmount { get; set; }

    /// <summary>Restschuld zum Zeitpunkt <see cref="StartDate"/> (Erfassung).</summary>
    public decimal OpeningBalance { get; set; }

    public DateOnly StartDate { get; set; }

    /// <summary>Sollzins in % p. a.; <c>null</c>/0 = zinsfrei.</summary>
    public decimal? InterestRate { get; set; }

    /// <summary>Vertragliche Monatsrate (optional, F-020).</summary>
    public decimal? MonthlyRate { get; set; }

    public int? DebitDay { get; set; }

    /// <summary>Restlaufzeit in Monaten (Leasing/Kredit).</summary>
    public int? TermMonths { get; set; }

    /// <summary>Ende der Zinsbindung (Baufinanzierung).</summary>
    public DateOnly? FixedInterestUntil { get; set; }

    /// <summary>Restwert/Schlussrate (Leasing, Ballonfinanzierung).</summary>
    public decimal? ResidualValue { get; set; }

    /// <summary>Fällige Raten verknüpfter Budgetpositionen automatisch verbuchen.</summary>
    public bool AutoBook { get; set; } = true;

    public int SortIndex { get; set; }
    public string? Note { get; set; }
}

/// <summary>Verbuchte Zahlung auf eine Schuld (F-021).</summary>
public sealed class DebtPayment : HouseholdEntity
{
    public Guid DebtId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Zinsanteil (wird chronologisch berechnet).</summary>
    public decimal InterestPart { get; set; }

    /// <summary>Tilgungsanteil (wird chronologisch berechnet).</summary>
    public decimal PrincipalPart { get; set; }

    /// <summary>Sondertilgung: Betrag zählt vollständig als Tilgung.</summary>
    public bool IsExtra { get; set; }

    /// <summary>Zinssatz zum Buchungszeitpunkt (Zinsänderungen wirken nicht rückwirkend).</summary>
    public decimal? InterestRateApplied { get; set; }

    /// <summary>Quelle bei automatischer Buchung aus einer Budgetposition.</summary>
    public Guid? SourceEntryId { get; set; }
    public Period? SourcePeriod { get; set; }

    public string? Note { get; set; }
}
