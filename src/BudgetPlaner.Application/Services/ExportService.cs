using System.Text.Json;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared;
using BudgetPlaner.Shared.Localization;

namespace BudgetPlaner.Application.Services;

/// <summary>Datenexport CSV/Excel/JSON für einen Zeitraum (F-110).</summary>
public sealed class ExportService(HouseholdDataLoader loader, ISpreadsheetIO io, IClock clock, SyncService sync)
{
    public async Task<(byte[] Content, string ContentType, string FileName)> ExportAsync(UserContext ctx, Period from, Period to, string format,
        CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);
        if (Period.MonthsBetween(from, to) > 240) throw AppException.BadRequest("err.validation");
        var data = await loader.LoadForReadAsync(ctx.HouseholdId, ct);
        var today = clock.TodayIn(await sync.OwnerTimeZoneAsync(ctx.HouseholdId, ct));
        var engine = new HouseholdEngine(data, today);
        var name = $"budgetplaner-{from}_{to}";
        switch ((format ?? "xlsx").ToLowerInvariant())
        {
            case "csv":
                return (io.WriteCsvZip(Build(engine, from, to, ctx.Language, clock.UtcNow), ctx.Language), "application/zip", name + "-csv.zip");
            case "json":
                var json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    exportedAt = clock.UtcNow,
                    range = new { from, to },
                    household = data,
                    months = Period.Range(from, to).Select(p => engine.Month(p)).Select(m => new
                    {
                        m.Period, m.Start, m.End, m.IsClosed, m.CarryIn, m.Income, m.Expenses, m.Allocations, m.Withdrawals, m.Surplus, m.Unverplant,
                        lines = m.Lines.Select(l => new { l.EntryId, l.Name, l.Kind, l.CategoryId, l.Group, l.Planned, l.Actual, l.Effective, l.IsDone, l.DueDate }),
                    }),
                }, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true });
                return (json, "application/json", name + ".json");
            default:
                return (io.WriteXlsx(Build(engine, from, to, ctx.Language, clock.UtcNow)),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }
    }

    public static ExportWorkbook Build(HouseholdEngine engine, Period from, Period to, string lang, DateTimeOffset now)
    {
        string T(string key) => Texts.Get(lang, key);
        string Yes(bool b) => b ? T("col.yes") : T("col.no");
        var ix = engine.Index;
        var currency = engine.Data.Household.Currency;
        var wb = new ExportWorkbook();
        string Cat(Guid? id) => Labels.CategoryName(ix, id, lang);
        string Parent(Guid id) => ix.CategoryById.TryGetValue(id, out var c) && c.ParentId is { } p ? Cat(p) : "";
        var (rangeStart, _) = engine.Calendar.Range(from);
        var rangeEnd = engine.Calendar.End(to);

        var info = new ExportSheet(T("export.sheet.info"), [T("col.name"), T("col.note")]);
        info.Rows.Add([T("col.household"), engine.Data.Household.Name]);
        info.Rows.Add([T("col.currency"), currency]);
        info.Rows.Add([T("col.range"), $"{Fmt.PeriodName(from, lang)} – {Fmt.PeriodName(to, lang)}"]);
        info.Rows.Add([T("col.exportedAt"), Fmt.DateTime(now, lang)]);
        wb.Sheets.Add(info);

        var overview = new ExportSheet(T("export.sheet.overview"),
            [T("col.period"), T("col.periodStart"), T("col.periodEnd"), T("col.carryIn"), T("col.income"), T("col.expenses"), T("col.allocations"),
             T("col.withdrawals"), T("col.surplus"), T("col.unverplant"), T("col.closed")]);
        foreach (var c in new[] { 3, 4, 5, 6, 7, 8, 9 }) overview.MoneyColumns.Add(c);
        var lines = new ExportSheet(T("export.sheet.lines"),
            [T("col.period"), T("col.name"), T("col.kind"), T("col.category"), T("col.parentCategory"), T("col.group"), T("col.planned"),
             T("col.actual"), T("col.done"), T("col.dueDate"), T("col.fixed"), T("col.rhythm")]);
        lines.MoneyColumns.Add(6);
        lines.MoneyColumns.Add(7);
        foreach (var p in Period.Range(from, to))
        {
            var m = engine.Month(p);
            overview.Rows.Add([p.ToString(), m.Start, m.End, m.CarryIn, m.Income, m.Expenses, m.Allocations, m.Withdrawals, m.Surplus, m.Unverplant, Yes(m.IsClosed)]);
            foreach (var l in m.Lines)
                lines.Rows.Add([p.ToString(), l.Name, Labels.Of(l.Kind, lang), Cat(l.CategoryId), Parent(l.CategoryId), Labels.Of(l.Group, lang),
                    l.Planned, l.Effective, Yes(l.IsDone), l.DueDate, Yes(l.IsFixed), Labels.Of(l.Rhythm, lang)]);
        }
        wb.Sheets.Add(overview);
        wb.Sheets.Add(lines);

        var entries = new ExportSheet(T("export.sheet.entries"),
            [T("col.name"), T("col.kind"), T("col.category"), T("col.parentCategory"), T("col.amount"), T("col.rhythm"), T("col.debitDay"),
             T("col.fixed"), T("col.start"), T("col.end"), T("col.debt"), T("col.buffer"), T("col.note")]);
        entries.MoneyColumns.Add(4);
        foreach (var e in ix.Entries)
            entries.Rows.Add([e.Name, Labels.Of(e.Kind, lang), Cat(e.CategoryId), Parent(e.CategoryId), EntrySchedule.ScheduledAmount(ix, e, to).Amount,
                Labels.Of(e.Rhythm, lang), e.DebitDay, Yes(e.IsFixed), e.StartPeriod.ToString(), e.EndPeriod?.ToString(),
                e.DebtId is { } d && ix.DebtById.TryGetValue(d, out var debt) ? debt.Name : null,
                e.BufferId is { } b && ix.BufferById.TryGetValue(b, out var buf) ? buf.Name : null, e.Note]);
        wb.Sheets.Add(entries);

        var cats = new ExportSheet(T("export.sheet.categories"), [T("col.category"), T("col.parentCategory"), T("col.kind"), T("col.group")]);
        foreach (var c in ix.Categories)
            cats.Rows.Add([Cat(c.Id), c.ParentId is { } p ? Cat(p) : "", c.Type == CategoryType.Income ? Labels.Of(EntryKind.Income, lang) : Labels.Of(EntryKind.Expense, lang), Labels.Of(c.Group, lang)]);
        wb.Sheets.Add(cats);

        var debts = new ExportSheet(T("export.sheet.debts"),
            [T("col.name"), T("col.creditor"), T("col.type"), T("col.original"), T("col.balance"), T("col.interestRate"), T("col.monthlyRate"),
             T("col.debitDay"), T("col.start"), T("col.progress"), T("col.paidOff"), T("col.note")]);
        foreach (var c in new[] { 3, 4, 6 }) debts.MoneyColumns.Add(c);
        var payments = new ExportSheet(T("export.sheet.payments"),
            [T("col.debt"), T("col.date"), T("col.amount"), T("col.interest"), T("col.principal"), T("col.extra"), T("col.note")]);
        foreach (var c in new[] { 2, 3, 4 }) payments.MoneyColumns.Add(c);
        foreach (var d in ix.Debts)
        {
            var s = engine.Debts.Status(d.Id);
            debts.Rows.Add([d.Name, d.Creditor, Labels.Of(d.Type, lang), d.OriginalAmount, s.Balance, d.InterestRate, d.MonthlyRate, d.DebitDay, d.StartDate,
                Math.Round(s.ProgressPercent, 1), Yes(s.IsPaidOff), d.Note]);
            foreach (var p in s.Payments.Where(p => p.Date >= rangeStart && p.Date <= rangeEnd))
                payments.Rows.Add([d.Name, p.Date, p.Amount, p.Interest, p.Principal, Yes(p.IsExtra), p.Note]);
        }
        wb.Sheets.Add(debts);
        wb.Sheets.Add(payments);

        var buffers = new ExportSheet(T("export.sheet.buffers"),
            [T("col.name"), T("col.target"), T("col.targetDate"), T("col.bufferBalance"), T("col.progress"), T("col.emergency"), T("col.separate"), T("col.note")]);
        buffers.MoneyColumns.Add(1);
        buffers.MoneyColumns.Add(3);
        var tx = new ExportSheet(T("export.sheet.transactions"),
            [T("col.buffer"), T("col.date"), T("col.type"), T("col.amount"), T("col.reason"), T("col.affectsBudget")]);
        tx.MoneyColumns.Add(3);
        foreach (var b in ix.Buffers)
        {
            var s = engine.Buffers.Status(b.Id);
            buffers.Rows.Add([b.Name, b.TargetAmount, b.TargetDate, s.Balance, s.ProgressPercent is { } pp ? Math.Round(pp, 1) : null, Yes(b.IsEmergencyFund), Yes(b.SeparateAccount), b.Note]);
            foreach (var h in s.History.Where(h => h.Date >= rangeStart && h.Date <= rangeEnd))
                tx.Rows.Add([b.Name, h.Date, h.Type == BufferTransactionType.Allocation ? T("export.allocation") : T("export.withdrawal"),
                    Math.Abs(h.SignedAmount), h.Reason, Yes(h.AffectsBudget)]);
        }
        wb.Sheets.Add(buffers);
        wb.Sheets.Add(tx);

        var checkpoints = new ExportSheet(T("export.sheet.checkpoints"), [T("col.date"), T("col.amount"), T("col.note")]);
        checkpoints.MoneyColumns.Add(1);
        foreach (var c in ix.Checkpoints.Where(c => c.Date >= rangeStart && c.Date <= rangeEnd))
            checkpoints.Rows.Add([c.Date, c.Amount, c.Note]);
        wb.Sheets.Add(checkpoints);
        return wb;
    }
}
