using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

public enum BookingTarget
{
    Debt = 0,
    Buffer = 1,
}

/// <summary>Fällige automatische Buchung einer verknüpften Position (Kreditrate, Zusatztilgung, Sparplan).</summary>
public sealed record DueBooking(Guid EntryId, Period Period, DateOnly Date, decimal Amount, BookingTarget Target, Guid TargetId, bool IsExtra);

/// <summary>
/// Ermittelt Raten und Sparplan-Zuweisungen, die bis zu einem Stichtag fällig, aber noch nicht als
/// <see cref="DebtPayment"/>/<see cref="BufferTransaction"/> verbucht sind (ENTSCHEIDUNGEN §4.7/§4.8).
/// Der Server verbucht sie; der Client rechnet sie bis dahin als Projektion mit (Offline-Konsistenz).
/// </summary>
public static class BookingPlanner
{
    public static IReadOnlyList<DueBooking> Pending(HouseholdEngine engine, DateOnly upTo)
    {
        var ix = engine.Index;
        var cal = engine.Calendar;
        var lastPeriod = cal.PeriodOf(upTo);
        var result = new List<DueBooking>();

        foreach (var e in ix.Entries)
        {
            if (e.Kind == EntryKind.Expense && e.DebtId is { } debtId && e.DebtRole != DebtRole.None)
            {
                if (!ix.DebtById.TryGetValue(debtId, out var debt) || !debt.AutoBook) continue;
                var from = Period.Max(e.StartPeriod, cal.PeriodOf(debt.StartDate));
                foreach (var p in EntrySchedule.DuePeriods(e, from, lastPeriod))
                {
                    var date = cal.DueDate(p, e.DebitDay ?? debt.DebitDay);
                    // Nur Fälligkeiten nach Erfassung der Restschuld – frühere sind in ihr bereits enthalten.
                    if (date <= debt.StartDate || date > upTo) continue;
                    if (ix.PaymentSources.Contains((e.Id, p.Index))) continue;
                    var amount = EffectiveAmount(engine, e, p);
                    if (amount <= 0) continue;
                    result.Add(new DueBooking(e.Id, p, date, amount, BookingTarget.Debt, debtId, e.DebtRole == DebtRole.Extra));
                }
            }
            else if (e.Kind == EntryKind.Allocation && e.BufferId is { } bufferId)
            {
                if (!ix.BufferById.ContainsKey(bufferId)) continue;
                // Sparpläne buchen ab der Periode ihrer Anlage (keine rückwirkenden Buchungen).
                var anchor = cal.Start(cal.PeriodOf(DateOnly.FromDateTime(e.CreatedAt.UtcDateTime)));
                var from = Period.Max(e.StartPeriod, cal.PeriodOf(anchor));
                foreach (var p in EntrySchedule.DuePeriods(e, from, lastPeriod))
                {
                    var date = cal.DueDate(p, e.DebitDay);
                    if (date < anchor || date > upTo) continue;
                    if (ix.TransactionSources.Contains((e.Id, p.Index))) continue;
                    var amount = EffectiveAmount(engine, e, p);
                    if (amount <= 0) continue;
                    result.Add(new DueBooking(e.Id, p, date, amount, BookingTarget.Buffer, bufferId, false));
                }
            }
        }

        result.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.IsExtra.CompareTo(b.IsExtra));
        return result;
    }

    /// <summary>Effektiver Betrag (Ist ?? Plan); in abgeschlossenen Perioden aus dem Snapshot.</summary>
    public static decimal EffectiveAmount(HouseholdEngine engine, BudgetEntry e, Period p)
    {
        if (engine.Index.SnapshotByPeriod.TryGetValue(p.Index, out var snap))
        {
            foreach (var l in snap.Lines)
                if (l.EntryId == e.Id) return l.Actual;
            return 0;
        }
        return EntrySchedule.State(engine.Index, e, p).Effective;
    }
}
