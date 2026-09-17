using System;

namespace SpaceWars.Core;

/// <summary>
/// Impact lethality helpers built on the energy-to-mass ratio (EMR) criterion used by
/// the NASA Standard Breakup Model. This is the quantitative core of the "a single nail
/// is not harmless" argument.
/// </summary>
public static class Lethality
{
    /// <summary>Impact kinetic energy [J] of a projectile at a given relative speed.</summary>
    public static double KineticEnergyJoules(double projectileMassKg, double relVelMetersPerSec)
        => 0.5 * projectileMassKg * relVelMetersPerSec * relVelMetersPerSec;

    /// <summary>
    /// Energy-to-mass ratio [J/g] = impact energy divided by the *target* mass.
    /// Compared against <see cref="Constants.CatastrophicEmrThreshold"/> (40 J/g).
    /// </summary>
    public static double Emr(double projectileMassKg, double relVelMetersPerSec, double targetMassKg)
    {
        double eJoules = KineticEnergyJoules(projectileMassKg, relVelMetersPerSec);
        double targetMassGrams = targetMassKg * 1000.0;
        return eJoules / targetMassGrams;
    }

    /// <summary>True if the collision catastrophically fragments the target (EMR ≥ 40 J/g).</summary>
    public static bool IsCatastrophic(double projectileMassKg, double relVelMetersPerSec, double targetMassKg)
        => Emr(projectileMassKg, relVelMetersPerSec, targetMassKg) >= Constants.CatastrophicEmrThreshold;

    /// <summary>
    /// Largest target mass [kg] that a given projectile catastrophically destroys at
    /// this relative speed. (Anything up to this is fully fragmented; heavier targets
    /// are only cratered / mission-killed by a single impact.)
    /// </summary>
    public static double MaxCatastrophicTargetMassKg(double projectileMassKg, double relVelMetersPerSec)
    {
        double eJoules = KineticEnergyJoules(projectileMassKg, relVelMetersPerSec);
        double targetMassGrams = eJoules / Constants.CatastrophicEmrThreshold;
        return targetMassGrams / 1000.0;
    }
}
