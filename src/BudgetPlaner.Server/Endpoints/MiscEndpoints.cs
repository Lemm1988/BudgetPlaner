using System.Net;
using System.Text;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Domain;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Server.Endpoints;

public static class MiscEndpoints
{
    public static void MapMiscEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment env)
    {
        var g = app.MapGroup("/api/v1").Authed();

        // ---------- Benachrichtigungen ----------
        var n = g.MapGroup("/notifications").WithTags("Benachrichtigungen");
        n.MapGet("/", async ([FromQuery] int? take, RequestUser u, NotificationService s, CancellationToken ct) => Results.Ok(await s.ListAsync(u.Context, take ?? 50, ct)));
        n.MapPost("/{id:guid}/read", async (Guid id, RequestUser u, NotificationService s, CancellationToken ct) =>
        {
            await s.MarkReadAsync(u.Context, id, ct);
            return Results.NoContent();
        });
        n.MapPost("/read-all", async (RequestUser u, NotificationService s, CancellationToken ct) =>
        {
            await s.MarkReadAsync(u.Context, null, ct);
            return Results.NoContent();
        });
        n.MapDelete("/{id:guid}", async (Guid id, RequestUser u, NotificationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(u.Context, id, ct);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/push/vapid-key", (IPushSender push) => Results.Ok(new VapidKeyResponse(push.PublicKey ?? "")))
            .WithTags("Benachrichtigungen").WithSummary("Öffentlicher VAPID-Schlüssel für Web-Push.");

        // ---------- Export / Import (F-110, F-133) ----------
        var io = g.MapGroup("").WithTags("Export & Import");
        io.MapGet("/export", async ([FromQuery] string from, [FromQuery] string to, [FromQuery] string? format, RequestUser u, ExportService s, CancellationToken ct) =>
        {
            if (!Period.TryParse(from, out var f) || !Period.TryParse(to, out var t)) throw AppException.BadRequest("err.validation");
            var (content, type, name) = await s.ExportAsync(u.Context, f, t, format ?? "xlsx", ct);
            return Results.File(content, type, name);
        }).WithSummary("Datenexport als Excel (xlsx), CSV (zip) oder JSON für einen Zeitraum.");
        io.MapPost("/import/preview", async (IFormFile file, RequestUser u, ImportService s) =>
        {
            if (file.Length is 0 or > 10_000_000) throw AppException.BadRequest("err.import.empty");
            await using var stream = file.OpenReadStream();
            return Results.Ok(s.Preview(u.Context, stream, file.FileName));
        }).DisableAntiforgery().WithSummary("CSV/Excel hochladen → Spalten und Zuordnungsvorschlag.");
        io.MapPost("/import/validate", async (ImportMappingRequest body, RequestUser u, ImportService s, CancellationToken ct) =>
            Results.Ok(await s.ValidateAsync(u.Context, body, ct))).WithSummary("Zuordnung prüfen; fehlerhafte Zeilen werden markiert.");
        io.MapPost("/import/commit", async (ImportCommitRequest body, RequestUser u, ImportService s, CancellationToken ct) =>
            Results.Ok(await s.CommitAsync(u.Context, body, ct))).WithSummary("Import ausführen; markierte Zeilen können übersprungen werden.");

        // ---------- Demodaten (F-142) ----------
        var demo = g.MapGroup("/demo").WithTags("Beispieldaten");
        demo.MapGet("/", async (RequestUser u, DemoService s, CancellationToken ct) => Results.Ok(await s.StatusAsync(u.Context, ct)));
        demo.MapPost("/", async (RequestUser u, DemoService s, CancellationToken ct) =>
        {
            await s.LoadAsync(u.Context, ct);
            return Results.NoContent();
        }).WithSummary("Beispieldaten in alle Module laden.");
        demo.MapDelete("/", async (RequestUser u, DemoService s, CancellationToken ct) =>
        {
            await s.ClearAsync(u.Context, ct);
            return Results.NoContent();
        }).WithSummary("Beispieldaten mit einem Klick entfernen.");

        // ---------- Änderungsverlauf ----------
        var changes = g.MapGroup("/changes").WithTags("Änderungsverlauf");
        changes.MapGet("/", async ([FromQuery] string? type, [FromQuery] Guid? entityId, [FromQuery] int? take, RequestUser u, ChangeLogService s, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(u.Context, type, entityId, take ?? 100, ct))).WithSummary("Änderungen inkl. Konflikte (I-13).");
        changes.MapPost("/{id:guid}/restore", async (Guid id, RequestUser u, ChangeLogService s, CancellationToken ct) =>
            Results.Ok(await s.RestoreAsync(u.Context, id, ct))).WithSummary("Zustand vor einer Änderung wiederherstellen.");

        // ---------- Sonstiges ----------
        app.MapGet("/api/v1/version", async (IOptions<AppOptions> o, IEmailSender mail, IPushSender push, RegistrationPolicy registration, CancellationToken ct) =>
            Results.Ok(new VersionInfo(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", env.EnvironmentName, o.Value.DevMailbox,
                mail.IsSmtpConfigured, push.IsConfigured, (await registration.GetAsync(ct)).Mode)))
            .WithTags("System");
        app.MapPost("/api/v1/feedback", async (FeedbackRequest body, HttpContext http, ProfileService s, CancellationToken ct) =>
        {
            var ctx = await UserContextFilter.ResolveAsync(http, required: false);
            await s.FeedbackAsync(ctx, body, ct);
            return Results.NoContent();
        }).WithTags("System").WithSummary("Feedback senden (B-22).");
        app.MapGet("/health", async (IAppDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503)).WithTags("System").ExcludeFromDescription();

        // ---------- Entwicklungs-Postfach (nur wenn aktiviert) ----------
        app.MapGet("/dev/postfach", async (IOptions<AppOptions> o, IAppDbContext db, CancellationToken ct) =>
        {
            if (!o.Value.DevMailbox) return Results.NotFound();
            var mails = await db.DevMails.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(50).ToListAsync(ct);
            var sb = new StringBuilder("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
                                       "<title>Dev-Postfach</title><style>body{font-family:system-ui,sans-serif;max-width:900px;margin:24px auto;padding:0 16px;background:#F7F3EA;color:#1E2522}" +
                                       "a{color:#1F5F4A}li{background:#fff;border-radius:10px;padding:12px 16px;margin:8px 0;list-style:none}small{color:#666}</style></head><body>" +
                                       "<h1>Dev-Postfach</h1><p>Hier landen alle Mails, solange <code>App:DevMailbox</code> aktiv ist (Login-Links, Codes, Einladungen).</p><ul style=\"padding:0\">");
            foreach (var m in mails)
                sb.Append($"<li><a href=\"/dev/postfach/{m.Id}\">{WebUtility.HtmlEncode(m.Subject)}</a><br><small>an {WebUtility.HtmlEncode(m.To)} · {m.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}</small></li>");
            sb.Append("</ul></body></html>");
            return Results.Content(sb.ToString(), "text/html; charset=utf-8");
        }).ExcludeFromDescription();
        app.MapGet("/dev/postfach/{id:guid}", async (Guid id, IOptions<AppOptions> o, IAppDbContext db, CancellationToken ct) =>
        {
            if (!o.Value.DevMailbox) return Results.NotFound();
            var mail = await db.DevMails.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            return mail is null ? Results.NotFound() : Results.Content(mail.Html, "text/html; charset=utf-8");
        }).ExcludeFromDescription();
        app.MapGet("/api/v1/dev/latest-mail", async ([FromQuery] string to, IOptions<AppOptions> o, IAppDbContext db, CancellationToken ct) =>
        {
            if (!o.Value.DevMailbox) return Results.NotFound();
            var mail = await db.DevMails.AsNoTracking().Where(m => m.To == to.ToLower()).OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);
            return mail is null ? Results.NotFound() : Results.Ok(new { mail.Subject, mail.Text, mail.CreatedAt });
        }).ExcludeFromDescription();
    }
}
