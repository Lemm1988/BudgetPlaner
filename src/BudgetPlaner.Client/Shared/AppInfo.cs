namespace BudgetPlaner.Client.Shared;

/// <summary>Versionsinformationen (Footer, B-19).</summary>
public static class AppInfo
{
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

/// <summary>Farbpalette für Diagramme (CSS-Variablen, in Hell/Dunkel kontrastgeprüft).</summary>
public static class ChartColors
{
    public static readonly string[] Series =
        ["var(--chart-1)", "var(--chart-2)", "var(--chart-3)", "var(--chart-4)", "var(--chart-5)", "var(--chart-6)", "var(--chart-7)", "var(--chart-8)"];

    public static string At(int i) => Series[((i % Series.Length) + Series.Length) % Series.Length];
}
