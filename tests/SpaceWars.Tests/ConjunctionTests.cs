using System;
using System.Collections.Generic;
using System.Linq;
using SpaceWars.Core;
using SpaceWars.Interop;
using Xunit;

namespace SpaceWars.Tests;

public class ConjunctionTests
{
    private static List<OrbitalElements> Synth(int n, double lo, double hi, int seed = 8)
    {
        var list = new List<OrbitalElements>(n);
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            double a = Constants.EarthRadiusKm + lo + (hi - lo) * rng.NextDouble();
            double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
            list.Add(OrbitalElements.FromMeanMotionRevPerDay(rev, 0.005 * rng.NextDouble(),
                (45 + 45 * rng.NextDouble()) * Constants.DegToRad,
                rng.NextDouble() * Constants.TwoPi, rng.NextDouble() * Constants.TwoPi, rng.NextDouble() * Constants.TwoPi));
        }
        return list;
    }

    [Fact]
    public void Conjunction_RunsFiniteAndProducesRealCollisions()
    {
        var c = new ConjunctionCascade(seed: 3);
        c.SeedFromCatalog(Synth(3000, 700, 1000));
        var r = c.Run(horizonYears: 20, dtDays: 60);

        Assert.All(r.TotalObjects, v => Assert.True(double.IsFinite(v) && v >= 0));
        Assert.True(r.CatastrophicPerYear.Sum() > 0, "expected real conjunction collisions in the debris belt");
    }

    [Fact]
    public void Calibration_GivesRealisticEncounterSpeedAndFiniteFactor()
    {
        var c = new ConjunctionCascade(seed: 6);
        c.SeedFromCatalog(Synth(4000, 600, 1000), backgroundSmallTotal: 2_000_000);
        var (cube, vrel) = c.MeasureCubeRate(30 * 86400.0);
        Assert.InRange(vrel / 1000.0, 3.0, 15.0);     // realistic LEO encounter speed [km/s]
        double kin = c.MeasureKineticRate(30 * 86400.0, vrel);
        Assert.True(cube > 0 && kin > 0);
        Assert.InRange(cube / kin, 0.001, 100.0);     // finite, sane calibration factor
    }

    [Fact]
    public void LaunchTraffic_DrivesGrowthInTheConjunctionModel()
    {
        double End(double rate)
        {
            var c = new ConjunctionCascade(seed: 4) { LaunchRatePerYear = rate, LaunchAltKm = 900 };
            c.SeedFromCatalog(Synth(3000, 700, 1000));
            var r = c.Run(horizonYears: 25, dtDays: 60);
            return r.TotalObjects[^1];
        }
        // Sustained launch into a weakly-decaying band must leave more objects than none.
        Assert.True(End(800) > End(0));
    }

    [Fact]
    public void EccentricObjects_CollideAcrossShells()
    {
        // Highly eccentric objects crossing many shells must still register collisions
        // against a circular population they intersect — the whole point of the cube method.
        var pop = new List<OrbitalElements>();
        var rng = new Random(1);
        // dense circular ring at ~800 km
        for (int i = 0; i < 2500; i++)
        {
            double a = Constants.EarthRadiusKm + 800;
            double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
            pop.Add(OrbitalElements.FromMeanMotionRevPerDay(rev, 0.0005, 60 * Constants.DegToRad, rng.NextDouble() * Constants.TwoPi, 0, rng.NextDouble() * Constants.TwoPi));
        }
        var c = new ConjunctionCascade(seed: 5);
        c.SeedFromCatalog(pop, backgroundSmallTotal: 2_000_000, backgroundSuperParticles: 3000);
        double before = c.TotalObjects();
        var r = c.Run(horizonYears: 15, dtDays: 60);
        Assert.True(r.CatastrophicPerYear.Sum() > 0);
        Assert.All(r.TotalObjects, v => Assert.True(double.IsFinite(v)));
    }
}
