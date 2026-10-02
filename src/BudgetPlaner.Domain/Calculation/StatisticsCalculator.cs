using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

public sealed record SegmentLine(Guid EntryId, string Name, Guid CategoryId, decimal Amount);

public sealed record CategorySegment(Guid CategoryId, decimal Amount, decimal Percent, IReadOnlyList<SegmentLine> Lines);

/// <summary>F-060: Segmentsumme = Gesamtausgaben, leere Kategorien ausgeblendet.</summary>
public sealed record CategoryBreakdown(decimal Total, IReadOnlyList<CategorySegment> Segments);

/// <summary>F-061: <c>null</c> = Lücke (kein 0 €).</summary>
public sealed record SeriesPoint(Period Period, decimal? Income, decimal? Expenses, decimal? Surplus, decimal? Unverplant);

public sealed record GroupShare(BudgetGroup Group, decimal Amount, decimal ActualPercent, decimal? TargetPercent, decimal? DeviationPoints, bool ShowHint, decimal? ShareOfIncome);

/// <summary>F-062: Ist-Anteile summieren exakt zu 100 %.</summary>
public sealed record FiftyThirtyTwentyResult(Period From, Period To, decimal Base, decimal Income, IReadOnlyList<GroupShare> Groups)
{
    public bool HasData => Base > 0;
}

/// <summary>F-063: Sparquote je Monat und 3-Monats-Durchschnitt (ab dem 3. Monat).</summary>
public sealed record SavingsRatePoint(Period Period, decimal? Rate, decimal? Average3);

public sealed record TopItem(Guid EntryId, string Name, Guid CategoryId, decimal Amount, decimal Percent);

public sealed record YearRow(string Key, Guid? CategoryId, EntryKind? Kind, int Level, decimal?[] Months, decimal YearTotal,
    decimal? PreviousYearTotal, decimal? DeviationPercent, bool Highlight);

/// <summary>F-065: Jahressumme = Summe der 12 Monate; ohne Vorjahr kein Prozentwert.</summary>
/// <remarks>Vorjahresvergleich nur über Monate mit Daten in beiden Jahren (<see cref="ComparedMonths"/>), sonst entstehen falsche Prozentwerte.</remarks>
public sealed record YearMatrix(int Year, IReadOnlyList<Period> Periods, bool HasPreviousYear, IReadOnlyList<YearRow> Rows, IReadOnlyList<int> ComparedMonths);

public sealed record PlanActualRow(Guid CategoryId, EntryKind Kind, int Level, decimal Planned, decimal Actual, decimal Deviation,
    decimal? DeviationPercent, AmpelStatus Status, int LinesWithoutActual);

/// <summary>F-066: Summe der Abweichungen = Gesamtabweichung.</summary>
public sealed record PlanActualResult(Period From, Period To, IReadOnlyList<PlanActualRow> Rows,
    decimal IncomeDeviation, decimal ExpenseDeviation, decimal AllocationDeviation, int LinesWithoutActual);

/// <summary>F-067: Fixkosten ÷ Einnahmen.</summary>
public sealed record FixedCostResult(Period Period, decimal FixedExpenses, decimal Income, decimal? RatioPercent, bool Warning);

/// <summary>F-068: Polster − Restschulden.</summary>
public sealed record NetWorthPoint(Period Period, decimal? Buffers, decimal? Debts, decimal? NetWorth);

/// <summary>Auswertungen und Statistiken (F-060–F-068).</summary>
public sealed class StatisticsCalculator
{
    private readonly HouseholdEngine _engine;

    public StatisticsCalculator(HouseholdEngine engine) => _engine = engine;

    private readonly record struct StatLine(Guid EntryId, string Name, EntryKind Kind, Guid CategoryId, Guid? RootId,
        BudgetGroup Group, decimal Amount, decimal Planned, Rhythm Rhythm, bool IsFixed);

    private List<StatLine> Lines(Period p, bool smoothed)
    {
        var list = new List<StatLine>();
        if (!smoothed)
        {
            foreach (var l in _engine.Month(p).Lines)
                list.Add(new StatLine(l.EntryId, l.Name, l.Kind, l.CategoryId, l.RootCategoryId, l.Group, l.Effective, l.Planned, l.Rhythm, l.IsFixed));
            return list;
        }
        var ix = _engine.Index;
        foreach (var e in ix.Entries)
        {
            if (!EntrySchedule.IsActive(e, p)) continue;
            if (e.Rhythm.IntervalMonths() <= 1 && !EntrySchedule.IsDue(e, p)) continue;
            var amount = EntrySchedule.SmoothedAmount(ix, e, p);
            if (amount == 0) continue;
            var group = e.Kind == EntryKind.Allocation ? BudgetGroup.Savings : ix.GroupOf(e.CategoryId);
            list.Add(new StatLine(e.Id, e.Name, e.Kind, e.CategoryId, ix.RootOf(e.CategoryId)?.Id, group, amount, amount, e.Rhythm, e.IsFixed));
        }
        return list;
    }

    private IEnumerable<Period> DataPeriods(Period from, Period to, bool smoothed = false)
    {
        foreach (var p in Period.Range(from, to))
            if (HasData(p, smoothed)) yield return p;
    }

    /// <summary>Periode mit Daten (sonst Lücke, F-061); geglättet zählen auch anteilige Positionen.</summary>
    private bool HasData(Period p, bool smoothed) =>
        !_engine.IsGap(p) || (smoothed && p >= _engine.FirstPeriod && Lines(p, true).Count > 0);

    /// <summary>Ausgaben nach Kategorie (F-060).</summary>
    public CategoryBreakdown ExpensesByCategory(Period from, Period to, bool byRoot = true, bool smoothed = false)
    {
        var bySegment = new Dictionary<Guid, Dictionary<Guid, (string Name, Guid Cat, decimal Amount)>>();
        decimal total = 0;
        foreach (var p in DataPeriods(from, to, smoothed))
        {
            foreach (var l in Lines(p, smoothed))
            {
                if (l.Kind != EntryKind.Expense || l.Amount <= 0) continue;
                var key = byRoot ? l.RootId ?? l.CategoryId : l.CategoryId;
                if (!bySegment.TryGetValue(key, out var entries)) bySegment[key] = entries = [];
                var cur = entries.GetValueOrDefault(l.EntryId, (l.Name, l.CategoryId, 0m));
                entries[l.EntryId] = (l.Name, l.CategoryId, cur.Item3 + l.Amount);
                total += l.Amount;
            }
        }
        var segments = bySegment
            .Select(kv =>
            {
                var amount = kv.Value.Values.Sum(v => v.Amount);
                var lines = kv.Value.Select(x => new SegmentLine(x.Key, x.Value.Name, x.Value.Cat, x.Value.Amount))
                    .OrderByDescending(x => x.Amount).ToList();
                return new CategorySegment(kv.Key, amount, total == 0 ? 0 : amount / total * 100m, lines);
            })
            .Where(s => s.Amount > 0)
            .OrderByDescending(s => s.Amount)
            .ToList();
        return new CategoryBreakdown(total, segments);
    }

    /// <summary>Einnahmen-/Ausgaben-Verlauf über 3/6/12/24 Monate (F-061).</summary>
    public IReadOnlyList<SeriesPoint> IncomeExpenseSeries(Period end, int months, bool smoothed = false)
    {
        var result = new List<SeriesPoint>(months);
        for (var p = end.AddMonths(-(months - 1)); p <= end; p = p.Next)
        {
            if (!HasData(p, smoothed)) { result.Add(new SeriesPoint(p, null, null, null, null)); continue; }
            if (!smoothed)
            {
                var t = _engine.Totals(p);
                result.Add(new SeriesPoint(p, t.Income, t.Expenses, t.Surplus, t.Unverplant));
                continue;
            }
            var lines = Lines(p, true);
            var inc = lines.Where(l => l.Kind == EntryKind.Income).Sum(l => l.Amount);
            var exp = lines.Where(l => l.Kind == EntryKind.Expense).Sum(l => l.Amount);
            result.Add(new SeriesPoint(p, inc, exp, inc - exp, null));
        }
        return result;
    }

    /// <summary>50/30/20-Analyse Ist vs. Soll (F-062) für einen Monat oder als Durchschnitt über mehrere Monate.</summary>
    public FiftyThirtyTwentyResult FiftyThirtyTwenty(Period from, Period to, bool smoothed = false)
    {
        decimal needs = 0, wants = 0, savingsExp = 0, none = 0, alloc = 0, withdraw = 0, income = 0;
        foreach (var p in DataPeriods(from, to, smoothed))
        {
            var t = _engine.Totals(p);
            alloc += t.ManualAllocations;
            withdraw += t.Withdrawals;
            if (!smoothed)
            {
                income += t.Income;
                alloc += t.EntryAllocations;
                needs += t.Needs;
                wants += t.Wants;
                savingsExp += t.SavingsExpenses;
                none += t.UngroupedExpenses;
                continue;
            }
            foreach (var l in Lines(p, smoothed))
            {
                switch (l.Kind)
                {
                    case EntryKind.Income: income += l.Amount; break;
                    case EntryKind.Allocation: alloc += l.Amount; break;
                    case EntryKind.Expense:
                        switch (l.Group)
                        {
                            case BudgetGroup.Needs: needs += l.Amount; break;
                            case BudgetGroup.Wants: wants += l.Amount; break;
                            case BudgetGroup.Savings: savingsExp += l.Amount; break;
                            default: none += l.Amount; break;
                        }
                        break;
                }
            }
        }
        var savings = Math.Max(0, savingsExp + alloc - withdraw);
        var rules = _engine.Rules;
        var groups = new List<(BudgetGroup G, decimal Amount, decimal? Target)>
        {
            (BudgetGroup.Needs, Math.Max(0, needs), rules.TargetNeedsPercent),
            (BudgetGroup.Wants, Math.Max(0, wants), rules.TargetWantsPercent),
            (BudgetGroup.Savings, savings, rules.TargetSavingsPercent),
        };
        if (none > 0) groups.Add((BudgetGroup.None, none, null));
        var baseSum = groups.Sum(g => g.Amount);
        var percents = MoneyMath.RoundToHundred(groups.Select(g => g.Amount).ToList(), 1);
        var shares = groups.Select((g, i) =>
        {
            var actual = baseSum == 0 ? 0 : percents[i];
            decimal? dev = g.Target is { } t && baseSum > 0 ? actual - t : null;
            return new GroupShare(g.G, g.Amount, actual, g.Target, dev,
                dev is { } d && Math.Abs(d) > rules.FiftyThirtyTwentyHintPoints,
                income > 0 ? MoneyMath.Round1(g.Amount / income * 100m) : null);
        }).ToList();
        return new FiftyThirtyTwentyResult(from, to, baseSum, income, shares);
    }

    /// <summary>Sparquote mit gleitendem 3-Monats-Durchschnitt (F-063).</summary>
    public IReadOnlyList<SavingsRatePoint> SavingsRate(Period end, int months)
    {
        var start = end.AddMonths(-(months - 1));
        var rates = new List<(Period P, decimal? Rate)>();
        for (var p = start.AddMonths(-2); p <= end; p = p.Next) rates.Add((p, RateOf(p)));
        var result = new List<SavingsRatePoint>(months);
        for (var i = 2; i < rates.Count; i++)
        {
            var window = rates.Skip(i - 2).Take(3).ToList();
            decimal? avg = window.All(w => w.Rate is not null) ? MoneyMath.Round1(window.Average(w => w.Rate!.Value)) : null;
            result.Add(new SavingsRatePoint(rates[i].P, rates[i].Rate, rates[i].Rate is null ? null : avg));
        }
        return result;
    }

    /// <summary>Sparquote einer Periode in Prozent (kaufmännisch auf 1 Stelle gerundet) oder <c>null</c>.</summary>
    public decimal? RateOf(Period p)
    {
        if (_engine.IsGap(p)) return null;
        var t = _engine.Totals(p);
        if (t.Income <= 0) return null;
        return MoneyMath.Round1(t.SavingsContribution / t.Income * 100m);
    }

    /// <summary>Top-Ausgabenpositionen absteigend (F-064).</summary>
    public IReadOnlyList<TopItem> TopExpenses(Period from, Period to, int take = 10)
    {
        var sums = new Dictionary<Guid, (string Name, Guid Cat, decimal Amount)>();
        decimal total = 0;
        foreach (var p in DataPeriods(from, to))
            foreach (var l in _engine.Month(p).Lines)
            {
                if (l.Kind != EntryKind.Expense || l.Effective <= 0) continue;
                var cur = sums.GetValueOrDefault(l.EntryId, (l.Name, l.CategoryId, 0m));
                sums[l.EntryId] = (l.Name, l.CategoryId, cur.Item3 + l.Effective);
                total += l.Effective;
            }
        return sums
            .Select(kv => new TopItem(kv.Key, kv.Value.Name, kv.Value.Cat, kv.Value.Amount, total == 0 ? 0 : kv.Value.Amount / total * 100m))
            .OrderByDescending(x => x.Amount).ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .Take(take <= 0 ? int.MaxValue : take)
            .ToList();
    }

    /// <summary>Jahresübersicht mit Vorjahresvergleich (F-065).</summary>
    public YearMatrix YearOverview(int year, bool smoothed = false)
    {
        var periods = Enumerable.Range(1, 12).Select(m => new Period(year, m)).ToList();
        var prevPeriods = Enumerable.Range(1, 12).Select(m => new Period(year - 1, m)).ToList();
        // Vergleichbar sind nur Monate mit Daten in beiden Jahren (z. B. Haushalt erst seit November des Vorjahres).
        var comparable = periods.Select((p, i) => HasData(p, smoothed) && HasData(prevPeriods[i], smoothed)).ToArray();
        var hasPrev = comparable.Any(x => x);
        var highlight = _engine.Rules.YearDeviationHighlightPercent;
        var ix = _engine.Index;

        decimal?[] Totals(IReadOnlyList<Period> ps, Func<MonthResult, List<StatLine>, decimal> f) =>
            ps.Select(p => HasData(p, smoothed) ? f(_engine.Month(p), Lines(p, smoothed)) : (decimal?)null).ToArray();

        YearRow Row(string key, Guid? cat, EntryKind? kind, int level, Func<MonthResult, List<StatLine>, decimal> f)
        {
            var months = Totals(periods, f);
            var total = months.Sum(x => x ?? 0);
            decimal? prev = null, dev = null;
            if (hasPrev)
            {
                var prevMonths = Totals(prevPeriods, f);
                decimal current = 0, previous = 0;
                for (var i = 0; i < 12; i++)
                {
                    if (!comparable[i]) continue;
                    current += months[i] ?? 0;
                    previous += prevMonths[i] ?? 0;
                }
                prev = previous;
                dev = previous != 0 ? (current - previous) / Math.Abs(previous) * 100m : null;
            }
            return new YearRow(key, cat, kind, level, months, total, prev, dev, dev is { } d && Math.Abs(d) > highlight);
        }

        var rows = new List<YearRow>
        {
            Row("income", null, EntryKind.Income, 0, (_, ls) => ls.Where(l => l.Kind == EntryKind.Income).Sum(l => l.Amount)),
            Row("expenses", null, EntryKind.Expense, 0, (_, ls) => ls.Where(l => l.Kind == EntryKind.Expense).Sum(l => l.Amount)),
            Row("allocations", null, EntryKind.Allocation, 0, (m, ls) => ls.Where(l => l.Kind == EntryKind.Allocation).Sum(l => l.Amount) + m.ManualAllocations),
            Row("surplus", null, null, 0, (_, ls) => ls.Where(l => l.Kind == EntryKind.Income).Sum(l => l.Amount) - ls.Where(l => l.Kind == EntryKind.Expense).Sum(l => l.Amount)),
        };
        if (!smoothed) rows.Add(Row("unverplant", null, null, 0, (m, _) => m.Unverplant));

        foreach (var root in ix.Categories.Where(c => c.ParentId is null).OrderBy(c => c.Type).ThenBy(c => c.SortIndex))
        {
            var kind = root.Type == CategoryType.Income ? EntryKind.Income : EntryKind.Expense;
            var rootRow = Row("category", root.Id, kind, 1, (_, ls) => ls.Where(l => (l.RootId ?? l.CategoryId) == root.Id && l.Kind != EntryKind.Allocation).Sum(l => l.Amount));
            if (rootRow.YearTotal == 0 && (rootRow.PreviousYearTotal ?? 0) == 0) continue;
            rows.Add(rootRow);
            foreach (var child in ix.Categories.Where(c => c.ParentId == root.Id).OrderBy(c => c.SortIndex))
            {
                var r = Row("category", child.Id, kind, 2, (_, ls) => ls.Where(l => l.CategoryId == child.Id && l.Kind != EntryKind.Allocation).Sum(l => l.Amount));
                if (r.YearTotal != 0 || (r.PreviousYearTotal ?? 0) != 0) rows.Add(r);
            }
        }
        return new YearMatrix(year, periods, hasPrev, rows, Enumerable.Range(1, 12).Where(m => comparable[m - 1]).ToList());
    }

    /// <summary>Soll/Ist-Abweichung je Kategorie (F-066).</summary>
    public PlanActualResult PlanVsActual(Period from, Period to)
    {
        var ix = _engine.Index;
        var leaf = new Dictionary<(Guid, EntryKind), (decimal Plan, decimal Act, int NoActual)>();
        var noActualTotal = 0;
        foreach (var p in DataPeriods(from, to))
            foreach (var l in _engine.Month(p).Lines)
            {
                var key = (l.CategoryId, l.Kind);
                var cur = leaf.GetValueOrDefault(key);
                var missing = l.Actual is null && !l.FromSnapshot ? 1 : 0;
                noActualTotal += missing;
                leaf[key] = (cur.Plan + l.Planned, cur.Act + l.Effective, cur.NoActual + missing);
            }

        var rows = new List<PlanActualRow>();
        foreach (var kind in new[] { EntryKind.Income, EntryKind.Expense, EntryKind.Allocation })
        {
            var items = leaf.Where(kv => kv.Key.Item2 == kind).ToList();
            foreach (var rootGroup in items.GroupBy(kv => ix.RootOf(kv.Key.Item1)?.Id ?? kv.Key.Item1))
            {
                var plan = rootGroup.Sum(x => x.Value.Plan);
                var act = rootGroup.Sum(x => x.Value.Act);
                rows.Add(MakeRow(rootGroup.Key, kind, 0, plan, act, rootGroup.Sum(x => x.Value.NoActual)));
                foreach (var kv in rootGroup.Where(x => x.Key.Item1 != rootGroup.Key).OrderByDescending(x => x.Value.Plan))
                    rows.Add(MakeRow(kv.Key.Item1, kind, 1, kv.Value.Plan, kv.Value.Act, kv.Value.NoActual));
            }
        }

        decimal Dev(EntryKind k) => leaf.Where(kv => kv.Key.Item2 == k).Sum(kv => kv.Value.Act - kv.Value.Plan);
        return new PlanActualResult(from, to, rows, Dev(EntryKind.Income), Dev(EntryKind.Expense), Dev(EntryKind.Allocation), noActualTotal);

        static PlanActualRow MakeRow(Guid cat, EntryKind kind, int level, decimal plan, decimal act, int noActual)
        {
            var dev = act - plan;
            decimal? pct = plan != 0 ? dev / plan * 100m : null;
            var status = kind == EntryKind.Income
                ? dev < 0 ? AmpelStatus.Red : AmpelStatus.Green
                : dev > 0 ? AmpelStatus.Red : AmpelStatus.Green;
            return new PlanActualRow(cat, kind, level, plan, act, dev, pct, status, noActual);
        }
    }

    /// <summary>Fixkostenquote (F-067).</summary>
    public FixedCostResult FixedCostRatio(Period p)
    {
        var m = _engine.Totals(p);
        decimal? ratio = m.Income > 0 ? m.FixedExpenses / m.Income * 100m : null;
        var warn = ratio is { } r ? r > _engine.Rules.FixedCostWarnPercent : m.FixedExpenses > 0;
        return new FixedCostResult(p, m.FixedExpenses, m.Income, ratio, warn);
    }

    /// <summary>Vermögensverlauf: Polster minus Restschulden zum Periodenende (F-068).</summary>
    public IReadOnlyList<NetWorthPoint> NetWorth(Period end, int months = 12)
    {
        var result = new List<NetWorthPoint>(months);
        var last = Period.Min(end, _engine.CurrentPeriod);
        for (var p = last.AddMonths(-(months - 1)); p <= last; p = p.Next)
        {
            if (p < _engine.FirstPeriod) { result.Add(new NetWorthPoint(p, null, null, null)); continue; }
            var date = p == _engine.CurrentPeriod ? _engine.Today : _engine.Calendar.End(p);
            var buffers = _engine.Index.Buffers.Sum(b => _engine.Buffers.BalanceAt(b.Id, date));
            var debts = _engine.Index.Debts.Sum(d => _engine.Debts.BalanceAt(d.Id, date) ?? 0);
            result.Add(new NetWorthPoint(p, buffers, debts, buffers - debts));
        }
        return result;
    }
}
