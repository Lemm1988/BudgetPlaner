using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudgetPlaner.Shared;

/// <summary>Einheitliche JSON-Einstellungen für API, Sync, IndexedDB und Export.</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>Für den Web-Host (ASP.NET Core) identisch konfigurieren.</summary>
    public static void Apply(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.PropertyNameCaseInsensitive = true;
        if (!o.Converters.OfType<JsonStringEnumConverter>().Any())
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }
}
