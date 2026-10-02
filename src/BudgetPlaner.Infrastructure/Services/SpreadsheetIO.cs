using System.Globalization;
using System.IO.Compression;
using System.Text;
using BudgetPlaner.Application.Abstractions;
using BudgetPlaner.Domain;
using ClosedXML.Excel;

namespace BudgetPlaner.Infrastructure.Services;

/// <summary>CSV/Excel lesen und schreiben (F-110, F-133).</summary>
public sealed class SpreadsheetIO : ISpreadsheetIO
{
    static SpreadsheetIO() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public List<List<string>> Read(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".xlsx" ? ReadXlsx(stream) : ReadCsv(stream);
    }

    private static List<List<string>> ReadXlsx(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        var rows = new List<List<string>>();
        if (range is null) return rows;
        foreach (var row in range.Rows())
        {
            var cells = new List<string>();
            foreach (var cell in row.Cells())
            {
                var v = cell.Value;
                cells.Add(v.Type switch
                {
                    XLDataType.Number => v.GetNumber().ToString(CultureInfo.InvariantCulture),
                    XLDataType.DateTime => v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    XLDataType.Boolean => v.GetBoolean() ? "ja" : "nein",
                    XLDataType.Blank => "",
                    _ => cell.GetFormattedString(),
                });
            }
            rows.Add(cells);
        }
        return rows;
    }

    private static List<List<string>> ReadCsv(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(1252).GetString(bytes); }
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n')[0];
        var delimiter = new[] { ';', '\t', ',' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        return ParseCsv(text, delimiter);
    }

    public static List<List<string>> ParseCsv(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    public byte[] WriteXlsx(ExportWorkbook workbook)
    {
        using var wb = new XLWorkbook();
        foreach (var sheet in workbook.Sheets)
        {
            var ws = wb.Worksheets.Add(SafeSheetName(sheet.Name));
            for (var c = 0; c < sheet.Headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = sheet.Headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E6EFEA");
            }
            var r = 2;
            foreach (var row in sheet.Rows)
            {
                for (var c = 0; c < row.Length; c++)
                {
                    var cell = ws.Cell(r, c + 1);
                    switch (row[c])
                    {
                        case null: break;
                        case decimal d:
                            cell.Value = d;
                            cell.Style.NumberFormat.Format = sheet.MoneyColumns.Contains(c) ? "#,##0.00" : "0.##";
                            break;
                        case int i: cell.Value = i; break;
                        case long l: cell.Value = l; break;
                        case double db: cell.Value = db; break;
                        case DateOnly date:
                            cell.Value = date.ToDateTime(TimeOnly.MinValue);
                            cell.Style.DateFormat.Format = "yyyy-mm-dd";
                            break;
                        case Period p: cell.Value = p.ToString(); break;
                        default: cell.Value = row[c]!.ToString(); break;
                    }
                }
                r++;
            }
            ws.SheetView.FreezeRows(1);
            if (sheet.Headers.Count > 0) ws.Range(1, 1, Math.Max(1, r - 1), sheet.Headers.Count).SetAutoFilter();
            ws.Columns().AdjustToContents(1, Math.Min(r, 200));
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] WriteCsvZip(ExportWorkbook workbook, string language)
    {
        var german = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var sep = german ? ';' : ',';
        var culture = german ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var index = 1;
            foreach (var sheet in workbook.Sheets)
            {
                var entry = zip.CreateEntry($"{index++:D2}-{SafeFileName(sheet.Name)}.csv");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true));
                writer.WriteLine(string.Join(sep, sheet.Headers.Select(h => Escape(h, sep))));
                foreach (var row in sheet.Rows)
                    writer.WriteLine(string.Join(sep, row.Select(v => Escape(Format(v, culture), sep))));
            }
        }
        return ms.ToArray();
    }

    private static string Format(object? v, CultureInfo culture) => v switch
    {
        null => "",
        decimal d => d.ToString("0.00##", culture),
        double d => d.ToString(culture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Escape(string s, char sep) =>
        s.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string SafeFileName(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-').ToLowerInvariant();
}
