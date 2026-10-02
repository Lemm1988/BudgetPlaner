using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>
/// Ein Ampelwert inkl. Rechenweg für den Tooltip (F-051 „Tooltip erklärt Berechnung“).
/// <see cref="Value"/> ist die bewertete Kennzahl, <see cref="Unit"/> „%“, „€“ oder „Monate“.
/// </summary>
public sealed record AmpelItem(string Key, AmpelStatus Status, decimal? Value, string Unit, decimal? GreenLimit, decimal? YellowLimit,
    decimal? Numerator = null, decimal? Denominator = null, Guid? CategoryId = null);

public sealed record AmpelReport(Period Period, AmpelItem Budget, AmpelItem Cashflow, AmpelItem Debts, AmpelItem Buffers,
    IReadOnlyList<AmpelItem> Groups, IReadOnlyList<AmpelItem> Categories)
{
    public AmpelStatus Overall => new[] { Budget, Cashflow, Debts, Buffers }.Max(i => i.Status);
}

/// <summary>Ampelsystem je Bereich (F-051, ENTSCHEIDUNGEN §4.5). Dashboard und Modul-Tabs nutzen dieselbe Bewertung.</summary>
public sealed class AmpelEvaluator
{
    private readonly HouseholdEngine _engine;
    private readonly Dictionary<int, AmpelReport> _cache = [];

    public AmpelEvaluator(HouseholdEngine engine) => _engine = engine;

    private AmpelRules Rules => _engine.Rules;

    public AmpelReport Evaluate(Period p)
    {
        if (_cache.TryGetValue(p.Index, out var cached)) return cached;
        var report = new AmpelReport(p, Budget(p), Cashflow(p), Debts(p), Buffers(p), Groups(p), Categories(p));
        _cache[p.Index] = report;
        return report;
    }

    /// <summary>Unverplant ≥ 0 → grün; &lt; −10 % der Einnahmen → rot; dazwischen gelb.</summary>
    public AmpelItem Budget(Period p)
    {
        var m = _engine.Month(p);
        var u = m.Unverplant;
        var redLimit = m.Income * Rules.UnverplantRedBelowPercent / 100m;
        var status = u >= 0 ? AmpelStatus.Green : u < redLimit || m.Income <= 0 ? AmpelStatus.Red : AmpelStatus.Yellow;
        return new AmpelItem("budget", status, u, "€", 0, redLimit, m.Income + m.CarryIn + m.Withdrawals, m.Expenses + m.Allocations);
    }

    /// <summary>Tiefster Tagessaldo: &lt; 0 rot, &lt; 10 % der Einnahmen gelb; ohne Kontostand-Abgleich neutral.</summary>
    public AmpelItem Cashflow(Period p)
    {
        var cf = _engine.Cashflow.ForPeriod(p);
        var income = _engine.Month(p).Income;
        var greenMin = income * Rules.CashflowGreenMinPercent / 100m;
        if (!cf.HasAnchor) return new AmpelItem("cashflow", AmpelStatus.Neutral, cf.MinBalance, "€", greenMin, 0);
        var status = cf.MinBalance < 0 ? AmpelStatus.Red : cf.MinBalance < greenMin ? AmpelStatus.Yellow : AmpelStatus.Green;
        return new AmpelItem("cashflow", status, cf.MinBalance, "€", greenMin, 0);
    }

    /// <summary>Schuldendienstquote (Raten ÷ Einnahmen).</summary>
    public AmpelItem Debts(Period p)
    {
        var m = _engine.Month(p);
        var statuses = _engine.Index.Debts.Select(d => _engine.Debts.Status(d.Id)).Where(s => !s.IsPaidOff).ToList();
        var rates = statuses.Sum(s => _engine.Debts.RegularRate(s.Debt, p));
        if (statuses.Count == 0)
            return new AmpelItem("debts", AmpelStatus.Green, 0, "%", Rules.DebtServiceGreenMaxPercent, Rules.DebtServiceYellowMaxPercent, 0, m.Income);
        if (m.Income <= 0)
            return new AmpelItem("debts", rates > 0 ? AmpelStatus.Red : AmpelStatus.Neutral, null, "%", Rules.DebtServiceGreenMaxPercent, Rules.DebtServiceYellowMaxPercent, rates, m.Income);
        var ratio = rates / m.Income * 100m;
        var status = statuses.Any(s => s.RateBelowInterest) || ratio > Rules.DebtServiceYellowMaxPercent ? AmpelStatus.Red
            : ratio > Rules.DebtServiceGreenMaxPercent ? AmpelStatus.Yellow : AmpelStatus.Green;
        return new AmpelItem("debts", status, ratio, "%", Rules.DebtServiceGreenMaxPercent, Rules.DebtServiceYellowMaxPercent, rates, m.Income);
    }

    /// <summary>Reichweite der Polster (Notgroschen) in Monatsausgaben.</summary>
    public AmpelItem Buffers(Period p)
    {
        var m = _engine.Month(p);
        var emergency = _engine.Index.Buffers.Where(b => b.IsEmergencyFund).ToList();
        var relevant = emergency.Count > 0 ? emergency : _engine.Index.Buffers;
        var total = relevant.Sum(b => _engine.Buffers.Status(b.Id).Balance);
        if (m.Expenses <= 0) return new AmpelItem("buffers", AmpelStatus.Neutral, null, "months", Rules.BufferGreenMonths, Rules.BufferYellowMonths, total, m.Expenses);
        var months = total / m.Expenses;
        var status = months >= Rules.BufferGreenMonths ? AmpelStatus.Green : months >= Rules.BufferYellowMonths ? AmpelStatus.Yellow : AmpelStatus.Red;
        return new AmpelItem("buffers", status, months, "months", Rules.BufferGreenMonths, Rules.BufferYellowMonths, total, m.Expenses);
    }

    /// <summary>Bedarf/Wunsch-Anteil und Sparquote (50/30/20, BudgetHeld „Spar-/Investquote“).</summary>
    public IReadOnlyList<AmpelItem> Groups(Period p)
    {
        var m = _engine.Month(p);
        var list = new List<AmpelItem>();
        if (m.Income <= 0)
        {
            list.Add(new AmpelItem("needs", AmpelStatus.Neutral, null, "%", Rules.TargetNeedsPercent, Rules.NeedsYellowMaxPercent));
            list.Add(new AmpelItem("wants", AmpelStatus.Neutral, null, "%", Rules.TargetWantsPercent, Rules.WantsYellowMaxPercent));
            list.Add(new AmpelItem("savings", AmpelStatus.Neutral, null, "%", Rules.SavingsRateGreenMinPercent, Rules.SavingsRateYellowMinPercent));
            return list;
        }
        var needs = m.ExpensesInGroup(BudgetGroup.Needs) / m.Income * 100m;
        var wants = m.ExpensesInGroup(BudgetGroup.Wants) / m.Income * 100m;
        var savings = m.SavingsContribution / m.Income * 100m;
        list.Add(new AmpelItem("needs", Max(needs, Rules.TargetNeedsPercent, Rules.NeedsYellowMaxPercent), needs, "%", Rules.TargetNeedsPercent, Rules.NeedsYellowMaxPercent, m.ExpensesInGroup(BudgetGroup.Needs), m.Income));
        list.Add(new AmpelItem("wants", Max(wants, Rules.TargetWantsPercent, Rules.WantsYellowMaxPercent), wants, "%", Rules.TargetWantsPercent, Rules.WantsYellowMaxPercent, m.ExpensesInGroup(BudgetGroup.Wants), m.Income));
        var sStatus = savings >= Rules.SavingsRateGreenMinPercent ? AmpelStatus.Green : savings >= Rules.SavingsRateYellowMinPercent ? AmpelStatus.Yellow : AmpelStatus.Red;
        list.Add(new AmpelItem("savings", sStatus, savings, "%", Rules.SavingsRateGreenMinPercent, Rules.SavingsRateYellowMinPercent, m.SavingsContribution, m.Income));

        var invest = _engine.Index.Categories.FirstOrDefault(c => c.DefaultKey == "investing");
        if (invest is not null)
        {
            var amount = m.Lines.Where(l => l.Kind == EntryKind.Expense && IsInSubtree(l.CategoryId, invest.Id)).Sum(l => l.Effective);
            var pct = amount / m.Income * 100m;
            list.Add(new AmpelItem("investing", pct >= 10 ? AmpelStatus.Green : pct > 0 ? AmpelStatus.Yellow : AmpelStatus.Neutral, pct, "%", 10, 0, amount, m.Income, invest.Id));
        }
        return list;
    }

    /// <summary>Kategorie-Ampeln nach Richtwerten (BudgetHeld 1.3).</summary>
    public IReadOnlyList<AmpelItem> Categories(Period p)
    {
        var m = _engine.Month(p);
        var list = new List<AmpelItem>();
        foreach (var limit in Rules.CategoryLimits)
        {
            var cat = _engine.Index.Categories.FirstOrDefault(c => c.DefaultKey == limit.CategoryKey);
            if (cat is null) continue;
            var amount = m.Lines.Where(l => l.Kind == EntryKind.Expense && IsInSubtree(l.CategoryId, cat.Id)).Sum(l => l.Effective);
            if (m.Income <= 0)
            {
                list.Add(new AmpelItem($"cat:{limit.CategoryKey}", AmpelStatus.Neutral, null, "%", limit.GreenMaxPercent, limit.YellowMaxPercent, amount, 0, cat.Id));
                continue;
            }
            var pct = amount / m.Income * 100m;
            list.Add(new AmpelItem($"cat:{limit.CategoryKey}", Max(pct, limit.GreenMaxPercent, limit.YellowMaxPercent), pct, "%",
                limit.GreenMaxPercent, limit.YellowMaxPercent, amount, m.Income, cat.Id));
        }
        return list;
    }

    /// <summary>Ampel für eine beliebige Kategorie (für Budget-Ansicht), falls ein Richtwert existiert.</summary>
    public AmpelItem? ForCategory(Period p, Guid categoryId)
    {
        var cat = _engine.Index.CategoryById.GetValueOrDefault(categoryId);
        if (cat?.DefaultKey is null) return null;
        return Categories(p).FirstOrDefault(i => i.CategoryId == categoryId);
    }

    private static AmpelStatus Max(decimal value, decimal green, decimal yellow) =>
        value <= green ? AmpelStatus.Green : value <= yellow ? AmpelStatus.Yellow : AmpelStatus.Red;

    private bool IsInSubtree(Guid categoryId, Guid rootId)
    {
        var ix = _engine.Index;
        var current = categoryId;
        for (var depth = 0; depth < 16; depth++)
        {
            if (current == rootId) return true;
            if (!ix.CategoryById.TryGetValue(current, out var c) || c.ParentId is not { } parent) return false;
            current = parent;
        }
        return false;
    }
}
