using System.Text.Json;
using System.Threading.Channels;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BudgetPlaner.Application.Services.Sync;

public sealed record ApplyResult(long Seq, IReadOnlyList<SyncOpResult> Results, SyncSession Session);

/// <summary>Arbeit für den Benachrichtigungs-Auswerter (F-081: Zielerreichung binnen 5 Minuten).</summary>
public sealed record NotificationWork(Guid HouseholdId, Guid? ActorUserId, IReadOnlyList<string> ConflictLabels, IReadOnlyList<DebtEntry> PaidOffDebts);

/// <summary>Warteschlange: Haushalte, deren Benachrichtigungen neu ausgewertet werden müssen.</summary>
public sealed class NotificationTrigger
{
    private readonly Channel<NotificationWork> _channel = Channel.CreateUnbounded<NotificationWork>();

    public ChannelReader<NotificationWork> Reader => _channel.Reader;

    public void Trigger(NotificationWork work) => _channel.Writer.TryWrite(work);
}

/// <summary>
/// Zentraler Schreib- und Synchronisationsdienst (F-131, F-132). Jede Änderung – aus der App-Outbox, der REST-API
/// oder internen Diensten – läuft hier durch: Sperre je Haushalt → Merge/Validierung → Nachverarbeitung →
/// Speichern mit neuer Änderungssequenz → SignalR-Signal an alle Geräte.
/// </summary>
public sealed class SyncService(
    IAppDbContext db,
    HouseholdDataLoader loader,
    HouseholdLocks locks,
    HouseholdReadCache cache,
    IClock clock,
    IRealtimeNotifier realtime,
    NotificationTrigger trigger,
    ILogger<SyncService> logger)
{
    public async Task<SyncPullResponse> PullAsync(UserContext ctx, long since, CancellationToken ct = default)
    {
        var hid = ctx.HouseholdId;
        var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == hid, ct);
        var seq = household.ChangeSeq;
        var full = since <= 0 || since < household.PurgedSeq || since > seq;
        var response = new SyncPullResponse { Seq = seq, Full = full };
        if (full)
        {
            var data = await loader.LoadAsync(hid, includeDeleted: false, ct);
            response.Household = data.Household;
            response.Members = data.Members;
            response.Categories = data.Categories;
            response.Entries = data.Entries;
            response.EntryChanges = data.EntryChanges;
            response.EntryMonths = data.EntryMonths;
            response.Debts = data.Debts;
            response.DebtPayments = data.DebtPayments;
            response.Buffers = data.Buffers;
            response.BufferTransactions = data.BufferTransactions;
            response.Snapshots = data.Snapshots;
            response.Checkpoints = data.Checkpoints;
            return response;
        }

        response.Household = household.Version > since ? household : null;
        response.Categories = await Since(db.Categories).ToListAsync(ct);
        response.Entries = await Since(db.Entries).ToListAsync(ct);
        response.EntryChanges = await Since(db.EntryChanges).ToListAsync(ct);
        response.EntryMonths = await Since(db.EntryMonths).ToListAsync(ct);
        response.Debts = await Since(db.Debts).ToListAsync(ct);
        response.DebtPayments = await Since(db.DebtPayments).ToListAsync(ct);
        response.Buffers = await Since(db.Buffers).ToListAsync(ct);
        response.BufferTransactions = await Since(db.BufferTransactions).ToListAsync(ct);
        response.Snapshots = await Since(db.Snapshots).ToListAsync(ct);
        response.Checkpoints = await Since(db.Checkpoints).ToListAsync(ct);
        response.Members = await db.Users.AsNoTracking().Where(u => u.HouseholdId == hid).OrderBy(u => u.JoinedAt)
            .Select(u => new Member { UserId = u.Id, Name = u.Name, Email = u.Email, Role = u.Role, JoinedAt = u.JoinedAt })
            .ToListAsync(ct);
        return response;

        IQueryable<T> Since<T>(DbSet<T> set) where T : HouseholdEntity =>
            set.AsNoTracking().Where(e => e.HouseholdId == hid && e.Version > since);
    }

    /// <summary>Outbox-Operationen des Clients anwenden.</summary>
    public async Task<SyncPushResponse> PushAsync(UserContext ctx, SyncPushRequest request, CancellationToken ct = default)
    {
        var mutations = new List<EntityMutation>(request.Operations.Count);
        var invalid = new List<SyncOpResult>();
        foreach (var op in request.Operations)
        {
            if (!SyncTypes.ClrTypes.TryGetValue(op.Type, out var clr))
            {
                invalid.Add(new SyncOpResult(op.OpId, SyncOpStatus.Rejected, "err.sync.unknownType"));
                continue;
            }
            try
            {
                var delete = string.Equals(op.Action, "delete", StringComparison.OrdinalIgnoreCase);
                var data = !delete && op.Data is { } d ? d.Deserialize(clr, JsonDefaults.Options) : null;
                // Ohne Basis (offline neu angelegt) gilt ein leerer Datensatz als Basis: nur tatsächlich gesetzte Felder
                // überschreiben eine inzwischen von einem anderen Gerät angelegte Version (kein Datenverlust, F-131).
                var baseline = op.Base is { ValueKind: JsonValueKind.Object } b ? b.Deserialize(clr, JsonDefaults.Options)
                    : delete || clr == typeof(Household) ? null : Activator.CreateInstance(clr);
                mutations.Add(new EntityMutation(op.OpId, op.Type, op.Id, delete, data, baseline));
            }
            catch (JsonException)
            {
                invalid.Add(new SyncOpResult(op.OpId, SyncOpStatus.Rejected, "err.validation"));
            }
        }
        var result = await ApplyAsync(ctx, ctx.HouseholdId, mutations, ct);
        return new SyncPushResponse(result.Seq, [.. result.Results, .. invalid]);
    }

    /// <summary>
    /// Wendet Änderungen atomar an. <paramref name="work"/> erlaubt zusätzliche serverseitige Schritte in derselben Sitzung.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(UserContext? ctx, Guid householdId, IReadOnlyList<EntityMutation> mutations,
        CancellationToken ct = default, Func<SyncSession, Task>? work = null, bool postProcess = true)
    {
        using var _ = await locks.AcquireAsync(householdId, ct);
        var data = await loader.LoadAsync(householdId, includeDeleted: true, ct);
        var timeZone = await OwnerTimeZoneAsync(householdId, ct);
        var session = new SyncSession(data, ctx, clock.UtcNow, clock.TodayIn(timeZone));

        var results = new List<SyncOpResult>(mutations.Count);
        foreach (var m in mutations) results.Add(session.Apply(m));
        if (work is not null) await work(session);
        if (postProcess) session.PostProcess();

        var seq = await SaveAsync(session, ct);
        if (session.HasChanges)
        {
            cache.Invalidate(householdId);
            await realtime.HouseholdChangedAsync(householdId, seq);
            trigger.Trigger(new NotificationWork(householdId, ctx?.UserId, session.ConflictLabels, session.PaidOffDebts));
        }
        if (results.Any(r => r.Status == SyncOpStatus.Rejected))
            logger.LogInformation("Sync {Household}: {Count} Operation(en) abgelehnt", householdId, results.Count(r => r.Status == SyncOpStatus.Rejected));
        return new ApplyResult(seq, results, session);
    }

    /// <summary>Änderungssequenz erhöhen (z. B. Mitglieder- oder Namensänderung) und Geräte informieren.</summary>
    public async Task<long> BumpAsync(Guid householdId, CancellationToken ct = default)
    {
        using var _ = await locks.AcquireAsync(householdId, ct);
        var household = await db.Households.FirstAsync(h => h.Id == householdId, ct);
        household.ChangeSeq++;
        household.Version = household.ChangeSeq;
        await db.SaveChangesAsync(ct);
        cache.Invalidate(householdId);
        await realtime.HouseholdChangedAsync(householdId, household.ChangeSeq);
        return household.ChangeSeq;
    }

    public async Task<string> OwnerTimeZoneAsync(Guid householdId, CancellationToken ct = default) =>
        await db.Users.AsNoTracking()
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.Role).ThenBy(u => u.JoinedAt)
            .Select(u => u.TimeZone)
            .FirstOrDefaultAsync(ct) ?? "Europe/Berlin";

    private async Task<long> SaveAsync(SyncSession s, CancellationToken ct)
    {
        var household = await db.Households.FirstAsync(h => h.Id == s.HouseholdId, ct);
        if (!s.HasChanges) return household.ChangeSeq;
        var seq = household.ChangeSeq + 1;
        household.ChangeSeq = seq;
        if (s.HouseholdChanged)
        {
            var h = s.Data.Household;
            household.Name = h.Name;
            household.Currency = h.Currency;
            household.MonthStartRules = [.. h.MonthStartRules];
            household.AmpelRules = h.AmpelRules;
            household.OnboardingCompletedAt = h.OnboardingCompletedAt;
            household.UpdatedAt = s.Now;
            household.UpdatedBy = h.UpdatedBy;
            household.Version = seq;
        }
        foreach (var e in s.Added)
        {
            e.Version = seq;
            db.AddEntity(e);
        }
        foreach (var e in s.Modified)
        {
            e.Version = seq;
            db.UpdateEntity(e);
        }
        foreach (var log in s.Logs) db.ChangeLogs.Add(log);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return seq;
    }
}
