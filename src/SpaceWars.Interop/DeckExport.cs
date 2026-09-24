using System;
using System.Collections.Generic;
using System.Linq;
using SpaceWars.Core;

namespace SpaceWars.Interop;

/// <summary>
/// Every number and chart series the presentation quotes, computed in one run (CLI <c>--deck</c>
/// → data/deck_numbers.json). Launch-driven results are given under two cratering conventions
/// that bracket the model's main uncertainty: the breakup model as written (every projectile's
/// cratering ejecta) and the LEGEND convention (only ≥10 cm projectiles).
/// </summary>
public static class DeckExport
{
    private const double Horizon = 50, Dt = 10, BeltAltKm = 900;
    private static readonly (string Key, string Label, double MinLc)[] Conventions =
    {
        ("asWritten", "breakup model as written", 0.0),
        ("legend", "LEGEND convention (>=10 cm projectiles crater)", 0.1),
    };

    public static object Compute(CatalogBundle bundle, NailSpec nail, int nailsPerBarrel, Action<string>? log = null)
    {
        log ??= _ => { };
        var cat = bundle.Objects;

        KesslerEvolution Box(double minLc, double rate = 0, bool responsive = false)
        {
            var m = new KesslerEvolution(nail)
            {
                CrateringEjectaMinLcM = minLc, LaunchRatePerYear = rate, LaunchAltKm = BeltAltKm,
                ResponsiveLaunch = responsive, LossTolerancePerYear = 0.02,
            };
            m.SeedFromCatalog(cat);
            return m;
        }
        // End-of-horizon populations: all objects, objects ≥10 cm, and ≥10 cm in the 700–1100 km belt.
        (double All, double Trk, double Belt) End(KesslerEvolution m) { var r = m.Run(Horizon, Dt); return (r.TotalObjects[^1], r.TrackableObjects[^1], r.BeltTrackableObjects[^1]); }

        // --- catalog & single-impact physics ---
        var catalog = new
        {
            leoObjects = cat.Count, totalTracked = bundle.TotalTracked, withRcs = bundle.WithRcs, source = bundle.Source,
            includesDebris = bundle.IncludesDebris, countByType = bundle.CountByType,
            payloadMeanMassKg = bundle.MeanMassKg, payloadMeanAreaM2 = bundle.MeanAreaM2,
            catalogMassTonnes = cat.Sum(o => o.MassKg) / 1000,
            modelledLargeBelt = DebrisEnvironment.LargeBeltFor(cat),
            modelledSmallDebris1to10cm = 1_000_000,
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

        var perConv = new Dictionary<string, object>();
        foreach (var (key, label, minLc) in Conventions)
        {
            log($"  [{key}] baseline, tipping, responsive, barrels, ASAT ...");
            var baseRun = Box(minLc).Run(Horizon, Dt);
            double b0 = baseRun.TotalObjects[0], bEnd = baseRun.TotalObjects[^1];
            double t0 = baseRun.TrackableObjects[0], tEnd = baseRun.TrackableObjects[^1];
            double k0 = baseRun.BeltTrackableObjects[0], kEnd = baseRun.BeltTrackableObjects[^1];

            // Launch sweep into the belt: 50-yr growth (both metrics) and break-even rates (growth = 1).
            double[] rates = { 0, 25, 50, 100, 150, 200, 300, 400, 500, 700, 1000 };
            var sweep = rates.Select(r => End(Box(minLc, r))).ToArray();
            double BreakEven(Func<(double All, double Trk, double Belt), bool> grows)
            {
                double lo = 0, hi = 1000;
                if (grows(End(Box(minLc, 0)))) return 0;
                for (int it = 0; it < 16; it++) { double mid = 0.5 * (lo + hi); if (grows(End(Box(minLc, mid)))) hi = mid; else lo = mid; }
                return 0.5 * (lo + hi);
            }

            // Constant vs responsive launch at 500/yr — also the "extreme" scenario.
            var cst = Box(minLc, 500).Run(Horizon, Dt);
            var rsp = Box(minLc, 500, responsive: true).Run(Horizon, Dt);
            int quitIdx = Array.FindIndex(rsp.LaunchFraction, f => f <= 1e-6);

            // Barrels dumped at 900 km, no launch traffic: 50-yr populations vs the no-barrel baseline.
            (double All, double Trk, double Belt) BarrelRatio(double k, double alt = BeltAltKm)
            {
                var m = Box(minLc); m.InjectBarrel(alt, k * nailsPerBarrel); var e = End(m);
                return (e.All / bEnd, e.Trk / tEnd, e.Belt / kEnd);
            }
            double[] counts = { 1, 3, 10, 30, 100, 300, 1000 };
            var barrel = counts.Select(k => BarrelRatio(k)).ToArray();
            double BarrelsToDouble(Func<(double All, double Trk, double Belt), double> metric)
            {
                double klo = 1, khi = 30_000;
                if (metric(BarrelRatio(khi)) < 2) return double.PositiveInfinity;
                for (int it = 0; it < 20; it++) { double mid = Math.Sqrt(klo * khi); if (metric(BarrelRatio(mid)) >= 2) khi = mid; else klo = mid; }
                return Math.Sqrt(klo * khi);
            }
            double toDoubleAll = BarrelsToDouble(r => r.All), toDoubleTrk = BarrelsToDouble(r => r.Trk), toDoubleBelt = BarrelsToDouble(r => r.Belt);

            // One ASAT strike (1 t satellite, 10 kg interceptor) at 865 km, like Fengyun-1C.
            var asatM = Box(minLc); asatM.InjectBreakup(865, 1000, 10);
            var asatEnd = End(asatM);
            double asatAll = asatEnd.All / bEnd, asatTrk = asatEnd.Trk / tEnd, asatBelt = asatEnd.Belt / kEnd;
            double Equiv(double asat, double oneBarrel) => (asat - 1) / Math.Max(1e-12, oneBarrel - 1);

            perConv[key] = new
            {
                label,
                baseline = new
                {
                    years = baseRun.Years, total = baseRun.TotalObjects, trackable = baseRun.TrackableObjects, beltTrackable = baseRun.BeltTrackableObjects,
                    growthAll = bEnd / b0, growthTrackable = tEnd / t0, growthBelt = kEnd / k0,
                },
                tipping = new
                {
                    rates,
                    growthAll = sweep.Select(e => e.All / b0).ToArray(),
                    growthTrackable = sweep.Select(e => e.Trk / t0).ToArray(),
                    growthBelt = sweep.Select(e => e.Belt / k0).ToArray(),
                    breakEvenBeltPerYear = BreakEven(e => e.Belt / k0 > 1),
                    breakEvenAllPerYear = BreakEven(e => e.All / b0 > 1),
                    breakEvenTrackablePerYear = BreakEven(e => e.Trk / t0 > 1),
                },
                responsive500 = new
                {
                    years = cst.Years, constant = cst.TotalObjects, constantTrackable = cst.TrackableObjects, constantBelt = cst.BeltTrackableObjects,
                    constantCatPerYear = cst.CatastrophicPerYear,
                    responsive = rsp.TotalObjects, responsiveTrackable = rsp.TrackableObjects, responsiveBelt = rsp.BeltTrackableObjects, throttle = rsp.LaunchFraction,
                    operatorsQuitYear = quitIdx >= 0 ? rsp.Years[quitIdx] : -1,
                    growthAfterQuitAll = quitIdx >= 0 ? rsp.TotalObjects[^1] / rsp.TotalObjects[quitIdx] : 1,
                    growthAfterQuitTrackable = quitIdx >= 0 ? rsp.TrackableObjects[^1] / rsp.TrackableObjects[quitIdx] : 1,
                    growthAfterQuitBelt = quitIdx >= 0 ? rsp.BeltTrackableObjects[^1] / rsp.BeltTrackableObjects[quitIdx] : 1,
                },
                barrels = new
                {
                    counts, ratioAll = barrel.Select(r => r.All).ToArray(), ratioTrackable = barrel.Select(r => r.Trk).ToArray(), ratioBelt = barrel.Select(r => r.Belt).ToArray(),
                    barrelsToDoubleAll = double.IsFinite(toDoubleAll) ? toDoubleAll : -1,
                    barrelsToDoubleTrackable = double.IsFinite(toDoubleTrk) ? toDoubleTrk : -1,
                    barrelsToDoubleBelt = double.IsFinite(toDoubleBelt) ? toDoubleBelt : -1,
                    oneAt550All = BarrelRatio(1, 550).All,
                },
                asat = new
                {
                    altKm = 865, targetKg = 1000, projectileKg = 10, ratioAll = asatAll, ratioTrackable = asatTrk, ratioBelt = asatBelt,
                    barrelEquivalentsAll = Equiv(asatAll, barrel[0].All),
                    barrelEquivalentsTrackable = Equiv(asatTrk, barrel[0].Trk),
                    barrelEquivalentsBelt = Equiv(asatBelt, barrel[0].Belt),
                },
            };
        }

        // --- per-satellite hazard by altitude (0 launches), and drag persistence ---
        log("  hazard by altitude & persistence ...");
        var hz = Box(0);
        double satA = bundle.MeanAreaM2;   // mean payload: the stand-in operational satellite
        var alt = hz.MidAltitudesKm; var haz0 = hz.SatelliteHazardByShell(satA, 10_000);
        hz.Run(Horizon, Dt); var haz50 = hz.SatelliteHazardByShell(satA, 10_000);
        var hb = Box(0); hb.InjectBarrel(550, nailsPerBarrel); var hazBarrel = hb.SatelliteHazardByShell(satA, 10_000);
        double Life(double a, double am) { double d = AtmosphericDrag.LifetimeDays(a, am, 1.0); return double.IsFinite(d) ? Math.Min(d / 365.25, 1e4) : 1e4; }
        double fragAm = BreakupModel.FragmentAreaToMass(0.0562), intactAm = BreakupModel.AreaFromLc(1.78) / BreakupModel.IntactMassFromLc(1.78);
        var usability = new
        {
            altKm = alt, satAreaM2 = satA, hazardToday = haz0, hazardYear50 = haz50, hazardTodayWithBarrelAt550 = hazBarrel,
            walkAwayPerYear = 0.02,
            fragmentAreaToMass = fragAm, intactAreaToMass = intactAm,
            fragmentLifetimeYears = alt.Select(a => Life(a, fragAm)).ToArray(),
            intactLifetimeYears = alt.Select(a => Life(a, intactAm)).ToArray(),
            nailLifetimeYears = alt.Select(a => Life(a, nail.AreaToMassRatio)).ToArray(),
        };

        // --- low-altitude breakup (Cosmos-1408-like, 2.2 t at 480 km): hazard spike and recovery ---
        log("  low-altitude breakup ...");
        var low = Box(0); var lowBase = Box(0);
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
        var lowEvent = new { altKm = alt[si], targetKg = 2200, months, hazard = hazLow, baselineHazard = hazBase };

        // --- stochastic engines: seed ensembles of the 50-yr zero-launch baseline ---
        log("  discrete / conjunction ensembles (8 seeds) ...");
        var dis = new List<double>(); var con = new List<double>(); var disT = new List<double>(); var conT = new List<double>(); var disB = new List<double>(); var conB = new List<double>();
        for (int s = 1; s <= 8; s++)
        {
            var d = new DiscreteCascade(seed: s); d.SeedFromCatalog(cat); var dr = d.Run(Horizon, 15);
            dis.Add(dr.TotalObjects[^1] / dr.TotalObjects[0]); disT.Add(dr.TrackableObjects[^1] / dr.TrackableObjects[0]); disB.Add(dr.BeltTrackableObjects[^1] / dr.BeltTrackableObjects[0]);
            var c = new ConjunctionCascade(seed: s); c.SeedFromCatalog(cat); var cr = c.Run(Horizon, 60);
            con.Add(cr.TotalObjects[^1] / cr.TotalObjects[0]); conT.Add(cr.TrackableObjects[^1] / cr.TrackableObjects[0]); conB.Add(cr.BeltTrackableObjects[^1] / cr.BeltTrackableObjects[0]);
        }

        return new
        {
            generatedUtc = DateTime.UtcNow, horizonYears = Horizon, beltAltKm = BeltAltKm, nailsPerBarrel,
            catalog, physics, conventions = perConv, usability, lowEvent,
            ensembles = new { discreteAll = dis, conjunctionAll = con, discreteTrackable = disT, conjunctionTrackable = conT, discreteBelt = disB, conjunctionBelt = conB },
        };
    }
}
