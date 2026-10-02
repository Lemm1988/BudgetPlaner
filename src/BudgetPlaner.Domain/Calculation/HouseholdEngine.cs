namespace BudgetPlaner.Domain;

/// <summary>
/// Fassade über alle Berechnungen eines Haushalts mit Zwischenspeicher je Periode.
/// Nach einer Datenänderung wird eine neue Engine erzeugt (Berechnungen &lt; 300 ms, NFR Performance).
/// </summary>
public sealed class HouseholdEngine
{
    private readonly Dictionary<int, MonthResult> _months = [];
    private Dictionary<int, (decimal Allocations, decimal Withdrawals)>? _budgetTransactions;
    private IReadOnlyList<DueBooking>? _pendingBookings;
    private CashflowCalculator? _cashflow;
    private DebtCalculator? _debts;
    private BufferCalculator? _buffers;
    private StatisticsCalculator? _statistics;
    private ForecastCalculator? _forecasts;
    private AmpelEvaluator? _ampel;

    public HouseholdEngine(HouseholdData data, DateOnly today)
    {
        Data = data;
        Today = today;
        Index = data.Index;
        Calendar = Index.Calendar;
        CurrentPeriod = Calendar.PeriodOf(today);
        Rules = data.Household.AmpelRules ?? AmpelRules.Default;
        FirstPeriod = ComputeFirstPeriod();
    }

    public HouseholdData Data { get; }
    public HouseholdIndex Index { get; }
    public PeriodCalendar Calendar { get; }
    public DateOnly Today { get; }
    public Period CurrentPeriod { get; }
    public AmpelRules Rules { get; }

    /// <summary>Erste Periode mit Haushaltsdaten; davor gelten Monate als Lücke (F-061).</summary>
    public Period FirstPeriod { get; }

    public MonthResult Month(Period p)
    {
        if (!_months.TryGetValue(p.Index, out var result))
        {
            result = BudgetCalculator.Compute(this, p);
            _months[p.Index] = result;
        }
        return result;
    }

    public bool HasCachedMonth(Period p) => _months.ContainsKey(p.Index);

    /// <summary>Netto-Kontofluss aus manuellen Zuweisungen/Entnahmen von Polstern mit separatem Konto.</summary>
    public decimal SeparateAccountTransactionNet(Period p) => SeparateNet().GetValueOrDefault(p.Index);

    private Dictionary<int, decimal>? _separateNet;

    private Dictionary<int, decimal> SeparateNet()
    {
        if (_separateNet is not null) return _separateNet;
        var dict = new Dictionary<int, decimal>();
        foreach (var t in Index.Transactions)
        {
            if (!t.AffectsBudget || !Index.BufferById.TryGetValue(t.BufferId, out var b) || !b.SeparateAccount) continue;
            decimal amount = t.Type == BufferTransactionType.Withdrawal ? t.Amount : t.SourceEntryId is null ? -t.Amount : 0;
            if (amount == 0) continue;
            var key = Calendar.PeriodOf(t.Date).Index;
            dict[key] = dict.GetValueOrDefault(key) + amount;
        }
        return _separateNet = dict;
    }

    /// <summary>Monatsergebnis oder <c>null</c>, wenn die Periode eine Lücke ist (vor Haushaltsbeginn oder ohne Daten).</summary>
    public MonthResult? MonthOrGap(Period p) => IsGap(p) ? null : Month(p);

    private readonly Dictionary<int, MonthTotals> _totals = [];

    /// <summary>Monatssummen (schnell, ohne Zeilen) – für Zeitreihen, Ampel und Kennzahlen.</summary>
    public MonthTotals Totals(Period p)
    {
        if (!_totals.TryGetValue(p.Index, out var t)) _totals[p.Index] = t = BudgetCalculator.ComputeTotals(this, p);
        return t;
    }

    /// <summary>Lücke = vor Haushaltsbeginn oder Periode ohne Daten (F-061).</summary>
    public bool IsGap(Period p) => p < FirstPeriod || !Totals(p).HasData;

    public CashflowCalculator Cashflow => _cashflow ??= new CashflowCalculator(this);
    public DebtCalculator Debts => _debts ??= new DebtCalculator(this);
    public BufferCalculator Buffers => _buffers ??= new BufferCalculator(this);
    public StatisticsCalculator Statistics => _statistics ??= new StatisticsCalculator(this);
    public ForecastCalculator Forecasts => _forecasts ??= new ForecastCalculator(this);
    public AmpelEvaluator Ampel => _ampel ??= new AmpelEvaluator(this);

    /// <summary>Fällige, noch nicht verbuchte automatische Buchungen bis heute (Raten, Sparpläne).</summary>
    public IReadOnlyList<DueBooking> PendingBookings => _pendingBookings ??= BookingPlanner.Pending(this, Today);

    public decimal ManualAllocationsIn(Period p) => BudgetTransactions().GetValueOrDefault(p.Index).Allocations;

    public decimal WithdrawalsIn(Period p) => BudgetTransactions().GetValueOrDefault(p.Index).Withdrawals;

    private Dictionary<int, (decimal Allocations, decimal Withdrawals)> BudgetTransactions()
    {
        if (_budgetTransactions is not null) return _budgetTransactions;
        var dict = new Dictionary<int, (decimal, decimal)>();
        foreach (var t in Index.Transactions)
        {
            if (!t.AffectsBudget) continue;
            var key = Calendar.PeriodOf(t.Date).Index;
            var (a, w) = dict.GetValueOrDefault(key);
            if (t.Type == BufferTransactionType.Allocation)
            {
                // Sparplan-Buchungen sind bereits als Position enthalten.
                if (t.SourceEntryId is null) a += t.Amount;
            }
            else w += t.Amount;
            dict[key] = (a, w);
        }
        return _budgetTransactions = dict;
    }

    private Period ComputeFirstPeriod()
    {
        var candidates = new List<Period>();
        var hp = Data.Household.FirstPeriod;
        if (hp.Year > 0) candidates.Add(hp);
        foreach (var e in Index.Entries) candidates.Add(e.StartPeriod);
        foreach (var s in Index.SnapshotByPeriod.Values) candidates.Add(s.Period);
        if (Index.Transactions.Count > 0) candidates.Add(Calendar.PeriodOf(Index.Transactions[0].Date));
        if (Index.Checkpoints.Count > 0) candidates.Add(Calendar.PeriodOf(Index.Checkpoints[0].Date));
        foreach (var d in Index.Debts) candidates.Add(Calendar.PeriodOf(d.StartDate));
        return candidates.Count == 0 ? CurrentPeriod : candidates.MinBy(p => p.Index);
    }
}
