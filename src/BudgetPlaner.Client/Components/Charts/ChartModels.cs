using System.Globalization;

namespace BudgetPlaner.Client.Components.Charts;

/// <summary>Segment eines Ringdiagramms.</summary>
public sealed record DonutSegment(string Key, string Label, decimal Value, string Color);

/// <summary>Datenreihe eines Linien-/Balkendiagramms; <c>null</c>-Werte erzeugen Lücken (F-061).</summary>
public sealed record ChartSeries(string Label, IReadOnlyList<decimal?> Values, string Color, bool Dashed = false, bool Area = false);

internal static class ChartMath
{
    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>„Schöne“ Achsenwerte (1, 2, 2.5, 5 × 10^n).</summary>
    public static (double Min, double Max, double Step) NiceScale(double min, double max, int ticks = 5)
    {
        if (min == max)
        {
            if (min == 0) return (0, 1, 0.25);
            min = Math.Min(0, min);
            max = Math.Max(0, max) * 1.1;
        }
        var range = Nice(max - min, false);
        var step = Nice(range / (ticks - 1), true);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        return (niceMin, niceMax, step);
    }

    private static double Nice(double range, bool round)
    {
        if (range <= 0) return 1;
        var exp = Math.Floor(Math.Log10(range));
        var frac = range / Math.Pow(10, exp);
        double nice = round
            ? frac < 1.5 ? 1 : frac < 3 ? 2 : frac < 7 ? 5 : 10
            : frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
        return nice * Math.Pow(10, exp);
    }
}
