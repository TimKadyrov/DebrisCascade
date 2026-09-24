using System;

namespace SpaceWars.Core;

/// <summary>
/// Atmospheric drag decay using Vallado's piecewise-exponential atmosphere (Fundamentals
/// of Astrodynamics, App. B). Gives the density vs altitude, the semi-major-axis decay
/// rate for a circular orbit, and the orbital lifetime — i.e. how fast the nail cloud
/// rains out. A solar-activity multiplier brackets the (large) real-world variation.
/// </summary>
public static class AtmosphericDrag
{
    public const double DragCoefficient = 2.2; // Cd, typical for tumbling debris
    public const double ReentryAltitudeKm = 120.0;

    // Base altitude [km], nominal density [kg/m^3], scale height [km].
    private static readonly (double H0, double Rho0, double Scale)[] Table =
    [
        (0,    1.225,      7.249),
        (150,  2.070e-9,   22.523),
        (180,  5.464e-10,  29.740),
        (200,  2.789e-10,  37.105),
        (250,  7.248e-11,  45.546),
        (300,  2.418e-11,  53.628),
        (350,  9.518e-12,  53.298),
        (400,  3.725e-12,  58.515),
        (450,  1.585e-12,  60.828),
        (500,  6.967e-13,  63.822),
        (600,  1.454e-13,  71.835),
        (700,  3.614e-14,  88.667),
        (800,  1.170e-14,  124.64),
        (900,  5.245e-15,  181.05),
        (1000, 3.019e-15,  268.00),
    ];

    /// <summary>
    /// Atmospheric density [kg/m³] at geodetic altitude <paramref name="altKm"/>.
    /// <paramref name="solarActivity"/> ≈ 0.5 (quiet) … 1 (nominal) … ~5 (active max)
    /// scales the thermospheric density.
    /// </summary>
    public static double Density(double altKm, double solarActivity = 1.0)
    {
        if (altKm < 0) altKm = 0;
        int row = Table.Length - 1;
        for (int i = 0; i < Table.Length - 1; i++)
            if (altKm < Table[i + 1].H0) { row = i; break; }

        var (h0, rho0, scale) = Table[row];
        double rho = rho0 * Math.Exp(-(altKm - h0) / scale);
        return altKm >= 150 ? rho * solarActivity : rho;
    }

    /// <summary>Rate of change of semi-major axis for a circular orbit [km/s] (negative = decaying).</summary>
    public static double SemiMajorAxisDecayRateKmPerSec(double aKm, double areaToMass, double solarActivity = 1.0)
    {
        double altKm = aKm - Constants.EarthRadiusKm;
        double rho = Density(altKm, solarActivity);                       // kg/m^3
        double aM = aKm * 1000.0;                                         // m
        double muM = Constants.Mu * 1e9;                                  // m^3/s^2
        double daDtM = -DragCoefficient * areaToMass * rho * Math.Sqrt(muM * aM); // m/s
        return daDtM / 1000.0;                                            // km/s
    }

    /// <summary>
    /// Orbit-averaged semi-major-axis decay rate [km/s] for an eccentric orbit: da/dt = −(a²/μ)·C_D·(A/m)·⟨ρ v³⟩,
    /// the drag energy-loss rate averaged over the orbit in time (dM = (1 − e cos E) dE). For e = 0 it is the
    /// circular rate above. Drag acts mostly near perigee, so the apogee comes down while the perigee holds.
    /// </summary>
    public static double OrbitAveragedDecayRateKmPerSec(double aKm, double e, double areaToMass, double solarActivity = 1.0)
    {
        if (e < 1e-4) return SemiMajorAxisDecayRateKmPerSec(aKm, areaToMass, solarActivity);
        const double muM = Constants.Mu * 1e9; const int K = 48;
        double aM = aKm * 1000, sum = 0, wsum = 0;
        for (int k = 0; k < K; k++)
        {
            double Ek = (k + 0.5) * Math.PI / K, w = 1 - e * Math.Cos(Ek), r = aKm * w;
            double rho = Density(r - Constants.EarthRadiusKm, solarActivity);
            double v2 = muM * (2 / (r * 1000) - 1 / aM);
            sum += w * rho * Math.Pow(Math.Max(v2, 0), 1.5); wsum += w;
        }
        return -(aM * aM / muM) * DragCoefficient * areaToMass * (sum / wsum) / 1000.0;
    }

    /// <summary>
    /// Orbital lifetime [days] for a circular orbit, integrating the decay until reentry.
    /// Returns +∞ if effectively stable on a millennium scale.
    /// </summary>
    public static double LifetimeDays(double startAltKm, double areaToMass, double solarActivity = 1.0)
    {
        double a = Constants.EarthRadiusKm + startAltKm;
        double aReentry = Constants.EarthRadiusKm + ReentryAltitudeKm;
        double tSec = 0.0;
        const double maxSec = 1000.0 * 365.25 * Constants.SecondsPerDay; // 1000-yr cap

        while (a > aReentry && tSec < maxSec)
        {
            double rate = -SemiMajorAxisDecayRateKmPerSec(a, areaToMass, solarActivity); // km/s, positive
            if (rate <= 0) return double.PositiveInfinity;
            // Adaptive step: don't drop more than ~2 km per step.
            double dt = Math.Min(2.0 / rate, Constants.SecondsPerDay);
            a -= rate * dt;
            tSec += dt;
        }
        return tSec >= maxSec ? double.PositiveInfinity : tSec / Constants.SecondsPerDay;
    }
}
