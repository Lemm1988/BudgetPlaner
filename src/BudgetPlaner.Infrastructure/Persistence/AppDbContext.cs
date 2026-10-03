using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BudgetPlaner.Infrastructure.Persistence;

/// <summary>
/// EF-Core-Kontext (SQLite). Finanzbeträge, Bezeichnungen und Notizen werden spaltenweise mit AES-GCM verschlüsselt
/// gespeichert (NFR Sicherheit, B-17); Berechnungen finden ausschließlich im Speicher statt.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IDataProtector protector) : DbContext(options), IAppDbContext
{
    public DbSet<Household> Households => Set<Household>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<BudgetEntry> Entries => Set<BudgetEntry>();
    public DbSet<BudgetEntryChange> EntryChanges => Set<BudgetEntryChange>();
    public DbSet<BudgetEntryMonth> EntryMonths => Set<BudgetEntryMonth>();
    public DbSet<DebtEntry> Debts => Set<DebtEntry>();
    public DbSet<DebtPayment> DebtPayments => Set<DebtPayment>();
    public DbSet<BufferEntry> Buffers => Set<BufferEntry>();
    public DbSet<BufferTransaction> BufferTransactions => Set<BufferTransaction>();
    public DbSet<MonthSnapshot> Snapshots => Set<MonthSnapshot>();
    public DbSet<BalanceCheckpoint> Checkpoints => Set<BalanceCheckpoint>();
    public DbSet<User> Users => Set<User>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<PasskeyCredential> Passkeys => Set<PasskeyCredential>();
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationSetting> NotificationSettings => Set<NotificationSetting>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<ChangeLogEntry> ChangeLogs => Set<ChangeLogEntry>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<DevMail> DevMails => Set<DevMail>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();

    public void AddEntity(object entity) => Add(entity);
    public void UpdateEntity(object entity) => Update(entity);
    public void RemoveEntity(object entity) => Remove(entity);
    EntityEntry IAppDbContext.Entry(object entity) => Entry(entity);

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<UtcTicksConverter>();
        b.Properties<Period>().HaveConversion<PeriodConverter>();
        b.Properties<Period?>().HaveConversion<PeriodConverter>();
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var encString = new ValueConverter<string, string>(v => protector.Protect(v), v => protector.Unprotect(v));
        var encNullable = new ValueConverter<string?, string?>(v => v == null ? null : protector.Protect(v), v => v == null ? null : protector.Unprotect(v));
        var encDecimal = new ValueConverter<decimal, string>(
            v => protector.Protect(v.ToString(CultureInfo.InvariantCulture)),
            v => decimal.Parse(protector.Unprotect(v), CultureInfo.InvariantCulture));

        mb.Entity<Household>(e =>
        {
            e.HasKey(x => x.Id);
            e.Ignore(x => x.CurrentMonthStartDay);
            e.Property(x => x.MonthStartRules).HasConversion(Json<List<MonthStartRule>>(), ListComparer<MonthStartRule>());
            e.Property(x => x.AmpelRules).HasConversion(NullableJson<AmpelRules>());
            e.Property(x => x.Name).HasConversion(encString);
        });

        mb.Entity<Category>(e => { Synced(e); });

        mb.Entity<BudgetEntry>(e =>
        {
            Synced(e);
            e.Property(x => x.Name).HasConversion(encString);
            e.Property(x => x.Note).HasConversion(encNullable);
            e.Property(x => x.Amount).HasConversion(encDecimal);
        });
        mb.Entity<BudgetEntryChange>(e =>
        {
            Synced(e);
            e.HasIndex(x => x.EntryId);
            e.Property(x => x.Amount).HasConversion(encDecimal);
        });
        mb.Entity<BudgetEntryMonth>(e =>
        {
            Synced(e);
            e.HasIndex(x => x.EntryId);
            e.Property(x => x.PlannedAmount).HasConversion(encDecimal);
            e.Property(x => x.ActualAmount).HasConversion(encDecimal);
            e.Property(x => x.Note).HasConversion(encNullable);
        });
        mb.Entity<DebtEntry>(e =>
        {
            Synced(e);
            e.Property(x => x.Name).HasConversion(encString);
            e.Property(x => x.Creditor).HasConversion(encNullable);
            e.Property(x => x.Note).HasConversion(encNullable);
            e.Property(x => x.OriginalAmount).HasConversion(encDecimal);
            e.Property(x => x.OpeningBalance).HasConversion(encDecimal);
            e.Property(x => x.MonthlyRate).HasConversion(encDecimal);
            e.Property(x => x.ResidualValue).HasConversion(encDecimal);
        });
        mb.Entity<DebtPayment>(e =>
        {
            Synced(e);
            e.HasIndex(x => x.DebtId);
            e.Property(x => x.Amount).HasConversion(encDecimal);
            e.Property(x => x.InterestPart).HasConversion(encDecimal);
            e.Property(x => x.PrincipalPart).HasConversion(encDecimal);
            e.Property(x => x.Note).HasConversion(encNullable);
        });
        mb.Entity<BufferEntry>(e =>
        {
            Synced(e);
            e.Property(x => x.Name).HasConversion(encString);
            e.Property(x => x.Note).HasConversion(encNullable);
            e.Property(x => x.TargetAmount).HasConversion(encDecimal);
        });
        mb.Entity<BufferTransaction>(e =>
        {
            Synced(e);
            e.HasIndex(x => x.BufferId);
            e.Property(x => x.Amount).HasConversion(encDecimal);
            e.Property(x => x.Reason).HasConversion(encNullable);
        });
        mb.Entity<MonthSnapshot>(e =>
        {
            Synced(e);
            foreach (var p in new Expression<Func<MonthSnapshot, decimal>>[]
                     { x => x.TotalIncome, x => x.TotalExpenses, x => x.TotalAllocations, x => x.ManualAllocations, x => x.TotalWithdrawals, x => x.CarryIn, x => x.Unverplant, x => x.CarryOver })
                e.Property(p).HasConversion(encDecimal);
            e.Property(x => x.ClosingBalance).HasConversion(encDecimal);
            e.Property(x => x.Lines).HasConversion(EncryptedJson<List<SnapshotLine>>(encString), ListComparer<SnapshotLine>());
        });
        mb.Entity<BalanceCheckpoint>(e =>
        {
            Synced(e);
            e.Property(x => x.Amount).HasConversion(encDecimal);
            e.Property(x => x.Note).HasConversion(encNullable);
        });

        mb.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.HouseholdId);
        });
        mb.Entity<LoginToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => new { x.Email, x.CreatedAt });
        });
        mb.Entity<Session>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RefreshTokenHash);
            e.HasIndex(x => x.PreviousTokenHash);
            e.HasIndex(x => x.UserId);
        });
        mb.Entity<Invitation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.HouseholdId);
        });
        mb.Entity<PasskeyCredential>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.CredentialId);
        });
        mb.Entity<PushSubscriptionEntity>(e =>
        {
            e.ToTable("PushSubscriptions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.UserId);
        });
        mb.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Body).HasConversion(encString);
            e.Property(x => x.Title).HasConversion(encString);
        });
        mb.Entity<NotificationSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Type, x.Channel }).IsUnique();
        });
        mb.Entity<NotificationLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Type, x.Key }).IsUnique();
        });
        mb.Entity<ChangeLogEntry>(e =>
        {
            e.ToTable("ChangeLogs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.HouseholdId, x.CreatedAt });
            e.HasIndex(x => new { x.HouseholdId, x.EntityId });
            e.Property(x => x.BeforeJson).HasConversion(encNullable);
            e.Property(x => x.AfterJson).HasConversion(encNullable);
            e.Property(x => x.Label).HasConversion(encNullable);
        });
        mb.Entity<ApiToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Jti).IsUnique();
            e.HasIndex(x => x.UserId);
        });
        mb.Entity<Feedback>(e => e.HasKey(x => x.Id));
        mb.Entity<DevMail>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CreatedAt);
        });
        mb.Entity<ServerSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }

    private static void Synced<T>(EntityTypeBuilder<T> e) where T : HouseholdEntity
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.HouseholdId, x.Version });
        e.HasIndex(x => new { x.HouseholdId, x.IsDeleted });
    }

    private static ValueConverter<T, string> Json<T>() where T : class =>
        new(v => JsonSerializer.Serialize(v, JsonDefaults.Options), v => JsonSerializer.Deserialize<T>(v, JsonDefaults.Options)!);

    private static ValueConverter<T?, string?> NullableJson<T>() where T : class =>
        new(v => v == null ? null : JsonSerializer.Serialize(v, JsonDefaults.Options), v => v == null ? null : JsonSerializer.Deserialize<T>(v, JsonDefaults.Options));

    private static ValueConverter<T, string> EncryptedJson<T>(ValueConverter<string, string> enc) where T : class
    {
        var to = (Func<string, string>)enc.ConvertToProviderExpression.Compile();
        var from = (Func<string, string>)enc.ConvertFromProviderExpression.Compile();
        return new ValueConverter<T, string>(
            v => to(JsonSerializer.Serialize(v, JsonDefaults.Options)),
            v => JsonSerializer.Deserialize<T>(from(v), JsonDefaults.Options)!);
    }

    private static ValueComparer<List<T>> ListComparer<T>() => new(
        (a, b) => JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options),
        v => JsonSerializer.Serialize(v, JsonDefaults.Options).GetHashCode(),
        v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, JsonDefaults.Options), JsonDefaults.Options)!);
}

/// <summary>DateTimeOffset als UTC-Ticks (SQLite kann DateTimeOffset nicht sortieren/vergleichen).</summary>
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

/// <summary>Periode als JJJJMM-Ganzzahl.</summary>
public sealed class PeriodConverter() : ValueConverter<Period, int>(v => v.Year * 100 + v.Month, v => new Period(v / 100, v % 100));
