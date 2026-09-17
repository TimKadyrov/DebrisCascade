using System;
using SpaceWars.Core;
using Xunit;

namespace SpaceWars.Tests;

public class PhysicsTests
{
    [Theory]
    [InlineData(0.0, 0.1)]
    [InlineData(1.5, 0.2)]
    [InlineData(3.0, 0.7)]
    [InlineData(-2.0, 0.05)]
    public void KeplerSolver_SatisfiesEquation(double m, double e)
    {
        double eAnom = OrbitalElements.SolveKepler(m, e);
        double residual = eAnom - e * Math.Sin(eAnom) - m;
        // residual is checked modulo 2π since M was wrapped internally.
        residual = Math.IEEERemainder(residual, Constants.TwoPi);
        Assert.True(Math.Abs(residual) < 1e-9, $"residual={residual}");
    }

    [Fact]
    public void ElementStateRoundTrip_PreservesOrbit()
    {
        var el = OrbitalElements.FromMeanMotionRevPerDay(
            meanMotionRevPerDay: 15.5,           // ~ISS altitude
            e: 0.0012,
            iRad: 51.6 * Constants.DegToRad,
            raanRad: 120.0 * Constants.DegToRad,
            argpRad: 45.0 * Constants.DegToRad,
            meanAnomalyRad: 200.0 * Constants.DegToRad);

        var (r, v) = el.StateAt(0.0);
        var back = OrbitalElements.FromStateVector(r, v);

        Assert.Equal(el.SemiMajorAxis, back.SemiMajorAxis, 3);   // km
        Assert.Equal(el.Eccentricity, back.Eccentricity, 6);
        Assert.Equal(el.Inclination, back.Inclination, 6);       // rad
        Assert.Equal(el.Raan, back.Raan, 6);
        Assert.Equal(el.ArgPerigee, back.ArgPerigee, 5);
    }

    [Fact]
    public void SunSynchronous_HasCorrectNodalRegression()
    {
        // ~800 km SSO: i≈98.6°, node should regress eastward at ~1.99e-7 rad/s
        // (one full turn per year) to stay sun-synchronous.
        var el = OrbitalElements.FromMeanMotionRevPerDay(
            meanMotionRevPerDay: 14.28, e: 0.001,
            iRad: 98.6 * Constants.DegToRad, raanRad: 0, argpRad: 0, meanAnomalyRad: 0);

        Assert.InRange(el.RaanDot, 1.75e-7, 2.15e-7);
    }

    [Fact]
    public void OrbitPropagation_ConservesRadiusForNearCircular()
    {
        var el = OrbitalElements.FromMeanMotionRevPerDay(15.5, 0.0, 51.6 * Constants.DegToRad, 0, 0, 0);
        double r0 = el.PositionAt(0).Length;
        double r1 = el.PositionAt(1800).Length; // half an orbit later
        Assert.Equal(r0, r1, 1); // km, near-circular so radius ~ constant
    }
}

public class LethalityAndBreakupTests
{
    private static readonly NailSpec Nail = new();

    [Fact]
    public void DefaultNail_HasRealisticMassAndCrossSection()
    {
        Assert.InRange(Nail.MassKg, 0.003, 0.006);          // ~4 g steel framing nail
        Assert.InRange(Nail.MeanCrossSectionM2, 1e-4, 3e-4); // tumbling projected area
        Assert.InRange(Nail.CharacteristicLengthM, 0.02, 0.03); // a >1 cm object
    }

    [Fact]
    public void SingleNail_MissionKillsButDoesNotShatterAStarlink()
    {
        // 260 kg satellite. A ~4 g nail at 10 km/s carries ~200 kJ → EMR well under
        // 40 J/g, so it is NON-catastrophic: it cripples and craters the satellite
        // rather than fully fragmenting it.
        var f = BreakupModel.Fragment(targetMassKg: 260.0, projectileMassKg: Nail.MassKg,
                                       relVelMetersPerSec: 10_000);
        Assert.False(f.Catastrophic);
        Assert.InRange(f.EmrJoulesPerGram, 0.5, 1.5);
        Assert.InRange(f.CountLargerThan1cm, 80, 250); // cratering ejecta, not a full cloud
    }

    [Fact]
    public void SingleNail_CatastrophicallyDestroysOnlySmallTargets()
    {
        // The largest target a 4 g nail at 10 km/s can fully fragment is only ~5 kg.
        double maxKg = Lethality.MaxCatastrophicTargetMassKg(Nail.MassKg, 10_000);
        Assert.InRange(maxKg, 4.0, 6.5);

        var cube = BreakupModel.Fragment(3.0, Nail.MassKg, 10_000); // 3 kg cubesat
        Assert.True(cube.Catastrophic);
        Assert.InRange(cube.CountLargerThan1cm, 400, 900);
    }

    [Fact]
    public void LargeLargeCollision_IsTheRealCascadeSeed()
    {
        // A dead 260 kg satellite struck by a 260 kg satellite at 10 km/s is deeply
        // catastrophic and produces the ~10^4 fragment cloud people picture.
        var f = BreakupModel.Fragment(260.0, 260.0, 10_000);
        Assert.True(f.Catastrophic);
        Assert.InRange(f.CountLargerThan1cm, 10_000, 40_000);
        Assert.InRange(f.CountLargerThan10cm, 200, 800);
    }
}

public class DragTests
{
    [Fact]
    public void Density_DecreasesWithAltitude()
    {
        Assert.True(AtmosphericDrag.Density(300) > AtmosphericDrag.Density(550));
        Assert.True(AtmosphericDrag.Density(550) > AtmosphericDrag.Density(800));
        Assert.InRange(AtmosphericDrag.Density(550), 1e-13, 6e-13); // Vallado ~3e-13
    }

    [Fact]
    public void NailLifetime_IsYearsAt550_ShortLowerDown()
    {
        double aM = new NailSpec().AreaToMassRatio; // ~0.043 m^2/kg
        double life550 = AtmosphericDrag.LifetimeDays(550, aM);
        double life300 = AtmosphericDrag.LifetimeDays(300, aM);

        Assert.InRange(life550, 365, 11_000);   // ~1–30 years
        Assert.True(life300 < 365);              // months at 300 km
        Assert.True(life300 < life550);
    }

    [Fact]
    public void HighAltitude_IsEffectivelyPersistent()
    {
        double aM = new NailSpec().AreaToMassRatio;
        Assert.True(AtmosphericDrag.LifetimeDays(1000, aM) > 50 * 365); // >50 yr
    }
}
