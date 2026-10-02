namespace BudgetPlaner.Domain;

/// <summary>Rolle im Haushalt (F-003).</summary>
public enum Role
{
    Owner = 0,
    Member = 1,
}

/// <summary>Typ einer Kategorie.</summary>
public enum CategoryType
{
    Income = 0,
    Expense = 1,
}

/// <summary>Gruppe nach der 50/30/20-Regel (D-01).</summary>
public enum BudgetGroup
{
    /// <summary>Keine Zuordnung (Einnahmen, Sonstiges).</summary>
    None = 0,
    /// <summary>Bedarf – Soll 50 %.</summary>
    Needs = 1,
    /// <summary>Wunsch – Soll 30 %.</summary>
    Wants = 2,
    /// <summary>Sparen (inkl. Schuldenabbau) – Soll 20 %.</summary>
    Savings = 3,
}

/// <summary>Art einer Budgetposition.</summary>
public enum EntryKind
{
    Income = 0,
    Expense = 1,
    /// <summary>Zuweisung an ein Polster (Sparplan) – reduziert Unverplant, ist aber keine Ausgabe (F-014).</summary>
    Allocation = 2,
}

/// <summary>Zahlungsrhythmus; der Zahlenwert ist das Intervall in Monaten (F-013).</summary>
public enum Rhythm
{
    Once = 0,
    Monthly = 1,
    BiMonthly = 2,
    Quarterly = 3,
    HalfYearly = 6,
    Yearly = 12,
}

/// <summary>Art einer Verbindlichkeit (BudgetHeld 1.2/1.3).</summary>
public enum DebtType
{
    Loan = 0,
    Mortgage = 1,
    Leasing = 2,
    Private = 3,
    CreditCard = 4,
    Overdraft = 5,
    Other = 6,
}

/// <summary>Rolle einer mit einer Schuld verknüpften Budgetposition.</summary>
public enum DebtRole
{
    None = 0,
    /// <summary>Reguläre Rate.</summary>
    Rate = 1,
    /// <summary>Zusatz-/Sondertilgung.</summary>
    Extra = 2,
}

public enum BufferTransactionType
{
    Allocation = 0,
    Withdrawal = 1,
}

/// <summary>Ampelstatus (F-051). Neutral = nicht bewertbar.</summary>
public enum AmpelStatus
{
    Neutral = 0,
    Green = 1,
    Yellow = 2,
    Red = 3,
}

/// <summary>Benachrichtigungsarten (F-080, F-081), einzeln (de)aktivierbar.</summary>
public enum NotificationType
{
    MonthStart = 0,
    BudgetOverplanned = 1,
    NegativeBalanceForecast = 2,
    BudgetEarlyWarning = 3,
    CategoryOverrun = 4,
    BufferGoalReached = 5,
    DebtPaidOff = 6,
    DebtRateDue = 7,
    FixedCostRatioHigh = 8,
    HouseholdMembership = 9,
    SyncConflict = 10,
}

public enum NotificationChannel
{
    InApp = 0,
    Push = 1,
    Email = 2,
}

public static class RhythmExtensions
{
    /// <summary>Intervall in Monaten; 0 bei einmaligen Positionen.</summary>
    public static int IntervalMonths(this Rhythm rhythm) => (int)rhythm;
}
