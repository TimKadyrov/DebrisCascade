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

        // Debris classes carry breakup-model fragment masses (flat, light): a 3–10 cm fragment
        // (~17 g) only craters a ~180 kg intact, while a 30 cm–1 m fragment (~2.6 kg) shatters it.
        Assert.False(Lethality.IsCatastrophic(c[0].MassKg, 10_000, c[4].MassKg));
        Assert.False(Lethality.IsCatastrophic(c[1].MassKg, 10_000, c[4].MassKg));
        Assert.True(Lethality.IsCatastrophic(c[3].MassKg, 10_000, c[4].MassKg));
        Assert.InRange(c[1].MassKg, 0.010, 0.030);
        Assert.InRange(c[4].MassKg, 120, 250);   // intact payload class stays ~180 kg
    }

    [Fact]
    public void Explosions_HitTheCalibratedRate_AndFeedSmallDebris()
    {
        var cat = SynthCatalog(4000, 700, 1000);
        var withEx = new KesslerEvolution(new NailSpec()) { ExplosionsPerYear = 4, ExplosionScale = 1.0 };
        var noEx = new KesslerEvolution(new NailSpec()) { ExplosionsPerYear = 0 };
        withEx.SeedFromCatalog(cat); noEx.SeedFromCatalog(cat);
        var a = withEx.Run(horizonYears: 10, dtDays: 10);
        var b = noEx.Run(horizonYears: 10, dtDays: 10);

        // ~4 a year at the seeded population (drag slowly thins the intacts, so a little under 40).
        Assert.InRange(withEx.ExplosionsTotal, 30, 41);
        Assert.Equal(0, noEx.ExplosionsTotal);
        Assert.True(a.TotalObjects[^1] > b.TotalObjects[^1] + 50_000, "explosions should add small debris");
    }

    [Fact]
    public void Run_EndsExactlyOnTheHorizon()
    {
        var m = new KesslerEvolution(new NailSpec());
        m.SeedFromCatalog(SynthCatalog(500, 500, 900));
        var r = m.Run(horizonYears: 50, dtDays: 10);   // 50 yr is not a whole number of 10-day steps
        Assert.Equal(51, r.Years.Length);
        Assert.Equal(50.0, r.Years[^1], 6);
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
