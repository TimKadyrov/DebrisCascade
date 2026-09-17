using System;

namespace SpaceWars.Core;

/// <summary>
/// Stylized LEO debris spatial-density profile vs altitude, à la ESA MASTER / NASA ORDEM:
/// the tracked and lethal-untracked debris populations peak strongly at ~800–1000 km
/// (Fengyun-1C, Cosmos-2251/Iridium-33, Cosmos-1408 clouds and decades of spent hardware),
/// with a secondary bump near ~1400 km and a modern low-altitude bump near ~550 km.
///
/// This is used to place the *modelled* background debris — it must NOT be tied to the
/// active-payload catalog, which is Starlink-dominated at ~550 km (a fast-decaying band).
/// Getting this wrong makes LEO look self-cleaning and understates the Kessler risk.
/// </summary>
public static class DebrisEnvironment
{
    /// <summary>Relative spatial weight at a geodetic altitude [km] (unnormalized).</summary>
    public static double SpatialWeight(double altKm)
    {
        static double G(double x, double h0, double s) { double z = (x - h0) / s; return Math.Exp(-z * z); }
        return 1.00 * G(altKm, 850, 150)   // dominant 800–1000 km debris belt
             + 0.45 * G(altKm, 1450, 130)  // secondary high band
             + 0.35 * G(altKm, 550, 70);   // modern low-altitude constellation band
    }
}
