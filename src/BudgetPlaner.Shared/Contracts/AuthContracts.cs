using BudgetPlaner.Domain;

namespace BudgetPlaner.Shared.Contracts;

/// <summary>Anmeldung/Registrierung per E-Mail anfordern (F-001).</summary>
public sealed record LoginRequest(string Email, string? Language = null, string? ReturnUrl = null);

/// <summary>Ergebnis: Mail versendet bzw. Wartezeit bei Rate-Limit.</summary>
public sealed record LoginRequestResult(bool Sent, int RetryAfterSeconds);

/// <summary>Einlösen per Magic-Link-Token oder per E-Mail + 6-stelligem Code (I-10).</summary>
public sealed record VerifyRequest(string? Email, string? Code, string? Token, string? DeviceName);

public sealed record RefreshRequest(string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User,
    bool IsNewUser,
    string? ReturnUrl);

public sealed record UserDto(
    Guid Id,
    string Email,
    string Name,
    string Language,
    string TimeZone,
    string Theme,
    Guid HouseholdId,
    Role Role);

/// <summary>Passkey-Anmeldung (B-15): Optionen anfordern, Assertion einreichen.</summary>
public sealed record PasskeyLoginRequest(string AssertionJson, string? DeviceName);

public sealed record PasskeyRegisterRequest(string AttestationJson, string Name);

public sealed record PasskeyDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>WebAuthn-Optionen als JSON (direkt an navigator.credentials übergeben).</summary>
public sealed record PasskeyOptionsResponse(string OptionsJson);
