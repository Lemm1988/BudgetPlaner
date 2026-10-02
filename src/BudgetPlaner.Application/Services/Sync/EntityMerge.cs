using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Application.Services.Sync;

/// <summary>
/// Feldweiser 3-Wege-Merge (F-131, I-13): Ein Feld wird übernommen, wenn der Client es gegenüber seiner Basis geändert hat.
/// Hat der Server dasselbe Feld inzwischen anders geändert, liegt ein Konflikt vor – die spätere Änderung gewinnt,
/// der überschriebene Wert bleibt im Änderungsverlauf erhalten.
/// </summary>
public static class EntityMerge
{
    private static readonly HashSet<string> Meta =
    [
        nameof(HouseholdEntity.Id), nameof(HouseholdEntity.HouseholdId), nameof(HouseholdEntity.Version),
        nameof(HouseholdEntity.CreatedAt), nameof(HouseholdEntity.UpdatedAt), nameof(HouseholdEntity.UpdatedBy),
        nameof(HouseholdEntity.IsDeleted), nameof(HouseholdEntity.IsDemo),
        nameof(Household.ChangeSeq), nameof(Household.PurgedSeq), nameof(Household.CurrentMonthStartDay),
    ];

    /// <summary>Felder, die nur der Server setzt.</summary>
    private static readonly Dictionary<Type, HashSet<string>> ServerOwned = new()
    {
        [typeof(Category)] = [nameof(Category.IsSystem), nameof(Category.DefaultKey)],
        [typeof(DebtPayment)] = [nameof(DebtPayment.InterestPart), nameof(DebtPayment.PrincipalPart)],
        [typeof(Household)] = [nameof(Household.FirstPeriod), nameof(Household.Version), nameof(Household.CreatedAt), nameof(Household.UpdatedAt), nameof(Household.UpdatedBy)],
        [typeof(MonthSnapshot)] =
        [
            nameof(MonthSnapshot.TotalIncome), nameof(MonthSnapshot.TotalExpenses), nameof(MonthSnapshot.TotalAllocations),
            nameof(MonthSnapshot.ManualAllocations), nameof(MonthSnapshot.TotalWithdrawals), nameof(MonthSnapshot.CarryIn),
            nameof(MonthSnapshot.Unverplant), nameof(MonthSnapshot.Lines), nameof(MonthSnapshot.ClosedAt), nameof(MonthSnapshot.ClosedBy),
        ],
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    public static PropertyInfo[] MergeableProperties(Type type) => Props.GetOrAdd(type, t =>
    {
        var owned = ServerOwned.GetValueOrDefault(t) ?? [];
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => !Meta.Contains(p.Name) && !owned.Contains(p.Name))
            .ToArray();
    });

    public sealed record MergeResult(bool Changed, bool Conflict, IReadOnlyList<string> ChangedFields);

    /// <summary>Übernimmt die Client-Änderungen in <paramref name="target"/> (Kopie des Serverstands).</summary>
    public static MergeResult Merge(object target, object incoming, object? baseline)
    {
        var changed = new List<string>();
        var conflict = false;
        foreach (var p in MergeableProperties(target.GetType()))
        {
            var newVal = p.GetValue(incoming);
            if (baseline is not null && ValueEquals(newVal, p.GetValue(baseline))) continue;
            var cur = p.GetValue(target);
            if (ValueEquals(newVal, cur)) continue;
            if (baseline is not null && !ValueEquals(cur, p.GetValue(baseline))) conflict = true;
            p.SetValue(target, newVal);
            changed.Add(p.Name);
        }
        return new MergeResult(changed.Count > 0, conflict, changed);
    }

    public static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is decimal da && b is decimal db) return da == db;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        var t = a.GetType();
        if (t.IsPrimitive || t.IsEnum || a is Guid || a is DateOnly || a is DateTimeOffset || a is DateTime || a is Period) return a.Equals(b);
        // Komplexe Werte (Listen, Regeln) über ihre JSON-Darstellung vergleichen.
        return JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options);
    }

    public static object Clone(object entity) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(entity, entity.GetType(), JsonDefaults.Options), entity.GetType(), JsonDefaults.Options)!;

    /// <summary>Kopiert Server-Felder (Meta + serverseitige) aus <paramref name="source"/> nach <paramref name="target"/>.</summary>
    public static void CopyServerFields(object source, object target)
    {
        var mergeable = MergeableProperties(target.GetType()).Select(p => p.Name).ToHashSet();
        foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !mergeable.Contains(p.Name))
                p.SetValue(target, p.GetValue(source));
    }

    /// <summary>Lesbare Bezeichnung für Verlauf und Konfliktmeldung.</summary>
    public static string LabelOf(object entity) => entity switch
    {
        BudgetEntry e => e.Name,
        Category c => c.Name ?? c.DefaultKey ?? "",
        DebtEntry d => d.Name,
        BufferEntry b => b.Name,
        BudgetEntryChange c => $"{c.FromPeriod}",
        BudgetEntryMonth m => $"{m.Period}",
        DebtPayment p => $"{p.Date:yyyy-MM-dd}",
        BufferTransaction t => t.Reason ?? $"{t.Date:yyyy-MM-dd}",
        MonthSnapshot s => $"{s.Period}",
        BalanceCheckpoint c => $"{c.Date:yyyy-MM-dd}",
        Household h => h.Name,
        _ => "",
    };

    public static string TypeOf(object entity) => SyncTypes.NameOf(entity);
}
