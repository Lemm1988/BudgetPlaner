using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain;

/// <summary>Eine Position in einer bestimmten Periode.</summary>
public sealed class EntryLine
{
    public required Guid EntryId { get; init; }
    public required string Name { get; init; }
    public required EntryKind Kind { get; init; }
    public required Guid CategoryId { get; init; }
    public Guid? RootCategoryId { get; init; }
    public required BudgetGroup Group { get; init; }
    public Rhythm Rhythm { get; init; }

    /// <summary>Betrag ohne Pause/Aussetzen (für durchgestrichene Anzeige).</summary>
    public decimal Nominal { get; init; }

    /// <summary>Soll (0 bei Pause).</summary>
    public decimal Planned { get; init; }

    /// <summary>Erfasster Ist-Betrag oder <c>null</c>.</summary>
    public decimal? Actual { get; init; }

    /// <summary>Ist ?? Soll – Grundlage aller Summen.</summary>
    public decimal Effective { get; init; }

    public bool IsPaused { get; init; }
    public bool IsSkipped { get; init; }
    public bool IsDone { get; init; }
    public bool IsFixed { get; init; }
    public int? DebitDay { get; init; }
    public DateOnly DueDate { get; init; }
    public Guid? DebtId { get; init; }
    public DebtRole DebtRole { get; init; }
    public Guid? BufferId { get; init; }
    public int SortIndex { get; init; }

    /// <summary>Zeile stammt aus einem Monatsabschluss.</summary>
    public bool FromSnapshot { get; init; }

    public bool IsInactive => IsPaused || IsSkipped;
}

/// <summary>Ergebnis eines Budgetmonats (F-014, F-050).</summary>
public sealed class MonthResult
{
    public required Period Period { get; init; }
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public bool IsClosed { get; init; }
    public MonthSnapshot? Snapshot { get; init; }
    public required IReadOnlyList<EntryLine> Lines { get; init; }

    /// <summary>Übertrag aus dem Abschluss der Vorperiode.</summary>
    public decimal CarryIn { get; init; }

    public decimal Income { get; init; }
    public decimal Expenses { get; init; }

    /// <summary>Zuweisungen aus Sparplan-Positionen.</summary>
    public decimal EntryAllocations { get; init; }

    /// <summary>Manuelle Zuweisungen mit Budgetwirkung.</summary>
    public decimal ManualAllocations { get; init; }

    /// <summary>Entnahmen aus Polstern ins Budget.</summary>
    public decimal Withdrawals { get; init; }

    public decimal PlannedIncome { get; init; }
    public decimal PlannedExpenses { get; init; }
    public decimal PlannedEntryAllocations { get; init; }

    /// <summary>Fixe Ausgaben (F-067).</summary>
    public decimal FixedExpenses { get; init; }

    public decimal Allocations => EntryAllocations + ManualAllocations;

    /// <summary>Überschuss = Einnahmen − Ausgaben (F-050).</summary>
    public decimal Surplus => Income - Expenses;

    /// <summary>Unverplant = Übertrag + Einnahmen − Ausgaben − Polster-Zuweisungen + Entnahmen (F-014).</summary>
    public decimal Unverplant => CarryIn + Income - Expenses - Allocations + Withdrawals;

    /// <summary>Unverplant nur auf Plan-Basis (ohne Ist-Werte).</summary>
    public decimal PlannedUnverplant => CarryIn + PlannedIncome - PlannedExpenses - PlannedEntryAllocations - ManualAllocations + Withdrawals;

    /// <summary>Hat die Periode überhaupt Daten? (Lücken in Statistiken, F-061)</summary>
    public bool HasData => Lines.Count > 0 || ManualAllocations != 0 || Withdrawals != 0 || IsClosed;

    public decimal ExpensesInGroup(BudgetGroup group) =>
        Lines.Where(l => l.Kind == EntryKind.Expense && l.Group == group).Sum(l => l.Effective);

    /// <summary>Sparbeitrag = Ausgaben der Gruppe Sparen + Zuweisungen − Entnahmen (F-063).</summary>
    public decimal SavingsContribution => ExpensesInGroup(BudgetGroup.Savings) + Allocations - Withdrawals;

    /// <summary>Raten verknüpfter Schulden (Schuldendienst).</summary>
    public decimal DebtService => Lines.Where(l => l.Kind == EntryKind.Expense && l.DebtId is not null && l.DebtRole == DebtRole.Rate).Sum(l => l.Effective);
}

/// <summary>Nur die Summen eines Budgetmonats – ohne Zeilenobjekte (schneller Pfad für Zeitreihen).</summary>
public readonly record struct MonthTotals(
    Period Period,
    bool IsClosed,
    bool HasLines,
    decimal CarryIn,
    decimal Income,
    decimal Expenses,
    decimal EntryAllocations,
    decimal ManualAllocations,
    decimal Withdrawals,
    decimal FixedExpenses,
    decimal Needs,
    decimal Wants,
    decimal SavingsExpenses,
    decimal UngroupedExpenses,
    decimal PlannedIncome,
    decimal PlannedExpenses,
    decimal PlannedEntryAllocations)
{
    public decimal Allocations => EntryAllocations + ManualAllocations;
    public decimal Surplus => Income - Expenses;
    public decimal Unverplant => CarryIn + Income - Expenses - Allocations + Withdrawals;
    public decimal SavingsContribution => SavingsExpenses + Allocations - Withdrawals;
    public bool HasData => HasLines || ManualAllocations != 0 || Withdrawals != 0 || IsClosed;

    public static MonthTotals From(MonthResult m)
    {
        decimal needs = 0, wants = 0, savings = 0, none = 0;
        foreach (var l in m.Lines)
        {
            if (l.Kind != EntryKind.Expense) continue;
            switch (l.Group)
            {
                case BudgetGroup.Needs: needs += l.Effective; break;
                case BudgetGroup.Wants: wants += l.Effective; break;
                case BudgetGroup.Savings: savings += l.Effective; break;
                default: none += l.Effective; break;
            }
        }
        return new MonthTotals(m.Period, m.IsClosed, m.Lines.Count > 0, m.CarryIn, m.Income, m.Expenses, m.EntryAllocations,
            m.ManualAllocations, m.Withdrawals, m.FixedExpenses, needs, wants, savings, none, m.PlannedIncome, m.PlannedExpenses,
            m.PlannedEntryAllocations);
    }
}

/// <summary>Berechnet Budgetmonate.</summary>
public static class BudgetCalculator
{
    /// <summary>Summen einer offenen Periode ohne Zeilenobjekte; abgeschlossene/gecachte Perioden über <see cref="MonthResult"/>.</summary>
    public static MonthTotals ComputeTotals(HouseholdEngine engine, Period p)
    {
        var ix = engine.Index;
        if (ix.SnapshotByPeriod.ContainsKey(p.Index) || engine.HasCachedMonth(p)) return MonthTotals.From(engine.Month(p));
        decimal income = 0, expenses = 0, alloc = 0, pIncome = 0, pExpenses = 0, pAlloc = 0, fixedExp = 0, needs = 0, wants = 0, savings = 0, none = 0;
        var hasLines = false;
        foreach (var info in ix.EntryInfos)
        {
            var e = info.Entry;
            if (!EntrySchedule.IsDue(e, p)) continue;
            hasLines = true;
            var st = EntrySchedule.State(info, p);
            var eff = st.Effective;
            switch (e.Kind)
            {
                case EntryKind.Income: income += eff; pIncome += st.Planned; break;
                case EntryKind.Allocation: alloc += eff; pAlloc += st.Planned; break;
                default:
                    expenses += eff; pExpenses += st.Planned;
                    if (e.IsFixed) fixedExp += eff;
                    switch (info.Group)
                    {
                        case BudgetGroup.Needs: needs += eff; break;
                        case BudgetGroup.Wants: wants += eff; break;
                        case BudgetGroup.Savings: savings += eff; break;
                        default: none += eff; break;
                    }
                    break;
            }
        }
        var carryIn = ix.SnapshotByPeriod.TryGetValue(p.Previous.Index, out var prev) && prev.CarryOverBufferId is null ? prev.CarryOver : 0m;
        return new MonthTotals(p, false, hasLines, carryIn, income, expenses, alloc, engine.ManualAllocationsIn(p), engine.WithdrawalsIn(p),
            fixedExp, needs, wants, savings, none, pIncome, pExpenses, pAlloc);
    }

    public static MonthResult Compute(HouseholdEngine engine, Period p)
    {
        var ix = engine.Index;
        var cal = ix.Calendar;
        var (start, end) = cal.Range(p);

        if (ix.SnapshotByPeriod.TryGetValue(p.Index, out var snapshot))
            return FromSnapshot(engine, p, start, end, snapshot);

        var range = cal.RangeOf(p);
        var lines = new List<EntryLine>();
        foreach (var info in ix.EntryInfos)
        {
            var e = info.Entry;
            if (!EntrySchedule.IsDue(e, p)) continue;
            var s = EntrySchedule.State(info, p);
            lines.Add(new EntryLine
            {
                EntryId = e.Id,
                Name = e.Name,
                Kind = e.Kind,
                CategoryId = e.CategoryId,
                RootCategoryId = info.RootId,
                Group = info.Group,
                Rhythm = e.Rhythm,
                Nominal = s.Nominal,
                Planned = s.Planned,
                Actual = s.Actual,
                Effective = s.Effective,
                IsPaused = s.IsPaused,
                IsSkipped = s.IsSkipped,
                IsDone = s.IsDone,
                IsFixed = e.IsFixed,
                DebitDay = e.DebitDay,
                DueDate = PeriodCalendar.DueDate(range, e.DebitDay),
                DebtId = e.DebtId,
                DebtRole = e.DebtRole,
                BufferId = e.BufferId,
                SortIndex = e.SortIndex,
            });
        }

        var carryIn = ix.SnapshotByPeriod.TryGetValue(p.Previous.Index, out var prev) && prev.CarryOverBufferId is null
            ? prev.CarryOver
            : 0m;

        return Build(engine, p, start, end, lines, carryIn,
            engine.ManualAllocationsIn(p), engine.WithdrawalsIn(p), closed: false, snapshot: null);
    }

    private static MonthResult FromSnapshot(HouseholdEngine engine, Period p, DateOnly start, DateOnly end, MonthSnapshot s)
    {
        var ix = engine.Index;
        var cal = ix.Calendar;
        var lines = new List<EntryLine>(s.Lines.Count);
        foreach (var l in s.Lines)
        {
            // „Löschen entfernt Position aus allen Berechnungen“ (F-010) – auch aus abgeschlossenen Monaten.
            if (!ix.EntryById.TryGetValue(l.EntryId, out var entry)) continue;
            lines.Add(new EntryLine
            {
                EntryId = l.EntryId,
                Name = entry.Name,
                Kind = l.Kind,
                CategoryId = l.CategoryId,
                RootCategoryId = ix.RootOf(l.CategoryId)?.Id,
                Group = l.Kind == EntryKind.Allocation ? BudgetGroup.Savings : l.Group,
                Rhythm = l.Rhythm,
                Nominal = l.Planned,
                Planned = l.Planned,
                Actual = l.Actual,
                Effective = l.Actual,
                IsDone = l.IsDone,
                IsFixed = l.IsFixed,
                DebitDay = l.DebitDay,
                DueDate = cal.DueDate(p, l.DebitDay),
                DebtId = l.DebtId,
                DebtRole = l.DebtRole,
                BufferId = l.BufferId,
                SortIndex = entry.SortIndex,
                FromSnapshot = true,
            });
        }
        lines.Sort(static (a, b) => a.SortIndex != b.SortIndex ? a.SortIndex.CompareTo(b.SortIndex) : string.CompareOrdinal(a.Name, b.Name));
        return Build(engine, p, start, end, lines, s.CarryIn, s.ManualAllocations, s.TotalWithdrawals, closed: true, snapshot: s);
    }

    private static MonthResult Build(HouseholdEngine engine, Period p, DateOnly start, DateOnly end, List<EntryLine> lines,
        decimal carryIn, decimal manualAllocations, decimal withdrawals, bool closed, MonthSnapshot? snapshot)
    {
        decimal income = 0, expenses = 0, alloc = 0, pIncome = 0, pExpenses = 0, pAlloc = 0, fixedExp = 0;
        foreach (var l in lines)
        {
            switch (l.Kind)
            {
                case EntryKind.Income: income += l.Effective; pIncome += l.Planned; break;
                case EntryKind.Expense:
                    expenses += l.Effective; pExpenses += l.Planned;
                    if (l.IsFixed) fixedExp += l.Effective;
                    break;
                case EntryKind.Allocation: alloc += l.Effective; pAlloc += l.Planned; break;
            }
        }
        return new MonthResult
        {
            Period = p,
            Start = start,
            End = end,
            IsClosed = closed,
            Snapshot = snapshot,
            Lines = lines,
            CarryIn = carryIn,
            Income = income,
            Expenses = expenses,
            EntryAllocations = alloc,
            ManualAllocations = manualAllocations,
            Withdrawals = withdrawals,
            PlannedIncome = pIncome,
            PlannedExpenses = pExpenses,
            PlannedEntryAllocations = pAlloc,
            FixedExpenses = fixedExp,
        };
    }

    /// <summary>
    /// Netto-Kontofluss einer Periode ohne Zeilenobjekte (schneller Pfad für Kontostand-Vorläufe über viele Monate).
    /// Entspricht der Summe aller Cashflow-Positionen der Periode.
    /// </summary>
    public static decimal CashNet(HouseholdEngine engine, Period p)
    {
        var ix = engine.Index;
        decimal net = 0;
        if (ix.SnapshotByPeriod.ContainsKey(p.Index) || engine.HasCachedMonth(p))
        {
            foreach (var l in engine.Month(p).Lines) net += LineCash(ix, l.Kind, l.BufferId, l.Effective);
        }
        else
        {
            foreach (var info in ix.EntryInfos)
            {
                var e = info.Entry;
                if (!EntrySchedule.IsDue(e, p)) continue;
                net += LineCash(ix, e.Kind, e.BufferId, EntrySchedule.State(info, p).Effective);
            }
        }
        return net + engine.SeparateAccountTransactionNet(p);
    }

    private static decimal LineCash(HouseholdIndex ix, EntryKind kind, Guid? bufferId, decimal effective) => kind switch
    {
        EntryKind.Income => effective,
        EntryKind.Expense => -effective,
        EntryKind.Allocation when bufferId is { } b && ix.BufferById.TryGetValue(b, out var buf) && buf.SeparateAccount => -effective,
        _ => 0,
    };

    /// <summary>Erstellt den Snapshot für einen Monatsabschluss (I-01).</summary>
    public static MonthSnapshot CreateSnapshot(HouseholdEngine engine, Period p, decimal carryOver, Guid? carryOverBufferId,
        decimal? closingBalance, DateTimeOffset now, Guid? userId)
    {
        var m = engine.Month(p);
        if (m.IsClosed) throw new InvalidOperationException("Periode ist bereits abgeschlossen.");
        return new MonthSnapshot
        {
            Id = DeterministicId.ForSnapshot(engine.Data.Household.Id, p),
            HouseholdId = engine.Data.Household.Id,
            Period = p,
            ClosedAt = now,
            ClosedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = userId,
            TotalIncome = m.Income,
            TotalExpenses = m.Expenses,
            TotalAllocations = m.Allocations,
            ManualAllocations = m.ManualAllocations,
            TotalWithdrawals = m.Withdrawals,
            CarryIn = m.CarryIn,
            Unverplant = m.Unverplant,
            CarryOver = carryOver,
            CarryOverBufferId = carryOverBufferId,
            ClosingBalance = closingBalance,
            Lines = m.Lines.Select(l => new SnapshotLine
            {
                EntryId = l.EntryId,
                Name = l.Name,
                Kind = l.Kind,
                CategoryId = l.CategoryId,
                Group = l.Group,
                Rhythm = l.Rhythm,
                Planned = l.Planned,
                Actual = l.Effective,
                IsFixed = l.IsFixed,
                IsDone = l.IsDone,
                DebitDay = l.DebitDay,
                DebtId = l.DebtId,
                DebtRole = l.DebtRole,
                BufferId = l.BufferId,
            }).ToList(),
        };
    }
}
