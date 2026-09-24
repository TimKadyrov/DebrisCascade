using System;

namespace DebrisCascade.Core;

/// <summary>Where an orbit spends its time, from Kepler's equation (r = a(1 − e cos E), M = E − e sin E).</summary>
public static class OrbitGeometry
{
    /// <summary>Fraction of the orbit's time with radius below <paramref name="rKm"/>.</summary>
    public static double TimeBelow(double rKm, double aKm, double e)
    {
        if (e < 1e-9) return rKm > aKm ? 1 : 0;
        double rp = aKm * (1 - e), ra = aKm * (1 + e);
        if (rKm <= rp) return 0; if (rKm >= ra) return 1;
        double E = Math.Acos(Math.Clamp((1 - rKm / aKm) / e, -1, 1));
        return (E - e * Math.Sin(E)) / Math.PI;
    }

    /// <summary>Fraction of the orbit's time between two altitudes [km] — NASA's "effective number" weight for a band.</summary>
    public static double TimeBetweenAltitudes(double aKm, double e, double loKm, double hiKm) =>
        TimeBelow(Constants.EarthRadiusKm + hiKm, aKm, e) - TimeBelow(Constants.EarthRadiusKm + loKm, aKm, e);
}
