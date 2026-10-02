using System.Text;
using System.Text.Json;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Infrastructure.Services;

/// <summary>Passkeys/WebAuthn (B-15: Face ID, Fingerabdruck, Gerätecode) mit Fido2NetLib.</summary>
public sealed class PasskeyService(IAppDbContext db, IMemoryCache cache, IClock clock, IPublicUrlProvider urls) : IPasskeyService
{
    private IFido2 CreateFido()
    {
        var origin = new Uri(urls.BaseUrl);
        var origins = new HashSet<string> { origin.GetLeftPart(UriPartial.Authority) };
        // Lokale Entwicklung: beide üblichen Ports erlauben.
        if (origin.Host == "localhost") { origins.Add("https://localhost:7264"); origins.Add("http://localhost:5278"); }
        return new Fido2(new Fido2Configuration
        {
            RPID = origin.Host,
            RPName = "BudgetPlaner",
            Origins = origins,
            TimestampDriftTolerance = 300000,
        }, null!);
    }

    public async Task<string> RegistrationOptionsAsync(User user, CancellationToken ct)
    {
        var existing = await db.Passkeys.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.CredentialId).ToListAsync(ct);
        var options = CreateFido().RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Email, DisplayName = user.Name },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        cache.Set($"passkey-reg:{user.Id}", options.ToJson(), TimeSpan.FromMinutes(5));
        return options.ToJson();
    }

    public async Task<PasskeyCredential> RegisterAsync(User user, string attestationJson, string name, CancellationToken ct)
    {
        if (!cache.TryGetValue($"passkey-reg:{user.Id}", out string? optionsJson) || optionsJson is null)
            throw AppException.BadRequest("err.passkey.failed");
        var original = CredentialCreateOptions.FromJson(optionsJson);
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson) ?? throw AppException.BadRequest("err.passkey.failed");
        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await CreateFido().MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = original,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.Passkeys.AnyAsync(p => p.CredentialId == args.CredentialId, token),
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            throw AppException.BadRequest("err.passkey.failed");
        }
        var entity = new PasskeyCredential
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            UserHandle = user.Id.ToByteArray(),
            SignCount = credential.SignCount,
            Transports = credential.Transports is null ? null : string.Join(",", credential.Transports),
            Name = string.IsNullOrWhiteSpace(name) ? "Passkey" : name.Trim()[..Math.Min(name.Trim().Length, 80)],
            CreatedAt = clock.UtcNow,
        };
        db.Passkeys.Add(entity);
        await db.SaveChangesAsync(ct);
        cache.Remove($"passkey-reg:{user.Id}");
        return entity;
    }

    public Task<(string OptionsJson, string FlowId)> LoginOptionsAsync(CancellationToken ct)
    {
        var options = CreateFido().GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var flowId = TokenHelper.RandomToken(16);
        cache.Set($"passkey-login:{flowId}", options.ToJson(), TimeSpan.FromMinutes(5));
        return Task.FromResult((options.ToJson(), flowId));
    }

    public async Task<User> LoginAsync(string flowId, string assertionJson, CancellationToken ct)
    {
        if (!cache.TryGetValue($"passkey-login:{flowId}", out string? optionsJson) || optionsJson is null)
            throw AppException.Unauthorized("err.passkey.failed");
        cache.Remove($"passkey-login:{flowId}");
        var original = AssertionOptions.FromJson(optionsJson);
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson) ?? throw AppException.Unauthorized("err.passkey.failed");
        var credentialId = response.RawId;
        var stored = await db.Passkeys.FirstOrDefaultAsync(p => p.CredentialId == credentialId, ct) ?? throw AppException.Unauthorized("err.passkey.unknown");
        try
        {
            var result = await CreateFido().MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = original,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(stored.UserHandle)),
            }, ct);
            stored.SignCount = result.SignCount;
        }
        catch (Fido2VerificationException)
        {
            throw AppException.Unauthorized("err.passkey.failed");
        }
        stored.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct) ?? throw AppException.Unauthorized("err.passkey.unknown");
    }
}
