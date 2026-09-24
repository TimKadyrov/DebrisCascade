using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpaceWars.Wpf;

/// <summary>The deck's palette, brightened where needed for the dark background.</summary>
internal static class Palette
{
    public static readonly Color Red = Color.FromRgb(0xFF, 0x5A, 0x52);
    public static readonly Color Orange = Color.FromRgb(0xE8, 0x79, 0x2B);
    public static readonly Color Amber = Color.FromRgb(0xF2, 0xC1, 0x4E);
    public static readonly Color Blue = Color.FromRgb(0x4D, 0xA6, 0xFF);
    public static readonly Color Green = Color.FromRgb(0x2B, 0xB6, 0x73);
    public static readonly Color Mute = Color.FromRgb(0x7C, 0x8A, 0xA0);
    public static readonly Color Ink = Color.FromRgb(0xE7, 0xEE, 0xF8);
    public static readonly Color Dim = Color.FromRgb(0x9F, 0xB2, 0xCC);
    public static readonly Color Axis = Color.FromRgb(0x2A, 0x3A, 0x52);
    public static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>Number formats used on the axes and labels, matching the deck charts.</summary>
internal static class Fmt
{
    public static string Num(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e6) return (v / 1e6).ToString("0.#") + "M";
        if (a >= 1e3) return (v / 1e3).ToString("0.#") + "k";
        if (a >= 10 || a == 0) return v.ToString("0");
        if (a >= 1) return v.ToString("0.#");
        return v.ToString("0.###");
    }
    /// <summary>Growth factor: ×1.36, ×16, ×87.</summary>
    public static string Times(double v) => v >= 10 ? $"×{v:0}" : v >= 2 ? $"×{v:0.0}" : $"×{v:0.0#}";
    public static string Pct(double v) => $"{v * 100:0.##}%";
    public static string Years(double v) => v >= 1e4 ? ">10k" : Num(v);
}

/// <summary>
/// A small Canvas chart: lines (optionally on a right-hand axis), grouped bars on categories, horizontal
/// reference lines, shaded x-bands, text notes, log or linear axes, a legend and a source tag.
/// </summary>
internal sealed class Chart
{
    public sealed record Curve(string Name, Color Color, double[] X, double[] Y, bool Dashed = false,
        double Thickness = 2.2, bool Axis2 = false, bool Markers = false, bool Fill = false);
    public sealed record Ref(double Y, string Label, Color Color, bool LabelLeft = false);
    public sealed record Band(double X0, double X1, Color Color, string Label);
    public sealed record Note(double X, double Y, string Text, Color Color, bool Bold = true, bool Axis2 = false,
        double Dx = 6, double Dy = -18);
    public sealed record BarSet(string Name, Color Color, double[] Values, Color[]? Colors = null);
    public sealed record Marker(int Category, int Set, double Mean, double Lo, double Hi, string Name);

    public string XLabel = "", YLabel = "", Y2Label = "", Tag = "";
    public bool LogY, LogY2;
    public Func<double, string> XFmt = Fmt.Num, YFmt = Fmt.Num, Y2Fmt = Fmt.Num, BarLabel = Fmt.Num;
    public double? YMin, Y2Min, Y2Max;
    public Color Y2Color = Palette.Orange;
    /// <summary>Extra room above the data for the legend: a factor on a log axis, a fraction of the span on a linear one.</summary>
    public double Headroom = 1.0;
    /// <summary>Extra bottom margin (e.g. for a slider laid over the chart).</summary>
    public double BottomExtra = 0;
    public readonly List<Curve> Lines = new();
    public readonly List<Ref> Refs = new();
    public readonly List<Band> Bands = new();
    public readonly List<Note> Notes = new();
    public string[]? Categories;
    public readonly List<BarSet> Bars = new();
    public readonly List<Marker> Markers = new();

    public void Draw(Canvas c)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 80 || h < 80) return;
        bool bars = Categories is { Length: > 0 } && Bars.Count > 0, y2 = Lines.Any(l => l.Axis2);
        double mL = 70, mR = y2 ? 70 : 22, mT = 18, mB = (bars ? 58 : 58) + BottomExtra;
        double x0 = mL, x1 = w - mR, yb = h - mB, yt = mT;

        // --- ranges ---
        double xmin, xmax;
        if (bars) { xmin = -0.5; xmax = Categories!.Length - 0.5; }
        else
        {
            var xs = Lines.SelectMany(l => l.X).ToList();
            xmin = xs.Count > 0 ? xs.Min() : 0; xmax = xs.Count > 0 ? xs.Max() : 1;
        }
        if (xmax <= xmin) xmax = xmin + 1;
        var ys = Lines.Where(l => !l.Axis2).SelectMany(l => l.Y).Concat(Refs.Select(r => r.Y))
                      .Concat(Bars.SelectMany(b => b.Values)).Concat(Markers.SelectMany(m => new[] { m.Lo, m.Hi }))
                      .Where(v => double.IsFinite(v) && (!LogY || v > 0)).ToList();
        (double ymin, double ymax) = Range(ys, LogY, YMin, null, bars);
        if (Headroom > 1) ymax = LogY ? ymax * Headroom : ymin + (ymax - ymin) * Headroom;
        var y2s = Lines.Where(l => l.Axis2).SelectMany(l => l.Y).Where(v => double.IsFinite(v) && (!LogY2 || v > 0)).ToList();
        (double y2min, double y2max) = Range(y2s, LogY2, Y2Min, Y2Max, false);

        double T(double v, bool log, double lo) => log ? Math.Log10(Math.Max(v, lo)) : v;
        double PX(double x) => x0 + (x - xmin) / (xmax - xmin) * (x1 - x0);
        double PY(double y) => yb + (T(y, LogY, ymin) - T(ymin, LogY, ymin)) / (T(ymax, LogY, ymin) - T(ymin, LogY, ymin)) * (yt - yb);
        double PY2(double y) => yb + (T(y, LogY2, y2min) - T(y2min, LogY2, y2min)) / (T(y2max, LogY2, y2min) - T(y2min, LogY2, y2min)) * (yt - yb);

        // --- bands, grid, axes ---
        foreach (var b in Bands)
        {
            double a = PX(Math.Max(b.X0, xmin)), z = PX(Math.Min(b.X1, xmax)); if (z <= a) continue;
            var r = new Rectangle { Width = z - a, Height = yb - yt, Fill = new SolidColorBrush(b.Color) };
            Canvas.SetLeft(r, a); Canvas.SetTop(r, yt); c.Children.Add(r);
            if (b.Label.Length > 0) Add(c, Text(b.Label, Palette.Alpha(b.Color, 255), 11, true), (a + z) / 2, yt + 2, 0.5);
        }
        foreach (double tv in Ticks(ymin, ymax, LogY))
        {
            double py = PY(tv);
            c.Children.Add(Seg(x0, py, x1, py, Palette.Alpha(Palette.Blue, 28), 1));
            Add(c, Text(YFmt(tv), Palette.Dim, 10.5), x0 - 6, py - 8, 1.0);
        }
        c.Children.Add(Seg(x0, yb, x1, yb, Palette.Axis, 1.2));
        c.Children.Add(Seg(x0, yb, x0, yt, Palette.Axis, 1.2));
        if (bars)
            for (int k = 0; k < Categories!.Length; k++)
            {
                var t = Text(Categories[k], Palette.Dim, 11); t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center;
                t.Width = Math.Max(60, (x1 - x0) / Categories.Length - 8);
                Canvas.SetLeft(t, PX(k) - t.Width / 2); Canvas.SetTop(t, yb + 6); c.Children.Add(t);
            }
        else
            foreach (double tv in Ticks(xmin, xmax, false))
                Add(c, Text(XFmt(tv), Palette.Dim, 10.5), PX(tv), yb + 6, 0.5);
        if (y2)
        {
            c.Children.Add(Seg(x1, yb, x1, yt, Palette.Alpha(Y2Color, 160), 1.2));
            foreach (double tv in Ticks(y2min, y2max, LogY2))
                Add(c, Text(Y2Fmt(tv), Y2Color, 10.5), x1 + 6, PY2(tv) - 8, 0.0);
            var y2l = Text(Y2Label, Y2Color, 11); y2l.RenderTransform = new RotateTransform(90);
            Canvas.SetLeft(y2l, w - 6); Canvas.SetTop(y2l, (yt + yb) / 2 - Measure(y2l) / 2); c.Children.Add(y2l);
        }
        if (!bars) Add(c, Text(XLabel, Palette.Dim, 11.5), (x0 + x1) / 2, yb + 24, 0.5);
        var yl = Text(YLabel, Palette.Dim, 11.5); yl.RenderTransform = new RotateTransform(-90);
        Canvas.SetLeft(yl, 4); Canvas.SetTop(yl, (yt + yb) / 2 + Measure(yl) / 2); c.Children.Add(yl);

        foreach (var r in Refs)
        {
            if (r.Y < ymin || r.Y > ymax) continue;
            double py = PY(r.Y);
            var s = Seg(x0, py, x1, py, r.Color, 1.2); s.StrokeDashArray = new DoubleCollection { 5, 4 }; c.Children.Add(s);
            if (r.Label.Length > 0) Add(c, Text(r.Label, r.Color, 10.5), r.LabelLeft ? x0 + 6 : x1 - 4, py - 16, r.LabelLeft ? 0.0 : 1.0);
        }

        // --- bars ---
        if (bars)
        {
            int nSet = Bars.Count; double slot = 0.8 / nSet;
            for (int si = 0; si < nSet; si++)
            {
                var set = Bars[si];
                for (int k = 0; k < Categories!.Length && k < set.Values.Length; k++)
                {
                    double v = set.Values[k]; if (!double.IsFinite(v) || (LogY && v <= 0)) continue;
                    double cx = k - 0.4 + slot * (si + 0.5);
                    double a = PX(cx - slot * 0.45), z = PX(cx + slot * 0.45), top = PY(v), bot = PY(LogY ? ymin : Math.Max(0, ymin));
                    var col = set.Colors is { } cs && k < cs.Length ? cs[k] : set.Color;
                    var r = new Rectangle { Width = Math.Max(1, z - a), Height = Math.Max(1, bot - top), Fill = new SolidColorBrush(col) };
                    Canvas.SetLeft(r, a); Canvas.SetTop(r, top); c.Children.Add(r);
                    double fs = Math.Clamp((z - a) / 5.2, 8.5, 11);
                    Add(c, Text(BarLabel(v), Palette.Ink, fs, true), (a + z) / 2, top - fs - 6, 0.5);
                }
            }
            foreach (var m in Markers)
            {
                double cx = m.Category - 0.4 + slot * (m.Set + 0.5) + slot * 0.2, px = PX(cx);
                c.Children.Add(Seg(px, PY(m.Lo), px, PY(m.Hi), Palette.Ink, 1.2));
                foreach (double yv in new[] { m.Lo, m.Hi }) c.Children.Add(Seg(px - 4, PY(yv), px + 4, PY(yv), Palette.Ink, 1.2));
                var d = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = new SolidColorBrush(Palette.Ink), RenderTransform = new RotateTransform(45, 4, 4) };
                Canvas.SetLeft(d, px - 4); Canvas.SetTop(d, PY(m.Mean) - 4); c.Children.Add(d);
            }
        }

        // --- lines ---
        foreach (var l in Lines)
        {
            Func<double, double> py = l.Axis2 ? PY2 : PY;
            var pts = new PointCollection();
            for (int k = 0; k < l.X.Length && k < l.Y.Length; k++)
            {
                if (!double.IsFinite(l.Y[k]) || ((l.Axis2 ? LogY2 : LogY) && l.Y[k] <= 0)) continue;
                pts.Add(new Point(PX(l.X[k]), py(l.Y[k])));
            }
            if (pts.Count == 0) continue;
            if (l.Fill && pts.Count > 1)
            {
                var poly = new Polygon { Fill = new SolidColorBrush(Palette.Alpha(l.Color, 40)) };
                foreach (var p in pts) poly.Points.Add(p);
                poly.Points.Add(new Point(pts[^1].X, yb)); poly.Points.Add(new Point(pts[0].X, yb));
                c.Children.Add(poly);
            }
            var pl = new Polyline { Points = pts, Stroke = new SolidColorBrush(l.Color), StrokeThickness = l.Thickness };
            if (l.Dashed) pl.StrokeDashArray = new DoubleCollection { 4, 3 };
            c.Children.Add(pl);
            if (l.Markers)
                foreach (var p in pts)
                {
                    var e = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(l.Color) };
                    Canvas.SetLeft(e, p.X - 3); Canvas.SetTop(e, p.Y - 3); c.Children.Add(e);
                }
        }

        foreach (var n in Notes)
        {
            double px = PX(n.X), py = n.Axis2 ? PY2(n.Y) : PY(n.Y);
            bool right = px + n.Dx > x1 - 120;
            Add(c, Text(n.Text, n.Color, 11, n.Bold), right ? Math.Min(px - 6, x1 - 4) : px + n.Dx, py + n.Dy, right ? 1.0 : 0.0);
        }

        // --- legend (top-left) and source tag (bottom-right) ---
        double lx = x0 + 12, ly = yt + 6;
        foreach (var (name, col, dashed, diamond) in Lines.Where(l => l.Name.Length > 0).Select(l => (l.Name, l.Color, l.Dashed, false))
                     .Concat(Bars.Where(b => b.Name.Length > 0).Select(b => (b.Name, b.Color, false, false)))
                     .Concat(Markers.Select(m => m.Name).Distinct().Where(n => n.Length > 0).Select(n => (n, Palette.Ink, false, true))))
        {
            if (diamond)
            {
                var d = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = new SolidColorBrush(col), RenderTransform = new RotateTransform(45, 4, 4) };
                Canvas.SetLeft(d, lx + 4); Canvas.SetTop(d, ly + 3); c.Children.Add(d);
            }
            else
            {
                var s = Seg(lx, ly + 7, lx + 18, ly + 7, col, dashed ? 2 : 4);
                if (dashed) s.StrokeDashArray = new DoubleCollection { 3, 2 };
                c.Children.Add(s);
            }
            Add(c, Text(name, Palette.Ink, 11), lx + 24, ly - 1, 0.0);
            ly += 17;
        }
        if (Tag.Length > 0) Add(c, Text(Tag, Palette.Mute, 9.5, italic: true), w - 6, h - 16, 1.0);
    }

    private static (double, double) Range(List<double> v, bool log, double? lo, double? hi, bool fromZero)
    {
        double a = lo ?? (v.Count > 0 ? v.Min() : 0), b = hi ?? (v.Count > 0 ? v.Max() : 1);
        if (log)
        {
            a = Math.Max(lo ?? a / 1.5, 1e-6); b = hi ?? b * 1.8;
            if (b <= a * 1.01) b = a * 10;
        }
        else
        {
            if (fromZero || lo is null) a = Math.Min(0, a);
            b = hi ?? b + 0.12 * (b - a);
            if (b <= a) b = a + 1;
        }
        return (a, b);
    }

    /// <summary>Axis ticks: decades on a log axis (1-2-5 when it spans less than two), 1-2-5 steps on a linear one.</summary>
    private static IEnumerable<double> Ticks(double lo, double hi, bool log)
    {
        if (log)
        {
            int d0 = (int)Math.Floor(Math.Log10(lo)), d1 = (int)Math.Ceiling(Math.Log10(hi));
            double[] mult = d1 - d0 <= 2 ? new[] { 1.0, 2.0, 5.0 } : new[] { 1.0 };
            for (int d = d0; d <= d1; d++)
                foreach (double m in mult) { double v = m * Math.Pow(10, d); if (v >= lo * 0.999 && v <= hi * 1.001) yield return v; }
            yield break;
        }
        double span = hi - lo, raw = span / 5, mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }.Select(s => s * mag).First(s => span / s <= 6);
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + 1e-9 * step; v += step) yield return Math.Abs(v) < 1e-12 ? 0 : v;
    }

    private static Line Seg(double ax, double ay, double bx, double by, Color c, double t) => new()
    { X1 = ax, Y1 = ay, X2 = bx, Y2 = by, Stroke = new SolidColorBrush(c), StrokeThickness = t };

    private static TextBlock Text(string s, Color c, double size, bool bold = false, bool italic = false) => new()
    {
        Text = s, Foreground = new SolidColorBrush(c), FontSize = size, FontFamily = new FontFamily("Segoe UI"),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
    };

    private static double Measure(TextBlock t) { t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return t.DesiredSize.Width; }

    /// <summary>Place a text block with its anchor at fraction <paramref name="align"/> of its width (0 left, 0.5 centre, 1 right).</summary>
    private static void Add(Canvas c, TextBlock t, double x, double y, double align)
    {
        Canvas.SetLeft(t, x - align * Measure(t)); Canvas.SetTop(t, y); c.Children.Add(t);
    }
}
