using System;
using System.Collections.Generic;
using System.Linq;
using DebrisCascade.Core;
using DebrisCascade.Interop;
using Xunit;

namespace DebrisCascade.Tests;

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
    public void FormationNeighbours_AreNotCountedAsCollisions()
    {
        // Two payloads in one plane at the same altitude, ~3 km apart along track (constellation neighbours).
        double a = Constants.EarthRadiusKm + 550, rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        var pair = new List<CatalogObject>
        {
            new(OrbitalElements.FromMeanMotionRevPerDay(rev, 0.0001, 53 * Constants.DegToRad, 1.0, 0, 0.0000), 300, 4, true, "PAY", "STARLINK-1"),
            new(OrbitalElements.FromMeanMotionRevPerDay(rev, 0.0001, 53 * Constants.DegToRad, 1.0, 0, 0.0005), 300, 4, true, "PAY", "STARLINK-2"),
        };
        double Rate(bool exclude)
        {
            var c = new ConjunctionCascade(seed: 2) { SubSamples = 64, ExcludeFormationPairs = exclude };
            c.SeedFromCatalog(pair, backgroundSmallTotal: 0, backgroundLargeTotal: 0);
            return c.MeasureCubeRate(30 * 86400.0).ExpectedCollisions;
        }
        Assert.True(Rate(exclude: false) > 0, "without the exclusion the neighbours share cubes");
        Assert.Equal(0, Rate(exclude: true));
    }

    [Fact]
    public void Calibration_GivesRealisticEncounterSpeedAndFiniteFactor()
    {
        var c = new ConjunctionCascade(seed: 6);
        c.SeedFromCatalog(Synth(4000, 600, 1000), backgroundSmallTotal: 2_000_000);
        var (cube, vrel, cubeCat) = c.MeasureCubeRate(30 * 86400.0);
        Assert.InRange(vrel / 1000.0, 3.0, 15.0);     // realistic LEO encounter speed [km/s]
        double kin = c.MeasureKineticRate(30 * 86400.0, vrel);
        Assert.True(cube > 0 && kin > 0);
        Assert.InRange(cube / kin, 0.001, 100.0);     // finite, sane calibration factor
        // Catastrophic collisions are a subset of all collisions, in both methods.
        double kinCat = c.MeasureKineticCatastrophic(30 * 86400.0, 10_000.0);
        Assert.InRange(cubeCat, 0, cube);
        Assert.InRange(kinCat, 0, c.MeasureKineticRate(30 * 86400.0, 10_000.0) * (1 + 1e-9));
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
    public void HugeObjects_HighAltitude_StayBounded()
    {
        // Reproduces the OOM case: 750x750 mm "nails" (~2.6 t each) at 1300 km (no drag).
        var c = new ConjunctionCascade(seed: 9);
        c.SeedFromCatalog(Synth(2000, 1200, 1400));
        var nail = new NailSpec { LengthM = 0.75, DiameterM = 0.75 };
        double a = Constants.EarthRadiusKm + 1300;
        double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        var parent = OrbitalElements.FromMeanMotionRevPerDay(rev, 0, 60 * Constants.DegToRad, 0, 0, 0);
        var cloud = new NailBarrel { Nail = nail, Count = 20000 }.Deploy(parent, 0, 60, seed: 3);
        c.InjectBarrel(cloud, nail, 3000, 200_000);
        var r = c.Run(horizonYears: 3, dtDays: 90);   // enough steps to trigger any blow-up
        Assert.All(r.TotalObjects, v => Assert.True(double.IsFinite(v) && v >= 0));
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

    [Fact]
    public void Fragments_DecayUnderDrag_LikeTheBoxModel()
    {
        // One breakup at 900 km, collisions off: 10-100 cm fragments from 900 km live ~50-80 years, so after 100
        // years most are gone in both engines. (A drag bug once froze circularised fragments at their old perigee.)
        var box = new KesslerEvolution(new NailSpec()) { CollisionsOff = true, ExplosionsPerYear = 0 };
        box.InjectBreakup(900, 1550, 10);
        var br = box.Run(100, 10);
        var cube = new ConjunctionCascade(seed: 1) { CollisionsOff = true, ExplosionsPerYear = 0, FragmentSplit = 64 };
        cube.InjectBreakup(900, 98, 1550, 10);
        double c0 = cube.TotalTrackable();
        var cr = cube.Run(100, 60);
        double boxLeft = br.TrackableObjects[^1] / br.TrackableObjects[0], cubeLeft = cr.TrackableObjects[^1] / c0;
        Assert.InRange(cubeLeft, 0.05, 0.8);
        Assert.InRange(cubeLeft / boxLeft, 0.4, 2.5);
    }
}
