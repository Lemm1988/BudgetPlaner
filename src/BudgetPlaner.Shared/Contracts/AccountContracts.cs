using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;

namespace BudgetPlaner.Shared.Contracts;

// ---------- Profil (F-100) ----------

public sealed record ProfileUpdate(string? Name, string? Language, string? TimeZone, string? Theme);

public sealed record EmailChangeRequest(string NewEmail);

public sealed record EmailChangeConfirm(string Code);

/// <summary>Benachrichtigungseinstellung je Art und Kanal (F-080, F-081).</summary>
public sealed record NotificationSettingDto(NotificationType Type, NotificationChannel Channel, bool Active);

public sealed record SessionDto(Guid Id, string DeviceName, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, bool IsCurrent);

/// <summary>Persönlicher API-Zugang für externe Nutzung der REST-API (F-130).</summary>
public sealed record ApiTokenDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt);

public sealed record CreateApiTokenRequest(string Name, int ExpiresInDays);

public sealed record CreateApiTokenResponse(string Token, ApiTokenDto Info);

// ---------- Haushalt (F-002, F-003) ----------

public sealed record HouseholdInfoDto(Household Household, IReadOnlyList<Member> Members, Role MyRole);

public sealed record HouseholdSettingsUpdate(string? Name, string? Currency, int? MonthStartDay, AmpelRules? AmpelRules, bool ResetAmpelRules = false);

public sealed record InvitationDto(Guid Id, string? Email, string Link, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public sealed record CreateInvitationRequest(string? Email);

public sealed record InvitationInfoDto(
    string HouseholdName,
    string InviterName,
    DateTimeOffset ExpiresAt,
    bool Valid,
    bool AlreadyMember,
    bool CurrentHouseholdHasData,
    bool CurrentHouseholdHasOtherMembers);

public sealed record AcceptInvitationRequest(bool ConfirmLeave);

public sealed record TransferOwnershipRequest(Guid UserId);

// ---------- Monatsabschluss (I-01) ----------

/// <summary>Was mit dem Rest-Unverplant passiert: „carry“ (Übertrag), „buffer“ (Polster), „none“.</summary>
public sealed record CloseMonthRequest(string LeftoverAction, Guid? BufferId, decimal? ClosingBalance);

// ---------- Benachrichtigungen ----------

public sealed record NotificationDto(Guid Id, NotificationType Type, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth, string? DeviceName);

public sealed record VapidKeyResponse(string PublicKey);

// ---------- Änderungsverlauf (I-13) ----------

public sealed record ChangeLogDto(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Action,
    string? UserName,
    DateTimeOffset CreatedAt,
    string? BeforeJson,
    string? AfterJson,
    bool IsConflict,
    string? Label);

// ---------- Sonstiges ----------

public sealed record FeedbackRequest(string Message, int? Rating, string? Page);

public sealed record VersionInfo(string Version, string Environment, bool DevMailbox, bool SmtpConfigured, bool PushConfigured);

public sealed record DemoStatusDto(bool HasDemoData, int DemoRecords);
