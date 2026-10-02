namespace BudgetPlaner.Domain.Calculators;

// Öffentliche Finanzrechner wie im Original (B-10): ohne Login nutzbar, rein clientseitig berechnet.

public sealed record CompoundYear(int Year, decimal Contributions, decimal Interest, decimal Balance, decimal RealBalance);

public sealed record CompoundInterestResult(decimal FinalBalance, decimal TotalContributions, decimal TotalInterest, decimal RealFinalBalance,
    IReadOnlyList<CompoundYear> Years);

public sealed record AmortizationRow(int Month, decimal Payment, decimal Interest, decimal Principal, decimal SpecialRepayment, decimal Balance);

public sealed record AmortizationYear(int Year, decimal Payments, decimal Interest, decimal Principal, decimal SpecialRepayment, decimal Balance);

public sealed record LoanResult(decimal MonthlyPayment, decimal TotalInterest, decimal TotalCost, IReadOnlyList<AmortizationRow> Schedule,
    IReadOnlyList<AmortizationYear> Years);

public sealed record HourlyWageResult(decimal HourlyWage, decimal MonthlyHours, decimal MinuteWage);

public sealed record RetirementResult(int YearsToRetirement, int YearsInRetirement, decimal MonthlyGapToday, decimal MonthlyGapAtRetirement,
    decimal RequiredCapital, decimal ProjectedCapital, decimal Surplus, decimal RequiredMonthlySavings, int? CapitalLastsUntilAge,
    IReadOnlyList<(int Age, decimal Balance)> Timeline);

public sealed record MortgageResult(decimal ClosingCosts, decimal LoanAmount, decimal LoanToValuePercent, decimal MonthlyPayment,
    decimal RemainingAfterFixedPeriod, decimal InterestDuringFixedPeriod, int? MonthsToPayoff, decimal TotalInterest,
    IReadOnlyList<AmortizationYear> Years);

public static class FinanceCalculators
{
    /// <summary>Zinseszins mit monatlicher Sparrate und monatlicher Verzinsung (Rendite ÷ 12), optional Dynamik und Inflation.</summary>
    public static CompoundInterestResult CompoundInterest(decimal startCapital, decimal monthlyContribution, decimal annualReturnPercent,
        int years, decimal inflationPercent = 0, decimal annualContributionIncreasePercent = 0)
    {
        years = Math.Clamp(years, 0, 100);
        var r = annualReturnPercent / 1200m;
        var balance = startCapital;
        var contributions = startCapital;
        var contribution = monthlyContribution;
        var rows = new List<CompoundYear>();
        for (var y = 1; y <= years; y++)
        {
            for (var m = 0; m < 12; m++)
            {
                balance = balance * (1 + r) + contribution;
                contributions += contribution;
            }
            var real = Real(balance, inflationPercent, y);
            rows.Add(new CompoundYear(y, MoneyMath.Round2(contributions), MoneyMath.Round2(balance - contributions), MoneyMath.Round2(balance), real));
            contribution *= 1 + annualContributionIncreasePercent / 100m;
        }
        var final = MoneyMath.Round2(balance);
        return new CompoundInterestResult(final, MoneyMath.Round2(contributions), MoneyMath.Round2(balance - contributions), Real(balance, inflationPercent, years), rows);
    }

    private static decimal Real(decimal nominal, decimal inflationPercent, int years) =>
        inflationPercent == 0 ? MoneyMath.Round2(nominal) : MoneyMath.Round2(nominal / (decimal)Math.Pow(1 + (double)inflationPercent / 100d, years));

    /// <summary>Annuitätendarlehen: gleichbleibende Monatsrate (Sollzins), Tilgungsplan je Monat und Jahr.</summary>
    public static LoanResult Loan(decimal amount, decimal annualRatePercent, int months)
    {
        months = Math.Clamp(months, 1, 1200);
        var payment = DebtCalculator.AnnuityPayment(amount, annualRatePercent, months);
        var schedule = Amortize(amount, annualRatePercent, payment, 0, months);
        var totalInterest = schedule.Sum(s => s.Interest);
        return new LoanResult(payment, MoneyMath.Round2(totalInterest), MoneyMath.Round2(amount + totalInterest), schedule, Yearly(schedule));
    }

    /// <summary>Tilgungsplan einer Annuität mit optionaler jährlicher Sondertilgung (am Jahresende).</summary>
    public static List<AmortizationRow> Amortize(decimal amount, decimal annualRatePercent, decimal monthlyPayment, decimal annualSpecial, int maxMonths)
    {
        var rows = new List<AmortizationRow>();
        var balance = amount;
        var r = annualRatePercent / 1200m;
        for (var m = 1; m <= maxMonths && balance > 0.005m; m++)
        {
            var interest = MoneyMath.Round2(balance * r);
            var principal = Math.Min(monthlyPayment - interest, balance);
            if (principal <= 0 && monthlyPayment > 0 && annualSpecial <= 0) break;
            balance -= principal;
            decimal special = 0;
            if (annualSpecial > 0 && m % 12 == 0 && balance > 0)
            {
                special = Math.Min(annualSpecial, balance);
                balance -= special;
            }
            rows.Add(new AmortizationRow(m, MoneyMath.Round2(interest + principal), interest, MoneyMath.Round2(principal), special, MoneyMath.Round2(Math.Max(0, balance))));
        }
        return rows;
    }

    public static List<AmortizationYear> Yearly(IReadOnlyList<AmortizationRow> rows) =>
        rows.GroupBy(r => (r.Month - 1) / 12 + 1)
            .Select(g => new AmortizationYear(g.Key, g.Sum(x => x.Payment), g.Sum(x => x.Interest), g.Sum(x => x.Principal),
                g.Sum(x => x.SpecialRepayment), g.Last().Balance))
            .ToList();

    /// <summary>Stundenlohn aus Nettogehalt: Wochenstunden × 52 ÷ 12 Stunden pro Monat.</summary>
    public static HourlyWageResult HourlyWage(decimal netMonthly, decimal hoursPerWeek)
    {
        if (hoursPerWeek <= 0) return new HourlyWageResult(0, 0, 0);
        var monthlyHours = hoursPerWeek * 52m / 12m;
        var hourly = netMonthly / monthlyHours;
        return new HourlyWageResult(MoneyMath.Round2(hourly), Math.Round(monthlyHours, 2), hourly / 60m);
    }

    /// <summary>Arbeitszeit in Stunden, die ein Kauf kostet.</summary>
    public static decimal WorkHoursFor(decimal price, HourlyWageResult wage) => wage.HourlyWage <= 0 ? 0 : price / wage.HourlyWage;

    /// <summary>
    /// Ruhestandsrechner: Versorgungslücke (inflationsbereinigt), benötigtes Kapital (wachsende Entnahme, Verzinsung im Ruhestand),
    /// prognostiziertes Kapital aus Bestand + Sparrate und nötige Sparrate.
    /// </summary>
    public static RetirementResult Retirement(int currentAge, int retirementAge, int lifeExpectancy, decimal desiredMonthlyIncome,
        decimal expectedPension, decimal currentSavings, decimal monthlySavings, decimal returnBeforePercent, decimal returnAfterPercent,
        decimal inflationPercent)
    {
        var yearsTo = Math.Max(0, retirementAge - currentAge);
        var yearsIn = Math.Max(0, lifeExpectancy - retirementAge);
        var n = yearsTo * 12;
        var rb = (double)returnBeforePercent / 1200d;
        var ra = (double)returnAfterPercent / 1200d;
        var g = Math.Pow(1 + (double)inflationPercent / 100d, 1d / 12d) - 1;

        var gapToday = Math.Max(0, desiredMonthlyIncome - expectedPension);
        var gapAtRetirement = (double)gapToday * Math.Pow(1 + (double)inflationPercent / 100d, yearsTo);

        // Benötigtes Kapital: Barwert einer monatlich mit der Inflation wachsenden Entnahme (vorschüssig).
        double required = 0;
        var m = yearsIn * 12;
        for (var k = 0; k < m; k++) required += gapAtRetirement * Math.Pow((1 + g) / (1 + ra), k);

        var growth = Math.Pow(1 + rb, n);
        var fvSavings = rb == 0 ? (double)monthlySavings * n : (double)monthlySavings * (growth - 1) / rb;
        var projected = (double)currentSavings * growth + fvSavings;

        double requiredSavings = 0;
        if (required > (double)currentSavings * growth && n > 0)
            requiredSavings = rb == 0 ? (required - (double)currentSavings) / n : (required - (double)currentSavings * growth) * rb / (growth - 1);

        // Simulation: wie lange reicht das prognostizierte Kapital?
        var timeline = new List<(int, decimal)>();
        double balance = (double)currentSavings;
        for (var age = currentAge; age < retirementAge; age++)
        {
            timeline.Add((age, (decimal)Math.Round(balance, 2)));
            for (var i = 0; i < 12; i++) balance = balance * (1 + rb) + (double)monthlySavings;
        }
        int? lastsUntil = null;
        var withdrawal = gapAtRetirement;
        for (var age = retirementAge; age <= Math.Max(lifeExpectancy, retirementAge) + 30 && age <= 130; age++)
        {
            timeline.Add((age, (decimal)Math.Round(Math.Max(0, balance), 2)));
            for (var i = 0; i < 12; i++)
            {
                balance -= withdrawal;
                if (balance < 0 && lastsUntil is null) lastsUntil = age;
                balance *= 1 + ra;
                withdrawal *= 1 + g;
            }
            if (lastsUntil is not null && age >= lifeExpectancy) break;
        }
        if (gapToday == 0) lastsUntil = null;

        return new RetirementResult(yearsTo, yearsIn, gapToday, MoneyMath.Round2((decimal)gapAtRetirement), MoneyMath.Round2((decimal)required),
            MoneyMath.Round2((decimal)projected), MoneyMath.Round2((decimal)(projected - required)), MoneyMath.Round2((decimal)Math.Max(0, requiredSavings)),
            lastsUntil, timeline);
    }

    /// <summary>Baufinanzierung: Kaufnebenkosten, Darlehen, Rate aus Zins + anfänglicher Tilgung, Restschuld nach Zinsbindung.</summary>
    public static MortgageResult Mortgage(decimal purchasePrice, decimal equity, decimal closingCostsPercent, decimal annualRatePercent,
        decimal initialRepaymentPercent, int fixedRateYears, decimal annualSpecialRepayment = 0)
    {
        var closing = MoneyMath.Round2(purchasePrice * closingCostsPercent / 100m);
        var loan = Math.Max(0, purchasePrice + closing - equity);
        var payment = MoneyMath.Round2(loan * (annualRatePercent + initialRepaymentPercent) / 1200m);
        var ltv = purchasePrice > 0 ? loan / purchasePrice * 100m : 0;
        var rows = Amortize(loan, annualRatePercent, payment, annualSpecialRepayment, 1200);
        var fixedMonths = Math.Max(0, fixedRateYears * 12);
        var withinFixed = rows.Where(r => r.Month <= fixedMonths).ToList();
        var remaining = withinFixed.Count == 0 ? loan : withinFixed[^1].Balance;
        if (withinFixed.Count < fixedMonths && rows.Count > 0 && rows[^1].Balance <= 0.005m) remaining = 0;
        int? months = rows.Count > 0 && rows[^1].Balance <= 0.005m ? rows.Count : null;
        return new MortgageResult(closing, loan, MoneyMath.Round2(ltv), payment, remaining, withinFixed.Sum(r => r.Interest), months,
            rows.Sum(r => r.Interest), Yearly(rows));
    }
}
