namespace BudgetPlaner.Domain.Entities;

/// <summary>Haushalt: bündelt alle Finanzdaten einer Familie (F-002).</summary>
public sealed class Household
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>ISO-4217-Code der Haushaltswährung (F-100, eine Währung je Haushalt).</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>Historie der Monatsstart-Tage; eine Änderung wirkt erst ab der Folgeperiode (F-101).</summary>
    public List<MonthStartRule> MonthStartRules { get; set; } = [];

    /// <summary>Erste Budgetperiode des Haushalts; frühere Monate sind in Statistiken Lücken (F-061).</summary>
    public Period FirstPeriod { get; set; }

    /// <summary>Haushaltsspezifische Ampel-/Zielwerte; <c>null</c> = Standardregeln.</summary>
    public AmpelRules? AmpelRules { get; set; }

    /// <summary>Zeitpunkt, zu dem die Ersteinrichtung abgeschlossen oder übersprungen wurde (F-140).</summary>
    public DateTimeOffset? OnboardingCompletedAt { get; set; }

    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Server: fortlaufende Änderungssequenz des Haushalts (F-131); nicht Teil der Sync-Daten.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long ChangeSeq { get; set; }

    /// <summary>Server: Tombstones bis zu dieser Sequenz wurden bereinigt → ältere Clients brauchen eine Vollsynchronisation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long PurgedSeq { get; set; }

    /// <summary>Aktuell gültiger Monatsstart-Tag (letzte Regel).</summary>
    public int CurrentMonthStartDay =>
        MonthStartRules.Count == 0 ? 1 : MonthStartRules.MaxBy(r => r.FromPeriod.Index)!.Day;
}

/// <summary>Ab Periode <see cref="FromPeriod"/> beginnt jede Periode am Tag <see cref="Day"/> (1–28).</summary>
public sealed record MonthStartRule(Period FromPeriod, int Day);

/// <summary>Sichtbares Haushaltsmitglied (aus der Nutzertabelle abgeleitet).</summary>
public sealed class Member
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public Role Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}
