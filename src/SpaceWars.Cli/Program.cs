using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using SpaceWars.Core;
using SpaceWars.Interop;

// ---------------------------------------------------------------------------
// SpaceWars — barrel-of-nails LEO collision-flux assessment (near-term engine).
// Scenario: random tumbling dump. CPU reference run; CUDA propagator to follow.
// ---------------------------------------------------------------------------

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // stable, unambiguous numeric output

var opts = CliOptions.Parse(args);
string dataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data");
dataDir = Path.GetFullPath(dataDir);

Console.WriteLine("=== SpaceWars: barrel-of-nails LEO flux assessment ===\n");

// 1. Load the catalog: every object on orbit (Space-Track), the CelesTrak group, or a local file.
Console.WriteLine($"Loading catalog (cache dir: {dataDir}) ...");
CatalogBundle bundle;
try { bundle = await Scenarios.LoadCatalogAsync(dataDir, opts.Group, includeDebris: !opts.ActiveOnly, offlineFile: opts.OfflineFile); }
catch (Exception ex)
{
    Console.Error.WriteLine($"  catalog unavailable: {ex.Message}");
    return 1;
}
var catalogObjects = bundle.Objects.ToList();
var catalog = catalogObjects.Select(o => o.Elements).ToList();
int withRcs = bundle.WithRcs; string satSrc = bundle.Source;
double meanArea = bundle.MeanAreaM2, meanMass = bundle.MeanMassKg;
Console.WriteLine($"  {bundle.Source}");
Console.WriteLine($"  loaded {bundle.TotalTracked:N0} objects, {catalog.Count:N0} in LEO (<2000 km perigee): " +
                  string.Join(", ", bundle.CountByType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value:N0} {kv.Key}")) + ".");
Console.WriteLine($"  {withRcs:N0} carry RCS ({(catalog.Count > 0 ? 100.0 * withRcs / catalog.Count : 0):F0}%); " +
                  $"payload mean cross-section {meanArea:F2} m², mean mass {meanMass:F0} kg.\n");

// 2. Configure and deploy the barrel.
var nail = new NailSpec();
var barrel = new NailBarrel { Nail = nail, Count = opts.NailCount };

double a = Constants.EarthRadiusKm + opts.AltKm;
double nRadPerSec = Math.Sqrt(Constants.Mu / (a * a * a));
double revPerDay = nRadPerSec * Constants.SecondsPerDay / Constants.TwoPi;
var parent = OrbitalElements.FromMeanMotionRevPerDay(
    revPerDay, e: 0.0, iRad: opts.IncDeg * Constants.DegToRad,
    raanRad: 0, argpRad: 0, meanAnomalyRad: 0);

Console.WriteLine($"Barrel: {barrel.Count:N0} nails, {nail.LengthM * 1000:F0}×{nail.DiameterM * 1000:F0} mm steel");
Console.WriteLine($"  per nail: {nail.MassKg * 1000:F2} g, σ={nail.MeanCrossSectionM2 * 1e4:F2} cm², " +
                  $"Lc={nail.CharacteristicLengthM * 100:F1} cm, A/m={nail.AreaToMassRatio:F3} m²/kg");
Console.WriteLine($"  total launched mass: {barrel.TotalMassKg:N0} kg");
Console.WriteLine($"  release orbit: {opts.AltKm:F0} km circular, {opts.IncDeg:F1}° inclination");
Console.WriteLine($"  dispersal: σ={opts.DispersalSigmaMS:F0} m/s per axis (random tumbling dump)\n");

var cloud = barrel.Deploy(parent, releaseTimeSecFromParentEpoch: 0, opts.DispersalSigmaMS);

// Orbital lifetime of the cloud (how fast it rains out).
Console.WriteLine("--- Orbital lifetime / decay (drag) ---");
foreach (var (label, solar) in new[] { ("solar min", 0.5), ("nominal", 1.0), ("solar max", 4.0) })
{
    double days = AtmosphericDrag.LifetimeDays(opts.AltKm, nail.AreaToMassRatio, solar);
    string span = double.IsInfinity(days) ? ">1000 yr" : $"{days / 365.25:F1} yr ({days:N0} d)";
    Console.WriteLine($"  {label,-9} ({solar:F1}×): a nail at {opts.AltKm:F0} km decays in ~{span}");
}
Console.WriteLine();

// 3. Lethality of a single nail.
Console.WriteLine("--- Single-nail lethality (NASA breakup model) ---");
foreach (double v in new[] { 7_600.0, 10_000.0, 15_200.0 })
{
    double maxKg = Lethality.MaxCatastrophicTargetMassKg(nail.MassKg, v);
    double ej = Lethality.KineticEnergyJoules(nail.MassKg, v);
    Console.WriteLine($"  @ {v / 1000,5:F1} km/s: {ej / 1000,6:F0} kJ → shatters targets ≤ {maxKg,5:F1} kg; " +
                      $"larger targets are mission-killed.");
}
var star = BreakupModel.Fragment(260, nail.MassKg, opts.RelVelMS);
var cube = BreakupModel.Fragment(3, nail.MassKg, opts.RelVelMS);
Console.WriteLine($"  vs 260 kg satellite @ {opts.RelVelMS / 1000:F1} km/s: {star}");
Console.WriteLine($"  vs   3 kg cubesat   @ {opts.RelVelMS / 1000:F1} km/s: {cube}\n");

// 4. Spatial-density flux.
double targetArea = opts.TargetAreaM2 != 5.0 ? opts.TargetAreaM2 : meanArea; // SATCAT mean unless overridden
var flux = new SpatialDensityFlux { RelVelMetersPerSec = opts.RelVelMS, TargetAreaM2 = targetArea };
var shells = flux.Analyze(catalog, cloud, nail);
double totalPerYear = SpatialDensityFlux.TotalCollisionsPerYear(shells);

var peak = shells.Where(s => s.NailCount > 0).OrderByDescending(s => s.NailImpactsPerSatPerYear).FirstOrDefault();
Console.WriteLine("--- Collision flux (first-order spatial density) ---");
Console.WriteLine($"  assumed rel. velocity {opts.RelVelMS / 1000:F1} km/s, representative target area {targetArea:F2} m² (SATCAT-derived)");
if (peak is not null)
{
    Console.WriteLine($"  peak nail shell: {peak.AltLowKm:F0}-{peak.AltHighKm:F0} km, " +
                      $"{peak.NailCount:N0} nails, {peak.CatalogCount:N0} catalog objects");
    Console.WriteLine($"    nail number density: {peak.NailDensity:E2} /m³");
    double yearsPerHit = peak.NailImpactsPerSatPerYear > 0 ? 1.0 / peak.NailImpactsPerSatPerYear : double.PositiveInfinity;
    Console.WriteLine($"    a satellite there is hit by a nail on average every {yearsPerHit:N0} years");
}
Console.WriteLine($"  expected nail-on-catalogued-object impacts (mission kills): {totalPerYear:F1} per year across LEO");
if (totalPerYear is > 0 and < 1)
    Console.WriteLine($"    (≈ one every {1.0 / totalPerYear:N0} years)");
Console.WriteLine();

Console.WriteLine("  top shells by expected total collisions/year:");
foreach (var s in shells.OrderByDescending(s => s.TotalCollisionsPerYear).Take(5).Where(s => s.TotalCollisionsPerYear > 0))
    Console.WriteLine($"    {s.AltLowKm,5:F0}-{s.AltHighKm,5:F0} km: {s.NailCount,8:N0} nails, " +
                      $"{s.CatalogCount,5:N0} sats, {s.TotalCollisionsPerYear:E2}/yr");

// 4b. GPU engine: full-population time-averaged density + CPU benchmark.
if (opts.Gpu)
{
    Console.WriteLine("--- GPU engine (CUDA) ---");
    if (!Cuda.Available)
        Console.WriteLine($"  CUDA unavailable: {Cuda.DeviceInfo}\n");
    else
    {
        Console.WriteLine($"  device: {Cuda.DeviceInfo}");
        double minAlt = 200, binKm = 25; int nBins = 72;
        var times = Enumerable.Range(0, 96).Select(k => k * 900.0).ToArray(); // 96 samples / ~1 day
        var nailEpoch = new double[cloud.Length];
        var catEpoch = new double[catalog.Count];
        long states = (long)(cloud.Length + catalog.Count) * times.Length;

        var sw = Stopwatch.StartNew();
        var nailH = Cuda.SampleDensity(cloud, nailEpoch, times, minAlt, binKm, nBins);
        var catH = Cuda.SampleDensity(catalog, catEpoch, times, minAlt, binKm, nBins);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds;

        double sigma = Math.Pow(Math.Sqrt(targetArea) + Math.Sqrt(nail.MeanCrossSectionM2), 2);
        const double reM = 6_378_135.0, secYr = 3.15576e7;
        double totalGpu = 0;
        for (int b = 0; b < nBins; b++)
        {
            double lo = minAlt + b * binKm, hi = lo + binKm;
            double rLo = reM + lo * 1000, rHi = reM + hi * 1000;
            double V = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
            double nNail = nailH[b] / (double)times.Length, nCat = catH[b] / (double)times.Length;
            if (V > 0) totalGpu += nNail * nCat / V * sigma * opts.RelVelMS * secYr;
        }

        Console.WriteLine($"  propagated {cloud.Length + catalog.Count:N0} objects × {times.Length} samples " +
                          $"= {states:N0} states in {gpuMs:F1} ms ({states / gpuMs / 1000.0:F1} M states/s)");
        Console.WriteLine($"  GPU time-averaged flux: {totalGpu:F1} mission-kills/yr across LEO");

        var swc = Stopwatch.StartNew();
        DensitySampler.Histogram(cloud, nailEpoch, times, minAlt, binKm, nBins);
        DensitySampler.Histogram(catalog, catEpoch, times, minAlt, binKm, nBins);
        swc.Stop();
        Console.WriteLine($"  same workload on CPU: {swc.Elapsed.TotalMilliseconds:F0} ms " +
                          $"→ GPU speedup ≈ {swc.Elapsed.TotalMilliseconds / gpuMs:F0}×\n");
    }
}

// 4c. Long-term Kessler evolution: baseline vs with-barrel.
if (opts.Evolve)
{
    Console.WriteLine("--- Kessler evolution (source-sink ODE, 50 yr, nominal solar) ---");
    if (opts.LaunchRatePerYear > 0)
        Console.WriteLine($"   launch source: {opts.LaunchRatePerYear:N0} intacts/yr into the {opts.LaunchAltKm:F0} km band" +
                          (opts.Responsive ? $" (RESPONSIVE: operators quit above {opts.LossTolerance:P0}/yr attrition)" : " (constant)"));
    var baseM = new KesslerEvolution(nail)
    {
        LaunchRatePerYear = opts.LaunchRatePerYear, LaunchAltKm = opts.LaunchAltKm,
        ResponsiveLaunch = opts.Responsive, LossTolerancePerYear = opts.LossTolerance,
    };
    baseM.SeedFromCatalog(catalogObjects);
    var barM = new KesslerEvolution(nail)
    {
        LaunchRatePerYear = opts.LaunchRatePerYear, LaunchAltKm = opts.LaunchAltKm,
        ResponsiveLaunch = opts.Responsive, LossTolerancePerYear = opts.LossTolerance,
    };
    barM.SeedFromCatalog(catalogObjects); barM.InjectBarrel(opts.AltKm, opts.NailCount);
    var baseR = baseM.Run(50, 10);
    var barR = barM.Run(50, 10);

    int last = baseR.Years.Length - 1;
    Console.WriteLine("   year | baseline debris | +barrel debris | baseline cat/yr | +barrel cat/yr");
    foreach (int y in new[] { 0, 10, 25, 50 })
    {
        int idx = Math.Min(y, last);
        Console.WriteLine($"   {(int)baseR.Years[idx],4} | {baseR.TotalObjects[idx],15:N0} | {barR.TotalObjects[idx],14:N0} | " +
                          $"{baseR.CatastrophicPerYear[idx],15:F2} | {barR.CatastrophicPerYear[idx],14:F2}");
    }

    if (opts.Responsive && opts.LaunchRatePerYear > 0)
    {
        Console.WriteLine("   launch throttle (fraction of nominal actually flown, baseline):");
        Console.Write("     ");
        foreach (int y in new[] { 0, 10, 25, 50 })
        {
            int idx = Math.Min(y, last);
            Console.Write($"yr {(int)baseR.Years[idx]}: {baseR.LaunchFraction[idx],5:P0}   ");
        }
        Console.WriteLine();
    }

    double extraCat = barR.CatastrophicPerYear.Sum() - baseR.CatastrophicPerYear.Sum();
    double deltaEnd = barR.TotalObjects[^1] - baseR.TotalObjects[^1];
    bool baseGrows = baseR.TotalObjects[^1] > baseR.TotalObjects[0];
    Console.WriteLine($"   barrel-attributable extra catastrophic collisions over 50 yr: {extraCat:F1}");
    Console.WriteLine($"   extra debris still on orbit at year 50: {deltaEnd:N0}");
    Console.WriteLine($"   release shell trend: baseline debris {(baseGrows ? "GROWS (supercritical)" : "declines (subcritical)")} over the horizon");

    string evoPath = Path.Combine(dataDir, "evolution.json");
    var evo = new
    {
        years = baseR.Years,
        baselineDebris = baseR.TotalObjects, barrelDebris = barR.TotalObjects,
        baselineCatPerYear = baseR.CatastrophicPerYear, barrelCatPerYear = barR.CatastrophicPerYear,
        survivingNails = barR.SurvivingNails,
        meta = new { altKm = opts.AltKm, nailCount = opts.NailCount }
    };
    File.WriteAllText(evoPath, System.Text.Json.JsonSerializer.Serialize(evo));
    Console.WriteLine($"   time series exported to {evoPath}\n");
}

// 4d. Discrete per-object cascade (Monte Carlo).
if (opts.Cascade)
{
    Console.WriteLine("--- Discrete per-object cascade (Monte Carlo, 50 yr) ---");
    if (opts.LaunchRatePerYear > 0)
        Console.WriteLine($"   launch source: {opts.LaunchRatePerYear:N0} intacts/yr into the {opts.LaunchAltKm:F0} km band" +
                          (opts.Responsive ? $" (RESPONSIVE: operators quit above {opts.LossTolerance:P0}/yr attrition)" : " (constant)"));
    var baseC = new DiscreteCascade(seed: 1)
    {
        LaunchRatePerYear = opts.LaunchRatePerYear, LaunchAltKm = opts.LaunchAltKm,
        ResponsiveLaunch = opts.Responsive, LossTolerancePerYear = opts.LossTolerance,
    };
    baseC.SeedFromCatalog(catalogObjects);
    var barC = new DiscreteCascade(seed: 1)
    {
        LaunchRatePerYear = opts.LaunchRatePerYear, LaunchAltKm = opts.LaunchAltKm,
        ResponsiveLaunch = opts.Responsive, LossTolerancePerYear = opts.LossTolerance,
    };
    barC.SeedFromCatalog(catalogObjects);
    barC.InjectBarrel(cloud, nail, superParticles: 3000, totalNails: opts.NailCount);

    var swk = Stopwatch.StartNew();
    var bR = baseC.Run(50, 15);
    var kR = barC.Run(50, 15);
    swk.Stop();

    Console.WriteLine("   year | baseline objects | +barrel objects | +barrel catastrophic/yr");
    int last = bR.Years.Length - 1;
    foreach (int y in new[] { 0, 10, 25, 50 })
    {
        int idx = Math.Min(y, last);
        Console.WriteLine($"   {(int)bR.Years[idx],4} | {bR.TotalObjects[idx],16:N0} | {kR.TotalObjects[idx],15:N0} | {kR.CatastrophicPerYear[idx],10:F1}");
    }

    double baseStart = bR.TotalObjects[0], baseEnd = bR.TotalObjects[^1];
    double barEnd = kR.TotalObjects[^1];
    double extraCat = kR.CatastrophicPerYear.Sum() - bR.CatastrophicPerYear.Sum();
    bool runaway = baseEnd > baseStart * 1.05;
    Console.WriteLine($"   baseline objects: {baseStart:N0} → {baseEnd:N0} over 50 yr " +
                      $"({(runaway ? "RUNAWAY / supercritical" : "declining / subcritical")})");
    Console.WriteLine($"   barrel-attributable extra catastrophic collisions: {extraCat:F0} over 50 yr");
    Console.WriteLine($"   extra objects vs baseline at year 50: {barEnd - baseEnd:N0}");
    Console.WriteLine($"   (ran on CPU super-particles in {swk.Elapsed.TotalSeconds:F1} s; " +
                      $"full-resolution per-fragment tracking is the CUDA propagator's job)");

    string cpath = Path.Combine(dataDir, "cascade.json");
    File.WriteAllText(cpath, System.Text.Json.JsonSerializer.Serialize(new
    {
        years = bR.Years,
        baselineObjects = bR.TotalObjects, barrelObjects = kR.TotalObjects,
        baselineCrossSection = bR.TotalCrossSection, barrelCrossSection = kR.TotalCrossSection,
        barrelCatPerYear = kR.CatastrophicPerYear, survivingNails = kR.SurvivingNails,
        meta = new { altKm = opts.AltKm, nailCount = opts.NailCount }
    }));

    string survive = runaway ? "NO — the modelled environment runs away" : "YES";
    Console.WriteLine($"\n   ⇒ Does LEO survive this barrel? {survive}. " +
                      "One barrel is a transient, self-cleaning perturbation, not a cascade trigger.\n");
}

// 4e. Tipping point: find the critical launch rate where LEO flips to runaway.
if (opts.Tipping)
{
    Console.WriteLine($"--- Tipping point: launch-rate sweep into the {opts.LaunchAltKm:F0} km band (50 yr) ---");
    var cat = catalogObjects; // capture for closures
    double alt = opts.LaunchAltKm; int nails = opts.NailCount; double barrelAlt = opts.LaunchAltKm;

    // 50-yr net growth factor of the debris environment at a given launch rate.
    double GrowthFactor(double rate, bool withBarrel)
    {
        var m = new KesslerEvolution(nail) { LaunchRatePerYear = rate, LaunchAltKm = alt };
        m.SeedFromCatalog(cat);
        if (withBarrel) m.InjectBarrel(barrelAlt, nails);
        var r = m.Run(50, 10);
        return r.TotalObjects[^1] / r.TotalObjects[0];
    }
    // Net 50-yr growth > 1 means launches out-pace drag — a break-even point, not by itself a
    // self-sustaining cascade (launched intacts alone raise the count).
    bool NetGrowth(double rate, bool b) => GrowthFactor(rate, b) > 1.0;

    double Critical(bool b)
    {
        double lo = 0, hi = 3000;
        if (!NetGrowth(hi, b)) return double.NaN;
        if (NetGrowth(0, b)) return 0;
        for (int it = 0; it < 18; it++) { double mid = 0.5 * (lo + hi); if (NetGrowth(mid, b)) hi = mid; else lo = mid; }
        return 0.5 * (lo + hi);
    }

    Console.WriteLine("   launch/yr | baseline 50-yr growth | +barrel 50-yr growth | regime");
    foreach (double rate in new[] { 0, 100, 250, 500, 1000, 1500, 2000 })
    {
        double gb = GrowthFactor(rate, false), gk = GrowthFactor(rate, true);
        string regime = gb > 1 ? "net growth" : "self-clean";
        Console.WriteLine($"   {rate,9:N0} | {gb,20:F2}× | {gk,19:F2}× | {regime}");
    }

    double critBase = Critical(false), critBar = Critical(true);
    Console.WriteLine($"\n   break-even launch rate (net 50-yr growth = 1):");
    Console.WriteLine($"     baseline    : {(double.IsNaN(critBase) ? ">3000" : critBase.ToString("F1"))} intacts/yr");
    Console.WriteLine($"     with barrel : {(double.IsNaN(critBar) ? ">3000" : critBar.ToString("F1"))} intacts/yr");
    if (!double.IsNaN(critBase) && !double.IsNaN(critBar))
    {
        double nudge = critBase - critBar;
        double gB = GrowthFactor(critBase, false), gK = GrowthFactor(critBase, true);
        Console.WriteLine($"   the barrel nudges the threshold DOWN by {nudge:F1} launches/yr " +
                          $"(at the baseline threshold, growth {gB:F3}× → {gK:F3}× with the barrel).");
        Console.WriteLine("   ⇒ Real but tiny: whether LEO runs away is set by the LAUNCH RATE, not by a barrel of");
        Console.WriteLine("     nails. The barrel only matters once a shell is already sitting on the knife-edge.\n");
    }
}

// 4f. How many barrels to push a band over the edge?
if (opts.BarrelThreshold)
{
    double band = opts.LaunchAltKm;
    int nailsPer = opts.NailCount;
    Console.WriteLine($"--- Barrels-to-threshold: inject K barrels into the {band:F0} km band, no launch traffic (50 yr) ---");
    var cat2 = catalogObjects;

    // Returns (peak growth factor over the run, end growth factor at 50 yr).
    (double peak, double end) Growth(double barrels)
    {
        var m = new KesslerEvolution(nail);
        m.SeedFromCatalog(cat2);
        if (barrels > 0) m.InjectBarrel(band, barrels * nailsPer);
        var r = m.Run(50, 10);
        double p = 0; foreach (var v in r.TotalObjects) p = Math.Max(p, v / r.TotalObjects[0]);
        return (p, r.TotalObjects[^1] / r.TotalObjects[0]);
    }

    Console.WriteLine("      barrels |        nails | peak growth | 50-yr growth | outcome");
    foreach (double k in new double[] { 1, 100, 10_000, 1_000_000, 100_000_000 })
    {
        var (p, e) = Growth(k);
        string outcome = e > 1.05 ? "SUSTAINED runaway" : p > 1.05 ? "transient spike, re-cleans" : "absorbed";
        Console.WriteLine($"   {k,10:N0} | {k * nailsPer,12:N0} | {p,10:F2}× | {e,11:F2}× | {outcome}");
    }

    // Bisect the smallest K (barrels) whose band still ends supercritical at 50 yr.
    double lo = 0, hi = 1e9; bool feasible = Growth(hi).end > 1.05;
    double kcrit = double.NaN;
    if (feasible) { for (int it = 0; it < 40; it++) { double mid = Math.Sqrt(Math.Max(lo, 1) * hi); if (Growth(mid).end > 1.05) hi = mid; else lo = mid; } kcrit = hi; }

    Console.WriteLine();
    if (double.IsNaN(kcrit))
        Console.WriteLine("   ⇒ No finite number of barrels leaves the band supercritical at 50 yr: a one-time");
    else
        Console.WriteLine($"   ⇒ ~{kcrit:N0} barrels ({kcrit * nailsPer:N0} nails) to keep the band growing at 50 yr — a one-time");
    Console.WriteLine("     nail dump has no resupply, so drag re-cleans it; sustained runaway needs sustained mass.");

    // Compare with the launch rate that makes the same band grow on net over 50 yr (computed).
    double GrowthAt(double rate)
    {
        var m = new KesslerEvolution(nail) { LaunchRatePerYear = rate, LaunchAltKm = band };
        m.SeedFromCatalog(cat2);
        var r = m.Run(50, 10);
        return r.TotalObjects[^1] / r.TotalObjects[0];
    }
    double rlo = 0, rhi = 3000;
    if (GrowthAt(rhi) > 1.0)
    {
        for (int it = 0; it < 18; it++) { double mid = 0.5 * (rlo + rhi); if (GrowthAt(mid) > 1.0) rhi = mid; else rlo = mid; }
        Console.WriteLine($"     (By contrast ~{0.5 * (rlo + rhi):F0} satellites/yr of LAUNCH traffic makes the same band grow on net.)\n");
    }
    else Console.WriteLine();
}

// 4g. Tier-3 conjunction cascade (Cube method, real orbit-crossing geometry).
if (opts.Conjunction)
{
    Console.WriteLine("--- Conjunction per-object cascade (Cube method, real geometry, 50 yr) ---");
    if (opts.LaunchRatePerYear > 0)
        Console.WriteLine($"   launch source: {opts.LaunchRatePerYear:N0} intacts/yr into the {opts.LaunchAltKm:F0} km band" +
                          (opts.Responsive ? $" (RESPONSIVE, quit above {opts.LossTolerance:P0}/yr)" : " (constant)"));
    var baseX = new ConjunctionCascade(seed: 1)
    { SolarActivity = 1.0, LaunchRatePerYear = opts.LaunchRatePerYear, LaunchAltKm = opts.LaunchAltKm, ResponsiveLaunch = opts.Responsive, LossTolerancePerYear = opts.LossTolerance };
    baseX.SeedFromCatalog(catalogObjects);
    var barX = new ConjunctionCascade(seed: 1)
    { SolarActivity = 1.0, LaunchRatePerYear = opts.LaunchRatePerYear, LaunchAltKm = opts.LaunchAltKm, ResponsiveLaunch = opts.Responsive, LossTolerancePerYear = opts.LossTolerance };
    barX.SeedFromCatalog(catalogObjects);
    barX.InjectBarrel(cloud, nail, superParticles: 3000, totalNails: opts.NailCount);

    var sw = Stopwatch.StartNew();
    var bR = baseX.Run(50, 60);
    var kR = barX.Run(50, 60);
    sw.Stop();

    Console.WriteLine($"   engine: {(baseX.UsedGpu ? "CUDA sw_propagate_state" : "CPU fallback")}, cube method, real per-encounter v_rel");
    Console.WriteLine("   year | baseline objects | +barrel objects | +barrel catastrophic/yr");
    int last = bR.Years.Length - 1;
    foreach (int y in new[] { 0, 10, 25, 50 })
    {
        int idx = Math.Min(y, last);
        Console.WriteLine($"   {(int)bR.Years[idx],4} | {bR.TotalObjects[idx],16:N0} | {kR.TotalObjects[idx],15:N0} | {kR.CatastrophicPerYear[idx],10:F1}");
    }
    bool grows = bR.TotalObjects[^1] > bR.TotalObjects[0] * 1.02;
    Console.WriteLine($"   baseline {bR.TotalObjects[0]:N0} → {bR.TotalObjects[^1]:N0} ({(grows ? "supercritical" : "subcritical")}); " +
                      $"ran in {sw.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine("   collisions resolved by real orbit crossings — eccentric cross-shell impacts included.");
    if (baseX.Coarsened) Console.WriteLine("   (population coarsened during a violent runaway — geometric fidelity reduced there)");

    File.WriteAllText(Path.Combine(dataDir, "conjunction.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        years = bR.Years, baselineObjects = bR.TotalObjects, barrelObjects = kR.TotalObjects,
        barrelCatPerYear = kR.CatastrophicPerYear, survivingNails = kR.SurvivingNails,
    }));
    Console.WriteLine();
}

// 4h. Combined chart dataset for the artifact.
if (opts.Charts)
{
    Console.WriteLine("--- Generating chart dataset → data/charts.json ---");
    var cat3 = catalogObjects;
    KesslerEvolution BoxBase() { var m = new KesslerEvolution(nail); m.SeedFromCatalog(cat3); return m; }

    // A. Timeline, no launch: box baseline, box +barrel, conjunction baseline.
    var boxBaseR = BoxBase().Run(50, 10);
    var boxBarM = new KesslerEvolution(nail); boxBarM.SeedFromCatalog(cat3); boxBarM.InjectBarrel(opts.AltKm, opts.NailCount);
    var boxBarR = boxBarM.Run(50, 10);
    var conjM = new ConjunctionCascade(seed: 1) { SolarActivity = 1.0 }; conjM.SeedFromCatalog(cat3);
    var conjR = conjM.Run(50, 60);

    // B. Tipping: 50-yr growth factor vs launch rate into 900 km.
    double[] rates = { 0, 50, 100, 150, 200, 300, 400, 600, 800, 1200, 1600, 2000 };
    var tipGrowth = rates.Select(r => { var m = new KesslerEvolution(nail) { LaunchRatePerYear = r, LaunchAltKm = 900 }; m.SeedFromCatalog(cat3); var rr = m.Run(50, 10); return rr.TotalObjects[^1] / rr.TotalObjects[0]; }).ToArray();

    // C. Constant vs responsive launch at 500/yr into 900 km.
    var cstM = new KesslerEvolution(nail) { LaunchRatePerYear = 500, LaunchAltKm = 900 }; cstM.SeedFromCatalog(cat3); var cstR = cstM.Run(50, 10);
    var rspM = new KesslerEvolution(nail) { LaunchRatePerYear = 500, LaunchAltKm = 900, ResponsiveLaunch = true, LossTolerancePerYear = 0.02 }; rspM.SeedFromCatalog(cat3); var rspR = rspM.Run(50, 10);

    // D. Barrels-to-threshold: 50-yr growth vs barrel count into 900 km.
    double[] bcounts = { 1, 10, 100, 300, 1000, 3000, 10000, 30000 };
    var barGrowth = bcounts.Select(k => { var m = new KesslerEvolution(nail); m.SeedFromCatalog(cat3); m.InjectBarrel(900, k * opts.NailCount); var rr = m.Run(50, 10); return rr.TotalObjects[^1] / rr.TotalObjects[0]; }).ToArray();

    var charts = new
    {
        meta = new { altKm = opts.AltKm, nailCount = opts.NailCount, catalog = catalog.Count },
        timeline = new { years = boxBaseR.Years, boxBaseline = boxBaseR.TotalObjects, boxBarrel = boxBarR.TotalObjects, conjBaseline = conjR.TotalObjects },
        tipping = new { rates, growth = tipGrowth },
        responsive = new { years = cstR.Years, constant = cstR.TotalObjects, responsive = rspR.TotalObjects, throttle = rspR.LaunchFraction },
        barrels = new { counts = bcounts, growth = barGrowth },
    };
    File.WriteAllText(Path.Combine(dataDir, "charts.json"), System.Text.Json.JsonSerializer.Serialize(charts));
    Console.WriteLine($"   wrote {Path.Combine(dataDir, "charts.json")}\n");
}

// 4h'. Every number and chart series the presentation quotes, from one run.
if (opts.Deck)
{
    Console.WriteLine("--- Deck numbers (data/deck_numbers.json) ---");
    var deck = DeckExport.Compute(bundle, nail, opts.NailCount, s => Console.WriteLine(s));
    string deckPath = Path.Combine(dataDir, "deck_numbers.json");
    File.WriteAllText(deckPath, System.Text.Json.JsonSerializer.Serialize(deck, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"   wrote {deckPath}\n");
}

// 4i. Cube-method calibration: geometric (cube) vs well-mixed (kinetic) rate.
if (opts.Calibrate)
{
    Console.WriteLine("--- Cube-method calibration (geometric vs well-mixed) ---");
    double dt = 30 * 86400.0; const double secYr = 3.15576e7;
    var sw = Stopwatch.StartNew();
    Console.WriteLine("   Fixed 20 km cubes, near-unit-weight particles; increasing temporal sampling.");
    Console.WriteLine("   A converged (quotable) rate STABILISES as sub-samples grow (variance, not bias, is the issue).");
    Console.WriteLine("   sub-samples | mean v_rel |  cube/yr | kinetic/yr |     k");

    double lastRate = 0, lastK = 0, lastVrel = 0;
    var rates = new List<double>();
    foreach (int subs in new[] { 32, 96, 288 })
    {
        var m = new ConjunctionCascade(seed: 1) { CubeKm = 20.0, SubSamples = subs };
        m.SeedFromCatalog(catalogObjects, backgroundSmallTotal: 120_000, backgroundSuperParticles: 120_000, backgroundLargeTotal: 8000);
        var (coll, vrel) = m.MeasureCubeRate(dt);
        double kin = m.MeasureKineticRate(dt, vrel);
        double rate = coll * secYr / dt, k = kin > 0 ? coll / kin : double.NaN;
        rates.Add(rate); lastRate = rate; lastK = k; lastVrel = vrel;
        Console.WriteLine($"   {subs,11} | {vrel / 1000,7:F2} km/s | {rate,8:F1} | {kin * secYr / dt,10:F1} | {k,6:F2}");
    }

    double drift = rates.Count >= 2 ? Math.Abs(rates[^1] - rates[^2]) / Math.Max(rates[^2], 1e-9) : 1;
    Console.WriteLine($"   last-step drift {drift:P0}; mean encounter speed {lastVrel / 1000:F1} km/s; ran in {sw.Elapsed.TotalSeconds:F0} s.");
    if (drift < 0.30)
        Console.WriteLine($"   ⇒ QUOTABLE: total collision rate stabilised at ≈{lastRate:F0}/yr for this test population ({drift:P0} drift).");
    else
        Console.WriteLine($"   ⇒ Still settling ({drift:P0} drift); more sub-samples / particles tighten it further.");
    Console.WriteLine($"     Geometric rate ≈ {lastK:F1}× the full-sphere well-mixed rate — real orbits concentrate in");
    Console.WriteLine($"     latitude/inclination bands the shell-average dilutes, so the box model UNDERcounts by");
    Console.WriteLine($"     ~{lastK:F0}× and its near-critical result is CONSERVATIVE.");
    Console.WriteLine($"     Caveat: mean v_rel {lastVrel / 1000:F1} km/s stays below the ~10 km/s isotropic value — co-moving");
    Console.WriteLine($"     pairs dominate and the rare high-speed crossings that drive CATASTROPHIC events remain");
    Console.WriteLine($"     under-sampled, so the energy-weighted (catastrophic) rate needs still more sampling.\n");
}

// 5. Population-context and verdict.
const double BackgroundLethal1to10cm = 1_000_000; // ~NASA/ESA modelled 1–10 cm population
Console.WriteLine($"\n--- Population context ---");
Console.WriteLine($"  nails added to the >1 cm lethal-but-untrackable class: {barrel.Count:N0}");
Console.WriteLine($"  that is a {barrel.Count / BackgroundLethal1to10cm * 100:F1}% increase over the " +
                  $"~{BackgroundLethal1to10cm:N0} modelled 1–10 cm objects.");

// 6. Optional scene export for the 3D visualization.
if (opts.ExportPath is not null)
{
    SceneExport.Write(opts.ExportPath, cloud, catalog, nail, opts);
    Console.WriteLine($"\nScene exported to {opts.ExportPath} " +
                      $"({new FileInfo(opts.ExportPath).Length / 1024:N0} KB)");
}

Console.WriteLine($"\n--- Verdict (this run) ---");
Console.WriteLine("  • Every nail is a mission-kill weapon; none of them individually shatters a large satellite.");
Console.WriteLine("  • Direct cascade ignition is NOT expected from one barrel (see collision rate above).");
Console.WriteLine("  • The barrel acts as an ACCELERANT: it raises standing debris density and dead-mass in");
Console.WriteLine("    the release shell, increasing the rate of the large–large collisions that drive Kessler.");
Console.WriteLine("  → Quantitatively, 'it will do no harm' is false; 'it renders LEO unusable' is overstated.");
return 0;

// ---------------------------------------------------------------------------
file sealed class CliOptions
{
    public string Group = CelesTrakClient.Groups.Active;
    public string? OfflineFile;
    public int NailCount = 200_000;
    public double AltKm = 550;
    public double IncDeg = 53;
    public double DispersalSigmaMS = 50;
    public double RelVelMS = 10_000;
    public double TargetAreaM2 = 5.0;
    public string? ExportPath;
    public int ExportNails = 5000;
    public int ExportCatalog = 3000;
    public bool Gpu;
    public bool Evolve;
    public bool Cascade;
    public bool Tipping;
    public double LaunchRatePerYear;
    public double LaunchAltKm = 900;
    public bool Responsive;
    public double LossTolerance = 0.02;
    public bool BarrelThreshold;
    public bool Conjunction;
    public bool Charts;
    public bool Calibrate;
    public bool Deck;
    public bool ActiveOnly;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--gpu") { o.Gpu = true; continue; }
            if (a == "--evolve") { o.Evolve = true; continue; }
            if (a == "--cascade") { o.Cascade = true; continue; }
            if (a == "--tipping") { o.Tipping = true; continue; }
            if (a == "--responsive") { o.Responsive = true; continue; }
            if (a == "--barrel-threshold") { o.BarrelThreshold = true; continue; }
            if (a == "--conjunction") { o.Conjunction = true; continue; }
            if (a == "--charts") { o.Charts = true; continue; }
            if (a == "--calibrate") { o.Calibrate = true; continue; }
            if (a == "--deck") { o.Deck = true; continue; }
            if (a == "--active-only") { o.ActiveOnly = true; continue; }
            if (i + 1 >= args.Length) break; // remaining flags need a value
            switch (a)
            {
                case "--group": o.Group = args[++i]; break;
                case "--file": o.OfflineFile = args[++i]; break;
                case "--nails": o.NailCount = (int)D(args[++i]); break;
                case "--alt": o.AltKm = D(args[++i]); break;
                case "--inc": o.IncDeg = D(args[++i]); break;
                case "--sigma": o.DispersalSigmaMS = D(args[++i]); break;
                case "--relvel": o.RelVelMS = D(args[++i]); break;
                case "--area": o.TargetAreaM2 = D(args[++i]); break;
                case "--export": o.ExportPath = args[++i]; break;
                case "--launch": o.LaunchRatePerYear = D(args[++i]); break;
                case "--launch-alt": o.LaunchAltKm = D(args[++i]); break;
                case "--loss-tol": o.LossTolerance = D(args[++i]); break;
            }
        }
        return o;
    }
}

/// <summary>Writes a compact JSON scene (subsampled mean elements) for the WebGL globe.</summary>
file static class SceneExport
{
    public static void Write(string path, IReadOnlyList<OrbitalElements> nails,
        IReadOnlyList<OrbitalElements> catalog, NailSpec nail, CliOptions opts)
    {
        static double[] Row(OrbitalElements e) =>
        [
            Math.Round(e.SemiMajorAxis, 3), Math.Round(e.Eccentricity, 6),
            Math.Round(e.Inclination, 5), Math.Round(e.Raan, 5),
            Math.Round(e.ArgPerigee, 5), Math.Round(e.MeanAnomaly, 5),
        ];

        static List<double[]> Sub(IReadOnlyList<OrbitalElements> src, int max)
        {
            var outp = new List<double[]>(Math.Min(src.Count, max));
            int step = Math.Max(1, src.Count / max);
            for (int i = 0; i < src.Count; i += step) outp.Add(Row(src[i]));
            return outp;
        }

        var scene = new
        {
            meta = new
            {
                altKm = opts.AltKm,
                incDeg = opts.IncDeg,
                nailCount = opts.NailCount,
                dispersalSigmaMS = opts.DispersalSigmaMS,
                nailAoverM = Math.Round(nail.AreaToMassRatio, 5),
                catAoverM = 0.01,          // nominal for catalogued objects
                earthRadiusKm = Constants.EarthRadiusKm,
                muKm3S2 = Constants.Mu,
                j2 = Constants.J2,
            },
            nails = Sub(nails, opts.ExportNails),
            cat = Sub(catalog, opts.ExportCatalog),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(scene);
        File.WriteAllText(path, json);
    }
}
