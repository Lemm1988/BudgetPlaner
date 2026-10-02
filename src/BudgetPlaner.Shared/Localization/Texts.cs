using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace BudgetPlaner.Shared.Localization;

/// <summary>
/// Übersetzungen DE/EN (B-12) aus eingebetteten JSON-Dateien. Fehlende Schlüssel fallen auf Deutsch und dann auf
/// den Schlüssel selbst zurück. Platzhalter: {0}, {1}, …
/// </summary>
public static class Texts
{
    public const string German = "de";
    public const string English = "en";
    public static readonly IReadOnlyList<string> Languages = [German, English];

    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> Catalog = new(Load);

    public static string Normalize(string? lang) =>
        lang is not null && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : German;

    public static string Get(string lang, string key)
    {
        var all = Catalog.Value;
        if (all.TryGetValue(Normalize(lang), out var dict) && dict.TryGetValue(key, out var text)) return text;
        if (all.TryGetValue(German, out var de) && de.TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    public static string Get(string lang, string key, params object?[] args)
    {
        var template = Get(lang, key);
        if (args.Length == 0) return template;
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch (FormatException) { return template; }
    }

    public static bool Has(string lang, string key) =>
        Catalog.Value.TryGetValue(Normalize(lang), out var dict) && dict.ContainsKey(key);

    /// <summary>Alle Schlüssel einer Sprache (für Vollständigkeitstests).</summary>
    public static IEnumerable<string> Keys(string lang) =>
        Catalog.Value.TryGetValue(Normalize(lang), out var dict) ? dict.Keys : [];

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var asm = typeof(Texts).Assembly;
        var result = new Dictionary<string, FrozenDictionary<string, string>>();
        foreach (var lang in Languages)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith($".{lang}.json", StringComparison.Ordinal)).Order())
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
                foreach (var (k, v) in map) dict[k] = v;
            }
            result[lang] = dict.ToFrozenDictionary(StringComparer.Ordinal);
        }
        return result.ToFrozenDictionary();
    }
}
