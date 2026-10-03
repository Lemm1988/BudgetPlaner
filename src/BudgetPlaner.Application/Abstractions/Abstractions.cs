using BudgetPlaner.Application.Entities;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BudgetPlaner.Application.Abstractions;

/// <summary>Persistenz (Implementierung: EF Core + SQLite in Infrastructure).</summary>
public interface IAppDbContext
{
    DbSet<Household> Households { get; }
    DbSet<Category> Categories { get; }
    DbSet<BudgetEntry> Entries { get; }
    DbSet<BudgetEntryChange> EntryChanges { get; }
    DbSet<BudgetEntryMonth> EntryMonths { get; }
    DbSet<DebtEntry> Debts { get; }
    DbSet<DebtPayment> DebtPayments { get; }
    DbSet<BufferEntry> Buffers { get; }
    DbSet<BufferTransaction> BufferTransactions { get; }
    DbSet<MonthSnapshot> Snapshots { get; }
    DbSet<BalanceCheckpoint> Checkpoints { get; }

    DbSet<User> Users { get; }
    DbSet<LoginToken> LoginTokens { get; }
    DbSet<Session> Sessions { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<PasskeyCredential> Passkeys { get; }
    DbSet<PushSubscriptionEntity> PushSubscriptions { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<NotificationSetting> NotificationSettings { get; }
    DbSet<NotificationLog> NotificationLogs { get; }
    DbSet<ChangeLogEntry> ChangeLogs { get; }
    DbSet<ApiToken> ApiTokens { get; }
    DbSet<Feedback> Feedback { get; }
    DbSet<DevMail> DevMails { get; }
    DbSet<ServerSetting> ServerSettings { get; }
    DbSet<RegistrationRequest> RegistrationRequests { get; }

    DatabaseFacade Database { get; }
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }
    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Entry(object entity);
    void AddEntity(object entity);
    void UpdateEntity(object entity);
    void RemoveEntity(object entity);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Öffentliche Basis-URL für Links (konfiguriert oder aus der aktuellen Anfrage).</summary>
public interface IPublicUrlProvider
{
    string BaseUrl { get; }
}

/// <summary>Uhr (testbar).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Heutiges Datum in einer Zeitzone (IANA, z. B. „Europe/Berlin“).</summary>
    DateOnly TodayIn(string? timeZone);

    DateTimeOffset NowIn(string? timeZone);
}

public sealed record EmailMessage(string To, string Subject, string Html, string Text);

public interface IEmailSender
{
    bool IsSmtpConfigured { get; }
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Warteschlange für zeitnahen Versand ohne die Anfrage zu blockieren (F-001: Versand in &lt; 60 s).</summary>
public interface IEmailQueue
{
    void Enqueue(EmailMessage message);
}

public sealed record PushPayload(string Title, string Body, string? Url, string? Tag);

public interface IPushSender
{
    bool IsConfigured { get; }
    string? PublicKey { get; }

    /// <summary>Liefert false, wenn das Abo ungültig ist (wird dann gelöscht).</summary>
    Task<bool> SendAsync(PushSubscriptionEntity subscription, PushPayload payload, CancellationToken ct = default);
}

/// <summary>Echtzeit-Benachrichtigung der Clients (SignalR, F-131).</summary>
public interface IRealtimeNotifier
{
    Task HouseholdChangedAsync(Guid householdId, long seq);
    Task NotificationAsync(Guid userId, NotificationDto notification);
    Task AccessRevokedAsync(Guid userId);
    Task ProfileChangedAsync(Guid userId);
}

/// <summary>JWT-Ausstellung (F-130).</summary>
public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid sessionId);
    string CreateApiToken(User user, ApiToken token);
}

/// <summary>Symmetrische Verschlüsselung für Daten im Ruhezustand.</summary>
public interface IDataProtector
{
    string Protect(string plain);
    string Unprotect(string protectedText);
}

public interface IPasskeyService
{
    Task<string> RegistrationOptionsAsync(User user, CancellationToken ct);
    Task<PasskeyCredential> RegisterAsync(User user, string attestationJson, string name, CancellationToken ct);
    Task<(string OptionsJson, string FlowId)> LoginOptionsAsync(CancellationToken ct);
    Task<User> LoginAsync(string flowId, string assertionJson, CancellationToken ct);
}

/// <summary>Tabellen lesen/schreiben (CSV, Excel) für Import/Export (F-110, F-133).</summary>
public interface ISpreadsheetIO
{
    List<List<string>> Read(Stream stream, string fileName);
    byte[] WriteXlsx(ExportWorkbook workbook);
    byte[] WriteCsvZip(ExportWorkbook workbook, string language);
}

public sealed class ExportWorkbook
{
    public List<ExportSheet> Sheets { get; } = [];
}

public sealed class ExportSheet(string name, IReadOnlyList<string> headers)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Headers { get; } = headers;
    public List<object?[]> Rows { get; } = [];

    /// <summary>Spalten mit Geldbeträgen (für Zahlenformat).</summary>
    public HashSet<int> MoneyColumns { get; } = [];
}
