using System;

namespace DebrisCascade.Core;

/// <summary>Minimal double-precision 3-vector for ECI positions/velocities [km, km/s].</summary>
public readonly struct Vec3(double x, double y, double z)
{
    public readonly double X = x;
    public readonly double Y = y;
    public readonly double Z = z;

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => a * s;

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <summary>Geocentric altitude above the spherical Earth [km].</summary>
    public double Altitude => Length - Constants.EarthRadiusKm;

    /// <summary>Geocentric latitude [rad] (declination of the position vector).</summary>
    public double Latitude => Math.Asin(Math.Clamp(Z / Length, -1.0, 1.0));

    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}
