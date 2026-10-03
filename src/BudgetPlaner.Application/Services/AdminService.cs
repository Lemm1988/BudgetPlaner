using System.Globalization;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Application.Services;

/// <summary>Ergebnis der Registrierungsprüfung für eine Adresse ohne Konto.</summary>
public enum RegistrationDecision
{
    Allowed,

    /// <summary>Modus „approval“: keine Mail, stattdessen Anfrage an die Administratoren.</summary>
    NeedsApproval,
    Denied,
}

/// <summary>
/// Wer darf ein neues Konto anlegen? Einstellbar durch den Administrator:
/// „open“ = jeder; „approval“ = Freigegebene sofort, alle anderen stellen eine Anfrage, die ein Administrator freischaltet;
/// „allowlist“ = nur freigegebene Adressen/Domains sowie Eingeladene (E-Mail-Einladung an die Adresse oder Anmeldung
/// über einen gültigen Einladungslink); „closed“ = niemand. Administratoren (App:AdminEmails) dürfen sich immer
/// registrieren. Bestehende Konten sind nie betroffen. An abgelehnte oder wartende Adressen geht keine Mail.
/// </summary>
public sealed class RegistrationPolicy(IAppDbContext db, IClock clock, IOptions<AppOptions> options, IEmailQueue mail, IPublicUrlProvider urls,
    ILogger<RegistrationPolicy> logger)
{
    public const string ModeKey = "registration.mode";
    public const string AllowListKey = "registration.allowlist";
    public const string LastAdminMailKey = "registration.lastAdminMail";
    public const int MaxPendingRequests = 200;
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan AdminMailInterval = TimeSpan.FromMinutes(30);

    public async Task<RegistrationSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.ServerSettings.AsNoTracking().Where(s => s.Key == ModeKey || s.Key == AllowListKey).ToListAsync(ct);
        var mode = settings.FirstOrDefault(s => s.Key == ModeKey)?.Value;
        var list = settings.FirstOrDefault(s => s.Key == AllowListKey)?.Value ?? "";
        var fallback = RegistrationModes.All.Contains(options.Value.DefaultRegistrationMode) ? options.Value.DefaultRegistrationMode : RegistrationModes.Open;
        return new RegistrationSettingsDto(RegistrationModes.All.Contains(mode) ? mode! : fallback, ParseList(list));
    }

    /// <param name="returnUrl">Ziel nach der Anmeldung – „/einladung/{token}“ mit gültigem Token zählt als Einladung.</param>
    public async Task<RegistrationDecision> CheckAsync(string email, string? returnUrl = null, CancellationToken ct = default)
    {
        if (options.Value.IsAdmin(email)) return RegistrationDecision.Allowed;
        var settings = await GetAsync(ct);
        switch (settings.Mode)
        {
            case RegistrationModes.Open:
                return RegistrationDecision.Allowed;
            case RegistrationModes.AllowList or RegistrationModes.Approval:
                if (await IsApprovedOrInvitedAsync(email, returnUrl, settings.AllowList, ct)) return RegistrationDecision.Allowed;
                return settings.Mode == RegistrationModes.Approval ? RegistrationDecision.NeedsApproval : RegistrationDecision.Denied;
            default:
                return RegistrationDecision.Denied;
        }
    }

    public async Task<bool> CanRegisterAsync(string email, string? returnUrl = null, CancellationToken ct = default) =>
        await CheckAsync(email, returnUrl, ct) == RegistrationDecision.Allowed;

    private async Task<bool> IsApprovedOrInvitedAsync(string email, string? returnUrl, IReadOnlyList<string> allowList, CancellationToken ct)
    {
        if (Matches(email, allowList)) return true;
        var now = clock.UtcNow;
        var valid = db.Invitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);
        if (await valid.AnyAsync(i => i.Email == email, ct)) return true;
        if (InvitationToken(returnUrl) is not { } token) return false;
        var hash = TokenHelper.Sha256(token);
        return await valid.AnyAsync(i => i.TokenHash == hash, ct);
    }

    /// <summary>
    /// Anfrage einer unbekannten Adresse speichern (Modus „approval“). Die Adresse selbst bekommt keine Mail; die
    /// Administratoren werden höchstens alle 30 Minuten per Mail auf neue Anfragen hingewiesen.
    /// </summary>
    public async Task RequestAccessAsync(string email, string language, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var cutoff = now - RequestLifetime;
        await db.RegistrationRequests.Where(r => r.LastRequestedAt < cutoff).ExecuteDeleteAsync(ct);

        var existing = await db.RegistrationRequests.FirstOrDefaultAsync(r => r.Email == email, ct);
        if (existing is not null)
        {
            existing.LastRequestedAt = now;
            existing.Count++;
            await db.SaveChangesAsync(ct);
            return;
        }
        if (await db.RegistrationRequests.CountAsync(ct) >= MaxPendingRequests)
        {
            logger.LogWarning("Registrierungsanfrage verworfen: bereits {Max} offene Anfragen", MaxPendingRequests);
            return;
        }
        db.RegistrationRequests.Add(new RegistrationRequest
        {
            Id = Guid.NewGuid(), Email = email, Language = Texts.Normalize(language), CreatedAt = now, LastRequestedAt = now, Count = 1,
        });
        var notify = await ClaimAdminMailSlotAsync(now, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return; // gleichzeitige Anfrage derselben Adresse – die andere wurde gespeichert
        }
        logger.LogInformation("Neue Registrierungsanfrage wartet auf Freischaltung");
        if (!notify) return;

        var link = $"{urls.BaseUrl.TrimEnd('/')}/verwaltung";
        foreach (var admin in options.Value.AdminEmailList().Select(TokenHelper.NormalizeEmail).Distinct())
        {
            var lang = await db.Users.AsNoTracking().Where(u => u.Email == admin).Select(u => u.Language).FirstOrDefaultAsync(ct) ?? Texts.German;
            mail.Enqueue(EmailTemplates.RegistrationRequested(lang, admin, email, link));
        }
    }

    /// <summary>Höchstens eine Hinweis-Mail je <see cref="AdminMailInterval"/> – Schutz vor Mailfluten durch Bots.</summary>
    private async Task<bool> ClaimAdminMailSlotAsync(DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == LastAdminMailKey, ct);
        if (row is not null && DateTimeOffset.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            && now - last < AdminMailInterval) return false;
        if (row is null) db.ServerSettings.Add(row = new ServerSetting { Key = LastAdminMailKey });
        row.Value = now.ToString("O", CultureInfo.InvariantCulture);
        row.UpdatedAt = now;
        return true;
    }

    private static string? InvitationToken(string? returnUrl)
    {
        const string prefix = "/einladung/";
        if (returnUrl is null || !returnUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = Uri.UnescapeDataString(returnUrl[prefix.Length..].Split('?', '#')[0]).Trim();
        return token.Length is > 0 and < 200 ? token : null;
    }

    /// <summary>Eintrag „name@beispiel.de“ gilt für genau diese Adresse, „@beispiel.de“ für die ganze Domain.</summary>
    public static bool Matches(string email, IEnumerable<string> allowList)
    {
        var e = email.Trim().ToLowerInvariant();
        return allowList.Any(a => a.StartsWith('@') ? e.EndsWith(a, StringComparison.Ordinal) : e == a);
    }

    public static List<string> ParseList(string? text) =>
        (text ?? "").Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .Where(x => x.Contains('@'))
            .Distinct()
            .ToList();
}

/// <summary>Serververwaltung (nur Administratoren): Konten, Sperren, Löschen, Registrierungsmodus und -anfragen.</summary>
public sealed class AdminService(IAppDbContext db, IClock clock, IOptions<AppOptions> options, IRealtimeNotifier realtime,
    AccountService accounts, RegistrationPolicy registration, IEmailQueue mail, IPublicUrlProvider urls, ILogger<AdminService> logger)
{
    public sealed record UserStats(int Users, int Active30, int New30, int Blocked, int Households);

    public async Task<UserStats> StatsAsync(CancellationToken ct = default)
    {
        var since = clock.UtcNow.AddDays(-30);
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.CreatedAt, u.LastLoginAt, u.BlockedAt, u.HouseholdId }).ToListAsync(ct);
        var recentSessions = await db.Sessions.AsNoTracking().Where(s => s.LastUsedAt >= since).Select(s => s.UserId).Distinct().ToListAsync(ct);
        var active = users.Count(u => u.LastLoginAt >= since || recentSessions.Contains(u.Id));
        return new UserStats(users.Count, active, users.Count(u => u.CreatedAt >= since), users.Count(u => u.BlockedAt != null),
            users.Select(u => u.HouseholdId).Distinct().Count());
    }

    public async Task<List<AdminUserDto>> UsersAsync(string? search, int take, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.Email.Contains(s) || u.Name.ToLower().Contains(s));
        }
        var users = await query.OrderByDescending(u => u.CreatedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        var ids = users.Select(u => u.Id).ToList();
        var householdIds = users.Select(u => u.HouseholdId).Distinct().ToList();
        var sessions = await db.Sessions.AsNoTracking().Where(s => ids.Contains(s.UserId) && s.RevokedAt == null)
            .GroupBy(s => s.UserId).Select(g => new { UserId = g.Key, Count = g.Count(), Last = g.Max(x => x.LastUsedAt) }).ToListAsync(ct);
        var households = await db.Households.AsNoTracking().Where(h => householdIds.Contains(h.Id)).ToListAsync(ct);
        var members = await db.Users.AsNoTracking().Where(u => householdIds.Contains(u.HouseholdId))
            .GroupBy(u => u.HouseholdId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return users.Select(u =>
        {
            var s = sessions.FirstOrDefault(x => x.UserId == u.Id);
            DateTimeOffset? last = s?.Last is { } l && (u.LastLoginAt is null || l > u.LastLoginAt) ? l : u.LastLoginAt;
            return new AdminUserDto(u.Id, u.Email, u.Name, u.CreatedAt, last, u.HouseholdId,
                households.FirstOrDefault(h => h.Id == u.HouseholdId)?.Name ?? "", members.FirstOrDefault(m => m.Key == u.HouseholdId)?.Count ?? 1,
                u.Role, options.Value.IsAdmin(u.Email), u.BlockedAt, u.BlockedReason, s?.Count ?? 0);
        }).ToList();
    }

    /// <summary>Sperren: keine Anmeldung mehr, alle Sitzungen und API-Zugänge sofort ungültig.</summary>
    public async Task BlockAsync(UserContext admin, Guid userId, string? reason, CancellationToken ct = default)
    {
        admin.RequireAdmin();
        if (userId == admin.UserId) throw AppException.BadRequest("err.admin.self");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound();
        if (options.Value.IsAdmin(user.Email)) throw AppException.BadRequest("err.admin.otherAdmin");
        var now = clock.UtcNow;
        user.BlockedAt = now;
        user.BlockedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];
        await db.SaveChangesAsync(ct);
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await db.ApiTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await db.PushSubscriptions.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await realtime.AccessRevokedAsync(userId);
        logger.LogWarning("Verwaltung: Konto {UserId} durch {AdminId} gesperrt", userId, admin.UserId);
    }

    public async Task UnblockAsync(UserContext admin, Guid userId, CancellationToken ct = default)
    {
        admin.RequireAdmin();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound();
        user.BlockedAt = null;
        user.BlockedReason = null;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Verwaltung: Sperre von Konto {UserId} durch {AdminId} aufgehoben", userId, admin.UserId);
    }

    /// <summary>Konto löschen wie durch den Nutzer selbst (F-111): Owner-Rolle geht an ein anderes Mitglied, Daten anderer bleiben.</summary>
    public async Task DeleteAsync(UserContext admin, Guid userId, CancellationToken ct = default)
    {
        admin.RequireAdmin();
        if (userId == admin.UserId) throw AppException.BadRequest("err.admin.self");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound();
        if (options.Value.IsAdmin(user.Email)) throw AppException.BadRequest("err.admin.otherAdmin");
        await accounts.DeleteAccountAsync(new UserContext(user, null), ct);
        logger.LogWarning("Verwaltung: Konto {UserId} durch {AdminId} gelöscht", userId, admin.UserId);
    }

    public Task<RegistrationSettingsDto> GetRegistrationAsync(CancellationToken ct = default) => registration.GetAsync(ct);

    public async Task<RegistrationSettingsDto> SetRegistrationAsync(UserContext admin, RegistrationSettingsDto settings, CancellationToken ct = default)
    {
        admin.RequireAdmin();
        if (!RegistrationModes.All.Contains(settings.Mode)) throw AppException.BadRequest("err.validation");
        var list = RegistrationPolicy.ParseList(string.Join('\n', settings.AllowList ?? []));
        await UpsertAsync(RegistrationPolicy.ModeKey, settings.Mode, admin.UserId, ct);
        await UpsertAsync(RegistrationPolicy.AllowListKey, string.Join('\n', list), admin.UserId, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Verwaltung: Registrierungsmodus „{Mode}“ ({Count} Freigaben) durch {AdminId}", settings.Mode, list.Count, admin.UserId);
        return await registration.GetAsync(ct);
    }

    public async Task<List<RegistrationRequestDto>> RequestsAsync(CancellationToken ct = default)
    {
        var cutoff = clock.UtcNow - RegistrationPolicy.RequestLifetime;
        var requests = await db.RegistrationRequests.AsNoTracking().Where(r => r.LastRequestedAt >= cutoff)
            .OrderByDescending(r => r.LastRequestedAt).ToListAsync(ct);
        return requests.Select(r => new RegistrationRequestDto(r.Id, r.Email, r.CreatedAt, r.LastRequestedAt, r.Count)).ToList();
    }

    /// <summary>Freischalten: Adresse auf die Freigabeliste, Anfrage entfernen, Mail mit Anmeldelink an die Person.</summary>
    public async Task<RegistrationSettingsDto> ApproveAsync(UserContext admin, Guid requestId, CancellationToken ct = default)
    {
        admin.RequireAdmin();
        var request = await db.RegistrationRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw AppException.NotFound();
        var settings = await registration.GetAsync(ct);
        if (!RegistrationPolicy.Matches(request.Email, settings.AllowList))
            await UpsertAsync(RegistrationPolicy.AllowListKey, string.Join('\n', settings.AllowList.Append(request.Email)), admin.UserId, ct);
        db.RegistrationRequests.Remove(request);
        await db.SaveChangesAsync(ct);
        var link = $"{urls.BaseUrl.TrimEnd('/')}/login?email={Uri.EscapeDataString(request.Email)}";
        mail.Enqueue(EmailTemplates.RegistrationApproved(request.Language, request.Email, link));
        logger.LogWarning("Verwaltung: Registrierungsanfrage {RequestId} durch {AdminId} freigeschaltet", requestId, admin.UserId);
        return await registration.GetAsync(ct);
    }

    /// <summary>Ablehnen: Anfrage entfernen – ohne Mail (die Adresse könnte missbräuchlich eingetragen worden sein).</summary>
    public async Task RejectAsync(UserContext admin, Guid requestId, CancellationToken ct = default)
    {
        admin.RequireAdmin();
        var deleted = await db.RegistrationRequests.Where(r => r.Id == requestId).ExecuteDeleteAsync(ct);
        if (deleted == 0) throw AppException.NotFound();
        logger.LogWarning("Verwaltung: Registrierungsanfrage {RequestId} durch {AdminId} abgelehnt", requestId, admin.UserId);
    }

    private async Task UpsertAsync(string key, string value, Guid by, CancellationToken ct)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.ServerSettings.Add(row = new ServerSetting { Key = key });
        row.Value = value;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = by;
    }
}
