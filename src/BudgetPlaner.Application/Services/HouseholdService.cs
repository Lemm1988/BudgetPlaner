using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Application.Services;

/// <summary>Haushalt, Mitglieder, Einladungen und Rollen (F-002, F-003).</summary>
public sealed class HouseholdService(
    IAppDbContext db,
    IClock clock,
    IEmailQueue mail,
    IDataProtector protector,
    IOptions<AppOptions> options,
    SyncService sync,
    AuthService auth,
    NotificationService notifications,
    IRealtimeNotifier realtime,
    HouseholdDataLoader loader,
    IPublicUrlProvider urls)
{
    public async Task<HouseholdInfoDto> GetInfoAsync(UserContext ctx, CancellationToken ct = default)
    {
        var data = await loader.LoadForReadAsync(ctx.HouseholdId, ct);
        return new HouseholdInfoDto(data.Household, data.Members, ctx.Role);
    }

    /// <summary>Haushaltseinstellungen – nur Owner (I-07). Monatsstart wirkt ab der Folgeperiode (F-101).</summary>
    public async Task<HouseholdInfoDto> UpdateSettingsAsync(UserContext ctx, HouseholdSettingsUpdate update, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        var current = await db.Households.AsNoTracking().FirstAsync(h => h.Id == ctx.HouseholdId, ct);
        var changed = (Household)EntityMerge.Clone(current);
        if (update.Name is not null) changed.Name = update.Name;
        if (update.Currency is not null) changed.Currency = update.Currency.ToUpperInvariant();
        if (update.MonthStartDay is { } day)
        {
            if (day is < 1 or > 28) throw AppException.BadRequest("err.monthStart.range");
            var tz = await sync.OwnerTimeZoneAsync(ctx.HouseholdId, ct);
            var currentPeriod = new PeriodCalendar(current.MonthStartRules).PeriodOf(clock.TodayIn(tz));
            changed.MonthStartRules = PeriodCalendar.WithChangedDay(current.MonthStartRules, currentPeriod, day);
        }
        if (update.ResetAmpelRules) changed.AmpelRules = null;
        else if (update.AmpelRules is not null) changed.AmpelRules = update.AmpelRules;
        var result = await sync.ApplyAsync(ctx, ctx.HouseholdId, [EntityMutation.Settings(changed)], ct);
        if (result.Results[0].Status == SyncOpStatus.Rejected) throw AppException.BadRequest(result.Results[0].ErrorKey ?? "err.validation");
        return await GetInfoAsync(ctx, ct);
    }

    // ---------------------------------------------------------------- Einladungen

    public async Task<List<InvitationDto>> ListInvitationsAsync(UserContext ctx, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        var now = clock.UtcNow;
        var list = await db.Invitations.AsNoTracking()
            .Where(i => i.HouseholdId == ctx.HouseholdId && i.AcceptedAt == null && i.RevokedAt == null)
            .ToListAsync(ct);
        return list.Where(i => i.ExpiresAt > now).OrderByDescending(i => i.CreatedAt).Select(ToDto).ToList();
    }

    public async Task<InvitationDto> CreateInvitationAsync(UserContext ctx, CreateInvitationRequest request, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        string? email = null;
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            email = TokenHelper.NormalizeEmail(request.Email);
            if (!TokenHelper.IsValidEmail(email)) throw AppException.BadRequest("err.email.invalid");
        }
        var token = TokenHelper.RandomToken();
        var now = clock.UtcNow;
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            HouseholdId = ctx.HouseholdId,
            CreatedBy = ctx.UserId,
            Email = email,
            TokenHash = TokenHelper.Sha256(token),
            TokenProtected = protector.Protect(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.InvitationDays),
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        var dto = ToDto(invitation);
        if (email is not null)
        {
            var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == ctx.HouseholdId, ct);
            var recipient = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
            mail.Enqueue(EmailTemplates.Invitation(recipient?.Language ?? ctx.Language, email, ctx.User.Name, household.Name, dto.Link));
        }
        return dto;
    }

    public async Task RevokeInvitationAsync(UserContext ctx, Guid id, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        var inv = await db.Invitations.FirstOrDefaultAsync(i => i.Id == id && i.HouseholdId == ctx.HouseholdId, ct) ?? throw AppException.NotFound();
        inv.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<InvitationInfoDto> GetInvitationInfoAsync(string token, UserContext? ctx, CancellationToken ct = default)
    {
        var inv = await FindValidInvitationAsync(token, ct);
        if (inv is null) return new InvitationInfoDto("", "", default, false, false, false, false);
        var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == inv.HouseholdId, ct);
        var inviter = await db.Users.AsNoTracking().Where(u => u.Id == inv.CreatedBy).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";
        var alreadyMember = ctx is not null && ctx.HouseholdId == inv.HouseholdId;
        var hasData = false;
        var otherMembers = false;
        if (ctx is not null && !alreadyMember)
        {
            hasData = await db.Entries.AnyAsync(e => e.HouseholdId == ctx.HouseholdId && !e.IsDeleted, ct)
                      || await db.Debts.AnyAsync(e => e.HouseholdId == ctx.HouseholdId && !e.IsDeleted, ct)
                      || await db.Buffers.AnyAsync(e => e.HouseholdId == ctx.HouseholdId && !e.IsDeleted, ct);
            otherMembers = await db.Users.AnyAsync(u => u.HouseholdId == ctx.HouseholdId && u.Id != ctx.UserId, ct);
        }
        return new InvitationInfoDto(household.Name, inviter, inv.ExpiresAt, true, alreadyMember, hasData, otherMembers);
    }

    /// <summary>Einladung annehmen: Nutzer verlässt seinen bisherigen Haushalt (fremde Daten bleiben unberührt).</summary>
    public async Task AcceptInvitationAsync(UserContext ctx, string token, bool confirmLeave, CancellationToken ct = default)
    {
        var inv = await FindValidInvitationAsync(token, ct) ?? throw AppException.BadRequest("err.invite.invalid");
        if (inv.HouseholdId == ctx.HouseholdId) throw AppException.BadRequest("err.invite.alreadyMember");
        if (!confirmLeave) throw AppException.BadRequest("err.invite.confirmLeave");

        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        var oldHousehold = user.HouseholdId;
        await DetachFromHouseholdAsync(user, ct);

        user.HouseholdId = inv.HouseholdId;
        user.Role = Role.Member;
        user.JoinedAt = clock.UtcNow;
        var tracked = await db.Invitations.FirstAsync(i => i.Id == inv.Id, ct);
        tracked.AcceptedAt = clock.UtcNow;
        tracked.AcceptedBy = user.Id;
        await db.SaveChangesAsync(ct);

        await sync.BumpAsync(inv.HouseholdId, ct);
        if (await db.Households.AnyAsync(h => h.Id == oldHousehold, ct)) await sync.BumpAsync(oldHousehold, ct);
        await realtime.ProfileChangedAsync(user.Id);
        await NotifyMembersAsync(inv.HouseholdId, user.Id, "notif.member.joined", user.Name, ct);
    }

    // ---------------------------------------------------------------- Mitglieder

    /// <summary>Owner entfernt ein Mitglied – Zugriff endet sofort (F-003); die Person erhält einen eigenen leeren Haushalt.</summary>
    public async Task RemoveMemberAsync(UserContext ctx, Guid userId, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        if (userId == ctx.UserId) throw AppException.BadRequest("err.household.self");
        var member = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.HouseholdId == ctx.HouseholdId, ct)
                     ?? throw AppException.BadRequest("err.household.notMember");
        var householdName = await db.Households.Where(h => h.Id == ctx.HouseholdId).Select(h => h.Name).FirstAsync(ct);
        member.HouseholdId = await auth.CreateHouseholdAsync(member, ct);
        member.Role = Role.Owner;
        member.JoinedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await sync.BumpAsync(ctx.HouseholdId, ct);
        await realtime.AccessRevokedAsync(member.Id);
        await notifications.NotifyAsync(member, NotificationType.HouseholdMembership, $"removed:{ctx.HouseholdId}:{clock.UtcNow.Ticks}",
            Texts.Get(member.Language, "notif.member.title", householdName), Texts.Get(member.Language, "notif.member.removed", householdName), "/haushalt", ct);
    }

    /// <summary>Mitglied verlässt den Haushalt (Owner muss vorher übertragen).</summary>
    public async Task LeaveAsync(UserContext ctx, CancellationToken ct = default)
    {
        var user = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        var others = await db.Users.CountAsync(u => u.HouseholdId == user.HouseholdId && u.Id != user.Id, ct);
        if (user.Role == Role.Owner) throw AppException.BadRequest("err.household.ownerCannotLeave");
        if (others == 0) throw AppException.BadRequest("err.household.ownerCannotLeave");
        var old = user.HouseholdId;
        user.HouseholdId = await auth.CreateHouseholdAsync(user, ct);
        user.Role = Role.Owner;
        user.JoinedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await sync.BumpAsync(old, ct);
        await realtime.ProfileChangedAsync(user.Id);
        await NotifyMembersAsync(old, user.Id, "notif.member.left", user.Name, ct);
    }

    public async Task TransferOwnershipAsync(UserContext ctx, Guid userId, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.HouseholdId == ctx.HouseholdId, ct)
                     ?? throw AppException.BadRequest("err.household.notMember");
        if (target.Id == ctx.UserId) return;
        var me = await db.Users.FirstAsync(u => u.Id == ctx.UserId, ct);
        target.Role = Role.Owner;
        me.Role = Role.Member;
        await db.SaveChangesAsync(ct);
        await sync.BumpAsync(ctx.HouseholdId, ct);
        await realtime.ProfileChangedAsync(target.Id);
        await realtime.ProfileChangedAsync(me.Id);
        await NotifyMembersAsync(ctx.HouseholdId, null, "notif.member.owner", target.Name, ct);
    }

    /// <summary>Owner löscht den Haushalt samt Daten; alle Mitglieder erhalten einen neuen leeren Haushalt.</summary>
    public async Task DeleteHouseholdAsync(UserContext ctx, CancellationToken ct = default)
    {
        ctx.RequireOwner();
        var hid = ctx.HouseholdId;
        var members = await db.Users.Where(u => u.HouseholdId == hid).ToListAsync(ct);
        foreach (var m in members)
        {
            m.HouseholdId = await auth.CreateHouseholdAsync(m, ct);
            m.Role = Role.Owner;
            m.JoinedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await HouseholdPurge.DeleteAllAsync(db, hid, ct);
        foreach (var m in members) await realtime.AccessRevokedAsync(m.Id);
    }

    /// <summary>Löst den Nutzer aus seinem Haushalt: Owner-Rolle wird übertragen bzw. ein leerer Einzelhaushalt gelöscht.</summary>
    public async Task DetachFromHouseholdAsync(User user, CancellationToken ct)
    {
        var others = await db.Users.Where(u => u.HouseholdId == user.HouseholdId && u.Id != user.Id).OrderBy(u => u.JoinedAt).ToListAsync(ct);
        if (others.Count == 0)
        {
            var old = user.HouseholdId;
            await HouseholdPurge.DeleteAllAsync(db, old, ct);
            return;
        }
        if (user.Role == Role.Owner) others[0].Role = Role.Owner;
    }

    private async Task<Invitation?> FindValidInvitationAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = TokenHelper.Sha256(token.Trim());
        var inv = await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (inv is null || inv.AcceptedAt is not null || inv.RevokedAt is not null || inv.ExpiresAt < clock.UtcNow) return null;
        return await db.Households.AnyAsync(h => h.Id == inv.HouseholdId, ct) ? inv : null;
    }

    private InvitationDto ToDto(Invitation i)
    {
        var token = protector.Unprotect(i.TokenProtected);
        var link = $"{urls.BaseUrl.TrimEnd('/')}/einladung/{Uri.EscapeDataString(token)}";
        return new InvitationDto(i.Id, i.Email, link, i.ExpiresAt, i.CreatedAt);
    }

    private async Task NotifyMembersAsync(Guid householdId, Guid? exceptUser, string key, string arg, CancellationToken ct)
    {
        var household = await db.Households.AsNoTracking().FirstOrDefaultAsync(h => h.Id == householdId, ct);
        if (household is null) return;
        var members = await db.Users.AsNoTracking().Where(u => u.HouseholdId == householdId && u.Id != exceptUser).ToListAsync(ct);
        foreach (var m in members)
            await notifications.NotifyAsync(m, NotificationType.HouseholdMembership, $"{key}:{arg}:{clock.UtcNow.Ticks}",
                Texts.Get(m.Language, "notif.member.title", household.Name), Texts.Get(m.Language, key, arg), "/haushalt", ct);
    }
}

/// <summary>Endgültiges Löschen aller Daten eines Haushalts (F-111, Haushalt löschen).</summary>
public static class HouseholdPurge
{
    public static async Task DeleteAllAsync(IAppDbContext db, Guid hid, CancellationToken ct)
    {
        await db.EntryMonths.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.EntryChanges.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Entries.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.DebtPayments.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Debts.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.BufferTransactions.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Buffers.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Snapshots.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Checkpoints.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Categories.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.ChangeLogs.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Invitations.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(x => x.HouseholdId == hid).ExecuteDeleteAsync(ct);
        await db.Households.Where(x => x.Id == hid).ExecuteDeleteAsync(ct);
    }
}
