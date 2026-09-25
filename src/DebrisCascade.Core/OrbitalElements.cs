using System;

namespace DebrisCascade.Core;

/// <summary>
/// Classical (Keplerian) mean orbital elements plus the derived J2 secular rates.
/// Angles in radians, semi-major axis in km, time in seconds since the element epoch.
///
/// This is a mean-element set: it carries the same orbit-averaged information a TLE
/// does, and is propagated with the secular J2 theory (nodal regression, apsidal
/// precession, anomalistic mean-motion correction). Periodic terms and drag are
/// deliberately omitted — for a *statistical* flux/spatial-density study we care about
/// where an object spends its time, not its precise instantaneous ephemeris.
/// </summary>
public struct OrbitalElements
{
    public double SemiMajorAxis; // a  [km]
    public double Eccentricity;  // e  [-]
    public double Inclination;   // i  [rad]
    public double Raan;          // Ω  [rad] right ascension of ascending node
    public double ArgPerigee;    // ω  [rad] argument of perigee
    public double MeanAnomaly;   // M0 [rad] mean anomaly at epoch
    public double MeanMotion;    // n  [rad/s] Keplerian mean motion at epoch

    // Cached J2 secular rates [rad/s], filled by ComputeSecularRates().
    public double RaanDot;
    public double ArgPerigeeDot;
    public double MeanAnomalyDot;

    public double PerigeeAltitude => SemiMajorAxis * (1.0 - Eccentricity) - Constants.EarthRadiusKm;
    public double ApogeeAltitude => SemiMajorAxis * (1.0 + Eccentricity) - Constants.EarthRadiusKm;

    /// <summary>Build a mean-element set from mean motion (rev/day, TLE convention).</summary>
    public static OrbitalElements FromMeanMotionRevPerDay(
        double meanMotionRevPerDay, double e, double iRad, double raanRad,
        double argpRad, double meanAnomalyRad)
    {
        double n = meanMotionRevPerDay * Constants.TwoPi / Constants.SecondsPerDay; // rad/s
        var el = new OrbitalElements
        {
            SemiMajorAxis = Math.Cbrt(Constants.Mu / (n * n)), // Kepler's third law
            Eccentricity = e,
            Inclination = iRad,
            Raan = raanRad,
            ArgPerigee = argpRad,
            MeanAnomaly = meanAnomalyRad,
            MeanMotion = n,
        };
        el.ComputeSecularRates();
        return el;
    }

    /// <summary>
    /// These elements moved <paramref name="tSeconds"/> along the orbit (secular J2 on Ω, ω and M), as a new epoch.
    /// Catalog element sets each hold at their own epoch, often an equator crossing; advancing every one to a common
    /// instant is what puts the population where it actually is at that moment.
    /// </summary>
    public readonly OrbitalElements AdvancedBy(double tSeconds)
    {
        var el = this;
        el.Raan = Wrap(Raan + RaanDot * tSeconds);
        el.ArgPerigee = Wrap(ArgPerigee + ArgPerigeeDot * tSeconds);
        el.MeanAnomaly = Wrap(MeanAnomaly + MeanAnomalyDot * tSeconds);
        return el;
    }

    private static double Wrap(double a) { a %= Constants.TwoPi; return a < 0 ? a + Constants.TwoPi : a; }

    /// <summary>Compute and cache the secular J2 rates from the current mean elements.</summary>
    public void ComputeSecularRates()
    {
        double a = SemiMajorAxis;
        double e = Eccentricity;
        double n = MeanMotion;
        double p = a * (1.0 - e * e);                 // semi-latus rectum [km]
        double reOverP = Constants.EarthRadiusKm / p;
        double factor = 1.5 * Constants.J2 * reOverP * reOverP * n;
        double cosI = Math.Cos(Inclination);
        double sin2I = Math.Sin(Inclination) * Math.Sin(Inclination);

        RaanDot = -factor * cosI;
        ArgPerigeeDot = factor * (2.0 - 2.5 * sin2I);
        MeanAnomalyDot = n + factor * Math.Sqrt(1.0 - e * e) * (1.0 - 1.5 * sin2I);
    }

    /// <summary>
    /// Propagate to <paramref name="tSeconds"/> after epoch and return the ECI position [km].
    /// Applies secular J2 to Ω, ω, M then solves Kepler for the in-plane geometry.
    /// </summary>
    public readonly Vec3 PositionAt(double tSeconds)
    {
        double raan = Raan + RaanDot * tSeconds;
        double argp = ArgPerigee + ArgPerigeeDot * tSeconds;
        double m = MeanAnomaly + MeanAnomalyDot * tSeconds;

        double ecc = SolveKepler(m, Eccentricity);
        // True anomaly from eccentric anomaly.
        double sinNu = Math.Sqrt(1.0 - Eccentricity * Eccentricity) * Math.Sin(ecc);
        double cosNu = Math.Cos(ecc) - Eccentricity;
        double nu = Math.Atan2(sinNu, cosNu);
        double r = SemiMajorAxis * (1.0 - Eccentricity * Math.Cos(ecc)); // radius [km]

        // Perifocal position.
        double xp = r * Math.Cos(nu);
        double yp = r * Math.Sin(nu);

        // Rotate perifocal -> ECI via 3-1-3 (argp, inclination, raan).
        double cosO = Math.Cos(raan), sinO = Math.Sin(raan);
        double cosI = Math.Cos(Inclination), sinI = Math.Sin(Inclination);
        double cosW = Math.Cos(argp), sinW = Math.Sin(argp);

        double r11 = cosO * cosW - sinO * sinW * cosI;
        double r12 = -cosO * sinW - sinO * cosW * cosI;
        double r21 = sinO * cosW + cosO * sinW * cosI;
        double r22 = -sinO * sinW + cosO * cosW * cosI;
        double r31 = sinW * sinI;
        double r32 = cosW * sinI;

        return new Vec3(
            r11 * xp + r12 * yp,
            r21 * xp + r22 * yp,
            r31 * xp + r32 * yp);
    }

    /// <summary>
    /// Propagate to <paramref name="tSeconds"/> after epoch and return the full ECI state
    /// (position [km], velocity [km/s]). Used to seed the barrel dispersal.
    /// </summary>
    public readonly (Vec3 Position, Vec3 Velocity) StateAt(double tSeconds)
    {
        double raan = Raan + RaanDot * tSeconds;
        double argp = ArgPerigee + ArgPerigeeDot * tSeconds;
        double m = MeanAnomaly + MeanAnomalyDot * tSeconds;

        double ecc = SolveKepler(m, Eccentricity);
        double sinNu = Math.Sqrt(1.0 - Eccentricity * Eccentricity) * Math.Sin(ecc);
        double cosNu = Math.Cos(ecc) - Eccentricity;
        double nu = Math.Atan2(sinNu, cosNu);
        double r = SemiMajorAxis * (1.0 - Eccentricity * Math.Cos(ecc));

        double p = SemiMajorAxis * (1.0 - Eccentricity * Eccentricity);
        double h = Math.Sqrt(Constants.Mu * p);

        // Perifocal position and velocity.
        double xp = r * Math.Cos(nu);
        double yp = r * Math.Sin(nu);
        double vxp = -(Constants.Mu / h) * Math.Sin(nu);
        double vyp = (Constants.Mu / h) * (Eccentricity + Math.Cos(nu));

        double cosO = Math.Cos(raan), sinO = Math.Sin(raan);
        double cosI = Math.Cos(Inclination), sinI = Math.Sin(Inclination);
        double cosW = Math.Cos(argp), sinW = Math.Sin(argp);

        double r11 = cosO * cosW - sinO * sinW * cosI;
        double r12 = -cosO * sinW - sinO * cosW * cosI;
        double r21 = sinO * cosW + cosO * sinW * cosI;
        double r22 = -sinO * sinW + cosO * cosW * cosI;
        double r31 = sinW * sinI;
        double r32 = cosW * sinI;

        var pos = new Vec3(r11 * xp + r12 * yp, r21 * xp + r22 * yp, r31 * xp + r32 * yp);
        var vel = new Vec3(r11 * vxp + r12 * vyp, r21 * vxp + r22 * vyp, r31 * vxp + r32 * vyp);
        return (pos, vel);
    }

    /// <summary>
    /// Osculating elements from an ECI state vector (Vallado's rv2coe). The returned set
    /// has M defined at this instant; treat that instant as the new epoch.
    /// </summary>
    public static OrbitalElements FromStateVector(Vec3 r, Vec3 v)
    {
        double mu = Constants.Mu;
        double rMag = r.Length;
        double vMag = v.Length;

        Vec3 hVec = Vec3.Cross(r, v);
        double h = hVec.Length;

        Vec3 kHat = new(0, 0, 1);
        Vec3 nVec = Vec3.Cross(kHat, hVec);
        double n = nVec.Length;

        Vec3 eVec = ((vMag * vMag - mu / rMag) * r - Vec3.Dot(r, v) * v) * (1.0 / mu);
        double e = eVec.Length;

        double energy = vMag * vMag / 2.0 - mu / rMag;
        double a = -mu / (2.0 * energy);

        double i = Math.Acos(Math.Clamp(hVec.Z / h, -1.0, 1.0));

        double raan = 0.0;
        if (n > 1e-9)
        {
            raan = Math.Acos(Math.Clamp(nVec.X / n, -1.0, 1.0));
            if (nVec.Y < 0) raan = Constants.TwoPi - raan;
        }

        double argp = 0.0;
        if (n > 1e-9 && e > 1e-9)
        {
            argp = Math.Acos(Math.Clamp(Vec3.Dot(nVec, eVec) / (n * e), -1.0, 1.0));
            if (eVec.Z < 0) argp = Constants.TwoPi - argp;
        }

        double nu;
        if (e > 1e-9)
        {
            nu = Math.Acos(Math.Clamp(Vec3.Dot(eVec, r) / (e * rMag), -1.0, 1.0));
            if (Vec3.Dot(r, v) < 0) nu = Constants.TwoPi - nu;
        }
        else
        {
            // Near-circular: measure from the ascending node (argument of latitude).
            nu = Math.Acos(Math.Clamp(Vec3.Dot(nVec, r) / (n * rMag), -1.0, 1.0));
            if (r.Z < 0) nu = Constants.TwoPi - nu;
        }

        // True anomaly -> eccentric -> mean anomaly.
        double eAnom = Math.Atan2(Math.Sqrt(1.0 - e * e) * Math.Sin(nu), e + Math.Cos(nu));
        double meanAnom = eAnom - e * Math.Sin(eAnom);

        var el = new OrbitalElements
        {
            SemiMajorAxis = a,
            Eccentricity = e,
            Inclination = i,
            Raan = raan,
            ArgPerigee = argp,
            MeanAnomaly = meanAnom,
            MeanMotion = Math.Sqrt(mu / (a * a * a)),
        };
        el.ComputeSecularRates();
        return el;
    }

    /// <summary>Solve Kepler's equation M = E - e·sin E for the eccentric anomaly E [rad].</summary>
    public static double SolveKepler(double meanAnomaly, double e)
    {
        // Wrap M into [-pi, pi] for fast, stable convergence.
        double m = meanAnomaly % Constants.TwoPi;
        if (m > Math.PI) m -= Constants.TwoPi;
        else if (m < -Math.PI) m += Constants.TwoPi;

        double eAnom = e < 0.8 ? m : Math.PI; // sensible initial guess
        for (int it = 0; it < 30; it++)
        {
            double f = eAnom - e * Math.Sin(eAnom) - m;
            double fp = 1.0 - e * Math.Cos(eAnom);
            double d = f / fp;
            eAnom -= d;
            if (Math.Abs(d) < 1e-12) break;
        }
        return eAnom;
    }
}
