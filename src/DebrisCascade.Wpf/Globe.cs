using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DebrisCascade.Core;
using DebrisCascade.Interop;

namespace DebrisCascade.Wpf;

/// <summary>
/// The globe view: every object of a cube-engine snapshot at its real position around a shaded Earth, in an
/// orthographic view the user can turn (azimuth, elevation). Objects behind the Earth are hidden; points add up in
/// brightness, so dense bands glow the way the deck's globe renders do. Belt debris is coloured by altitude.
/// </summary>
internal static class GlobeRenderer
{
    public const byte Intact = 0, Debris = 1, Small = 2, Nail = 3, Working = 4;

    /// <param name="w">Width in device pixels.</param><param name="dpi">Device pixels per WPF unit.</param>
    public static WriteableBitmap Render(GlobeSnapshot s, int w, int h, double az, double el, bool showSmall, double dpi = 1)
    {
        var buf = new float[w * h * 3];
        const float bgR = 0.024f, bgG = 0.031f, bgB = 0.055f;
        for (int p = 0; p < w * h; p++) { buf[3 * p] = bgR; buf[3 * p + 1] = bgG; buf[3 * p + 2] = bgB; }

        double re = Constants.EarthRadiusKm, R = Math.Min(w, h) * 0.34, scale = R / re, cx = w > 1.7 * h ? w * 0.6 : w / 2.0, cy = h / 2.0 - 10 * dpi;
        double ca = Math.Cos(az), sa = Math.Sin(az), ce = Math.Cos(el), se = Math.Sin(el);
        // Screen basis: x right, u up, d toward the viewer.
        (double X, double U, double D) View(double x, double y, double z)
        {
            double x1 = x * ca - y * sa, y1 = x * sa + y * ca;
            return (x1, z * ce - y1 * se, y1 * ce + z * se);
        }

        // Earth: Lambert-shaded ocean with a faint 30° graticule, lit from the upper left.
        var light = Normalize(-0.45, 0.55, 0.70);
        for (int py = (int)(cy - R); py <= (int)(cy + R); py++)
            for (int px = (int)(cx - R); px <= (int)(cx + R); px++)
            {
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                double sx = (px - cx) / R, su = (cy - py) / R, r2 = sx * sx + su * su;
                if (r2 > 1) continue;
                double sd = Math.Sqrt(1 - r2);
                double lam = Math.Max(0, sx * light.X + su * light.Y + sd * light.Z);
                double k = 0.18 + 0.82 * lam, rim = 0.35 * Math.Pow(1 - sd, 3);
                // Back to Earth-fixed coordinates for the graticule (inverse of View).
                double y1 = sd * ce - su * se, z = su * ce + sd * se, x1 = sx;
                double x = x1 * ca + y1 * sa, y = -x1 * sa + y1 * ca;
                double lat = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(y, x) * 180 / Math.PI;
                bool grid = Math.Abs(lat - Math.Round(lat / 30) * 30) < 0.45 || Math.Abs(lon - Math.Round(lon / 30) * 30) < 0.45 / Math.Max(0.2, Math.Cos(lat * Math.PI / 180));
                int o = 3 * (py * w + px);
                buf[o] = (float)(0.055 * k + (grid ? 0.03 : 0) + rim * 0.2);
                buf[o + 1] = (float)(0.18 * k + (grid ? 0.05 : 0) + rim * 0.45);
                buf[o + 2] = (float)(0.31 * k + (grid ? 0.08 : 0) + rim * 0.8);
            }

        // Objects: additive dots, brightness growing with the objects each particle stands for.
        for (int n = 0; n < s.X.Length; n++)
        {
            byte kind = s.Kind[n];
            if (kind == Small && !showSmall) continue;
            var (vx, vu, vd) = View(s.X[n], s.Y[n], s.Z[n]);
            if (vd < 0 && vx * vx + vu * vu < re * re) continue;   // behind the Earth
            double alt = Math.Sqrt((double)s.X[n] * s.X[n] + (double)s.Y[n] * s.Y[n] + (double)s.Z[n] * s.Z[n]) - re;
            var (cr, cg, cb) = Colour(kind, alt);
            double a = kind switch
            {
                Intact => 0.55, Working => 0.85, Small => 0.12 + 0.05 * Math.Log10(1 + s.Weight[n]),
                _ => Math.Clamp(0.40 + 0.15 * Math.Log10(1 + s.Weight[n]), 0.40, 1.0),
            };
            int px = (int)Math.Round(cx + vx * scale), py = (int)Math.Round(cy - vu * scale);
            int size = Math.Max(1, (int)Math.Round(dpi * (kind is Working ? 1.6 : 1.0)));
            for (int dy = 0; dy < size; dy++)
                for (int dx = 0; dx < size; dx++)
                {
                    int qx = px + dx, qy = py + dy;
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                    int o = 3 * (qy * w + qx);
                    buf[o] += (float)(cr * a); buf[o + 1] += (float)(cg * a); buf[o + 2] += (float)(cb * a);
                }
        }

        var bmp = new WriteableBitmap(w, h, 96 * dpi, 96 * dpi, PixelFormats.Bgr32, null);
        var px32 = new int[w * h];
        for (int p = 0; p < w * h; p++)
        {
            int r = (int)(255 * Tone(buf[3 * p])), g = (int)(255 * Tone(buf[3 * p + 1])), b = (int)(255 * Tone(buf[3 * p + 2]));
            px32[p] = (r << 16) | (g << 8) | b;
        }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px32, w * 4, 0);
        return bmp;
    }

    /// <summary>Soft saturation so dense bands glow instead of clipping flat.</summary>
    private static double Tone(double v) => v <= 0 ? 0 : 1 - Math.Exp(-1.6 * v);

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        double n = Math.Sqrt(x * x + y * y + z * z); return (x / n, y / n, z / n);
    }

    /// <summary>Kind colours as in the deck's globe: intact muted blue, debris ≥10 cm on matplotlib's autumn map (red 700 km → yellow 1,100 km).</summary>
    public static (double R, double G, double B) Colour(byte kind, double altKm) => kind switch
    {
        Intact => (0.50, 0.66, 0.82),
        Working => (0.25, 0.95, 0.55),
        Nail => (0.85, 0.45, 1.0),
        Small => (0.45, 0.55, 0.70),
        _ => (1.0, Math.Clamp((altKm - 700) / 400, 0, 1), 0.0),
    };

    public static Color Swatch(byte kind, double altKm = 900)
    {
        var (r, g, b) = Colour(kind, altKm);
        return Color.FromRgb((byte)(255 * r), (byte)(255 * g), (byte)(255 * b));
    }
}
