using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetPlaner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Jti = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Buffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TargetAmount = table.Column<string>(type: "TEXT", nullable: true),
                    TargetDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SeparateAccount = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEmergencyFund = table.Column<bool>(type: "INTEGER", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    SortIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buffers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BufferTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BufferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    AffectsBudget = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourcePeriod = table.Column<int>(type: "INTEGER", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BufferTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultKey = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Group = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    Icon = table.Column<string>(type: "TEXT", nullable: true),
                    SortIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChangeLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsConflict = table.Column<bool>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DebtPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DebtId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    InterestPart = table.Column<string>(type: "TEXT", nullable: false),
                    PrincipalPart = table.Column<string>(type: "TEXT", nullable: false),
                    IsExtra = table.Column<bool>(type: "INTEGER", nullable: false),
                    InterestRateApplied = table.Column<decimal>(type: "TEXT", nullable: true),
                    SourceEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourcePeriod = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DebtPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Debts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Creditor = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalAmount = table.Column<string>(type: "TEXT", nullable: false),
                    OpeningBalance = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    InterestRate = table.Column<decimal>(type: "TEXT", nullable: true),
                    MonthlyRate = table.Column<string>(type: "TEXT", nullable: true),
                    DebitDay = table.Column<int>(type: "INTEGER", nullable: true),
                    TermMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    FixedInterestUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ResidualValue = table.Column<string>(type: "TEXT", nullable: true),
                    AutoBook = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Debts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DevMails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Html = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevMails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    Rhythm = table.Column<int>(type: "INTEGER", nullable: false),
                    StartPeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    EndPeriod = table.Column<int>(type: "INTEGER", nullable: true),
                    DebitDay = table.Column<int>(type: "INTEGER", nullable: true),
                    IsFixed = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    DebtId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DebtRole = table.Column<int>(type: "INTEGER", nullable: false),
                    BufferId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntryChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromPeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<string>(type: "TEXT", nullable: false),
                    IsPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntryMonths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Period = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedAmount = table.Column<string>(type: "TEXT", nullable: true),
                    IsSkipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActualAmount = table.Column<string>(type: "TEXT", nullable: true),
                    IsDone = table.Column<bool>(type: "INTEGER", nullable: false),
                    DoneAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryMonths", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Page = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Households",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    MonthStartRules = table.Column<string>(type: "TEXT", nullable: false),
                    FirstPeriod = table.Column<int>(type: "INTEGER", nullable: false),
                    AmpelRules = table.Column<string>(type: "TEXT", nullable: true),
                    OnboardingCompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChangeSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    PurgedSeq = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Households", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    TokenProtected = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AcceptedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ReturnUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Link = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Passkeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UserHandle = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Transports = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passkeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", nullable: false),
                    Auth = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousTokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Period = table.Column<int>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ClosedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    TotalIncome = table.Column<string>(type: "TEXT", nullable: false),
                    TotalExpenses = table.Column<string>(type: "TEXT", nullable: false),
                    TotalAllocations = table.Column<string>(type: "TEXT", nullable: false),
                    ManualAllocations = table.Column<string>(type: "TEXT", nullable: false),
                    TotalWithdrawals = table.Column<string>(type: "TEXT", nullable: false),
                    CarryIn = table.Column<string>(type: "TEXT", nullable: false),
                    Unverplant = table.Column<string>(type: "TEXT", nullable: false),
                    CarryOver = table.Column<string>(type: "TEXT", nullable: false),
                    CarryOverBufferId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClosingBalance = table.Column<string>(type: "TEXT", nullable: true),
                    Lines = table.Column<string>(type: "TEXT", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    ReminderActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    JoinedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastLoginAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PendingEmail = table.Column<string>(type: "TEXT", nullable: true),
                    PendingEmailCodeHash = table.Column<string>(type: "TEXT", nullable: true),
                    PendingEmailExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_Jti",
                table: "ApiTokens",
                column: "Jti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_UserId",
                table: "ApiTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Buffers_HouseholdId_IsDeleted",
                table: "Buffers",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Buffers_HouseholdId_Version",
                table: "Buffers",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_BufferTransactions_BufferId",
                table: "BufferTransactions",
                column: "BufferId");

            migrationBuilder.CreateIndex(
                name: "IX_BufferTransactions_HouseholdId_IsDeleted",
                table: "BufferTransactions",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_BufferTransactions_HouseholdId_Version",
                table: "BufferTransactions",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_HouseholdId_IsDeleted",
                table: "Categories",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_HouseholdId_Version",
                table: "Categories",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLogs_HouseholdId_CreatedAt",
                table: "ChangeLogs",
                columns: new[] { "HouseholdId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLogs_HouseholdId_EntityId",
                table: "ChangeLogs",
                columns: new[] { "HouseholdId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_HouseholdId_IsDeleted",
                table: "Checkpoints",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoints_HouseholdId_Version",
                table: "Checkpoints",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DebtPayments_DebtId",
                table: "DebtPayments",
                column: "DebtId");

            migrationBuilder.CreateIndex(
                name: "IX_DebtPayments_HouseholdId_IsDeleted",
                table: "DebtPayments",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_DebtPayments_HouseholdId_Version",
                table: "DebtPayments",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_Debts_HouseholdId_IsDeleted",
                table: "Debts",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Debts_HouseholdId_Version",
                table: "Debts",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DevMails_CreatedAt",
                table: "DevMails",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_HouseholdId_IsDeleted",
                table: "Entries",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_HouseholdId_Version",
                table: "Entries",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryChanges_EntryId",
                table: "EntryChanges",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_EntryChanges_HouseholdId_IsDeleted",
                table: "EntryChanges",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryChanges_HouseholdId_Version",
                table: "EntryChanges",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryMonths_EntryId",
                table: "EntryMonths",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_EntryMonths_HouseholdId_IsDeleted",
                table: "EntryMonths",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryMonths_HouseholdId_Version",
                table: "EntryMonths",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_HouseholdId",
                table: "Invitations",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginTokens_Email_CreatedAt",
                table: "LoginTokens",
                columns: new[] { "Email", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoginTokens_TokenHash",
                table: "LoginTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLogs_UserId_Type_Key",
                table: "NotificationLogs",
                columns: new[] { "UserId", "Type", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_UserId_Type_Channel",
                table: "NotificationSettings",
                columns: new[] { "UserId", "Type", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_CredentialId",
                table: "Passkeys",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_UserId",
                table: "Passkeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId",
                table: "PushSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PreviousTokenHash",
                table: "Sessions",
                column: "PreviousTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_RefreshTokenHash",
                table: "Sessions",
                column: "RefreshTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                table: "Sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_HouseholdId_IsDeleted",
                table: "Snapshots",
                columns: new[] { "HouseholdId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_Snapshots_HouseholdId_Version",
                table: "Snapshots",
                columns: new[] { "HouseholdId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_HouseholdId",
                table: "Users",
                column: "HouseholdId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropTable(
                name: "Buffers");

            migrationBuilder.DropTable(
                name: "BufferTransactions");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "ChangeLogs");

            migrationBuilder.DropTable(
                name: "Checkpoints");

            migrationBuilder.DropTable(
                name: "DebtPayments");

            migrationBuilder.DropTable(
                name: "Debts");

            migrationBuilder.DropTable(
                name: "DevMails");

            migrationBuilder.DropTable(
                name: "Entries");

            migrationBuilder.DropTable(
                name: "EntryChanges");

            migrationBuilder.DropTable(
                name: "EntryMonths");

            migrationBuilder.DropTable(
                name: "Feedback");

            migrationBuilder.DropTable(
                name: "Households");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "LoginTokens");

            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "NotificationSettings");

            migrationBuilder.DropTable(
                name: "Passkeys");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Snapshots");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
