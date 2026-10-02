using System.Security.Claims;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BudgetPlaner.Server.Auth;

/// <summary>Stellt JWTs aus (F-130): kurzlebige Zugriffstoken je Sitzung und widerrufbare API-Tokens.</summary>
public sealed class TokenService(KeyStore keys, IOptions<AppOptions> options, IClock clock) : ITokenService
{
    public const string Issuer = "BudgetPlaner";
    public const string Audience = "BudgetPlaner";
    public const string TypeClaim = "typ";
    public const string SessionClaim = "sid";

    public SymmetricSecurityKey SigningKey { get; } = new(keys.JwtSigningKey);

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid sessionId)
    {
        var expires = clock.UtcNow.AddMinutes(options.Value.AccessTokenMinutes);
        var token = Create(expires,
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(SessionClaim, sessionId.ToString()),
            new Claim(TypeClaim, "access"),
            new Claim(JwtRegisteredClaimNames.Name, user.Name),
            new Claim(JwtRegisteredClaimNames.Email, user.Email));
        return (token, expires);
    }

    public string CreateApiToken(User user, ApiToken apiToken) => Create(apiToken.ExpiresAt,
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Jti, apiToken.Jti),
        new Claim(TypeClaim, "pat"));

    private string Create(DateTimeOffset expires, params Claim[] claims) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = clock.UtcNow.UtcDateTime,
            NotBefore = clock.UtcNow.UtcDateTime.AddSeconds(-5),
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
}
