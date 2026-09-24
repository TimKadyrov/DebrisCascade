using System;
using System.Collections.Generic;
using System.Linq;
using DebrisCascade.Core;
using Xunit;

namespace DebrisCascade.Tests;

public class CascadeTests
{
    private static List<OrbitalElements> Synth(int n, double lo, double hi, int seed = 5)
    {
        var list = new List<OrbitalElements>(n);
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            double a = Constants.EarthRadiusKm + lo + (hi - lo) * rng.NextDouble();
            double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
            list.Add(OrbitalElements.FromMeanMotionRevPerDay(rev, 0.001,
                (45 + 45 * rng.NextDouble()) * Constants.DegToRad,
                rng.NextDouble() * Constants.TwoPi, 0, rng.NextDouble() * Constants.TwoPi));
        }
        return list;
    }

    [Fact]
    public void Run_StaysFiniteAndProducesFragmentEvents()
    {
        var c = new DiscreteCascade(seed: 11);
        c.SeedFromCatalog(Synth(4000, 500, 900));
        var r = c.Run(horizonYears: 20, dtDays: 20);

        Assert.All(r.TotalObjects, v => Assert.True(double.IsFinite(v) && v >= 0));
        Assert.True(r.CatastrophicPerYear.Sum() > 0, "expected fragmentation events");
    }

    [Fact]
    public void InjectedNails_RainOut()
    {
        var c = new DiscreteCascade(seed: 2);
        c.SeedFromCatalog(Synth(2000, 450, 650));
        var cloud = new NailBarrel { Count = 20_000 }.Deploy(
            MakeCircular(500, 53), 0, 60, seed: 4);
        c.InjectBarrel(cloud, new NailSpec(), superParticles: 1000, totalNails: 200_000);
        Assert.True(c.TotalNails() > 150_000);

        var r = c.Run(horizonYears: 20, dtDays: 15);
        Assert.True(r.SurvivingNails[^1] < 0.02 * 200_000, $"nails left: {r.SurvivingNails[^1]:N0}");
    }

    [Fact]
    public void DenserStart_YieldsMoreCatastrophicEvents()
    {
        double Events(double bg)
        {
            var c = new DiscreteCascade(seed: 7);
            c.SeedFromCatalog(Synth(4000, 700, 950), backgroundSmallTotal: bg);
            return c.Run(horizonYears: 20, dtDays: 20).CatastrophicPerYear.Sum();
        }
        Assert.True(Events(3_000_000) > Events(500_000));
    }

    private static OrbitalElements MakeCircular(double altKm, double incDeg)
    {
        double a = Constants.EarthRadiusKm + altKm;
        double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        return OrbitalElements.FromMeanMotionRevPerDay(rev, 0.0, incDeg * Constants.DegToRad, 0, 0, 0);
    }
}
