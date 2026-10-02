using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Domain.Defaults;

/// <summary>
/// Startkategorien nach D-01: Oberkategorien Bedarf/Wunsch/Sparen (50/30/20) mit den BudgetHeld-Bereichen als
/// Unterkategorien, dazu Einnahmen und die Systemkategorien „Sonstiges“/„Sonstige Einnahmen“ (F-012, I-17).
/// </summary>
public static class DefaultCategories
{
    public sealed record Definition(string Key, CategoryType Type, BudgetGroup Group, string? ParentKey, string Color, string Icon, bool IsSystem);

    public const string IncomeRoot = "income";
    public const string OtherIncome = "other-income";
    public const string Needs = "needs";
    public const string Wants = "wants";
    public const string Savings = "savings";
    public const string Other = "other";

    public static IReadOnlyList<Definition> All { get; } =
    [
        new(IncomeRoot, CategoryType.Income, BudgetGroup.None, null, "#2E7D5B", "wallet", true),
        new("salary", CategoryType.Income, BudgetGroup.None, IncomeRoot, "#3C8D68", "briefcase", false),
        new("side-income", CategoryType.Income, BudgetGroup.None, IncomeRoot, "#4E9C76", "spark", false),
        new("benefits", CategoryType.Income, BudgetGroup.None, IncomeRoot, "#62AB86", "family", false),
        new(OtherIncome, CategoryType.Income, BudgetGroup.None, IncomeRoot, "#79B998", "dots", true),

        new(Needs, CategoryType.Expense, BudgetGroup.Needs, null, "#2F6690", "shield", true),
        new("housing", CategoryType.Expense, BudgetGroup.Needs, Needs, "#3A7CA5", "home", false),
        new("mobility", CategoryType.Expense, BudgetGroup.Needs, Needs, "#4A8DB7", "car", false),
        new("living", CategoryType.Expense, BudgetGroup.Needs, Needs, "#5B9EC9", "basket", false),

        new(Wants, CategoryType.Expense, BudgetGroup.Wants, null, "#B5651D", "heart", true),
        new("guiltfree", CategoryType.Expense, BudgetGroup.Wants, Wants, "#C97B32", "smile", false),
        new("giving", CategoryType.Expense, BudgetGroup.Wants, Wants, "#D9914A", "gift", false),

        new(Savings, CategoryType.Expense, BudgetGroup.Savings, null, "#6A4C93", "piggy", true),
        new("saving", CategoryType.Expense, BudgetGroup.Savings, Savings, "#7B5EA7", "piggy", false),
        new("investing", CategoryType.Expense, BudgetGroup.Savings, Savings, "#8C71B8", "chart", false),
        new("debt-payoff", CategoryType.Expense, BudgetGroup.Savings, Savings, "#9D85C7", "debt", false),

        new(Other, CategoryType.Expense, BudgetGroup.None, null, "#707070", "dots", true),
    ];

    public static List<Category> Create(Guid householdId, DateTimeOffset now)
    {
        var byKey = new Dictionary<string, Category>();
        var list = new List<Category>();
        var sort = 0;
        foreach (var d in All)
        {
            var c = new Category
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                DefaultKey = d.Key,
                Type = d.Type,
                Group = d.Group,
                ParentId = d.ParentKey is null ? null : byKey[d.ParentKey].Id,
                Color = d.Color,
                Icon = d.Icon,
                IsSystem = d.IsSystem,
                SortIndex = sort++,
                CreatedAt = now,
                UpdatedAt = now,
            };
            byKey[d.Key] = c;
            list.Add(c);
        }
        return list;
    }

    /// <summary>Kategorie, auf die beim Löschen umgehängt wird (F-012).</summary>
    public static Category? FallbackFor(HouseholdIndex ix, CategoryType type) =>
        ix.Categories.FirstOrDefault(c => c.DefaultKey == (type == CategoryType.Income ? OtherIncome : Other))
        ?? ix.Categories.FirstOrDefault(c => c.Type == type && c.IsSystem);
}

/// <summary>Erzeugt neue Haushalte mit Standardwerten (F-002: automatisch beim ersten Login).</summary>
public static class HouseholdFactory
{
    public static (Household Household, List<Category> Categories) Create(Guid householdId, string name, string currency, int monthStartDay,
        DateOnly today, DateTimeOffset now)
    {
        var rules = new List<MonthStartRule> { new(Period.OfCalendarDate(today).AddMonths(-120), Math.Clamp(monthStartDay, 1, 28)) };
        var calendar = new PeriodCalendar(rules);
        var household = new Household
        {
            Id = householdId,
            Name = name,
            Currency = currency,
            MonthStartRules = rules,
            FirstPeriod = calendar.PeriodOf(today),
            CreatedAt = now,
            UpdatedAt = now,
        };
        return (household, DefaultCategories.Create(householdId, now));
    }
}
