namespace BudgetPlaner.Domain.Entities;

/// <summary>
/// Basis aller synchronisierten Haushaltsdaten. <see cref="Version"/> ist die Haushalts-Änderungssequenz
/// der letzten Änderung (F-131); gelöschte Datensätze bleiben als Tombstone erhalten, damit andere
/// Geräte die Löschung beim nächsten Abgleich erfahren.
/// </summary>
public abstract class HouseholdEntity
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Beispieldaten (F-142) – mit einem Klick entfernbar.</summary>
    public bool IsDemo { get; set; }

    /// <summary>Flache Kopie (z. B. für Szenarien oder optimistische Änderungen im Client).</summary>
    public T CloneAs<T>() where T : HouseholdEntity => (T)MemberwiseClone();
}
