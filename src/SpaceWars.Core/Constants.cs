namespace SpaceWars.Core;

/// <summary>
/// Physical and modeling constants. Distances in km, time in seconds, mass in kg
/// unless noted. These are the WGS-72 values used by SGP4/TLE conventions so that
/// TLE-derived mean elements stay self-consistent with the propagator.
/// </summary>
public static class Constants
{
    // --- Earth / gravity (WGS-72, matches TLE mean-element conventions) ---
    /// <summary>Earth gravitational parameter GM [km^3/s^2].</summary>
    public const double Mu = 398600.8;

    /// <summary>Earth equatorial radius [km].</summary>
    public const double EarthRadiusKm = 6378.135;

    /// <summary>Second zonal harmonic (oblateness) coefficient.</summary>
    public const double J2 = 1.082616e-3;

    /// <summary>Earth sidereal rotation rate [rad/s].</summary>
    public const double EarthRotationRate = 7.2921159e-5;

    // --- Time ---
    public const double SecondsPerDay = 86400.0;
    public const double MinutesPerDay = 1440.0;

    // --- Materials ---
    /// <summary>Density of steel [kg/m^3] (typical carbon steel nail).</summary>
    public const double SteelDensity = 7850.0;

    // --- NASA Standard Breakup Model / lethality ---
    /// <summary>
    /// Energy-to-mass ratio threshold separating catastrophic (target fully
    /// fragmented) from non-catastrophic collisions [J/g]. Above this the target
    /// disintegrates; below it you get cratering ejecta only.
    /// </summary>
    public const double CatastrophicEmrThreshold = 40.0; // J/g

    // --- Unit helpers ---
    public const double DegToRad = System.Math.PI / 180.0;
    public const double RadToDeg = 180.0 / System.Math.PI;
    public const double TwoPi = 2.0 * System.Math.PI;
}
