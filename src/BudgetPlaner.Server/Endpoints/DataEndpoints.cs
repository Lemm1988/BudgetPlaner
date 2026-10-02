using System.Text.Json;
using System.Text.Json.Nodes;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BudgetPlaner.Server.Endpoints;

/// <summary>
/// REST-Ressourcen für alle Haushaltsdaten (F-130). Schreibzugriffe laufen über denselben Pfad wie die App-Synchronisation;
/// optimistische Nebenläufigkeit über ETag/If-Match (= Version).
/// </summary>
public static class DataEndpoints
{
    public static void MapDataEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").Authed();
        MapCrud<Category>(g, "categories", SyncTypes.Category, d => d.Categories, "Kategorien (F-012)", null);
        MapCrud<BudgetEntry>(g, "entries", SyncTypes.Entry, d => d.Entries, "Budgetpositionen (F-010, F-011, F-013)",
            (e, q) => Match(q, "categoryId", e.CategoryId) && Match(q, "debtId", e.DebtId) && Match(q, "bufferId", e.BufferId) && MatchEnum(q, "kind", e.Kind));
        MapCrud<BudgetEntryChange>(g, "entry-changes", SyncTypes.EntryChange, d => d.EntryChanges, "Betragsänderungen „ab Monat“ (B-03)",
            (e, q) => Match(q, "entryId", e.EntryId));
        MapCrud<BudgetEntryMonth>(g, "entry-months", SyncTypes.EntryMonth, d => d.EntryMonths, "Monatswerte: Ist, abgehakt, Abweichung (D-02)",
            (e, q) => Match(q, "entryId", e.EntryId) && (q["period"].Count == 0 || e.Period.ToString() == q["period"]));
        MapCrud<DebtEntry>(g, "debts", SyncTypes.Debt, d => d.Debts, "Schulden (F-020)", null);
        MapCrud<DebtPayment>(g, "debt-payments", SyncTypes.DebtPayment, d => d.DebtPayments, "Tilgungen (F-021)",
            (e, q) => Match(q, "debtId", e.DebtId));
        MapCrud<BufferEntry>(g, "buffers", SyncTypes.Buffer, d => d.Buffers, "Polster (F-040)", null);
        MapCrud<BufferTransaction>(g, "buffer-transactions", SyncTypes.BufferTransaction, d => d.BufferTransactions, "Polster-Zuweisungen/-Entnahmen (F-040, F-042)",
            (e, q) => Match(q, "bufferId", e.BufferId));
        MapCrud<BalanceCheckpoint>(g, "checkpoints", SyncTypes.Checkpoint, d => d.Checkpoints, "Kontostand-Abgleich (I-03)", null);

        g.MapGet("/snapshots", async (RequestUser u, HouseholdDataLoader loader, CancellationToken ct) =>
            Results.Ok((await loader.LoadForReadAsync(u.Context.HouseholdId, ct)).Snapshots.OrderBy(s => s.Period.Index)))
            .WithTags("Monatsabschluss").WithSummary("Abgeschlossene Monate (MonthSnapshot).");
    }

    private static bool Match(IQueryCollection q, string key, Guid? value) =>
        q[key].Count == 0 || (Guid.TryParse(q[key], out var g) && value == g);

    private static bool MatchEnum<TEnum>(IQueryCollection q, string key, TEnum value) where TEnum : struct, Enum =>
        q[key].Count == 0 || (Enum.TryParse<TEnum>(q[key], true, out var v) && v.Equals(value));

    private static void MapCrud<T>(RouteGroupBuilder root, string route, string type, Func<HouseholdData, IEnumerable<T>> select, string tag,
        Func<T, IQueryCollection, bool>? filter) where T : HouseholdEntity
    {
        var g = root.MapGroup("/" + route).WithTags(tag);

        g.MapGet("/", async (HttpRequest req, RequestUser u, HouseholdDataLoader loader, CancellationToken ct) =>
        {
            var data = await loader.LoadForReadAsync(u.Context.HouseholdId, ct);
            var items = select(data).Where(e => !e.IsDeleted);
            if (filter is not null) items = items.Where(e => filter(e, req.Query));
            return Results.Ok(items.ToList());
        }).WithSummary("Alle Einträge (optional gefiltert).");

        g.MapGet("/{id:guid}", async (Guid id, HttpResponse res, RequestUser u, HouseholdDataLoader loader, CancellationToken ct) =>
        {
            var data = await loader.LoadForReadAsync(u.Context.HouseholdId, ct);
            var entity = select(data).FirstOrDefault(e => e.Id == id && !e.IsDeleted);
            if (entity is null) return Results.NotFound();
            res.Headers.ETag = $"W/\"{entity.Version}\"";
            return Results.Ok(entity);
        }).WithSummary("Einzelner Eintrag inkl. ETag (Version).");

        g.MapPost("/", async (T body, RequestUser u, SyncService sync, HttpResponse res, CancellationToken ct) =>
        {
            if (body.Id == Guid.Empty) body.Id = Guid.NewGuid();
            var result = await sync.ApplyAsync(u.Context, u.Context.HouseholdId, [EntityMutation.Upsert(body)], ct);
            var stored = Ensure(result, select, body.Id);
            res.Headers.ETag = $"W/\"{stored.Version}\"";
            return Results.Created($"/api/v1/{route}/{stored.Id}", stored);
        }).WithSummary("Anlegen.");

        g.MapPut("/{id:guid}", async (Guid id, T body, HttpRequest req, HttpResponse res, RequestUser u, SyncService sync, CancellationToken ct) =>
        {
            body.Id = id;
            var result = await sync.ApplyAsync(u.Context, u.Context.HouseholdId, [EntityMutation.Upsert(body, IfMatch(req))], ct);
            var stored = Ensure(result, select, id);
            res.Headers.ETag = $"W/\"{stored.Version}\"";
            return Results.Ok(stored);
        }).WithSummary("Vollständig ersetzen (If-Match optional).");

        g.MapPatch("/{id:guid}", async (Guid id, JsonObject patch, HttpRequest req, HttpResponse res, RequestUser u, HouseholdDataLoader loader,
            SyncService sync, CancellationToken ct) =>
        {
            var data = await loader.LoadForReadAsync(u.Context.HouseholdId, ct);
            var current = select(data).FirstOrDefault(e => e.Id == id && !e.IsDeleted) ?? throw AppException.NotFound();
            var node = JsonSerializer.SerializeToNode(current, JsonDefaults.Options)!.AsObject();
            MergePatch(node, patch);
            var updated = node.Deserialize<T>(JsonDefaults.Options)!;
            updated.Id = id;
            var result = await sync.ApplyAsync(u.Context, u.Context.HouseholdId, [EntityMutation.Upsert(updated, IfMatch(req))], ct);
            var stored = Ensure(result, select, id);
            res.Headers.ETag = $"W/\"{stored.Version}\"";
            return Results.Ok(stored);
        }).WithSummary("Teilweise ändern (JSON Merge Patch, RFC 7396).");

        g.MapDelete("/{id:guid}", async (Guid id, HttpRequest req, RequestUser u, SyncService sync, CancellationToken ct) =>
        {
            var result = await sync.ApplyAsync(u.Context, u.Context.HouseholdId, [EntityMutation.Remove(type, id, IfMatch(req))], ct);
            var r = result.Results[0];
            if (r.Status == SyncOpStatus.Rejected) throw AppException.BadRequest(r.ErrorKey ?? "err.validation");
            return r.Status == SyncOpStatus.NoChange ? Results.NotFound() : Results.NoContent();
        }).WithSummary("Löschen (Positionen: aus allen Berechnungen, F-010).");
    }

    private static T Ensure<T>(ApplyResult result, Func<HouseholdData, IEnumerable<T>> select, Guid requestedId) where T : HouseholdEntity
    {
        var r = result.Results[0];
        if (r.Status == SyncOpStatus.Rejected) throw AppException.BadRequest(r.ErrorKey ?? "err.validation");
        var session = result.Session;
        return select(session.Data).FirstOrDefault(e => e.Id == requestedId && !e.IsDeleted)
               ?? session.Added.Concat(session.Modified).OfType<T>().FirstOrDefault(e => !e.IsDeleted)
               ?? throw AppException.NotFound();
    }

    private static long? IfMatch(HttpRequest req)
    {
        var raw = req.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Replace("W/", "").Trim('"', ' ');
        return long.TryParse(cleaned, out var v) ? v : throw AppException.BadRequest("err.validation");
    }

    private static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            var targetKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject childPatch && target[targetKey] is JsonObject childTarget) MergePatch(childTarget, childPatch);
            else target[targetKey] = value?.DeepClone();
        }
    }
}
