using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Tests;

public class BufferTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "F-040: Neuer Topf startet bei 0 €, Zuweisung erhöht Stand exakt")]
    public void StartsAtZero()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Urlaub", 1000);
        Assert.Equal(0, h.Engine(new DateOnly(2026, 1, 5)).Buffers.Status(b.Id).Balance);
        h.Tx(b, new DateOnly(2026, 1, 5), 123.45m);
        Assert.Equal(123.45m, h.Engine(new DateOnly(2026, 1, 5)).Buffers.Status(b.Id).Balance);
    }

    [Fact(DisplayName = "F-041: 250/1000 € → 25 %; ohne Ziel nur Stand")]
    public void Progress()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Urlaub", 1000);
        var c = h.Buffer("Ohne Ziel");
        h.Tx(b, new DateOnly(2026, 1, 2), 250);
        h.Tx(c, new DateOnly(2026, 1, 2), 250);
        var eng = h.Engine(new DateOnly(2026, 1, 5));
        Assert.Equal(25, eng.Buffers.Status(b.Id).ProgressPercent);
        Assert.Null(eng.Buffers.Status(c.Id).ProgressPercent);
        Assert.Equal(250, eng.Buffers.Status(c.Id).Balance);
    }

    [Fact(DisplayName = "F-040: Zielerreichung erkannt")]
    public void GoalReached()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Urlaub", 500);
        h.Tx(b, new DateOnly(2026, 1, 2), 500);
        var s = h.Engine(new DateOnly(2026, 1, 5)).Buffers.Status(b.Id);
        Assert.True(s.GoalReached);
        Assert.True(s.Forecast.AlreadyReached);
    }

    [Fact(DisplayName = "F-042: Historie dokumentiert Datum, Betrag, Grund")]
    public void History()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Reparatur");
        h.Tx(b, new DateOnly(2026, 1, 2), 300);
        h.Tx(b, new DateOnly(2026, 1, 9), 120, BufferTransactionType.Withdrawal, true, "Fahrradreparatur");
        var s = h.Engine(new DateOnly(2026, 1, 10)).Buffers.Status(b.Id);
        Assert.Equal(180, s.Balance);
        var w = s.History.Single(x => x.Type == BufferTransactionType.Withdrawal);
        Assert.Equal(new DateOnly(2026, 1, 9), w.Date);
        Assert.Equal(-120, w.SignedAmount);
        Assert.Equal("Fahrradreparatur", w.Reason);
    }

    [Fact(DisplayName = "F-043/F-072: 200 €/Monat bis 2.000 € → 10 Monate")]
    public void SavingsForecast()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Ziel", 2000, createdAt: Created);
        h.Allocation(b, 200, 28, createdAt: Created);
        var s = h.Engine(new DateOnly(2026, 1, 5)).Buffers.Status(b.Id);
        Assert.True(s.Forecast.Possible);
        Assert.Equal(10, s.Forecast.Months);
        Assert.Equal(new Period(2026, 10), s.Forecast.GoalPeriod);
    }

    [Fact(DisplayName = "F-043: Bei 0 € Zuweisung „Keine Prognose möglich“")]
    public void NoForecastWithoutAllocation()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Ziel", 2000, createdAt: Created);
        Assert.False(h.Engine(new DateOnly(2026, 1, 5)).Buffers.Status(b.Id).Forecast.Possible);
    }

    [Fact(DisplayName = "F-072: Höhere Zuweisung → früheres Zieldatum; Durchschnitt statt Einzelwert")]
    public void HigherAllocationEarlier()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Ziel", 3000, createdAt: Created);
        var plan = h.Allocation(b, 100, 1, createdAt: Created);
        var engMarch = h.Engine(new DateOnly(2026, 3, 15));
        var before = engMarch.Buffers.Status(b.Id).Forecast;
        Assert.Equal(100, before.AverageMonthly);
        h.Change(plan, new Period(2026, 3), 400);
        var after = h.Engine(new DateOnly(2026, 2, 15)).Buffers.Status(b.Id).Forecast;
        // Feb: Jan 100 + Feb 100 gebucht, März noch nicht relevant → Ø 100
        Assert.Equal(100, after.AverageMonthly);
        var marchAfter = h.Engine(new DateOnly(2026, 3, 15)).Buffers.Status(b.Id).Forecast;
        // Jan 100, Feb 100, März 400 → Ø 200 (nicht 400 = Einzelwert)
        Assert.Equal(200, marchAfter.AverageMonthly);
        Assert.True(marchAfter.GoalPeriod < before.GoalPeriod);
    }

    [Fact(DisplayName = "Sparplan wird ab Anlage gebucht (Projektion bis zur Serverbuchung)")]
    public void PlanBooked()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Notgroschen", createdAt: Created);
        h.Allocation(b, 250, 2, createdAt: Created);
        var eng = h.Engine(new DateOnly(2026, 4, 1));
        Assert.Equal(750, eng.Buffers.Status(b.Id).Balance);
        Assert.All(eng.Buffers.Status(b.Id).History, x => Assert.True(x.IsProjected));
    }
}

public class CashflowTests
{
    [Fact(DisplayName = "F-030: Abbuchungstag 15 erscheint exakt am 15., Klick auf Tag zeigt Positionen")]
    public void DayPlacement()
    {
        var h = new TestHousehold();
        h.Expense("Strom", 90, "housing", 15);
        var cf = h.Engine(new DateOnly(2026, 3, 1)).Cashflow.ForPeriod(new Period(2026, 3));
        var day = cf.Days.Single(d => d.Items.Count > 0);
        Assert.Equal(new DateOnly(2026, 3, 15), day.Date);
        Assert.Equal("Strom", day.Items[0].Name);
    }

    [Fact(DisplayName = "F-031: Saldo Monatsende = Start + Einnahmen − Ausgaben; Saldo < 0 kritisch")]
    public void RunningBalance()
    {
        var h = new TestHousehold();
        h.Checkpoint(new DateOnly(2026, 2, 28), 100);
        h.Income("Gehalt", 2000, 28);
        h.Expense("Miete", 1200, "housing", 3);
        h.Expense("Strom", 100, "housing", 15);
        var cf = h.Engine(new DateOnly(2026, 3, 1)).Cashflow.ForPeriod(new Period(2026, 3));
        Assert.Equal(100, cf.StartBalance);
        Assert.Equal(100 + 2000 - 1300, cf.EndBalance);
        Assert.Equal(cf.StartBalance + cf.TotalIn + cf.TotalOut, cf.EndBalance);
        Assert.True(cf.Days.Single(d => d.Date == new DateOnly(2026, 3, 3)).IsCritical);
        Assert.False(cf.Days.Single(d => d.Date == new DateOnly(2026, 3, 28)).IsCritical);
        Assert.Equal(-1200, cf.MinBalance);
        Assert.Equal(new DateOnly(2026, 3, 15), cf.MinBalanceDate);
    }

    [Fact(DisplayName = "I-03: Rückwärtsrechnung vor dem ersten Abgleich")]
    public void BackwardFromCheckpoint()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000, 1);
        h.Expense("Miete", 1000, "housing", 5);
        h.Checkpoint(new DateOnly(2026, 3, 10), 1500);
        var eng = h.Engine(new DateOnly(2026, 3, 10));
        Assert.Equal(500, eng.Cashflow.BalanceAtEndOfDay(new DateOnly(2026, 2, 28)));
        Assert.Equal(1500, eng.Cashflow.BalanceAtEndOfDay(new DateOnly(2026, 3, 31)));
    }

    [Fact(DisplayName = "Zuweisung an separates Konto ist Abfluss, auf dem Girokonto nicht")]
    public void AllocationOutflow()
    {
        var h = new TestHousehold();
        var sep = h.Buffer("Tagesgeld", separate: true);
        var giro = h.Buffer("Umschlag", separate: false);
        h.Allocation(sep, 100, 2);
        h.Allocation(giro, 50, 2);
        var cf = h.Engine(new DateOnly(2026, 3, 1)).Cashflow.ForPeriod(new Period(2026, 3));
        Assert.Equal(-100, cf.TotalOut);
    }

    [Fact(DisplayName = "F-032: Künftige Monate zeigen wiederkehrende Positionen; 12 Monate vor/zurück")]
    public void FutureProjection()
    {
        var h = new TestHousehold(firstPeriod: new Period(2025, 1));
        h.Income("Gehalt", 2000, 1, new Period(2025, 1));
        var eng = h.Engine(new DateOnly(2026, 3, 1));
        Assert.Equal(2000, eng.Cashflow.ForPeriod(new Period(2027, 3)).TotalIn);
        Assert.Equal(2000, eng.Cashflow.ForPeriod(new Period(2025, 3)).TotalIn);
    }

    [Fact(DisplayName = "F-070: Nächster Monat = Saldo + Einnahmen − Ausgaben")]
    public void BalanceForecast()
    {
        var h = new TestHousehold();
        h.Checkpoint(new DateOnly(2026, 3, 1), 500);
        h.Income("Gehalt", 2000, 25);
        h.Expense("Miete", 1200, "housing", 3);
        var f = h.Engine(new DateOnly(2026, 3, 1)).Forecasts.Balance(12);
        var march = f.Months[0];
        var april = f.Months[1];
        Assert.Equal(march.EndBalance + 2000 - 1200, april.EndBalance);
        Assert.Equal(13, f.Months.Count);
        Assert.Contains(f.Daily, d => d.IsForecast);
        Assert.Contains(f.Daily, d => !d.IsForecast);
    }
}
