using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Scenarios;
using BudgetPlaner.Server.Auth;
using BudgetPlaner.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BudgetPlaner.Server.Endpoints;

public sealed record BufferSummary(Guid Id, string Name, decimal Balance, decimal? Target, decimal? ProgressPercent, bool GoalReached);

public sealed record DashboardDto(
    Period Period,
    MonthTotals Month,
    AmpelReport Ampel,
    IReadOnlyList<Warning> Warnings,
    decimal DebtTotal,
    decimal DebtProgressPercent,
    Period? DebtFreePeriod,
    decimal BufferTotal,
    IReadOnlyList<BufferSummary> Buffers,
    bool HasBalanceAnchor,
    decimal BalanceToday,
    decimal MinBalanceThisPeriod,
    FiftyThirtyTwentyResult FiftyThirtyTwenty,
    FixedCostResult FixedCosts,
    decimal? SavingsRate);

public sealed record MonthComparison(Period Period, decimal BaselineUnverplant, decimal ScenarioUnverplant, decimal BaselineEndBalance, decimal ScenarioEndBalance);

public sealed record ScenarioComparison(
    IReadOnlyList<MonthComparison> Months,
    DebtFreeSummary BaselineDebtFree,
    DebtFreeSummary ScenarioDebtFree,
    YearEndForecast BaselineYearEnd,
    YearEndForecast ScenarioYearEnd,
    IReadOnlyList<BufferForecast> BaselineGoals,
    IReadOnlyList<BufferForecast> ScenarioGoals);

/// <summary>Monats-, Statistik-, Prognose- und Szenario-Endpunkte (F-050–F-075) – dieselbe Rechenlogik wie in der App.</summary>
public static class AnalysisEndpoints
{
    private static async Task<HouseholdEngine> EngineAsync(RequestUser u, HouseholdDataLoader loader, SyncService sync, Application.Abstractions.IClock clock, CancellationToken ct)
    {
        var data = await loader.LoadForReadAsync(u.Context.HouseholdId, ct);
        var today = clock.TodayIn(await sync.OwnerTimeZoneAsync(u.Context.HouseholdId, ct));
        return new HouseholdEngine(data, today);
    }

    private static Period P(string value) => Period.TryParse(value, out var p) ? p : throw AppException.BadRequest("err.validation");

    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").Authed();

        // ---------- Monat & Abschluss ----------
        var months = g.MapGroup("/months").WithTags("Budgetmonat");
        months.MapGet("/{period}", async (string period, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Month(P(period))))
            .WithSummary("Budgetmonat mit Positionen, Unverplant (F-014) und Überschuss (F-050). Periode im Format JJJJ-MM.");
        months.MapPost("/{period}/close", async (string period, CloseMonthRequest body, RequestUser u, MonthService m, CancellationToken ct) =>
        {
            await m.CloseAsync(u.Context, P(period), body, ct);
            return Results.NoContent();
        }).WithSummary("Monat abschließen: Rest übertragen („carry“) oder einem Polster zuweisen („buffer“), optional Kontostand erfassen.");
        months.MapPost("/{period}/reopen", async (string period, RequestUser u, MonthService m, CancellationToken ct) =>
        {
            await m.ReopenAsync(u.Context, P(period), ct);
            return Results.NoContent();
        }).WithSummary("Abgeschlossenen Monat wieder öffnen.");

        g.MapGet("/dashboard/{period}", async (string period, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
        {
            var e = await EngineAsync(u, l, s, c, ct);
            var p = P(period);
            var cf = e.Cashflow.ForPeriod(p);
            return Results.Ok(new DashboardDto(p, e.Totals(p), e.Ampel.Evaluate(p), e.Forecasts.Warnings(), e.Debts.TotalBalance, e.Debts.TotalProgressPercent,
                e.Debts.Combined().PayoffPeriod, e.Buffers.TotalBalance,
                e.Buffers.All().Select(b => new BufferSummary(b.Buffer.Id, b.Buffer.Name, b.Balance, b.Target, b.ProgressPercent, b.GoalReached)).ToList(),
                cf.HasAnchor, e.Cashflow.BalanceToday, cf.MinBalance, e.Statistics.FiftyThirtyTwenty(p, p), e.Statistics.FixedCostRatio(p), e.Statistics.RateOf(p)));
        }).WithTags("Übersicht").WithSummary("Monats-Dashboard mit Kennzahlen und Ampel (F-050, F-051).");

        g.MapGet("/ampel/{period}", async (string period, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Ampel.Evaluate(P(period)))).WithTags("Übersicht").WithSummary("Ampel je Bereich inkl. Rechenweg (F-051).");

        g.MapGet("/cashflow/{period}", async (string period, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Cashflow.ForPeriod(P(period))))
            .WithTags("Cashflow").WithSummary("Kalender mit tagesgenauen Zu-/Abflüssen und laufendem Kontostand (F-030–F-032).");

        g.MapGet("/debts/{id:guid}/status", async (Guid id, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
        {
            var e = await EngineAsync(u, l, s, c, ct);
            if (!e.Index.DebtById.ContainsKey(id)) return Results.NotFound();
            return Results.Ok(new { status = e.Debts.Status(id), forecast = e.Debts.Forecast(id) });
        }).WithTags("Schulden (F-020)").WithSummary("Restschuld, Fortschritt, Zahlungen mit Zins/Tilgung, Tilgungsplan und Prognose (F-021, F-022, F-071).");

        g.MapGet("/buffers/{id:guid}/status", async (Guid id, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
        {
            var e = await EngineAsync(u, l, s, c, ct);
            return e.Index.BufferById.ContainsKey(id) ? Results.Ok(e.Buffers.Status(id)) : Results.NotFound();
        }).WithTags("Polster (F-040)").WithSummary("Stand, Fortschritt, Historie und Sparziel-Prognose (F-041–F-043, F-072).");

        // ---------- Statistik ----------
        var st = g.MapGroup("/statistics").WithTags("Statistik");
        st.MapGet("/categories", async ([FromQuery] string from, [FromQuery] string to, [FromQuery] bool? byRoot, [FromQuery] bool? smoothed,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.ExpensesByCategory(P(from), P(to), byRoot ?? true, smoothed ?? false)))
            .WithSummary("Ausgaben nach Kategorie (F-060).");
        st.MapGet("/series", async ([FromQuery] string end, [FromQuery] int? months, [FromQuery] bool? smoothed,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.IncomeExpenseSeries(P(end), Math.Clamp(months ?? 12, 1, 60), smoothed ?? false)))
            .WithSummary("Einnahmen-/Ausgaben-Verlauf über 3/6/12/24 Monate, Lücken als null (F-061).");
        st.MapGet("/fifty-thirty-twenty", async ([FromQuery] string from, [FromQuery] string to, [FromQuery] bool? smoothed,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.FiftyThirtyTwenty(P(from), P(to), smoothed ?? false)))
            .WithSummary("50/30/20-Analyse Ist vs. Soll (F-062).");
        st.MapGet("/savings-rate", async ([FromQuery] string end, [FromQuery] int? months,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.SavingsRate(P(end), Math.Clamp(months ?? 12, 1, 60))))
            .WithSummary("Sparquote mit 3-Monats-Durchschnitt (F-063).");
        st.MapGet("/top", async ([FromQuery] string from, [FromQuery] string to, [FromQuery] int? take,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.TopExpenses(P(from), P(to), take ?? 10)))
            .WithSummary("Top-Ausgabenpositionen (F-064).");
        st.MapGet("/year/{year:int}", async (int year, [FromQuery] bool? smoothed, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.YearOverview(year, smoothed ?? false)))
            .WithSummary("Jahresübersicht mit Vorjahresvergleich (F-065).");
        st.MapGet("/plan-actual", async ([FromQuery] string from, [FromQuery] string to,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.PlanVsActual(P(from), P(to))))
            .WithSummary("Soll/Ist-Abweichung je Kategorie (F-066).");
        st.MapGet("/fixed-costs/{period}", async (string period, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.FixedCostRatio(P(period))))
            .WithSummary("Fixkostenquote (F-067).");
        st.MapGet("/net-worth", async ([FromQuery] string end, [FromQuery] int? months,
                RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Statistics.NetWorth(P(end), Math.Clamp(months ?? 12, 1, 120))))
            .WithSummary("Vermögensverlauf: Polster minus Restschulden (F-068).");

        // ---------- Prognosen ----------
        var fc = g.MapGroup("/forecasts").WithTags("Prognosen");
        fc.MapGet("/balance", async ([FromQuery] int? months, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Forecasts.Balance(months ?? 12))).WithSummary("Kontostand-Prognose 1–12 Monate (F-070).");
        fc.MapGet("/debt-free", async (RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Forecasts.DebtFree())).WithSummary("Schuldenfreiheits-Prognose inkl. Schneeball/Lawine (F-071).");
        fc.MapGet("/savings-goals", async (RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Buffers.All().Select(b => new { bufferId = b.Buffer.Id, b.Buffer.Name, b.Balance, b.Target, b.Forecast })))
            .WithSummary("Sparziel-Prognosen (F-072).");
        fc.MapGet("/warnings", async (RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Forecasts.Warnings())).WithSummary("Budget-Frühwarnung und Warnhinweise (F-073).");
        fc.MapGet("/year-end/{year:int}", async (int year, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
            Results.Ok((await EngineAsync(u, l, s, c, ct)).Forecasts.YearEnd(year))).WithSummary("Jahresend-Prognose (F-074).");
        fc.MapPost("/scenario", async (Scenario scenario, RequestUser u, HouseholdDataLoader l, SyncService s, Application.Abstractions.IClock c, CancellationToken ct) =>
        {
            var baseline = await EngineAsync(u, l, s, c, ct);
            var copy = ScenarioApplier.Apply(baseline.Data, scenario, c.UtcNow);
            var sim = new HouseholdEngine(copy, baseline.Today);
            var b = baseline.Forecasts.Balance(12);
            var sb = sim.Forecasts.Balance(12);
            var months = b.Months.Zip(sb.Months, (x, y) => new MonthComparison(x.Period, baseline.Month(x.Period).Unverplant, sim.Month(y.Period).Unverplant, x.EndBalance, y.EndBalance)).ToList();
            var year = baseline.CurrentPeriod.Year;
            return Results.Ok(new ScenarioComparison(months, baseline.Forecasts.DebtFree(), sim.Forecasts.DebtFree(), baseline.Forecasts.YearEnd(year), sim.Forecasts.YearEnd(year),
                baseline.Buffers.All().Select(x => x.Forecast).ToList(), sim.Buffers.All().Select(x => x.Forecast).ToList()));
        }).WithSummary("„Was-wäre-wenn“: wirkt nur in der Simulation, Originaldaten bleiben unverändert (F-075).");
    }
}
