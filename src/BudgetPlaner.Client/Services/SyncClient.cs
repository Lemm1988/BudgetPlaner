using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace BudgetPlaner.Client.Services;

/// <summary>
/// Synchronisation (F-131, F-132): überträgt die Outbox, holt Deltas, hört per SignalR auf Änderungen anderer Geräte
/// (&lt; 5 s) und überträgt Offline-Änderungen nach Wiederverbindung sofort (spätestens nach 15 s Takt).
/// </summary>
public sealed class SyncClient(
    DataStore store,
    ApiClient api,
    AppSession session,
    BrowserJs browser,
    NavigationManager nav,
    ToastService toasts,
    NotificationCenter notifications,
    Loc l) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private Timer? _timer;
    private DotNetObjectReference<SyncClient>? _ref;
    private bool _started;
    private DateTimeOffset _lastPull = DateTimeOffset.MinValue;
    private string _deviceId = "";

    public bool IsOnline { get; private set; } = true;
    public bool IsSyncing { get; private set; }
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public DateTimeOffset? LastSync { get; private set; }
    public string? LastError { get; private set; }

    public event Action? StatusChanged;

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _deviceId = await browser.GetLocal("bp.device") ?? "";
        if (_deviceId.Length == 0)
        {
            _deviceId = Guid.NewGuid().ToString("N");
            await browser.SetLocal("bp.device", _deviceId);
        }
        _ref ??= DotNetObjectReference.Create(this);
        await browser.Run("registerConnectivity", _ref);
        IsOnline = await browser.Call<bool>("isOnline");
        store.Mutated += OnMutated;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        _ = ConnectHubAsync();
        await SyncNowAsync();
        _ = notifications.LoadAsync();
    }

    public async Task StopAsync()
    {
        _started = false;
        store.Mutated -= OnMutated;
        _timer?.Dispose();
        _timer = null;
        if (_hub is not null)
        {
            try { await _hub.DisposeAsync(); } catch (Exception) { }
            _hub = null;
        }
    }

    private void OnMutated() => _ = FlushAsync();

    [JSInvokable]
    public async Task OnOnlineChanged(bool online)
    {
        IsOnline = online;
        StatusChanged?.Invoke();
        if (online) await SyncNowAsync();
    }

    [JSInvokable]
    public Task OnVisible() => SyncNowAsync();

    public async Task SyncNowAsync()
    {
        if (!session.IsAuthenticated) return;
        await FlushAsync();
        await PullAsync();
    }

    public async Task FlushAsync()
    {
        if (!session.IsAuthenticated || !store.HasOutbox) return;
        await _gate.WaitAsync();
        try
        {
            while (store.HasOutbox)
            {
                var batch = store.TakeBatch();
                SetSyncing(true);
                try
                {
                    var response = await api.PostAsync<SyncPushResponse>("api/v1/sync", new SyncPushRequest(_deviceId, batch));
                    var rejected = await store.CompletePushAsync(batch, response);
                    foreach (var r in rejected) toasts.Error(r.Error ?? l["err.generic"]);
                    if (response.Results.Any(r => r.Status == SyncOpStatus.Conflict)) toasts.Info(l["sync.conflictMerged"]);
                    SetOnline(true);
                    LastError = null;
                }
                catch (OfflineException)
                {
                    store.FailPush(batch);
                    SetOnline(false);
                    break;
                }
                catch (ApiException ex)
                {
                    store.FailPush(batch);
                    LastError = ex.Message;
                    if (ex.Status == 401) break;
                    toasts.Error(ex.Message);
                    break;
                }
            }
        }
        finally
        {
            SetSyncing(false);
            _gate.Release();
        }
        await PullAsync();
    }

    public async Task PullAsync()
    {
        if (!session.IsAuthenticated) return;
        await _gate.WaitAsync();
        try
        {
            SetSyncing(true);
            var since = store.Seq;
            var response = await api.GetAsync<SyncPullResponse>($"api/v1/sync?since={since}");
            if (response.Full && response.Household is { } h && store.Data.Household.Id != Guid.Empty && h.Id != store.Data.Household.Id)
                await store.ResetAsync();
            await store.ApplyPullAsync(response);
            _lastPull = DateTimeOffset.UtcNow;
            LastSync = DateTimeOffset.Now;
            LastError = null;
            SetOnline(true);
        }
        catch (OfflineException)
        {
            SetOnline(false);
        }
        catch (ApiException ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            SetSyncing(false);
            _gate.Release();
        }
    }

    private async Task TickAsync()
    {
        if (!_started || !session.IsAuthenticated) return;
        try
        {
            if (!IsOnline) SetOnline(await browser.Call<bool>("isOnline"));
            if (store.HasOutbox) await FlushAsync();
            if (_hub is null || _hub.State == HubConnectionState.Disconnected) await ConnectHubAsync();
            if (DateTimeOffset.UtcNow - _lastPull > TimeSpan.FromSeconds(IsConnected ? 120 : 30)) await PullAsync();
        }
        catch (Exception)
        {
            // Hintergrundtakt darf nie abstürzen.
        }
    }

    private async Task ConnectHubAsync()
    {
        try
        {
            if (_hub is null)
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl(nav.ToAbsoluteUri("/hubs/sync"), o => o.AccessTokenProvider = () => session.GetAccessTokenAsync())
                    .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
                    .Build();
                _hub.On<long>("changed", seq =>
                {
                    if (seq > store.Seq) _ = PullAsync();
                });
                _hub.On<NotificationDto>("notification", n =>
                {
                    notifications.Add(n);
                    toasts.Info(n.Title, link: n.Link);
                });
                _hub.On("revoked", () => _ = OnRevokedAsync());
                _hub.On("profile", () => _ = OnProfileAsync());
                _hub.Reconnected += _ =>
                {
                    StatusChanged?.Invoke();
                    return SyncNowAsync();
                };
                _hub.Closed += _ =>
                {
                    StatusChanged?.Invoke();
                    return Task.CompletedTask;
                };
            }
            if (_hub.State == HubConnectionState.Disconnected) await _hub.StartAsync();
            StatusChanged?.Invoke();
        }
        catch (Exception)
        {
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Nach eigenem Haushaltswechsel (verlassen, gelöscht, Einladung angenommen): Profil und Daten neu laden.</summary>
    public Task ReloadMembershipAsync() => OnRevokedAsync();

    /// <summary>Zugriff entzogen (F-003): Profil neu laden; entweder abgemeldet oder anderer Haushalt.</summary>
    private async Task OnRevokedAsync()
    {
        try
        {
            var me = await api.GetAsync<UserDto>("api/v1/me");
            await session.UpdateUserAsync(me);
            await store.ResetAsync();
            if (_hub?.State == HubConnectionState.Connected) await _hub.InvokeAsync("JoinAsync");
            await PullAsync();
            toasts.Info(l["household.accessChanged"]);
            nav.NavigateTo("/uebersicht");
        }
        catch (ApiException)
        {
            await session.SignOutAsync(expired: true);
        }
    }

    private async Task OnProfileAsync()
    {
        try
        {
            var me = await api.GetAsync<UserDto>("api/v1/me");
            var householdChanged = me.HouseholdId != store.Data.Household.Id && store.Data.Household.Id != Guid.Empty;
            await session.UpdateUserAsync(me);
            if (householdChanged)
            {
                await store.ResetAsync();
                if (_hub?.State == HubConnectionState.Connected) await _hub.InvokeAsync("JoinAsync");
                await PullAsync();
            }
            await l.ApplyUserAsync(me);
        }
        catch (Exception)
        {
            // nächster Abgleich holt es nach
        }
    }

    public async Task RejoinAsync()
    {
        if (_hub?.State == HubConnectionState.Connected) await _hub.InvokeAsync("JoinAsync");
    }

    private void SetOnline(bool online)
    {
        if (IsOnline == online) return;
        IsOnline = online;
        StatusChanged?.Invoke();
    }

    private void SetSyncing(bool syncing)
    {
        IsSyncing = syncing;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ref?.Dispose();
    }
}
