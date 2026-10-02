namespace BudgetPlaner.Domain;

public enum PayoffStrategy
{
    /// <summary>Jede Schuld nur mit ihrer Mindestrate, frei werdende Raten verfallen.</summary>
    Minimum = 0,
    /// <summary>Schneeball: frei werdendes Geld zuerst auf die kleinste Restschuld.</summary>
    Snowball = 1,
    /// <summary>Lawine: frei werdendes Geld zuerst auf den höchsten Zinssatz.</summary>
    Avalanche = 2,
}

public sealed record StrategyDebt(string Name, decimal Balance, decimal MinPayment, decimal AnnualRatePercent, Guid? Id = null);

public sealed record StrategyPayoff(string Name, Guid? Id, int Month, decimal InterestPaid);

public sealed class StrategyResult
{
    public bool Possible { get; init; }
    public int Months { get; init; }
    public decimal TotalInterest { get; init; }
    public decimal TotalPaid { get; init; }
    public IReadOnlyList<StrategyPayoff> Order { get; init; } = [];

    /// <summary>Gesamte Restschuld am Ende jedes Monats (für Diagramme).</summary>
    public IReadOnlyList<decimal> BalanceTimeline { get; init; } = [];
}

/// <summary>Tilgungsstrategien Schneeball/Lawine (BudgetHeld 1.3 Tilgungsrechner, B-10).</summary>
public static class DebtStrategy
{
    public static StrategyResult Simulate(IReadOnlyList<StrategyDebt> debts, decimal extraPerMonth, PayoffStrategy strategy, int maxMonths = 600)
    {
        var n = debts.Count;
        var balances = debts.Select(d => Math.Max(0, d.Balance)).ToArray();
        var interestPaid = new decimal[n];
        var payoffMonth = new int?[n];
        var budget = debts.Sum(d => d.MinPayment) + Math.Max(0, extraPerMonth);
        decimal totalInterest = 0, totalPaid = 0;
        var timeline = new List<decimal>();

        for (var i = 0; i < n; i++)
            if (balances[i] <= 0) payoffMonth[i] = 0;

        for (var month = 1; month <= maxMonths; month++)
        {
            if (balances.All(b => b <= 0.005m)) break;

            for (var i = 0; i < n; i++)
            {
                if (balances[i] <= 0) continue;
                var interest = MoneyMath.Round2(balances[i] * debts[i].AnnualRatePercent / 1200m);
                balances[i] += interest;
                interestPaid[i] += interest;
                totalInterest += interest;
            }

            var available = strategy == PayoffStrategy.Minimum
                ? debts.Where((_, i) => balances[i] > 0).Sum(d => d.MinPayment) + Math.Max(0, extraPerMonth)
                : budget;

            for (var i = 0; i < n; i++)
            {
                if (balances[i] <= 0) continue;
                var pay = Math.Min(debts[i].MinPayment, balances[i]);
                balances[i] -= pay;
                available -= pay;
                totalPaid += pay;
            }

            if (strategy != PayoffStrategy.Minimum || extraPerMonth > 0)
            {
                var targets = Enumerable.Range(0, n).Where(i => balances[i] > 0);
                targets = strategy == PayoffStrategy.Avalanche
                    ? targets.OrderByDescending(i => debts[i].AnnualRatePercent).ThenBy(i => balances[i])
                    : targets.OrderBy(i => balances[i]).ThenByDescending(i => debts[i].AnnualRatePercent);
                foreach (var i in targets.ToList())
                {
                    if (available <= 0) break;
                    var pay = Math.Min(available, balances[i]);
                    balances[i] -= pay;
                    available -= pay;
                    totalPaid += pay;
                }
            }

            for (var i = 0; i < n; i++)
                if (payoffMonth[i] is null && balances[i] <= 0.005m)
                {
                    balances[i] = 0;
                    payoffMonth[i] = month;
                }

            timeline.Add(MoneyMath.Round2(balances.Sum()));
            if (timeline.Count > 24 && timeline[^1] >= timeline[^25])
                return new StrategyResult { Possible = false, TotalInterest = totalInterest, BalanceTimeline = timeline };
        }

        var possible = balances.All(b => b <= 0.005m);
        return new StrategyResult
        {
            Possible = possible,
            Months = possible ? payoffMonth.Max() ?? 0 : maxMonths,
            TotalInterest = MoneyMath.Round2(totalInterest),
            TotalPaid = MoneyMath.Round2(totalPaid),
            Order = Enumerable.Range(0, n)
                .Where(i => payoffMonth[i] is not null)
                .OrderBy(i => payoffMonth[i])
                .Select(i => new StrategyPayoff(debts[i].Name, debts[i].Id, payoffMonth[i]!.Value, MoneyMath.Round2(interestPaid[i])))
                .ToList(),
            BalanceTimeline = timeline,
        };
    }
}
