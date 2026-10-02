using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Server.Hosting;

/// <summary>Versendet Mails aus der Warteschlange sofort (F-001: binnen 60 Sekunden) mit Wiederholung.</summary>
public sealed class EmailWorker(EmailQueue queue, IEmailSender sender, IOptions<AppOptions> options, ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(ct))
        {
            if (!sender.IsSmtpConfigured && !options.Value.DevMailbox)
                logger.LogWarning("Kein SMTP konfiguriert – Mail an {To} ({Subject}):\n{Text}", message.To, message.Subject, message.Text);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await sender.SendAsync(message, ct);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Mailversand an {To} fehlgeschlagen (Versuch {Attempt})", message.To, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Mailversand an {To} endgültig fehlgeschlagen", message.To);
                }
            }
        }
    }
}

/// <summary>Wertet Haushalte nach Änderungen aus (F-081, binnen Sekunden) und sendet Monatsstart-Erinnerungen (F-080).</summary>
public sealed class NotificationWorker(NotificationTrigger trigger, IServiceScopeFactory scopes, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(ProcessTriggersAsync(ct), RemindersAsync(ct));

    private async Task ProcessTriggersAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var first = await trigger.Reader.ReadAsync(ct);
            await Task.Delay(1500, ct);
            var batch = new List<NotificationWork> { first };
            while (trigger.Reader.TryRead(out var more)) batch.Add(more);
            foreach (var group in batch.GroupBy(w => w.HouseholdId))
            {
                var work = new NotificationWork(group.Key, group.Last().ActorUserId,
                    group.SelectMany(w => w.ConflictLabels).Distinct().ToList(), group.SelectMany(w => w.PaidOffDebts).ToList());
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<NotificationEvaluator>().EvaluateAsync(work, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Benachrichtigungen für Haushalt {Household} fehlgeschlagen", group.Key);
                }
            }
        }
    }

    private async Task RemindersAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NotificationEvaluator>().MonthStartRemindersAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Monatsstart-Erinnerungen fehlgeschlagen");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>
/// Wartung: fällige Raten/Sparpläne verbuchen (viertelstündlich), Warnungen neu bewerten (6-stündlich),
/// Aufräumen und tägliche SQLite-Backups mit 14 Tagen Aufbewahrung (NFR Verfügbarkeit).
/// </summary>
public sealed class MaintenanceWorker(IServiceScopeFactory scopes, NotificationTrigger trigger, IOptions<AppOptions> options,
    IConfiguration config, IHostEnvironment env, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        var lastDaily = DateTimeOffset.MinValue;
        var lastEvaluation = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            var now = DateTimeOffset.UtcNow;
            await RunSafe("Buchungen", BookAllAsync, ct);
            if (now - lastEvaluation > TimeSpan.FromHours(6))
            {
                lastEvaluation = now;
                await RunSafe("Bewertung", TriggerAllAsync, ct);
            }
            if (now - lastDaily > TimeSpan.FromHours(24))
            {
                lastDaily = now;
                await RunSafe("Aufräumen", CleanupAsync, ct);
                await RunSafe("Backup", BackupAsync, ct);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RunSafe(string name, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogError(ex, "Wartung „{Name}“ fehlgeschlagen", name); }
    }

    private async Task<List<Guid>> HouseholdIdsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Households.AsNoTracking().Select(h => h.Id).ToListAsync(ct);
    }

    private async Task BookAllAsync(CancellationToken ct)
    {
        foreach (var id in await HouseholdIdsAsync(ct))
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SyncService>().ApplyAsync(null, id, [], ct);
        }
    }

    private async Task TriggerAllAsync(CancellationToken ct)
    {
        foreach (var id in await HouseholdIdsAsync(ct)) trigger.Trigger(new NotificationWork(id, null, [], []));
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var o = options.Value;
        await db.LoginTokens.Where(t => t.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        await db.Sessions.Where(s => (s.RevokedAt != null || s.ExpiresAt < now) && s.LastUsedAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(n => n.CreatedAt < now.AddDays(-180)).ExecuteDeleteAsync(ct);
        await db.NotificationLogs.Where(n => n.CreatedAt < now.AddDays(-400)).ExecuteDeleteAsync(ct);
        await db.ChangeLogs.Where(c => c.CreatedAt < now.AddDays(-o.ChangeLogRetentionDays)).ExecuteDeleteAsync(ct);
        await db.DevMails.Where(m => m.CreatedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        await db.Invitations.Where(i => i.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);

        // Tombstones bereinigen; verbuchte Automatik-Buchungen bleiben als Unterdrückungsmarker erhalten.
        var limit = now.AddDays(-o.TombstoneRetentionDays);
        var affected = new HashSet<Guid>();
        async Task Purge<T>(IQueryable<T> q) where T : HouseholdEntity
        {
            var ids = await q.Where(e => e.IsDeleted && e.UpdatedAt < limit).Select(e => e.HouseholdId).Distinct().ToListAsync(ct);
            if (ids.Count == 0) return;
            affected.UnionWith(ids);
            await q.Where(e => e.IsDeleted && e.UpdatedAt < limit).ExecuteDeleteAsync(ct);
        }
        await Purge(db.Categories);
        await Purge(db.Entries);
        await Purge(db.EntryChanges);
        await Purge(db.EntryMonths);
        await Purge(db.Debts);
        await Purge(db.DebtPayments.Where(p => p.SourceEntryId == null));
        await Purge(db.Buffers);
        await Purge(db.BufferTransactions.Where(t => t.SourceEntryId == null));
        await Purge(db.Snapshots);
        await Purge(db.Checkpoints);
        foreach (var hid in affected)
            await db.Households.Where(h => h.Id == hid).ExecuteUpdateAsync(s => s.SetProperty(h => h.PurgedSeq, h => h.ChangeSeq), ct);
    }

    private async Task BackupAsync(CancellationToken ct)
    {
        if (config.GetConnectionString("Default") is { } cs && !cs.Contains(".db", StringComparison.OrdinalIgnoreCase)) return;
        var dir = Path.Combine(Path.GetFullPath(o().DataDirectory, env.ContentRootPath), "backups");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"budgetplaner-{DateTime.UtcNow:yyyyMMdd-HHmm}.db");
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {file}", ct);
        }
        foreach (var old in Directory.GetFiles(dir, "budgetplaner-*.db").Where(f => File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddDays(-o().BackupRetentionDays)))
            File.Delete(old);
        logger.LogInformation("Backup erstellt: {File}", file);

        AppOptions o() => options.Value;
    }
}
