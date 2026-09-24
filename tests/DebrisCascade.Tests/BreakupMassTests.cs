using System;
using System.Linq;
using DebrisCascade.Core;
using Xunit;

namespace DebrisCascade.Tests;

/// <summary>Size ↔ mass relations and mass-limited fragment counts shared by the cascade engines.</summary>
public class BreakupMassTests
{
    private static readonly double[] Edges = BreakupModel.SizeBinEdges;

    [Fact]
    public void IntactMass_IsContinuous_NoJumpAt8cm()
    {
        // The bulk-density law meets solid aluminium at ~1.05 cm; no discontinuity anywhere.
        foreach (double lc in new[] { 0.0105, 0.08, 0.3, 1.0 })
        {
            double lo = BreakupModel.IntactMassFromLc(lc * 0.999), hi = BreakupModel.IntactMassFromLc(lc * 1.001);
            Assert.InRange(hi / lo, 1.0, 1.01);
        }
        // 5.6 cm: ~73 g on the power law, not the 248 g a solid-Al switch at 8 cm gave.
        Assert.InRange(BreakupModel.IntactMassFromLc(0.0562), 0.06, 0.09);
    }

    [Fact]
    public void FragmentAreaToMass_MatchesBreakupModelBranches()
    {
        Assert.InRange(BreakupModel.FragmentAreaToMass(0.0178), 0.45, 0.55);  // small-object branch, 10^-0.3
        Assert.InRange(BreakupModel.FragmentAreaToMass(0.0562), 0.095, 0.105); // 10^-1.0
        Assert.InRange(BreakupModel.FragmentAreaToMass(1.78), 0.06, 0.12);     // spacecraft branch
        // Fragments are far lighter than intact objects of the same size.
        foreach (double lc in new[] { 0.02, 0.1, 0.5, 2.0 })
            Assert.True(BreakupModel.FragmentMassFromLc(lc) < BreakupModel.IntactMassFromLc(lc));
    }

    [Fact]
    public void DistributeFragments_KeepsSmallCountsAndConservesMass()
    {
        // Catastrophic 180 + 180 kg: the ≥1 cm count should follow the power law, not be
        // suppressed by a notional multi-tonne fragment (the old uniform-scale bug gave ~1,400).
        double meff = 360, avail = 360;
        var mass = Edges.Zip(Edges.Skip(1), (a, b) => BreakupModel.FragmentMassFromLc(Math.Sqrt(a * b))).ToArray();
        var cnt = new double[mass.Length];
        BreakupModel.DistributeFragments(meff, avail, Edges, mass, cnt);

        double total = cnt.Sum(), sbm1cm = BreakupModel.CountLargerThan(meff, 0.01);
        Assert.InRange(total, 0.9 * sbm1cm, sbm1cm);
        Assert.Equal(BreakupModel.CountLargerThan(meff, 0.01) - BreakupModel.CountLargerThan(meff, 0.0316), cnt[0], 3);

        double used = cnt.Select((n, c) => n * mass[c]).Sum();
        Assert.True(used <= avail * (1 + 1e-9), $"fragments carry {used:F1} kg > {avail} kg available");
        Assert.True(cnt[^1] == 0, "no 3–10 m fragments from a 360 kg breakup");
    }

    [Fact]
    public void ExplosionFragments_FollowTheExplosionLaw_AndConserveMass()
    {
        // Breakup-model explosion branch: N(>Lc) = 6·S·Lc^-1.6 → ~9,500 fragments ≥1 cm for S = 1.
        Assert.InRange(BreakupModel.ExplosionCountLargerThan(0.01), 9_400, 9_600);

        var mass = Edges.Zip(Edges.Skip(1), (a, b) => BreakupModel.FragmentMassFromLc(Math.Sqrt(a * b))).ToArray();
        var cnt = new double[mass.Length];
        BreakupModel.DistributeExplosionFragments(1500, Edges, mass, cnt);   // a rocket upper stage
        Assert.InRange(cnt.Sum(), 0.9 * BreakupModel.ExplosionCountLargerThan(0.01), BreakupModel.ExplosionCountLargerThan(0.01));
        Assert.True(cnt.Select((n, c) => n * mass[c]).Sum() <= 1500 * (1 + 1e-9));

        BreakupModel.DistributeExplosionFragments(1500, Edges, mass, cnt, scale: 0);
        Assert.Equal(0, cnt.Sum());
    }

    [Fact]
    public void DistributeFragments_TinyBudget_ProducesFewFragments()
    {
        var mass = Edges.Zip(Edges.Skip(1), (a, b) => BreakupModel.FragmentMassFromLc(Math.Sqrt(a * b))).ToArray();
        var cnt = new double[mass.Length];
        BreakupModel.DistributeFragments(1.0, 1e-4, Edges, mass, cnt);   // 0.1 g available
        Assert.True(cnt.Sum() * mass[0] <= 1e-4 * (1 + 1e-9));
    }
}
