using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Application.Services;

/// <summary>Profil, Benachrichtigungen, Sitzungen, API-Zugänge und Push-Abos (F-100, F-080, F-130).</summary>
public sealed class ProfileService(
    IAppDbContext db,
    IClock clock,
    IEmailQueue mail,
    ITokenService tokens,
    SyncService sync,
    IRealtimeNotifier realtime,
    IOptions<AppOptions> options)
{
    public static readonly string[] Themes = ["system", "light", "dark"];

    public async Task<UserDto> UpdateAsync(UserContext ctx, ProfileUpdate update, CancellationToken ct = default)
    {
        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        var nameChanged = false;
        if (update.Name is not null)
        {
            var name = update.Name.Trim();
            if (name.Length == 0) throw AppException.BadRequest("err.name.required");
            if (name.Length > 80) name = name[..80];
            nameChanged = name != user.Name;
            user.Name = name;
        }
        if (update.Language is not null) user.Language = Texts.Normalize(update.Language);
        if (update.TimeZone is not null && IsValidTimeZone(update.TimeZone)) user.TimeZone = update.TimeZone;
        if (update.Theme is not null && Themes.Contains(update.Theme)) user.Theme = update.Theme;
        await db.SaveChangesAsync(ct);
        // F-100: Namensänderung sofort für alle Mitglieder sichtbar.
        if (nameChanged) await sync.BumpAsync(user.HouseholdId, ct);
        await realtime.ProfileChangedAsync(user.Id);
        return AuthService.ToDto(user, options.Value.IsAdmin(user.Email));
    }

    public async Task RequestEmailChangeAsync(UserContext ctx, string newEmail, CancellationToken ct = default)
    {
        var email = TokenHelper.NormalizeEmail(newEmail);
        if (!TokenHelper.IsValidEmail(email)) throw AppException.BadRequest("err.email.invalid");
        if (await db.Users.AnyAsync(u => u.Email == email && u.Id != ctx.UserId, ct)) throw AppException.Conflict("err.email.taken");
        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        var code = TokenHelper.Code6();
        user.PendingEmail = email;
        user.PendingEmailCodeHash = TokenHelper.Sha256($"{code}:{user.Id}");
        user.PendingEmailExpiresAt = clock.UtcNow.AddMinutes(options.Value.LoginTokenMinutes);
        await db.SaveChangesAsync(ct);
        mail.Enqueue(EmailTemplates.EmailChange(user.Language, email, code));
    }

    public async Task<UserDto> ConfirmEmailChangeAsync(UserContext ctx, string code, CancellationToken ct = default)
    {
        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (user.PendingEmail is null || user.PendingEmailExpiresAt < clock.UtcNow
            || !TokenHelper.FixedTimeEquals(TokenHelper.Sha256($"{digits}:{user.Id}"), user.PendingEmailCodeHash ?? ""))
            throw AppException.BadRequest("err.login.invalid");
        if (await db.Users.AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id, ct)) throw AppException.Conflict("err.email.taken");
        user.Email = user.PendingEmail;
        user.PendingEmail = null;
        user.PendingEmailCodeHash = null;
        user.PendingEmailExpiresAt = null;
        await db.SaveChangesAsync(ct);
        await sync.BumpAsync(user.HouseholdId, ct);
        return AuthService.ToDto(user, options.Value.IsAdmin(user.Email));
    }

    // ---------------------------------------------------------------- Benachrichtigungen

    public async Task<List<NotificationSettingDto>> GetNotificationSettingsAsync(UserContext ctx, CancellationToken ct = default)
    {
        var rows = await db.NotificationSettings.AsNoTracking().Where(s => s.UserId == ctx.UserId).ToListAsync(ct);
        return (from type in Enum.GetValues<NotificationType>()
                from channel in Enum.GetValues<NotificationChannel>()
                let row = rows.FirstOrDefault(r => r.Type == type && r.Channel == channel)
                select new NotificationSettingDto(type, channel, row?.Active ?? NotificationDefaults.DefaultActive(type, channel))).ToList();
    }

    /// <summary>Jede Art einzeln (de)aktivierbar (F-081); Monatsstart-Erinnerung optional (F-080).</summary>
    public async Task<List<NotificationSettingDto>> UpdateNotificationSettingsAsync(UserContext ctx, IEnumerable<NotificationSettingDto> settings, CancellationToken ct = default)
    {
        var rows = await db.NotificationSettings.Where(s => s.UserId == ctx.UserId).ToListAsync(ct);
        foreach (var s in settings)
        {
            var row = rows.FirstOrDefault(r => r.Type == s.Type && r.Channel == s.Channel);
            if (row is null)
            {
                row = new NotificationSetting { Id = Guid.NewGuid(), UserId = ctx.UserId, Type = s.Type, Channel = s.Channel };
                db.NotificationSettings.Add(row);
                rows.Add(row);
            }
            row.Active = s.Active;
        }
        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        user.ReminderActive = Enum.GetValues<NotificationChannel>().Any(c =>
            rows.FirstOrDefault(r => r.Type == NotificationType.MonthStart && r.Channel == c)?.Active ?? NotificationDefaults.DefaultActive(NotificationType.MonthStart, c));
        await db.SaveChangesAsync(ct);
        return await GetNotificationSettingsAsync(ctx, ct);
    }

    // ---------------------------------------------------------------- Sitzungen & Zugänge

    public async Task<List<SessionDto>> SessionsAsync(UserContext ctx, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var list = await db.Sessions.AsNoTracking().Where(s => s.UserId == ctx.UserId && s.RevokedAt == null).ToListAsync(ct);
        return list.Where(s => s.ExpiresAt > now).OrderByDescending(s => s.LastUsedAt)
            .Select(s => new SessionDto(s.Id, s.DeviceName, s.CreatedAt, s.LastUsedAt, s.Id == ctx.SessionId)).ToList();
    }

    public async Task RevokeSessionAsync(UserContext ctx, Guid id, CancellationToken ct = default)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == ctx.UserId, ct) ?? throw AppException.NotFound();
        s.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ApiTokenDto>> ApiTokensAsync(UserContext ctx, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var list = await db.ApiTokens.AsNoTracking().Where(t => t.UserId == ctx.UserId && t.RevokedAt == null).ToListAsync(ct);
        return list.Where(t => t.ExpiresAt > now).OrderByDescending(t => t.CreatedAt).Select(ToDto).ToList();
    }

    public async Task<CreateApiTokenResponse> CreateApiTokenAsync(UserContext ctx, CreateApiTokenRequest request, CancellationToken ct = default)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) throw AppException.BadRequest("err.token.name");
        var now = clock.UtcNow;
        var token = new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = ctx.UserId,
            Name = name.Length > 80 ? name[..80] : name,
            Jti = TokenHelper.RandomToken(16),
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(request.ExpiresInDays, 1, 365)),
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return new CreateApiTokenResponse(tokens.CreateApiToken(ctx.User, token), ToDto(token));
    }

    public async Task RevokeApiTokenAsync(UserContext ctx, Guid id, CancellationToken ct = default)
    {
        var t = await db.ApiTokens.FirstOrDefaultAsync(x => x.Id == id && x.UserId == ctx.UserId, ct) ?? throw AppException.NotFound();
        t.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<PasskeyDto>> PasskeysAsync(UserContext ctx, CancellationToken ct = default) =>
        (await db.Passkeys.AsNoTracking().Where(p => p.UserId == ctx.UserId).OrderBy(p => p.CreatedAt).ToListAsync(ct))
        .Select(p => new PasskeyDto(p.Id, p.Name, p.CreatedAt, p.LastUsedAt)).ToList();

    public async Task DeletePasskeyAsync(UserContext ctx, Guid id, CancellationToken ct = default) =>
        await db.Passkeys.Where(p => p.Id == id && p.UserId == ctx.UserId).ExecuteDeleteAsync(ct);

    // ---------------------------------------------------------------- Push & Feedback

    public async Task SubscribePushAsync(UserContext ctx, PushSubscriptionRequest request, CancellationToken ct = default)
    {
        if (!IsAllowedPushEndpoint(request.Endpoint, options.Value.PushEndpointHosts))
            throw AppException.BadRequest("err.push.endpoint");
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
        if (existing is null)
        {
            existing = new PushSubscriptionEntity { Id = Guid.NewGuid(), Endpoint = request.Endpoint, CreatedAt = clock.UtcNow };
            db.PushSubscriptions.Add(existing);
        }
        existing.UserId = ctx.UserId;
        existing.P256dh = request.P256dh;
        existing.Auth = request.Auth;
        existing.DeviceName = request.DeviceName;
        existing.FailureCount = 0;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nur HTTPS-Adressen bekannter Push-Dienste, keine IP-Adressen (SSRF-Schutz).</summary>
    public static bool IsAllowedPushEndpoint(string? endpoint, IEnumerable<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return allowedHosts.Any(a =>
        {
            var allowed = a.Trim().ToLowerInvariant();
            return allowed.StartsWith('.') ? host.EndsWith(allowed, StringComparison.Ordinal) && host.Length > allowed.Length : host == allowed;
        });
    }

    public async Task UnsubscribePushAsync(UserContext ctx, string endpoint, CancellationToken ct = default) =>
        await db.PushSubscriptions.Where(s => s.UserId == ctx.UserId && s.Endpoint == endpoint).ExecuteDeleteAsync(ct);

    public async Task FeedbackAsync(UserContext? ctx, FeedbackRequest request, CancellationToken ct = default)
    {
        var message = (request.Message ?? "").Trim();
        if (message.Length == 0) throw AppException.BadRequest("err.validation");
        db.Feedback.Add(new Feedback
        {
            Id = Guid.NewGuid(), UserId = ctx?.UserId, Message = message.Length > 4000 ? message[..4000] : message,
            Rating = request.Rating is >= 1 and <= 5 ? request.Rating : null, Page = request.Page, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public static bool IsValidTimeZone(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception) { return false; }
    }

    private static ApiTokenDto ToDto(ApiToken t) => new(t.Id, t.Name, t.CreatedAt, t.ExpiresAt, t.LastUsedAt);
}
