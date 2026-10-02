using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BudgetPlaner.Server.Endpoints;

public static class EndpointExtensions
{
    /// <summary>Gruppe mit Pflicht-Anmeldung; lädt den Nutzerkontext frisch aus der DB.</summary>
    public static RouteGroupBuilder Authed(this RouteGroupBuilder group) =>
        (RouteGroupBuilder)group.RequireAuthorization().AddEndpointFilter<UserContextFilter>();
}

public static class AccountEndpoints
{
    public sealed record PasskeyLoginBody(string FlowId, string AssertionJson, string? DeviceName);
    public sealed record PasskeyLoginOptions(string OptionsJson, string FlowId);

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------------- Anmeldung (F-001) ----------------
        var auth = app.MapGroup("/api/v1/auth").WithTags("Anmeldung").RequireRateLimiting("auth");
        auth.MapPost("/request", async (LoginRequest body, AuthService s, CancellationToken ct) => Results.Ok(await s.RequestLoginAsync(body, ct)))
            .WithSummary("Magic Link + Code per E-Mail anfordern (F-001). 429 bei erneuter Anforderung innerhalb von 60 Sekunden.");
        auth.MapPost("/verify", async (VerifyRequest body, AuthService s, CancellationToken ct) => Results.Ok(await s.VerifyAsync(body, ct)))
            .WithSummary("Link-Token oder E-Mail + Code einlösen → JWT + Refresh-Token. Neue Nutzer erhalten automatisch einen Haushalt.");
        auth.MapPost("/refresh", async (RefreshRequest body, AuthService s, CancellationToken ct) => Results.Ok(await s.RefreshAsync(body.RefreshToken, ct)))
            .WithSummary("Zugriffstoken erneuern (Refresh-Token rotiert).");
        auth.MapPost("/logout", async (HttpContext http, AuthService s, CancellationToken ct) =>
        {
            await s.LogoutAsync(http.User.SessionId(), ct);
            return Results.NoContent();
        }).RequireAuthorization().WithSummary("Aktuelle Sitzung beenden.");
        auth.MapPost("/passkey/options", async (IPasskeyService p, CancellationToken ct) =>
        {
            var (options, flow) = await p.LoginOptionsAsync(ct);
            return Results.Ok(new PasskeyLoginOptions(options, flow));
        }).WithSummary("WebAuthn-Optionen für die Passkey-Anmeldung (B-15).");
        auth.MapPost("/passkey/login", async (PasskeyLoginBody body, IPasskeyService p, AuthService s, IAppDbContext db, CancellationToken ct) =>
        {
            var user = await p.LoginAsync(body.FlowId, body.AssertionJson, ct);
            var tracked = await db.Users.FirstAsync(u => u.Id == user.Id, ct);
            tracked.LastLoginAt = DateTimeOffset.UtcNow;
            var response = s.CreateSession(tracked, body.DeviceName, false, null);
            await db.SaveChangesAsync(ct);
            return Results.Ok(response);
        }).WithSummary("Mit Passkey anmelden.");

        // ---------------- Profil (F-100) ----------------
        var me = app.MapGroup("/api/v1/me").WithTags("Profil").Authed();
        me.MapGet("/", (RequestUser u) => Results.Ok(AuthService.ToDto(u.Context.User))).WithSummary("Eigenes Profil.");
        me.MapPatch("/", async (ProfileUpdate body, RequestUser u, ProfileService s, CancellationToken ct) => Results.Ok(await s.UpdateAsync(u.Context, body, ct)))
            .WithSummary("Name, Sprache, Zeitzone, Design ändern (Namensänderung sofort für alle Mitglieder sichtbar).");
        me.MapPost("/email", async (EmailChangeRequest body, RequestUser u, ProfileService s, CancellationToken ct) =>
        {
            await s.RequestEmailChangeAsync(u.Context, body.NewEmail, ct);
            return Results.NoContent();
        }).WithSummary("E-Mail-Änderung anfordern (Bestätigungscode an neue Adresse).");
        me.MapPost("/email/confirm", async (EmailChangeConfirm body, RequestUser u, ProfileService s, CancellationToken ct) =>
            Results.Ok(await s.ConfirmEmailChangeAsync(u.Context, body.Code, ct))).WithSummary("E-Mail-Änderung bestätigen.");
        me.MapGet("/notification-settings", async (RequestUser u, ProfileService s, CancellationToken ct) => Results.Ok(await s.GetNotificationSettingsAsync(u.Context, ct)))
            .WithSummary("Benachrichtigungseinstellungen je Art und Kanal (F-080, F-081).");
        me.MapPut("/notification-settings", async (List<NotificationSettingDto> body, RequestUser u, ProfileService s, CancellationToken ct) =>
            Results.Ok(await s.UpdateNotificationSettingsAsync(u.Context, body, ct))).WithSummary("Benachrichtigungen einzeln (de)aktivieren.");
        me.MapGet("/sessions", async (RequestUser u, ProfileService s, CancellationToken ct) => Results.Ok(await s.SessionsAsync(u.Context, ct)))
            .WithSummary("Angemeldete Geräte.");
        me.MapDelete("/sessions/{id:guid}", async (Guid id, RequestUser u, ProfileService s, CancellationToken ct) =>
        {
            await s.RevokeSessionAsync(u.Context, id, ct);
            return Results.NoContent();
        }).WithSummary("Gerät abmelden.");
        me.MapGet("/passkeys", async (RequestUser u, ProfileService s, CancellationToken ct) => Results.Ok(await s.PasskeysAsync(u.Context, ct)));
        me.MapPost("/passkeys/options", async (RequestUser u, IPasskeyService p, CancellationToken ct) =>
            Results.Ok(new PasskeyOptionsResponse(await p.RegistrationOptionsAsync(u.Context.User, ct)))).WithSummary("Passkey-Registrierung vorbereiten.");
        me.MapPost("/passkeys", async (PasskeyRegisterRequest body, RequestUser u, IPasskeyService p, CancellationToken ct) =>
        {
            var c = await p.RegisterAsync(u.Context.User, body.AttestationJson, body.Name, ct);
            return Results.Ok(new PasskeyDto(c.Id, c.Name, c.CreatedAt, c.LastUsedAt));
        }).WithSummary("Passkey speichern.");
        me.MapDelete("/passkeys/{id:guid}", async (Guid id, RequestUser u, ProfileService s, CancellationToken ct) =>
        {
            await s.DeletePasskeyAsync(u.Context, id, ct);
            return Results.NoContent();
        });
        me.MapGet("/api-tokens", async (RequestUser u, ProfileService s, CancellationToken ct) => Results.Ok(await s.ApiTokensAsync(u.Context, ct)))
            .WithSummary("Persönliche API-Zugänge (F-130).");
        me.MapPost("/api-tokens", async (CreateApiTokenRequest body, RequestUser u, ProfileService s, CancellationToken ct) =>
            Results.Ok(await s.CreateApiTokenAsync(u.Context, body, ct))).WithSummary("API-Zugang erstellen – das Token wird nur einmal angezeigt.");
        me.MapDelete("/api-tokens/{id:guid}", async (Guid id, RequestUser u, ProfileService s, CancellationToken ct) =>
        {
            await s.RevokeApiTokenAsync(u.Context, id, ct);
            return Results.NoContent();
        });
        me.MapPost("/push-subscriptions", async (PushSubscriptionRequest body, RequestUser u, ProfileService s, CancellationToken ct) =>
        {
            await s.SubscribePushAsync(u.Context, body, ct);
            return Results.NoContent();
        }).WithSummary("Web-Push-Abo dieses Geräts speichern.");
        me.MapDelete("/push-subscriptions", async ([FromQuery] string endpoint, RequestUser u, ProfileService s, CancellationToken ct) =>
        {
            await s.UnsubscribePushAsync(u.Context, endpoint, ct);
            return Results.NoContent();
        });
        me.MapGet("/export", async (RequestUser u, AccountService s, CancellationToken ct) =>
            Results.File(await s.PersonalDataJsonAsync(u.Context, ct), "application/json", "budgetplaner-meine-daten.json"))
            .WithSummary("Datenauskunft: alle Daten als JSON (DSGVO Art. 15/20).");
        me.MapDelete("/", async (RequestUser u, AccountService s, CancellationToken ct) =>
        {
            await s.DeleteAccountAsync(u.Context, ct);
            return Results.NoContent();
        }).WithSummary("Konto löschen (F-111): persönliche Daten sofort; Haushaltsdaten anderer Mitglieder bleiben.");

        // ---------------- Haushalt (F-002, F-003) ----------------
        var hh = app.MapGroup("/api/v1/household").WithTags("Haushalt").Authed();
        hh.MapGet("/", async (RequestUser u, HouseholdService s, CancellationToken ct) => Results.Ok(await s.GetInfoAsync(u.Context, ct)))
            .WithSummary("Haushalt, Einstellungen und Mitglieder.");
        hh.MapPatch("/", async (HouseholdSettingsUpdate body, RequestUser u, HouseholdService s, CancellationToken ct) =>
            Results.Ok(await s.UpdateSettingsAsync(u.Context, body, ct))).WithSummary("Name, Währung, Monatsstart-Tag, Ampel-Regeln (nur Owner).");
        hh.MapDelete("/", async (RequestUser u, HouseholdService s, CancellationToken ct) =>
        {
            await s.DeleteHouseholdAsync(u.Context, ct);
            return Results.NoContent();
        }).WithSummary("Haushalt mit allen Daten löschen (nur Owner).");
        hh.MapPost("/leave", async (RequestUser u, HouseholdService s, CancellationToken ct) =>
        {
            await s.LeaveAsync(u.Context, ct);
            return Results.NoContent();
        }).WithSummary("Haushalt verlassen (Mitglieder).");
        hh.MapPost("/transfer-ownership", async (TransferOwnershipRequest body, RequestUser u, HouseholdService s, CancellationToken ct) =>
        {
            await s.TransferOwnershipAsync(u.Context, body.UserId, ct);
            return Results.NoContent();
        }).WithSummary("Owner-Rolle übertragen.");
        hh.MapDelete("/members/{userId:guid}", async (Guid userId, RequestUser u, HouseholdService s, CancellationToken ct) =>
        {
            await s.RemoveMemberAsync(u.Context, userId, ct);
            return Results.NoContent();
        }).WithSummary("Mitglied entfernen – Zugriff endet sofort (nur Owner).");
        hh.MapGet("/invitations", async (RequestUser u, HouseholdService s, CancellationToken ct) => Results.Ok(await s.ListInvitationsAsync(u.Context, ct)));
        hh.MapPost("/invitations", async (CreateInvitationRequest body, RequestUser u, HouseholdService s, CancellationToken ct) =>
            Results.Ok(await s.CreateInvitationAsync(u.Context, body, ct))).WithSummary("Einladung per Link/E-Mail erstellen (nur Owner).");
        hh.MapDelete("/invitations/{id:guid}", async (Guid id, RequestUser u, HouseholdService s, CancellationToken ct) =>
        {
            await s.RevokeInvitationAsync(u.Context, id, ct);
            return Results.NoContent();
        });

        var inv = app.MapGroup("/api/v1/invitations").WithTags("Haushalt");
        inv.MapGet("/{token}", async (string token, HttpContext http, HouseholdService s, CancellationToken ct) =>
        {
            var ctx = await UserContextFilter.ResolveAsync(http, required: false);
            return Results.Ok(await s.GetInvitationInfoAsync(token, ctx, ct));
        }).WithSummary("Informationen zu einer Einladung (auch ohne Anmeldung).");
        inv.MapPost("/{token}/accept", async (string token, AcceptInvitationRequest body, HttpContext http, HouseholdService s, CancellationToken ct) =>
        {
            var ctx = await UserContextFilter.ResolveAsync(http, required: true);
            await s.AcceptInvitationAsync(ctx!, token, body.ConfirmLeave, ct);
            return Results.NoContent();
        }).RequireAuthorization().WithSummary("Einladung annehmen.");

        // ---------------- Synchronisation (F-131, F-132) ----------------
        var sync = app.MapGroup("/api/v1/sync").WithTags("Synchronisation").Authed();
        sync.MapGet("/", async ([FromQuery] long? since, RequestUser u, SyncService s, CancellationToken ct) => Results.Ok(await s.PullAsync(u.Context, since ?? 0, ct)))
            .WithSummary("Änderungen seit Sequenz holen (inkl. Löschungen); since=0 → Vollabgleich.");
        sync.MapPost("/", async (SyncPushRequest body, RequestUser u, SyncService s, CancellationToken ct) => Results.Ok(await s.PushAsync(u.Context, body, ct)))
            .WithSummary("Offline-Änderungen übertragen; feldweiser Merge, Konflikte landen im Änderungsverlauf.");
    }
}
