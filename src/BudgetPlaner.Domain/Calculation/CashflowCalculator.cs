using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>Einzelner Zu- oder Abfluss an einem Tag (F-030).</summary>
public sealed class CashItem
{
    public required string Name { get; init; }

    /// <summary>Vorzeichenbehaftet: Zufluss +, Abfluss −.</summary>
    public decimal Amount { get; init; }

    /// <summary>„income“, „expense“, „allocation“, „withdrawal“.</summary>
    public required string Type { get; init; }

    public DateOnly Date { get; init; }
    public Guid? EntryId { get; init; }
    public Guid? TransactionId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? BufferId { get; init; }
    public Guid? DebtId { get; init; }
    public bool IsDone { get; init; }
    public bool IsFixed { get; init; }
    public bool HasActual { get; init; }
}

public sealed class CashDay
{
    public DateOnly Date { get; init; }
    public required IReadOnlyList<CashItem> Items { get; init; }
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Net => In + Out;

    /// <summary>Kontostand am Ende des Tages (F-031).</summary>
    public decimal Balance { get; init; }

    /// <summary>Saldo &lt; 0 → kritisch (F-031).</summary>
    public bool IsCritical => Balance < 0;

    public BalanceCheckpoint? Checkpoint { get; init; }

    /// <summary>Abweichung zwischen gerechnetem und abgeglichenem Stand.</summary>
    public decimal? CheckpointDifference { get; init; }

    public bool IsPast { get; init; }
    public bool IsToday { get; init; }
}

public sealed class CashflowResult
{
    public Period Period { get; init; }
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public required IReadOnlyList<CashDay> Days { get; init; }
    public decimal StartBalance { get; init; }
    public decimal EndBalance { get; init; }
    public decimal TotalIn { get; init; }
    public decimal TotalOut { get; init; }
    public decimal MinBalance { get; init; }
    public DateOnly MinBalanceDate { get; init; }

    /// <summary>Es existiert mindestens ein Kontostand-Abgleich (sonst Start bei 0 €, I-03).</summary>
    public bool HasAnchor { get; init; }

    public bool HasCriticalDays => Days.Any(d => d.IsCritical);
}

/// <summary>Cashflow-Kalender, laufender Kontostand und Saldo-Projektion (F-030–F-032, F-070).</summary>
public sealed class CashflowCalculator
{
    private readonly HouseholdEngine _engine;
    private readonly Dictionary<int, List<CashItem>> _flows = [];
    private readonly Dictionary<int, CashflowResult> _results = [];
    private Dictionary<int, List<BufferTransaction>>? _txByPeriod;

    public CashflowCalculator(HouseholdEngine engine) => _engine = engine;

    public bool HasAnchor => _engine.Index.Checkpoints.Count > 0;

    /// <summary>Alle Zahlungsflüsse einer Periode, nach Datum sortiert.</summary>
    public IReadOnlyList<CashItem> Flows(Period p)
    {
        if (_flows.TryGetValue(p.Index, out var cached)) return cached;
        var ix = _engine.Index;
        var month = _engine.Month(p);
        var list = new List<CashItem>();
        foreach (var l in month.Lines)
        {
            if (l.Effective == 0) continue;
            switch (l.Kind)
            {
                case EntryKind.Income:
                    list.Add(Item(l, +l.Effective, "income"));
                    break;
                case EntryKind.Expense:
                    list.Add(Item(l, -l.Effective, "expense"));
                    break;
                case EntryKind.Allocation:
                    if (l.BufferId is { } bid && ix.BufferById.TryGetValue(bid, out var buf) && buf.SeparateAccount)
                        list.Add(Item(l, -l.Effective, "allocation"));
                    break;
            }
        }

        foreach (var t in TransactionsIn(p))
        {
            if (!t.AffectsBudget || !ix.BufferById.TryGetValue(t.BufferId, out var buffer) || !buffer.SeparateAccount) continue;
            if (t.Type == BufferTransactionType.Allocation && t.SourceEntryId is null)
                list.Add(new CashItem { Name = buffer.Name, Amount = -t.Amount, Type = "allocation", Date = t.Date, TransactionId = t.Id, BufferId = t.BufferId, IsDone = true });
            else if (t.Type == BufferTransactionType.Withdrawal)
                list.Add(new CashItem { Name = buffer.Name, Amount = t.Amount, Type = "withdrawal", Date = t.Date, TransactionId = t.Id, BufferId = t.BufferId, IsDone = true });
        }

        list.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : b.Amount.CompareTo(a.Amount));
        _flows[p.Index] = list;
        return list;

        static CashItem Item(EntryLine l, decimal amount, string type) => new()
        {
            Name = l.Name, Amount = amount, Type = type, Date = l.DueDate, EntryId = l.EntryId, CategoryId = l.CategoryId,
            BufferId = l.BufferId, DebtId = l.DebtId, IsDone = l.IsDone, IsFixed = l.IsFixed, HasActual = l.Actual is not null,
        };
    }

    /// <summary>Summe aller Flüsse im Zeitraum [von, bis] (inklusive); volle Perioden über den schnellen Summenpfad.</summary>
    public decimal FlowsBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        if (fromInclusive > toInclusive) return 0;
        var cal = _engine.Calendar;
        decimal sum = 0;
        for (var p = cal.PeriodOf(fromInclusive); p <= cal.PeriodOf(toInclusive); p = p.Next)
        {
            var (start, end) = cal.Range(p);
            if (start >= fromInclusive && end <= toInclusive && !_flows.ContainsKey(p.Index))
            {
                sum += PeriodNet(p);
                continue;
            }
            foreach (var f in Flows(p))
                if (f.Date >= fromInclusive && f.Date <= toInclusive) sum += f.Amount;
        }
        return sum;
    }

    private readonly Dictionary<int, decimal> _net = [];

    /// <summary>Netto-Fluss einer ganzen Periode (gecacht).</summary>
    public decimal PeriodNet(Period p)
    {
        if (!_net.TryGetValue(p.Index, out var net)) _net[p.Index] = net = BudgetCalculator.CashNet(_engine, p);
        return net;
    }

    /// <summary>
    /// Kontostand am Ende eines Tages: letzter Abgleich davor + Flüsse; liegt der erste Abgleich danach,
    /// wird rückwärts gerechnet; ohne Abgleich Start bei 0 € am Haushaltsbeginn (ENTSCHEIDUNGEN §4.4).
    /// </summary>
    public decimal BalanceAtEndOfDay(DateOnly date)
    {
        var cps = _engine.Index.Checkpoints;
        BalanceCheckpoint? before = null, after = null;
        foreach (var c in cps)
        {
            if (c.Date <= date) before = c;
            else { after = c; break; }
        }
        if (before is not null) return before.Amount + FlowsBetween(before.Date.AddDays(1), date);
        if (after is not null) return after.Amount - FlowsBetween(date.AddDays(1), after.Date);
        var start = _engine.Calendar.Start(_engine.FirstPeriod);
        return date < start ? 0 : FlowsBetween(start, date);
    }

    public decimal BalanceToday => BalanceAtEndOfDay(_engine.Today);

    public CashflowResult ForPeriod(Period p)
    {
        if (_results.TryGetValue(p.Index, out var cached)) return cached;
        var cal = _engine.Calendar;
        var (start, end) = cal.Range(p);
        var flows = Flows(p);
        var byDate = flows.GroupBy(f => f.Date).ToDictionary(g => g.Key, g => g.ToList());
        var checkpoints = _engine.Index.Checkpoints.Where(c => c.Date >= start && c.Date <= end)
            .GroupBy(c => c.Date).ToDictionary(g => g.Key, g => g.Last());

        var startBalance = BalanceAtEndOfDay(start.AddDays(-1));
        var balance = startBalance;
        var days = new List<CashDay>(cal.DayCount(p));
        decimal totalIn = 0, totalOut = 0, min = decimal.MaxValue;
        var minDate = start;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var items = byDate.GetValueOrDefault(d) ?? [];
            var din = items.Where(i => i.Amount > 0).Sum(i => i.Amount);
            var dout = items.Where(i => i.Amount < 0).Sum(i => i.Amount);
            totalIn += din;
            totalOut += dout;
            balance += din + dout;
            BalanceCheckpoint? cp = null;
            decimal? diff = null;
            if (checkpoints.TryGetValue(d, out var c))
            {
                cp = c;
                diff = c.Amount - balance;
                balance = c.Amount;
            }
            if (balance < min) { min = balance; minDate = d; }
            days.Add(new CashDay
            {
                Date = d, Items = items, In = din, Out = dout, Balance = balance, Checkpoint = cp, CheckpointDifference = diff,
                IsPast = d < _engine.Today, IsToday = d == _engine.Today,
            });
        }

        var result = new CashflowResult
        {
            Period = p, Start = start, End = end, Days = days, StartBalance = startBalance, EndBalance = balance,
            TotalIn = totalIn, TotalOut = totalOut, MinBalance = days.Count == 0 ? startBalance : min, MinBalanceDate = minDate,
            HasAnchor = HasAnchor,
        };
        _results[p.Index] = result;
        return result;
    }

    private List<BufferTransaction> TransactionsIn(Period p)
    {
        if (_txByPeriod is null)
        {
            _txByPeriod = [];
            foreach (var t in _engine.Index.Transactions)
            {
                var key = _engine.Calendar.PeriodOf(t.Date).Index;
                if (!_txByPeriod.TryGetValue(key, out var list)) _txByPeriod[key] = list = [];
                list.Add(t);
            }
        }
        return _txByPeriod.GetValueOrDefault(p.Index) ?? [];
    }
}
