using System.Text.Json.Serialization;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Scenarios;

/// <summary>„Was-wäre-wenn“-Szenario (F-075): eine Liste hypothetischer Änderungen, die nur auf einer Kopie wirken.</summary>
public sealed class Scenario
{
    public string Name { get; set; } = "";
    public List<ScenarioChange> Changes { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ChangeEntryAmount), "entryAmount")]
[JsonDerivedType(typeof(PauseEntry), "pauseEntry")]
[JsonDerivedType(typeof(EndEntry), "endEntry")]
[JsonDerivedType(typeof(AddEntry), "addEntry")]
[JsonDerivedType(typeof(ScaleIncome), "scaleIncome")]
[JsonDerivedType(typeof(ScaleExpenses), "scaleExpenses")]
[JsonDerivedType(typeof(ChangeDebtRate), "debtRate")]
[JsonDerivedType(typeof(AddDebtExtra), "debtExtra")]
[JsonDerivedType(typeof(ChangeBufferPlan), "bufferPlan")]
public abstract record ScenarioChange
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// <summary>Betrag einer Position ab Periode ändern.</summary>
public sealed record ChangeEntryAmount(Guid EntryId, decimal Amount, Period From) : ScenarioChange;

/// <summary>Position pausieren (optional bis einschließlich <paramref name="To"/>).</summary>
public sealed record PauseEntry(Guid EntryId, Period From, Period? To) : ScenarioChange;

/// <summary>Position ab Periode streichen.</summary>
public sealed record EndEntry(Guid EntryId, Period From) : ScenarioChange;

/// <summary>Neue (hypothetische) Position.</summary>
public sealed record AddEntry(string Name, EntryKind Kind, Guid CategoryId, decimal Amount, Rhythm Rhythm, Period Start, Period? End,
    int? DebitDay, bool IsFixed, Guid? BufferId = null) : ScenarioChange;

/// <summary>Alle Einnahmen ab Periode um Prozent ändern (z. B. Gehaltserhöhung, Elternzeit).</summary>
public sealed record ScaleIncome(decimal Percent, Period From) : ScenarioChange;

/// <summary>Ausgaben (optional nur einer Kategorie inkl. Unterkategorien) ab Periode um Prozent ändern (z. B. Inflation).</summary>
public sealed record ScaleExpenses(decimal Percent, Period From, Guid? CategoryId) : ScenarioChange;

/// <summary>Kreditrate ab Periode ändern.</summary>
public sealed record ChangeDebtRate(Guid DebtId, decimal NewRate, Period From) : ScenarioChange;

/// <summary>Monatliche Zusatztilgung ergänzen.</summary>
public sealed record AddDebtExtra(Guid DebtId, decimal MonthlyExtra, Period From, Period? To) : ScenarioChange;

/// <summary>Sparplan eines Polsters ab Periode ändern.</summary>
public sealed record ChangeBufferPlan(Guid BufferId, decimal MonthlyAmount, Period From) : ScenarioChange;

/// <summary>Wendet Szenarien auf eine Kopie der Haushaltsdaten an – die Originaldaten bleiben unverändert (F-075).</summary>
public static class ScenarioApplier
{
    public static HouseholdData Apply(HouseholdData original, Scenario scenario, DateTimeOffset now)
    {
        var copy = original.ShallowCopy();
        var hid = original.Household.Id;
        foreach (var change in scenario.Changes) ApplyChange(copy, change, hid, now);
        copy.Invalidate();
        return copy;
    }

    private static void ApplyChange(HouseholdData d, ScenarioChange change, Guid hid, DateTimeOffset now)
    {
        switch (change)
        {
            case ChangeEntryAmount c:
                SetAmountFrom(d, c.EntryId, c.Amount, c.From, hid, now);
                break;

            case PauseEntry c:
            {
                var ix = d.Index;
                if (!ix.EntryById.TryGetValue(c.EntryId, out var e)) return;
                var resume = c.To?.Next;
                var amountAtResume = resume is { } r ? EntrySchedule.ScheduledAmount(ix, e, r).Amount : 0;
                var amountAtFrom = EntrySchedule.ScheduledAmount(ix, e, c.From).Amount;
                d.EntryChanges.RemoveAll(x => x.EntryId == c.EntryId && x.FromPeriod >= c.From && (resume is null || x.FromPeriod < resume.Value));
                d.EntryChanges.Add(NewChange(hid, now, c.EntryId, c.From, amountAtFrom, true));
                if (resume is { } rr && !d.EntryChanges.Any(x => x.EntryId == c.EntryId && x.FromPeriod == rr))
                    d.EntryChanges.Add(NewChange(hid, now, c.EntryId, rr, amountAtResume, false));
                d.Invalidate();
                break;
            }

            case EndEntry c:
            {
                var idx = d.Entries.FindIndex(x => x.Id == c.EntryId);
                if (idx < 0) return;
                var clone = d.Entries[idx].CloneAs<BudgetEntry>();
                if (c.From <= clone.StartPeriod) d.Entries.RemoveAt(idx);
                else
                {
                    var end = c.From.Previous;
                    clone.EndPeriod = clone.EndPeriod is { } old && old < end ? old : end;
                    d.Entries[idx] = clone;
                }
                d.Invalidate();
                break;
            }

            case AddEntry c:
                d.Entries.Add(new BudgetEntry
                {
                    Id = Guid.NewGuid(), HouseholdId = hid, CreatedAt = now, UpdatedAt = now,
                    Name = c.Name, Kind = c.Kind, CategoryId = c.CategoryId, Amount = c.Amount, Rhythm = c.Rhythm,
                    StartPeriod = c.Start, EndPeriod = c.End, DebitDay = c.DebitDay, IsFixed = c.IsFixed, BufferId = c.BufferId,
                    SortIndex = int.MaxValue / 2,
                });
                d.Invalidate();
                break;

            case ScaleIncome c:
                foreach (var e in d.Index.Entries.Where(x => x.Kind == EntryKind.Income).ToList())
                    Scale(d, e, c.Percent, c.From, hid, now);
                break;

            case ScaleExpenses c:
            {
                var ix = d.Index;
                foreach (var e in ix.Entries.Where(x => x.Kind == EntryKind.Expense && (c.CategoryId is null || InSubtree(ix, x.CategoryId, c.CategoryId.Value))).ToList())
                    Scale(d, e, c.Percent, c.From, hid, now);
                break;
            }

            case ChangeDebtRate c:
            {
                var ix = d.Index;
                var rates = (ix.EntriesByDebt.GetValueOrDefault(c.DebtId) ?? []).Where(e => e.DebtRole == DebtRole.Rate).ToList();
                if (rates.Count > 0)
                {
                    SetAmountFrom(d, rates[0].Id, c.NewRate, c.From, hid, now);
                    foreach (var other in rates.Skip(1)) SetAmountFrom(d, other.Id, 0, c.From, hid, now);
                }
                var di = d.Debts.FindIndex(x => x.Id == c.DebtId);
                if (di >= 0)
                {
                    var clone = d.Debts[di].CloneAs<DebtEntry>();
                    clone.MonthlyRate = c.NewRate;
                    d.Debts[di] = clone;
                }
                d.Invalidate();
                break;
            }

            case AddDebtExtra c:
            {
                var ix = d.Index;
                var category = ix.Categories.FirstOrDefault(x => x.DefaultKey == "debt-payoff")
                               ?? ix.Categories.FirstOrDefault(x => x.ParentId is null && x.Group == BudgetGroup.Savings);
                if (category is null || !ix.DebtById.TryGetValue(c.DebtId, out var debt)) return;
                d.Entries.Add(new BudgetEntry
                {
                    Id = Guid.NewGuid(), HouseholdId = hid, CreatedAt = now, UpdatedAt = now,
                    Name = $"Zusatztilgung {debt.Name}", Kind = EntryKind.Expense, CategoryId = category.Id, Amount = c.MonthlyExtra,
                    Rhythm = Rhythm.Monthly, StartPeriod = c.From, EndPeriod = c.To, DebitDay = debt.DebitDay,
                    DebtId = c.DebtId, DebtRole = DebtRole.Extra, SortIndex = int.MaxValue / 2,
                });
                d.Invalidate();
                break;
            }

            case ChangeBufferPlan c:
            {
                var ix = d.Index;
                var plans = (ix.EntriesByBuffer.GetValueOrDefault(c.BufferId) ?? []).ToList();
                if (plans.Count > 0)
                {
                    SetAmountFrom(d, plans[0].Id, c.MonthlyAmount, c.From, hid, now);
                    foreach (var other in plans.Skip(1)) SetAmountFrom(d, other.Id, 0, c.From, hid, now);
                }
                else
                {
                    var category = ix.Categories.FirstOrDefault(x => x.DefaultKey == "saving")
                                   ?? ix.Categories.FirstOrDefault(x => x.ParentId is null && x.Group == BudgetGroup.Savings);
                    if (category is null || !ix.BufferById.TryGetValue(c.BufferId, out var buffer)) return;
                    d.Entries.Add(new BudgetEntry
                    {
                        Id = Guid.NewGuid(), HouseholdId = hid, CreatedAt = now, UpdatedAt = now,
                        Name = buffer.Name, Kind = EntryKind.Allocation, CategoryId = category.Id, Amount = c.MonthlyAmount,
                        Rhythm = Rhythm.Monthly, StartPeriod = c.From, BufferId = c.BufferId, SortIndex = int.MaxValue / 2,
                    });
                    d.Invalidate();
                }
                break;
            }
        }
    }

    /// <summary>Setzt den Betrag ab einer Periode („ab diesem Monat“) und entfernt spätere Änderungen.</summary>
    private static void SetAmountFrom(HouseholdData d, Guid entryId, decimal amount, Period from, Guid hid, DateTimeOffset now)
    {
        var ix = d.Index;
        if (!ix.EntryById.TryGetValue(entryId, out var e)) return;
        d.EntryChanges.RemoveAll(x => x.EntryId == entryId && x.FromPeriod >= from);
        if (from <= e.StartPeriod)
        {
            var idx = d.Entries.FindIndex(x => x.Id == entryId);
            var clone = e.CloneAs<BudgetEntry>();
            clone.Amount = amount;
            d.Entries[idx] = clone;
        }
        else d.EntryChanges.Add(NewChange(hid, now, entryId, from, amount, false));
        d.EntryMonths.RemoveAll(x => x.EntryId == entryId && x.Period >= from && x.PlannedAmount is not null && x.ActualAmount is null && !x.IsDone);
        d.Invalidate();
    }

    private static void Scale(HouseholdData d, BudgetEntry e, decimal percent, Period from, Guid hid, DateTimeOffset now)
    {
        var factor = 1 + percent / 100m;
        var ix = d.Index;
        var (baseAmount, paused) = EntrySchedule.ScheduledAmount(ix, e, from);
        var later = d.EntryChanges.Where(x => x.EntryId == e.Id && x.FromPeriod > from).ToList();
        d.EntryChanges.RemoveAll(x => x.EntryId == e.Id && x.FromPeriod >= from);
        if (from <= e.StartPeriod)
        {
            var idx = d.Entries.FindIndex(x => x.Id == e.Id);
            var clone = e.CloneAs<BudgetEntry>();
            clone.Amount = MoneyMath.Round2(e.Amount * factor);
            d.Entries[idx] = clone;
        }
        else d.EntryChanges.Add(NewChange(hid, now, e.Id, from, MoneyMath.Round2(baseAmount * factor), paused));
        foreach (var l in later)
        {
            var c = l.CloneAs<BudgetEntryChange>();
            c.Amount = MoneyMath.Round2(l.Amount * factor);
            d.EntryChanges.Add(c);
        }
        d.Invalidate();
    }

    private static BudgetEntryChange NewChange(Guid hid, DateTimeOffset now, Guid entryId, Period from, decimal amount, bool paused) => new()
    {
        Id = Guid.NewGuid(), HouseholdId = hid, CreatedAt = now, UpdatedAt = now, EntryId = entryId, FromPeriod = from, Amount = amount, IsPaused = paused,
    };

    private static bool InSubtree(HouseholdIndex ix, Guid categoryId, Guid rootId)
    {
        var current = categoryId;
        for (var i = 0; i < 16; i++)
        {
            if (current == rootId) return true;
            if (!ix.CategoryById.TryGetValue(current, out var c) || c.ParentId is not { } p) return false;
            current = p;
        }
        return false;
    }

    /// <summary>
    /// Unterschied zwischen Original und Szenario-Kopie als Upserts/Löschungen – Grundlage für „Szenario übernehmen“.
    /// </summary>
    public static (IReadOnlyList<HouseholdEntity> Upserts, IReadOnlyList<HouseholdEntity> Deletes) Diff(HouseholdData original, HouseholdData scenario)
    {
        var originalSet = new HashSet<HouseholdEntity>(original.AllEntities(), ReferenceEqualityComparer.Instance);
        var scenarioSet = new HashSet<HouseholdEntity>(scenario.AllEntities(), ReferenceEqualityComparer.Instance);
        var upserts = scenario.AllEntities().Where(e => !originalSet.Contains(e)).ToList();
        var upsertIds = upserts.Select(u => u.Id).ToHashSet();
        var deletes = original.AllEntities().Where(e => !scenarioSet.Contains(e) && !upsertIds.Contains(e.Id) && !e.IsDeleted).ToList();
        return (upserts, deletes);
    }
}
