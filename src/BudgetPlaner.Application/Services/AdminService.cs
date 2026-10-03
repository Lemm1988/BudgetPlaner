using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Application.Services;

/// <summary>
/// Wer darf ein neues Konto anlegen? Einstellbar durch den Administrator:
/// „open“ = jeder, „allowlist“ = freigegebene Adressen/Domains sowie Eingeladene (E-Mail-Einladung an die Adresse
/// oder Anmeldung über einen gültigen Einladungslink), „closed“ = niemand. Administratoren (App:AdminEmails) dürfen
/// sich immer registrieren. Bestehende Konten sind nie betroffen.
/// </summary>
public sealed class RegistrationPolicy(IAppDbContext db, IClock clock, IOptions<AppOptions> options)
{
    public const string ModeKey = "registration.mode";
    public const string AllowListKey = "registration.allowlist";

    public async Task<RegistrationSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.ServerSettings.AsNoTracking().Where(s => s.Key == ModeKey || s.Key == AllowListKey).ToListAsync(ct);
        var mode = settings.FirstOrDefault(s => s.Key == ModeKey)?.Value;
        var list = settings.FirstOrDefault(s => s.Key == AllowListKey)?.Value ?? "";
        return new RegistrationSettingsDto(RegistrationModes.All.Contains(mode) ? mode! : RegistrationModes.Open, ParseList(list));
    }

    /// <param name="returnUrl">Ziel nach der Anmeldung – „/einladung/{token}“ mit gültigem Token zählt als Einladung.</param>
    public async Task<bool> CanRegisterAsync(string email, string? returnUrl = null, CancellationToken ct = default)
    {
        if (options.Value.IsAdmin(email)) return true;
        var settings = await GetAsync(ct);
        switch (settings.Mode)
        {
            case RegistrationModes.Open:
                return true;
            case RegistrationModes.AllowList:
                if (Matches(email, settings.AllowList)) return true;
                var now = clock.UtcNow;
                var valid = db.Invitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);
                if (await valid.AnyAsync(i => i.Email == email, ct)) return true;
                if (InvitationToken(returnUrl) is not { } token) return false;
                var hash = TokenHelper.Sha256(token);
                return await valid.AnyAsync(i => i.TokenHash == hash, ct);
            default:
                return false;
        }
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

/// <summary>Serververwaltung (nur Administratoren): Konten, Sperren, Löschen, Registrierungsmodus.</summary>
public sealed class AdminService(IAppDbContext db, IClock clock, IOptions<AppOptions> options, IRealtimeNotifier realtime,
    AccountService accounts, RegistrationPolicy registration, ILogger<AdminService> logger)
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

    private async Task UpsertAsync(string key, string value, Guid by, CancellationToken ct)
    {
        var row = await db.ServerSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.ServerSettings.Add(row = new ServerSetting { Key = key });
        row.Value = value;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = by;
    }
}
