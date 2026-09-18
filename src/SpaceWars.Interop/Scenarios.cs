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
    public double DispersalSigmaMS { get; set; } = 50;
    public double RelVelMS { get; set; } = 10_000;
    public double LaunchRatePerYear { get; set; } = 0;
    public double LaunchAltKm { get; set; } = 900;
    public bool Responsive { get; set; } = false;
    public double LossTolerance { get; set; } = 0.02;
    public double SolarActivity { get; set; } = 1.0;
    public double HorizonYears { get; set; } = 50;
}

/// <summary>The loaded catalog plus its provenance and derived stats.</summary>
public sealed class CatalogBundle
{
    public required IReadOnlyList<CatalogObject> Objects { get; init; }
    public required string Source { get; init; }
    public int TotalTracked { get; init; }
    public int WithRcs { get; init; }
    public double MeanAreaM2 { get; init; }
    public double MeanMassKg { get; init; }
}

/// <summary>Orchestrates the model runs from a set of inputs — the reusable analysis layer.</summary>
public static class Scenarios
{
    private static readonly NailSpec Nail = new();

    public static async Task<CatalogBundle> LoadCatalogAsync(string dataDir, string group = "active")
    {
        var tles = await new CelesTrakClient(dataDir).GetGroupAsync(group);

        Dictionary<int, SatcatRecord> satcat = new(); string src = "none";
        try
        {
            if (SpaceTrackClient.HasCredentials) { satcat = await new SpaceTrackClient(dataDir).GetSatcatAsync(); src = "Space-Track"; }
            else { satcat = await new CelesTrakClient(dataDir).GetSatcatAsync(); src = "CelesTrak"; }
        }
        catch { try { satcat = await new CelesTrakClient(dataDir).GetSatcatAsync(); src = "CelesTrak"; } catch { src = "none"; } }

        int withRcs = 0;
        var objs = new List<CatalogObject>();
        foreach (var t in tles)
        {
            var el = t.ToElements();
            if (!(el.PerigeeAltitude < 2000 && el.PerigeeAltitude > 100)) continue;
            double mass = 180, area = 1.78; bool intact = true;
            if (satcat.TryGetValue(t.NoradId, out var rec)) { (mass, area) = Satcat.DeriveMassArea(rec); intact = Satcat.IsIntact(rec.ObjectType); if (rec.RcsM2.HasValue) withRcs++; }
            objs.Add(new CatalogObject(el, mass, area, intact));
        }
        return new CatalogBundle
        {
            Objects = objs, Source = src, TotalTracked = tles.Count, WithRcs = withRcs,
            MeanAreaM2 = objs.Count > 0 ? objs.Average(o => o.AreaM2) : 5.0,
            MeanMassKg = objs.Count > 0 ? objs.Average(o => o.MassKg) : 180.0,
        };
    }

    public static OrbitalElements[] DeployCloud(ScenarioInputs i)
    {
        double a = Constants.EarthRadiusKm + i.AltKm;
        double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        var parent = OrbitalElements.FromMeanMotionRevPerDay(rev, 0, i.IncDeg * Constants.DegToRad, 0, 0, 0);
        return new NailBarrel { Nail = Nail, Count = i.NailCount }.Deploy(parent, 0, i.DispersalSigmaMS);
    }

    public static (EvolutionResult Baseline, EvolutionResult Barrel) Evolution(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var b = new KesslerEvolution(Nail) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance };
        b.SeedFromCatalog(cat);
        var k = new KesslerEvolution(Nail) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance };
        k.SeedFromCatalog(cat); k.InjectBarrel(i.AltKm, i.NailCount);
        return (b.Run(i.HorizonYears, 10), k.Run(i.HorizonYears, 10));
    }

    public static (CascadeResult Baseline, CascadeResult Barrel) Cascade(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var cloud = DeployCloud(i);
        var b = new DiscreteCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance };
        b.SeedFromCatalog(cat);
        var k = new DiscreteCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance };
        k.SeedFromCatalog(cat); k.InjectBarrel(cloud, Nail, 3000, i.NailCount);
        return (b.Run(i.HorizonYears, 15), k.Run(i.HorizonYears, 15));
    }

    public static (CascadeResult Baseline, CascadeResult Barrel, bool UsedGpu) Conjunction(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        var cloud = DeployCloud(i);
        var b = new ConjunctionCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance };
        b.SeedFromCatalog(cat);
        var k = new ConjunctionCascade(seed: 1) { SolarActivity = i.SolarActivity, LaunchRatePerYear = i.LaunchRatePerYear, LaunchAltKm = i.LaunchAltKm, ResponsiveLaunch = i.Responsive, LossTolerancePerYear = i.LossTolerance };
        k.SeedFromCatalog(cat); k.InjectBarrel(cloud, Nail, 3000, i.NailCount);
        var br = b.Run(i.HorizonYears, 60); var kr = k.Run(i.HorizonYears, 60);
        return (br, kr, b.UsedGpu);
    }

    public static (double[] Rates, double[] Growth) Tipping(IReadOnlyList<CatalogObject> cat, ScenarioInputs i)
    {
        double[] rates = { 0, 50, 100, 150, 200, 300, 400, 600, 800, 1200, 1600, 2000 };
        var g = rates.Select(r =>
        {
            var m = new KesslerEvolution(Nail) { SolarActivity = i.SolarActivity, LaunchRatePerYear = r, LaunchAltKm = i.LaunchAltKm };
            m.SeedFromCatalog(cat);
            var rr = m.Run(i.HorizonYears, 10);
            return rr.TotalObjects[^1] / rr.TotalObjects[0];
        }).ToArray();
        return (rates, g);
    }

    /// <summary>Single-nail lethality + spatial-density flux summary as text.</summary>
    public static string LethalityAndFlux(IReadOnlyList<CatalogObject> cat, ScenarioInputs i, double targetAreaM2)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Nail: {Nail.MassKg * 1000:F2} g, σ={Nail.MeanCrossSectionM2 * 1e4:F2} cm², Lc={Nail.CharacteristicLengthM * 100:F1} cm, A/m={Nail.AreaToMassRatio:F3} m²/kg");
        sb.AppendLine($"Barrel: {i.NailCount:N0} nails = {i.NailCount * Nail.MassKg:N0} kg at {i.AltKm:F0} km / {i.IncDeg:F0}°");
        foreach (var solar in new[] { ("solar min", 0.5), ("nominal", 1.0), ("solar max", 4.0) })
        {
            double d = AtmosphericDrag.LifetimeDays(i.AltKm, Nail.AreaToMassRatio, solar.Item2);
            sb.AppendLine($"  lifetime ({solar.Item1}): {(double.IsInfinity(d) ? ">1000 yr" : $"{d / 365.25:F1} yr")}");
        }
        sb.AppendLine();
        foreach (double v in new[] { 7_600.0, 10_000.0, 15_200.0 })
            sb.AppendLine($"  @ {v / 1000,5:F1} km/s: shatters targets ≤ {Lethality.MaxCatastrophicTargetMassKg(Nail.MassKg, v):F1} kg");
        sb.AppendLine();

        var cloud = DeployCloud(i);
        var flux = new SpatialDensityFlux { RelVelMetersPerSec = i.RelVelMS, TargetAreaM2 = targetAreaM2 };
        var shells = flux.Analyze(cat.Select(o => o.Elements).ToList(), cloud, Nail);
        double total = SpatialDensityFlux.TotalCollisionsPerYear(shells);
        sb.AppendLine($"Expected nail mission-kills across LEO: {total:F1} / year (target area {targetAreaM2:F2} m²)");
        var peak = shells.Where(s => s.NailCount > 0).OrderByDescending(s => s.NailImpactsPerSatPerYear).FirstOrDefault();
        if (peak is not null)
            sb.AppendLine($"Peak shell {peak.AltLowKm:F0}-{peak.AltHighKm:F0} km: {peak.NailCount:N0} nails, {peak.CatalogCount:N0} sats");
        return sb.ToString();
    }
}
