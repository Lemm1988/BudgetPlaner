using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Tests;

public class DebtTests
{
    [Fact(DisplayName = "F-020: Schuld mit Bezeichnung + Restschuld, Gesamtschuldensumme")]
    public void TotalDebt()
    {
        var h = new TestHousehold();
        h.Debt("Auto", 6000);
        h.Debt("Eltern", 1500);
        var eng = h.Engine(new DateOnly(2026, 1, 5));
        Assert.Equal(7500, eng.Debts.TotalBalance);
    }

    [Fact(DisplayName = "F-021: Annuität – korrekter Tilgungsanteil wird abgezogen")]
    public void AnnuitySplit()
    {
        var h = new TestHousehold();
        var d = h.Debt("Kredit", 10000, rate: 6, monthly: 300);
        h.Payment(d, new DateOnly(2026, 1, 15), 300);
        var s = h.Engine(new DateOnly(2026, 1, 20)).Debts.Status(d.Id);
        Assert.Equal(50, s.Payments[0].Interest);
        Assert.Equal(250, s.Payments[0].Principal);
        Assert.Equal(9750, s.Balance);
    }

    [Fact(DisplayName = "F-021: Ohne Zinssatz zählt die gesamte Rate als Tilgung")]
    public void NoInterestFullPrincipal()
    {
        var h = new TestHousehold();
        var d = h.Debt("Eltern", 1000, monthly: 100);
        h.Payment(d, new DateOnly(2026, 1, 15), 100);
        var s = h.Engine(new DateOnly(2026, 1, 20)).Debts.Status(d.Id);
        Assert.Equal(100, s.Payments[0].Principal);
        Assert.Equal(900, s.Balance);
    }

    [Fact(DisplayName = "F-021: Bei 0 € Restschuld automatisch „getilgt“")]
    public void PaidOff()
    {
        var h = new TestHousehold();
        var d = h.Debt("Eltern", 200);
        h.Payment(d, new DateOnly(2026, 1, 15), 150);
        h.Payment(d, new DateOnly(2026, 2, 15), 100);
        var s = h.Engine(new DateOnly(2026, 3, 1)).Debts.Status(d.Id);
        Assert.True(s.IsPaidOff);
        Assert.Equal(0, s.Balance);
        Assert.Equal(new DateOnly(2026, 2, 15), s.PaidOffDate);
        Assert.Equal(50, s.Payments[^1].Overpayment);
    }

    [Fact(DisplayName = "F-022: 50 % Tilgung = halbe Füllung; Gesamtübersicht summiert korrekt")]
    public void Progress()
    {
        var h = new TestHousehold();
        var a = h.Debt("A", 1000);
        var b = h.Debt("B", 3000);
        h.Payment(a, new DateOnly(2026, 1, 2), 500);
        h.Payment(b, new DateOnly(2026, 1, 2), 1500);
        var eng = h.Engine(new DateOnly(2026, 1, 3));
        Assert.Equal(50, eng.Debts.Status(a.Id).ProgressPercent);
        Assert.Equal(50, eng.Debts.TotalProgressPercent);
        Assert.Equal(2000, eng.Debts.TotalBalance);
    }

    [Fact(DisplayName = "F-071: 6.000 € / 500 € ohne Zinsen = 12 Monate")]
    public void DebtFree12Months()
    {
        var h = new TestHousehold();
        var d = h.Debt("Auto", 6000, monthly: 500, day: 15, start: new DateOnly(2026, 1, 1));
        h.Entry("Rate Auto", EntryKind.Expense, 500, "mobility", 15, new Period(2026, 1), debtId: d.Id, role: DebtRole.Rate);
        var f = h.Engine(new DateOnly(2026, 1, 5)).Debts.Forecast(d.Id);
        Assert.True(f.Possible);
        Assert.Equal(12, f.Months);
        Assert.Equal(new Period(2026, 12), f.PayoffPeriod);
    }

    [Fact(DisplayName = "F-043: 200 €/Monat, 2.000 € Restschuld → 10 Monate")]
    public void Forecast10Months()
    {
        var h = new TestHousehold();
        var d = h.Debt("Kredit", 2000, monthly: 200, day: 20, start: new DateOnly(2026, 1, 1));
        Assert.Equal(10, h.Engine(new DateOnly(2026, 1, 5)).Debts.Forecast(d.Id).Months);
    }

    [Fact(DisplayName = "F-043: Prognose aktualisiert sich bei Ratenänderung automatisch")]
    public void ForecastUpdatesOnRateChange()
    {
        var h = new TestHousehold();
        var d = h.Debt("Kredit", 2000, monthly: 200, day: 20, start: new DateOnly(2026, 1, 1));
        var rate = h.Entry("Rate", EntryKind.Expense, 200, "living", 20, new Period(2026, 1), debtId: d.Id, role: DebtRole.Rate);
        Assert.Equal(10, h.Engine(new DateOnly(2026, 1, 5)).Debts.Forecast(d.Id).Months);
        h.Change(rate, new Period(2026, 1), 400);
        Assert.Equal(5, h.Engine(new DateOnly(2026, 1, 5)).Debts.Forecast(d.Id).Months);
    }

    [Fact(DisplayName = "F-071: Annuität mit Zinsen entspricht der geschlossenen Formel")]
    public void AnnuityMatchesFormula()
    {
        var h = new TestHousehold();
        var d = h.Debt("Kredit", 10000, rate: 5, monthly: 300, day: 28, start: new DateOnly(2026, 1, 1));
        var f = h.Engine(new DateOnly(2026, 1, 5)).Debts.Forecast(d.Id);
        Assert.Equal(DebtCalculator.AnnuityMonths(10000, 5, 300), f.Months);
        Assert.True(f.TotalInterest > 0);
        Assert.Equal(0, f.Schedule[^1].Balance);
    }

    [Fact(DisplayName = "F-071: Rate deckt Zinsen nicht → keine Prognose")]
    public void RateBelowInterest()
    {
        var h = new TestHousehold();
        var d = h.Debt("Dispo", 10000, rate: 12, monthly: 50, day: 1, start: new DateOnly(2026, 1, 1));
        var eng = h.Engine(new DateOnly(2026, 1, 1));
        var f = eng.Debts.Forecast(d.Id);
        Assert.False(f.Possible);
        Assert.Equal("rate-below-interest", f.Reason);
        Assert.True(eng.Debts.Status(d.Id).RateBelowInterest);
    }

    [Fact(DisplayName = "F-071: Mehrere Positionen separat berechnet und zusammengeführt")]
    public void Combined()
    {
        var h = new TestHousehold();
        h.Debt("A", 1000, monthly: 500, day: 20, start: new DateOnly(2026, 1, 1));
        h.Debt("B", 1200, monthly: 100, day: 20, start: new DateOnly(2026, 1, 1));
        var (possible, payoff, months, _) = h.Engine(new DateOnly(2026, 1, 5)).Debts.Combined();
        Assert.True(possible);
        Assert.Equal(12, months);
        Assert.Equal(new Period(2026, 12), payoff);
    }

    [Fact(DisplayName = "Automatische Buchung der Rate nach Erfassungsdatum (nicht davor)")]
    public void AutoBooking()
    {
        var h = new TestHousehold();
        var d = h.Debt("Auto", 6000, monthly: 500, day: 15, start: new DateOnly(2026, 1, 20));
        h.Entry("Rate", EntryKind.Expense, 500, "mobility", 15, new Period(2026, 1), debtId: d.Id, role: DebtRole.Rate);
        var eng = h.Engine(new DateOnly(2026, 3, 16));
        var pending = eng.PendingBookings.Where(b => b.TargetId == d.Id).ToList();
        Assert.Equal([new DateOnly(2026, 2, 15), new DateOnly(2026, 3, 15)], pending.Select(p => p.Date));
        Assert.Equal(5000, eng.Debts.Status(d.Id).Balance);

        var result = BookingMaterializer.Materialize(eng, TestHousehold.Now, null);
        Assert.Equal(2, result.Payments.Count);
        h.Data.DebtPayments.AddRange(result.Payments);
        var after = h.Engine(new DateOnly(2026, 3, 16));
        Assert.Empty(after.PendingBookings);
        Assert.Equal(5000, after.Debts.Status(d.Id).Balance);
    }

    [Fact(DisplayName = "B-10: Schneeball ist schneller als nur Mindestraten")]
    public void Snowball()
    {
        var debts = new List<StrategyDebt>
        {
            new("Kreditkarte", 1500, 50, 18),
            new("Auto", 6000, 200, 5),
            new("Eltern", 800, 50, 0),
        };
        var min = DebtStrategy.Simulate(debts, 0, PayoffStrategy.Minimum);
        var snow = DebtStrategy.Simulate(debts, 100, PayoffStrategy.Snowball);
        var aval = DebtStrategy.Simulate(debts, 100, PayoffStrategy.Avalanche);
        Assert.True(snow.Possible && aval.Possible && min.Possible);
        Assert.True(snow.Months < min.Months);
        Assert.Equal("Eltern", snow.Order[0].Name);
        Assert.True(aval.TotalInterest <= snow.TotalInterest);
    }

    [Fact(DisplayName = "Leasing mit Restwert endet mit Schlussrate")]
    public void LeasingResidual()
    {
        var h = new TestHousehold();
        var d = h.Debt("Leasing", 3000, monthly: 250, day: 10, start: new DateOnly(2026, 1, 1), type: DebtType.Leasing);
        d.ResidualValue = 1000;
        h.Entry("Leasingrate", EntryKind.Expense, 250, "mobility", 10, new Period(2026, 1), end: new Period(2026, 8), debtId: d.Id, role: DebtRole.Rate);
        var f = h.Engine(new DateOnly(2026, 1, 5)).Debts.Forecast(d.Id);
        Assert.True(f.Possible);
        Assert.Equal(9, f.Months);
        Assert.Equal(1000, f.Schedule[^1].Extra);
    }
}
