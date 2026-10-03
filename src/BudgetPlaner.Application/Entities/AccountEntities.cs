using BudgetPlaner.Domain;

namespace BudgetPlaner.Application.Entities;

/// <summary>Nutzer (Anhang A: Id, HouseholdId, Email, Name, Rolle, ErinnerungAktiv).</summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Normalisiert (klein, getrimmt).</summary>
    public string Email { get; set; } = "";

    public string Name { get; set; } = "";
    public Guid HouseholdId { get; set; }
    public Role Role { get; set; }
    public string Language { get; set; } = "de";
    public string TimeZone { get; set; } = "Europe/Berlin";
    public string Theme { get; set; } = "system";

    /// <summary>Monatsstart-Erinnerung aktiv (Spiegel der Benachrichtigungseinstellung, F-080).</summary>
    public bool ReminderActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Vom Administrator gesperrt: keine Anmeldung, bestehende Sitzungen ungültig.</summary>
    public DateTimeOffset? BlockedAt { get; set; }
    public string? BlockedReason { get; set; }

    // E-Mail-Änderung mit Bestätigungscode
    public string? PendingEmail { get; set; }
    public string? PendingEmailCodeHash { get; set; }
    public DateTimeOffset? PendingEmailExpiresAt { get; set; }
}

/// <summary>Serverweite Einstellung, die der Administrator zur Laufzeit ändert (z. B. Registrierungsmodus).</summary>
public sealed class ServerSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Einmaliger Magic-Link + Code (F-001): 15 Minuten gültig, nur einmal einlösbar.</summary>
public sealed class LoginToken
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public int FailedAttempts { get; set; }
    public string? ReturnUrl { get; set; }
    public string Language { get; set; } = "de";
}

/// <summary>Angemeldetes Gerät mit rotierendem Refresh-Token.</summary>
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string RefreshTokenHash { get; set; } = "";
    public string? PreviousTokenHash { get; set; }
    public string DeviceName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Einladung in einen Haushalt per Link/E-Mail (F-002).</summary>
public sealed class Invitation
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Guid CreatedBy { get; set; }
    public string? Email { get; set; }
    public string TokenHash { get; set; } = "";

    /// <summary>Verschlüsseltes Token, damit der Owner den Link erneut kopieren kann.</summary>
    public string TokenProtected { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>WebAuthn-Credential für Passkey-Login (B-15).</summary>
public sealed class PasskeyCredential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public byte[] UserHandle { get; set; } = [];
    public uint SignCount { get; set; }
    public string Name { get; set; } = "";
    public string? Transports { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Web-Push-Abonnement eines Geräts.</summary>
public sealed class PushSubscriptionEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int FailureCount { get; set; }
}

/// <summary>In-App-Benachrichtigung.</summary>
public sealed class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid HouseholdId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Benachrichtigungseinstellung (Anhang A: Id, UserId, Typ, Aktiv, Kanal).</summary>
public sealed class NotificationSetting
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public NotificationChannel Channel { get; set; }
    public bool Active { get; set; }
}

/// <summary>Bereits versendete Benachrichtigung (Dedupe: „genau eine Benachrichtigung am Stichtag“, F-080).</summary>
public sealed class NotificationLog
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Key { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Änderungsverlauf: alte und neue Werte jeder Änderung (I-13, „kein Datenverlust“).</summary>
public sealed class ChangeLogEntry
{
    public Guid Id { get; set; }
    public Guid HouseholdId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }

    /// <summary>„create“, „update“, „delete“, „restore“.</summary>
    public string Action { get; set; } = "";

    public Guid? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public bool IsConflict { get; set; }
    public string? Label { get; set; }
}

/// <summary>Persönlicher API-Zugang (JWT mit Kennung <see cref="Jti"/>, widerrufbar).</summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Jti { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Feedback
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Message { get; set; } = "";
    public int? Rating { get; set; }
    public string? Page { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Entwicklungs-Postfach: versendete Mails zum Testen ohne SMTP.</summary>
public sealed class DevMail
{
    public Guid Id { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Html { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
