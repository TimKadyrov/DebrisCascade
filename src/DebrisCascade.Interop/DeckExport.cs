using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DebrisCascade.Core;

namespace DebrisCascade.Interop;

/// <summary>
/// Every number and chart series the presentation quotes, computed in one run (CLI <c>--deck</c> →
/// data/deck_numbers.json).
///
/// Scenario outcomes come from the cube engine — every object on its own orbit with its own catalogued mass
/// and area, true encounter geometry — as seed ensembles (mean, min, max). It is the engine that reproduces
/// NASA's LEGEND benchmark (<see cref="NasaBenchmark"/>). Effects too small for seed scatter to resolve (one
/// barrel, one ASAT strike, the barrel ceiling) and the instantaneous hazard charts come from the deterministic
/// box model, which also cross-checks every cube headline.
/// </summary>
public static class DeckExport
{
    private const double Horizon = 50, Dt = 10, BeltAltKm = 900;
    public const int Seeds = 16;
    private static readonly (string Key, string Label, double MinLc)[] Conventions =
    {
        ("asWritten", "breakup model as written", 0.0),
        ("legend", "LEGEND convention (>=10 cm projectiles crater)", 0.1),
    };

    /// <summary>A scenario: traffic, operator behaviour, explosions, removal.</summary>
    private sealed record Spec(double Launches = 0, bool Responsive = false, double Removals = 0, double Explosions = 4,
        double ExplScale = 0.25, bool Working = false, double Pmd = 0.9, double Rb = 0.8, double Avoid = 0.9);

    /// <summary>A cube-engine seed ensemble: mean yearly series, and each seed's belt growth over today.</summary>
    private sealed record Ens(double[] Years, double[] Belt, double[] Trackable, double[] Total, double[] CatPerYear,
        double[] LaunchFraction, double[] Working, double[] Growth, double[] QuitYears)
    {
        public double Mean => Growth.Average();
        public double Min => Growth.Min();
        public double Max => Growth.Max();
    }

    public static object Compute(CatalogBundle bundle, NailSpec nail, int nailsPerBarrel, Action<string>? log = null)
    {
        log ??= _ => { };
        var cat = bundle.Objects;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---------------------------------------------------------------- cube ensembles -------------
        var cache = new ConcurrentDictionary<Spec, Ens>();
        var todayBySeed = new ConcurrentDictionary<int, double>();
        double Today(int seed) => todayBySeed.GetOrAdd(seed, s => { var c = new ConjunctionCascade(s); c.SeedFromCatalog(cat); return c.TotalTrackable(700, 1100); });

        // Disposal and avoidance mean nothing with working satellites off: one cache key for all such specs.
        Ens Cube(Spec s) => cache.GetOrAdd(s.Working ? s : s with { Pmd = 0.9, Rb = 0.8, Avoid = 0.9 }, spec =>
        {
            var runs = new CascadeResult[Seeds]; var growth = new double[Seeds]; var quit = new double[Seeds];
            Parallel.For(0, Seeds, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Seeds, Environment.ProcessorCount) }, k =>
            {
                int seed = k + 1;
                var c = new ConjunctionCascade(seed)
                {
                    LaunchRatePerYear = spec.Launches, LaunchAltKm = BeltAltKm, ResponsiveLaunch = spec.Responsive, LossTolerancePerYear = 0.02,
                    RemovalsPerYear = spec.Removals, ExplosionsPerYear = spec.Explosions, ExplosionScale = spec.ExplScale,
                    WorkingSatellites = spec.Working, DisposalSuccess = spec.Pmd, RocketBodyDisposal = spec.Rb, AvoidanceSuccess = spec.Avoid,
                };
                c.SeedFromCatalog(cat);
                var r = c.Run(Horizon, 60);
                runs[k] = r; growth[k] = r.BeltTrackableObjects[^1] / Today(seed);
                int q = Array.FindIndex(r.LaunchFraction, f => f <= 1e-6);
                quit[k] = spec.Responsive && q >= 0 ? r.Years[q] : double.NaN;
            });
            double[] Mean(Func<CascadeResult, double[]> f) =>
                Enumerable.Range(0, runs[0].Years.Length).Select(t => runs.Average(r => t < f(r).Length ? f(r)[t] : 0)).ToArray();
            log($"    cube x{Seeds}: {Describe(spec)} -> x{growth.Average():F2} (x{growth.Min():F2}-x{growth.Max():F2})  [{sw.Elapsed.TotalMinutes:F1} min]");
            return new Ens(runs[0].Years, Mean(r => r.BeltTrackableObjects), Mean(r => r.TrackableObjects), Mean(r => r.TotalObjects),
                Mean(r => r.CatastrophicPerYear), Mean(r => r.LaunchFraction), Mean(r => r.WorkingSatellites), growth, quit);
        });
        static string Describe(Spec s) => $"{s.Launches}/yr{(s.Responsive ? " responsive" : "")}{(s.Removals > 0 ? $", {s.Removals} removed/yr" : "")}" +
            $"{(s.Explosions != 4 || s.ExplScale != 0.25 ? $", expl {s.Explosions}x{s.ExplScale}" : "")}{(s.Working ? $", working {s.Pmd}/{s.Rb}/{s.Avoid}" : "")}";

        // ---------------------------------------------------------------- box model --------------------
        KesslerEvolution Box(double minLc = 0, double rate = 0, bool responsive = false, double removals = 0, double explosions = 4,
            double explScale = 0.25, bool working = false, double pmd = 0.9, double rb = 0.8, double avoid = 0.9)
        {
            var m = new KesslerEvolution(nail)
            {
                CrateringEjectaMinLcM = minLc, LaunchRatePerYear = rate, LaunchAltKm = BeltAltKm, ResponsiveLaunch = responsive,
                LossTolerancePerYear = 0.02, RemovalsPerYear = removals, ExplosionsPerYear = explosions, ExplosionScale = explScale,
                WorkingSatellites = working, DisposalSuccess = pmd, RocketBodyDisposal = rb, AvoidanceSuccess = avoid,
            };
            m.SeedFromCatalog(cat);
            return m;
        }
        double boxToday; { boxToday = Box().TotalTrackable(700, 1100); }
        double BoxGrowth(KesslerEvolution m) => m.Run(Horizon, Dt).BeltTrackableObjects[^1] / boxToday;
        (double All, double Trk, double Belt) End(KesslerEvolution m) { var r = m.Run(Horizon, Dt); return (r.TotalObjects[^1], r.TrackableObjects[^1], r.BeltTrackableObjects[^1]); }

        // ---------------------------------------------------------------- catalog & physics ------------
        var catalog = new
        {
            leoObjects = cat.Count, totalTracked = bundle.TotalTracked, withRcs = bundle.WithRcs, source = bundle.Source,
            includesDebris = bundle.IncludesDebris, countByType = bundle.CountByType,
            payloadMeanMassKg = bundle.MeanMassKg, payloadMeanAreaM2 = bundle.MeanAreaM2,
            catalogMassTonnes = cat.Sum(o => o.MassKg) / 1000,
            modelledLargeBelt = DebrisEnvironment.LargeBeltFor(cat),
            modelledSmallDebris1to10cm = 1_000_000,
            beltTodayCube = Enumerable.Range(1, Seeds).Average(Today), beltTodayBox = boxToday,
        };
        var physics = new
        {
            nailMassG = nail.MassKg * 1000, nailAreaCm2 = nail.MeanCrossSectionM2 * 1e4,
            nailEmrVs260kgJPerG = Lethality.Emr(nail.MassKg, 10_000, 260),
            nailFragmentsVs260kg = BreakupModel.Fragment(260, nail.MassKg, 10_000).CountLargerThan1cm,
            nailFragmentsVs3kg = BreakupModel.Fragment(3, nail.MassKg, 10_000).CountLargerThan1cm,
            sat260vs260Fragments1cm = BreakupModel.Fragment(260, 260, 10_000).CountLargerThan1cm,
            sat260vs260Fragments10cm = BreakupModel.Fragment(260, 260, 10_000).CountLargerThan10cm,
        };

        // ---------------------------------------------------------------- cube headline scenarios ------
        log($"  cube engine, {Seeds} seeds per scenario:");
        var baseEns = Cube(new Spec());
        double[] rates = { 0, 25, 50, 100, 150, 200, 300, 400, 500, 700, 1000 };
        var tipEns = rates.Select(r => Cube(new Spec(Launches: r))).ToArray();
        var cst = Cube(new Spec(Launches: 500)); var rsp = Cube(new Spec(Launches: 500, Responsive: true));
        // Median over every seed: a seed whose operators never quit counts as +infinity, and a median of +infinity
        // is reported as -1 (never).
        var quits = rsp.QuitYears.Select(x => double.IsFinite(x) ? x : double.PositiveInfinity).OrderBy(x => x).ToArray();
        double quitYear = double.IsFinite(quits[quits.Length / 2]) ? quits[quits.Length / 2] : -1;
        int qi = quitYear >= 0 ? Array.FindIndex(rsp.Years, t => t >= quitYear - 1e-9) : -1;
        var explSpecs = new[] { (0.0, 0.25), (4.0, 0.25), (10.0, 0.25), (4.0, 1.0) };
        var explEns = explSpecs.Select(x => Cube(new Spec(Explosions: x.Item1, ExplScale: x.Item2))).ToArray();
        double[] removalRates = { 0, 2, 5, 10, 20, 50, 100 };
        var remNone = removalRates.Select(k => Cube(new Spec(Removals: k))).ToArray();
        var rem50 = removalRates.Select(k => Cube(new Spec(Launches: 50, Removals: k))).ToArray();
        var remNoExpl = removalRates.Select(k => Cube(new Spec(Removals: k, Explosions: 0))).ToArray();
        static double HoldFlat(double[] x, double[] g)
        {
            if (g[0] <= 1) return 0;
            for (int k = 1; k < x.Length; k++) if (g[k] <= 1) return x[k - 1] + (g[k - 1] - 1) / (g[k - 1] - g[k]) * (x[k] - x[k - 1]);
            return -1;
        }
        var settings = new (string Name, bool On, double Pmd, double Rb, double Avoid)[]
        {
            ("neverDeorbited", false, 0, 0, 0), ("poor", true, 0.70, 0.50, 0.50),
            ("baseline", true, 0.90, 0.80, 0.90), ("best", true, 0.99, 0.95, 0.99),
        };
        var workingRates = new[] { 0.0, 50, 500 };
        var workEns = settings.Select(st => workingRates.Select(r => Cube(new Spec(Launches: r, Working: st.On, Pmd: st.Pmd, Rb: st.Rb, Avoid: st.Avoid))).ToArray()).ToArray();
        var disposalOnly = Cube(new Spec(Launches: 500, Working: true, Pmd: 0.9, Rb: 0.8, Avoid: 0));
        var avoidanceOnly = Cube(new Spec(Launches: 500, Working: true, Pmd: 0, Rb: 0, Avoid: 0.9));

        // ---------------------------------------------------------------- box: small effects, both conventions
        log("  box model: barrels, ASAT (both cratering conventions) and cross-checks ...");
        var perConv = new Dictionary<string, object>();
        foreach (var (key, label, minLc) in Conventions)
        {
            var baseRun = Box(minLc).Run(Horizon, Dt);
            double bEnd = baseRun.TotalObjects[^1], tEnd = baseRun.TrackableObjects[^1], kEnd = baseRun.BeltTrackableObjects[^1];
            (double All, double Trk, double Belt) BarrelRatio(double k, double alt = BeltAltKm)
            {
                var m = Box(minLc); m.InjectBarrel(alt, k * nailsPerBarrel); var e = End(m);
                return (e.All / bEnd, e.Trk / tEnd, e.Belt / kEnd);
            }
            double[] counts = { 1, 3, 10, 30, 100, 300, 1000 };
            var barrel = counts.AsParallel().AsOrdered().Select(k => BarrelRatio(k)).ToArray();
            double BarrelsToDouble(Func<(double All, double Trk, double Belt), double> metric)
            {
                double klo = 1, khi = 30_000;
                if (metric(BarrelRatio(khi)) < 2) return double.PositiveInfinity;
                for (int it = 0; it < 20; it++) { double mid = Math.Sqrt(klo * khi); if (metric(BarrelRatio(mid)) >= 2) khi = mid; else klo = mid; }
                return Math.Sqrt(klo * khi);
            }
            double toDoubleAll = BarrelsToDouble(r => r.All), toDoubleTrk = BarrelsToDouble(r => r.Trk), toDoubleBelt = BarrelsToDouble(r => r.Belt);
            var asatM = Box(minLc); asatM.InjectBreakup(865, 1000, 10); var asatEnd = End(asatM);
            double asatAll = asatEnd.All / bEnd, asatTrk = asatEnd.Trk / tEnd, asatBelt = asatEnd.Belt / kEnd;
            double Equiv(double asat, double oneBarrel) => (asat - 1) / Math.Max(1e-12, oneBarrel - 1);
            bool headline = key == "asWritten";

            perConv[key] = new
            {
                label,
                // Headline scenario outcomes (cube ensembles) under the as-written convention; the LEGEND convention
                // entry carries only the box model's barrels and ASAT (its belt results are the same).
                baseline = headline ? (object)new
                {
                    years = baseEns.Years, total = baseEns.Total, trackable = baseEns.Trackable, beltTrackable = baseEns.Belt,
                    growthAll = baseEns.Total[^1] / baseEns.Total[0], growthTrackable = baseEns.Trackable[^1] / baseEns.Trackable[0],
                    growthBelt = baseEns.Mean, growthBeltMin = baseEns.Min, growthBeltMax = baseEns.Max,
                    boxGrowthBelt = kEnd / baseRun.BeltTrackableObjects[0],
                } : new { boxGrowthBelt = kEnd / baseRun.BeltTrackableObjects[0] },
                tipping = headline ? (object)new
                {
                    rates,
                    growthBelt = tipEns.Select(e => e.Mean).ToArray(), growthBeltMin = tipEns.Select(e => e.Min).ToArray(), growthBeltMax = tipEns.Select(e => e.Max).ToArray(),
                    growthTrackable = tipEns.Select(e => e.Trackable[^1] / e.Trackable[0]).ToArray(),
                    growthAll = tipEns.Select(e => e.Total[^1] / e.Total[0]).ToArray(),
                    catastrophicYear50 = tipEns.Select(e => e.CatPerYear[^1]).ToArray(),
                    boxGrowthBelt = rates.AsParallel().AsOrdered().Select(r => BoxGrowth(Box(minLc, r))).ToArray(),
                } : null,
                responsive500 = headline ? (object)new
                {
                    years = cst.Years, constant = cst.Total, constantTrackable = cst.Trackable, constantBelt = cst.Belt, constantCatPerYear = cst.CatPerYear,
                    constantGrowthBelt = cst.Mean, constantGrowthBeltMin = cst.Min, constantGrowthBeltMax = cst.Max,
                    responsive = rsp.Total, responsiveTrackable = rsp.Trackable, responsiveBelt = rsp.Belt, throttle = rsp.LaunchFraction,
                    operatorsQuitYear = quitYear, operatorsQuitYears = rsp.QuitYears.Select(x => double.IsFinite(x) ? x : -1).ToArray(),
                    growthAfterQuitBelt = qi >= 0 ? rsp.Belt[^1] / rsp.Belt[qi] : 1,
                } : null,
                barrels = new
                {
                    engine = "box", counts, ratioAll = barrel.Select(r => r.All).ToArray(), ratioTrackable = barrel.Select(r => r.Trk).ToArray(), ratioBelt = barrel.Select(r => r.Belt).ToArray(),
                    barrelsToDoubleAll = double.IsFinite(toDoubleAll) ? toDoubleAll : -1,
                    barrelsToDoubleTrackable = double.IsFinite(toDoubleTrk) ? toDoubleTrk : -1,
                    barrelsToDoubleBelt = double.IsFinite(toDoubleBelt) ? toDoubleBelt : -1,
                    oneAt550All = BarrelRatio(1, 550).All,
                },
                asat = new
                {
                    engine = "box", altKm = 865, targetKg = 1000, projectileKg = 10, ratioAll = asatAll, ratioTrackable = asatTrk, ratioBelt = asatBelt,
                    barrelEquivalentsAll = Equiv(asatAll, barrel[0].All),
                    barrelEquivalentsTrackable = Equiv(asatTrk, barrel[0].Trk),
                    barrelEquivalentsBelt = Equiv(asatBelt, barrel[0].Belt),
                },
            };
        }

        // One measure for every source (box model, deterministic): extra belt objects after 50 yr vs adding nothing.
        double boxBase = Box().Run(Horizon, Dt).BeltTrackableObjects[^1];
        double Extra(Action<KesslerEvolution> setup, double launches = 0)
        {
            var m = Box(rate: launches); setup(m); return m.Run(Horizon, Dt).BeltTrackableObjects[^1] - boxBase;
        }
        var comparison = new
        {
            engine = "box",
            oneBarrel = Extra(m => m.InjectBarrel(BeltAltKm, nailsPerBarrel)),
            asat = Extra(m => m.InjectBreakup(865, 1000, 10)),
            barrelCeiling = new[] { 300.0, 1000.0 }.Max(k => Extra(m => m.InjectBarrel(BeltAltKm, k * nailsPerBarrel))),
            traffic50 = Extra(_ => { }, 50), traffic500 = Extra(_ => { }, 500),
            barrelOnTop500 = Box(rate: 500).Run(Horizon, Dt).BeltTrackableObjects[^1] is var t500
                ? (Extra(m => m.InjectBarrel(BeltAltKm, nailsPerBarrel), 500) + boxBase - t500) / t500 : 0,
        };

        // ---------------------------------------------------------------- explosions, removal, working --
        var explosions = explSpecs.Select((x, k) => new
        {
            perYear = x.Item1, scale = x.Item2,
            growthBelt = explEns[k].Mean, growthBeltMin = explEns[k].Min, growthBeltMax = explEns[k].Max,
            growthTrackable = explEns[k].Trackable[^1] / explEns[k].Trackable[0], growthAll = explEns[k].Total[^1] / explEns[k].Total[0],
        }).ToArray();

        var removal = new
        {
            removalsPerYear = removalRates,
            beltGrowthNoLaunches = remNone.Select(e => e.Mean).ToArray(), beltGrowthNoLaunchesMin = remNone.Select(e => e.Min).ToArray(), beltGrowthNoLaunchesMax = remNone.Select(e => e.Max).ToArray(),
            beltGrowthAt50Launches = rem50.Select(e => e.Mean).ToArray(), beltGrowthAt50LaunchesMin = rem50.Select(e => e.Min).ToArray(), beltGrowthAt50LaunchesMax = rem50.Select(e => e.Max).ToArray(),
            beltGrowthNoLaunchesNoExplosionsSweep = remNoExpl.Select(e => e.Mean).ToArray(),
            toHoldFlatNoLaunches = HoldFlat(removalRates, remNone.Select(e => e.Mean).ToArray()),
            toHoldFlatAt50Launches = HoldFlat(removalRates, rem50.Select(e => e.Mean).ToArray()),
            toHoldFlatNoLaunchesNoExplosions = HoldFlat(removalRates, remNoExpl.Select(e => e.Mean).ToArray()),
            beltGrowthNoLaunchesNoExplosions = remNoExpl[0].Mean,
            box = new
            {
                beltGrowthNoLaunches = removalRates.AsParallel().AsOrdered().Select(k => BoxGrowth(Box(removals: k))).ToArray(),
                beltGrowthAt50Launches = removalRates.AsParallel().AsOrdered().Select(k => BoxGrowth(Box(rate: 50, removals: k))).ToArray(),
            },
        };

        var working = new
        {
            beltToday = catalog.beltTodayCube, activeSatellitesLeo = bundle.ActiveCount, launchAltKm = BeltAltKm, rates = workingRates,
            lifetimeYears = new KesslerEvolution(nail).SatelliteLifetimeYears, manoeuvrableFraction = new KesslerEvolution(nail).ManoeuvrableFraction,
            sweep = settings.Select((st, i) => new
            {
                setting = st.Name, disposalSuccess = st.Pmd, rocketBodyDisposal = st.Rb, avoidanceSuccess = st.Avoid,
                runs = workEns[i].Select((e, r) => new
                {
                    growth = e.Mean, growthMin = e.Min, growthMax = e.Max, catastrophicYear50 = e.CatPerYear[^1], workingEnd = e.Working[^1],
                    boxGrowth = BoxGrowth(Box(rate: workingRates[r], working: st.On, pmd: st.Pmd, rb: st.Rb, avoid: st.Avoid)),
                }).ToArray(),
            }).ToArray(),
            levers500 = new
            {
                disposalOnly = new { growth = disposalOnly.Mean, growthMin = disposalOnly.Min, growthMax = disposalOnly.Max },
                avoidanceOnly = new { growth = avoidanceOnly.Mean, growthMin = avoidanceOnly.Min, growthMax = avoidanceOnly.Max },
            },
        };

        // Cross-checks of the cube headlines by the other two engines.
        log("  cross-checks: discrete engine at 50 launches/yr and 10 removals/yr ...");
        object CrossCheck(double launches, double removals)
        {
            var dis = Enumerable.Range(1, 8).AsParallel().AsOrdered().Select(s =>
            {
                var d = new DiscreteCascade(seed: s) { LaunchRatePerYear = launches, LaunchAltKm = BeltAltKm, RemovalsPerYear = removals };
                d.SeedFromCatalog(cat); var dr = d.Run(Horizon, 15);
                return dr.BeltTrackableObjects[^1] / dr.BeltTrackableObjects[0];
            }).ToArray();
            var cube = Cube(new Spec(Launches: launches, Removals: removals));
            return new { launchesPerYear = launches, removalsPerYear = removals, box = BoxGrowth(Box(rate: launches, removals: removals)), discrete = dis, conjunction = cube.Growth };
        }
        var crossChecks = new[] { CrossCheck(50, 0), CrossCheck(0, 10) };

        // ---------------------------------------------------------------- box: hazard snapshots ---------
        log("  box model: hazard by altitude, persistence, low-altitude breakup ...");
        var hz = Box();
        double satA = bundle.MeanAreaM2;
        var alt = hz.MidAltitudesKm; var haz0 = hz.SatelliteHazardByShell(satA, 10_000);
        hz.Run(Horizon, Dt); var haz50 = hz.SatelliteHazardByShell(satA, 10_000);
        var hb = Box(); hb.InjectBarrel(550, nailsPerBarrel); var hazBarrel = hb.SatelliteHazardByShell(satA, 10_000);
        double Life(double a, double am) { double d = AtmosphericDrag.LifetimeDays(a, am, 1.0); return double.IsFinite(d) ? Math.Min(d / 365.25, 1e4) : 1e4; }
        double fragAm = BreakupModel.FragmentAreaToMass(0.0562), intactAm = BreakupModel.AreaFromLc(1.78) / BreakupModel.IntactMassFromLc(1.78);
        var usability = new
        {
            engine = "box", altKm = alt, satAreaM2 = satA, hazardToday = haz0, hazardYear50 = haz50, hazardTodayWithBarrelAt550 = hazBarrel,
            walkAwayPerYear = 0.02, fragmentAreaToMass = fragAm, intactAreaToMass = intactAm,
            fragmentLifetimeYears = alt.Select(a => Life(a, fragAm)).ToArray(),
            intactLifetimeYears = alt.Select(a => Life(a, intactAm)).ToArray(),
            nailLifetimeYears = alt.Select(a => Life(a, nail.AreaToMassRatio)).ToArray(),
        };
        var low = Box(); var lowBase = Box();
        int si = Array.IndexOf(alt, alt.OrderBy(a => Math.Abs(a - 480)).First());
        low.InjectBreakup(480, 2200, 10);
        var months = new List<double>(); var hazLow = new List<double>(); var hazBase = new List<double>();
        for (int mo = 0; mo <= 60; mo++)
        {
            months.Add(mo);
            hazLow.Add(low.SatelliteHazardByShell(satA, 10_000)[si]);
            hazBase.Add(lowBase.SatelliteHazardByShell(satA, 10_000)[si]);
            low.Run(30.4375 / 365.25, 30.4375 / 2); lowBase.Run(30.4375 / 365.25, 30.4375 / 2);
        }
        var lowEvent = new { engine = "box", altKm = alt[si], targetKg = 2200, months, hazard = hazLow, baselineHazard = hazBase };

        // Baseline ensembles of all three engines (nothing added).
        log("  discrete ensemble (nothing added) ...");
        var disB = Enumerable.Range(1, 8).AsParallel().AsOrdered().Select(s =>
        {
            var d = new DiscreteCascade(seed: s); d.SeedFromCatalog(cat); var dr = d.Run(Horizon, 15);
            return dr.BeltTrackableObjects[^1] / dr.BeltTrackableObjects[0];
        }).ToArray();
        log($"  done in {sw.Elapsed.TotalMinutes:F1} min.");

        return new
        {
            generatedUtc = DateTime.UtcNow, horizonYears = Horizon, beltAltKm = BeltAltKm, nailsPerBarrel,
            headlineEngine = "cube", seeds = Seeds,
            explosionsPerYear = new KesslerEvolution(nail).ExplosionsPerYear, explosionScale = new KesslerEvolution(nail).ExplosionScale, explosionSensitivity = explosions,
            removal, crossChecks, working, comparison,
            catalog, physics, conventions = perConv, usability, lowEvent,
            ensembles = new { conjunctionBelt = baseEns.Growth, discreteBelt = disB, boxBelt = BoxGrowth(Box()) },
        };
    }
}
