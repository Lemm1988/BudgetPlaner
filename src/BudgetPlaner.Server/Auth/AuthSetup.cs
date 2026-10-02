using System.Security.Claims;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BudgetPlaner.Server.Auth;

public static class AuthSetup
{
    /// <summary>JWT-Bearer: Sitzungen/API-Tokens werden bei jeder Anfrage gegen die DB geprüft → Widerruf wirkt sofort.</summary>
    public static IServiceCollection AddBudgetAuth(this IServiceCollection services)
    {
        services.AddSingleton<TokenService>();
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<TokenService>());
        services.AddScoped<RequestUser>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<KeyStore>((o, keys) =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = TokenService.Issuer,
                ValidAudience = TokenService.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(keys.JwtSigningKey),
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Name,
            };
            o.Events = new JwtBearerEvents
            {
                // SignalR überträgt das Token im Query-String.
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                    return Task.CompletedTask;
                },
                OnTokenValidated = async ctx =>
                {
                    var db = ctx.HttpContext.RequestServices.GetRequiredService<IAppDbContext>();
                    var principal = ctx.Principal!;
                    var type = principal.FindFirstValue(TokenService.TypeClaim);
                    var now = DateTimeOffset.UtcNow;
                    if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)) { ctx.Fail("sub"); return; }
                    if (type == "pat")
                    {
                        var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
                        var pat = await db.ApiTokens.FirstOrDefaultAsync(t => t.Jti == jti && t.UserId == userId);
                        if (pat is null || pat.RevokedAt is not null || pat.ExpiresAt < now) { ctx.Fail("revoked"); return; }
                        if (pat.LastUsedAt is null || (now - pat.LastUsedAt.Value).TotalMinutes > 5)
                        {
                            pat.LastUsedAt = now;
                            await db.SaveChangesAsync();
                        }
                    }
                    else
                    {
                        if (!Guid.TryParse(principal.FindFirstValue(TokenService.SessionClaim), out var sid)) { ctx.Fail("sid"); return; }
                        var ok = await db.Sessions.AnyAsync(s => s.Id == sid && s.UserId == userId && s.RevokedAt == null);
                        if (!ok) { ctx.Fail("revoked"); return; }
                    }
                },
            };
        });
        services.AddAuthorization();
        return services;
    }

    public static Guid? SessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TokenService.SessionClaim), out var sid) ? sid : null;

    public static Guid? UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : null;
}

/// <summary>Angemeldeter Nutzer der Anfrage (wird vom <see cref="UserContextFilter"/> geladen).</summary>
public sealed class RequestUser
{
    private UserContext? _context;
    public UserContext Context => _context ?? throw AppException.Unauthorized();
    public UserContext? Optional => _context;
    public void Set(UserContext context) => _context = context;
}

/// <summary>Lädt Nutzer, Haushalt und Rolle frisch aus der Datenbank (F-003: Entzug wirkt sofort).</summary>
public sealed class UserContextFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        await ResolveAsync(http, required: true);
        return await next(context);
    }

    public static async Task<UserContext?> ResolveAsync(HttpContext http, bool required)
    {
        var holder = http.RequestServices.GetRequiredService<RequestUser>();
        if (holder.Optional is { } existing) return existing;
        if (http.User.Identity?.IsAuthenticated != true || http.User.UserId() is not { } userId)
        {
            if (required) throw AppException.Unauthorized();
            return null;
        }
        var db = http.RequestServices.GetRequiredService<IAppDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            if (required) throw AppException.Unauthorized("err.auth.sessionRevoked");
            return null;
        }
        var ctx = new UserContext(user, http.User.SessionId());
        holder.Set(ctx);
        return ctx;
    }
}
