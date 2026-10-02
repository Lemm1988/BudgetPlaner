using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>Zustand einer Position in einer Periode (ENTSCHEIDUNGEN §4.2).</summary>
public readonly record struct EntryMonthState(
    bool IsDue,
    decimal Nominal,
    decimal Planned,
    decimal? Actual,
    bool IsPaused,
    bool IsSkipped,
    bool IsDone,
    BudgetEntryMonth? Month)
{
    /// <summary>Ist, falls erfasst, sonst Plan (D-02: ohne Eintrag gilt Ist = Soll).</summary>
    public decimal Effective => Actual ?? Planned;

    public bool IsInactive => IsPaused || IsSkipped;
}

/// <summary>Fälligkeit und Beträge wiederkehrender Positionen (F-010, F-011, F-013).</summary>
public static class EntrySchedule
{
    /// <summary>Ist die Position in der Periode fällig (Rhythmus, Start, Ende)?</summary>
    public static bool IsDue(BudgetEntry e, Period p)
    {
        if (p.Index < e.StartPeriod.Index) return false;
        if (e.EndPeriod is { } end && p.Index > end.Index) return false;
        var interval = e.Rhythm.IntervalMonths();
        if (interval == 0) return p.Index == e.StartPeriod.Index;
        return (p.Index - e.StartPeriod.Index) % interval == 0;
    }

    /// <summary>Ist die Position in der Periode aktiv (zwischen Start und Ende), unabhängig von der Fälligkeit?</summary>
    public static bool IsActive(BudgetEntry e, Period p) =>
        p.Index >= e.StartPeriod.Index && (e.EndPeriod is not { } end || p.Index <= end.Index);

    /// <summary>Betrag laut Grundbetrag/Änderungen „ab Monat“ und Pausenstatus.</summary>
    public static (decimal Amount, bool Paused) ScheduledAmount(HouseholdIndex ix, BudgetEntry e, Period p) =>
        ScheduledAmount(e, ix.ChangesByEntry.GetValueOrDefault(e.Id), p);

    public static (decimal Amount, bool Paused) ScheduledAmount(BudgetEntry e, BudgetEntryChange[]? changes, Period p)
    {
        if (changes is not null)
        {
            int lo = 0, hi = changes.Length - 1, found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (changes[mid].FromPeriod.Index <= p.Index) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found >= 0) return (changes[found].Amount, changes[found].IsPaused);
        }
        return (e.Amount, false);
    }

    public static EntryMonthState State(HouseholdIndex ix, BudgetEntry e, Period p) =>
        ix.EntryInfoById.TryGetValue(e.Id, out var info) ? State(info, p) : State(e, null, null, p);

    public static EntryMonthState State(EntryInfo info, Period p) => State(info.Entry, info.Changes, info.Months, p);

    public static EntryMonthState State(BudgetEntry e, BudgetEntryChange[]? changes, Dictionary<int, BudgetEntryMonth>? months, Period p)
    {
        BudgetEntryMonth? month = null;
        months?.TryGetValue(p.Index, out month);
        if (!IsDue(e, p)) return new EntryMonthState(false, 0, 0, null, false, false, false, month);
        var (amount, paused) = ScheduledAmount(e, changes, p);
        var nominal = month?.PlannedAmount ?? amount;
        var skipped = month?.IsSkipped ?? false;
        var planned = paused || skipped ? 0 : nominal;
        return new EntryMonthState(true, nominal, planned, month?.ActualAmount, paused, skipped, month?.IsDone ?? false, month);
    }

    /// <summary>Monatlicher Anteil für die geglättete Statistik (I-02): Betrag ÷ Intervall in jedem aktiven Monat.</summary>
    public static decimal SmoothedAmount(HouseholdIndex ix, BudgetEntry e, Period p)
    {
        var interval = e.Rhythm.IntervalMonths();
        if (interval <= 1) return State(ix, e, p).Effective;
        if (!IsActive(e, p)) return 0;
        var (amount, paused) = ScheduledAmount(ix, e, p);
        return paused ? 0 : amount / interval;
    }

    public static IEnumerable<Period> DuePeriods(BudgetEntry e, Period from, Period to)
    {
        var start = Period.Max(from, e.StartPeriod);
        var end = e.EndPeriod is { } ep ? Period.Min(to, ep) : to;
        if (start > end) yield break;
        var interval = e.Rhythm.IntervalMonths();
        if (interval == 0)
        {
            if (e.StartPeriod >= start && e.StartPeriod <= end) yield return e.StartPeriod;
            yield break;
        }
        var offset = (start.Index - e.StartPeriod.Index) % interval;
        var first = offset == 0 ? start : start.AddMonths(interval - offset);
        for (var p = first; p <= end; p = p.AddMonths(interval)) yield return p;
    }

    /// <summary>Nächste Fälligkeit ab einer Periode (für Anzeige „nächste Abbuchung“).</summary>
    public static Period? NextDue(BudgetEntry e, Period from)
    {
        foreach (var p in DuePeriods(e, from, from.AddMonths(Math.Max(12, e.Rhythm.IntervalMonths()))))
            return p;
        return null;
    }
}
