using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Domain;

namespace BudgetPlaner.Application.Common;

/// <summary>Fachlicher Fehler mit HTTP-Status und übersetzbarem Schlüssel (siehe core.*.json „err.*“).</summary>
public sealed class AppException(int statusCode, string errorKey, params object?[] args) : Exception(errorKey)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorKey { get; } = errorKey;
    public object?[] Args { get; } = args;
    public int? RetryAfterSeconds { get; init; }

    public static AppException BadRequest(string key, params object?[] args) => new(400, key, args);
    public static AppException Unauthorized(string key = "err.auth.required") => new(401, key);
    public static AppException Forbidden(string key = "err.forbidden.owner") => new(403, key);
    public static AppException NotFound(string key = "err.notFound") => new(404, key);
    public static AppException Conflict(string key, params object?[] args) => new(409, key, args);
    public static AppException PreconditionFailed() => new(412, "err.sync.conflict");
    public static AppException TooManyRequests(int seconds) => new(429, "err.login.rateLimited", seconds) { RetryAfterSeconds = seconds };
}

/// <summary>Konfiguration (appsettings „App“).</summary>
public sealed class AppOptions
{
    /// <summary>
    /// E-Mail-Adressen der Administratoren (durch Komma oder Semikolon getrennt). Administratoren sehen den
    /// Verwaltungsbereich (/admin): Konten, Sperren, Registrierungsmodus, Serverstatus.
    /// </summary>
    public string AdminEmails { get; set; } = "";

    public string[] AdminEmailList() =>
        AdminEmails.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsAdmin(string? email) =>
        !string.IsNullOrWhiteSpace(email) && AdminEmailList().Any(a => string.Equals(a, email.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registrierungsmodus, solange in der Verwaltung keiner gewählt wurde: „open“, „approval“, „allowlist“ oder
    /// „closed“. Auf dem Raspberry Pi voreingestellt: „approval“ (öffentliche Adresse, keine Mails an Unbekannte).
    /// </summary>
    public string DefaultRegistrationMode { get; set; } = "open";

    /// <summary>Öffentliche Basis-URL für Links in E-Mails (Magic Link, Einladung).</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7264";

    /// <summary>
    /// Mails zusätzlich im Entwicklungs-Postfach /dev/postfach ablegen. Wird außerhalb der Umgebungen
    /// „Development“/„Testing“ zwangsweise abgeschaltet – sonst wären Anmeldecodes öffentlich abrufbar.
    /// </summary>
    public bool DevMailbox { get; set; }

    /// <summary>
    /// Erlaubte Push-Dienste (exakter Host oder Domain-Suffix mit führendem Punkt). Schutz vor Anfragen an
    /// interne Adressen (SSRF); per Konfiguration erweiterbar (App:PushEndpointHosts).
    /// </summary>
    public List<string> PushEndpointHosts { get; set; } =
        ["fcm.googleapis.com", "updates.push.services.mozilla.com", ".notify.windows.com", "web.push.apple.com", ".push.apple.com"];

    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 90;
    public int LoginTokenMinutes { get; set; } = 15;
    public int LoginRateLimitSeconds { get; set; } = 60;
    public int InvitationDays { get; set; } = 7;
    public string DefaultTimeZone { get; set; } = "Europe/Berlin";
    public string DataDirectory { get; set; } = "App_Data";
    public int BackupRetentionDays { get; set; } = 14;
    public int ChangeLogRetentionDays { get; set; } = 180;
    public int TombstoneRetentionDays { get; set; } = 90;
    public int MonthStartReminderHour { get; set; } = 8;
}

/// <summary>Angemeldeter Nutzer der aktuellen Anfrage (Haushalt/Rolle frisch aus der DB → Entzug wirkt sofort, F-003).</summary>
public sealed class UserContext(User user, Guid? sessionId)
{
    public User User { get; } = user;
    public Guid UserId => User.Id;
    public Guid HouseholdId => User.HouseholdId;
    public Role Role => User.Role;
    public bool IsOwner => User.Role == Role.Owner;
    public Guid? SessionId { get; } = sessionId;

    /// <summary>Serveradministrator (App:AdminEmails) – unabhängig von der Haushaltsrolle.</summary>
    public bool IsAdmin { get; init; }

    public void RequireAdmin()
    {
        if (!IsAdmin) throw AppException.Forbidden("err.forbidden.admin");
    }

    public string Language => User.Language;

    public void RequireOwner()
    {
        if (!IsOwner) throw AppException.Forbidden();
    }
}

/// <summary>Serialisiert Schreibzugriffe je Haushalt (konsistente Änderungssequenz).</summary>
public sealed class HouseholdLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid householdId, CancellationToken ct = default)
    {
        var sem = _locks.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) sem.Release();
        }
    }
}

public static class TokenHelper
{
    public static string RandomToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>6-stelliger Zahlencode (kryptografisch zufällig).</summary>
    public static string Code6() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Contains(' ')) return false;
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email && email.Contains('.', StringComparison.Ordinal) && email.IndexOf('@') > 0;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Nur lokale Rücksprungadressen zulassen (Open-Redirect-Schutz).</summary>
    public static string? SafeReturnUrl(string? url) =>
        url is not null && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\') && url.Length < 500 ? url : null;

    public static string NameFromEmail(string email)
    {
        var local = email.Split('@')[0].Split('.', '_', '-', '+')[0];
        return local.Length == 0 ? "Ich" : char.ToUpperInvariant(local[0]) + local[1..];
    }
}
