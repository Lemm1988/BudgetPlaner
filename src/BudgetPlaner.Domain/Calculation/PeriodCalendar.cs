using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>
/// Bildet Budgetperioden auf Kalendertage ab (F-101, ENTSCHEIDUNGEN §4.1).
/// Periode (J, M) endet am Tag <c>d − 1</c> des Monats M (bei d = 1 am Monatsletzten) und beginnt am Tag
/// nach dem Ende der Vorperiode. Dadurch entstehen bei einer Änderung des Starttags weder Lücken noch Überlappungen.
/// </summary>
public sealed class PeriodCalendar
{
    private readonly MonthStartRule[] _rules;

    public PeriodCalendar(IEnumerable<MonthStartRule>? rules)
    {
        _rules = (rules ?? []).OrderBy(r => r.FromPeriod.Index).ToArray();
        foreach (var r in _rules)
            if (r.Day is < 1 or > 28) throw new ArgumentOutOfRangeException(nameof(rules), "Monatsstart-Tag muss zwischen 1 und 28 liegen.");
    }

    public static PeriodCalendar Default { get; } = new([]);

    /// <summary>Gültiger Starttag der Periode.</summary>
    public int StartDayFor(Period period)
    {
        if (_rules.Length == 0) return 1;
        var day = _rules[0].Day;
        foreach (var r in _rules)
        {
            if (r.FromPeriod.Index <= period.Index) day = r.Day;
            else break;
        }
        return day;
    }

    public DateOnly End(Period period)
    {
        var d = StartDayFor(period);
        return d == 1
            ? new DateOnly(period.Year, period.Month, period.DaysInCalendarMonth)
            : new DateOnly(period.Year, period.Month, d - 1);
    }

    public DateOnly Start(Period period) => End(period.Previous).AddDays(1);

    public (DateOnly Start, DateOnly End) Range(Period period) => (Start(period), End(period));

    public int DayCount(Period period) => End(period).DayNumber - Start(period).DayNumber + 1;

    /// <summary>Budgetperiode, in die ein Kalenderdatum fällt.</summary>
    public Period PeriodOf(DateOnly date)
    {
        var candidate = Period.OfCalendarDate(date);
        for (var guard = 0; guard < 4; guard++)
        {
            if (date > End(candidate)) candidate = candidate.Next;
            else if (date < Start(candidate)) candidate = candidate.Previous;
            else return candidate;
        }
        return candidate;
    }

    /// <summary>Periodengrenzen + Starttag in einem Schritt (für Schleifen über viele Positionen).</summary>
    public PeriodRange RangeOf(Period period)
    {
        var (start, end) = Range(period);
        return new PeriodRange(period, start, end, StartDayFor(period));
    }

    /// <summary>
    /// Fälligkeitsdatum einer Position mit Abbuchungstag in der Periode (F-013, F-030):
    /// Tage jenseits des Monatsendes fallen auf den letzten Tag des Monats.
    /// </summary>
    public DateOnly DueDate(Period period, int? debitDay) => DueDate(RangeOf(period), debitDay);

    public static DateOnly DueDate(in PeriodRange range, int? debitDay)
    {
        var (period, start, end, d) = range;
        if (debitDay is not { } x) return start;
        x = Math.Clamp(x, 1, 31);
        var calendarMonth = d > 1 && x >= d ? period.Previous : period;
        var day = Math.Min(x, calendarMonth.DaysInCalendarMonth);
        var date = new DateOnly(calendarMonth.Year, calendarMonth.Month, day);
        if (date < start) return start;
        if (date > end) return end;
        return date;
    }

    /// <summary>Neue Regelliste, bei der <paramref name="newDay"/> ab der Periode nach <paramref name="current"/> gilt (F-101 „wirkt nur auf zukünftige Monate“).</summary>
    public static List<MonthStartRule> WithChangedDay(IEnumerable<MonthStartRule> rules, Period current, int newDay)
    {
        if (newDay is < 1 or > 28) throw new ArgumentOutOfRangeException(nameof(newDay));
        var from = current.Next;
        var list = rules.Where(r => r.FromPeriod.Index < from.Index).OrderBy(r => r.FromPeriod.Index).ToList();
        var effective = list.Count == 0 ? 1 : list[^1].Day;
        if (effective != newDay || list.Count == 0) list.Add(new MonthStartRule(from, newDay));
        return list;
    }
}

/// <summary>Grenzen einer Periode inkl. gültigem Starttag.</summary>
public readonly record struct PeriodRange(Period Period, DateOnly Start, DateOnly End, int StartDay);
