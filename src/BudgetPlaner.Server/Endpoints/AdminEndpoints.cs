using System.Diagnostics;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Server.Hosting;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BudgetPlaner.Server.Endpoints;

/// <summary>Verwaltungsbereich für Serveradministratoren (App:AdminEmails): Konten, Registrierung, Status, Sicherung.</summary>
public static class AdminEndpoints
{
    private static readonly DateTimeOffset StartedAt = new(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment env)
    {
        var admin = app.MapGroup("/api/v1/admin").WithTags("Verwaltung").Authed().AddEndpointFilter(RequireAdmin);

        admin.MapGet("/overview", async (AdminService s, RegistrationPolicy registration, BackupService backups, IAppDbContext db,
            IEmailSender mail, IPushSender push, CancellationToken ct) =>
        {
            var stats = await s.StatsAsync(ct);
            var backup = backups.Describe();
            return Results.Ok(new AdminOverviewDto(stats.Users, stats.Active30, stats.New30, stats.Blocked, stats.Households,
                (await registration.GetAsync(ct)).Mode, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", env.EnvironmentName,
                StartedAt, Environment.WorkingSet, DatabaseBytes(db), backup.Count, backup.LastAt, mail.IsSmtpConfigured, push.IsConfigured));
        }).WithSummary("Serverstatus: Konten, Speicher, Datenbank, Sicherungen, E-Mail/Push.");

        admin.MapGet("/users", async ([FromQuery] string? search, [FromQuery] int? take, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.UsersAsync(search, take ?? 500, ct))).WithSummary("Alle Konten mit Haushalt, letzter Aktivität und Geräten.");
        admin.MapPost("/users/{id:guid}/block", async (Guid id, AdminBlockRequest? body, RequestUser u, AdminService s, CancellationToken ct) =>
        {
            await s.BlockAsync(u.Context, id, body?.Reason, ct);
            return Results.NoContent();
        }).WithSummary("Konto sperren: keine Anmeldung, alle Sitzungen und API-Zugänge werden sofort beendet.");
        admin.MapPost("/users/{id:guid}/unblock", async (Guid id, RequestUser u, AdminService s, CancellationToken ct) =>
        {
            await s.UnblockAsync(u.Context, id, ct);
            return Results.NoContent();
        }).WithSummary("Sperre aufheben.");
        admin.MapDelete("/users/{id:guid}", async (Guid id, RequestUser u, AdminService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(u.Context, id, ct);
            return Results.NoContent();
        }).WithSummary("Konto endgültig löschen (wie F-111: Haushalt bleibt für weitere Mitglieder erhalten).");

        admin.MapGet("/registration", async (AdminService s, CancellationToken ct) => Results.Ok(await s.GetRegistrationAsync(ct)))
            .WithSummary("Registrierungsmodus und Freigabeliste.");
        admin.MapPut("/registration", async (RegistrationSettingsDto body, RequestUser u, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.SetRegistrationAsync(u.Context, body, ct))).WithSummary("Registrierung: open, approval, allowlist oder closed.");
        admin.MapGet("/registration-requests", async (AdminService s, CancellationToken ct) => Results.Ok(await s.RequestsAsync(ct)))
            .WithSummary("Offene Registrierungsanfragen (Modus „approval“).");
        admin.MapPost("/registration-requests/{id:guid}/approve", async (Guid id, RequestUser u, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.ApproveAsync(u.Context, id, ct)))
            .WithSummary("Anfrage freischalten: Adresse auf die Freigabeliste, Mail mit Anmeldelink an die Person.");
        admin.MapDelete("/registration-requests/{id:guid}", async (Guid id, RequestUser u, AdminService s, CancellationToken ct) =>
        {
            await s.RejectAsync(u.Context, id, ct);
            return Results.NoContent();
        }).WithSummary("Anfrage ablehnen (ohne Mail).");

        admin.MapPost("/backup", async (BackupService backups, CancellationToken ct) =>
        {
            if (await backups.CreateAsync(ct) is null) return Results.Problem(statusCode: 409, title: "Backup not supported");
            var info = backups.Describe();
            return Results.Ok(new { info.Count, info.LastAt });
        }).WithSummary("Sofort eine Sicherung der Datenbank erstellen (z. B. vor einem Update).");
    }

    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.RequestServices.GetRequiredService<RequestUser>().Context.RequireAdmin();
        return await next(context);
    }

    private static long DatabaseBytes(IAppDbContext db)
    {
        var file = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return 0;
        var wal = file + "-wal";
        return new FileInfo(file).Length + (File.Exists(wal) ? new FileInfo(wal).Length : 0);
    }
}
