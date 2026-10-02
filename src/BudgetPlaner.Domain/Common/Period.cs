using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BudgetPlaner.Domain;

/// <summary>
/// Budgetmonat (Periode) als Jahr/Monat. Welche Kalendertage zu einer Periode gehören,
/// bestimmt der Monatsstart-Tag des Haushalts (F-101, siehe <see cref="PeriodCalendar"/>).
/// </summary>
[JsonConverter(typeof(PeriodJsonConverter))]
public readonly struct Period : IEquatable<Period>, IComparable<Period>
{
    public Period(int year, int month)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month), month, "Monat muss zwischen 1 und 12 liegen.");
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year), year, "Ungültiges Jahr.");
        Year = year;
        Month = month;
    }

    public int Year { get; }
    public int Month { get; }

    /// <summary>Fortlaufender Monatsindex (Jahr × 12 + Monat − 1) für schnelle Arithmetik.</summary>
    public int Index => Year * 12 + Month - 1;

    public static Period FromIndex(int index) => new(index / 12, index % 12 + 1);
    public Period AddMonths(int months) => FromIndex(Index + months);
    public Period Next => AddMonths(1);
    public Period Previous => AddMonths(-1);
    public static int MonthsBetween(Period from, Period to) => to.Index - from.Index;
    public int DaysInCalendarMonth => DateTime.DaysInMonth(Year, Month);

    /// <summary>Kalendermonat eines Datums (nicht die Budgetperiode – dafür <see cref="PeriodCalendar.PeriodOf"/>).</summary>
    public static Period OfCalendarDate(DateOnly date) => new(date.Year, date.Month);

    /// <summary>Kompakte Ganzzahl-Darstellung JJJJMM (z. B. 202611) für Datenbank-Spalten.</summary>
    public int ToKey() => Year * 100 + Month;
    public static Period FromKey(int key) => new(key / 100, key % 100);

    public static IEnumerable<Period> Range(Period from, Period to)
    {
        for (var i = from.Index; i <= to.Index; i++) yield return FromIndex(i);
    }

    public static Period Max(Period a, Period b) => a >= b ? a : b;
    public static Period Min(Period a, Period b) => a <= b ? a : b;

    public override string ToString() => $"{Year:D4}-{Month:D2}";

    public static Period Parse(string s) =>
        TryParse(s, out var p) ? p : throw new FormatException($"Ungültige Periode '{s}', erwartet JJJJ-MM.");

    public static bool TryParse([NotNullWhen(true)] string? s, out Period period)
    {
        period = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split('-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var y)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (m is < 1 or > 12 || y is < 1 or > 9999) return false;
        period = new Period(y, m);
        return true;
    }

    public bool Equals(Period other) => Year == other.Year && Month == other.Month;
    public override bool Equals(object? obj) => obj is Period p && Equals(p);
    public override int GetHashCode() => Index;
    public int CompareTo(Period other) => Index.CompareTo(other.Index);

    public static bool operator ==(Period a, Period b) => a.Equals(b);
    public static bool operator !=(Period a, Period b) => !a.Equals(b);
    public static bool operator <(Period a, Period b) => a.Index < b.Index;
    public static bool operator >(Period a, Period b) => a.Index > b.Index;
    public static bool operator <=(Period a, Period b) => a.Index <= b.Index;
    public static bool operator >=(Period a, Period b) => a.Index >= b.Index;
}

public sealed class PeriodJsonConverter : JsonConverter<Period>
{
    public override Period Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return Period.FromKey(reader.GetInt32());
        var s = reader.GetString();
        return Period.TryParse(s, out var p) ? p : throw new JsonException($"Ungültige Periode '{s}'.");
    }

    public override void Write(Utf8JsonWriter writer, Period value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override Period ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Period.Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Period value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString());
}
