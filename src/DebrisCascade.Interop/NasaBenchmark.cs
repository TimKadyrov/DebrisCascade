using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DebrisCascade.Core;

namespace DebrisCascade.Interop;

/// <summary>
/// Benchmark against NASA's LEGEND "no new launches" projection (Liou &amp; Johnson 2006, Science 311:340):
/// the LEO catalog as it stood on 1 January 2006, no launches, no future explosions, no disposal manoeuvres,
/// 200 years. LEGEND found the ≥10 cm population roughly constant to ~2055, then rising; 10.8 catastrophic
/// (18.2 total) collisions in 200 years; ~60% of the catastrophic ones at 900–1,000 km.
/// </summary>
public static class NasaBenchmark
{
    public static readonly DateTime Epoch = new(2006, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The 1 Jan 2006 LEO catalog (perigee 100–2,000 km) with SATCAT-derived masses and areas.</summary>
    public static async Task<List<CatalogObject>> LoadCatalog2006Async(string dataDir)
    {
        var st = new SpaceTrackClient(dataDir);
        var tles = await st.GetHistoricalCatalogAsync(Epoch);
        var satcat = await st.GetSatcatAsync();
        var objs = new List<CatalogObject>();
        foreach (var t in tles)
        {
            var el = t.ToElementsAt(Epoch);   // every object at 1 Jan 2006, not at its own epoch
            if (!(el.PerigeeAltitude < 2000 && el.PerigeeAltitude > 100)) continue;
            double mass = 180, area = 1.78; bool intact = true; string type = "UNK";
            if (satcat.TryGetValue(t.NoradId, out var rec))
            {
                (mass, area) = Satcat.DeriveMassArea(rec); intact = Satcat.IsIntact(rec.ObjectType); type = rec.ObjectType;
            }
            objs.Add(new CatalogObject(el, mass, area, intact, type, t.Name));
        }
        return objs;
    }

    public sealed record Run(string Name, double[] Years, double[] LeoTrackable, double CatastrophicTotal,
        double NonCatastrophicTrackedTotal, double ShareAt900to1000, double[] ShellMidKm, double[] CatastrophicByShell);

    /// <summary>One 200-year projection from the 2006 catalog, nothing added and no explosions.</summary>
    public static Run Project(IReadOnlyList<CatalogObject> cat, string name, bool trackedOnly, double solarCycle,
        double smallBackground, double years = 200, bool fineMasses = true, bool eccentric = true)
    {
        var m = new KesslerEvolution(new NailSpec())
        {
            LaunchRatePerYear = 0, ExplosionsPerYear = 0, RemovalsPerYear = 0,
            TrackedOnlyCollisions = trackedOnly, SolarCycleAmplitude = solarCycle,
            FineIntactMasses = fineMasses, EccentricOrbits = eccentric,
        };
        m.SeedFromCatalog(cat, backgroundSmallTotal: smallBackground);
        var r = m.Run(years, 10);
        var byShell = m.CatastrophicByShell; var mid = m.MidAltitudesKm;
        double total = byShell.Sum();
        double at900 = Enumerable.Range(0, mid.Length).Where(s => mid[s] >= 900 && mid[s] < 1000).Sum(s => byShell[s]);
        return new Run(name, r.Years, r.TrackableObjects, total, m.NonCatastrophicTrackedTotal,
            total > 0 ? at900 / total : 0, mid, byShell);
    }

    /// <summary>The same projection on the cube engine: every object on its own orbit with its own mass and area,
    /// true encounter geometry, orbit-averaged drag. Stochastic, so several seeds.</summary>
    public static Run ProjectCube(IReadOnlyList<CatalogObject> cat, int seed, double years = 200)
    {
        var c = new ConjunctionCascade(seed) { LaunchRatePerYear = 0, ExplosionsPerYear = 0, RemovalsPerYear = 0, TrackedOnlyCollisions = true };
        c.SeedFromCatalog(cat, backgroundSmallTotal: 0);
        var r = c.Run(years, 60);
        var byShell = c.CatastrophicByShell; double total = byShell.Sum();
        var mid = Enumerable.Range(0, byShell.Length).Select(s => 225.0 + 50 * s).ToArray();
        double at900 = Enumerable.Range(0, mid.Length).Where(s => mid[s] >= 900 && mid[s] < 1000).Sum(s => byShell[s]);
        return new Run($"cube engine, seed {seed}", r.Years, r.LeoEffectiveTrackable, total, c.NonCatastrophicTrackedTotal,
            total > 0 ? at900 / total : 0, mid, byShell);
    }

    public static async Task<object> ComputeAsync(string dataDir, Action<string> log, int cubeSeeds = 0)
    {
        log("  loading the 1 Jan 2006 catalog from Space-Track history ...");
        var cat = await LoadCatalog2006Async(dataDir);
        var byType = cat.GroupBy(o => o.ObjectType).ToDictionary(g => g.Key, g => g.Count());
        log($"  {cat.Count:N0} LEO objects on 1 Jan 2006: " + string.Join(", ", byType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value:N0} {kv.Key}")));
        // All on NASA's terms (>=10 cm objects only, no launches, no explosions), then the model's own physics.
        var runs = new[]
        {
            Project(cat, "before: two intact mass classes, mean-altitude shells", true, 0, 0, fineMasses: false, eccentric: false),
            Project(cat, "finer intact masses only", true, 0, 0, fineMasses: true, eccentric: false),
            Project(cat, "eccentric orbits only", true, 0, 0, fineMasses: false, eccentric: true),
            Project(cat, "finer masses + eccentric orbits", true, 0, 0),
            Project(cat, "finer masses + eccentric orbits + 11-yr solar cycle", true, Math.Log(2), 0),
            Project(cat, "model's own physics (incl. 1-10 cm field), finer masses + eccentric", false, 0, 1_000_000),
        };
        if (cubeSeeds > 0)
        {
            log($"  cube engine, {cubeSeeds} seeds x 200 yr ...");
            runs = runs.Concat(Enumerable.Range(1, cubeSeeds).AsParallel().AsOrdered().Select(s => ProjectCube(cat, s))).ToArray();
        }
        foreach (var r in runs)
        {
            int Y(double y) => Array.FindIndex(r.Years, t => t >= y - 1e-6);
            log($"  {r.Name}:");
            log("    LEO >=10 cm: " + string.Join("  ", new[] { 0, 25, 50, 100, 150, 200 }.Select(y => $"y{y}={r.LeoTrackable[Y(y)]:N0}")));
            log($"    catastrophic collisions in 200 yr: {r.CatastrophicTotal:F1} (LEGEND 10.8); non-catastrophic between >=10 cm objects: {r.NonCatastrophicTrackedTotal:F1} (LEGEND 7.4)");
            log($"    share of catastrophic collisions at 900-1,000 km: {r.ShareAt900to1000:P0} (LEGEND ~60%)");
        }
        return new
        {
            generatedUtc = DateTime.UtcNow, epoch = Epoch, leoObjects = cat.Count, byType,
            legend = new { catastrophic200yr = 10.8, nonCatastrophic200yr = 7.4, shareAt900to1000 = 0.6, flatUntilYear = 2055,
                           source = "Liou & Johnson (2006), Science 311:340" },
            runs,
        };
    }
}
