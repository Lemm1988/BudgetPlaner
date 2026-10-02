using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Entities;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BudgetPlaner.Application.Services;

/// <summary>Erstellt und verteilt Benachrichtigungen über die aktivierten Kanäle (F-080, F-081).</summary>
public sealed class NotificationService(
    IAppDbContext db,
    IClock clock,
    IEmailQueue mail,
    IPushSender push,
    IRealtimeNotifier realtime,
    IPublicUrlProvider urls,
    ILogger<NotificationService> logger)
{
    /// <summary>
    /// Benachrichtigt einen Nutzer, falls die Art aktiviert ist und für <paramref name="dedupeKey"/> noch nichts gesendet wurde.
    /// </summary>
    public async Task<bool> NotifyAsync(User user, NotificationType type, string dedupeKey, string title, string body, string? link, CancellationToken ct = default)
    {
        var rows = await db.NotificationSettings.AsNoTracking().Where(s => s.UserId == user.Id && s.Type == type).ToListAsync(ct);
        bool Active(NotificationChannel c) => rows.FirstOrDefault(r => r.Channel == c)?.Active ?? NotificationDefaults.DefaultActive(type, c);
        var inApp = Active(NotificationChannel.InApp);
        var viaPush = Active(NotificationChannel.Push);
        var viaEmail = Active(NotificationChannel.Email);
        if (!inApp && !viaPush && !viaEmail) return false;

        var key = dedupeKey.Length > 300 ? dedupeKey[..300] : dedupeKey;
        if (await db.NotificationLogs.AnyAsync(l => l.UserId == user.Id && l.Type == type && l.Key == key, ct)) return false;
        var now = clock.UtcNow;
        db.NotificationLogs.Add(new NotificationLog { Id = Guid.NewGuid(), UserId = user.Id, Type = type, Key = key, CreatedAt = now });
        Notification? n = null;
        if (inApp)
        {
            n = new Notification { Id = Guid.NewGuid(), UserId = user.Id, HouseholdId = user.HouseholdId, Type = type, Title = title, Body = body, Link = link, CreatedAt = now };
            db.Notifications.Add(n);
        }
        await db.SaveChangesAsync(ct);
        if (n is not null) await realtime.NotificationAsync(user.Id, ToDto(n));

        if (viaPush && push.IsConfigured)
        {
            var subs = await db.PushSubscriptions.Where(s => s.UserId == user.Id).ToListAsync(ct);
            foreach (var sub in subs)
            {
                try
                {
                    var ok = await push.SendAsync(sub, new PushPayload(title, body, link, $"{type}:{key}"), ct);
                    if (ok) { sub.LastSuccessAt = now; sub.FailureCount = 0; }
                    else db.PushSubscriptions.Remove(sub);
                }
                catch (Exception ex)
                {
                    sub.FailureCount++;
                    if (sub.FailureCount >= 5) db.PushSubscriptions.Remove(sub);
                    logger.LogWarning(ex, "Push an {Endpoint} fehlgeschlagen", sub.Endpoint);
                }
            }
            await db.SaveChangesAsync(ct);
        }

        if (viaEmail)
        {
            var absolute = link is null ? null : $"{urls.BaseUrl.TrimEnd('/')}{link}";
            mail.Enqueue(EmailTemplates.Notification(user.Language, user.Email, title, body, absolute));
        }
        return true;
    }

    public async Task<List<NotificationDto>> ListAsync(UserContext ctx, int take = 50, CancellationToken ct = default)
    {
        var list = await db.Notifications.AsNoTracking().Where(n => n.UserId == ctx.UserId)
            .OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task MarkReadAsync(UserContext ctx, Guid? id, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var q = db.Notifications.Where(n => n.UserId == ctx.UserId && n.ReadAt == null);
        if (id is { } nid) q = q.Where(n => n.Id == nid);
        foreach (var n in await q.ToListAsync(ct)) n.ReadAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(UserContext ctx, Guid id, CancellationToken ct = default)
    {
        await db.Notifications.Where(n => n.UserId == ctx.UserId && n.Id == id).ExecuteDeleteAsync(ct);
    }

    public static NotificationDto ToDto(Notification n) => new(n.Id, n.Type, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt);
}

/// <summary>
/// Wertet Haushalte aus: Frühwarnungen (F-073), Zielerreichung/Tilgung (F-081), Konflikte und Monatsstart-Erinnerungen (F-080).
/// </summary>
public sealed class NotificationEvaluator(
    IAppDbContext db,
    HouseholdDataLoader loader,
    NotificationService notifier,
    SyncService sync,
    IClock clock,
    IOptions<AppOptions> options)
{
    public async Task EvaluateAsync(NotificationWork work, CancellationToken ct = default)
    {
        var members = await db.Users.AsNoTracking().Where(u => u.HouseholdId == work.HouseholdId).ToListAsync(ct);
        if (members.Count == 0 || !await db.Households.AnyAsync(h => h.Id == work.HouseholdId, ct)) return;
        var data = await loader.LoadAsync(work.HouseholdId, includeDeleted: false, ct);
        var today = clock.TodayIn(await sync.OwnerTimeZoneAsync(work.HouseholdId, ct));
        var engine = new HouseholdEngine(data, today);
        var currency = data.Household.Currency;

        var warnings = engine.Forecasts.Warnings();
        foreach (var member in members)
        {
            var lang = member.Language;
            foreach (var w in warnings)
            {
                var (title, body, link) = Describe(w, lang, currency, engine);
                await notifier.NotifyAsync(member, w.Type, w.Key, title, body, link, ct);
            }

            foreach (var b in engine.Buffers.All().Where(s => s.GoalReached && !s.Buffer.IsDemo))
            {
                var target = Fmt.Money(b.Target ?? 0, currency, lang);
                await notifier.NotifyAsync(member, NotificationType.BufferGoalReached, $"goal:{b.Buffer.Id}:{b.Target}",
                    Texts.Get(lang, "notif.bufferGoalReached.title", b.Buffer.Name), Texts.Get(lang, "notif.bufferGoalReached.body", b.Buffer.Name, target),
                    $"/polster/{b.Buffer.Id}", ct);
            }

            foreach (var d in engine.Debts.All().Where(s => s.IsPaidOff && !s.Debt.IsDemo && s.Payments.Count > 0))
                await notifier.NotifyAsync(member, NotificationType.DebtPaidOff, $"paid:{d.Debt.Id}",
                    Texts.Get(lang, "notif.debtPaidOff.title", d.Debt.Name), Texts.Get(lang, "notif.debtPaidOff.body", d.Debt.Name),
                    $"/schulden/{d.Debt.Id}", ct);

            foreach (var label in work.ConflictLabels.Distinct())
                await notifier.NotifyAsync(member, NotificationType.SyncConflict, $"conflict:{label}:{clock.UtcNow.Ticks}",
                    Texts.Get(lang, "notif.syncConflict.title"), Texts.Get(lang, "notif.syncConflict.body", label), "/verlauf", ct);
        }
    }

    /// <summary>„Genau eine Benachrichtigung am Stichtag“ (F-080): am ersten Tag der Periode ab der Erinnerungsstunde.</summary>
    public async Task MonthStartRemindersAsync(CancellationToken ct = default)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.ReminderActive).ToListAsync(ct);
        var households = new Dictionary<Guid, BudgetPlaner.Domain.Entities.Household?>();
        foreach (var user in users)
        {
            var now = clock.NowIn(user.TimeZone);
            if (now.Hour < options.Value.MonthStartReminderHour) continue;
            if (!households.TryGetValue(user.HouseholdId, out var household))
                households[user.HouseholdId] = household = await db.Households.AsNoTracking().FirstOrDefaultAsync(h => h.Id == user.HouseholdId, ct);
            if (household is null) continue;
            var calendar = new PeriodCalendar(household.MonthStartRules);
            var today = DateOnly.FromDateTime(now.DateTime);
            var period = calendar.PeriodOf(today);
            if (calendar.Start(period) != today) continue;
            await notifier.NotifyAsync(user, NotificationType.MonthStart, period.ToString(),
                Texts.Get(user.Language, "notif.monthStart.title", Fmt.PeriodName(period, user.Language)),
                Texts.Get(user.Language, "notif.monthStart.body"), $"/budget?monat={period}", ct);
        }
    }

    public static (string Title, string Body, string Link) Describe(Warning w, string lang, string currency, HouseholdEngine engine)
    {
        string M(decimal? v) => Fmt.Money(v ?? 0, currency, lang);
        switch (w.Type)
        {
            case NotificationType.BudgetOverplanned:
                return (Texts.Get(lang, "notif.budgetOverplanned.title"),
                    Texts.Get(lang, "notif.budgetOverplanned.body", Fmt.PeriodName(engine.CurrentPeriod, lang), M(-(w.Amount1 ?? 0))), "/budget");
            case NotificationType.BudgetEarlyWarning:
                return (Texts.Get(lang, "notif.budgetEarlyWarning.title"), Texts.Get(lang, "notif.budgetEarlyWarning.body", M(w.Amount1), M(w.Amount2)), "/prognosen");
            case NotificationType.CategoryOverrun:
                var cat = Labels.CategoryName(engine.Index, w.SubjectId, lang);
                return (Texts.Get(lang, "notif.categoryOverrun.title", cat), Texts.Get(lang, "notif.categoryOverrun.body", cat, M(w.Amount1), M(w.Amount2)), "/statistik");
            case NotificationType.NegativeBalanceForecast:
                return (Texts.Get(lang, "notif.negativeBalanceForecast.title"),
                    Texts.Get(lang, "notif.negativeBalanceForecast.body", w.Date is { } d ? Fmt.Date(d, lang) : "", M(w.Amount1)), "/cashflow");
            case NotificationType.FixedCostRatioHigh:
                return (Texts.Get(lang, "notif.fixedCostRatioHigh.title"),
                    Texts.Get(lang, "notif.fixedCostRatioHigh.body", Fmt.Percent(w.Amount1, lang), Fmt.Percent(w.Amount2, lang, 0)), "/statistik");
            case NotificationType.DebtRateDue when w.Key.StartsWith("debtinterest", StringComparison.Ordinal):
                return (Texts.Get(lang, "notif.debtInterest.title", w.Subject ?? ""), Texts.Get(lang, "notif.debtInterest.body", w.Subject ?? "", M(w.Amount1), M(w.Amount2)),
                    $"/schulden/{w.SubjectId}");
            case NotificationType.DebtRateDue:
                return (Texts.Get(lang, "notif.debtRateDue.title", w.Subject ?? ""), Texts.Get(lang, "notif.debtRateDue.body", w.Subject ?? "", M(w.Amount1)),
                    $"/schulden/{w.SubjectId}");
            default:
                return (Labels.Of(w.Type, lang), "", "/uebersicht");
        }
    }
}
