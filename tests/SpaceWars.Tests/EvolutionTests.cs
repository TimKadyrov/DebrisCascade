using System;
using System.Collections.Generic;
using System.Linq;
using SpaceWars.Core;
using Xunit;

namespace SpaceWars.Tests;

public class EvolutionTests
{
    private static List<OrbitalElements> SynthCatalog(int n, double loAlt, double hiAlt)
    {
        var list = new List<OrbitalElements>(n);
        var rng = new Random(3);
        for (int i = 0; i < n; i++)
        {
            double alt = loAlt + (hiAlt - loAlt) * rng.NextDouble();
            double a = Constants.EarthRadiusKm + alt;
            double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
            list.Add(OrbitalElements.FromMeanMotionRevPerDay(rev, 0.001,
                (50 + 50 * rng.NextDouble()) * Constants.DegToRad,
                rng.NextDouble() * Constants.TwoPi, 0, rng.NextDouble() * Constants.TwoPi));
        }
        return list;
    }

    [Fact]
    public void SizeClasses_HaveMonotonicMassAndRealisticLethality()
    {
        var m = new KesslerEvolution(new NailSpec());
        var c = m.Classes;
        for (int i = 1; i < m.SizeClassCount; i++)
            Assert.True(c[i].MassKg > c[i - 1].MassKg, $"class {i} not heavier than {i - 1}");

        // A 3–10 cm object (class 1, ~0.25 kg) shatters a ~180 kg intact (class 4); a 1–3 cm doesn't.
        Assert.True(Lethality.IsCatastrophic(c[1].MassKg, 10_000, c[4].MassKg));
        Assert.False(Lethality.IsCatastrophic(c[0].MassKg, 10_000, c[4].MassKg));
    }

    [Fact]
    public void BaselineRun_ProducesCollisionsAndStaysFinite()
    {
        var m = new KesslerEvolution(new NailSpec());
        m.SeedFromCatalog(SynthCatalog(8000, 450, 950));
        var r = m.Run(horizonYears: 30, dtDays: 15);

        Assert.All(r.TotalObjects, v => Assert.True(double.IsFinite(v) && v >= 0));
        Assert.True(r.CatastrophicPerYear.Sum() > 0, "expected some catastrophic collisions over 30 yr");
    }

    [Fact]
    public void MoreBackgroundDebris_DrivesMoreCatastrophicCollisions()
    {
        double Cascade(double bg)
        {
            var m = new KesslerEvolution(new NailSpec());
            m.SeedFromCatalog(SynthCatalog(6000, 700, 900), backgroundSmallTotal: bg);
            return m.Run(horizonYears: 25, dtDays: 15).CatastrophicPerYear.Sum();
        }
        // The N^2 feedback: denser environment ⇒ strictly more catastrophic events.
        Assert.True(Cascade(2_000_000) > Cascade(500_000));
    }

    [Fact]
    public void InjectedNails_DecayAwayWithinYears()
    {
        var m = new KesslerEvolution(new NailSpec());
        m.SeedFromCatalog(SynthCatalog(4000, 450, 650));
        m.InjectBarrel(500, 200_000);
        Assert.Equal(200_000, m.TotalNails(), 0);

        var r = m.Run(horizonYears: 20, dtDays: 10);
        // At ~500 km nails rain out; almost none remain after 20 years.
        Assert.True(r.SurvivingNails[^1] < 0.01 * 200_000, $"nails left: {r.SurvivingNails[^1]:N0}");
    }
}
