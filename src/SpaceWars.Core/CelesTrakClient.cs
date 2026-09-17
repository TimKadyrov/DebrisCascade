using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace SpaceWars.Core;

/// <summary>
/// Fetches TLE catalogs from CelesTrak's GP endpoint with a local file cache, so a
/// study run is reproducible and works offline once the catalog is downloaded.
/// (Space-Track can be swapped in later; it needs credentials, CelesTrak does not.)
/// </summary>
public sealed class CelesTrakClient(string cacheDir)
{
    private const string BaseUrl = "https://celestrak.org/NORAD/elements/gp.php";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Common CelesTrak GROUP names useful for a LEO debris study.</summary>
    public static class Groups
    {
        public const string Active = "active";
        public const string Starlink = "starlink";
        public const string Stations = "stations";
        public const string CosmosDebris = "cosmos-1408-debris";
        public const string FengyunDebris = "1999-025"; // Fengyun-1C debris (2007 ASAT)
        public const string Last30Days = "last-30-days";
    }

    /// <summary>
    /// Return the TLEs for a CelesTrak GROUP, using a cached copy if it is younger than
    /// <paramref name="maxCacheAge"/>. Falls back to any existing cache if the network fails.
    /// </summary>
    public async Task<List<Tle>> GetGroupAsync(string group, TimeSpan? maxCacheAge = null)
    {
        maxCacheAge ??= TimeSpan.FromHours(12);
        Directory.CreateDirectory(cacheDir);
        string cachePath = Path.Combine(cacheDir, $"{group}.tle");

        bool cacheFresh = File.Exists(cachePath) &&
                          DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < maxCacheAge;

        if (!cacheFresh)
        {
            try
            {
                string url = $"{BaseUrl}?GROUP={Uri.EscapeDataString(group)}&FORMAT=tle";
                string text = await Http.GetStringAsync(url);
                if (text.Contains('1') && text.Length > 100)
                    await File.WriteAllTextAsync(cachePath, text);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (!File.Exists(cachePath))
                    throw new InvalidOperationException(
                        $"Could not download group '{group}' and no cache exists at {cachePath}.", ex);
                // else: fall through and use the stale cache.
            }
        }

        using var reader = new StreamReader(cachePath);
        return Tle.LoadMany(reader);
    }

    /// <summary>Load TLEs from a local file (offline / sample data).</summary>
    public static List<Tle> LoadFromFile(string path)
    {
        using var reader = new StreamReader(path);
        return Tle.LoadMany(reader);
    }
}
