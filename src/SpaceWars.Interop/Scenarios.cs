using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpaceWars.Core;

namespace SpaceWars.Interop;

/// <summary>User-tunable model inputs (shared by the CLI and the WPF UI).</summary>
public sealed class ScenarioInputs
{
    public double AltKm { get; set; } = 550;
    public double IncDeg { get; set; } = 53;
    public int NailCount { get; set; } = 200_000;
    public double NailLengthMm { get; set; } = 75;
    public double NailDiameterMm { get; set; } = 3;
    public double DispersalSigmaMS { get; set; } = 50;
    public double RelVelMS { get; set; } = 10_000;
    public double LaunchRatePerYear { get; set; } = 0;
    public double LaunchAltKm { get; set; } = 900;
    public bool Responsive { get; set; } = false;
    public double LossTolerance { get; set; } = 0.02;
    public double SolarActivity { get; set; } = 1.0;
    public double HorizonYears { get; set; } = 50;
    /// <summary>Explosions of rocket bodies and derelicts per year (0 = none); see KesslerEvolution.</summary>
    public double ExplosionsPerYear { get; set; } = 4.0;
    /// <summary>Active debris removal: large intact objects removed per year, riskiest first (0 = none).</summary>
    public double RemovalsPerYear { get; set; } = 0.0;
    /// <summary>Working satellites that hold orbit, dodge tracked objects and are deorbited at end of life
    /// (box and cube engines; see KesslerEvolution.WorkingSatellites). Off = every satellite dead from day one.</summary>
    public bool WorkingSatellites { get; set; } = false;
    public double DisposalSuccess { get; set; } = 0.90;
    public double RocketBodyDisposal { get; set; } = 0.80;
    public double AvoidanceSuccess { get; set; } = 0.90;
    public double SatelliteLifetimeYears { get; set; } = 5.0;
}

/// <summary>The loaded catalog plus its provenance and derived stats.</summary>
public sealed class CatalogBundle
{
    public required IReadOnlyList<CatalogObject> Objects { get; init; }
    public required string Source { get; init; }
    public int TotalTracked { get; init; }
    public int WithRcs { get; init; }
    /// <summary>Mean cross-section / mass of the <i>payloads</i> — the stand-in operational satellite.</summary>
    public double MeanAreaM2 { get; init; }
    public double MeanMassKg { get; init; }
    /// <summary>True when the orbits include dead payloads, rocket bodies and catalogued debris.</summary>
    public bool IncludesDebris { get; init; }
    /// <summary>LEO object counts by SATCAT type (PAY, R/B, DEB, UNK).</summary>
    public IReadOnlyDictionary<string, int> CountByType { get; init; } = new Dictionary<string, int>();
    /// <summary>LEO payloads on CelesTrak's active list — working satellites.</summary>
    public int ActiveCount { get; init; }
}

/// <summary>Orchestrates the model runs from a set of inputs — the reusable analysis layer.</summary>
public static class Scenarios
{
    private static NailSpec MakeNail(ScenarioInputs i) =>
        new() { LengthM = i.NailLengthMm / 1000.0, DiameterM = i.NailDiameterMm / 1000.0 };

    /// <summary>
    /// Load the simulation catalog (LEO: perigee 100–2000 km) with SATCAT-derived masses and areas.
    /// With <paramref name="includeDebris"/> and Space-Track credentials the orbits are every object on
    /// orbit — payloads (active and dead), rocket bodies and catalogued debris. Otherwise they are the
    /// CelesTrak <paramref name="group"/> (default "active": active satellites only), and the engines'
    /// modelled large-object belt stands in for derelicts. <paramref name="offlineFile"/> loads a local
    /// TLE file instead, with no SATCAT.
    /// </summary>
    public static async Task<CatalogBundle> LoadCatalogAsync(string dataDir, string group = "active",
        bool includeDebris = true, string? offlineFile = null)
    {
        List<Tle> tles; string orbitSrc; bool full = false;
        if (offlineFile is not null) { tles = CelesTrakClient.LoadFromFile(offlineFile); orbitSrc = $"file {System.IO.Path.GetFileName(offlineFile)}"; }
        else if (includeDebris && SpaceTrackClient.HasCredentials)
        {
            try { tles = await new SpaceTrackClient(dataDir).GetOnOrbitCatalogAsync(); orbitSrc = "Space-Track on-orbit catalog"; full = true; }
            catch (Exception ex)
            {
                tles = await new CelesTrakClient(dataDir).GetGroupAsync(group);
                orbitSrc = $"CelesTrak '{group}' (Space-Track catalog unavailable: {ex.Message})";
            }
        }
        else { tles = await new CelesTrakClient(dataDir).GetGroupAsync(group); orbitSrc = $"CelesTrak '{group}'"; }

        Dictionary<int, SatcatRecord> satcat = new(); string satSrc = "none";
        if (offlineFile is null)
        {
            try
            {
                if (SpaceTrackClient.HasCredentials) { satcat = await new SpaceTrackClient(dataDir).GetSatcatAsync(); satSrc = "Space-Track"; }
                else { satcat = await new CelesTrakClient(dataDir).GetSatcatAsync(); satSrc = "CelesTrak"; }
            }
            catch (Exception ex)
            {
                try { satcat = await new CelesTrakClient(dataDir).GetSatcatAsync(); satSrc = $"CelesTrak (Space-Track SATCAT failed: {ex.Message})"; }
                catch { satSrc = "none — default masses"; }
            }
        }

        // Which objects are working satellites: the CelesTrak active list for the full catalog; every
        // object when the orbits already are CelesTrak's "active" group.
        HashSet<int>? activeIds = null;
        if (full)
        {
            try { activeIds = (await new CelesTrakClient(dataDir).GetGroupAsync("active")).Select(t => t.NoradId).ToHashSet(); }
            catch { activeIds = new HashSet<int>(); }
        }
        bool allActive = !full && offlineFile is null && group == "active";

        int withRcs = 0;
        var objs = new List<CatalogObject>();
        foreach (var t in tles)
        {
            var el = t.ToElements();
            if (!(el.PerigeeAltitude < 2000 && el.PerigeeAltitude > 100)) continue;
            double mass = 180, area = 1.78; bool intact = true; string type = full ? "UNK" : "PAY";
            if (satcat.TryGetValue(t.NoradId, out var rec))
            {
                (mass, area) = Satcat.DeriveMassArea(rec); intact = Satcat.IsIntact(rec.ObjectType); type = rec.ObjectType;
                if (rec.RcsM2.HasValue) withRcs++;
            }
            bool active = type == "PAY" && (allActive || (activeIds?.Contains(t.NoradId) ?? false));
            objs.Add(new CatalogObject(el, mass, area, intact, type, t.Name, active));
        }
        var pay = objs.Where(o => o.ObjectType == "PAY").ToList();
        if (pay.Count == 0) pay = objs;
        return new CatalogBundle
        {
            Objects = objs, Source = $"{orbitSrc}; SATCAT: {satSrc}", TotalTracked = tles.Count, WithRcs = withRcs,
            MeanAreaM2 = pay.Count > 0 ? pay.Average(o => o.AreaM2) : 5.0,
            MeanMassKg = pay.Count > 0 ? pay.Average(o => o.MassKg) : 180.0,
            IncludesDebris = full,
            CountByType = objs.GroupBy(o => o.ObjectType).ToDictionary(g => g.Key, g => g.Count()),
            ActiveCount = objs.Count(o => o.IsActive),
        };
    }

    public static OrbitalElements[] DeployCloud(ScenarioInputs i)
    {
        double a = Constants.EarthRadiusKm + i.AltKm;
        double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        var parent = OrbitalElements.FromMeanMotionRevPerDay(rev, 0, i.IncDeg * Constants.DegToRad, 0, 0, 0);
        return new NailBarrel { Nail = MakeNail(i), Count = i.NailCount }.Deploy(parent, 0, i.DispersalSigmaMS);
    }

    public static (EvolutionResult Baseline, EvolutionResult Barrel) Evolution(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var nail = MakeNail(i);
        var b = new KesslerEvolution(nail) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear, WorkingSatellites = i.WorkingSatellites, DisposalSuccess = i.DisposalSuccess, RocketBodyDisposal = i.RocketBodyDisposal, AvoidanceSuccess = i.AvoidanceSuccess, SatelliteLifetimeYears = i.SatelliteLifetimeYears };
        b.SeedFromCatalog(cat);
        var k = new KesslerEvolution(nail) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear, WorkingSatellites = i.WorkingSatellites, DisposalSuccess = i.DisposalSuccess, RocketBodyDisposal = i.RocketBodyDisposal, AvoidanceSuccess = i.AvoidanceSuccess, SatelliteLifetimeYears = i.SatelliteLifetimeYears };
        k.SeedFromCatalog(cat); k.InjectBarrel(i.AltKm, i.NailCount);
        return (b.Run(i.HorizonYears, 10), k.Run(i.HorizonYears, 10));
    }

    public static (CascadeResult Baseline, CascadeResult Barrel) Cascade(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var nail = MakeNail(i); var cloud = DeployCloud(i);
        var b = new DiscreteCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear };
        b.SeedFromCatalog(cat);
        var k = new DiscreteCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear };
        k.SeedFromCatalog(cat); k.InjectBarrel(cloud, nail, 3000, i.NailCount);
        return (b.Run(i.HorizonYears, 15), k.Run(i.HorizonYears, 15));
    }

    public static (CascadeResult Baseline, CascadeResult Barrel, bool UsedGpu) Conjunction(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var nail = MakeNail(i); var cloud = DeployCloud(i);
        var b = new ConjunctionCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear, WorkingSatellites = i.WorkingSatellites, DisposalSuccess = i.DisposalSuccess, RocketBodyDisposal = i.RocketBodyDisposal, AvoidanceSuccess = i.AvoidanceSuccess, SatelliteLifetimeYears = i.SatelliteLifetimeYears };
        b.SeedFromCatalog(cat);
        var k = new ConjunctionCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear, WorkingSatellites = i.WorkingSatellites, DisposalSuccess = i.DisposalSuccess, RocketBodyDisposal = i.RocketBodyDisposal, AvoidanceSuccess = i.AvoidanceSuccess, SatelliteLifetimeYears = i.SatelliteLifetimeYears };
        k.SeedFromCatalog(cat); k.InjectBarrel(cloud, nail, 3000, i.NailCount);
        var br = b.Run(i.HorizonYears, 60); var kr = k.Run(i.HorizonYears, 60);
        return (br, kr, b.UsedGpu);
    }

    // ---------------------------------------------------------------------------------------------
    // The deck's analyses, driven by the UI inputs. The metric throughout is the deck's: debris ≥10 cm
    // in the 700–1,100 km belt after the horizon, over today's belt objects ≥10 cm (working satellites
    // aren't debris, so a working run's own start count is lower; today's count is the common yardstick).
    // ---------------------------------------------------------------------------------------------

    /// <summary>A box model built from the inputs; any argument given overrides the matching input.</summary>
    public static KesslerEvolution Box(IReadOnlyList<CatalogObject> cat, ScenarioInputs i, double? launches = null,
        bool? working = null, double? removals = null, double? disposal = null, double? rocketBodies = null,
        double? avoidance = null, bool? responsive = null)
    {
        var m = new KesslerEvolution(MakeNail(i))
        {
            SolarActivity = i.SolarActivity, LaunchRatePerYear = launches ?? i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm,
            ResponsiveLaunch = responsive ?? i.Responsive, LossTolerancePerYear = i.LossTolerance,
            ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = removals ?? i.RemovalsPerYear,
            WorkingSatellites = working ?? i.WorkingSatellites, DisposalSuccess = disposal ?? i.DisposalSuccess,
            RocketBodyDisposal = rocketBodies ?? i.RocketBodyDisposal, AvoidanceSuccess = avoidance ?? i.AvoidanceSuccess,
            SatelliteLifetimeYears = i.SatelliteLifetimeYears,
        };
        m.SeedFromCatalog(cat);
        return m;
    }

    /// <summary>Today's belt objects ≥10 cm (700–1,100 km), every satellite counted — the growth yardstick.</summary>
    public static double BeltToday(IReadOnlyList<CatalogObject> cat, ScenarioInputs i) =>
        Box(cat, i, working: false).TotalTrackable(700, 1100);

    private static double BeltEnd(KesslerEvolution m, ScenarioInputs i) => m.Run(i.HorizonYears, 10).BeltTrackableObjects[^1];

    /// <summary>Belt growth vs objects added a year at the launch altitude — dead from day one, and (when the
    /// working-satellite box is ticked) as working satellites with the chosen disposal and avoidance.</summary>
    public static (double[] Rates, double[] NeverDeorbited, double[]? Working, double BeltToday) Tipping(
        IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        double[] rates = { 0, 25, 50, 100, 150, 200, 300, 400, 500, 700, 1000 };
        double today = BeltToday(cat, i);
        double[] Curve(bool working) => rates.AsParallel().AsOrdered()
            .Select(r => BeltEnd(Box(cat, i, launches: r, working: working, responsive: false), i) / today).ToArray();
        return (rates, Curve(false), i.WorkingSatellites ? Curve(true) : null, today);
    }

    /// <summary>Scale check: the barrel's effect on the belt (≥10 cm) and on all objects (incl. the 1–10 cm
    /// field) vs number of barrels at the release altitude, and one ASAT strike (1 t, 865 km) for scale.</summary>
    public static (double[] Barrels, double[] BeltRatio, double[] AllRatio, double AsatBeltRatio, double AsatAllRatio) Barrels(
        IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        double[] counts = { 1, 3, 10, 30, 100, 300, 1000 };
        (double Belt, double All) End(KesslerEvolution m) { var r = m.Run(i.HorizonYears, 10); return (r.BeltTrackableObjects[^1], r.TotalObjects[^1]); }
        var baseEnd = End(Box(cat, i));
        var runs = counts.AsParallel().AsOrdered().Select(k => { var m = Box(cat, i); m.InjectBarrel(i.AltKm, k * i.NailCount); return End(m); }).ToArray();
        var asatM = Box(cat, i); asatM.InjectBreakup(865, 1000, 10); var asat = End(asatM);
        return (counts, runs.Select(r => r.Belt / baseEnd.Belt).ToArray(), runs.Select(r => r.All / baseEnd.All).ToArray(),
                asat.Belt / baseEnd.Belt, asat.All / baseEnd.All);
    }

    /// <summary>Every source on one measure: extra belt objects ≥10 cm after the horizon, vs adding nothing.</summary>
    public static (string Label, double Extra, string Kind)[] Comparison(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        double baseEnd = BeltEnd(Box(cat, i, launches: 0, working: false), i);
        var jobs = new List<(string Label, string Kind, Func<double>)>
        {
            ($"1 barrel of nails ({i.AltKm:F0} km)", "barrel", () => { var m = Box(cat, i, launches: 0, working: false); m.InjectBarrel(i.AltKm, i.NailCount); return BeltEnd(m, i); }),
            ("1 ASAT strike on a 1 t satellite", "asat", () => { var m = Box(cat, i, launches: 0, working: false); m.InjectBreakup(865, 1000, 10); return BeltEnd(m, i); }),
            ($"1,000 barrels ({i.AltKm:F0} km, the ceiling)", "barrel", () => { var m = Box(cat, i, launches: 0, working: false); m.InjectBarrel(i.AltKm, 1000.0 * i.NailCount); return BeltEnd(m, i); }),
            ("50 a year, never deorbited", "traffic", () => BeltEnd(Box(cat, i, launches: 50, working: false, responsive: false), i)),
            ("500 a year, never deorbited", "traffic", () => BeltEnd(Box(cat, i, launches: 500, working: false, responsive: false), i)),
        };
        if (i.WorkingSatellites)
            jobs.Add(("500 a year, working + deorbited", "working", () => BeltEnd(Box(cat, i, launches: 500, working: true, responsive: false), i)));
        return jobs.AsParallel().AsOrdered().Select(j => (j.Label, j.Item3() - baseEnd, j.Kind)).ToArray();
    }

    /// <summary>Operators quit: belt ≥10 cm over time at the launch rate (500/yr if 0), constant vs responsive.</summary>
    public static (double[] Years, double[] Constant, double[] Responsive, double QuitYear, double Rate) Operators(
        IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        double rate = i.LaunchRatePerYear > 0 ? i.LaunchRatePerYear : 500;
        var c = Box(cat, i, launches: rate, responsive: false).Run(i.HorizonYears, 10);
        var r = Box(cat, i, launches: rate, responsive: true).Run(i.HorizonYears, 10);
        int q = Array.FindIndex(r.LaunchFraction, f => f <= 1e-9);
        return (c.Years, c.BeltTrackableObjects, r.BeltTrackableObjects, q >= 0 ? r.Years[q] : double.NaN, rate);
    }

    /// <summary>Removal: belt growth vs large dead objects removed a year (riskiest first), with nothing added
    /// and with the launch rate added (50/yr if 0).</summary>
    public static (double[] Removals, double[] NothingAdded, double[] WithLaunches, double Rate, double BeltToday) Removal(
        IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        double[] rem = { 0, 2, 5, 10, 20, 50, 100 };
        double rate = i.LaunchRatePerYear > 0 ? i.LaunchRatePerYear : 50, today = BeltToday(cat, i);
        double[] Curve(double launches) => rem.AsParallel().AsOrdered()
            .Select(k => BeltEnd(Box(cat, i, launches: launches, removals: k, responsive: false), i) / today).ToArray();
        return (rem, Curve(0), Curve(rate), rate, today);
    }

    /// <summary>Working satellites: belt growth at 50 and 500 a year (plus the input rate) for the deck's four
    /// settings and the user's own (disposal / rocket bodies / avoidance).</summary>
    public static (double[] Rates, (string Name, double Pmd, double Rb, double Avoid)[] Settings, double[,] Growth) WorkingSweep(
        IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var rates = new List<double> { 50, 500 };
        if (i.LaunchRatePerYear > 0 && !rates.Contains(i.LaunchRatePerYear)) rates.Add(i.LaunchRatePerYear);
        var settings = new (string Name, double Pmd, double Rb, double Avoid)[]
        {
            ("never deorbited", -1, -1, -1), ("poor", 0.70, 0.50, 0.50), ("today's practice", 0.90, 0.80, 0.90),
            ("best", 0.99, 0.95, 0.99),
            ($"your settings ({i.DisposalSuccess:P0} / {i.RocketBodyDisposal:P0} / {i.AvoidanceSuccess:P0})", i.DisposalSuccess, i.RocketBodyDisposal, i.AvoidanceSuccess),
        };
        double today = BeltToday(cat, i);
        var g = new double[rates.Count, settings.Length];
        var cells = from r in Enumerable.Range(0, rates.Count) from s in Enumerable.Range(0, settings.Length) select (r, s);
        foreach (var (r, s, v) in cells.AsParallel().Select(c =>
        {
            var st = settings[c.s]; bool on = st.Pmd >= 0;
            var m = Box(cat, i, launches: rates[c.r], working: on, responsive: false,
                        disposal: on ? st.Pmd : null, rocketBodies: on ? st.Rb : null, avoidance: on ? st.Avoid : null);
            return (c.r, c.s, BeltEnd(m, i) / today);
        }).ToList())
            g[r, s] = v;
        return (rates.ToArray(), settings, g);
    }

    /// <summary>How long drag takes to remove a 3–10 cm collision fragment at each altitude (years, capped 10⁴).</summary>
    public static double[] FragmentLifetimeYears(double[] altKm, double solarActivity)
    {
        double am = BreakupModel.FragmentAreaToMass(0.0562);
        return altKm.Select(a => { double d = AtmosphericDrag.LifetimeDays(a, am, solarActivity); return double.IsFinite(d) ? Math.Min(d / 365.25, 1e4) : 1e4; }).ToArray();
    }

    /// <summary>
    /// Per-satellite annual collision probability vs altitude, now and after the horizon.
    /// Above the loss tolerance the orbit is effectively unusable.
    /// </summary>
    public static (double[] AltKm, double[] Years, double[][] HazardByYear, double Threshold) UsabilityOverTime(
        IReadOnlyList<CatalogObject> cat, ScenarioInputs i, double satAreaM2)
    {
        var nail = MakeNail(i);
        var m = new KesslerEvolution(nail) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance, ExplosionsPerYear = i.ExplosionsPerYear, RemovalsPerYear = i.RemovalsPerYear, WorkingSatellites = i.WorkingSatellites, DisposalSuccess = i.DisposalSuccess, RocketBodyDisposal = i.RocketBodyDisposal, AvoidanceSuccess = i.AvoidanceSuccess, SatelliteLifetimeYears = i.SatelliteLifetimeYears };
        m.SeedFromCatalog(cat);
        m.InjectBarrel(i.AltKm, i.NailCount);

        var alt = m.MidAltitudesKm;
        int H = Math.Max(1, (int)Math.Round(i.HorizonYears));
        var years = new double[H + 1];
        var haz = new double[H + 1][];
        haz[0] = m.SatelliteHazardByShell(satAreaM2, i.RelVelMS);

        // Step to each year boundary exactly (10-day steps, last one shortened) — stepping
        // "while t < 1 yr" in fixed 10-day steps ran 370-day years.
        double dtSec = 10 * Constants.SecondsPerDay, yearSec = 365.25 * Constants.SecondsPerDay, simSec = 0;
        for (int y = 1; y <= H; y++)
        {
            while (simSec < y * yearSec - 1e-3)
            {
                double d = Math.Min(dtSec, y * yearSec - simSec);
                m.Step(d); simSec += d;
            }
            years[y] = y;
            haz[y] = m.SatelliteHazardByShell(satAreaM2, i.RelVelMS);
        }
        return (alt, years, haz, i.LossTolerance);
    }

    /// <summary>Single-nail lethality + spatial-density flux summary as text.</summary>
    public static string LethalityAndFlux(IReadOnlyList<CatalogObject> cat, ScenarioInputs i, double targetAreaM2)
    {
        var nail = MakeNail(i);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Nail: {i.NailLengthMm:F0}×{i.NailDiameterMm:F1} mm → {nail.MassKg * 1000:F2} g, σ={nail.MeanCrossSectionM2 * 1e4:F2} cm², Lc={nail.CharacteristicLengthM * 100:F1} cm, A/m={nail.AreaToMassRatio:F3} m²/kg");
        sb.AppendLine($"Barrel: {i.NailCount:N0} nails = {i.NailCount * nail.MassKg:N0} kg at {i.AltKm:F0} km / {i.IncDeg:F0}°");
        foreach (var solar in new[] { ("solar min", 0.5), ("nominal", 1.0), ("solar max", 4.0) })
        {
            double d = AtmosphericDrag.LifetimeDays(i.AltKm, nail.AreaToMassRatio, solar.Item2);
            sb.AppendLine($"  lifetime ({solar.Item1}): {(double.IsInfinity(d) ? ">1000 yr" : $"{d / 365.25:F1} yr")}");
        }
        sb.AppendLine();
        foreach (double v in new[] { 7_600.0, 10_000.0, 15_200.0 })
            sb.AppendLine($"  @ {v / 1000,5:F1} km/s: shatters targets ≤ {Lethality.MaxCatastrophicTargetMassKg(nail.MassKg, v):F1} kg");
        sb.AppendLine();

        var cloud = DeployCloud(i);
        var flux = new SpatialDensityFlux { RelVelMetersPerSec = i.RelVelMS, TargetAreaM2 = targetAreaM2 };
        var shells = flux.Analyze(cat.Select(o => o.Elements).ToList(), cloud, nail);
        double total = SpatialDensityFlux.TotalCollisionsPerYear(shells);
        sb.AppendLine($"Expected nail mission-kills across LEO: {total:F1} / year (target area {targetAreaM2:F2} m²)");
        var peak = shells.Where(s => s.NailCount > 0).OrderByDescending(s => s.NailImpactsPerSatPerYear).FirstOrDefault();
        if (peak is not null)
            sb.AppendLine($"Peak shell {peak.AltLowKm:F0}-{peak.AltHighKm:F0} km: {peak.NailCount:N0} nails, {peak.CatalogCount:N0} sats");
        return sb.ToString();
    }
}
