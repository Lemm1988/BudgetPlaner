using System.Text.Json;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.EntityFrameworkCore;

namespace BudgetPlaner.Application.Services;

/// <summary>Monatsabschluss und Wiedereröffnen (I-01).</summary>
public sealed class MonthService(SyncService sync, HouseholdDataLoader loader, IClock clock)
{
    public async Task<ApplyResult> CloseAsync(UserContext ctx, Period period, CloseMonthRequest request, CancellationToken ct = default)
    {
        var data = await loader.LoadAsync(ctx.HouseholdId, includeDeleted: false, ct);
        var today = clock.TodayIn(await sync.OwnerTimeZoneAsync(ctx.HouseholdId, ct));
        var engine = new HouseholdEngine(data, today);
        if (engine.Index.IsClosed(period)) throw AppException.BadRequest("err.month.alreadyClosed");
        if (engine.Calendar.Start(period) > today) throw AppException.BadRequest("err.month.notStarted");
        var month = engine.Month(period);
        var end = engine.Calendar.End(period);
        var mutations = new List<EntityMutation>();
        var action = (request.LeftoverAction ?? "carry").ToLowerInvariant();
        Guid? bufferId = null;

        if (action == "buffer" && request.BufferId is { } bid && engine.Index.BufferById.ContainsKey(bid) && month.Unverplant != 0)
        {
            bufferId = bid;
            var lang = ctx.Language;
            if (month.Unverplant > 0)
                mutations.Add(EntityMutation.Upsert(new BufferTransaction
                {
                    Id = Guid.NewGuid(), BufferId = bid, Date = end, Amount = month.Unverplant, Type = BufferTransactionType.Allocation,
                    AffectsBudget = true, Reason = $"{Texts.Get(lang, "month.closeReason")} {Fmt.PeriodName(period, lang)}",
                }));
            else
            {
                var available = engine.Buffers.Status(bid).Balance;
                var amount = Math.Min(-month.Unverplant, available);
                if (amount > 0)
                    mutations.Add(EntityMutation.Upsert(new BufferTransaction
                    {
                        Id = Guid.NewGuid(), BufferId = bid, Date = end, Amount = amount, Type = BufferTransactionType.Withdrawal,
                        AffectsBudget = true, Reason = $"{Texts.Get(lang, "month.compensateReason")} {Fmt.PeriodName(period, lang)}",
                    }));
            }
        }

        if (request.ClosingBalance is { } balance)
            mutations.Add(EntityMutation.Upsert(new BalanceCheckpoint { Id = Guid.NewGuid(), Date = end < today ? end : today, Amount = balance, Note = Fmt.PeriodName(period, ctx.Language) }));

        mutations.Add(EntityMutation.Upsert(new MonthSnapshot
        {
            Id = DeterministicId.ForSnapshot(ctx.HouseholdId, period),
            Period = period,
            CarryOver = action == "carry" ? 1 : 0,
            CarryOverBufferId = bufferId,
            ClosingBalance = request.ClosingBalance,
        }));
        var result = await sync.ApplyAsync(ctx, ctx.HouseholdId, mutations, ct);
        var rejected = result.Results.FirstOrDefault(r => r.Status == SyncOpStatus.Rejected);
        if (rejected is not null) throw AppException.BadRequest(rejected.ErrorKey ?? "err.validation");
        return result;
    }

    public async Task<ApplyResult> ReopenAsync(UserContext ctx, Period period, CancellationToken ct = default)
    {
        var result = await sync.ApplyAsync(ctx, ctx.HouseholdId,
            [EntityMutation.Remove(SyncTypes.Snapshot, DeterministicId.ForSnapshot(ctx.HouseholdId, period))], ct);
        if (result.Results[0].Status == SyncOpStatus.NoChange) throw AppException.BadRequest("err.month.notClosed");
        return result;
    }
}

/// <summary>Beispieldaten laden und mit einem Klick entfernen (F-142).</summary>
public sealed class DemoService(SyncService sync, HouseholdDataLoader loader)
{
    public async Task<DemoStatusDto> StatusAsync(UserContext ctx, CancellationToken ct = default)
    {
        var data = await loader.LoadForReadAsync(ctx.HouseholdId, ct);
        var count = data.AllEntities().Count(e => e.IsDemo);
        return new DemoStatusDto(count > 0, count);
    }

    public async Task LoadAsync(UserContext ctx, CancellationToken ct = default)
    {
        await sync.ApplyAsync(ctx, ctx.HouseholdId, [], ct, session =>
        {
            if (session.Data.AllEntities().Any(e => e.IsDemo && !e.IsDeleted)) return Task.CompletedTask;
            var active = session.Data.ShallowCopy();
            active.Invalidate();
            var lang = ctx.Language;
            var texts = new DemoData.DemoTexts(k => Texts.Get(lang, "demo." + k), period => Fmt.PeriodName(period, lang));
            var set = DemoData.Create(active, session.Today, session.Now, ctx.UserId, texts);
            foreach (var e in set.All) session.AddEntity(e);
            return Task.CompletedTask;
        });
    }

    public async Task ClearAsync(UserContext ctx, CancellationToken ct = default)
    {
        await sync.ApplyAsync(ctx, ctx.HouseholdId, [], ct, session =>
        {
            foreach (var e in session.Data.AllEntities().Where(e => e.IsDemo && !e.IsDeleted).ToList()) session.MarkDeleted(e, log: false);
            return Task.CompletedTask;
        }, postProcess: false);
    }
}

/// <summary>Änderungsverlauf ansehen und alte Werte wiederherstellen (I-13).</summary>
public sealed class ChangeLogService(IAppDbContext db, SyncService sync)
{
    public async Task<List<ChangeLogDto>> ListAsync(UserContext ctx, string? type, Guid? entityId, int take = 100, CancellationToken ct = default)
    {
        var q = db.ChangeLogs.AsNoTracking().Where(c => c.HouseholdId == ctx.HouseholdId);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(c => c.EntityType == type);
        if (entityId is { } id) q = q.Where(c => c.EntityId == id);
        var logs = await q.OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var userIds = logs.Where(l => l.UserId is not null).Select(l => l.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return logs.Select(l => new ChangeLogDto(l.Id, l.EntityType, l.EntityId, l.Action,
            l.UserId is { } u ? names.GetValueOrDefault(u) : null, l.CreatedAt, l.BeforeJson, l.AfterJson, l.IsConflict, l.Label)).ToList();
    }

    /// <summary>Stellt den Zustand vor einer Änderung wieder her.</summary>
    public async Task<SyncOpResult> RestoreAsync(UserContext ctx, Guid logId, CancellationToken ct = default)
    {
        var log = await db.ChangeLogs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == logId && c.HouseholdId == ctx.HouseholdId, ct)
                  ?? throw AppException.NotFound();
        if (log.BeforeJson is null || !SyncTypes.ClrTypes.TryGetValue(log.EntityType, out var clr) || log.EntityType == SyncTypes.Household)
            throw AppException.BadRequest("err.validation");
        var entity = JsonSerializer.Deserialize(log.BeforeJson, clr, JsonDefaults.Options) as HouseholdEntity ?? throw AppException.BadRequest("err.validation");
        entity.IsDeleted = false;
        var result = await sync.ApplyAsync(ctx, ctx.HouseholdId, [EntityMutation.Upsert(entity)], ct);
        return result.Results[0];
    }
}

/// <summary>Konto löschen (F-111) und Datenauskunft (DSGVO Art. 15/20).</summary>
public sealed class AccountService(IAppDbContext db, IClock clock, IEmailQueue mail, HouseholdDataLoader loader, SyncService sync,
    IRealtimeNotifier realtime, NotificationService notifications)
{
    public async Task DeleteAccountAsync(UserContext ctx, CancellationToken ct = default)
    {
        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        var hid = user.HouseholdId;
        var others = await db.Users.Where(u => u.HouseholdId == hid && u.Id != user.Id).OrderBy(u => u.JoinedAt).ToListAsync(ct);
        if (others.Count == 0) await HouseholdPurge.DeleteAllAsync(db, hid, ct);
        else if (user.Role == Role.Owner) others[0].Role = Role.Owner;

        await db.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.Passkeys.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.PushSubscriptions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.NotificationSettings.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.NotificationLogs.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.ApiTokens.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.LoginTokens.Where(x => x.Email == user.Email).ExecuteDeleteAsync(ct);
        await db.Feedback.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.UserId, (Guid?)null), ct);
        await db.ChangeLogs.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.UserId, (Guid?)null), ct);
        await db.Invitations.Where(x => x.CreatedBy == user.Id && x.AcceptedAt == null).ExecuteDeleteAsync(ct);
        var email = user.Email;
        var lang = user.Language;
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);

        if (others.Count > 0)
        {
            await sync.BumpAsync(hid, ct);
            var household = await db.Households.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hid, ct);
            foreach (var m in others)
                await notifications.NotifyAsync(m, NotificationType.HouseholdMembership, $"left:{user.Id}",
                    Texts.Get(m.Language, "notif.member.title", household?.Name ?? ""), Texts.Get(m.Language, "notif.member.left", user.Name), "/haushalt", ct);
        }
        await realtime.AccessRevokedAsync(user.Id);
        mail.Enqueue(EmailTemplates.AccountDeleted(lang, email));
    }

    /// <summary>Vollständige Kopie aller Daten zum Nutzer und Haushalt als JSON.</summary>
    public async Task<byte[]> PersonalDataJsonAsync(UserContext ctx, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == ctx.UserId, ct);
        var data = await loader.LoadAsync(ctx.HouseholdId, includeDeleted: false, ct);
        var export = new
        {
            exportedAt = clock.UtcNow,
            profile = new { user.Id, user.Email, user.Name, user.Language, user.TimeZone, user.Theme, user.Role, user.CreatedAt, user.LastLoginAt },
            notificationSettings = await db.NotificationSettings.AsNoTracking().Where(s => s.UserId == user.Id).Select(s => new { s.Type, s.Channel, s.Active }).ToListAsync(ct),
            notifications = await db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id).Select(n => new { n.Type, n.Title, n.Body, n.CreatedAt, n.ReadAt }).ToListAsync(ct),
            sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == user.Id).Select(s => new { s.DeviceName, s.CreatedAt, s.LastUsedAt, s.RevokedAt }).ToListAsync(ct),
            passkeys = await db.Passkeys.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => new { p.Name, p.CreatedAt, p.LastUsedAt }).ToListAsync(ct),
            apiTokens = await db.ApiTokens.AsNoTracking().Where(t => t.UserId == user.Id).Select(t => new { t.Name, t.CreatedAt, t.ExpiresAt, t.RevokedAt }).ToListAsync(ct),
            household = data,
        };
        return JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true });
    }
}
