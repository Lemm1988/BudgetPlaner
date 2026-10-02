using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>
/// Wandelt fällige automatische Buchungen in echte Zahlungen/Transaktionen um (Server, Demodaten)
/// und hält die Zins-/Tilgungsanteile der Zahlungen konsistent (F-021).
/// </summary>
public static class BookingMaterializer
{
    public sealed record Result(List<DebtPayment> Payments, List<BufferTransaction> Transactions, List<Guid> PaidOffDebts);

    public static Result Materialize(HouseholdEngine engine, DateTimeOffset now, Guid? userId)
    {
        var payments = new List<DebtPayment>();
        var transactions = new List<BufferTransaction>();
        var paidOff = new List<Guid>();
        var hid = engine.Data.Household.Id;

        foreach (var group in engine.PendingBookings.Where(b => b.Target == BookingTarget.Debt).GroupBy(b => b.TargetId))
        {
            var debt = engine.Index.DebtById[group.Key];
            var existing = engine.Index.PaymentsByDebt.GetValueOrDefault(debt.Id) ?? [];
            var inputs = existing.Select(p => new PaymentInput(p.Id, p.Date, p.Amount, p.IsExtra, p.InterestRateApplied, false, CreatedAt: p.CreatedAt)).ToList();
            var wasPaidOff = inputs.Count > 0 && DebtCalculator.Split(debt, inputs)[^1].BalanceAfter <= 0.005m;
            if (wasPaidOff) continue;
            foreach (var b in group.OrderBy(x => x.Date).ThenBy(x => x.IsExtra))
            {
                var balance = inputs.Count == 0 ? debt.OpeningBalance : DebtCalculator.Split(debt, inputs)[^1].BalanceAfter;
                if (balance <= 0.005m) break;
                var p = new DebtPayment
                {
                    Id = Guid.NewGuid(), HouseholdId = hid, CreatedAt = now, UpdatedAt = now, UpdatedBy = userId,
                    DebtId = debt.Id, Date = b.Date, Amount = b.Amount, IsExtra = b.IsExtra, InterestRateApplied = debt.InterestRate,
                    SourceEntryId = b.EntryId, SourcePeriod = b.Period,
                };
                payments.Add(p);
                inputs.Add(new PaymentInput(p.Id, p.Date, p.Amount, p.IsExtra, p.InterestRateApplied, false, CreatedAt: now));
            }
            var final = DebtCalculator.Split(debt, inputs);
            ApplySplits(final, payments);
            if (final.Count > 0 && final[^1].BalanceAfter <= 0.005m) paidOff.Add(debt.Id);
        }

        foreach (var b in engine.PendingBookings.Where(b => b.Target == BookingTarget.Buffer))
        {
            transactions.Add(new BufferTransaction
            {
                Id = Guid.NewGuid(), HouseholdId = hid, CreatedAt = now, UpdatedAt = now, UpdatedBy = userId,
                BufferId = b.TargetId, Date = b.Date, Amount = b.Amount, Type = BufferTransactionType.Allocation,
                AffectsBudget = true, SourceEntryId = b.EntryId, SourcePeriod = b.Period,
            });
        }
        return new Result(payments, transactions, paidOff);
    }

    /// <summary>Berechnet Zins-/Tilgungsanteile aller Zahlungen einer Schuld neu; liefert geänderte Zahlungen.</summary>
    public static List<DebtPayment> RecomputeSplits(DebtEntry debt, IReadOnlyCollection<DebtPayment> payments)
    {
        var active = payments.Where(p => !p.IsDeleted).ToList();
        var splits = DebtCalculator.Split(debt, active.Select(p => new PaymentInput(p.Id, p.Date, p.Amount, p.IsExtra, p.InterestRateApplied, false, CreatedAt: p.CreatedAt)));
        return ApplySplits(splits, active);
    }

    private static List<DebtPayment> ApplySplits(IEnumerable<PaymentView> splits, IEnumerable<DebtPayment> payments)
    {
        var byId = payments.ToDictionary(p => p.Id);
        var changed = new List<DebtPayment>();
        foreach (var s in splits)
        {
            if (s.PaymentId is not { } id || !byId.TryGetValue(id, out var p)) continue;
            if (p.InterestPart == s.Interest && p.PrincipalPart == s.Principal) continue;
            p.InterestPart = s.Interest;
            p.PrincipalPart = s.Principal;
            changed.Add(p);
        }
        return changed;
    }
}
