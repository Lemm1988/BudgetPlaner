using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Defaults;

/// <summary>
/// Beispieldaten für alle Module (F-142): Familie mit zwei Einkommen, 12 Monaten Historie, Ist-Werten,
/// Monatsabschlüssen, Krediten, Polstern und Kontostand-Abgleich. Alle Datensätze tragen <c>IsDemo = true</c>.
/// </summary>
public static class DemoData
{
    public sealed class DemoSet
    {
        public List<BudgetEntry> Entries { get; } = [];
        public List<BudgetEntryChange> Changes { get; } = [];
        public List<BudgetEntryMonth> Months { get; } = [];
        public List<DebtEntry> Debts { get; } = [];
        public List<DebtPayment> Payments { get; } = [];
        public List<BufferEntry> Buffers { get; } = [];
        public List<BufferTransaction> Transactions { get; } = [];
        public List<MonthSnapshot> Snapshots { get; } = [];
        public List<BalanceCheckpoint> Checkpoints { get; } = [];

        public IEnumerable<HouseholdEntity> All =>
            Entries.Cast<HouseholdEntity>().Concat(Changes).Concat(Months).Concat(Debts).Concat(Payments)
                .Concat(Buffers).Concat(Transactions).Concat(Snapshots).Concat(Checkpoints);
    }

    /// <summary>Texte der Beispieldaten (Schlüssel „demo.*“ in den Sprachdateien) und Monatsnamen.</summary>
    public sealed record DemoTexts(Func<string, string> Text, Func<Period, string> PeriodName)
    {
        /// <summary>Ohne Übersetzung: Schlüssel als Text (Tests).</summary>
        public static readonly DemoTexts Keys = new(k => k, p => p.ToString());
    }

    public static DemoSet Create(HouseholdData data, DateOnly today, DateTimeOffset now, Guid? userId, DemoTexts? texts = null)
    {
        texts ??= DemoTexts.Keys;
        string T(string key) => texts.Text(key);
        var ix = data.Index;
        var cal = ix.Calendar;
        var hid = data.Household.Id;
        var current = cal.PeriodOf(today);
        var first = current.AddMonths(-11);
        var historyStart = cal.Start(first);
        var created = new DateTimeOffset(historyStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var set = new DemoSet();
        var sort = 1000;

        Category Cat(string key) =>
            ix.Categories.FirstOrDefault(c => c.DefaultKey == key)
            ?? ix.Categories.FirstOrDefault(c => c.DefaultKey == DefaultCategories.Other)
            ?? ix.Categories.First();

        T Stamp<T>(T e) where T : HouseholdEntity
        {
            e.Id = Guid.NewGuid();
            e.HouseholdId = hid;
            e.CreatedAt = created;
            e.UpdatedAt = now;
            e.UpdatedBy = userId;
            e.IsDemo = true;
            return e;
        }

        BudgetEntry E(string name, EntryKind kind, string catKey, decimal amount, int? day, bool isFixed, Rhythm rhythm = Rhythm.Monthly,
            Period? start = null, DebtEntry? debt = null, DebtRole role = DebtRole.None, BufferEntry? buffer = null)
        {
            var e = Stamp(new BudgetEntry
            {
                Name = name, Kind = kind, CategoryId = Cat(catKey).Id, Amount = amount, DebitDay = day, IsFixed = isFixed,
                Rhythm = rhythm, StartPeriod = start ?? first, SortIndex = sort++, DebtId = debt?.Id, DebtRole = role,
                BufferId = buffer?.Id, CreatedBy = userId,
            });
            set.Entries.Add(e);
            return e;
        }

        // Einnahmen
        var salaryA = E(T("salaryA"), EntryKind.Income, "salary", 3150, 1, true);
        E(T("salaryB"), EntryKind.Income, "salary", 1850, 1, true);
        E(T("childBenefit"), EntryKind.Income, "benefits", 255, 10, true);
        set.Changes.Add(Keyed(Stamp(new BudgetEntryChange { EntryId = salaryA.Id, FromPeriod = current.AddMonths(-3), Amount = 3270 })));

        // Bedarf
        E(T("rent"), EntryKind.Expense, "housing", 1380, 3, true);
        E(T("power"), EntryKind.Expense, "housing", 92, 15, true);
        E(T("internet"), EntryKind.Expense, "housing", 45, 5, true);
        E(T("contents"), EntryKind.Expense, "housing", 96, 1, true, Rhythm.Yearly, current.AddMonths(-7));
        E(T("fuel"), EntryKind.Expense, "mobility", 160, null, false);
        E(T("ticket"), EntryKind.Expense, "mobility", 58, 1, true);
        E(T("carInsurance"), EntryKind.Expense, "mobility", 486, 1, true, Rhythm.Yearly, current.AddMonths(-9));
        E(T("groceries"), EntryKind.Expense, "living", 780, null, false);
        E(T("drugstore"), EntryKind.Expense, "living", 85, null, false);
        E(T("phones"), EntryKind.Expense, "living", 44, 20, true);
        E(T("daycare"), EntryKind.Expense, "living", 330, 1, true);
        E(T("clothing"), EntryKind.Expense, "living", 120, null, false);
        E(T("insurance"), EntryKind.Expense, "living", 85, 1, true);

        // Wunsch
        E(T("dining"), EntryKind.Expense, "guiltfree", 150, null, false);
        var streaming = E(T("streaming"), EntryKind.Expense, "guiltfree", 23, 12, true);
        set.Changes.Add(Keyed(Stamp(new BudgetEntryChange { EntryId = streaming.Id, FromPeriod = current.AddMonths(-6), Amount = 27 })));
        var hobbies = E(T("hobbies"), EntryKind.Expense, "guiltfree", 65, 1, true);
        E(T("pocketMoney"), EntryKind.Expense, "guiltfree", 200, 1, false);
        E(T("donation"), EntryKind.Expense, "giving", 25, 1, true);
        E(T("gifts"), EntryKind.Expense, "giving", 40, null, false);

        // Sparen & Schulden
        E(T("etf"), EntryKind.Expense, "investing", 250, 2, true);
        E(T("pension"), EntryKind.Expense, "investing", 150, 1, true);
        var debtStart = historyStart.AddDays(-1);
        var car = Stamp(new DebtEntry
        {
            Name = T("carLoan"), Creditor = T("bank"), Type = DebtType.Loan, OriginalAmount = 15000, OpeningBalance = 9800,
            StartDate = debtStart, InterestRate = 4.9m, MonthlyRate = 289, DebitDay = 15, TermMonths = 38, AutoBook = true, SortIndex = 1,
        });
        var parents = Stamp(new DebtEntry
        {
            Name = T("parentsLoan"), Creditor = T("parents"), Type = DebtType.Private, OriginalAmount = 2000, OpeningBalance = 1400,
            StartDate = debtStart, MonthlyRate = 100, DebitDay = 20, AutoBook = true, SortIndex = 2,
        });
        set.Debts.AddRange([car, parents]);
        E(T("carLoanRate"), EntryKind.Expense, "mobility", 289, 15, true, debt: car, role: DebtRole.Rate);
        E(T("carLoanExtra"), EntryKind.Expense, "debt-payoff", 50, 15, false, start: current.AddMonths(-4), debt: car, role: DebtRole.Extra);
        E(T("parentsRate"), EntryKind.Expense, DefaultCategories.Needs, 100, 20, true, debt: parents, role: DebtRole.Rate);

        // Polster
        var emergency = Stamp(new BufferEntry { Name = T("emergency"), TargetAmount = 15000, IsEmergencyFund = true, SortIndex = 1, Color = "#2E7D5B" });
        var vacation = Stamp(new BufferEntry { Name = T("vacation"), TargetAmount = 2400, TargetDate = cal.End(current.AddMonths(8)), SortIndex = 2, Color = "#3A7CA5" });
        var repairs = Stamp(new BufferEntry { Name = T("repairs"), TargetAmount = 1500, SortIndex = 3, Color = "#B5651D" });
        set.Buffers.AddRange([emergency, vacation, repairs]);
        E(T("emergency"), EntryKind.Allocation, "saving", 400, 2, true, buffer: emergency);
        E(T("vacationPlan"), EntryKind.Allocation, "saving", 200, 2, true, buffer: vacation);
        E(T("repairsPlan"), EntryKind.Allocation, "saving", 100, 2, true, buffer: repairs);

        BufferTransaction Tx(BufferEntry b, DateOnly date, decimal amount, BufferTransactionType type, string reason, bool affects) =>
            Stamp(new BufferTransaction { BufferId = b.Id, Date = date, Amount = amount, Type = type, Reason = reason, AffectsBudget = affects });
        set.Transactions.Add(Tx(emergency, historyStart, 4200, BufferTransactionType.Allocation, T("opening"), false));
        set.Transactions.Add(Tx(vacation, historyStart, 300, BufferTransactionType.Allocation, T("opening"), false));
        set.Transactions.Add(Tx(repairs, historyStart, 650, BufferTransactionType.Allocation, T("opening"), false));
        set.Transactions.Add(Tx(vacation, cal.Start(current.AddMonths(-5)).AddDays(10), 600, BufferTransactionType.Withdrawal, T("whitsun"), false));
        set.Transactions.Add(Tx(repairs, cal.Start(current.AddMonths(-2)).AddDays(6), 280, BufferTransactionType.Withdrawal, T("inspection"), true));

        // Monatswerte: Ist-Werte für variable Positionen, abgehakt, eine Pause
        var seed = 0;
        foreach (var e in set.Entries)
        {
            seed++;
            foreach (var p in EntrySchedule.DuePeriods(e, first, current))
            {
                var due = cal.DueDate(p, e.DebitDay);
                // Variable Positionen ohne Abbuchungstag laufen bis Monatsende – im laufenden Monat noch offen.
                var done = p < current || (e.DebitDay is not null && due <= today);
                decimal? actual = null;
                if (!e.IsFixed && p < current && e.Kind == EntryKind.Expense)
                {
                    // Pseudozufällige Abweichung −20 … +20 % (64-Bit, sonst Überlauf bei großen Periodenindizes)
                    var jitter = ((int)(((long)seed * 7919 + (long)p.Index * 104729) % 41) - 20) / 100m;
                    actual = MoneyMath.Round2(e.Amount * (1 + jitter));
                }
                var skip = e.Id == hobbies.Id && p == current.AddMonths(-4);
                if (!done && actual is null && !skip) continue;
                var month = Stamp(new BudgetEntryMonth
                {
                    EntryId = e.Id, Period = p, ActualAmount = actual, IsDone = done && !skip, IsSkipped = skip,
                    DoneAt = done ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                });
                month.Id = DeterministicId.ForEntryMonth(e.Id, p);
                set.Months.Add(month);
            }
        }

        // Kontostand-Abgleich vor Beginn der Historie
        set.Checkpoints.Add(Stamp(new BalanceCheckpoint { Date = historyStart.AddDays(-1), Amount = 1850, Note = T("statement") }));

        var combined = Combine(data, set);

        // Monatsabschlüsse (nur wenn noch keine eigenen Positionen existieren, damit Nutzerdaten nicht eingefroren werden)
        if (!ix.Entries.Any(e => !e.IsDemo))
        {
            for (var p = first; p <= current.AddMonths(-2); p = p.Next)
            {
                var engine = new HouseholdEngine(combined, today);
                var leftover = engine.Month(p).Unverplant;
                Guid? bufferId = null;
                if (leftover > 0)
                {
                    set.Transactions.Add(Tx(emergency, cal.End(p), MoneyMath.Round2(leftover), BufferTransactionType.Allocation, string.Format(T("monthClose"), texts.PeriodName(p)), true));
                    combined = Combine(data, set);
                    engine = new HouseholdEngine(combined, today);
                    bufferId = emergency.Id;
                }
                var closedAt = new DateTimeOffset(cal.End(p).AddDays(1).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
                var snapshot = BudgetCalculator.CreateSnapshot(engine, p, leftover > 0 ? 0 : leftover, bufferId, null, closedAt, userId);
                snapshot.CreatedAt = closedAt;
                snapshot.IsDemo = true;
                set.Snapshots.Add(snapshot);
                combined = Combine(data, set);
            }
        }

        // Fällige Raten und Sparpläne verbuchen
        var final = new HouseholdEngine(combined, today);
        var booked = BookingMaterializer.Materialize(final, now, userId);
        foreach (var p in booked.Payments) { p.IsDemo = true; p.CreatedAt = created; set.Payments.Add(p); }
        foreach (var t in booked.Transactions) { t.IsDemo = true; t.CreatedAt = created; set.Transactions.Add(t); }

        // Aktueller Abgleich mit kleiner Differenz (zeigt die Abgleich-Funktion)
        combined = Combine(data, set);
        var balanceEngine = new HouseholdEngine(combined, today);
        var checkDate = today.AddDays(-3) < cal.Start(current) ? cal.Start(current) : today.AddDays(-3);
        if (checkDate <= today)
        {
            var computed = balanceEngine.Cashflow.BalanceAtEndOfDay(checkDate);
            set.Checkpoints.Add(Stamp(new BalanceCheckpoint { Date = checkDate, Amount = MoneyMath.Round2(computed - 23.40m), Note = T("onlineBanking") }));
        }
        return set;
    }

    private static BudgetEntryChange Keyed(BudgetEntryChange c)
    {
        c.Id = DeterministicId.ForEntryChange(c.EntryId, c.FromPeriod);
        return c;
    }

    private static HouseholdData Combine(HouseholdData data, DemoSet set)
    {
        var c = data.ShallowCopy();
        c.Entries.AddRange(set.Entries);
        c.EntryChanges.AddRange(set.Changes);
        c.EntryMonths.AddRange(set.Months);
        c.Debts.AddRange(set.Debts);
        c.DebtPayments.AddRange(set.Payments);
        c.Buffers.AddRange(set.Buffers);
        c.BufferTransactions.AddRange(set.Transactions);
        c.Snapshots.AddRange(set.Snapshots);
        c.Checkpoints.AddRange(set.Checkpoints);
        c.Invalidate();
        return c;
    }
}
