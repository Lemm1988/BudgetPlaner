using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Application.Services;

/// <summary>Passwortlose Anmeldung (F-001): Magic Link + Code, JWT + rotierende Refresh-Tokens.</summary>
public sealed class AuthService(IAppDbContext db, IClock clock, IEmailQueue mail, ITokenService tokens, IOptions<AppOptions> options, IPublicUrlProvider urls,
    RegistrationPolicy registration)
{
    private const int MaxCodeAttempts = 5;
    private AppOptions Opt => options.Value;

    public async Task<LoginRequestResult> RequestLoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = TokenHelper.NormalizeEmail(request.Email);
        if (!TokenHelper.IsValidEmail(email)) throw AppException.BadRequest("err.email.invalid");
        var now = clock.UtcNow;

        // Sperre und Registrierungsmodus vor dem Rate-Limit prüfen: es wird keine Mail verschickt, die Meldung ist eindeutig.
        var existingUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
        if (existingUser?.BlockedAt is not null) throw AppException.Forbidden("err.account.blocked");
        if (existingUser is null)
        {
            switch (await registration.CheckAsync(email, request.ReturnUrl, ct))
            {
                case RegistrationDecision.Denied:
                    throw AppException.Forbidden("err.registration.closed");
                case RegistrationDecision.NeedsApproval:
                    // „Auf Anfrage“: keine Mail an die (evtl. fremde) Adresse – die Anfrage wartet auf einen Administrator.
                    await registration.RequestAccessAsync(email, Texts.Normalize(request.Language), ct);
                    return new LoginRequestResult(false, 0, PendingApproval: true);
            }
        }

        // Rate-Limit: höchstens eine Anforderung je 60 Sekunden und Adresse.
        var last = await db.LoginTokens.Where(t => t.Email == email).OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (last is not null)
        {
            var elapsed = (now - last.CreatedAt).TotalSeconds;
            if (elapsed < Opt.LoginRateLimitSeconds) throw AppException.TooManyRequests((int)Math.Ceiling(Opt.LoginRateLimitSeconds - elapsed));
        }

        // Nur der neueste Link/Code ist gültig.
        foreach (var old in await db.LoginTokens.Where(t => t.Email == email && t.UsedAt == null).ToListAsync(ct))
            old.UsedAt = now;

        var lang = existingUser?.Language ?? Texts.Normalize(request.Language);
        var token = TokenHelper.RandomToken();
        var code = TokenHelper.Code6();
        var login = new LoginToken
        {
            Id = Guid.NewGuid(),
            Email = email,
            TokenHash = TokenHelper.Sha256(token),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Opt.LoginTokenMinutes),
            ReturnUrl = TokenHelper.SafeReturnUrl(request.ReturnUrl),
            Language = lang,
        };
        login.CodeHash = TokenHelper.Sha256($"{code}:{login.Id}");
        db.LoginTokens.Add(login);
        await db.SaveChangesAsync(ct);

        var link = $"{urls.BaseUrl.TrimEnd('/')}/login/verify?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";
        mail.Enqueue(EmailTemplates.Login(lang, email, link, code));
        return new LoginRequestResult(true, Opt.LoginRateLimitSeconds);
    }

    public async Task<AuthResponse> VerifyAsync(VerifyRequest request, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        LoginToken? login;
        if (!string.IsNullOrWhiteSpace(request.Token))
        {
            var hash = TokenHelper.Sha256(request.Token.Trim());
            login = await db.LoginTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (login is null || login.UsedAt is not null || login.ExpiresAt < now) throw AppException.Unauthorized("err.login.invalid");
        }
        else
        {
            var email = TokenHelper.NormalizeEmail(request.Email);
            var code = new string((request.Code ?? "").Where(char.IsDigit).ToArray());
            login = await db.LoginTokens.Where(t => t.Email == email && t.UsedAt == null && t.ExpiresAt >= now)
                .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
            if (login is null || code.Length != 6) throw AppException.Unauthorized("err.login.invalid");
            if (!TokenHelper.FixedTimeEquals(TokenHelper.Sha256($"{code}:{login.Id}"), login.CodeHash))
            {
                login.FailedAttempts++;
                if (login.FailedAttempts >= MaxCodeAttempts) login.UsedAt = now;
                await db.SaveChangesAsync(ct);
                throw AppException.Unauthorized(login.FailedAttempts >= MaxCodeAttempts ? "err.login.tooManyAttempts" : "err.login.invalid");
            }
        }

        var known = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == login.Email, ct);
        if (known?.BlockedAt is not null) throw AppException.Forbidden("err.account.blocked");
        if (known is null && !await registration.CanRegisterAsync(login.Email, login.ReturnUrl, ct)) throw AppException.Forbidden("err.registration.closed");

        login.UsedAt = now;
        var (user, isNew) = await GetOrCreateUserAsync(login.Email, login.Language, ct);
        user.LastLoginAt = now;
        var response = CreateSession(user, request.DeviceName, isNew, login.ReturnUrl);
        await db.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>Neue Nutzer erhalten automatisch einen eigenen Haushalt mit Standardkategorien (F-002, D-01).</summary>
    public async Task<(User User, bool IsNew)> GetOrCreateUserAsync(string email, string language, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null) return (user, false);
        var now = clock.UtcNow;
        var name = TokenHelper.NameFromEmail(email);
        user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Name = name,
            Language = Texts.Normalize(language),
            TimeZone = Opt.DefaultTimeZone,
            Role = Role.Owner,
            ReminderActive = true,
            CreatedAt = now,
            JoinedAt = now,
        };
        user.HouseholdId = await CreateHouseholdAsync(user, ct);
        db.Users.Add(user);
        db.NotificationSettings.AddRange(NotificationDefaults.For(user.Id));
        return (user, true);
    }

    /// <summary>Legt einen leeren Haushalt für den Nutzer an (auch nach Verlassen/Entfernen, F-002).</summary>
    public Task<Guid> CreateHouseholdAsync(User user, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var today = clock.TodayIn(user.TimeZone);
        var (household, categories) = HouseholdFactory.Create(Guid.NewGuid(), Texts.Get(user.Language, "household.defaultName", user.Name), "EUR", 1, today, now);
        household.ChangeSeq = 1;
        household.Version = 1;
        foreach (var c in categories) c.Version = 1;
        db.Households.Add(household);
        db.Categories.AddRange(categories);
        return Task.FromResult(household.Id);
    }

    public AuthResponse CreateSession(User user, string? deviceName, bool isNew, string? returnUrl)
    {
        var now = clock.UtcNow;
        var refresh = TokenHelper.RandomToken(48);
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RefreshTokenHash = TokenHelper.Sha256(refresh),
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Browser" : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 100)],
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now.AddDays(Opt.RefreshTokenDays),
        };
        db.Sessions.Add(session);
        var (access, expires) = tokens.CreateAccessToken(user, session.Id);
        return new AuthResponse(access, expires, refresh, session.ExpiresAt, ToDto(user, Opt.IsAdmin(user.Email)), isNew, returnUrl);
    }

    /// <summary>Refresh-Token rotieren; Wiederverwendung eines alten Tokens nach Ablauf der Schonfrist beendet die Sitzung.</summary>
    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var hash = TokenHelper.Sha256(refreshToken ?? "");
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == hash, ct);
        if (session is null)
        {
            var reused = await db.Sessions.FirstOrDefaultAsync(s => s.PreviousTokenHash == hash, ct);
            if (reused is not null && reused.RevokedAt is null)
            {
                // Schonfrist für parallele Tabs: kurz nach der Rotation darf das alte Token noch einmal rotieren.
                if ((now - reused.LastUsedAt).TotalSeconds <= 60) session = reused;
                else
                {
                    reused.RevokedAt = now;
                    await db.SaveChangesAsync(ct);
                }
            }
            if (session is null) throw AppException.Unauthorized("err.auth.sessionRevoked");
        }
        if (session.RevokedAt is not null || session.ExpiresAt < now) throw AppException.Unauthorized("err.auth.sessionRevoked");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == session.UserId, ct) ?? throw AppException.Unauthorized("err.auth.sessionRevoked");
        if (user.BlockedAt is not null) throw AppException.Unauthorized("err.account.blocked");

        var refresh = TokenHelper.RandomToken(48);
        session.PreviousTokenHash = session.RefreshTokenHash == hash ? hash : session.RefreshTokenHash;
        session.RefreshTokenHash = TokenHelper.Sha256(refresh);
        session.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
        var (access, expires) = tokens.CreateAccessToken(user, session.Id);
        return new AuthResponse(access, expires, refresh, session.ExpiresAt, ToDto(user, Opt.IsAdmin(user.Email)), false, null);
    }

    public async Task LogoutAsync(Guid? sessionId, CancellationToken ct = default)
    {
        if (sessionId is not { } id) return;
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return;
        session.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public static UserDto ToDto(User u, bool isAdmin = false) => new(u.Id, u.Email, u.Name, u.Language, u.TimeZone, u.Theme, u.HouseholdId, u.Role, isAdmin);
}

/// <summary>Standard-Benachrichtigungseinstellungen neuer Nutzer.</summary>
public static class NotificationDefaults
{
    private static readonly HashSet<NotificationType> PushDefaults =
    [
        NotificationType.MonthStart, NotificationType.BudgetEarlyWarning, NotificationType.NegativeBalanceForecast,
        NotificationType.BufferGoalReached, NotificationType.DebtPaidOff, NotificationType.DebtRateDue, NotificationType.HouseholdMembership,
    ];

    public static bool DefaultActive(NotificationType type, NotificationChannel channel) => channel switch
    {
        NotificationChannel.InApp => true,
        NotificationChannel.Push => PushDefaults.Contains(type),
        _ => false,
    };

    public static IEnumerable<NotificationSetting> For(Guid userId) =>
        from type in Enum.GetValues<NotificationType>()
        from channel in Enum.GetValues<NotificationChannel>()
        select new NotificationSetting { Id = Guid.NewGuid(), UserId = userId, Type = type, Channel = channel, Active = DefaultActive(type, channel) };
}
