using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BudgetPlaner.Server.Tests;

/// <summary>Steuerbare Uhr für Ablauf-/Fristtests.</summary>
public sealed class TestClock : IClock
{
    public TimeSpan Offset { get; set; }
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Offset;
    public DateOnly TodayIn(string? timeZone) => DateOnly.FromDateTime(NowIn(timeZone).DateTime);

    public DateTimeOffset NowIn(string? timeZone)
    {
        try { return TimeZoneInfo.ConvertTime(UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone ?? "Europe/Berlin")); }
        catch (Exception) { return UtcNow; }
    }
}

/// <summary>Test-Host mit eigener SQLite-Datei je Instanz, Dev-Postfach und festen Schlüsseln.</summary>
/// <param name="settings">Zusätzliche Einstellungen für diesen Host (überschreiben die Vorgaben).</param>
public sealed class TestApp(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
{
    /// <summary>Administrator in allen Test-Hosts (App:AdminEmails).</summary>
    public const string AdminEmail = "admin@example.com";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "budgetplaner-tests", Guid.NewGuid().ToString("N"));
    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DataDirectory", _dir);
        builder.UseSetting("App:DevMailbox", "true");
        builder.UseSetting("App:RequireHttps", "false");
        builder.UseSetting("App:PublicBaseUrl", "http://localhost");
        builder.UseSetting("RateLimits:AuthPerMinute", "100000");
        builder.UseSetting("RateLimits:ApiPerMinute", "100000");
        builder.UseSetting("Security:EncryptionKey", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("Security:JwtSigningKey", Convert.ToBase64String(Enumerable.Range(1, 64).Select(i => (byte)(i * 3)).ToArray()));
        builder.UseSetting("App:AdminEmails", AdminEmail);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IClock>();
            s.AddSingleton<IClock>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Kompletter Login über Magic-Link-Mail im Dev-Postfach.</summary>
    public async Task<UserClient> LoginAsync(string email, string language = "de")
    {
        var http = Anonymous();
        var req = await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest(email, language), JsonDefaults.Options);
        req.EnsureSuccessStatusCode();
        var code = await WaitForCodeAsync(http, email);
        var verify = await http.PostAsJsonAsync("/api/v1/auth/verify", new VerifyRequest(email, code, null, "Test"), JsonDefaults.Options);
        verify.EnsureSuccessStatusCode();
        var auth = (await verify.Content.ReadFromJsonAsync<AuthResponse>(JsonDefaults.Options))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new UserClient(http, auth);
    }

    public static async Task<string> WaitForCodeAsync(HttpClient http, string email, DateTimeOffset? after = null)
    {
        for (var i = 0; i < 100; i++)
        {
            var res = await http.GetAsync($"/api/v1/dev/latest-mail?to={Uri.EscapeDataString(email)}");
            if (res.StatusCode == HttpStatusCode.OK)
            {
                var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
                var created = doc.GetProperty("createdAt").GetDateTimeOffset();
                if (after is null || created > after)
                {
                    var text = doc.GetProperty("text").GetString()!;
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{6})\b");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Keine Mail im Dev-Postfach.");
    }

    public static async Task<string> WaitForMailTextAsync(HttpClient http, string email)
    {
        for (var i = 0; i < 100; i++)
        {
            var res = await http.GetAsync($"/api/v1/dev/latest-mail?to={Uri.EscapeDataString(email)}");
            if (res.StatusCode == HttpStatusCode.OK) return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString()!;
            await Task.Delay(100);
        }
        throw new TimeoutException("Keine Mail im Dev-Postfach.");
    }
}

public sealed record UserClient(HttpClient Http, AuthResponse Auth)
{
    public Guid UserId => Auth.User.Id;

    public async Task<T> GetAsync<T>(string url)
    {
        var res = await Http.GetAsync(url);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(JsonDefaults.Options))!;
    }

    public Task<HttpResponseMessage> PostAsync<T>(string url, T body) => Http.PostAsJsonAsync(url, body, JsonDefaults.Options);
    public Task<HttpResponseMessage> PutAsync<T>(string url, T body) => Http.PutAsJsonAsync(url, body, JsonDefaults.Options);
    public Task<HttpResponseMessage> PatchAsync<T>(string url, T body) => Http.PatchAsJsonAsync(url, body, JsonDefaults.Options);

    public async Task<TResult> PostReadAsync<T, TResult>(string url, T body)
    {
        var res = await PostAsync(url, body);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<TResult>(JsonDefaults.Options))!;
    }

    public Task<SyncPullResponse> PullAsync(long since = 0) => GetAsync<SyncPullResponse>($"/api/v1/sync?since={since}");

    public Task<SyncPushResponse> PushAsync(params SyncOperation[] ops) =>
        PostReadAsync<SyncPushRequest, SyncPushResponse>("/api/v1/sync", new SyncPushRequest("test", [.. ops]));

    public static SyncOperation Upsert(object entity, object? baseline = null) => new(Guid.NewGuid(), SyncTypes.NameOf(entity),
        ((Domain.Entities.HouseholdEntity)entity).Id, "upsert", JsonSerializer.SerializeToElement(entity, entity.GetType(), JsonDefaults.Options),
        baseline is null ? null : JsonSerializer.SerializeToElement(baseline, baseline.GetType(), JsonDefaults.Options));

    public static SyncOperation Delete(string type, Guid id) => new(Guid.NewGuid(), type, id, "delete", null, null);
}
