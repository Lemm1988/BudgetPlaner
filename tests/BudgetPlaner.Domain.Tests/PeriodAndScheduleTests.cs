using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Tests;

public class PeriodAndScheduleTests
{
    [Fact(DisplayName = "F-101: Start=25 → „November“ beginnt am 25. Oktober")]
    public void MonthStartDay25()
    {
        var cal = new PeriodCalendar([new MonthStartRule(new Period(2000, 1), 25)]);
        var (start, end) = cal.Range(new Period(2026, 11));
        Assert.Equal(new DateOnly(2026, 10, 25), start);
        Assert.Equal(new DateOnly(2026, 11, 24), end);
        Assert.Equal(new Period(2026, 11), cal.PeriodOf(new DateOnly(2026, 10, 25)));
        Assert.Equal(new Period(2026, 10), cal.PeriodOf(new DateOnly(2026, 10, 24)));
    }

    [Fact(DisplayName = "F-101: Starttag 1 = Kalendermonat")]
    public void MonthStartDay1()
    {
        var (start, end) = PeriodCalendar.Default.Range(new Period(2028, 2));
        Assert.Equal(new DateOnly(2028, 2, 1), start);
        Assert.Equal(new DateOnly(2028, 2, 29), end);
    }

    [Theory(DisplayName = "F-101/I-08: Wechsel des Starttags erzeugt weder Lücken noch Überlappungen")]
    [InlineData(1, 25)]
    [InlineData(25, 1)]
    [InlineData(15, 28)]
    public void ChangeWithoutGaps(int from, int to)
    {
        var rules = PeriodCalendar.WithChangedDay([new MonthStartRule(new Period(2020, 1), from)], new Period(2026, 5), to);
        var cal = new PeriodCalendar(rules);
        Assert.Equal(from, cal.StartDayFor(new Period(2026, 5)));
        Assert.Equal(to, cal.StartDayFor(new Period(2026, 6)));
        for (var p = new Period(2026, 1); p < new Period(2027, 6); p = p.Next)
        {
            Assert.Equal(cal.End(p).AddDays(1), cal.Start(p.Next));
            Assert.True(cal.Start(p) <= cal.End(p));
        }
        for (var d = new DateOnly(2026, 1, 1); d < new DateOnly(2027, 6, 1); d = d.AddDays(1))
        {
            var p = cal.PeriodOf(d);
            Assert.InRange(d, cal.Start(p), cal.End(p));
        }
    }

    [Fact(DisplayName = "F-101: Änderung wirkt nur auf zukünftige Monate")]
    public void ChangeOnlyFuture()
    {
        var rules = PeriodCalendar.WithChangedDay([new MonthStartRule(new Period(2020, 1), 1)], new Period(2026, 10), 25);
        var cal = new PeriodCalendar(rules);
        Assert.Equal(new DateOnly(2026, 10, 1), cal.Start(new Period(2026, 10)));
        Assert.Equal(new DateOnly(2026, 10, 31), cal.End(new Period(2026, 10)));
        Assert.Equal(new DateOnly(2026, 11, 24), cal.End(new Period(2026, 11)));
    }

    [Fact(DisplayName = "F-030: Abbuchungstag 15 erscheint exakt am 15.")]
    public void DueDay15() => Assert.Equal(new DateOnly(2026, 3, 15), PeriodCalendar.Default.DueDate(new Period(2026, 3), 15));

    [Theory(DisplayName = "F-030: Abbuchungstag jenseits Monatsende → letzter Tag des Monats")]
    [InlineData(2026, 2, 31, 28)]
    [InlineData(2028, 2, 30, 29)]
    [InlineData(2026, 4, 31, 30)]
    public void DueDayBeyondMonthEnd(int year, int month, int day, int expectedDay) =>
        Assert.Equal(new DateOnly(year, month, expectedDay), PeriodCalendar.Default.DueDate(new Period(year, month), day));

    [Fact(DisplayName = "F-013: Abbuchungstag bei Starttag 25 liegt im richtigen Kalendermonat")]
    public void DueDateWithStartDay()
    {
        var cal = new PeriodCalendar([new MonthStartRule(new Period(2000, 1), 25)]);
        Assert.Equal(new DateOnly(2026, 10, 28), cal.DueDate(new Period(2026, 11), 28));
        Assert.Equal(new DateOnly(2026, 11, 3), cal.DueDate(new Period(2026, 11), 3));
        Assert.Equal(new DateOnly(2026, 2, 28), cal.DueDate(new Period(2026, 3), 30));
    }

    [Fact(DisplayName = "F-010: Monatliche Einnahmen werden automatisch in Folgemonate übernommen")]
    public void MonthlyCarriesForward()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 3000, 1);
        var eng = h.Engine(new DateOnly(2026, 6, 1));
        foreach (var m in new[] { 1, 2, 7, 12 })
            Assert.Equal(3000, eng.Month(new Period(2026, m)).Income);
        Assert.Equal(3000, eng.Month(new Period(2028, 4)).Income);
    }

    [Fact(DisplayName = "F-013: Jährliche Position erscheint nur im Fälligkeitsmonat")]
    public void YearlyOnlyInDueMonth()
    {
        var h = new TestHousehold();
        h.Expense("KFZ-Versicherung", 480, "mobility", 1, new Period(2026, 3), Rhythm.Yearly, true);
        var eng = h.Engine(new DateOnly(2026, 1, 1));
        Assert.Equal(480, eng.Month(new Period(2026, 3)).Expenses);
        Assert.Equal(0, eng.Month(new Period(2026, 4)).Expenses);
        Assert.Equal(0, eng.Month(new Period(2027, 2)).Expenses);
        Assert.Equal(480, eng.Month(new Period(2027, 3)).Expenses);
    }

    [Fact(DisplayName = "F-013: Geglättete Statistik verteilt jährliche Positionen anteilig")]
    public void YearlySmoothed()
    {
        var h = new TestHousehold();
        h.Expense("KFZ-Versicherung", 480, "mobility", 1, new Period(2026, 3), Rhythm.Yearly, true);
        var eng = h.Engine(new DateOnly(2026, 12, 31));
        var series = eng.Statistics.IncomeExpenseSeries(new Period(2026, 12), 10, smoothed: true);
        Assert.All(series, p => Assert.Equal(40, p.Expenses));
    }

    [Fact(DisplayName = "B-03: Änderung „ab diesem Monat“ und „nur diesen Monat“")]
    public void DeltaEditing()
    {
        var h = new TestHousehold();
        var e = h.Expense("Strom", 90);
        h.Change(e, new Period(2026, 5), 110);
        h.Month(e, new Period(2026, 7), planned: 200);
        var eng = h.Engine(new DateOnly(2026, 1, 1));
        Assert.Equal(90, eng.Month(new Period(2026, 4)).Expenses);
        Assert.Equal(110, eng.Month(new Period(2026, 5)).Expenses);
        Assert.Equal(200, eng.Month(new Period(2026, 7)).Expenses);
        Assert.Equal(110, eng.Month(new Period(2026, 8)).Expenses);
    }

    [Fact(DisplayName = "F-011: Pausieren ab Monat und einmaliges Aussetzen")]
    public void PauseAndSkip()
    {
        var h = new TestHousehold();
        var e = h.Expense("Fitness", 30);
        h.Change(e, new Period(2026, 4), 30, paused: true);
        h.Change(e, new Period(2026, 6), 30);
        h.Month(e, new Period(2026, 2), skipped: true);
        var eng = h.Engine(new DateOnly(2026, 1, 1));
        Assert.Equal(30, eng.Month(new Period(2026, 1)).Expenses);
        Assert.Equal(0, eng.Month(new Period(2026, 2)).Expenses);
        Assert.Equal(0, eng.Month(new Period(2026, 4)).Expenses);
        Assert.Equal(0, eng.Month(new Period(2026, 5)).Expenses);
        Assert.Equal(30, eng.Month(new Period(2026, 6)).Expenses);
        Assert.True(eng.Month(new Period(2026, 4)).Lines.Single().IsPaused);
    }

    [Fact(DisplayName = "Rhythmus vierteljährlich, einmalig und Enddatum")]
    public void RhythmsAndEnd()
    {
        var h = new TestHousehold();
        h.Expense("Quartal", 90, start: new Period(2026, 2), rhythm: Rhythm.Quarterly);
        h.Expense("Einmalig", 500, start: new Period(2026, 3), rhythm: Rhythm.Once);
        h.Expense("Abo", 10, end: new Period(2026, 2));
        var eng = h.Engine(new DateOnly(2026, 1, 1));
        Assert.Equal(10, eng.Month(new Period(2026, 1)).Expenses);
        Assert.Equal(100, eng.Month(new Period(2026, 2)).Expenses);
        Assert.Equal(500, eng.Month(new Period(2026, 3)).Expenses);
        Assert.Equal(0, eng.Month(new Period(2026, 4)).Expenses);
        Assert.Equal(90, eng.Month(new Period(2026, 5)).Expenses);
    }

    [Fact(DisplayName = "Period: Arithmetik, Parsing und JSON")]
    public void PeriodBasics()
    {
        var p = Period.Parse("2026-12");
        Assert.Equal(new Period(2027, 1), p.Next);
        Assert.Equal(new Period(2025, 12), new Period(2026, 1).Previous);
        Assert.Equal(13, Period.MonthsBetween(new Period(2026, 1), new Period(2027, 2)));
        Assert.Equal("\"2026-12\"", System.Text.Json.JsonSerializer.Serialize(p));
        Assert.Equal(p, System.Text.Json.JsonSerializer.Deserialize<Period>("\"2026-12\""));
        Assert.False(Period.TryParse("2026-13", out _));
        Assert.Equal(202612, p.ToKey());
    }
}
