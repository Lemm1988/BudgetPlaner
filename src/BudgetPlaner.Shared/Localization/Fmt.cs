using System.Globalization;
using BudgetPlaner.Domain;

namespace BudgetPlaner.Shared.Localization;

/// <summary>Unterstützte Haushaltswährungen (eine je Haushalt, F-100; Lastenheft §5: keine Umrechnung).</summary>
public sealed record CurrencyInfo(string Code, string Symbol, bool SymbolIsWord);

/// <summary>Formatierung von Beträgen, Prozenten, Daten und Monaten für DE/EN – unabhängig von installierten Kulturen.</summary>
public static class Fmt
{
    public static readonly IReadOnlyList<CurrencyInfo> Currencies =
    [
        new("EUR", "€", false),
        new("CHF", "CHF", true),
        new("USD", "$", false),
        new("GBP", "£", false),
        new("PLN", "zł", true),
        new("CZK", "Kč", true),
        new("SEK", "kr", true),
        new("NOK", "kr", true),
        new("DKK", "kr", true),
        new("HUF", "Ft", true),
    ];

    private static readonly string[] MonthsDe = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];
    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] MonthsDeShort = ["Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez"];
    private static readonly string[] MonthsEnShort = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] WeekdaysDe = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
    private static readonly string[] WeekdaysEn = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] WeekdaysDeLong = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];
    private static readonly string[] WeekdaysEnLong = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private static readonly NumberFormatInfo De = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NegativeSign = "-" };
    private static readonly NumberFormatInfo En = new() { NumberDecimalSeparator = ".", NumberGroupSeparator = ",", NegativeSign = "-" };

    public const char Nbsp = ' ';
    public const char Minus = '−';

    public static NumberFormatInfo Numbers(string lang) => Texts.Normalize(lang) == Texts.English ? En : De;

    public static CurrencyInfo Currency(string? code) =>
        Currencies.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)) ?? new(code ?? "EUR", code ?? "€", true);

    /// <summary>Betrag mit Währung, z. B. „1.234,56 €“ bzw. „€1,234.56“; negative Werte mit echtem Minuszeichen.</summary>
    public static string Money(decimal value, string currency, string lang, bool showPlus = false, int decimals = 2)
    {
        var info = Currency(currency);
        var abs = Math.Round(Math.Abs(value), decimals, MidpointRounding.AwayFromZero).ToString("N" + decimals, Numbers(lang));
        var sign = value < 0 && Math.Round(value, decimals) != 0 ? Minus.ToString() : showPlus && value > 0 ? "+" : "";
        if (Texts.Normalize(lang) == Texts.German) return $"{sign}{abs}{Nbsp}{info.Symbol}";
        return info.SymbolIsWord ? $"{sign}{info.Symbol}{Nbsp}{abs}" : $"{sign}{info.Symbol}{abs}";
    }

    /// <summary>Kompakte Darstellung ohne Nachkommastellen (Diagrammachsen), z. B. „1,2 Tsd. €“.</summary>
    public static string MoneyCompact(decimal value, string currency, string lang)
    {
        var abs = Math.Abs(value);
        if (abs < 10000) return Money(value, currency, lang, decimals: 0);
        var de = Texts.Normalize(lang) == Texts.German;
        var (scaled, suffix) = abs >= 1_000_000 ? (value / 1_000_000m, de ? " Mio." : "M") : (value / 1000m, de ? " Tsd." : "k");
        var info = Currency(currency);
        var n = Math.Round(scaled, 1).ToString("0.#", Numbers(lang));
        return de ? $"{n}{suffix}{Nbsp}{info.Symbol}" : info.SymbolIsWord ? $"{info.Symbol}{Nbsp}{n}{suffix}" : $"{info.Symbol}{n}{suffix}";
    }

    public static string Number(decimal value, string lang, int decimals = 2) =>
        (value < 0 ? Minus.ToString() : "") + Math.Abs(value).ToString("N" + decimals, Numbers(lang));

    /// <summary>Prozent, z. B. „12,3 %“ (DE) bzw. „12.3%“ (EN); <c>null</c> → „–“.</summary>
    public static string Percent(decimal? value, string lang, int decimals = 1, bool showPlus = false)
    {
        if (value is not { } v) return "–";
        var rounded = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        var s = Math.Abs(rounded).ToString("N" + decimals, Numbers(lang));
        var sign = rounded < 0 ? Minus.ToString() : showPlus && rounded > 0 ? "+" : "";
        return Texts.Normalize(lang) == Texts.German ? $"{sign}{s}{Nbsp}%" : $"{sign}{s}%";
    }

    public static string MonthName(int month, string lang, bool shortName = false)
    {
        var en = Texts.Normalize(lang) == Texts.English;
        return (en ? shortName ? MonthsEnShort : MonthsEn : shortName ? MonthsDeShort : MonthsDe)[month - 1];
    }

    /// <summary>„November 2026“ bzw. „Nov 26“.</summary>
    public static string PeriodName(Period p, string lang, bool shortName = false) =>
        shortName ? $"{MonthName(p.Month, lang, true)} {p.Year % 100:D2}" : $"{MonthName(p.Month, lang)} {p.Year}";

    public static string Date(DateOnly d, string lang) =>
        Texts.Normalize(lang) == Texts.English ? $"{d.Day} {MonthName(d.Month, lang, true)} {d.Year}" : $"{d.Day:D2}.{d.Month:D2}.{d.Year}";

    public static string DateShort(DateOnly d, string lang) =>
        Texts.Normalize(lang) == Texts.English ? $"{d.Day} {MonthName(d.Month, lang, true)}" : $"{d.Day:D2}.{d.Month:D2}.";

    public static string DateTime(DateTimeOffset dt, string lang, TimeZoneInfo? tz = null)
    {
        var local = tz is null ? dt.ToLocalTime() : TimeZoneInfo.ConvertTime(dt, tz);
        return $"{Date(DateOnly.FromDateTime(local.DateTime), lang)} {local:HH:mm}";
    }

    public static string Weekday(DateOnly d, string lang, bool longName = false)
    {
        var en = Texts.Normalize(lang) == Texts.English;
        var i = (int)d.DayOfWeek;
        return longName ? (en ? WeekdaysEnLong : WeekdaysDeLong)[i] : (en ? WeekdaysEn : WeekdaysDe)[i];
    }

    /// <summary>Zweibuchstabige Wochentagskürzel ab Montag (Kalenderkopf).</summary>
    public static IReadOnlyList<string> WeekHeader(string lang)
    {
        var en = Texts.Normalize(lang) == Texts.English;
        var names = en ? WeekdaysEn : WeekdaysDe;
        return [names[1], names[2], names[3], names[4], names[5], names[6], names[0]];
    }

    /// <summary>
    /// Toleranter Betrags-Parser: akzeptiert „1.234,56“, „1234,56“, „1,234.56“, „1234.56“, „-50“, „50 €“.
    /// </summary>
    public static bool TryParseAmount(string? input, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = new string(input.Trim().Replace(Minus, '-').Where(c => char.IsDigit(c) || c is ',' or '.' or '-' or '\'').ToArray()).Replace("'", "");
        if (s.Length == 0 || s == "-") return false;
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        string normalized;
        if (lastComma >= 0 && lastDot >= 0)
            normalized = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (lastComma >= 0)
            normalized = s.Count(c => c == ',') == 1 && s.Length - lastComma - 1 is > 0 and <= 2 ? s.Replace(',', '.') : s.Replace(",", "");
        else if (lastDot >= 0)
            normalized = s.Count(c => c == '.') == 1 && s.Length - lastDot - 1 is > 0 and <= 2 ? s : s.Replace(".", "");
        else normalized = s;
        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Betrag als Eingabetext (ohne Tausendertrennzeichen, mit Sprach-Dezimaltrenner).</summary>
    public static string AmountInput(decimal value, string lang) =>
        value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', Texts.Normalize(lang) == Texts.German ? ',' : '.');
}
