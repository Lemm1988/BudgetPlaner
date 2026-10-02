using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Server.Tests;

public class AuthTests : IClassFixture<TestAppFixture>
{
    private readonly TestApp _app;
    public AuthTests(TestAppFixture f) => _app = f.App;

    [Fact(DisplayName = "F-001: Registrierung per E-Mail – Mail mit Link und Code, Login ohne Passwort, Haushalt automatisch")]
    public async Task Registration()
    {
        var user = await _app.LoginAsync("f001@example.com");
        Assert.True(user.Auth.IsNewUser);
        var me = await user.GetAsync<UserDto>("/api/v1/me");
        Assert.Equal("f001@example.com", me.Email);
        var pull = await user.PullAsync();
        Assert.NotNull(pull.Household);
        Assert.Contains(pull.Categories, c => c.DefaultKey == "needs");
        Assert.Contains(pull.Categories, c => c.DefaultKey == "wants");
        Assert.Contains(pull.Categories, c => c.DefaultKey == "savings");
        Assert.Contains(pull.Categories, c => c.DefaultKey == "other");
    }

    [Fact(DisplayName = "F-001: Mehrfachanforderung innerhalb 60 Sek. wird per Rate-Limit verhindert")]
    public async Task RateLimit()
    {
        var http = _app.Anonymous();
        var first = await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest("rate@example.com"), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest("rate@example.com"), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.RetryAfter is not null);
    }

    [Fact(DisplayName = "F-001: Link wird innerhalb 60 Sek. versendet, ist nur einmal einlösbar")]
    public async Task SingleUse()
    {
        var http = _app.Anonymous();
        var start = DateTimeOffset.UtcNow;
        await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest("once@example.com"), JsonDefaults.Options);
        var text = await TestApp.WaitForMailTextAsync(http, "once@example.com");
        Assert.True(DateTimeOffset.UtcNow - start < TimeSpan.FromSeconds(60));
        var token = System.Text.RegularExpressions.Regex.Match(text, @"token=([^&\s]+)").Groups[1].Value;
        var ok = await http.PostAsJsonAsync("/api/v1/auth/verify", new VerifyRequest(null, null, Uri.UnescapeDataString(token), null), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var again = await http.PostAsJsonAsync("/api/v1/auth/verify", new VerifyRequest(null, null, Uri.UnescapeDataString(token), null), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact(DisplayName = "F-001: Link ist max. 15 Minuten gültig")]
    public async Task Expiry()
    {
        using var app = new TestApp();
        var http = app.Anonymous();
        await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest("expire@example.com"), JsonDefaults.Options);
        var code = await TestApp.WaitForCodeAsync(http, "expire@example.com");
        app.Clock.Offset = TimeSpan.FromMinutes(16);
        var res = await http.PostAsJsonAsync("/api/v1/auth/verify", new VerifyRequest("expire@example.com", code, null, null), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact(DisplayName = "Sicherheit: Push-Abos nur für bekannte Push-Dienste (kein SSRF), Sicherheitsheader inkl. CSP")]
    public async Task PushEndpointAllowlistAndHeaders()
    {
        var u = await _app.LoginAsync("push-ssrf@example.com");
        foreach (var bad in new[] { "https://169.254.169.254/latest/meta-data", "https://localhost/hook", "http://fcm.googleapis.com/fcm/send/x",
                     "https://evil.example.com/fcm.googleapis.com", "https://fcm.googleapis.com.evil.com/x", "https://fcm.googleapis.com:8443/x" })
        {
            var r = await u.PostAsync("/api/v1/me/push-subscriptions", new PushSubscriptionRequest(bad, "p", "a", "Test"));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        foreach (var good in new[] { "https://fcm.googleapis.com/fcm/send/abc", "https://updates.push.services.mozilla.com/wpush/v2/xyz",
                     "https://wns2-par02p.notify.windows.com/w/?token=1", "https://web.push.apple.com/QGqzv" })
        {
            var r = await u.PostAsync("/api/v1/me/push-subscriptions", new PushSubscriptionRequest(good, "p", "a", "Test"));
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        var page = await _app.Anonymous().GetAsync("/");
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", csp);
        Assert.DoesNotContain("connect-src 'self' ws: wss:", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact(DisplayName = "F-130: Ohne Token → 401; abgemeldete Sitzung → 401")]
    public async Task Unauthorized()
    {
        var anon = _app.Anonymous();
        foreach (var url in new[] { "/api/v1/sync", "/api/v1/entries", "/api/v1/me", "/api/v1/statistics/series?end=2026-01" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        var user = await _app.LoginAsync("logout@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await user.Http.PostAsync("/api/v1/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact(DisplayName = "Refresh-Token rotiert, altes Token nach Rotation nur kurz gültig")]
    public async Task Refresh()
    {
        var user = await _app.LoginAsync("refresh@example.com");
        var http = _app.Anonymous();
        var res = await http.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(user.Auth.RefreshToken), JsonDefaults.Options);
        res.EnsureSuccessStatusCode();
        var next = (await res.Content.ReadFromJsonAsync<AuthResponse>(JsonDefaults.Options))!;
        Assert.NotEqual(user.Auth.RefreshToken, next.RefreshToken);
        var http2 = _app.Anonymous();
        http2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await http2.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact(DisplayName = "F-130: Persönlicher API-Zugang nutzbar und widerrufbar")]
    public async Task ApiToken()
    {
        var user = await _app.LoginAsync("pat@example.com");
        var created = await user.PostReadAsync<CreateApiTokenRequest, CreateApiTokenResponse>("/api/v1/me/api-tokens", new CreateApiTokenRequest("Skript", 30));
        var http = _app.Anonymous();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/entries")).StatusCode);
        await user.Http.DeleteAsync($"/api/v1/me/api-tokens/{created.Info.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/entries")).StatusCode);
    }

    [Fact(DisplayName = "DoD: OpenAPI-Dokument und Swagger UI verfügbar, versioniert unter /api/v1/")]
    public async Task OpenApi()
    {
        var http = _app.Anonymous();
        var doc = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/openapi/v1.json");
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(paths.Count > 60);
        Assert.All(paths, p => Assert.StartsWith("/api/v1/", p));
        Assert.True(doc.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/docs/index.html")).StatusCode);
    }
}

public class HouseholdTests : IClassFixture<TestAppFixture>
{
    private readonly TestApp _app;
    public HouseholdTests(TestAppFixture f) => _app = f.App;

    private static async Task<BudgetEntry> CreateEntryAsync(UserClient u, string name, decimal amount, EntryKind kind = EntryKind.Expense, string cat = "living")
    {
        var pull = await u.PullAsync();
        var category = pull.Categories.Single(c => c.DefaultKey == (kind == EntryKind.Income ? "salary" : cat));
        var entry = new BudgetEntry
        {
            Id = Guid.NewGuid(), Name = name, Amount = amount, Kind = kind, CategoryId = category.Id, Rhythm = Rhythm.Monthly,
            StartPeriod = new Period(2026, 1),
        };
        var res = await u.PostAsync("/api/v1/entries", entry);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<BudgetEntry>(JsonDefaults.Options))!;
    }

    [Fact(DisplayName = "F-002/F-003: Einladung per E-Mail, Mitglied sieht dieselben Daten, darf niemanden entfernen")]
    public async Task InviteAndRoles()
    {
        var owner = await _app.LoginAsync("owner1@example.com");
        await CreateEntryAsync(owner, "Miete", 900, cat: "housing");
        var invite = await owner.PostReadAsync<CreateInvitationRequest, InvitationDto>("/api/v1/household/invitations", new CreateInvitationRequest("member1@example.com"));
        var mail = await TestApp.WaitForMailTextAsync(_app.Anonymous(), "member1@example.com");
        Assert.Contains("/einladung/", mail);
        var token = invite.Link.Split("/einladung/")[1];

        var member = await _app.LoginAsync("member1@example.com");
        var info = await member.GetAsync<InvitationInfoDto>($"/api/v1/invitations/{token}");
        Assert.True(info.Valid);
        var noConfirm = await member.PostAsync($"/api/v1/invitations/{token}/accept", new AcceptInvitationRequest(false));
        Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
        (await member.PostAsync($"/api/v1/invitations/{token}/accept", new AcceptInvitationRequest(true))).EnsureSuccessStatusCode();

        var memberData = await member.PullAsync();
        Assert.Contains(memberData.Entries, e => e.Name == "Miete");
        Assert.Equal(2, memberData.Members.Count);

        // Mitglieder bearbeiten Budgetdaten …
        await CreateEntryAsync(member, "Strom", 80, cat: "housing");
        Assert.Contains((await owner.PullAsync()).Entries, e => e.Name == "Strom");
        // … aber dürfen niemanden entfernen und keine Haushaltseinstellungen ändern.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Http.DeleteAsync($"/api/v1/household/members/{owner.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsync("/api/v1/household", new HouseholdSettingsUpdate(null, "CHF", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Http.GetAsync("/api/v1/household/invitations")).StatusCode);
        var info2 = await member.GetAsync<HouseholdInfoDto>("/api/v1/household");
        Assert.Equal(Role.Member, info2.MyRole);
    }

    [Fact(DisplayName = "F-003: Entfernen entzieht sofort den Zugriff; F-002: fremde Daten bleiben erhalten")]
    public async Task RemoveMember()
    {
        var owner = await _app.LoginAsync("owner2@example.com");
        var invite = await owner.PostReadAsync<CreateInvitationRequest, InvitationDto>("/api/v1/household/invitations", new CreateInvitationRequest(null));
        var member = await _app.LoginAsync("member2@example.com");
        (await member.PostAsync($"/api/v1/invitations/{invite.Link.Split("/einladung/")[1]}/accept", new AcceptInvitationRequest(true))).EnsureSuccessStatusCode();
        var shared = await CreateEntryAsync(member, "Gemeinsam", 10);

        (await owner.Http.DeleteAsync($"/api/v1/household/members/{member.UserId}")).EnsureSuccessStatusCode();
        var afterRemoval = await member.PullAsync();
        Assert.DoesNotContain(afterRemoval.Entries, e => e.Id == shared.Id);
        Assert.Single(afterRemoval.Members);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync($"/api/v1/entries/{shared.Id}")).StatusCode);
        Assert.Contains((await owner.PullAsync()).Entries, e => e.Id == shared.Id);
    }

    [Fact(DisplayName = "F-100/F-101: Währung haushaltsweit, Monatsstart wirkt nur auf künftige Monate")]
    public async Task Settings()
    {
        var owner = await _app.LoginAsync("settings@example.com");
        var info = await owner.GetAsync<HouseholdInfoDto>("/api/v1/household");
        var res = await owner.PatchAsync("/api/v1/household", new HouseholdSettingsUpdate("Familie Test", "CHF", 25, null));
        res.EnsureSuccessStatusCode();
        var updated = (await res.Content.ReadFromJsonAsync<HouseholdInfoDto>(JsonDefaults.Options))!;
        Assert.Equal("CHF", updated.Household.Currency);
        Assert.Equal("Familie Test", updated.Household.Name);
        var cal = new PeriodCalendar(updated.Household.MonthStartRules);
        var current = new PeriodCalendar(info.Household.MonthStartRules).PeriodOf(DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.Equal(1, cal.StartDayFor(current));
        Assert.Equal(25, cal.StartDayFor(current.Next));
    }

    [Fact(DisplayName = "F-111: Konto löschen – kein Zugriff mehr, Daten anderer Mitglieder bleiben unberührt")]
    public async Task DeleteAccount()
    {
        var owner = await _app.LoginAsync("delowner@example.com");
        var invite = await owner.PostReadAsync<CreateInvitationRequest, InvitationDto>("/api/v1/household/invitations", new CreateInvitationRequest(null));
        var member = await _app.LoginAsync("delmember@example.com");
        (await member.PostAsync($"/api/v1/invitations/{invite.Link.Split("/einladung/")[1]}/accept", new AcceptInvitationRequest(true))).EnsureSuccessStatusCode();
        await CreateEntryAsync(owner, "Bleibt", 100);

        (await owner.Http.DeleteAsync("/api/v1/me")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.Http.GetAsync("/api/v1/me")).StatusCode);
        var data = await member.PullAsync();
        Assert.Contains(data.Entries, e => e.Name == "Bleibt");
        Assert.Single(data.Members);
        Assert.Equal(Role.Owner, (await member.GetAsync<HouseholdInfoDto>("/api/v1/household")).MyRole);
    }
}

/// <summary>Gemeinsamer Test-Host je Testklasse.</summary>
public sealed class TestAppFixture : IDisposable
{
    public TestApp App { get; } = new();
    public void Dispose() => App.Dispose();
}
