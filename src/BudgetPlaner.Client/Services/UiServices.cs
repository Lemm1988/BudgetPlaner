using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;

namespace BudgetPlaner.Client.Services;

/// <summary>Übersetzung und Formatierung (B-12) mit Haushaltswährung.</summary>
public sealed class Loc(BrowserJs browser, DataStore store, ApiClient api)
{
    public string Lang { get; private set; } = Texts.German;
    public event Action? Changed;

    public string this[string key] => Texts.Get(Lang, key);
    public string T(string key, params object?[] args) => Texts.Get(Lang, key, args);
    public bool Has(string key) => Texts.Has(Lang, key);

    public string Currency => string.IsNullOrEmpty(store.Data.Household.Currency) ? "EUR" : store.Data.Household.Currency;
    public string CurrencySymbol => Fmt.Currency(Currency).Symbol;
    public string Money(decimal value, bool plus = false, int decimals = 2) => Fmt.Money(value, Currency, Lang, plus, decimals);
    public string MoneyIn(decimal value, string currency, int decimals = 2) => Fmt.Money(value, currency, Lang, false, decimals);
    public string MoneyCompact(decimal value) => Fmt.MoneyCompact(value, Currency, Lang);
    public string Pct(decimal? value, int decimals = 1, bool plus = false) => Fmt.Percent(value, Lang, decimals, plus);
    public string Num(decimal value, int decimals = 2) => Fmt.Number(value, Lang, decimals);
    public string Period(Period p, bool shortName = false) => Fmt.PeriodName(p, Lang, shortName);
    public string Month(int month, bool shortName = false) => Fmt.MonthName(month, Lang, shortName);
    public string Date(DateOnly d) => Fmt.Date(d, Lang);
    public string DateShort(DateOnly d) => Fmt.DateShort(d, Lang);
    public string DateTime(DateTimeOffset d) => Fmt.DateTime(d, Lang);
    public string Weekday(DateOnly d, bool longName = false) => Fmt.Weekday(d, Lang, longName);
    public string AmountInput(decimal v) => Fmt.AmountInput(v, Lang);

    /// <summary>Dauer in Monaten lesbar: „2 Jahre und 3 Monate“; <paramref name="dative"/> für „in 2 Jahren und 3 Monaten“.</summary>
    public string Duration(int months, bool dative = false)
    {
        if (months <= 0) return this["time.now"];
        int y = months / 12, m = months % 12;
        var suffix = dative ? "Dat" : "";
        var years = y == 1 ? this["time.year"] : T("time.years" + suffix, y);
        var rest = m == 1 ? this["time.month"] : T("time.months" + suffix, m);
        return y == 0 ? rest : m == 0 ? years : T("time.and", years, rest);
    }

    public string Cat(Guid? id) => Labels.CategoryName(store.Data.Index, id, Lang);
    public string Cat(Category? c) => Labels.CategoryName(c, Lang);
    public string Of(Rhythm v) => Labels.Of(v, Lang);
    public string Of(EntryKind v) => Labels.Of(v, Lang);
    public string Of(BudgetGroup v) => Labels.Of(v, Lang);
    public string Of(DebtType v) => Labels.Of(v, Lang);
    public string Of(Role v) => Labels.Of(v, Lang);
    public string Of(AmpelStatus v) => Labels.Of(v, Lang);
    public string Of(NotificationType v) => Labels.Of(v, Lang);
    public string Of(NotificationChannel v) => Labels.Of(v, Lang);

    /// <summary>„vor 5 Minuten“ etc.</summary>
    public string Ago(DateTimeOffset when)
    {
        var diff = DateTimeOffset.UtcNow - when;
        if (diff.TotalMinutes < 1) return this["time.justNow"];
        if (diff.TotalHours < 1) return T("time.minutesAgo", (int)diff.TotalMinutes);
        if (diff.TotalDays < 1) return T("time.hoursAgo", (int)diff.TotalHours);
        if (diff.TotalDays < 7) return T("time.daysAgo", (int)diff.TotalDays);
        return DateTime(when);
    }

    public async Task InitializeAsync(string? userLanguage)
    {
        var stored = await browser.GetLocal("bp.lang");
        var browserLang = await browser.Call<string>("browserLanguage");
        await SetInternalAsync(userLanguage ?? stored ?? browserLang, raise: false);
    }

    public async Task SetAsync(string lang)
    {
        await browser.SetLocal("bp.lang", Texts.Normalize(lang));
        await SetInternalAsync(lang, raise: true);
    }

    public Task ApplyUserAsync(UserDto user) => SetInternalAsync(user.Language, raise: true);

    private async Task SetInternalAsync(string lang, bool raise)
    {
        var normalized = Texts.Normalize(lang);
        var changed = normalized != Lang;
        Lang = normalized;
        api.Language = normalized;
        await browser.Run("setLang", normalized);
        if (changed && raise)
        {
            Changed?.Invoke();
            store.NotifyChanged();
        }
    }
}

/// <summary>Seitenübergreifender UI-Zustand: gewählter Monat, Mobilansicht, Theme.</summary>
public sealed class UiState
{
    private Period? _period;

    public bool IsMobile { get; set; }
    public string Theme { get; set; } = "system";
    public bool ShowOnboarding { get; set; }
    public event Action? Changed;

    public Period SelectedPeriod(DataStore store) => _period ?? store.Engine.CurrentPeriod;

    public void SetPeriod(Period p)
    {
        _period = p;
        Changed?.Invoke();
    }

    public void ResetPeriod()
    {
        _period = null;
        Changed?.Invoke();
    }

    public void Notify() => Changed?.Invoke();
}

public sealed record Toast(Guid Id, string Message, string Kind, string? ActionLabel, Func<Task>? Action, int DurationMs, string? Link);

/// <summary>Kurze Rückmeldungen inkl. „Rückgängig“ (B-04: 30 Sekunden).</summary>
public sealed class ToastService
{
    private readonly List<Toast> _items = [];
    public IReadOnlyList<Toast> Items => _items;
    public event Action? Changed;

    public void Show(string message, string kind = "info", string? actionLabel = null, Func<Task>? action = null, int durationMs = 5000, string? link = null)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind, actionLabel, action, durationMs, link);
        _items.Add(toast);
        if (_items.Count > 4) _items.RemoveAt(0);
        Changed?.Invoke();
        _ = AutoDismissAsync(toast);
    }

    public void Success(string message) => Show(message, "success");
    public void Error(string message) => Show(message, "error", durationMs: 8000);
    public void Info(string message, string? link = null) => Show(message, "info", link: link);
    public void Undo(string message, string label, Func<Task> undo) => Show(message, "info", label, undo, 30000);

    public void Dismiss(Guid id)
    {
        if (_items.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }

    private async Task AutoDismissAsync(Toast toast)
    {
        await Task.Delay(toast.DurationMs);
        Dismiss(toast.Id);
    }
}

public sealed record ConfirmRequest(string Title, string Message, string ConfirmLabel, bool Danger, TaskCompletionSource<bool> Result, string? Input = null);

/// <summary>Bestätigungsdialoge (z. B. vor dem Löschen, F-012 „fragt nach“).</summary>
public sealed class ConfirmService
{
    public ConfirmRequest? Current { get; private set; }
    public event Action? Changed;

    public Task<bool> AskAsync(string title, string message, string confirmLabel, bool danger = false, string? requireInput = null)
    {
        Current?.Result.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        Current = new ConfirmRequest(title, message, confirmLabel, danger, tcs, requireInput);
        Changed?.Invoke();
        return tcs.Task;
    }

    public void Resolve(bool value)
    {
        var current = Current;
        Current = null;
        Changed?.Invoke();
        current?.Result.TrySetResult(value);
    }
}

/// <summary>In-App-Benachrichtigungen (Glocke).</summary>
public sealed class NotificationCenter(ApiClient api)
{
    private List<NotificationDto> _items = [];
    public IReadOnlyList<NotificationDto> Items => _items;
    public int Unread => _items.Count(n => n.ReadAt is null);
    public event Action? Changed;

    public async Task LoadAsync()
    {
        try
        {
            _items = await api.GetAsync<List<NotificationDto>>("api/v1/notifications?take=100");
            Changed?.Invoke();
        }
        catch (Exception) { /* offline */ }
    }

    public void Add(NotificationDto n)
    {
        _items.RemoveAll(x => x.Id == n.Id);
        _items.Insert(0, n);
        Changed?.Invoke();
    }

    public async Task MarkReadAsync(Guid id)
    {
        var i = _items.FindIndex(n => n.Id == id);
        if (i >= 0 && _items[i].ReadAt is null) _items[i] = _items[i] with { ReadAt = DateTimeOffset.UtcNow };
        Changed?.Invoke();
        try { await api.PostAsync($"api/v1/notifications/{id}/read"); } catch (Exception) { }
    }

    public async Task MarkAllAsync()
    {
        _items = _items.Select(n => n.ReadAt is null ? n with { ReadAt = DateTimeOffset.UtcNow } : n).ToList();
        Changed?.Invoke();
        try { await api.PostAsync("api/v1/notifications/read-all"); } catch (Exception) { }
    }

    public async Task DeleteAsync(Guid id)
    {
        _items.RemoveAll(n => n.Id == id);
        Changed?.Invoke();
        try { await api.DeleteAsync($"api/v1/notifications/{id}"); } catch (Exception) { }
    }

    public void Clear()
    {
        _items = [];
        Changed?.Invoke();
    }
}
