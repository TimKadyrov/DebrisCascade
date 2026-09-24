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
    public void Removal_TakesTheRequestedCount_AndSlowsBeltGrowth()
    {
        var cat = SynthCatalog(4000, 750, 1050);
        var none = new KesslerEvolution(new NailSpec());
        var adr = new KesslerEvolution(new NailSpec()) { RemovalsPerYear = 10 };
        none.SeedFromCatalog(cat); adr.SeedFromCatalog(cat);
        var a = none.Run(horizonYears: 20, dtDays: 10);
        var b = adr.Run(horizonYears: 20, dtDays: 10);

        Assert.Equal(200, adr.RemovalsTotal, 1);   // 10 a year for 20 years
        Assert.Equal(0, none.RemovalsTotal);
        Assert.True(b.BeltTrackableObjects[^1] < a.BeltTrackableObjects[^1], "removal should slow belt growth");
    }

    [Fact]
    public void WorkingSatellites_RetireWithTheSetDisposalRate()
    {
        var cat = SynthCatalog(4000, 750, 1050);
        var m = new KesslerEvolution(new NailSpec())
        {
            WorkingSatellites = true, LaunchRatePerYear = 100, DisposalSuccess = 0.9, SatelliteLifetimeYears = 5,
        };
        m.SeedFromCatalog(cat);
        var r = m.Run(horizonYears: 20, dtDays: 10);

        // 85 working satellites a year for 20 years, 5-year life: about 85·(20 − 5·(1 − e^-4)) ≈ 1,280 retired,
        // 90% of them deorbited. The fleet settles near 85·5 = 425.
        double retired = m.DisposedTotal + m.FailedDisposalTotal;
        Assert.InRange(retired, 1150, 1400);
        Assert.Equal(0.9, m.DisposedTotal / retired, 3);
        Assert.InRange(r.WorkingSatellites[^1], 380, 440);
    }

    [Fact]
    public void WorkingSatellites_WithPerfectDisposal_LeaveTheBeltBelowDerelictTraffic()
    {
        var cat = SynthCatalog(4000, 750, 1050);
        double Belt(bool working, double pmd, double avoid)
        {
            var m = new KesslerEvolution(new NailSpec())
            {
                LaunchRatePerYear = 200, WorkingSatellites = working, DisposalSuccess = pmd,
                RocketBodyDisposal = pmd, AvoidanceSuccess = avoid,
            };
            m.SeedFromCatalog(cat);
            return m.Run(horizonYears: 30, dtDays: 10).BeltTrackableObjects[^1];
        }
        double derelict = Belt(false, 0, 0), poor = Belt(true, 0.5, 0.5), perfect = Belt(true, 1.0, 1.0);
        Assert.True(perfect < poor && poor < derelict, $"perfect {perfect:N0}, poor {poor:N0}, derelict {derelict:N0}");
    }

    [Fact]
    public void WorkingSatellites_DodgeTrackedObjects_ButNotSmallDebris()
    {
        var cat = SynthCatalog(4000, 750, 1050);
        (double Avoided, double Killed) Run(double avoid)
        {
            var m = new KesslerEvolution(new NailSpec())
            {
                LaunchRatePerYear = 200, WorkingSatellites = true, AvoidanceSuccess = avoid, ManoeuvrableFraction = 1.0,
            };
            m.SeedFromCatalog(cat);
            m.Run(horizonYears: 10, dtDays: 10);
            return (m.AvoidedTotal, m.MissionKillsTotal);
        }
        var none = Run(0.0); var full = Run(1.0);
        Assert.Equal(0, none.Avoided);
        Assert.True(full.Avoided > 0);
        // Mission kills come from untracked 1–10 cm debris, which avoidance can't touch.
        Assert.True(full.Killed > 0.5 * none.Killed, $"kills with avoidance {full.Killed:F1} vs without {none.Killed:F1}");
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
