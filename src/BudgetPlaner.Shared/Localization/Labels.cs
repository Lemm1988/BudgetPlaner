using System.Text.Json;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Shared.Localization;

/// <summary>Übersetzte Bezeichnungen für Enums und Standardkategorien.</summary>
public static class Labels
{
    public static string Camel(Enum value) => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static string Of(Rhythm v, string lang) => Texts.Get(lang, "rhythm." + Camel(v));
    public static string Of(EntryKind v, string lang) => Texts.Get(lang, "kind." + Camel(v));
    public static string Of(BudgetGroup v, string lang) => Texts.Get(lang, "group." + Camel(v));
    public static string Of(DebtType v, string lang) => Texts.Get(lang, "debttype." + Camel(v));
    public static string Of(Role v, string lang) => Texts.Get(lang, "role." + Camel(v));
    public static string Of(AmpelStatus v, string lang) => Texts.Get(lang, "ampel." + Camel(v));
    public static string Of(NotificationType v, string lang) => Texts.Get(lang, "notiftype." + Camel(v));
    public static string Of(NotificationChannel v, string lang) => Texts.Get(lang, "channel." + Camel(v));

    /// <summary>Anzeigename einer Kategorie: eigener Name oder übersetzter Standardname.</summary>
    public static string CategoryName(Category? c, string lang)
    {
        if (c is null) return Texts.Get(lang, "cat.other");
        if (!string.IsNullOrWhiteSpace(c.Name)) return c.Name!;
        return c.DefaultKey is { } key ? Texts.Get(lang, "cat." + key) : "?";
    }

    public static string CategoryName(HouseholdIndex ix, Guid? id, string lang) =>
        CategoryName(id is { } g ? ix.CategoryById.GetValueOrDefault(g) : null, lang);
}
