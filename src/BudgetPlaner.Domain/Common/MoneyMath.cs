namespace BudgetPlaner.Domain;

/// <summary>Rundungs- und Prozenthilfen. Geldbeträge werden kaufmännisch auf 2 Stellen gerundet.</summary>
public static class MoneyMath
{
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Round1(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>Anteil in Prozent (ungerundet) oder <c>null</c>, wenn die Basis 0 ist.</summary>
    public static decimal? Percent(decimal part, decimal total) => total == 0 ? null : part / total * 100m;

    /// <summary>
    /// Rundet Prozentwerte auf <paramref name="decimals"/> Stellen so, dass ihre Summe exakt 100 ergibt
    /// (Largest-Remainder-Verfahren, F-062 „Ist-Werte summieren zu 100 %“).
    /// </summary>
    public static decimal[] RoundToHundred(IReadOnlyList<decimal> rawPercents, int decimals = 1)
    {
        var result = new decimal[rawPercents.Count];
        if (rawPercents.Count == 0) return result;
        var total = rawPercents.Sum();
        if (total == 0) return result;

        var factor = (decimal)Math.Pow(10, decimals);
        var target = (long)(100m * factor);
        var scaled = rawPercents.Select(p => p / total * 100m * factor).ToArray();
        var floors = scaled.Select(s => (long)Math.Floor(s)).ToArray();
        var remainder = target - floors.Sum();
        var order = scaled
            .Select((s, i) => (Index: i, Fraction: s - Math.Floor(s)))
            .OrderByDescending(x => x.Fraction)
            .ThenBy(x => x.Index)
            .ToList();
        for (var k = 0; k < remainder && k < order.Count; k++) floors[order[k].Index]++;
        for (var i = 0; i < result.Length; i++) result[i] = floors[i] / factor;
        return result;
    }
}
