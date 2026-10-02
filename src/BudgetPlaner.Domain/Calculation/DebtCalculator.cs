using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>Zahlung inkl. Aufteilung in Zins und Tilgung (F-021).</summary>
public sealed class PaymentView
{
    public Guid? PaymentId { get; init; }
    public DateOnly Date { get; init; }
    public decimal Amount { get; init; }
    public decimal Interest { get; init; }
    public decimal Principal { get; init; }
    public decimal BalanceAfter { get; init; }
    public bool IsExtra { get; init; }

    /// <summary>Fällig, aber noch nicht vom Server verbucht.</summary>
    public bool IsProjected { get; init; }

    public Guid? SourceEntryId { get; init; }
    public Period? SourcePeriod { get; init; }
    public string? Note { get; init; }

    /// <summary>Betrag, der über die Restschuld hinaus gezahlt wurde.</summary>
    public decimal Overpayment => Math.Max(0, Amount - Interest - Principal);
}

/// <summary>Eingabe für die chronologische Zins-/Tilgungsaufteilung.</summary>
public sealed record PaymentInput(
    Guid? Id,
    DateOnly Date,
    decimal Amount,
    bool IsExtra,
    decimal? InterestRate,
    bool IsProjected,
    Guid? SourceEntryId = null,
    Period? SourcePeriod = null,
    string? Note = null,
    DateTimeOffset CreatedAt = default);

public sealed class DebtStatus
{
    public required DebtEntry Debt { get; init; }
    public decimal Balance { get; init; }
    public decimal PrincipalPaid { get; init; }
    public decimal InterestPaid { get; init; }

    /// <summary>Tilgungsfortschritt in Prozent (0–100) relativ zur Ursprungssumme (F-022).</summary>
    public decimal ProgressPercent { get; init; }

    public bool IsPaidOff { get; init; }
    public DateOnly? PaidOffDate { get; init; }
    public required IReadOnlyList<PaymentView> Payments { get; init; }

    /// <summary>Aktuelle Monatsbelastung (Rate + geplante Zusatztilgung der laufenden Periode).</summary>
    public decimal CurrentMonthlyPayment { get; init; }

    /// <summary>Zinsanteil, der im nächsten Monat anfällt.</summary>
    public decimal NextMonthInterest { get; init; }

    /// <summary>Die Rate deckt nicht einmal die Zinsen – die Schuld wächst.</summary>
    public bool RateBelowInterest { get; init; }
}

/// <summary>Zeile eines Tilgungsplans (B-06).</summary>
public sealed record ScheduleRow(Period Period, DateOnly Date, decimal Payment, decimal Interest, decimal Principal, decimal Extra, decimal Balance);

public sealed class DebtForecast
{
    public bool Possible { get; init; }

    /// <summary>„paid“, „annuity“, „linear“.</summary>
    public string Method { get; init; } = "";

    /// <summary>Grund, falls keine Prognose möglich: „no-payment“, „rate-below-interest“, „too-long“.</summary>
    public string? Reason { get; init; }

    public int? Months { get; init; }
    public Period? PayoffPeriod { get; init; }
    public DateOnly? PayoffDate { get; init; }
    public decimal TotalInterest { get; init; }
    public decimal TotalPaid { get; init; }
    public IReadOnlyList<ScheduleRow> Schedule { get; init; } = [];
}

/// <summary>Schulden: Restschuld, Fortschritt, Tilgungsplan und Schuldenfreiheits-Prognose (F-020–F-022, F-043, F-071).</summary>
public sealed class DebtCalculator
{
    public const int MaxForecastMonths = 600;
    private readonly HouseholdEngine _engine;
    private readonly Dictionary<Guid, DebtStatus> _status = [];
    private readonly Dictionary<Guid, DebtForecast> _forecasts = [];

    public DebtCalculator(HouseholdEngine engine) => _engine = engine;

    public IReadOnlyList<DebtStatus> All() => _engine.Index.Debts.Select(d => Status(d.Id)).ToList();

    /// <summary>Gesamtschuldensumme (F-020).</summary>
    public decimal TotalBalance => _engine.Index.Debts.Sum(d => Math.Max(0, Status(d.Id).Balance));

    public decimal TotalOriginal => _engine.Index.Debts.Sum(d => d.OriginalAmount);

    /// <summary>Gesamtfortschritt über alle Schulden (F-022).</summary>
    public decimal TotalProgressPercent
    {
        get
        {
            var original = _engine.Index.Debts.Sum(d => Math.Max(d.OriginalAmount, d.OpeningBalance));
            if (original <= 0) return 0;
            var remaining = TotalBalance;
            return Math.Clamp((original - remaining) / original * 100m, 0, 100);
        }
    }

    public DebtStatus Status(Guid debtId)
    {
        if (_status.TryGetValue(debtId, out var cached)) return cached;
        var ix = _engine.Index;
        var debt = ix.DebtById[debtId];
        var inputs = new List<PaymentInput>();
        if (ix.PaymentsByDebt.TryGetValue(debtId, out var payments))
            inputs.AddRange(payments.Select(p => new PaymentInput(p.Id, p.Date, p.Amount, p.IsExtra, p.InterestRateApplied,
                false, p.SourceEntryId, p.SourcePeriod, p.Note, p.CreatedAt)));
        foreach (var b in _engine.PendingBookings)
            if (b.Target == BookingTarget.Debt && b.TargetId == debtId)
                inputs.Add(new PaymentInput(null, b.Date, b.Amount, b.IsExtra, debt.InterestRate, true, b.EntryId, b.Period));

        var splits = Split(debt, inputs);
        var balance = splits.Count > 0 ? splits[^1].BalanceAfter : debt.OpeningBalance;
        DateOnly? paidOff = null;
        if (balance <= 0.005m && (debt.OpeningBalance > 0 || splits.Count > 0))
            paidOff = splits.FirstOrDefault(s => s.BalanceAfter <= 0.005m)?.Date ?? debt.StartDate;

        var reference = Math.Max(debt.OriginalAmount, debt.OpeningBalance);
        var progress = reference > 0 ? Math.Clamp((reference - Math.Max(balance, 0)) / reference * 100m, 0, 100) : (paidOff is not null ? 100 : 0);
        var monthly = CurrentMonthlyPayment(debt);
        var nextInterest = MoneyMath.Round2(Math.Max(balance, 0) * (debt.InterestRate ?? 0) / 1200m);
        var regularRate = RegularRate(debt, _engine.CurrentPeriod);

        var status = new DebtStatus
        {
            Debt = debt,
            Balance = MoneyMath.Round2(balance),
            PrincipalPaid = splits.Sum(s => s.Principal),
            InterestPaid = splits.Sum(s => s.Interest),
            ProgressPercent = progress,
            IsPaidOff = paidOff is not null,
            PaidOffDate = paidOff,
            Payments = splits,
            CurrentMonthlyPayment = monthly,
            NextMonthInterest = nextInterest,
            RateBelowInterest = paidOff is null && balance > 0 && regularRate > 0 && regularRate <= nextInterest,
        };
        _status[debtId] = status;
        return status;
    }

    /// <summary>Restschuld am Ende eines Tages (für Vermögensverlauf F-068); <c>null</c> vor Erfassung.</summary>
    public decimal? BalanceAt(Guid debtId, DateOnly date)
    {
        var debt = _engine.Index.DebtById[debtId];
        if (date < debt.StartDate) return null;
        var status = Status(debtId);
        var balance = debt.OpeningBalance;
        foreach (var p in status.Payments)
        {
            if (p.Date > date) break;
            balance = p.BalanceAfter;
        }
        return Math.Max(0, balance);
    }

    /// <summary>
    /// Chronologische Zins-/Tilgungsaufteilung (Annuitätenlogik): Zins = Rest × Satz ÷ 12, Tilgung = Rate − Zins;
    /// Sondertilgungen sind reine Tilgung; ohne Zinssatz ist die ganze Rate Tilgung (F-021).
    /// </summary>
    public static List<PaymentView> Split(DebtEntry debt, IEnumerable<PaymentInput> inputs)
    {
        var balance = debt.OpeningBalance;
        var result = new List<PaymentView>();
        foreach (var p in inputs.OrderBy(x => x.Date).ThenBy(x => x.IsExtra).ThenBy(x => x.IsProjected).ThenBy(x => x.CreatedAt))
        {
            if (p.IsProjected && balance <= 0.005m) continue;
            var rate = p.IsExtra ? 0m : p.InterestRate ?? debt.InterestRate ?? 0m;
            var interest = balance > 0 ? MoneyMath.Round2(balance * rate / 1200m) : 0m;
            var principal = p.Amount - interest;
            if (principal > balance) principal = Math.Max(balance, 0);
            if (interest > p.Amount && balance <= 0) interest = p.Amount;
            balance = MoneyMath.Round2(balance - principal);
            result.Add(new PaymentView
            {
                PaymentId = p.Id,
                Date = p.Date,
                Amount = p.Amount,
                Interest = interest,
                Principal = principal,
                BalanceAfter = balance,
                IsExtra = p.IsExtra,
                IsProjected = p.IsProjected,
                SourceEntryId = p.SourceEntryId,
                SourcePeriod = p.SourcePeriod,
                Note = p.Note,
            });
        }
        return result;
    }

    /// <summary>Reguläre Rate in einer Periode (verknüpfte Rate-Positionen oder Vertragsrate).</summary>
    public decimal RegularRate(DebtEntry debt, Period p)
    {
        var linked = _engine.Index.EntriesByDebt.GetValueOrDefault(debt.Id) ?? [];
        var rates = linked.Where(e => e.DebtRole == DebtRole.Rate).ToList();
        if (rates.Count == 0) return debt.MonthlyRate ?? 0;
        decimal sum = 0;
        foreach (var e in rates)
        {
            // Für nicht-monatliche Raten den nächsten fälligen Betrag heranziehen.
            var due = EntrySchedule.IsDue(e, p) ? p : EntrySchedule.NextDue(e, p);
            if (due is { } d) sum += EntrySchedule.State(_engine.Index, e, d).Planned / Math.Max(1, e.Rhythm.IntervalMonths());
        }
        return sum;
    }

    private decimal CurrentMonthlyPayment(DebtEntry debt)
    {
        var linked = _engine.Index.EntriesByDebt.GetValueOrDefault(debt.Id) ?? [];
        if (linked.Length == 0) return debt.MonthlyRate ?? 0;
        var p = _engine.CurrentPeriod;
        return linked.Where(e => EntrySchedule.IsDue(e, p)).Sum(e => EntrySchedule.State(_engine.Index, e, p).Effective);
    }

    /// <summary>Geplante künftige Zahlungen einer Periode (nach heute).</summary>
    public List<(DateOnly Date, decimal Amount, bool IsExtra)> PlannedPayments(DebtEntry debt, Period p, IReadOnlyCollection<BudgetEntry>? overrideLinked = null)
    {
        var ix = _engine.Index;
        var cal = _engine.Calendar;
        var linked = overrideLinked ?? (IReadOnlyCollection<BudgetEntry>)(ix.EntriesByDebt.GetValueOrDefault(debt.Id) ?? []);
        var list = new List<(DateOnly, decimal, bool)>();
        if (linked.Count == 0)
        {
            if (debt.MonthlyRate is > 0)
            {
                var date = cal.DueDate(p, debt.DebitDay);
                if (date > _engine.Today) list.Add((date, debt.MonthlyRate.Value, false));
            }
            return list;
        }
        foreach (var e in linked)
        {
            if (e.DebtRole == DebtRole.None || !EntrySchedule.IsDue(e, p)) continue;
            var date = cal.DueDate(p, e.DebitDay ?? debt.DebitDay);
            if (date <= _engine.Today) continue;
            var s = EntrySchedule.State(ix, e, p);
            var amount = s.Actual ?? s.Planned;
            if (amount > 0) list.Add((date, amount, e.DebtRole == DebtRole.Extra));
        }
        return list;
    }

    /// <summary>Schuldenfreiheits-Prognose mit Tilgungsplan (F-071; linear nach F-043 ohne feste Rate).</summary>
    public DebtForecast Forecast(Guid debtId)
    {
        if (_forecasts.TryGetValue(debtId, out var cached)) return cached;
        var result = ComputeForecast(debtId);
        _forecasts[debtId] = result;
        return result;
    }

    private DebtForecast ComputeForecast(Guid debtId)
    {
        var debt = _engine.Index.DebtById[debtId];
        var status = Status(debtId);
        if (status.IsPaidOff)
            return new DebtForecast { Possible = true, Method = "paid", Months = 0, PayoffDate = status.PaidOffDate, PayoffPeriod = status.PaidOffDate is { } pd ? _engine.Calendar.PeriodOf(pd) : null };

        var balance = status.Balance;
        var hasPlan = (_engine.Index.EntriesByDebt.GetValueOrDefault(debtId)?.Any(e => e.DebtRole != DebtRole.None) ?? false) || debt.MonthlyRate is > 0;
        if (!hasPlan) return LinearForecast(debt, status);

        var r = (debt.InterestRate ?? 0) / 1200m;
        var rows = new List<ScheduleRow>();
        decimal totalInterest = 0, totalPaid = 0;
        var monthsWithPayment = 0;
        var emptyStreak = 0;
        for (var k = 0; k < MaxForecastMonths; k++)
        {
            var p = _engine.CurrentPeriod.AddMonths(k);
            var payments = PlannedPayments(debt, p);
            if (payments.Count == 0)
            {
                // Leasing/Ballon: Restwert als Schlussrate, wenn keine Raten mehr anstehen.
                if (debt.ResidualValue is > 0 && balance <= debt.ResidualValue.Value + 0.005m && monthsWithPayment > 0)
                    payments.Add((_engine.Calendar.DueDate(p, debt.DebitDay), balance, true));
                else if (++emptyStreak > 24) return new DebtForecast { Possible = false, Method = "annuity", Reason = "no-payment", Schedule = rows };
                else continue;
            }
            emptyStreak = 0;
            monthsWithPayment++;
            decimal pay = 0, interest = 0, principal = 0, extra = 0;
            var date = payments[^1].Date;
            var regularPaid = false;
            foreach (var (pDate, amount, isExtra) in payments.OrderBy(x => x.Date).ThenBy(x => x.IsExtra))
            {
                if (balance <= 0.005m) break;
                var i = isExtra ? 0 : (regularPaid ? 0 : MoneyMath.Round2(balance * r));
                if (!isExtra) regularPaid = true;
                var pr = Math.Min(amount - i, balance);
                if (pr < 0 && !isExtra && r > 0 && payments.All(x => !x.IsExtra))
                    return new DebtForecast { Possible = false, Method = "annuity", Reason = "rate-below-interest", Schedule = rows };
                balance = MoneyMath.Round2(balance - pr);
                interest += i;
                principal += pr;
                if (isExtra) extra += amount; else pay += amount;
                date = pDate;
            }
            totalInterest += interest;
            totalPaid += interest + principal;
            rows.Add(new ScheduleRow(p, date, pay, interest, principal, extra, Math.Max(0, balance)));
            if (balance <= 0.005m)
                return new DebtForecast
                {
                    Possible = true, Method = "annuity", Months = monthsWithPayment, PayoffPeriod = p, PayoffDate = date,
                    TotalInterest = totalInterest, TotalPaid = totalPaid, Schedule = rows,
                };
            if (k >= 24 && rows.Count >= 13 && rows[^1].Balance >= rows[^13].Balance)
                return new DebtForecast { Possible = false, Method = "annuity", Reason = "rate-below-interest", Schedule = rows };
        }
        return new DebtForecast { Possible = false, Method = "annuity", Reason = "too-long", Schedule = rows };
    }

    /// <summary>Lineare Extrapolation aus dem 3-Monats-Durchschnitt der Tilgungen (F-043).</summary>
    private DebtForecast LinearForecast(DebtEntry debt, DebtStatus status)
    {
        var cal = _engine.Calendar;
        var current = _engine.CurrentPeriod;
        var firstPeriod = cal.PeriodOf(debt.StartDate);
        var periods = Enumerable.Range(0, 3).Select(i => current.AddMonths(-i)).Where(p => p >= firstPeriod).ToList();
        if (periods.Count == 0) periods.Add(current);
        var sum = status.Payments.Where(x => periods.Contains(cal.PeriodOf(x.Date))).Sum(x => x.Principal);
        var avg = sum / periods.Count;
        if (avg <= 0) return new DebtForecast { Possible = false, Method = "linear", Reason = "no-payment" };
        var months = (int)Math.Ceiling(status.Balance / avg);
        var payoff = current.AddMonths(months);
        return new DebtForecast
        {
            Possible = true, Method = "linear", Months = months, PayoffPeriod = payoff, PayoffDate = cal.End(payoff),
            TotalPaid = status.Balance,
        };
    }

    /// <summary>Gesamtprognose: späteste Einzelprognose (F-071 „separat berechnet, zusammengeführt“).</summary>
    public (bool Possible, Period? PayoffPeriod, int? Months, decimal TotalInterest) Combined()
    {
        var open = _engine.Index.Debts.Where(d => !Status(d.Id).IsPaidOff).ToList();
        if (open.Count == 0) return (true, null, 0, 0);
        var forecasts = open.Select(d => Forecast(d.Id)).ToList();
        if (forecasts.Any(f => !f.Possible)) return (false, null, null, forecasts.Where(f => f.Possible).Sum(f => f.TotalInterest));
        var latest = forecasts.MaxBy(f => f.PayoffPeriod?.Index ?? 0)!;
        return (true, latest.PayoffPeriod, forecasts.Max(f => f.Months), forecasts.Sum(f => f.TotalInterest));
    }

    /// <summary>Annuitäten-Laufzeit in Monaten: n = −ln(1 − r·B/A) / ln(1 + r); ohne Zins ⌈B/A⌉.</summary>
    public static int? AnnuityMonths(decimal balance, decimal annualRatePercent, decimal monthlyPayment)
    {
        if (balance <= 0) return 0;
        if (monthlyPayment <= 0) return null;
        var r = (double)annualRatePercent / 1200d;
        if (r <= 0) return (int)Math.Ceiling(balance / monthlyPayment);
        var x = 1 - r * (double)balance / (double)monthlyPayment;
        if (x <= 0) return null;
        return (int)Math.Ceiling(-Math.Log(x) / Math.Log(1 + r));
    }

    /// <summary>Annuität (Monatsrate) für Darlehen, Zins p. a. in %, Laufzeit in Monaten.</summary>
    public static decimal AnnuityPayment(decimal principal, decimal annualRatePercent, int months)
    {
        if (months <= 0) return principal;
        var r = (double)annualRatePercent / 1200d;
        if (r <= 0) return MoneyMath.Round2(principal / months);
        var a = (double)principal * r / (1 - Math.Pow(1 + r, -months));
        return MoneyMath.Round2((decimal)a);
    }
}
