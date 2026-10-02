namespace BudgetPlaner.Domain;

/// <summary>
/// Konfigurierbare Schwellwerte für Ampel, 50/30/20-Ziele und Warnungen (F-051, F-062, F-065, F-067;
/// NFR Wartbarkeit „Regeln ohne Kernänderung konfigurierbar“). Prozentwerte beziehen sich auf die Einnahmen.
/// </summary>
public sealed record AmpelRules
{
    public static AmpelRules Default { get; } = new();

    // 50/30/20 (F-062)
    public decimal TargetNeedsPercent { get; init; } = 50;
    public decimal TargetWantsPercent { get; init; } = 30;
    public decimal TargetSavingsPercent { get; init; } = 20;
    public decimal FiftyThirtyTwentyHintPoints { get; init; } = 5;

    // Budget / Unverplant (F-051): ≥ 0 grün, darunter gelb bis zu diesem Anteil, darunter rot.
    public decimal UnverplantRedBelowPercent { get; init; } = -10;

    // Cashflow: minimaler Tagessaldo als Anteil der Einnahmen.
    public decimal CashflowGreenMinPercent { get; init; } = 10;

    // Schulden: Raten ÷ Einnahmen.
    public decimal DebtServiceGreenMaxPercent { get; init; } = 20;
    public decimal DebtServiceYellowMaxPercent { get; init; } = 35;

    // Polster: Reichweite in Monatsausgaben.
    public decimal BufferGreenMonths { get; init; } = 3;
    public decimal BufferYellowMonths { get; init; } = 1;

    // Gruppen
    public decimal NeedsYellowMaxPercent { get; init; } = 60;
    public decimal WantsYellowMaxPercent { get; init; } = 40;
    public decimal SavingsRateGreenMinPercent { get; init; } = 20;
    public decimal SavingsRateYellowMinPercent { get; init; } = 10;

    /// <summary>Richtwerte je Standardkategorie (BudgetHeld 1.3 „Kategorie-Ampeln“).</summary>
    public List<CategoryLimit> CategoryLimits { get; init; } =
    [
        new("housing", 30, 40),
        new("mobility", 10, 15),
        new("living", 15, 25),
        new("guiltfree", 10, 15),
    ];

    /// <summary>Fixkostenquote, ab der gewarnt wird (F-067).</summary>
    public decimal FixedCostWarnPercent { get; init; } = 60;

    /// <summary>Jahresabweichung, ab der hervorgehoben wird (F-065).</summary>
    public decimal YearDeviationHighlightPercent { get; init; } = 20;

    /// <summary>Vorschlag Notgroschen = Faktor × Nettoeinkommen (B-16).</summary>
    public decimal EmergencyFundIncomeMultiplier { get; init; } = 3;
}

public sealed record CategoryLimit(string CategoryKey, decimal GreenMaxPercent, decimal YellowMaxPercent);
