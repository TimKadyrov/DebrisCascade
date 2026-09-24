using System;

namespace DebrisCascade.Core;

/// <summary>Physical description of a single nail (default: 75 mm × 3 mm carbon-steel framing nail).</summary>
public sealed class NailSpec
{
    public double LengthM { get; init; } = 0.075;
    public double DiameterM { get; init; } = 0.003;
    public double DensityKgM3 { get; init; } = Constants.SteelDensity;

    /// <summary>Mass [kg], modelling the nail as a solid steel cylinder (head neglected).</summary>
    public double MassKg
    {
        get
        {
            double radius = DiameterM / 2.0;
            double volume = Math.PI * radius * radius * LengthM;
            return DensityKgM3 * volume;
        }
    }

    /// <summary>
    /// Orientation-averaged projected (collision) cross-section [m²] for a randomly
    /// tumbling convex body: Cauchy's theorem, mean projected area = surface area / 4.
    /// </summary>
    public double MeanCrossSectionM2
    {
        get
        {
            double radius = DiameterM / 2.0;
            double lateral = Math.PI * DiameterM * LengthM;    // side wall
            double ends = 2.0 * Math.PI * radius * radius;     // two end caps
            return (lateral + ends) / 4.0;
        }
    }

    /// <summary>Characteristic length [m] = mean of the bounding-box dimensions (L × d × d).</summary>
    public double CharacteristicLengthM => (LengthM + DiameterM + DiameterM) / 3.0;

    /// <summary>Area-to-mass ratio [m²/kg] — governs how fast atmospheric drag removes it.</summary>
    public double AreaToMassRatio => MeanCrossSectionM2 / MassKg;
}

/// <summary>
/// A barrel of nails released as a "random tumbling dump": the barrel bursts at a point
/// on its parent orbit and each nail receives a small random Δv (no targeting). This is
/// the least-hostile deployment — the baseline that "it will do no harm" skeptics assume.
/// </summary>
public sealed class NailBarrel
{
    public NailSpec Nail { get; init; } = new();
    public int Count { get; init; } = 200_000;

    public double TotalMassKg => Count * Nail.MassKg;

    /// <summary>
    /// Deploy the cloud. Each nail's velocity is perturbed by an independent per-axis
    /// Gaussian Δv (standard deviation <paramref name="dispersalSigmaMetersPerSec"/> per
    /// ECI axis), so the RMS dispersal speed ≈ σ·√3. Returns one mean-element set per nail,
    /// each with its epoch at the release instant.
    /// </summary>
    public OrbitalElements[] Deploy(
        OrbitalElements parent,
        double releaseTimeSecFromParentEpoch,
        double dispersalSigmaMetersPerSec,
        int seed = 12345)
    {
        var (pos, vel) = parent.StateAt(releaseTimeSecFromParentEpoch);
        var rng = new Random(seed);
        double sigmaKmS = dispersalSigmaMetersPerSec / 1000.0;

        var cloud = new OrbitalElements[Count];
        for (int k = 0; k < Count; k++)
        {
            var dv = new Vec3(
                sigmaKmS * NextGaussian(rng),
                sigmaKmS * NextGaussian(rng),
                sigmaKmS * NextGaussian(rng));
            cloud[k] = OrbitalElements.FromStateVector(pos, vel + dv);
        }
        return cloud;
    }

    /// <summary>Standard-normal sample via Box–Muller.</summary>
    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(Constants.TwoPi * u2);
    }
}
