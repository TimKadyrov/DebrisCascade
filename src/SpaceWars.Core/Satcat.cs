using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SpaceWars.Core;

/// <summary>One CelesTrak SATCAT record: object type and radar cross-section (m², may be missing).</summary>
public readonly record struct SatcatRecord(int NoradId, string ObjectType, double? RcsM2);

/// <summary>A catalog object with its orbit and physically-derived mass/area (from SATCAT RCS).</summary>
public readonly record struct CatalogObject(OrbitalElements Elements, double MassKg, double AreaM2, bool IsIntact);

/// <summary>
/// Loads the CelesTrak SATCAT (satcat.csv) and derives per-object mass and cross-section,
/// replacing the class-representative assumptions. RCS is used as the collision cross-section;
/// mass is inferred from the implied characteristic length via the NASA breakup-model size↔mass
/// relations. Objects without an RCS fall back to a type-based default area.
/// </summary>
public static class Satcat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Column indices in satcat.csv.
    private const int ColNorad = 2, ColType = 3, ColRcs = 13, MinCols = 17;

    public static Dictionary<int, SatcatRecord> Load(TextReader reader)
    {
        var map = new Dictionary<int, SatcatRecord>();
        string? line = reader.ReadLine(); // header
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            if (f.Length < MinCols) continue;
            if (!int.TryParse(f[ColNorad].Trim(), NumberStyles.Integer, Inv, out int norad)) continue;
            double? rcs = null;
            if (double.TryParse(f[ColRcs].Trim(), NumberStyles.Float, Inv, out double r) && r > 0) rcs = r;
            map[norad] = new SatcatRecord(norad, f[ColType].Trim(), rcs);
        }
        return map;
    }

    public static Dictionary<int, SatcatRecord> LoadFromFile(string path)
    {
        using var reader = new StreamReader(path);
        return Load(reader);
    }

    /// <summary>Physical cross-section [m²] and mass [kg] for an object (RCS-driven, type fallback).</summary>
    public static (double MassKg, double AreaM2) DeriveMassArea(SatcatRecord rec)
    {
        double area = rec.RcsM2 ?? DefaultAreaForType(rec.ObjectType);
        if (area <= 0) area = DefaultAreaForType(rec.ObjectType);

        // Invert the NASA area–length law A = 0.556945·Lc^2.0047, then mass from Lc.
        double lc = Math.Pow(area / 0.556945, 1.0 / 2.0047);
        double mass = BulkDensity(lc) * (Math.PI / 6.0) * lc * lc * lc;
        return (mass, area);
    }

    public static bool IsIntact(string type) => type is "PAY" or "R/B";

    private static double DefaultAreaForType(string type) => type switch
    {
        "R/B" => 12.0,   // rocket body
        "PAY" => 4.0,    // payload
        "DEB" => 0.3,    // fragmentation debris
        _ => 1.0,        // unknown / TBA
    };

    private static double BulkDensity(double lc) => lc < 0.08 ? 2698.9 : 92.937 * Math.Pow(lc, -0.74);

    /// <summary>Split a CSV line honoring double-quoted fields.</summary>
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>(20);
        int start = 0; bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') q = !q;
            else if (c == ',' && !q) { fields.Add(line[start..i].Trim('"')); start = i + 1; }
        }
        fields.Add(line[start..].Trim('"'));
        return fields.ToArray();
    }
}
