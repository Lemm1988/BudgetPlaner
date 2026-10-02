using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Tests;

public class BudgetTests
{
    private static readonly Period Jan = new(2026, 1);
    private static readonly DateOnly Today = new(2026, 1, 10);

    [Fact(DisplayName = "F-014: 0 € Einnahmen/Ausgaben → Unverplant exakt 0,00 €")]
    public void EmptyIsZero()
    {
        var eng = new TestHousehold().Engine(Today);
        Assert.Equal(0.00m, eng.Month(Jan).Unverplant);
    }

    [Fact(DisplayName = "F-014: Neue 50-€-Ausgabe verringert Unverplant sofort um 50 €")]
    public void NewExpenseReducesUnverplant()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        var before = h.Engine(Today).Month(Jan).Unverplant;
        h.Expense("Neu", 50);
        var after = h.Engine(Today).Month(Jan).Unverplant;
        Assert.Equal(before - 50m, after);
    }

    [Fact(DisplayName = "F-014: Polster-Zuweisung verringert Unverplant exakt um den Betrag")]
    public void AllocationReducesUnverplant()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        var b = h.Buffer("Notgroschen", 5000);
        var before = h.Engine(Today).Month(Jan).Unverplant;
        h.Allocation(b, 150);
        Assert.Equal(before - 150, h.Engine(Today).Month(Jan).Unverplant);
        h.Tx(b, new DateOnly(2026, 1, 12), 99.99m);
        Assert.Equal(before - 249.99m, h.Engine(Today).Month(Jan).Unverplant);
    }

    [Fact(DisplayName = "F-014: Unverplant = Einnahmen − Ausgaben − Polster-Zuweisungen; F-050: Überschuss = Einnahmen − Ausgaben")]
    public void Formula()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 3000);
        h.Expense("Miete", 1200, "housing");
        var b = h.Buffer("Urlaub");
        h.Allocation(b, 300);
        var m = h.Engine(Today).Month(Jan);
        Assert.Equal(1800, m.Surplus);
        Assert.Equal(1500, m.Unverplant);
    }

    [Fact(DisplayName = "F-042/I-16: Entnahme „ins Budget“ erhöht Unverplant, „direkt ausgegeben“ nicht")]
    public void WithdrawalEffect()
    {
        var h = new TestHousehold();
        var b = h.Buffer("Reparatur");
        h.Tx(b, new DateOnly(2025, 12, 1), 1000, affectsBudget: false);
        h.Tx(b, new DateOnly(2026, 1, 5), 200, BufferTransactionType.Withdrawal, true, "Werkstatt");
        h.Tx(b, new DateOnly(2026, 1, 6), 100, BufferTransactionType.Withdrawal, false, "Bar bezahlt");
        Assert.Equal(200, h.Engine(Today).Month(Jan).Unverplant);
    }

    [Fact(DisplayName = "F-010: Löschen entfernt Position sofort aus allen Berechnungen (auch abgeschlossene Monate)")]
    public void DeleteRemovesEverywhere()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        var e = h.Expense("Abo", 20);
        var eng = h.Engine(new DateOnly(2026, 3, 10));
        h.Data.Snapshots.Add(BudgetCalculator.CreateSnapshot(eng, Jan, 0, null, null, TestHousehold.Now, null));
        Assert.Equal(1980, h.Engine(Today).Month(Jan).Unverplant);
        e.IsDeleted = true;
        var after = h.Engine(Today);
        Assert.Equal(2000, after.Month(Jan).Unverplant);
        Assert.Equal(2000, after.Month(new Period(2026, 2)).Unverplant);
        Assert.True(after.Month(Jan).IsClosed);
    }

    [Fact(DisplayName = "D-02: Ist-Betrag ersetzt Soll in den Summen, ohne Eintrag gilt Ist = Soll")]
    public void ActualOverridesPlan()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        var food = h.Expense("Lebensmittel", 600);
        h.Expense("Miete", 900, "housing");
        h.Month(food, Jan, actual: 640, done: true);
        var m = h.Engine(Today).Month(Jan);
        Assert.Equal(1540, m.Expenses);
        Assert.Equal(1500, m.PlannedExpenses);
        Assert.Equal(460, m.Unverplant);
        Assert.Equal(500, m.PlannedUnverplant);
        Assert.True(m.Lines.Single(l => l.EntryId == food.Id).IsDone);
    }

    [Fact(DisplayName = "I-01: Monatsabschluss friert Werte ein und überträgt den Rest")]
    public void MonthCloseCarryOver()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        var e = h.Expense("Miete", 1500, "housing");
        var eng = h.Engine(new DateOnly(2026, 2, 3));
        var snap = BudgetCalculator.CreateSnapshot(eng, Jan, 500, null, 1234, TestHousehold.Now, null);
        h.Data.Snapshots.Add(snap);
        // Änderung „ab Januar“ wirkt nicht mehr auf den abgeschlossenen Monat
        e.Amount = 1600;
        var after = h.Engine(new DateOnly(2026, 2, 3));
        Assert.Equal(1500, after.Month(Jan).Expenses);
        Assert.Equal(500, after.Month(Jan).Unverplant);
        Assert.Equal(500, after.Month(new Period(2026, 2)).CarryIn);
        Assert.Equal(900, after.Month(new Period(2026, 2)).Unverplant);
        Assert.Throws<InvalidOperationException>(() => BudgetCalculator.CreateSnapshot(after, Jan, 0, null, null, TestHousehold.Now, null));
    }

    [Theory(DisplayName = "F-051: Ampel Unverplant ≥ 0 grün, < −10 % der Einnahmen rot, dazwischen gelb")]
    [InlineData(1000, AmpelStatus.Green)]
    [InlineData(2000, AmpelStatus.Green)]
    [InlineData(2100, AmpelStatus.Yellow)]
    [InlineData(2200, AmpelStatus.Yellow)]
    [InlineData(2200.01, AmpelStatus.Red)]
    public void BudgetAmpel(decimal expenses, AmpelStatus expected)
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        h.Expense("Ausgaben", expenses);
        Assert.Equal(expected, h.Engine(Today).Ampel.Budget(Jan).Status);
    }

    [Fact(DisplayName = "F-051: Dashboard und Modul nutzen dieselbe Bewertung")]
    public void AmpelConsistent()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        h.Expense("Ausgaben", 2100);
        var eng = h.Engine(Today);
        Assert.Equal(eng.Ampel.Evaluate(Jan).Budget, eng.Ampel.Budget(Jan));
    }

    [Fact(DisplayName = "Kategorie-Ampel (BudgetHeld 1.3): Wohnen > 40 % rot")]
    public void CategoryAmpel()
    {
        var h = new TestHousehold();
        h.Income("Gehalt", 2000);
        h.Expense("Miete", 900, "housing");
        h.Expense("Bahn", 150, "mobility");
        var items = h.Engine(Today).Ampel.Categories(Jan);
        Assert.Equal(AmpelStatus.Red, items.Single(i => i.Key == "cat:housing").Status);
        Assert.Equal(AmpelStatus.Green, items.Single(i => i.Key == "cat:mobility").Status);
    }
}
