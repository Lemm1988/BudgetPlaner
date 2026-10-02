using Microsoft.AspNetCore.Components;
using BudgetPlaner.Shared.Contracts;
using Microsoft.JSInterop;

namespace BudgetPlaner.Client.Services;

/// <summary>Start der App: Sitzung, Sprache, Theme und lokale Daten laden (offline-first), danach synchronisieren.</summary>
public sealed class AppStartup(AppSession session, Loc l, DataStore store, SyncClient sync, BrowserJs browser, UiState ui, NotificationCenter notifications, NavigationManager nav)
{
    private DotNetObjectReference<AppStartup>? _ref;

    public bool UpdateAvailable { get; private set; }
    public bool CanInstall { get; private set; }
    public event Action? PwaChanged;

    public async Task InitializeAsync()
    {
        await session.InitializeAsync();
        await l.InitializeAsync(session.User?.Language);
        ui.Theme = session.User?.Theme ?? await browser.GetLocal("bp.theme") ?? "system";
        await browser.Run("setTheme", ui.Theme);
        _ref = DotNetObjectReference.Create(this);
        ui.IsMobile = await browser.Call<bool>("registerViewport", _ref);
        CanInstall = await browser.Call<bool>("registerInstall", _ref);
        await browser.Run("registerServiceWorkerUpdates", _ref);
        await browser.Run("registerServiceWorkerMessages", _ref);
        session.Expired += () => _ = SignedOutAsync();
        if (session.IsAuthenticated) await StartUserAsync();
    }

    /// <summary>Nach Anmeldung oder Start: lokale Daten sofort anzeigen, dann im Hintergrund synchronisieren.</summary>
    public async Task StartUserAsync()
    {
        await store.LoadAsync();
        _ = sync.StartAsync();
    }

    public async Task SignInAsync(AuthResponse auth)
    {
        await session.SignInAsync(auth);
        await l.ApplyUserAsync(auth.User);
        ui.Theme = auth.User.Theme;
        await browser.Run("setTheme", ui.Theme);
        await StartUserAsync();
    }

    public async Task SignOutAsync(bool clearLocalData = true)
    {
        await sync.StopAsync();
        await session.SignOutAsync();
        notifications.Clear();
        if (clearLocalData) await store.ResetAsync();
    }

    private async Task SignedOutAsync()
    {
        await sync.StopAsync();
        notifications.Clear();
        await store.ResetAsync();
    }

    public async Task SetThemeAsync(string theme)
    {
        ui.Theme = theme;
        await browser.SetLocal("bp.theme", theme);
        await browser.Run("setTheme", theme);
        ui.Notify();
    }

    [JSInvokable]
    public void OnViewportChanged(bool mobile)
    {
        ui.IsMobile = mobile;
        ui.Notify();
    }

    [JSInvokable]
    public async Task OnSystemThemeChanged() => await browser.Run("setTheme", ui.Theme);

    [JSInvokable]
    public void OnInstallAvailable()
    {
        CanInstall = true;
        PwaChanged?.Invoke();
    }

    [JSInvokable]
    public void OnInstalled()
    {
        CanInstall = false;
        PwaChanged?.Invoke();
    }

    [JSInvokable]
    public void OnUpdateAvailable()
    {
        UpdateAvailable = true;
        PwaChanged?.Invoke();
    }

    [JSInvokable]
    public void OnServiceWorkerNavigate(string url)
    {
        if (url.StartsWith('/') && !url.StartsWith("//")) nav.NavigateTo(url);
    }

    [JSInvokable]
    public async Task OnPushReceived()
    {
        if (session.IsAuthenticated) await notifications.LoadAsync();
    }
}
