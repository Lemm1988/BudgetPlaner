namespace BudgetPlaner.Domain.Entities;

/// <summary>
/// Kategorie (F-012). Oberkategorien tragen die 50/30/20-Gruppe, Unterkategorien erben sie (D-01).
/// </summary>
public sealed class Category : HouseholdEntity
{
    /// <summary>Vom Nutzer vergebener Name; leer = übersetzter Standardname aus <see cref="DefaultKey"/>.</summary>
    public string? Name { get; set; }

    /// <summary>Schlüssel der Standardkategorie (z. B. „housing“) für die Übersetzung DE/EN.</summary>
    public string? DefaultKey { get; set; }

    public CategoryType Type { get; set; }
    public BudgetGroup Group { get; set; }
    public Guid? ParentId { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public int SortIndex { get; set; }

    /// <summary>Systemkategorien (Oberkategorien, „Sonstiges“) sind nicht löschbar.</summary>
    public bool IsSystem { get; set; }
}
