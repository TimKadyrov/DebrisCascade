using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DebrisCascade.Core;

/// <summary>
/// A parsed two-line element set (optionally with a preceding name line, "3LE" format
/// as served by CelesTrak). Provides the epoch and the mean elements needed to seed
/// the propagator.
/// </summary>
public sealed class Tle
{
    public string Name { get; init; } = "";
    public int NoradId { get; init; }
    public DateTime EpochUtc { get; init; }

    public double InclinationRad { get; init; }
    public double RaanRad { get; init; }
    public double Eccentricity { get; init; }
    public double ArgPerigeeRad { get; init; }
    public double MeanAnomalyRad { get; init; }
    public double MeanMotionRevPerDay { get; init; }
    public double BStar { get; init; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public OrbitalElements ToElements() => OrbitalElements.FromMeanMotionRevPerDay(
        MeanMotionRevPerDay, Eccentricity, InclinationRad, RaanRad, ArgPerigeeRad, MeanAnomalyRad);

    /// <summary>
    /// The elements at <paramref name="atUtc"/> rather than at this element set's own epoch. Use one common instant
    /// for a whole catalog: each set's epoch is often at an equator crossing, so taking every object at its own
    /// epoch would bunch the population on the equator.
    /// </summary>
    public OrbitalElements ToElementsAt(DateTime atUtc) => ToElements().AdvancedBy((atUtc - EpochUtc).TotalSeconds);

    /// <summary>
    /// The 5-character catalog-number field: plain digits, or "Alpha-5" for numbers ≥ 100,000 — a
    /// letter for the leading two digits (A = 10 … Z = 33, skipping I and O) plus four digits, so
    /// A0000 = 100000 and Z9999 = 339999. Space-Track uses Alpha-5 for every newer object.
    /// </summary>
    public static int ParseCatalogNumber(string field)
    {
        field = field.Trim();
        if (field.Length == 5 && char.IsAsciiLetter(field[0]))
        {
            char c = char.ToUpperInvariant(field[0]);
            if (c is 'I' or 'O') throw new FormatException($"Invalid Alpha-5 catalog number '{field}'.");
            int lead = c - 'A' + 10 - (c > 'I' ? 1 : 0) - (c > 'O' ? 1 : 0);
            return lead * 10_000 + int.Parse(field[1..], NumberStyles.None, Inv);
        }
        return int.Parse(field, NumberStyles.None, Inv);
    }

    /// <summary>Parse a single TLE from its two data lines (and an optional name).</summary>
    public static Tle Parse(string line1, string line2, string? name = null)
    {
        if (line1.Length < 63 || line2.Length < 63)
            throw new FormatException("TLE lines are too short to be valid.");
        if (line1[0] != '1' || line2[0] != '2')
            throw new FormatException("TLE lines must start with '1' and '2'.");

        int norad = ParseCatalogNumber(line2.Substring(2, 5));

        // Epoch: 2-digit year + fractional day-of-year.
        int yy = int.Parse(line1.Substring(18, 2), Inv);
        int year = yy < 57 ? 2000 + yy : 1900 + yy;
        double doy = double.Parse(line1.Substring(20, 12), Inv);
        var epoch = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(doy - 1.0);

        // Eccentricity has an implied leading decimal point.
        double ecc = double.Parse("0." + line2.Substring(26, 7).Trim(), Inv);

        return new Tle
        {
            Name = (name ?? $"NORAD {norad}").Trim(),
            NoradId = norad,
            EpochUtc = epoch,
            InclinationRad = double.Parse(line2.Substring(8, 8), Inv) * Constants.DegToRad,
            RaanRad = double.Parse(line2.Substring(17, 8), Inv) * Constants.DegToRad,
            Eccentricity = ecc,
            ArgPerigeeRad = double.Parse(line2.Substring(34, 8), Inv) * Constants.DegToRad,
            MeanAnomalyRad = double.Parse(line2.Substring(43, 8), Inv) * Constants.DegToRad,
            MeanMotionRevPerDay = double.Parse(line2.Substring(52, 11), Inv),
            BStar = ParseExp(line1.Substring(53, 8)),
        };
    }

    /// <summary>Parse the TLE exponential field, e.g. " 12345-3" => 0.12345e-3.</summary>
    private static double ParseExp(string field)
    {
        field = field.Trim();
        if (field.Length == 0 || field == "00000-0" || field == "00000+0") return 0.0;
        int signIdx = field.LastIndexOfAny(['+', '-']);
        if (signIdx <= 0) return 0.0;
        string mantissa = field[..signIdx].Replace(" ", "");
        int exp = int.Parse(field[signIdx..], Inv);
        double sign = mantissa.StartsWith('-') ? -1.0 : 1.0;
        mantissa = mantissa.TrimStart('+', '-');
        double m = double.Parse("0." + mantissa, Inv);
        return sign * m * Math.Pow(10, exp);
    }

    /// <summary>Load all TLEs from a 2LE or 3LE (named) text stream, skipping malformed entries.</summary>
    public static List<Tle> LoadMany(TextReader reader)
    {
        var result = new List<Tle>();
        string? a = reader.ReadLine();
        while (a != null)
        {
            a = a.TrimEnd();
            if (a.Length == 0) { a = reader.ReadLine(); continue; }

            if (a[0] == '1')
            {
                // 2LE: current line is line1.
                string? b = reader.ReadLine();
                if (b == null) break;
                TryAdd(result, a, b.TrimEnd(), null);
                a = reader.ReadLine();
            }
            else
            {
                // 3LE: current line is the name, next two are the elements.
                string name = a.StartsWith("0 ") ? a[2..] : a;
                string? l1 = reader.ReadLine();
                string? l2 = reader.ReadLine();
                if (l1 == null || l2 == null) break;
                TryAdd(result, l1.TrimEnd(), l2.TrimEnd(), name);
                a = reader.ReadLine();
            }
        }
        return result;
    }

    private static void TryAdd(List<Tle> list, string l1, string l2, string? name)
    {
        try { list.Add(Parse(l1, l2, name)); }
        catch (FormatException) { /* skip malformed record */ }
    }
}
