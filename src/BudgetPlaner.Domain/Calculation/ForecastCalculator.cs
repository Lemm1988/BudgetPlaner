using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>Monatsendstand der Kontostand-Prognose (F-070); Engpass = Tagestiefststand &lt; 0 (B-08).</summary>
public sealed record BalanceForecastPoint(Period Period, decimal StartBalance, decimal Inflows, decimal Outflows, decimal EndBalance,
    decimal MinBalance, DateOnly MinBalanceDate, bool IsBottleneck);

public sealed record DailyBalance(DateOnly Date, decimal Balance, bool IsForecast);

public sealed record BalanceForecast(bool HasAnchor, decimal BalanceToday, IReadOnlyList<BalanceForecastPoint> Months, IReadOnlyList<DailyBalance> Daily);

/// <summary>Frühwarnung/Warnhinweis (F-073, F-081).</summary>
public sealed record Warning(NotificationType Type, AmpelStatus Severity, string Key, decimal? Amount1 = null, decimal? Amount2 = null,
    string? Subject = null, Guid? SubjectId = null, DateOnly? Date = null);

public sealed record YearEndFigure(decimal ActualToDate, decimal Projected, decimal RegularAverage, decimal NonRegular);

/// <summary>Jahresend-Prognose (F-074): Ø regelmäßige Beträge × 12 + nicht-monatliche Positionen exakt.</summary>
public sealed record YearEndForecast(int Year, int ElapsedPeriods, YearEndFigure Income, YearEndFigure Expenses, YearEndFigure Allocations,
    decimal ProjectedSurplus, decimal? ProjectedSavingsRate, decimal? ProjectedYearEndBalance);

public sealed record DebtFreeSummary(bool Possible, Period? PayoffPeriod, int? Months, decimal TotalInterest,
    StrategyResult? Snowball, StrategyResult? Avalanche, StrategyResult? Separate);

/// <summary>Prognosen und Vorhersagen (F-070–F-074).</summary>
public sealed class ForecastCalculator
{
    private readonly HouseholdEngine _engine;

    public ForecastCalculator(HouseholdEngine engine) => _engine = engine;

    /// <summary>Kontostand-Prognose für 1–12 (bis 24) Monate (F-070).</summary>
    public BalanceForecast Balance(int months = 12)
    {
        months = Math.Clamp(months, 1, 24);
        var cf = _engine.Cashflow;
        var points = new List<BalanceForecastPoint>();
        var daily = new List<DailyBalance>();
        for (var p = _engine.CurrentPeriod; p <= _engine.CurrentPeriod.AddMonths(months); p = p.Next)
        {
            var r = cf.ForPeriod(p);
            points.Add(new BalanceForecastPoint(p, r.StartBalance, r.TotalIn, r.TotalOut, r.EndBalance, r.MinBalance, r.MinBalanceDate, r.MinBalance < 0));
            foreach (var d in r.Days) daily.Add(new DailyBalance(d.Date, d.Balance, d.Date > _engine.Today));
        }
        return new BalanceForecast(cf.HasAnchor, cf.BalanceToday, points, daily);
    }

    /// <summary>Schuldenfreiheit gesamt inkl. Strategievergleich (F-071).</summary>
    public DebtFreeSummary DebtFree()
    {
        var debts = _engine.Debts;
        var combined = debts.Combined();
        var open = _engine.Index.Debts.Select(d => debts.Status(d.Id)).Where(s => !s.IsPaidOff && s.Balance > 0).ToList();
        if (open.Count == 0) return new DebtFreeSummary(true, null, 0, 0, null, null, null);

        var p = _engine.CurrentPeriod;
        var strategyDebts = open.Select(s => new StrategyDebt(s.Debt.Name, s.Balance, debts.RegularRate(s.Debt, p),
            s.Debt.InterestRate ?? 0, s.Debt.Id)).ToList();
        var extra = open.Sum(s => (_engine.Index.EntriesByDebt.GetValueOrDefault(s.Debt.Id) ?? [])
            .Where(e => e.DebtRole == DebtRole.Extra && EntrySchedule.IsDue(e, p))
            .Sum(e => EntrySchedule.State(_engine.Index, e, p).Planned));
        return new DebtFreeSummary(combined.Possible, combined.PayoffPeriod, combined.Months, combined.TotalInterest,
            DebtStrategy.Simulate(strategyDebts, extra, PayoffStrategy.Snowball),
            DebtStrategy.Simulate(strategyDebts, extra, PayoffStrategy.Avalanche),
            DebtStrategy.Simulate(strategyDebts, 0, PayoffStrategy.Minimum));
    }

    /// <summary>Frühwarnungen und Warnhinweise für die laufende Periode (F-073, F-081).</summary>
    public IReadOnlyList<Warning> Warnings()
    {
        var list = new List<Warning>();
        var p = _engine.CurrentPeriod;
        var month = _engine.Month(p);
        var cf = _engine.Cashflow;
        var today = _engine.Today;
        var (start, end) = _engine.Calendar.Range(p);
        var ampel = _engine.Ampel.Budget(p);

        // Budget überplant (Unverplant < 0)
        if (month.Unverplant < 0)
            list.Add(new Warning(NotificationType.BudgetOverplanned, ampel.Status == AmpelStatus.Red ? AmpelStatus.Red : AmpelStatus.Yellow,
                $"overplanned:{p}", month.Unverplant));

        // F-073: Restausgaben > verfügbares Geld (Kontostand + Resteinnahmen), I-05
        var flows = cf.Flows(p);
        var remainingIn = flows.Where(f => f.Date > today && f.Amount > 0).Sum(f => f.Amount);
        var remainingOut = -flows.Where(f => f.Date > today && f.Amount < 0).Sum(f => f.Amount);
        var available = cf.HasAnchor ? cf.BalanceToday : cf.FlowsBetween(start, today);
        if (today <= end && remainingOut > available + remainingIn)
            list.Add(new Warning(NotificationType.BudgetEarlyWarning, AmpelStatus.Red, $"early:{p}", remainingOut, available + remainingIn));

        // Kategorie-Überschreitung (Ist > Soll)
        foreach (var g in month.Lines.Where(l => l.Kind == EntryKind.Expense).GroupBy(l => l.CategoryId))
        {
            var plan = g.Sum(l => l.Planned);
            var act = g.Sum(l => l.Effective);
            if (act > plan && g.Any(l => l.Actual is not null))
                list.Add(new Warning(NotificationType.CategoryOverrun, AmpelStatus.Yellow, $"overrun:{p}:{g.Key}", act, plan, null, g.Key));
        }

        // Kontostand-Prognose: Minus in den nächsten 30 Tagen
        if (cf.HasAnchor)
        {
            var horizon = today.AddDays(30);
            DateOnly? firstNegative = null;
            decimal min = decimal.MaxValue;
            for (var q = p; _engine.Calendar.Start(q) <= horizon; q = q.Next)
                foreach (var d in cf.ForPeriod(q).Days)
                {
                    if (d.Date <= today || d.Date > horizon) continue;
                    if (d.Balance < min) min = d.Balance;
                    if (d.Balance < 0 && firstNegative is null) firstNegative = d.Date;
                }
            if (firstNegative is { } fn)
                list.Add(new Warning(NotificationType.NegativeBalanceForecast, AmpelStatus.Red, $"negbal:{fn:yyyy-MM-dd}", min, null, null, null, fn));
        }

        // Fixkostenquote > 60 % (F-067)
        var fixedCost = _engine.Statistics.FixedCostRatio(p);
        if (fixedCost.Warning && fixedCost.FixedExpenses > 0)
            list.Add(new Warning(NotificationType.FixedCostRatioHigh, AmpelStatus.Yellow, $"fixed:{p}", fixedCost.RatioPercent, _engine.Rules.FixedCostWarnPercent));

        // Schulden: Rate deckt Zinsen nicht / fällige Rate ohne automatische Buchung
        foreach (var debt in _engine.Index.Debts)
        {
            var s = _engine.Debts.Status(debt.Id);
            if (s.RateBelowInterest)
                list.Add(new Warning(NotificationType.DebtRateDue, AmpelStatus.Red, $"debtinterest:{debt.Id}", s.CurrentMonthlyPayment, s.NextMonthInterest, debt.Name, debt.Id));
            if (debt.AutoBook || s.IsPaidOff) continue;
            foreach (var e in _engine.Index.EntriesByDebt.GetValueOrDefault(debt.Id) ?? [])
            {
                if (e.DebtRole == DebtRole.None || !EntrySchedule.IsDue(e, p)) continue;
                var due = _engine.Calendar.DueDate(p, e.DebitDay ?? debt.DebitDay);
                if (due > today || due <= debt.StartDate || _engine.Index.PaymentSources.Contains((e.Id, p.Index))) continue;
                list.Add(new Warning(NotificationType.DebtRateDue, AmpelStatus.Yellow, $"ratedue:{e.Id}:{p}",
                    EntrySchedule.State(_engine.Index, e, p).Effective, null, debt.Name, debt.Id, due));
            }
        }
        return list;
    }

    /// <summary>Jahresend-Prognose (F-074).</summary>
    public YearEndForecast YearEnd(int year)
    {
        var periods = Enumerable.Range(1, 12).Select(m => new Period(year, m)).ToList();
        var elapsed = periods.Where(p => p <= _engine.CurrentPeriod && p >= _engine.FirstPeriod).ToList();

        YearEndFigure Figure(EntryKind kind, bool includeManual)
        {
            decimal regularElapsed = 0, nonRegular = 0, actualToDate = 0;
            foreach (var p in periods)
            {
                var m = _engine.Month(p);
                var isElapsed = elapsed.Contains(p);
                foreach (var l in m.Lines.Where(l => l.Kind == kind))
                {
                    if (l.Rhythm == Rhythm.Monthly) { if (isElapsed) regularElapsed += l.Effective; }
                    else nonRegular += l.Effective;
                    if (isElapsed) actualToDate += l.Effective;
                }
                if (includeManual && isElapsed)
                {
                    nonRegular += m.ManualAllocations;
                    actualToDate += m.ManualAllocations;
                }
            }
            decimal avg;
            if (elapsed.Count > 0) avg = regularElapsed / elapsed.Count;
            else
            {
                var planned = periods.Sum(p => _engine.Month(p).Lines.Where(l => l.Kind == kind && l.Rhythm == Rhythm.Monthly).Sum(l => l.Effective));
                avg = planned / 12m;
            }
            return new YearEndFigure(actualToDate, MoneyMath.Round2(avg * 12m + nonRegular), MoneyMath.Round2(avg), nonRegular);
        }

        var income = Figure(EntryKind.Income, false);
        var expenses = Figure(EntryKind.Expense, false);
        var allocations = Figure(EntryKind.Allocation, true);
        var surplus = income.Projected - expenses.Projected;
        decimal? savingsRate = income.Projected > 0 ? MoneyMath.Round1(allocations.Projected / income.Projected * 100m) : null;
        decimal? endBalance = null;
        var last = periods[^1];
        if (last >= _engine.CurrentPeriod && _engine.Cashflow.HasAnchor)
            endBalance = _engine.Cashflow.BalanceAtEndOfDay(_engine.Calendar.End(last));
        return new YearEndForecast(year, elapsed.Count, income, expenses, allocations, surplus, savingsRate, endBalance);
    }
}
