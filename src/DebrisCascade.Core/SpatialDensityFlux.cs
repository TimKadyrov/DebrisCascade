using System;
using System.Collections.Generic;
using System.Linq;

namespace DebrisCascade.Core;

/// <summary>Per-altitude-shell collision statistics.</summary>
public sealed class ShellStat
{
    public double AltLowKm;
    public double AltHighKm;
    public int CatalogCount;
    public long NailCount;
    public double VolumeM3;

    public double MidAltKm => 0.5 * (AltLowKm + AltHighKm);
    public double CatalogDensity => VolumeM3 > 0 ? CatalogCount / VolumeM3 : 0; // 1/m^3
    public double NailDensity => VolumeM3 > 0 ? NailCount / VolumeM3 : 0;       // 1/m^3

    /// <summary>Expected nail impacts on ONE satellite in this shell per year.</summary>
    public double NailImpactsPerSatPerYear;
    /// <summary>Expected total nail↔catalog collisions in this shell per year.</summary>
    public double TotalCollisionsPerYear;
}

/// <summary>
/// First-order collision flux via the spatial-density (Kessler) method: bin objects into
/// spherical altitude shells, treat each shell as well-mixed, and compute the pairwise
/// collision rate from number density, combined cross-section and relative velocity:
///     rate = n_nail · n_target · V · σ · v_rel   [collisions/s per shell]
///
/// This is the CPU reference the CUDA per-conjunction engine will later refine (real
/// geometry, per-pair relative velocities, latitude banding).
/// </summary>
public sealed class SpatialDensityFlux
{
    public double BinKm { get; init; } = 25.0;
    public double MinAltKm { get; init; } = 200.0;
    public double MaxAltKm { get; init; } = 2000.0;

    /// <summary>Average closing speed for random LEO encounters [m/s].</summary>
    public double RelVelMetersPerSec { get; init; } = 10_000.0;

    /// <summary>Representative catalog-object cross-section [m²] (assumption; refine with RCS/SATCAT).</summary>
    public double TargetAreaM2 { get; init; } = 5.0;

    private const double ReM = 6_378_135.0;         // Earth radius [m]
    private const double SecondsPerYear = 3.15576e7;

    public IReadOnlyList<ShellStat> Analyze(
        IReadOnlyList<OrbitalElements> catalog,
        IReadOnlyList<OrbitalElements> nailCloud,
        NailSpec nail)
    {
        int nBins = (int)Math.Ceiling((MaxAltKm - MinAltKm) / BinKm);
        var shells = new ShellStat[nBins];
        for (int i = 0; i < nBins; i++)
        {
            double lo = MinAltKm + i * BinKm;
            double hi = lo + BinKm;
            double rLo = ReM + lo * 1000.0;
            double rHi = ReM + hi * 1000.0;
            shells[i] = new ShellStat
            {
                AltLowKm = lo,
                AltHighKm = hi,
                VolumeM3 = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo),
            };
        }

        foreach (var el in catalog)
        {
            int b = BinIndex(el.SemiMajorAxis - Constants.EarthRadiusKm, nBins);
            if (b >= 0) shells[b].CatalogCount++;
        }
        foreach (var el in nailCloud)
        {
            int b = BinIndex(el.SemiMajorAxis - Constants.EarthRadiusKm, nBins);
            if (b >= 0) shells[b].NailCount++;
        }

        // Combined collision cross-section ≈ (√A_target + √A_nail)^2.
        double sigma = Math.Pow(Math.Sqrt(TargetAreaM2) + Math.Sqrt(nail.MeanCrossSectionM2), 2.0);

        foreach (var s in shells)
        {
            if (s.VolumeM3 <= 0) continue;
            double nNail = s.NailDensity;
            double perSatPerSec = nNail * sigma * RelVelMetersPerSec;
            s.NailImpactsPerSatPerYear = perSatPerSec * SecondsPerYear;
            s.TotalCollisionsPerYear = perSatPerSec * s.CatalogCount * SecondsPerYear;
        }

        return shells;
    }

    private int BinIndex(double altKm, int nBins)
    {
        if (altKm < MinAltKm || altKm >= MaxAltKm) return -1;
        return (int)((altKm - MinAltKm) / BinKm);
    }

    /// <summary>Total expected nail↔catalog collisions per year across all shells.</summary>
    public static double TotalCollisionsPerYear(IReadOnlyList<ShellStat> shells)
        => shells.Sum(s => s.TotalCollisionsPerYear);
}
