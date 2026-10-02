using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>Zuweisung/Entnahme in der Polster-Historie (F-042).</summary>
public sealed class BufferTxView
{
    public Guid? TransactionId { get; init; }
    public DateOnly Date { get; init; }

    /// <summary>Vorzeichenbehaftet: Zuweisung +, Entnahme −.</summary>
    public decimal SignedAmount { get; init; }

    public BufferTransactionType Type { get; init; }
    public string? Reason { get; init; }
    public bool AffectsBudget { get; init; }
    public bool IsProjected { get; init; }
    public Guid? SourceEntryId { get; init; }
    public decimal BalanceAfter { get; init; }
}

public sealed class BufferForecast
{
    public bool Possible { get; init; }
    public bool AlreadyReached { get; init; }

    /// <summary>Ø Nettozuweisung (3 Perioden inkl. aktueller Planung).</summary>
    public decimal AverageMonthly { get; init; }

    public int PeriodsInAverage { get; init; }
    public int? Months { get; init; }
    public Period? GoalPeriod { get; init; }
    public DateOnly? GoalDate { get; init; }
}

public sealed class BufferStatus
{
    public required BufferEntry Buffer { get; init; }
    public decimal Balance { get; init; }
    public decimal? Target { get; init; }

    /// <summary>Stand ÷ Ziel × 100 (F-041); <c>null</c> ohne Ziel.</summary>
    public decimal? ProgressPercent { get; init; }

    public bool GoalReached { get; init; }
    public required IReadOnlyList<BufferTxView> History { get; init; }

    /// <summary>Geplante Zuweisung der laufenden Periode.</summary>
    public decimal PlannedThisPeriod { get; init; }

    public required BufferForecast Forecast { get; init; }

    /// <summary>Benötigte Monatsrate bis zum Wunschdatum.</summary>
    public decimal? RequiredMonthlyForTargetDate { get; init; }
}

/// <summary>Polster: Stand, Fortschritt, Historie und Sparziel-Prognose (F-040–F-043, F-072).</summary>
public sealed class BufferCalculator
{
    private readonly HouseholdEngine _engine;
    private readonly Dictionary<Guid, BufferStatus> _status = [];

    public BufferCalculator(HouseholdEngine engine) => _engine = engine;

    public IReadOnlyList<BufferStatus> All() => _engine.Index.Buffers.Select(b => Status(b.Id)).ToList();

    public decimal TotalBalance => _engine.Index.Buffers.Sum(b => Status(b.Id).Balance);

    public BufferStatus Status(Guid bufferId)
    {
        if (_status.TryGetValue(bufferId, out var cached)) return cached;
        var ix = _engine.Index;
        var buffer = ix.BufferById[bufferId];
        var items = new List<(DateOnly Date, decimal Signed, BufferTransactionType Type, string? Reason, bool Affects, bool Projected, Guid? TxId, Guid? Source, DateTimeOffset Created)>();
        foreach (var t in ix.TransactionsByBuffer.GetValueOrDefault(bufferId) ?? [])
            items.Add((t.Date, t.Type == BufferTransactionType.Allocation ? t.Amount : -t.Amount, t.Type, t.Reason, t.AffectsBudget, false, t.Id, t.SourceEntryId, t.CreatedAt));
        foreach (var b in _engine.PendingBookings)
            if (b.Target == BookingTarget.Buffer && b.TargetId == bufferId)
                items.Add((b.Date, b.Amount, BufferTransactionType.Allocation, null, true, true, null, b.EntryId, DateTimeOffset.MaxValue));
        items.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.Created.CompareTo(b.Created));

        decimal balance = 0;
        var history = new List<BufferTxView>(items.Count);
        foreach (var i in items)
        {
            balance += i.Signed;
            history.Add(new BufferTxView
            {
                TransactionId = i.TxId, Date = i.Date, SignedAmount = i.Signed, Type = i.Type, Reason = i.Reason,
                AffectsBudget = i.Affects, IsProjected = i.Projected, SourceEntryId = i.Source, BalanceAfter = balance,
            });
        }

        var target = buffer.TargetAmount is > 0 ? buffer.TargetAmount : null;
        var progress = target is { } t2 ? balance / t2 * 100m : (decimal?)null;
        var planned = PlannedAllocation(bufferId, _engine.CurrentPeriod);
        var forecast = ComputeForecast(buffer, balance, target, history);

        decimal? required = null;
        if (target is { } tg && buffer.TargetDate is { } td && balance < tg)
        {
            var monthsLeft = Math.Max(1, Period.MonthsBetween(_engine.CurrentPeriod, _engine.Calendar.PeriodOf(td)) + 1);
            required = MoneyMath.Round2((tg - balance) / monthsLeft);
        }

        var status = new BufferStatus
        {
            Buffer = buffer,
            Balance = MoneyMath.Round2(balance),
            Target = target,
            ProgressPercent = progress,
            GoalReached = target is { } t3 && balance >= t3,
            History = history,
            PlannedThisPeriod = planned,
            Forecast = forecast,
            RequiredMonthlyForTargetDate = required,
        };
        _status[bufferId] = status;
        return status;
    }

    /// <summary>Stand am Ende eines Tages (F-068).</summary>
    public decimal BalanceAt(Guid bufferId, DateOnly date)
    {
        decimal balance = 0;
        foreach (var h in Status(bufferId).History)
        {
            if (h.Date > date) break;
            balance = h.BalanceAfter;
        }
        return balance;
    }

    /// <summary>Geplante Sparplan-Zuweisungen einer Periode.</summary>
    public decimal PlannedAllocation(Guid bufferId, Period p)
    {
        var entries = _engine.Index.EntriesByBuffer.GetValueOrDefault(bufferId) ?? [];
        return entries.Where(e => EntrySchedule.IsDue(e, p)).Sum(e => EntrySchedule.State(_engine.Index, e, p).Effective);
    }

    /// <summary>
    /// Sparziel-Prognose (F-043/F-072, I-04): Ø Nettozuweisung der letzten 3 Perioden inkl. der aktuellen
    /// (mit noch geplanten Zuweisungen) → Monate = ⌈(Ziel − Stand) ÷ Ø⌉.
    /// </summary>
    private BufferForecast ComputeForecast(BufferEntry buffer, decimal balance, decimal? target, List<BufferTxView> history)
    {
        var cal = _engine.Calendar;
        var current = _engine.CurrentPeriod;
        var created = cal.PeriodOf(DateOnly.FromDateTime(buffer.CreatedAt.UtcDateTime));
        var periods = Enumerable.Range(0, 3).Select(i => current.AddMonths(-i)).Where(p => p >= created).ToList();
        if (periods.Count == 0) periods.Add(current);

        decimal sum = 0;
        foreach (var p in periods)
        {
            foreach (var h in history)
            {
                if (cal.PeriodOf(h.Date) != p) continue;
                // Anfangsbestände (ohne Budgetwirkung) sind keine laufenden Zuweisungen.
                if (h.Type == BufferTransactionType.Allocation && !h.AffectsBudget) continue;
                sum += h.SignedAmount;
            }
        }

        // Noch nicht fällige Planbeträge der aktuellen Periode → Ratenänderungen wirken sofort.
        var pendingThisPeriod = false;
        foreach (var e in _engine.Index.EntriesByBuffer.GetValueOrDefault(buffer.Id) ?? [])
        {
            if (!EntrySchedule.IsDue(e, current)) continue;
            if (_engine.Index.TransactionSources.Contains((e.Id, current.Index))) continue;
            if (history.Any(h => h.IsProjected && h.SourceEntryId == e.Id && cal.PeriodOf(h.Date) == current)) continue;
            var amount = EntrySchedule.State(_engine.Index, e, current).Effective;
            if (amount <= 0) continue;
            sum += amount;
            pendingThisPeriod = true;
        }

        var avg = sum / periods.Count;
        if (target is not { } t)
            return new BufferForecast { Possible = false, AverageMonthly = avg, PeriodsInAverage = periods.Count };
        if (balance >= t)
            return new BufferForecast { Possible = true, AlreadyReached = true, Months = 0, AverageMonthly = avg, PeriodsInAverage = periods.Count };
        if (avg <= 0)
            return new BufferForecast { Possible = false, AverageMonthly = avg, PeriodsInAverage = periods.Count };

        var months = (int)Math.Ceiling((t - balance) / avg);
        var goal = current.AddMonths(pendingThisPeriod ? months - 1 : months);
        return new BufferForecast
        {
            Possible = true,
            AverageMonthly = avg,
            PeriodsInAverage = periods.Count,
            Months = months,
            GoalPeriod = goal,
            GoalDate = cal.End(goal),
        };
    }
}
