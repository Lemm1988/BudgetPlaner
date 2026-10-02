using System.Globalization;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Application.Common;
using BudgetPlaner.Application.Services.Sync;
using BudgetPlaner.Domain;
using BudgetPlaner.Domain.Defaults;
using BudgetPlaner.Domain.Entities;
using BudgetPlaner.Shared.Contracts;
using BudgetPlaner.Shared.Localization;
using Microsoft.Extensions.Caching.Memory;

namespace BudgetPlaner.Application.Services;

/// <summary>Import aus CSV/Excel mit Spaltenzuordnung; fehlerhafte Zeilen werden markiert und können übersprungen werden (F-133).</summary>
public sealed class ImportService(ISpreadsheetIO io, IMemoryCache cache, SyncService sync, HouseholdDataLoader loader, IClock clock)
{
    private static string Key(Guid user, Guid import) => $"import:{user}:{import}";

    public ImportPreviewResponse Preview(UserContext ctx, Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx" or ".txt" or ".tsv")) throw AppException.BadRequest("err.import.fileType");
        var rows = io.Read(stream, fileName).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count < 2) throw AppException.BadRequest("err.import.empty");
        var id = Guid.NewGuid();
        cache.Set(Key(ctx.UserId, id), rows, TimeSpan.FromMinutes(30));
        var headers = rows[0].Select(h => h.Trim()).ToList();
        var target = ImportParser.SuggestTarget(headers);
        return new ImportPreviewResponse(id, fileName, headers, rows.Skip(1).Take(10).Select(r => (IReadOnlyList<string>)r).ToList(),
            rows.Count - 1, ImportParser.SuggestMapping(headers, target), target);
    }

    public async Task<ImportValidationResponse> ValidateAsync(UserContext ctx, ImportMappingRequest request, CancellationToken ct = default)
    {
        var (rows, parsed) = await ParseAsync(ctx, request, ct);
        var results = parsed.Select(p => new ImportRowResult(p.RowNumber, p.Errors.Count == 0, p.Errors, p.Values)).ToList();
        return new ImportValidationResponse(rows, results.Count(r => r.Valid), results.Count(r => !r.Valid), results);
    }

    public async Task<ImportCommitResponse> CommitAsync(UserContext ctx, ImportCommitRequest request, CancellationToken ct = default)
    {
        var (_, parsed) = await ParseAsync(ctx, request.Mapping, ct);
        var skip = request.SkipRows.ToHashSet();
        var importable = parsed.Where(p => p.Errors.Count == 0 && !skip.Contains(p.RowNumber)).ToList();
        var mutations = new List<EntityMutation>();
        var newCategories = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        var data = await loader.LoadForReadAsync(ctx.HouseholdId, ct);
        var ix = data.Index;
        var sort = ix.Entries.Count == 0 ? 0 : ix.Entries.Max(e => e.SortIndex) + 1;

        var rowOps = new Dictionary<int, List<Guid>>();
        foreach (var row in importable)
        {
            var ops = rowOps[row.RowNumber] = [];
            foreach (var cat in row.NewCategories)
            {
                if (newCategories.ContainsKey(cat.Name!)) continue;
                newCategories[cat.Name!] = cat;
                var m = EntityMutation.Upsert(cat);
                ops.Add(m.OpId);
                mutations.Add(m);
            }
            foreach (var e in row.Entities)
            {
                if (e is BudgetEntry be)
                {
                    if (row.CategoryName is { } cn && newCategories.TryGetValue(cn, out var created)) be.CategoryId = created.Id;
                    be.SortIndex = sort++;
                }
                var m = EntityMutation.Upsert(e);
                ops.Add(m.OpId);
                mutations.Add(m);
            }
        }
        var result = await sync.ApplyAsync(ctx, ctx.HouseholdId, mutations, ct);
        var rejectedOps = result.Results.Where(r => r.Status == SyncOpStatus.Rejected).Select(r => r.OpId).ToHashSet();
        var imported = rowOps.Count(kv => kv.Value.All(id => !rejectedOps.Contains(id)));
        return new ImportCommitResponse(imported, parsed.Count - imported);
    }

    private async Task<(int Total, List<ImportParser.ParsedRow> Rows)> ParseAsync(UserContext ctx, ImportMappingRequest request, CancellationToken ct)
    {
        if (!cache.TryGetValue(Key(ctx.UserId, request.ImportId), out List<List<string>>? rows) || rows is null)
            throw AppException.BadRequest("err.import.expired");
        if (!ImportFields.Fields.ContainsKey(request.Target)) throw AppException.BadRequest("err.validation");
        var missing = ImportFields.Required[request.Target].Where(f => !request.Mapping.TryGetValue(f, out var c) || c < 0).ToList();
        if (missing.Count > 0)
            throw AppException.BadRequest("err.import.mapping", string.Join(", ", missing.Select(f => Texts.Get(ctx.Language, "import.field." + f))));
        var data = await loader.LoadForReadAsync(ctx.HouseholdId, ct);
        var today = clock.TodayIn(ctx.User.TimeZone);
        var parser = new ImportParser(data.Index, ctx.Language, data.Index.Calendar.PeriodOf(today), today);
        var body = request.FirstRowIsHeader ? rows.Skip(1) : rows;
        var offset = request.FirstRowIsHeader ? 2 : 1;
        var parsed = body.Select((r, i) => parser.Parse(request.Target, request.Mapping, r, i + offset)).ToList();
        return (parsed.Count, parsed);
    }
}

/// <summary>Zeilen-Parser für den Import (tolerant gegenüber deutschen/englischen Schreibweisen).</summary>
public sealed class ImportParser(HouseholdIndex ix, string lang, Period currentPeriod, DateOnly today)
{
    public sealed class ParsedRow
    {
        public int RowNumber { get; init; }
        public List<string> Errors { get; } = [];
        public Dictionary<string, string?> Values { get; } = [];
        public List<HouseholdEntity> Entities { get; } = [];
        public List<Category> NewCategories { get; } = [];
        public string? CategoryName { get; set; }
    }

    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["name"] = ["bezeichnung", "name", "position", "titel", "beschreibung", "posten", "item", "description", "verwendungszweck", "kategorie name"],
        ["amount"] = ["betrag", "amount", "summe", "wert", "value", "eur", "euro", "monatsbetrag", "sum"],
        ["kind"] = ["typ", "kind", "einnahme/ausgabe", "income/expense", "buchungsart"],
        ["category"] = ["kategorie", "category", "bereich", "unterkategorie"],
        ["rhythm"] = ["rhythmus", "intervall", "turnus", "frequency", "rhythm", "zahlungsrhythmus", "häufigkeit"],
        ["debitDay"] = ["abbuchungstag", "tag", "day", "debit day", "zahltag", "fälligkeitstag", "faelligkeitstag"],
        ["isFixed"] = ["fix", "fixkosten", "fixed", "fixed cost", "fix/variabel"],
        ["startPeriod"] = ["start", "beginn", "startmonat", "ab", "from", "gültig ab", "start month"],
        ["endPeriod"] = ["ende", "bis", "endmonat", "end", "to", "end month"],
        ["note"] = ["notiz", "note", "bemerkung", "kommentar", "comment"],
        ["creditor"] = ["gläubiger", "glaeubiger", "creditor", "bank", "kreditgeber", "lender"],
        ["type"] = ["kreditart", "art", "type", "loan type"],
        ["originalAmount"] = ["ursprungssumme", "kreditsumme", "original", "original amount", "darlehensbetrag", "darlehen"],
        ["balance"] = ["restschuld", "stand", "saldo", "balance", "remaining balance", "aktueller stand", "anfangsbestand"],
        ["interestRate"] = ["zinssatz", "zins", "interest", "zinssatz % p. a.", "interest % p.a.", "sollzins"],
        ["monthlyRate"] = ["monatsrate", "rate", "monthly payment", "monthly rate"],
        ["targetAmount"] = ["zielbetrag", "ziel", "target", "target amount", "sparziel"],
        ["isEmergencyFund"] = ["notgroschen", "emergency fund", "finanzpolster"],
    };

    private static string Norm(string s) => s.Trim().ToLowerInvariant().Replace("_", " ").Replace("  ", " ");

    public static string SuggestTarget(IReadOnlyList<string> headers)
    {
        var h = headers.Select(Norm).ToList();
        if (h.Any(x => Synonyms["creditor"].Contains(x) || x is "restschuld" or "zinssatz" or "zins" or "kreditart" or "remaining balance")) return ImportFields.TargetDebts;
        if (h.Any(x => Synonyms["targetAmount"].Contains(x))) return ImportFields.TargetBuffers;
        return ImportFields.TargetEntries;
    }

    public static Dictionary<string, int> SuggestMapping(IReadOnlyList<string> headers, string target)
    {
        var result = new Dictionary<string, int>();
        var normalized = headers.Select(Norm).ToList();
        foreach (var field in ImportFields.Fields[target])
        {
            if (!Synonyms.TryGetValue(field, out var syn)) continue;
            var idx = normalized.FindIndex(h => syn.Contains(h));
            if (idx < 0) idx = normalized.FindIndex(h => syn.Any(s => h.StartsWith(s, StringComparison.Ordinal)));
            if (idx >= 0 && !result.ContainsValue(idx)) result[field] = idx;
        }
        if (target == ImportFields.TargetBuffers && !result.ContainsKey("balance"))
        {
            var idx = normalized.FindIndex(h => h is "betrag" or "amount" or "stand");
            if (idx >= 0) result["balance"] = idx;
        }
        return result;
    }

    public ParsedRow Parse(string target, IReadOnlyDictionary<string, int> mapping, IReadOnlyList<string> row, int rowNumber)
    {
        var r = new ParsedRow { RowNumber = rowNumber };
        string? Get(string field) =>
            mapping.TryGetValue(field, out var col) && col >= 0 && col < row.Count && !string.IsNullOrWhiteSpace(row[col]) ? row[col].Trim() : null;
        foreach (var f in ImportFields.Fields[target]) r.Values[f] = Get(f);
        string Err(string key, params object?[] args) => Texts.Get(lang, key, args);

        var name = Get("name");
        if (name is null) r.Errors.Add(Err("err.import.required", Texts.Get(lang, "import.field.name")));

        switch (target)
        {
            case ImportFields.TargetEntries:
            {
                decimal amount = 0;
                var amountText = Get("amount");
                if (amountText is null) r.Errors.Add(Err("err.import.required", Texts.Get(lang, "import.field.amount")));
                else if (!Fmt.TryParseAmount(amountText, out amount)) r.Errors.Add(Err("err.import.amount", amountText));

                EntryKind? kind = null;
                if (Get("kind") is { } k)
                {
                    kind = ParseKind(k);
                    if (kind is null) r.Errors.Add(Err("err.import.kind", k));
                }
                Category? category = null;
                if (Get("category") is { } catName)
                {
                    category = FindCategory(catName);
                    if (category is null) r.CategoryName = catName;
                }
                kind ??= category?.Type == CategoryType.Income ? EntryKind.Income : EntryKind.Expense;
                if (amount < 0) amount = -amount;
                if (category is not null && (category.Type == CategoryType.Income) != (kind == EntryKind.Income))
                    r.Errors.Add(Err("err.category.type"));

                var rhythm = Rhythm.Monthly;
                if (Get("rhythm") is { } rh)
                {
                    if (ParseRhythm(rh) is { } parsed) rhythm = parsed;
                    else r.Errors.Add(Err("err.import.rhythm", rh));
                }
                int? day = null;
                if (Get("debitDay") is { } d)
                {
                    if (int.TryParse(d.Replace(".", ""), out var dd) && dd is >= 1 and <= 31) day = dd;
                    else r.Errors.Add(Err("err.import.debitDay", d));
                }
                var isFixed = false;
                if (Get("isFixed") is { } fx)
                {
                    if (ParseBool(fx) is { } b) isFixed = b;
                    else r.Errors.Add(Err("err.import.bool", fx));
                }
                var start = currentPeriod;
                if (Get("startPeriod") is { } sp)
                {
                    if (ParsePeriod(sp) is { } p) start = p;
                    else r.Errors.Add(Err("err.import.period", sp));
                }
                Period? end = null;
                if (Get("endPeriod") is { } ep)
                {
                    if (ParsePeriod(ep) is { } p) end = p;
                    else r.Errors.Add(Err("err.import.period", ep));
                }
                if (end is { } e2 && e2 < start) r.Errors.Add(Err("err.entry.period"));

                if (r.Errors.Count == 0)
                {
                    if (category is null && r.CategoryName is not null)
                    {
                        var parent = ix.Categories.First(c => c.DefaultKey == (kind == EntryKind.Income ? DefaultCategories.IncomeRoot : DefaultCategories.Other));
                        r.NewCategories.Add(new Category
                        {
                            Id = Guid.NewGuid(), Name = r.CategoryName, Type = parent.Type, Group = parent.Group, ParentId = parent.Id,
                            Color = parent.Color, Icon = parent.Icon, SortIndex = 1000,
                        });
                        category = r.NewCategories[0];
                    }
                    category ??= ix.Categories.First(c => c.DefaultKey == (kind == EntryKind.Income ? DefaultCategories.OtherIncome : DefaultCategories.Other));
                    r.Entities.Add(new BudgetEntry
                    {
                        Id = Guid.NewGuid(), Name = name!, Amount = amount, Kind = kind == EntryKind.Allocation ? EntryKind.Expense : kind.Value,
                        CategoryId = category.Id, Rhythm = rhythm, DebitDay = day, IsFixed = isFixed, StartPeriod = start, EndPeriod = end, Note = Get("note"),
                    });
                }
                break;
            }
            case ImportFields.TargetDebts:
            {
                var balance = Amount("balance", true, r);
                var original = Amount("originalAmount", false, r);
                var rate = Amount("interestRate", false, r, percent: true);
                var monthly = Amount("monthlyRate", false, r);
                var type = DebtType.Loan;
                if (Get("type") is { } t)
                {
                    if (ParseDebtType(t) is { } dt) type = dt;
                    else r.Errors.Add(Err("err.import.debtType", t));
                }
                int? day = null;
                if (Get("debitDay") is { } d)
                {
                    if (int.TryParse(d, out var dd) && dd is >= 1 and <= 31) day = dd;
                    else r.Errors.Add(Err("err.import.debitDay", d));
                }
                if (r.Errors.Count == 0)
                    r.Entities.Add(new DebtEntry
                    {
                        Id = Guid.NewGuid(), Name = name!, Creditor = Get("creditor"), Type = type, OpeningBalance = balance ?? 0,
                        OriginalAmount = Math.Max(original ?? 0, balance ?? 0), InterestRate = rate, MonthlyRate = monthly, DebitDay = day,
                        StartDate = today, AutoBook = true, Note = Get("note"),
                    });
                break;
            }
            case ImportFields.TargetBuffers:
            {
                var targetAmount = Amount("targetAmount", false, r);
                var balance = Amount("balance", false, r);
                var emergency = false;
                if (Get("isEmergencyFund") is { } em)
                {
                    if (ParseBool(em) is { } b) emergency = b;
                    else r.Errors.Add(Err("err.import.bool", em));
                }
                if (r.Errors.Count == 0)
                {
                    var buffer = new BufferEntry { Id = Guid.NewGuid(), Name = name!, TargetAmount = targetAmount, IsEmergencyFund = emergency, Note = Get("note") };
                    r.Entities.Add(buffer);
                    if (balance is > 0)
                        r.Entities.Add(new BufferTransaction
                        {
                            Id = Guid.NewGuid(), BufferId = buffer.Id, Date = today, Amount = balance.Value, Type = BufferTransactionType.Allocation,
                            AffectsBudget = false, Reason = Texts.Get(lang, "buffer.openingBalance"),
                        });
                }
                break;
            }
        }
        return r;

        decimal? Amount(string field, bool required, ParsedRow row, bool percent = false)
        {
            var text = Get(field);
            if (text is null)
            {
                if (required) row.Errors.Add(Err("err.import.required", Texts.Get(lang, "import.field." + field)));
                return null;
            }
            if (!Fmt.TryParseAmount(text.Replace("%", ""), out var v)) { row.Errors.Add(Err(percent ? "err.import.number" : "err.import.amount", text)); return null; }
            return Math.Abs(v);
        }
    }

    private Category? FindCategory(string name)
    {
        var n = Norm(name);
        foreach (var c in ix.Categories)
        {
            if (c.Name is { } own && Norm(own) == n) return c;
            if (c.DefaultKey is { } key && (key == n || Norm(Texts.Get("de", "cat." + key)) == n || Norm(Texts.Get("en", "cat." + key)) == n)) return c;
        }
        return null;
    }

    public static EntryKind? ParseKind(string s) => Norm(s) switch
    {
        "einnahme" or "einnahmen" or "income" or "e" or "+" or "i" or "in" or "haben" => EntryKind.Income,
        "ausgabe" or "ausgaben" or "expense" or "expenses" or "a" or "-" or "out" or "soll" => EntryKind.Expense,
        "zuweisung" or "polster" or "allocation" or "polster-zuweisung" => EntryKind.Allocation,
        _ => null,
    };

    public static Rhythm? ParseRhythm(string s) => Norm(s) switch
    {
        "monatlich" or "monthly" or "1" or "m" or "monat" or "jeden monat" => Rhythm.Monthly,
        "alle 2 monate" or "zweimonatlich" or "every 2 months" or "bimonthly" or "2" => Rhythm.BiMonthly,
        "vierteljährlich" or "vierteljaehrlich" or "quartal" or "quartalsweise" or "quarterly" or "3" or "q" => Rhythm.Quarterly,
        "halbjährlich" or "halbjaehrlich" or "half-yearly" or "halfyearly" or "semiannual" or "6" => Rhythm.HalfYearly,
        "jährlich" or "jaehrlich" or "yearly" or "annual" or "annually" or "12" or "j" or "jahr" => Rhythm.Yearly,
        "einmalig" or "once" or "one-time" or "einmal" or "0" => Rhythm.Once,
        _ => null,
    };

    public static bool? ParseBool(string s) => Norm(s) switch
    {
        "ja" or "yes" or "true" or "1" or "x" or "fix" or "wahr" or "y" or "j" => true,
        "nein" or "no" or "false" or "0" or "variabel" or "falsch" or "n" or "-" or "" => false,
        _ => null,
    };

    public static DebtType? ParseDebtType(string s) => Norm(s) switch
    {
        "ratenkredit" or "kredit" or "loan" or "personal loan" or "konsumkredit" => DebtType.Loan,
        "hypothek" or "baufinanzierung" or "immobilienkredit" or "mortgage" or "baufinanzierung / hypothek" => DebtType.Mortgage,
        "leasing" or "lease" => DebtType.Leasing,
        "privat" or "privatdarlehen" or "private" or "private loan" or "familie" => DebtType.Private,
        "kreditkarte" or "credit card" => DebtType.CreditCard,
        "dispo" or "dispokredit" or "overdraft" => DebtType.Overdraft,
        "sonstige" or "sonstige verbindlichkeit" or "other" or "other liability" => DebtType.Other,
        _ => null,
    };

    /// <summary>Monat aus „2026-11“, „11/2026“, „11.2026“, „01.11.2026“, „Nov 2026“ oder Excel-Datum.</summary>
    public static Period? ParsePeriod(string s)
    {
        s = s.Trim();
        if (Period.TryParse(s, out var p)) return p;
        var parts = s.Split(['/', '.', ' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var y) && m is >= 1 and <= 12)
            return new Period(y < 100 ? 2000 + y : y, m);
        if (parts.Length == 2 && int.TryParse(parts[1], out var y2))
        {
            for (var i = 1; i <= 12; i++)
                if (parts[0].StartsWith(Fmt.MonthName(i, "de", true), StringComparison.OrdinalIgnoreCase)
                    || parts[0].StartsWith(Fmt.MonthName(i, "en", true), StringComparison.OrdinalIgnoreCase))
                    return new Period(y2 < 100 ? 2000 + y2 : y2, i);
        }
        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-ddTHH:mm:ss"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return new Period(dt.Year, dt.Month);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa is > 20000 and < 80000)
        {
            var date = DateTime.FromOADate(oa);
            return new Period(date.Year, date.Month);
        }
        return null;
    }
}
