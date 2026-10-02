using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Tests;

/// <summary>Kompakter Builder für Testhaushalte.</summary>
public sealed class TestHousehold
{
    public static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public TestHousehold(int monthStartDay = 1, Period? firstPeriod = null)
    {
        var (household, categories) = HouseholdFactory.Create(Guid.NewGuid(), "Test", "EUR", monthStartDay, new DateOnly(2026, 1, 1), Now);
        household.FirstPeriod = firstPeriod ?? new Period(2026, 1);
        Data = new HouseholdData { Household = household, Categories = categories };
    }

    public HouseholdData Data { get; }
    public Guid Hid => Data.Household.Id;

    public Category Cat(string key) => Data.Categories.Single(c => c.DefaultKey == key);

    private T Stamp<T>(T e) where T : HouseholdEntity
    {
        e.Id = Guid.NewGuid();
        e.HouseholdId = Hid;
        e.CreatedAt = Now;
        e.UpdatedAt = Now;
        return e;
    }

    public BudgetEntry Entry(string name, EntryKind kind, decimal amount, string category, int? day = null, Period? start = null,
        Rhythm rhythm = Rhythm.Monthly, bool isFixed = false, Period? end = null, Guid? debtId = null, DebtRole role = DebtRole.None,
        Guid? bufferId = null, DateTimeOffset? createdAt = null)
    {
        var e = Stamp(new BudgetEntry
        {
            Name = name, Kind = kind, Amount = amount, CategoryId = Cat(category).Id, DebitDay = day, StartPeriod = start ?? new Period(2026, 1),
            Rhythm = rhythm, IsFixed = isFixed, EndPeriod = end, DebtId = debtId, DebtRole = role, BufferId = bufferId,
        });
        if (createdAt is { } c) e.CreatedAt = c;
        Data.Entries.Add(e);
        Data.Invalidate();
        return e;
    }

    public BudgetEntry Income(string name, decimal amount, int? day = null, Period? start = null, Rhythm rhythm = Rhythm.Monthly) =>
        Entry(name, EntryKind.Income, amount, "salary", day, start, rhythm, true);

    public BudgetEntry Expense(string name, decimal amount, string category = "living", int? day = null, Period? start = null,
        Rhythm rhythm = Rhythm.Monthly, bool isFixed = false, Period? end = null) =>
        Entry(name, EntryKind.Expense, amount, category, day, start, rhythm, isFixed, end);

    public BufferEntry Buffer(string name, decimal? target = null, bool separate = true, bool emergency = false, DateTimeOffset? createdAt = null)
    {
        var b = Stamp(new BufferEntry { Name = name, TargetAmount = target, SeparateAccount = separate, IsEmergencyFund = emergency });
        if (createdAt is { } c) b.CreatedAt = c;
        Data.Buffers.Add(b);
        Data.Invalidate();
        return b;
    }

    public BudgetEntry Allocation(BufferEntry b, decimal amount, int? day = null, Period? start = null, DateTimeOffset? createdAt = null) =>
        Entry(b.Name, EntryKind.Allocation, amount, "saving", day, start, bufferId: b.Id, createdAt: createdAt);

    public BufferTransaction Tx(BufferEntry b, DateOnly date, decimal amount, BufferTransactionType type = BufferTransactionType.Allocation,
        bool affectsBudget = true, string? reason = null)
    {
        var t = Stamp(new BufferTransaction { BufferId = b.Id, Date = date, Amount = amount, Type = type, AffectsBudget = affectsBudget, Reason = reason });
        Data.BufferTransactions.Add(t);
        Data.Invalidate();
        return t;
    }

    public DebtEntry Debt(string name, decimal balance, decimal? rate = null, decimal? monthly = null, int? day = null, DateOnly? start = null,
        decimal? original = null, bool autoBook = true, DebtType type = DebtType.Loan)
    {
        var d = Stamp(new DebtEntry
        {
            Name = name, OpeningBalance = balance, OriginalAmount = original ?? balance, InterestRate = rate, MonthlyRate = monthly,
            DebitDay = day, StartDate = start ?? new DateOnly(2025, 12, 31), AutoBook = autoBook, Type = type,
        });
        Data.Debts.Add(d);
        Data.Invalidate();
        return d;
    }

    public DebtPayment Payment(DebtEntry d, DateOnly date, decimal amount, bool extra = false)
    {
        var p = Stamp(new DebtPayment { DebtId = d.Id, Date = date, Amount = amount, IsExtra = extra, InterestRateApplied = d.InterestRate });
        Data.DebtPayments.Add(p);
        Data.Invalidate();
        return p;
    }

    public BalanceCheckpoint Checkpoint(DateOnly date, decimal amount)
    {
        var c = Stamp(new BalanceCheckpoint { Date = date, Amount = amount });
        Data.Checkpoints.Add(c);
        Data.Invalidate();
        return c;
    }

    public BudgetEntryMonth Month(BudgetEntry e, Period p, decimal? actual = null, bool done = false, decimal? planned = null, bool skipped = false)
    {
        var m = Stamp(new BudgetEntryMonth { EntryId = e.Id, Period = p, ActualAmount = actual, IsDone = done, PlannedAmount = planned, IsSkipped = skipped });
        Data.EntryMonths.Add(m);
        Data.Invalidate();
        return m;
    }

    public BudgetEntryChange Change(BudgetEntry e, Period from, decimal amount, bool paused = false)
    {
        var c = Stamp(new BudgetEntryChange { EntryId = e.Id, FromPeriod = from, Amount = amount, IsPaused = paused });
        Data.EntryChanges.Add(c);
        Data.Invalidate();
        return c;
    }

    public HouseholdEngine Engine(DateOnly today)
    {
        Data.Invalidate();
        return new HouseholdEngine(Data, today);
    }
}
