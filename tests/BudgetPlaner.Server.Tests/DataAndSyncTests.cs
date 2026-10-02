using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;

namespace BudgetPlaner.Server.Tests;

public class DataAndSyncTests : IClassFixture<TestAppFixture>
{
    private readonly TestApp _app;
    public DataAndSyncTests(TestAppFixture f) => _app = f.App;

    private static Period Current => Period.OfCalendarDate(DateOnly.FromDateTime(DateTime.UtcNow));

    private static BudgetEntry NewEntry(SyncPullResponse pull, string name, decimal amount, EntryKind kind = EntryKind.Expense, string cat = "living") => new()
    {
        Id = Guid.NewGuid(), Name = name, Amount = amount, Kind = kind, Rhythm = Rhythm.Monthly, StartPeriod = Current.AddMonths(-2),
        CategoryId = pull.Categories.Single(c => c.DefaultKey == (kind == EntryKind.Income ? "salary" : cat)).Id, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact(DisplayName = "F-014 über die API: 2.000 € Einnahmen, 50 € Ausgabe → Unverplant 1.950 €")]
    public async Task UnverplantViaApi()
    {
        var u = await _app.LoginAsync("unverplant@example.com");
        var pull = await u.PullAsync();
        (await u.PostAsync("/api/v1/entries", NewEntry(pull, "Gehalt", 2000, EntryKind.Income))).EnsureSuccessStatusCode();
        var empty = await u.GetAsync<JsonElement>($"/api/v1/months/{Current}");
        Assert.Equal(2000m, empty.GetProperty("unverplant").GetDecimal());
        (await u.PostAsync("/api/v1/entries", NewEntry(pull, "Neu", 50))).EnsureSuccessStatusCode();
        var month = await u.GetAsync<JsonElement>($"/api/v1/months/{Current}");
        Assert.Equal(1950m, month.GetProperty("unverplant").GetDecimal());
    }

    [Fact(DisplayName = "F-131: Gleichzeitige Änderungen verschiedener Felder werden zusammengeführt, gleiches Feld ohne Datenverlust")]
    public async Task ConcurrentEdits()
    {
        var a = await _app.LoginAsync("sync-a@example.com");
        var pull = await a.PullAsync();
        var entry = NewEntry(pull, "Strom", 90, cat: "housing");
        var r = await a.PushAsync(UserClient.Upsert(entry));
        Assert.Equal(SyncOpStatus.Applied, r.Results[0].Status);
        var serverCopy = (await a.PullAsync()).Entries.Single(e => e.Id == entry.Id);

        // Gerät A ändert den Betrag, Gerät B (gleiche Basis) den Namen → beides bleibt erhalten.
        var editA = serverCopy.CloneAs<BudgetEntry>();
        editA.Amount = 95;
        var editB = serverCopy.CloneAs<BudgetEntry>();
        editB.Name = "Strom & Gas";
        await a.PushAsync(UserClient.Upsert(editA, serverCopy));
        var merged = await a.PushAsync(UserClient.Upsert(editB, serverCopy));
        Assert.Equal(SyncOpStatus.Merged, merged.Results[0].Status);
        var result = (await a.PullAsync()).Entries.Single(e => e.Id == entry.Id);
        Assert.Equal(95, result.Amount);
        Assert.Equal("Strom & Gas", result.Name);

        // Gleiches Feld: spätere Änderung gewinnt, alter Wert steht im Änderungsverlauf und ist wiederherstellbar.
        var editC = serverCopy.CloneAs<BudgetEntry>();
        editC.Amount = 120;
        var conflict = await a.PushAsync(UserClient.Upsert(editC, serverCopy));
        Assert.Equal(SyncOpStatus.Conflict, conflict.Results[0].Status);
        var log = await a.GetAsync<List<ChangeLogDto>>($"/api/v1/changes?entityId={entry.Id}");
        var conflictEntry = log.First(l => l.IsConflict);
        Assert.Contains("95", conflictEntry.BeforeJson);
        var restored = await a.PostReadAsync<object?, SyncOpResult>($"/api/v1/changes/{conflictEntry.Id}/restore", null);
        Assert.NotEqual(SyncOpStatus.Rejected, restored.Status);
        Assert.Equal(95, (await a.PullAsync()).Entries.Single(e => e.Id == entry.Id).Amount);
    }

    [Fact(DisplayName = "F-131/F-132: Delta-Pull liefert nur Änderungen inkl. Löschungen; Offline-Monatswerte zweier Geräte verschmelzen")]
    public async Task DeltaAndDeterministicIds()
    {
        var u = await _app.LoginAsync("delta@example.com");
        var full = await u.PullAsync();
        var entry = NewEntry(full, "Kita", 300);
        await u.PushAsync(UserClient.Upsert(entry));
        var after = await u.PullAsync(full.Seq);
        Assert.False(after.Full);
        Assert.Single(after.Entries);

        // Zwei Geräte haken offline denselben Monat ab → gleiche deterministische Id → ein Datensatz.
        var month1 = new BudgetEntryMonth { Id = DeterministicId.ForEntryMonth(entry.Id, Current), EntryId = entry.Id, Period = Current, IsDone = true };
        var month2 = new BudgetEntryMonth { Id = DeterministicId.ForEntryMonth(entry.Id, Current), EntryId = entry.Id, Period = Current, ActualAmount = 310 };
        await u.PushAsync(UserClient.Upsert(month1));
        await u.PushAsync(UserClient.Upsert(month2));
        var months = (await u.PullAsync()).EntryMonths.Where(m => m.EntryId == entry.Id).ToList();
        Assert.Single(months);
        Assert.True(months[0].IsDone);
        Assert.Equal(310, months[0].ActualAmount);

        var del = await u.PushAsync(UserClient.Delete(SyncTypes.Entry, entry.Id));
        Assert.Equal(SyncOpStatus.Applied, del.Results[0].Status);
        var delta = await u.PullAsync(after.Seq);
        Assert.Contains(delta.Entries, e => e.Id == entry.Id && e.IsDeleted);
    }

    [Fact(DisplayName = "F-012: Löschen einer Kategorie hängt Positionen auf „Sonstiges“ um; Systemkategorien nicht löschbar")]
    public async Task DeleteCategory()
    {
        var u = await _app.LoginAsync("category@example.com");
        var pull = await u.PullAsync();
        var parent = pull.Categories.Single(c => c.DefaultKey == "wants");
        var own = new Category { Id = Guid.NewGuid(), Name = "Haustiere", Type = CategoryType.Expense, ParentId = parent.Id };
        (await u.PostAsync("/api/v1/categories", own)).EnsureSuccessStatusCode();
        var entry = NewEntry(pull, "Futter", 40);
        entry.CategoryId = own.Id;
        (await u.PostAsync("/api/v1/entries", entry)).EnsureSuccessStatusCode();
        var created = await u.GetAsync<Category>($"/api/v1/categories/{own.Id}");
        Assert.Equal(BudgetGroup.Wants, created.Group);

        (await u.Http.DeleteAsync($"/api/v1/categories/{own.Id}")).EnsureSuccessStatusCode();
        var moved = await u.GetAsync<BudgetEntry>($"/api/v1/entries/{entry.Id}");
        Assert.Equal(pull.Categories.Single(c => c.DefaultKey == "other").Id, moved.CategoryId);
        Assert.Equal(HttpStatusCode.BadRequest, (await u.Http.DeleteAsync($"/api/v1/categories/{parent.Id}")).StatusCode);
    }

    [Fact(DisplayName = "F-131: Abhängige Änderungen in einem Sync-Paket (Polster + Sparplan, Schuld + Rate) werden in Reihenfolge angenommen")]
    public async Task DependentOperationsInOneBatch()
    {
        var u = await _app.LoginAsync("batch@example.com");
        var pull = await u.PullAsync();
        var buffer = new BufferEntry { Id = Guid.NewGuid(), Name = "Notgroschen", TargetAmount = 5000 };
        var plan = NewEntry(pull, "Notgroschen", 200, EntryKind.Allocation, "saving");
        plan.BufferId = buffer.Id;
        var debt = new DebtEntry { Id = Guid.NewGuid(), Name = "Autokredit", OpeningBalance = 5000, OriginalAmount = 5000, StartDate = DateOnly.FromDateTime(DateTime.UtcNow), MonthlyRate = 200 };
        var rate = NewEntry(pull, "Rate Autokredit", 200, cat: "mobility");
        rate.DebtId = debt.Id;
        rate.DebtRole = DebtRole.Rate;

        var r = await u.PushAsync(UserClient.Upsert(buffer), UserClient.Upsert(plan), UserClient.Upsert(debt), UserClient.Upsert(rate));
        Assert.All(r.Results, x => Assert.Equal(SyncOpStatus.Applied, x.Status));
        var after = await u.PullAsync();
        Assert.Equal(buffer.Id, after.Entries.Single(e => e.Id == plan.Id).BufferId);
        var savedRate = after.Entries.Single(e => e.Id == rate.Id);
        Assert.Equal(debt.Id, savedRate.DebtId);
        Assert.Equal(DebtRole.Rate, savedRate.DebtRole);
    }

    [Fact(DisplayName = "F-042: Entnahme max. bis zum aktuellen Stand, Grund ist Pflicht")]
    public async Task Withdrawal()
    {
        var u = await _app.LoginAsync("buffer@example.com");
        var buffer = new BufferEntry { Id = Guid.NewGuid(), Name = "Urlaub", TargetAmount = 1000 };
        (await u.PostAsync("/api/v1/buffers", buffer)).EnsureSuccessStatusCode();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await u.PostAsync("/api/v1/buffer-transactions", new BufferTransaction { BufferId = buffer.Id, Date = today, Amount = 250, Type = BufferTransactionType.Allocation })).EnsureSuccessStatusCode();
        var status = await u.GetAsync<JsonElement>($"/api/v1/buffers/{buffer.Id}/status");
        Assert.Equal(25m, status.GetProperty("progressPercent").GetDecimal());
        var tooMuch = await u.PostAsync("/api/v1/buffer-transactions", new BufferTransaction { BufferId = buffer.Id, Date = today, Amount = 300, Type = BufferTransactionType.Withdrawal, Reason = "Flug" });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        var noReason = await u.PostAsync("/api/v1/buffer-transactions", new BufferTransaction { BufferId = buffer.Id, Date = today, Amount = 50, Type = BufferTransactionType.Withdrawal });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        (await u.PostAsync("/api/v1/buffer-transactions", new BufferTransaction { BufferId = buffer.Id, Date = today, Amount = 250, Type = BufferTransactionType.Withdrawal, Reason = "Flug" })).EnsureSuccessStatusCode();
    }

    [Fact(DisplayName = "I-01: Monat abschließen friert ein, Ist-Werte im abgeschlossenen Monat abgelehnt, Wiedereröffnen möglich")]
    public async Task CloseMonth()
    {
        var u = await _app.LoginAsync("close@example.com");
        var pull = await u.PullAsync();
        var salary = NewEntry(pull, "Gehalt", 3000, EntryKind.Income);
        await u.PushAsync(UserClient.Upsert(salary));
        var food = NewEntry(pull, "Essen", 500);
        await u.PushAsync(UserClient.Upsert(food));
        var period = Current.AddMonths(-1);
        (await u.PostAsync($"/api/v1/months/{period}/close", new CloseMonthRequest("carry", null, 1234))).EnsureSuccessStatusCode();
        var month = await u.GetAsync<JsonElement>($"/api/v1/months/{period}");
        Assert.True(month.GetProperty("isClosed").GetBoolean());
        var next = await u.GetAsync<JsonElement>($"/api/v1/months/{Current}");
        Assert.Equal(2500m, next.GetProperty("carryIn").GetDecimal());

        var r = await u.PushAsync(UserClient.Upsert(new BudgetEntryMonth { Id = DeterministicId.ForEntryMonth(food.Id, period), EntryId = food.Id, Period = period, ActualAmount = 600 }));
        Assert.Equal(SyncOpStatus.Rejected, r.Results[0].Status);
        Assert.Equal("err.month.closed", r.Results[0].ErrorKey);

        (await u.PostAsync<object?>($"/api/v1/months/{period}/reopen", null)).EnsureSuccessStatusCode();
        Assert.False((await u.GetAsync<JsonElement>($"/api/v1/months/{period}")).GetProperty("isClosed").GetBoolean());
    }

    [Fact(DisplayName = "F-110: Export Excel/CSV für 12 Monate in < 10 Sekunden, vollständig")]
    public async Task Export()
    {
        var u = await _app.LoginAsync("export@example.com");
        (await u.Http.PostAsync("/api/v1/demo", null)).EnsureSuccessStatusCode();
        var from = Current.AddMonths(-11);
        var sw = Stopwatch.StartNew();
        var xlsx = await u.Http.GetAsync($"/api/v1/export?from={from}&to={Current}&format=xlsx");
        sw.Stop();
        xlsx.EnsureSuccessStatusCode();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        var bytes = await xlsx.Content.ReadAsByteArrayAsync();
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        Assert.Contains(wb.Worksheets, w => w.Name == "Monatsübersicht");
        Assert.Equal(13, wb.Worksheet("Monatsübersicht").RowsUsed().Count());
        var csv = await u.Http.GetAsync($"/api/v1/export?from={from}&to={Current}&format=csv");
        csv.EnsureSuccessStatusCode();
        using var zip = new System.IO.Compression.ZipArchive(await csv.Content.ReadAsStreamAsync());
        Assert.True(zip.Entries.Count >= 8);
    }

    [Fact(DisplayName = "F-133: Import mit Spaltenzuordnung; fehlerhafte Zeilen markiert und überspringbar")]
    public async Task Import()
    {
        var u = await _app.LoginAsync("import@example.com");
        var csv = "Bezeichnung;Betrag;Typ;Kategorie;Rhythmus;Abbuchungstag\nMiete;950,00;Ausgabe;Wohnen;monatlich;3\nGehalt;3.100,50;Einnahme;Gehalt;monatlich;1\nKaputt;abc;Ausgabe;Wohnen;monatlich;40\nHaustier;35;Ausgabe;Tiere;monatlich;\n";
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "budget.csv");
        var previewRes = await u.Http.PostAsync("/api/v1/import/preview", form);
        previewRes.EnsureSuccessStatusCode();
        var preview = (await previewRes.Content.ReadFromJsonAsync<ImportPreviewResponse>(JsonDefaults.Options))!;
        Assert.Equal(4, preview.RowCount);
        Assert.Equal("entries", preview.SuggestedTarget);
        Assert.Equal(0, preview.SuggestedMapping["name"]);
        Assert.Equal(1, preview.SuggestedMapping["amount"]);

        var mapping = new ImportMappingRequest(preview.ImportId, "entries", preview.SuggestedMapping.ToDictionary(kv => kv.Key, kv => kv.Value));
        var validation = await u.PostReadAsync<ImportMappingRequest, ImportValidationResponse>("/api/v1/import/validate", mapping);
        Assert.Equal(3, validation.ValidCount);
        var bad = validation.Rows.Single(r => !r.Valid);
        Assert.Equal(4, bad.RowNumber);

        var commit = await u.PostReadAsync<ImportCommitRequest, ImportCommitResponse>("/api/v1/import/commit", new ImportCommitRequest(mapping, [bad.RowNumber]));
        Assert.Equal(3, commit.Imported);
        var data = await u.PullAsync();
        Assert.Contains(data.Entries, e => e.Name == "Gehalt" && e.Amount == 3100.50m && e.Kind == EntryKind.Income);
        Assert.Contains(data.Categories, c => c.Name == "Tiere");
    }

    [Fact(DisplayName = "F-142: Demodaten befüllen alle Module und sind mit einem Klick löschbar")]
    public async Task Demo()
    {
        var u = await _app.LoginAsync("demo@example.com");
        (await u.Http.PostAsync("/api/v1/demo", null)).EnsureSuccessStatusCode();
        var data = await u.PullAsync();
        Assert.NotEmpty(data.Entries);
        Assert.NotEmpty(data.Debts);
        Assert.NotEmpty(data.Buffers);
        Assert.NotEmpty(data.Snapshots);
        Assert.NotEmpty(data.Checkpoints);
        Assert.True((await u.GetAsync<DemoStatusDto>("/api/v1/demo")).HasDemoData);
        (await u.Http.DeleteAsync("/api/v1/demo")).EnsureSuccessStatusCode();
        var cleared = await u.PullAsync();
        Assert.Empty(cleared.Entries);
        Assert.Empty(cleared.Debts);
        Assert.False((await u.GetAsync<DemoStatusDto>("/api/v1/demo")).HasDemoData);
    }

    [Fact(DisplayName = "F-080: Monatsstart-Erinnerung genau einmal am Stichtag, Link ins Budget, keine bei Deaktivierung")]
    public async Task MonthStartReminder()
    {
        var u = await _app.LoginAsync("reminder@example.com");
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        TimeSpan OffsetTo(DateTime localTarget) =>
            TimeZoneInfo.ConvertTimeToUtc(localTarget, berlin) - DateTime.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, berlin);
        var nextStart = new DateTime(nowLocal.Year, nowLocal.Month, 1, 9, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
        try
        {
            // Stichtag (1. des Folgemonats, 9 Uhr): zweimal auswerten → genau eine Erinnerung
            _app.Clock.Offset = OffsetTo(nextStart);
            await RunRemindersAsync();
            await RunRemindersAsync();
            var list = await u.GetAsync<List<NotificationDto>>("/api/v1/notifications");
            var reminders = list.Where(n => n.Type == NotificationType.MonthStart).ToList();
            Assert.Single(reminders);
            Assert.StartsWith("/budget", reminders[0].Link);

            // Am Folgetag keine weitere Erinnerung
            _app.Clock.Offset = OffsetTo(nextStart.AddDays(1));
            await RunRemindersAsync();
            Assert.Single(await u.GetAsync<List<NotificationDto>>("/api/v1/notifications"), n => n.Type == NotificationType.MonthStart);

            // Deaktiviert (alle Kanäle) → am nächsten Stichtag keine Erinnerung
            var off = Enum.GetValues<NotificationChannel>().Select(c => new NotificationSettingDto(NotificationType.MonthStart, c, false)).ToList();
            (await u.PutAsync("/api/v1/me/notification-settings", off)).EnsureSuccessStatusCode();
            _app.Clock.Offset = OffsetTo(nextStart.AddMonths(1));
            await RunRemindersAsync();
            Assert.Single(await u.GetAsync<List<NotificationDto>>("/api/v1/notifications"), n => n.Type == NotificationType.MonthStart);
        }
        finally
        {
            _app.Clock.Offset = TimeSpan.Zero;
        }

        async Task RunRemindersAsync()
        {
            using var scope = _app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BudgetPlaner.Application.Services.NotificationEvaluator>().MonthStartRemindersAsync();
        }
    }

    [Fact(DisplayName = "F-081: Zielerreichung löst Benachrichtigung binnen 5 Minuten aus")]
    public async Task GoalNotification()
    {
        var u = await _app.LoginAsync("goal@example.com");
        var buffer = new BufferEntry { Id = Guid.NewGuid(), Name = "Fahrrad", TargetAmount = 500 };
        (await u.PostAsync("/api/v1/buffers", buffer)).EnsureSuccessStatusCode();
        (await u.PostAsync("/api/v1/buffer-transactions", new BufferTransaction
        {
            BufferId = buffer.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow), Amount = 500, Type = BufferTransactionType.Allocation,
        })).EnsureSuccessStatusCode();
        var sw = Stopwatch.StartNew();
        List<NotificationDto> list = [];
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            list = await u.GetAsync<List<NotificationDto>>("/api/v1/notifications");
            if (list.Any(n => n.Type == NotificationType.BufferGoalReached)) break;
            await Task.Delay(250);
        }
        Assert.Contains(list, n => n.Type == NotificationType.BufferGoalReached && n.Title.Contains("Fahrrad"));
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(5));
    }

    [Fact(DisplayName = "F-081: Benachrichtigungsarten einzeln deaktivierbar")]
    public async Task NotificationSettings()
    {
        var u = await _app.LoginAsync("settings-n@example.com");
        var settings = await u.GetAsync<List<NotificationSettingDto>>("/api/v1/me/notification-settings");
        Assert.Equal(Enum.GetValues<NotificationType>().Length * 3, settings.Count);
        var off = settings.Select(s => s with { Active = false }).ToList();
        var res = await u.Http.PutAsJsonAsync("/api/v1/me/notification-settings", off, JsonDefaults.Options);
        res.EnsureSuccessStatusCode();
        var buffer = new BufferEntry { Id = Guid.NewGuid(), Name = "Leise", TargetAmount = 10 };
        (await u.PostAsync("/api/v1/buffers", buffer)).EnsureSuccessStatusCode();
        (await u.PostAsync("/api/v1/buffer-transactions", new BufferTransaction
        {
            BufferId = buffer.Id, Date = DateOnly.FromDateTime(DateTime.UtcNow), Amount = 10, Type = BufferTransactionType.Allocation,
        })).EnsureSuccessStatusCode();
        await Task.Delay(4000);
        Assert.Empty(await u.GetAsync<List<NotificationDto>>("/api/v1/notifications"));
    }

    [Fact(DisplayName = "REST: ETag/If-Match verhindert Überschreiben, PATCH ändert Teilfelder")]
    public async Task ETagAndPatch()
    {
        var u = await _app.LoginAsync("etag@example.com");
        var pull = await u.PullAsync();
        var entry = NewEntry(pull, "Handy", 20);
        (await u.PostAsync("/api/v1/entries", entry)).EnsureSuccessStatusCode();
        var get = await u.Http.GetAsync($"/api/v1/entries/{entry.Id}");
        var etag = get.Headers.ETag!.Tag;
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/entries/{entry.Id}") { Content = JsonContent.Create(new { amount = 25 }) };
        patch.Headers.TryAddWithoutValidation("If-Match", $"W/{etag}");
        Assert.Equal(HttpStatusCode.OK, (await u.Http.SendAsync(patch)).StatusCode);
        var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/entries/{entry.Id}") { Content = JsonContent.Create(new { amount = 30 }) };
        stale.Headers.TryAddWithoutValidation("If-Match", $"W/{etag}");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await u.Http.SendAsync(stale)).StatusCode);
        Assert.Equal(25, (await u.GetAsync<BudgetEntry>($"/api/v1/entries/{entry.Id}")).Amount);
    }

    [Fact(DisplayName = "Statistik-/Prognose-Endpunkte liefern Ergebnisse (F-060–F-075)")]
    public async Task AnalysisEndpoints()
    {
        var u = await _app.LoginAsync("analysis@example.com");
        (await u.Http.PostAsync("/api/v1/demo", null)).EnsureSuccessStatusCode();
        var p = Current;
        string[] urls =
        [
            $"/api/v1/statistics/categories?from={p}&to={p}", $"/api/v1/statistics/series?end={p}&months=12",
            $"/api/v1/statistics/fifty-thirty-twenty?from={p.AddMonths(-5)}&to={p}", $"/api/v1/statistics/savings-rate?end={p}&months=12",
            $"/api/v1/statistics/top?from={p}&to={p}", $"/api/v1/statistics/year/{p.Year}", $"/api/v1/statistics/plan-actual?from={p.AddMonths(-1)}&to={p.AddMonths(-1)}",
            $"/api/v1/statistics/fixed-costs/{p}", $"/api/v1/statistics/net-worth?end={p}&months=12", "/api/v1/forecasts/balance?months=12",
            "/api/v1/forecasts/debt-free", "/api/v1/forecasts/savings-goals", "/api/v1/forecasts/warnings", $"/api/v1/forecasts/year-end/{p.Year}",
            $"/api/v1/cashflow/{p}", $"/api/v1/ampel/{p}", $"/api/v1/dashboard/{p}",
        ];
        foreach (var url in urls) Assert.True((await u.Http.GetAsync(url)).IsSuccessStatusCode, url);

        var data = await u.PullAsync();
        var salary = data.Entries.First(e => e.Kind == EntryKind.Income);
        var scenario = new { name = "Gehalt +10 %", changes = new object[] { new Dictionary<string, object> { ["$type"] = "scaleIncome", ["percent"] = 10, ["from"] = p.ToString() } } };
        var res = await u.Http.PostAsJsonAsync("/api/v1/forecasts/scenario", scenario);
        res.EnsureSuccessStatusCode();
        var cmp = await res.Content.ReadFromJsonAsync<JsonElement>();
        var first = cmp.GetProperty("months")[1];
        Assert.True(first.GetProperty("scenarioUnverplant").GetDecimal() > first.GetProperty("baselineUnverplant").GetDecimal());
        Assert.Equal(salary.Amount, (await u.GetAsync<BudgetEntry>($"/api/v1/entries/{salary.Id}")).Amount);
    }
}
