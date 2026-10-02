using System.Diagnostics;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Calculators;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Domain.Scenarios;

namespace BudgetPlaner.Domain.Tests;

public class ScenarioTests
{
    [Fact(DisplayName = "F-075: Szenario wirkt nur in der Simulation; Verwerfen setzt exakt zurück")]
    public void ScenarioIsolated()
    {
        var h = new TestHousehold();
        h.Checkpoint(new DateOnly(2026, 3, 1), 1000);
        var salary = h.Income("Gehalt", 3000, 1);
        var rent = h.Expense("Miete", 1200, "housing", 3);
        var d = h.Debt("Auto", 6000, monthly: 500, day: 15, start: new DateOnly(2026, 3, 1));
        var today = new DateOnly(2026, 3, 2);
        var baseline = h.Engine(today);
        var baseBalance = baseline.Forecasts.Balance(12).Months[^1].EndBalance;
        var baseDebt = baseline.Debts.Forecast(d.Id).Months;
        var entriesBefore = h.Data.Entries.Count;
        var changesBefore = h.Data.EntryChanges.Count;

        var scenario = new Scenario
        {
            Name = "Elternzeit",
            Changes =
            [
                new ScaleIncome(-50, new Period(2026, 5)),
                new EndEntry(rent.Id, new Period(2026, 8)),
                new ChangeDebtRate(d.Id, 1000, new Period(2026, 4)),
                new AddEntry("Kinderwagen", EntryKind.Expense, h.Cat("living").Id, 800, Rhythm.Once, new Period(2026, 6), null, 10, false),
            ],
        };
        var copy = ScenarioApplier.Apply(h.Data, scenario, TestHousehold.Now);
        var sim = new HouseholdEngine(copy, today);

        Assert.NotEqual(baseBalance, sim.Forecasts.Balance(12).Months[^1].EndBalance);
        Assert.True(sim.Debts.Forecast(d.Id).Months < baseDebt);
        Assert.Equal(1500, sim.Month(new Period(2026, 6)).Income);

        // Original unverändert
        Assert.Equal(entriesBefore, h.Data.Entries.Count);
        Assert.Equal(changesBefore, h.Data.EntryChanges.Count);
        Assert.Equal(3000, salary.Amount);
        Assert.Null(rent.EndPeriod);
        Assert.Equal(500, d.MonthlyRate);
        var again = h.Engine(today);
        Assert.Equal(baseBalance, again.Forecasts.Balance(12).Months[^1].EndBalance);

        var (upserts, deletes) = ScenarioApplier.Diff(h.Data, copy);
        Assert.NotEmpty(upserts);
        Assert.Empty(deletes);
    }

    [Fact(DisplayName = "F-075: Szenario ist JSON-serialisierbar (Speichern)")]
    public void ScenarioJson()
    {
        var s = new Scenario { Name = "Test", Changes = [new ScaleIncome(5, new Period(2026, 1)), new PauseEntry(Guid.NewGuid(), new Period(2026, 2), null)] };
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<Scenario>(json)!;
        Assert.IsType<ScaleIncome>(back.Changes[0]);
        Assert.IsType<PauseEntry>(back.Changes[1]);
    }
}

public class CalculatorTests
{
    [Fact(DisplayName = "Kreditrechner: Annuität 10.000 € / 5 % / 36 Monate = 299,71 €")]
    public void Loan()
    {
        var r = FinanceCalculators.Loan(10000, 5, 36);
        Assert.Equal(299.71m, r.MonthlyPayment);
        Assert.Equal(36, r.Schedule.Count);
        Assert.Equal(0, r.Schedule[^1].Balance);
        Assert.Equal(3, r.Years.Count);
        Assert.InRange(r.TotalInterest, 788m, 791m);
    }

    [Fact(DisplayName = "Zinseszinsrechner: ohne Rendite = Summe der Einzahlungen")]
    public void Compound()
    {
        var zero = FinanceCalculators.CompoundInterest(1000, 100, 0, 10);
        Assert.Equal(1000 + 100 * 120, zero.FinalBalance);
        var with = FinanceCalculators.CompoundInterest(0, 100, 6, 30);
        Assert.InRange(with.FinalBalance, 100400m, 100500m);
        Assert.True(with.RealFinalBalance == with.FinalBalance);
        Assert.True(FinanceCalculators.CompoundInterest(0, 100, 6, 30, 2).RealFinalBalance < with.FinalBalance);
    }

    [Fact(DisplayName = "Stundenlohnrechner: 2.000 € netto / 40 h = 11,54 €/h")]
    public void Hourly()
    {
        var r = FinanceCalculators.HourlyWage(2000, 40);
        Assert.Equal(11.54m, r.HourlyWage);
        Assert.InRange(FinanceCalculators.WorkHoursFor(23.08m, r), 1.99m, 2.01m);
    }

    [Fact(DisplayName = "Hypothekenrechner: Darlehen, Rate, Restschuld nach Zinsbindung")]
    public void Mortgage()
    {
        var r = FinanceCalculators.Mortgage(400000, 80000, 10, 3.5m, 2, 10);
        Assert.Equal(40000, r.ClosingCosts);
        Assert.Equal(360000, r.LoanAmount);
        Assert.Equal(1650, r.MonthlyPayment);
        Assert.InRange(r.RemainingAfterFixedPeriod, 270000m, 280000m);
        Assert.NotNull(r.MonthsToPayoff);
        var faster = FinanceCalculators.Mortgage(400000, 80000, 10, 3.5m, 2, 10, 5000);
        Assert.True(faster.MonthsToPayoff < r.MonthsToPayoff);
    }

    [Fact(DisplayName = "Ruhestandsrechner: Lücke, benötigtes Kapital, Sparrate")]
    public void Retirement()
    {
        var r = FinanceCalculators.Retirement(35, 67, 90, 2500, 1500, 10000, 300, 5, 3, 2);
        Assert.Equal(32, r.YearsToRetirement);
        Assert.Equal(1000, r.MonthlyGapToday);
        Assert.True(r.MonthlyGapAtRetirement > 1000);
        Assert.True(r.RequiredCapital > 0);
        Assert.True(r.ProjectedCapital > 10000);
        var noGap = FinanceCalculators.Retirement(35, 67, 90, 1500, 1500, 0, 0, 5, 3, 2);
        Assert.Equal(0, noGap.RequiredCapital);
    }
}

public class DemoAndPerformanceTests
{
    [Fact(DisplayName = "F-142: Demodaten befüllen alle Module und sind vollständig löschbar")]
    public void DemoData()
    {
        var h = new TestHousehold();
        var today = new DateOnly(2026, 10, 1);
        var set = Defaults.DemoData.Create(h.Data, today, TestHousehold.Now, null);
        Assert.NotEmpty(set.Entries);
        Assert.NotEmpty(set.Debts);
        Assert.NotEmpty(set.Payments);
        Assert.NotEmpty(set.Buffers);
        Assert.NotEmpty(set.Transactions);
        Assert.NotEmpty(set.Snapshots);
        Assert.NotEmpty(set.Checkpoints);
        Assert.NotEmpty(set.Months);
        Assert.All(set.All, e => Assert.True(e.IsDemo));
        // Ist-Werte variabler Positionen schwanken höchstens ±20 % um den Plan (kein Ganzzahl-Überlauf)
        var byId = set.Entries.ToDictionary(e => e.Id);
        Assert.All(set.Months.Where(m => m.ActualAmount is not null), m =>
        {
            var planned = byId[m.EntryId].Amount;
            Assert.InRange(m.ActualAmount!.Value, planned * 0.8m - 0.01m, planned * 1.2m + 0.01m);
        });

        h.Data.Entries.AddRange(set.Entries);
        h.Data.EntryChanges.AddRange(set.Changes);
        h.Data.EntryMonths.AddRange(set.Months);
        h.Data.Debts.AddRange(set.Debts);
        h.Data.DebtPayments.AddRange(set.Payments);
        h.Data.Buffers.AddRange(set.Buffers);
        h.Data.BufferTransactions.AddRange(set.Transactions);
        h.Data.Snapshots.AddRange(set.Snapshots);
        h.Data.Checkpoints.AddRange(set.Checkpoints);
        var eng = h.Engine(today);
        Assert.Empty(eng.PendingBookings);
        var current = eng.CurrentPeriod;
        Assert.True(eng.Month(current).Income > 0);
        Assert.True(eng.Debts.TotalBalance > 0);
        Assert.True(eng.Buffers.TotalBalance > 0);
        Assert.True(eng.Cashflow.HasAnchor);
        Assert.NotEmpty(eng.Statistics.TopExpenses(current, current));
        Assert.Equal(100.0m, eng.Statistics.FiftyThirtyTwenty(current.AddMonths(-5), current).Groups.Sum(g => g.ActualPercent));
        Assert.True(eng.Forecasts.DebtFree().Possible);
        Assert.Equal(12, eng.Statistics.NetWorth(current, 12).Count(p => p.NetWorth is not null));
        _ = eng.Ampel.Evaluate(current);
        _ = eng.Forecasts.YearEnd(current.Year);
        _ = eng.Forecasts.Warnings();
        Assert.True(eng.Month(current.AddMonths(-2)).IsClosed);
        Assert.False(eng.Month(current.AddMonths(-1)).IsClosed);

        // Mit einem Klick löschen
        foreach (var e in h.Data.AllEntities().Where(e => e.IsDemo)) e.IsDeleted = true;
        var cleared = h.Engine(today);
        Assert.Empty(cleared.Index.Entries);
        Assert.Equal(0, cleared.Month(current).Unverplant);
    }

    [Fact(DisplayName = "NFR Performance: 10.000 Positionen – Monat + 24-Monats-Statistik < 300 ms")]
    public void TenThousandEntries()
    {
        var h = new TestHousehold(firstPeriod: new Period(2024, 1));
        var cats = new[] { "housing", "mobility", "living", "guiltfree", "giving", "investing" };
        for (var i = 0; i < 10000; i++)
        {
            var e = new BudgetEntry
            {
                Id = Guid.NewGuid(), HouseholdId = h.Hid, Name = $"Position {i}", Kind = i % 10 == 0 ? EntryKind.Income : EntryKind.Expense,
                CategoryId = h.Cat(i % 10 == 0 ? "salary" : cats[i % cats.Length]).Id, Amount = 10 + i % 90,
                Rhythm = i % 7 == 0 ? Rhythm.Quarterly : Rhythm.Monthly, StartPeriod = new Period(2024, 1 + i % 12), DebitDay = 1 + i % 28,
                IsFixed = i % 2 == 0, SortIndex = i,
            };
            h.Data.Entries.Add(e);
        }
        var sw = Stopwatch.StartNew();
        var eng = h.Engine(new DateOnly(2026, 1, 15));
        var month = eng.Month(new Period(2026, 1));
        var series = eng.Statistics.IncomeExpenseSeries(new Period(2026, 1), 24);
        var cf = eng.Cashflow.ForPeriod(new Period(2026, 1));
        sw.Stop();
        Assert.Equal(10000, eng.Index.Entries.Count);
        Assert.True(month.Lines.Count > 8000);
        Assert.Equal(24, series.Count);
        Assert.NotEmpty(cf.Days);
        Assert.True(sw.ElapsedMilliseconds < 300, $"Dauer {sw.ElapsedMilliseconds} ms");
    }
}
