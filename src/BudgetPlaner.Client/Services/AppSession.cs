using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Client.Services;

/// <summary>
/// Anmeldestatus mit JWT/Refresh-Token (localStorage). Erneuert das Zugriffstoken automatisch; offline bleibt die
/// letzte Sitzung erhalten, damit die App mit lokalen Daten weiter nutzbar ist (F-132).
/// </summary>
public sealed class AppSession(BrowserJs browser, HttpClient http)
{
    private const string StorageKey = "bp.session";
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AuthResponse? Auth { get; private set; }
    public UserDto? User => Auth?.User;
    public bool IsAuthenticated => Auth is not null;
    public bool IsOwner => User?.Role == Domain.Role.Owner;

    public event Action? Changed;

    /// <summary>Sitzung ist serverseitig beendet (z. B. Konto gelöscht, Gerät abgemeldet).</summary>
    public event Action? Expired;

    public async Task InitializeAsync()
    {
        Auth = await ReadStoredAsync();
    }

    public async Task SignInAsync(AuthResponse auth)
    {
        Auth = auth;
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task UpdateUserAsync(UserDto user)
    {
        if (Auth is null) return;
        Auth = Auth with { User = user };
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task SignOutAsync(bool expired = false)
    {
        Auth = null;
        await browser.RemoveLocal(StorageKey);
        Changed?.Invoke();
        if (expired) Expired?.Invoke();
    }

    /// <summary>Gültiges Zugriffstoken (bei Bedarf per Refresh-Token erneuert); offline das letzte bekannte.</summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        if (Auth is null) return null;
        if (!forceRefresh && Auth.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return Auth.AccessToken;
        await _refreshLock.WaitAsync();
        try
        {
            // Ein anderer Tab hat evtl. schon erneuert.
            var stored = await ReadStoredAsync();
            if (stored is not null && stored.RefreshToken != Auth.RefreshToken && stored.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                Auth = stored;
                return Auth.AccessToken;
            }
            if (Auth is null) return null;
            if (!forceRefresh && Auth.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60)) return Auth.AccessToken;

            HttpResponseMessage res;
            try
            {
                res = await http.PostAsJsonAsync("api/v1/auth/refresh", new RefreshRequest(Auth.RefreshToken), JsonDefaults.Options);
            }
            catch (HttpRequestException)
            {
                return Auth.AccessToken;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                await SignOutAsync(expired: true);
                return null;
            }
            if (!res.IsSuccessStatusCode) return Auth.AccessToken;
            var refreshed = await res.Content.ReadFromJsonAsync<AuthResponse>(JsonDefaults.Options);
            if (refreshed is null) return Auth.AccessToken;
            Auth = refreshed;
            await PersistAsync();
            return Auth.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AuthResponse?> ReadStoredAsync()
    {
        var json = await browser.GetLocal(StorageKey);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<AuthResponse>(json, JsonDefaults.Options); }
        catch (JsonException) { return null; }
    }

    private ValueTask PersistAsync() =>
        Auth is null ? ValueTask.CompletedTask : browser.SetLocal(StorageKey, JsonSerializer.Serialize(Auth, JsonDefaults.Options));
}
