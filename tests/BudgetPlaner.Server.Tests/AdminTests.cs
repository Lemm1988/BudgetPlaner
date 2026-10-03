using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Server.Tests;

/// <summary>Verwaltung: Admin-Rechte, Registrierungsmodi, Sperren/Löschen. Eigener Host je Test, weil die Einstellungen serverweit gelten.</summary>
public class AdminTests
{
    private const string Admin = TestApp.AdminEmail;

    private static async Task<string?> ErrorKeyAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("key", out var key) ? key.GetString() : null;

    private static Task<HttpResponseMessage> RequestLoginAsync(TestApp app, string email, string? returnUrl = null) =>
        app.Anonymous().PostAsJsonAsync("/api/v1/auth/request", new LoginRequest(email, "de", returnUrl), JsonDefaults.Options);

    private static async Task<AuthResponse> VerifyAsync(HttpClient http, string email, DateTimeOffset? after = null)
    {
        var code = await TestApp.WaitForCodeAsync(http, email, after);
        var verify = await http.PostAsJsonAsync("/api/v1/auth/verify", new VerifyRequest(email, code, null, "Test"), JsonDefaults.Options);
        verify.EnsureSuccessStatusCode();
        return (await verify.Content.ReadFromJsonAsync<AuthResponse>(JsonDefaults.Options))!;
    }

    /// <summary>Erneute Anmeldung: wartet auf eine Mail, die neuer ist als die zuletzt zugestellte.</summary>
    private static async Task<UserClient> LoginAgainAsync(TestApp app, string email)
    {
        var http = app.Anonymous();
        var last = await http.GetFromJsonAsync<JsonElement>($"/api/v1/dev/latest-mail?to={Uri.EscapeDataString(email)}");
        (await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest(email, "de"), JsonDefaults.Options)).EnsureSuccessStatusCode();
        var auth = await VerifyAsync(http, email, last.GetProperty("createdAt").GetDateTimeOffset());
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new UserClient(http, auth);
    }

    [Fact(DisplayName = "Verwaltung: nur für Administratoren (App:AdminEmails) – andere erhalten 403, ohne Anmeldung 401")]
    public async Task AdminOnly()
    {
        using var app = new TestApp();
        var admin = await app.LoginAsync(Admin);
        var user = await app.LoginAsync("normal@example.com");
        Assert.True(admin.Auth.User.IsAdmin);
        Assert.True((await admin.GetAsync<UserDto>("/api/v1/me")).IsAdmin);
        Assert.False((await user.GetAsync<UserDto>("/api/v1/me")).IsAdmin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Anonymous().GetAsync("/api/v1/admin/overview")).StatusCode);
        foreach (var url in new[] { "/api/v1/admin/overview", "/api/v1/admin/users", "/api/v1/admin/registration" })
            Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.GetAsync(url)).StatusCode);
        var forbidden = await user.PutAsync("/api/v1/admin/registration", new RegistrationSettingsDto(RegistrationModes.Closed, []));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("err.forbidden.admin", await ErrorKeyAsync(forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync($"/api/v1/admin/users/{admin.UserId}/block", new AdminBlockRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.DeleteAsync($"/api/v1/admin/users/{admin.UserId}")).StatusCode);

        var overview = await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview");
        Assert.Equal(2, overview.Users);
        Assert.Equal(2, overview.Households);
        Assert.Equal(2, overview.NewUsers30Days);
        Assert.Equal(RegistrationModes.Open, overview.RegistrationMode);
        Assert.True(overview.DatabaseBytes > 0);
        Assert.True(overview.MemoryBytes > 0);

        var users = await admin.GetAsync<List<AdminUserDto>>("/api/v1/admin/users");
        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.Email == Admin && u.IsAdmin && u.Devices == 1);
        Assert.Contains(users, u => u.Email == "normal@example.com" && !u.IsAdmin && u.LastActiveAt is not null && u.HouseholdMembers == 1);
        Assert.Single(await admin.GetAsync<List<AdminUserDto>>("/api/v1/admin/users?search=normal"));

        (await admin.Http.PostAsync("/api/v1/admin/backup", null)).EnsureSuccessStatusCode();
        var after = await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview");
        Assert.True(after.Backups >= 1);
        Assert.NotNull(after.LastBackupAt);
    }

    [Fact(DisplayName = "Verwaltung: Registrierung offen / nur Freigegebene (Liste, Domain, Einladung) / geschlossen – bestehende Konten unberührt")]
    public async Task Registration()
    {
        using var app = new TestApp(("App:LoginRateLimitSeconds", "0"));
        var admin = await app.LoginAsync(Admin);
        var existing = await app.LoginAsync("existing@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/v1/admin/registration", new RegistrationSettingsDto("everyone", []))).StatusCode);

        // Geschlossen: niemand Neues, bestehende Konten melden sich weiter an.
        (await admin.PutAsync("/api/v1/admin/registration", new RegistrationSettingsDto(RegistrationModes.Closed, []))).EnsureSuccessStatusCode();
        var version = await app.Anonymous().GetFromJsonAsync<VersionInfo>("/api/v1/version", JsonDefaults.Options);
        Assert.Equal(RegistrationModes.Closed, version!.RegistrationMode);
        var denied = await RequestLoginAsync(app, "new1@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("err.registration.closed", await ErrorKeyAsync(denied));
        Assert.Equal(HttpStatusCode.OK, (await RequestLoginAsync(app, "existing@example.com")).StatusCode);

        // Nur Freigegebene: Adresse, Domain, E-Mail-Einladung oder Einladungslink.
        (await admin.PutAsync("/api/v1/admin/registration",
            new RegistrationSettingsDto(RegistrationModes.AllowList, ["Single@Example.com", "@familie-test.de", "kein-eintrag", "single@example.com"]))).EnsureSuccessStatusCode();
        var settings = await admin.GetAsync<RegistrationSettingsDto>("/api/v1/admin/registration");
        Assert.Equal(["single@example.com", "@familie-test.de"], settings.AllowList);
        Assert.Equal(HttpStatusCode.OK, (await RequestLoginAsync(app, "single@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RequestLoginAsync(app, "kind@familie-test.de")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RequestLoginAsync(app, "fremd@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RequestLoginAsync(app, "boese@nicht-familie-test.de")).StatusCode);

        (await existing.PostAsync("/api/v1/household/invitations", new CreateInvitationRequest("partner@example.com"))).EnsureSuccessStatusCode();
        var partner = await app.LoginAsync("partner@example.com");
        Assert.True(partner.Auth.IsNewUser);

        var link = await existing.PostReadAsync<CreateInvitationRequest, InvitationDto>("/api/v1/household/invitations", new CreateInvitationRequest(null));
        var returnUrl = "/einladung/" + link.Link.Split("/einladung/")[1];
        Assert.Equal(HttpStatusCode.Forbidden, (await RequestLoginAsync(app, "perlink@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RequestLoginAsync(app, "perlink@example.com", "/einladung/ungueltig")).StatusCode);
        var http = app.Anonymous();
        (await http.PostAsJsonAsync("/api/v1/auth/request", new LoginRequest("perlink@example.com", "de", returnUrl), JsonDefaults.Options)).EnsureSuccessStatusCode();
        var viaLink = await VerifyAsync(http, "perlink@example.com");
        Assert.True(viaLink.IsNewUser);
        Assert.Equal(returnUrl, viaLink.ReturnUrl);

        // Wieder offen: jeder darf.
        (await admin.PutAsync("/api/v1/admin/registration", new RegistrationSettingsDto(RegistrationModes.Open, []))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await RequestLoginAsync(app, "fremd@example.com")).StatusCode);
    }

    [Fact(DisplayName = "Verwaltung: Sperren beendet Sitzungen und API-Zugänge sofort und verhindert die Anmeldung; Entsperren; Löschen")]
    public async Task BlockUnblockDelete()
    {
        using var app = new TestApp(("App:LoginRateLimitSeconds", "0"));
        var admin = await app.LoginAsync(Admin);
        var user = await app.LoginAsync("sperren@example.com");
        var pat = await user.PostReadAsync<CreateApiTokenRequest, CreateApiTokenResponse>("/api/v1/me/api-tokens", new CreateApiTokenRequest("Skript", 30));

        // Selbstschutz: eigenes Konto weder sperren noch löschen.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/admin/users/{admin.UserId}/block", new AdminBlockRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Http.DeleteAsync($"/api/v1/admin/users/{admin.UserId}")).StatusCode);

        (await admin.PostAsync($"/api/v1/admin/users/{user.UserId}/block", new AdminBlockRequest("Spam"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/v1/me")).StatusCode);
        var script = app.Anonymous();
        script.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/v1/me")).StatusCode);
        var refresh = await app.Anonymous().PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(user.Auth.RefreshToken), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var login = await RequestLoginAsync(app, "sperren@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("err.account.blocked", await ErrorKeyAsync(login));

        var listed = (await admin.GetAsync<List<AdminUserDto>>("/api/v1/admin/users")).Single(u => u.Id == user.UserId);
        Assert.NotNull(listed.BlockedAt);
        Assert.Equal("Spam", listed.BlockedReason);
        Assert.Equal(0, listed.Devices);
        Assert.Equal(1, (await admin.GetAsync<AdminOverviewDto>("/api/v1/admin/overview")).BlockedUsers);

        // Entsperren: Anmeldung wieder möglich, Haushalt unverändert.
        (await admin.Http.PostAsync($"/api/v1/admin/users/{user.UserId}/unblock", null)).EnsureSuccessStatusCode();
        var again = await LoginAgainAsync(app, "sperren@example.com");
        Assert.False(again.Auth.IsNewUser);
        Assert.Equal(user.Auth.User.HouseholdId, again.Auth.User.HouseholdId);

        // Löschen: Konto weg, Sitzung ungültig.
        (await admin.Http.DeleteAsync($"/api/v1/admin/users/{user.UserId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.Http.GetAsync("/api/v1/me")).StatusCode);
        Assert.DoesNotContain(await admin.GetAsync<List<AdminUserDto>>("/api/v1/admin/users"), u => u.Email == "sperren@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.DeleteAsync($"/api/v1/admin/users/{Guid.NewGuid()}")).StatusCode);
    }
}
