using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace SpaceWars.Core;

/// <summary>
/// Fetches from Space-Track: the SATCAT (fuller RCS coverage than CelesTrak's public feed —
/// Space-Track populates the RCS_SIZE category SMALL/MEDIUM/LARGE for far more objects, which we
/// map to a representative cross-section) and the full on-orbit GP catalog, which — unlike
/// CelesTrak's "active" group — includes dead payloads, rocket bodies and catalogued debris.
///
/// Credentials come from the environment (SPACETRACK_USER / SPACETRACK_PASS) or a generic Windows
/// Credential Manager entry the user created; this code never prompts for, logs or stores a
/// password. Any failure (no credentials, login rejected, network) throws, and the caller falls
/// back to CelesTrak.
/// </summary>
public sealed class SpaceTrackClient(string cacheDir)
{
    private const string LoginUrl = "https://www.space-track.org/ajaxauth/login";
    private const string SatcatQuery =
        "https://www.space-track.org/basicspacedata/query/class/satcat/predicates/NORAD_CAT_ID,OBJECT_TYPE,RCS_SIZE/format/csv";
    // Space-Track's recommended "current catalog" query: every object not yet decayed, with an
    // element set from the last 30 days.
    private const string OnOrbitGpQuery =
        "https://www.space-track.org/basicspacedata/query/class/gp/decay_date/null-val/epoch/%3Enow-30/orderby/norad_cat_id/format/3le";

    private static readonly string[] CredTargets = { "SPACETRACK", "spacetrack", "www.space-track.org", "space-track.org" };

    /// <summary>Credentials from env (SPACETRACK_USER/PASS) or a generic Windows credential named SPACETRACK.</summary>
    public static (string User, string Pass)? GetCredentials()
    {
        string? u = Environment.GetEnvironmentVariable("SPACETRACK_USER");
        string? p = Environment.GetEnvironmentVariable("SPACETRACK_PASS");
        if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(p)) return (u, p);

        if (OperatingSystem.IsWindows())
            foreach (var t in CredTargets)
                if (WindowsCredential.TryRead(t, out string cu, out string cp) && cp.Length > 0)
                    return (string.IsNullOrEmpty(cu) ? (Environment.GetEnvironmentVariable("SPACETRACK_USER") ?? "") : cu, cp);
        return null;
    }

    public static bool HasCredentials => GetCredentials() is not null;

    public async Task<Dictionary<int, SatcatRecord>> GetSatcatAsync(TimeSpan? maxCacheAge = null)
    {
        maxCacheAge ??= TimeSpan.FromDays(3);
        Directory.CreateDirectory(cacheDir);
        string cachePath = Path.Combine(cacheDir, "spacetrack_satcat.csv");
        bool fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < maxCacheAge;

        if (!fresh)
        {
            using var http = await LoginAsync();
            string csv = await http.GetStringAsync(SatcatQuery);
            if (!csv.Contains("NORAD_CAT_ID"))
                throw new InvalidOperationException("Space-Track login failed or returned no data.");
            await File.WriteAllTextAsync(cachePath, csv);
        }

        using var reader = new StreamReader(cachePath);
        return ParseSatcatCsv(reader);
    }

    /// <summary>
    /// Every object currently on orbit — payloads (active and dead), rocket bodies and catalogued
    /// debris — as TLEs. Cached for a day: Space-Track asks users not to pull the full GP catalog
    /// more than about once an hour.
    /// </summary>
    public async Task<List<Tle>> GetOnOrbitCatalogAsync(TimeSpan? maxCacheAge = null)
    {
        maxCacheAge ??= TimeSpan.FromDays(1);
        Directory.CreateDirectory(cacheDir);
        string cachePath = Path.Combine(cacheDir, "spacetrack_onorbit.tle");
        bool fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < maxCacheAge;

        if (!fresh)
        {
            using var http = await LoginAsync();
            string tle = await http.GetStringAsync(OnOrbitGpQuery);
            if (tle.Length < 1000 || !tle.Contains("\n1 "))
                throw new InvalidOperationException("Space-Track login failed or returned no element sets.");
            await File.WriteAllTextAsync(cachePath, tle);
        }

        using var reader = new StreamReader(cachePath);
        return Tle.LoadMany(reader);
    }

    private static async Task<HttpClient> LoginAsync()
    {
        var creds = GetCredentials();
        if (creds is null)
            throw new InvalidOperationException("No Space-Track credentials (env SPACETRACK_USER/PASS or a generic Windows credential 'SPACETRACK').");
        (string user, string pass) = creds.Value;
        if (string.IsNullOrEmpty(user))
            throw new InvalidOperationException("Space-Track username missing (the stored credential has no username).");

        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = user, ["password"] = pass });
        var login = await http.PostAsync(LoginUrl, form);
        if (!login.IsSuccessStatusCode) { http.Dispose(); login.EnsureSuccessStatusCode(); }
        return http;
    }

    /// <summary>Parse the Space-Track satcat CSV (NORAD_CAT_ID, OBJECT_TYPE, RCS_SIZE).</summary>
    public static Dictionary<int, SatcatRecord> ParseSatcatCsv(TextReader reader)
    {
        var map = new Dictionary<int, SatcatRecord>();
        string? header = reader.ReadLine();
        if (header == null) return map;
        var cols = header.Split(',');
        int iNorad = IndexOf(cols, "NORAD_CAT_ID"), iType = IndexOf(cols, "OBJECT_TYPE"), iSize = IndexOf(cols, "RCS_SIZE");
        if (iNorad < 0) return map;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length <= iNorad) continue;
            if (!int.TryParse(f[iNorad].Trim('"', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out int norad)) continue;
            string type = NormalizeType(iType >= 0 && iType < f.Length ? f[iType].Trim('"', ' ') : "");
            string size = iSize >= 0 && iSize < f.Length ? f[iSize].Trim('"', ' ') : "";
            double? rcs = AreaFromRcsSize(size);
            // "LARGE" only says > 1 m². For a rocket body the 5 m² midpoint gives ~0.5 t, while
            // LEO upper stages are 1.4–9 t; 12 m² (the rocket-body default) gives ~1.5 t.
            if (type == "R/B" && size.Equals("LARGE", StringComparison.OrdinalIgnoreCase)) rcs = 12.0;
            map[norad] = new SatcatRecord(norad, type, rcs);
        }
        return map;
    }

    private static int IndexOf(string[] cols, string name)
    {
        for (int i = 0; i < cols.Length; i++) if (cols[i].Trim('"', ' ').Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // Space-Track uses full words; map to the codes Satcat.DeriveMassArea/IsIntact expect.
    private static string NormalizeType(string t) => t.ToUpperInvariant() switch
    {
        "PAYLOAD" => "PAY",
        "ROCKET BODY" => "R/B",
        "DEBRIS" => "DEB",
        _ => "UNK",
    };

    // Representative cross-section [m²] for the RCS_SIZE category midpoints.
    private static double? AreaFromRcsSize(string size) => size.ToUpperInvariant() switch
    {
        "SMALL" => 0.05,   // < 0.1 m²
        "MEDIUM" => 0.5,   // 0.1–1.0 m²
        "LARGE" => 5.0,    // > 1.0 m²
        _ => null,
    };
}
