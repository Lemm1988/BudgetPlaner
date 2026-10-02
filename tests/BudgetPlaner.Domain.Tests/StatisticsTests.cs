using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Tests;

public class StatisticsTests
{
    private static readonly Period Jan = new(2026, 1);

    private static TestHousehold Family()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 4000, 1);
        h.Expense("Miete", 1500, "housing", 3, isFixed: true);
        h.Expense("Lebensmittel", 600, "living");
        h.Expense("Hobby", 300, "guiltfree");
        h.Expense("ETF", 400, "investing", isFixed: true);
        return h;
    }

    [Fact(DisplayName = "F-060: Segmentsumme = Gesamtausgaben, leere Kategorien ausgeblendet, Klick zeigt Positionen")]
    public void ByCategory()
    {
        var eng = Family().Engine(new DateOnly(2026, 1, 15));
        var b = eng.Statistics.ExpensesByCategory(Jan, Jan);
        Assert.Equal(eng.Month(Jan).Expenses, b.Segments.Sum(s => s.Amount));
        Assert.Equal(b.Total, b.Segments.Sum(s => s.Amount));
        Assert.Equal(3, b.Segments.Count); // Bedarf, Wunsch, Sparen – „Sonstiges“ leer → ausgeblendet
        Assert.Equal(2, b.Segments[0].Lines.Count);
        var detail = eng.Statistics.ExpensesByCategory(Jan, Jan, byRoot: false);
        Assert.Equal(4, detail.Segments.Count);
        Assert.Equal(100m, Math.Round(b.Segments.Sum(s => s.Percent), 6));
    }

    [Fact(DisplayName = "F-061: Werte stimmen mit Dashboard überein, fehlende Monate sind Lücken")]
    public void Series()
    {
        var eng = Family().Engine(new DateOnly(2026, 3, 15));
        var s = eng.Statistics.IncomeExpenseSeries(new Period(2026, 3), 6);
        Assert.Equal(6, s.Count);
        Assert.Null(s[0].Income);
        Assert.Null(s[2].Expenses);
        Assert.Equal(eng.Month(Jan).Income, s[3].Income);
        Assert.Equal(eng.Month(Jan).Expenses, s[3].Expenses);
    }

    [Fact(DisplayName = "F-062: Ist-Werte summieren zu 100 %, Abweichung > 5 Punkte zeigt Hinweis")]
    public void FiftyThirtyTwenty()
    {
        var h = Family();
        h.Expense("Krumme Summe", 33.33m, "living");
        var eng = h.Engine(new DateOnly(2026, 1, 15));
        var r = eng.Statistics.FiftyThirtyTwenty(Jan, Jan);
        Assert.Equal(100.0m, r.Groups.Sum(g => g.ActualPercent));
        var needs = r.Groups.Single(g => g.Group == BudgetGroup.Needs);
        Assert.True(needs.ShowHint); // Bedarf ≈ 75 % vs. Soll 50 %
        var six = eng.Statistics.FiftyThirtyTwenty(new Period(2025, 8), Jan);
        Assert.Equal(100.0m, six.Groups.Sum(g => g.ActualPercent));
    }

    [Fact(DisplayName = "F-063: Sparquote korrekt gerundet, Durchschnitt erst ab dem 3. Monat")]
    public void SavingsRate()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 3000);
        var etf = h.Expense("ETF", 333.33m, "investing");
        h.Change(etf, new Period(2026, 3), 600);
        var eng = h.Engine(new DateOnly(2026, 4, 1));
        var s = eng.Statistics.SavingsRate(new Period(2026, 4), 4);
        Assert.Equal(11.1m, s[0].Rate);
        Assert.Null(s[0].Average3);
        Assert.Null(s[1].Average3);
        Assert.Equal(20.0m, s[2].Rate);
        Assert.Equal(MoneyMath.Round1((11.1m + 11.1m + 20m) / 3), s[2].Average3);
    }

    [Fact(DisplayName = "F-064: Top-Ausgaben korrekt sortiert, Prozentanteil korrekt")]
    public void Top()
    {
        var eng = Family().Engine(new DateOnly(2026, 1, 15));
        var top = eng.Statistics.TopExpenses(Jan, Jan);
        Assert.Equal(["Miete", "Lebensmittel", "ETF", "Hobby"], top.Select(t => t.Name));
        Assert.Equal(1500m / 2800m * 100m, top[0].Percent);
    }

    [Fact(DisplayName = "F-065: Jahressumme = Summe 12 Monate; ohne Vorjahr kein Prozentwert; > 20 % hervorgehoben")]
    public void YearMatrix()
    {
        var h = Family();
        var eng = h.Engine(new DateOnly(2026, 6, 1));
        var y = eng.Statistics.YearOverview(2026);
        var income = y.Rows.Single(r => r.Key == "income");
        Assert.Equal(income.Months.Sum(m => m ?? 0), income.YearTotal);
        Assert.Equal(48000, income.YearTotal);
        Assert.False(y.HasPreviousYear);
        Assert.Null(income.DeviationPercent);

        var h2 = new TestHousehold(firstPeriod: new Period(2025, 1));
        var sal = h2.Income("Gehalt", 1000, start: new Period(2025, 1));
        h2.Change(sal, new Period(2026, 1), 1300);
        var y2 = h2.Engine(new DateOnly(2026, 6, 1)).Statistics.YearOverview(2026);
        var inc2 = y2.Rows.Single(r => r.Key == "income");
        Assert.True(y2.HasPreviousYear);
        Assert.Equal(30m, inc2.DeviationPercent);
        Assert.True(inc2.Highlight);
        Assert.Equal(12, y2.ComparedMonths.Count);

        // Haushalt erst seit November: Vergleich nur über Nov/Dez, kein falscher Prozentwert gegen ein Rumpfjahr
        var h3 = new TestHousehold(firstPeriod: new Period(2025, 11));
        var sal3 = h3.Income("Gehalt", 1000, start: new Period(2025, 11));
        h3.Change(sal3, new Period(2026, 11), 1100);
        var y3 = h3.Engine(new DateOnly(2026, 12, 1)).Statistics.YearOverview(2026);
        var inc3 = y3.Rows.Single(r => r.Key == "income");
        Assert.Equal([11, 12], y3.ComparedMonths);
        Assert.Equal(2000m, inc3.PreviousYearTotal);
        Assert.Equal(10m, inc3.DeviationPercent);
        Assert.Equal(inc3.Months.Sum(m => m ?? 0), inc3.YearTotal);
    }

    [Fact(DisplayName = "F-066: Punktlandung = 0 €/0 % grün, Überschreitung rot, Summe = Gesamtabweichung")]
    public void PlanActual()
    {
        var h = Family();
        var food = h.Data.Entries.Single(e => e.Name == "Lebensmittel");
        var hobby = h.Data.Entries.Single(e => e.Name == "Hobby");
        h.Month(food, Jan, actual: 650);
        h.Month(hobby, Jan, actual: 250);
        var r = h.Engine(new DateOnly(2026, 1, 31)).Statistics.PlanVsActual(Jan, Jan);
        var living = r.Rows.Single(x => x.CategoryId == h.Cat("living").Id);
        Assert.Equal(50, living.Deviation);
        Assert.Equal(AmpelStatus.Red, living.Status);
        var housing = r.Rows.Single(x => x.CategoryId == h.Cat("housing").Id);
        Assert.Equal(0, housing.Deviation);
        Assert.Equal(0, housing.DeviationPercent);
        Assert.Equal(AmpelStatus.Green, housing.Status);
        Assert.Equal(0, r.ExpenseDeviation);
        Assert.Equal(r.ExpenseDeviation, r.Rows.Where(x => x.Kind == EntryKind.Expense && x.Level == 0).Sum(x => x.Deviation));
    }

    [Fact(DisplayName = "F-067: Fixkostenquote korrekt, > 60 % löst Warnhinweis aus")]
    public void FixedCost()
    {
        var eng = Family().Engine(new DateOnly(2026, 1, 15));
        var f = eng.Statistics.FixedCostRatio(Jan);
        Assert.Equal(1900m / 4000m * 100m, f.RatioPercent);
        Assert.False(f.Warning);
        var h = new TestHousehold();
        h.Income("Gehalt", 1000);
        h.Expense("Miete", 601, "housing", isFixed: true);
        Assert.True(h.Engine(new DateOnly(2026, 1, 15)).Statistics.FixedCostRatio(Jan).Warning);
    }

    [Fact(DisplayName = "F-068: Vermögen = Polster − Restschulden, Verlauf ≥ 12 Monate")]
    public void NetWorth()
    {
        var h = new TestHousehold(firstPeriod: new Period(2025, 1));
        var b = h.Buffer("Notgroschen");
        h.Tx(b, new DateOnly(2025, 6, 1), 3000);
        var d = h.Debt("Auto", 5000, start: new DateOnly(2025, 3, 1));
        h.Payment(d, new DateOnly(2025, 9, 1), 1000);
        var eng = h.Engine(new DateOnly(2026, 1, 10));
        var nw = eng.Statistics.NetWorth(new Period(2026, 1), 13);
        Assert.Equal(13, nw.Count);
        Assert.Equal(-1000m, nw[^1].NetWorth);
        Assert.Equal(3000 - 5000, nw.Single(p => p.Period == new Period(2025, 6)).NetWorth);
        Assert.Equal(0, nw.Single(p => p.Period == new Period(2025, 1)).NetWorth);
    }
}

public class ForecastTests
{
    [Fact(DisplayName = "F-073: Warnung bei Restausgaben > Resteinnahmen, verschwindet nach Behebung")]
    public void EarlyWarning()
    {
        var h = new TestHousehold();
        h.Checkpoint(new DateOnly(2026, 3, 9), 200);
        h.Income("Gehalt", 2000, 1);
        var big = h.Expense("Reparatur", 900, "mobility", 20, new Period(2026, 3), Rhythm.Once);
        var eng = h.Engine(new DateOnly(2026, 3, 10));
        Assert.Contains(eng.Forecasts.Warnings(), w => w.Type == NotificationType.BudgetEarlyWarning);
        big.Amount = 150;
        Assert.DoesNotContain(h.Engine(new DateOnly(2026, 3, 10)).Forecasts.Warnings(), w => w.Type == NotificationType.BudgetEarlyWarning);
    }

    [Fact(DisplayName = "F-073: Überplanung und Kategorie-Überschreitung")]
    public void OtherWarnings()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 1000);
        var food = h.Expense("Lebensmittel", 1100);
        h.Month(food, new Period(2026, 3), actual: 1200);
        var warnings = h.Engine(new DateOnly(2026, 3, 10)).Forecasts.Warnings();
        Assert.Contains(warnings, w => w.Type == NotificationType.BudgetOverplanned);
        Assert.Contains(warnings, w => w.Type == NotificationType.CategoryOverrun);
    }

    [Fact(DisplayName = "F-074: Durchschnitt × 12, bekannte Einmalzahlungen exakt")]
    public void YearEnd()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 3000);
        h.Income("Bonus", 1500, start: new Period(2026, 11), rhythm: Rhythm.Once);
        h.Expense("Miete", 1000, "housing");
        h.Expense("Versicherung", 600, "living", start: new Period(2026, 2), rhythm: Rhythm.Yearly);
        var y = h.Engine(new DateOnly(2026, 4, 15)).Forecasts.YearEnd(2026);
        Assert.Equal(4, y.ElapsedPeriods);
        Assert.Equal(3000 * 12 + 1500, y.Income.Projected);
        Assert.Equal(1000 * 12 + 600, y.Expenses.Projected);
        Assert.Equal(y.Income.Projected - y.Expenses.Projected, y.ProjectedSurplus);
    }
}
