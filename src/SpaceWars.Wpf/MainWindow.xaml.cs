using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SpaceWars.Interop;
using IOPath = System.IO.Path;

namespace SpaceWars.Wpf;

public partial class MainWindow : Window
{
    private CatalogBundle? _catalog;
    private Action? _redraw;

    private static readonly Color CyanC = Color.FromRgb(0x4D, 0xA6, 0xFF);
    private static readonly Color AmberC = Color.FromRgb(0xFF, 0xCF, 0x5C);
    private static readonly Color RedC = Color.FromRgb(0xFF, 0x5A, 0x52);
    private static readonly Color GreenC = Color.FromRgb(0x39, 0xD9, 0x8A);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ChartCanvas.SizeChanged += (_, _) => _redraw?.Invoke();
    }

    private static string DataDir()
    {
        string repo = IOPath.GetFullPath(IOPath.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
        if (Directory.Exists(repo)) return repo;
        string local = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceWars", "data");
        Directory.CreateDirectory(local);
        return local;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Loading catalog + SATCAT …");
        try
        {
            var cat = await Scenarios.LoadCatalogAsync(DataDir());
            _catalog = cat;
            ChartHint.Text = "Pick an analysis above.";
            Title = $"SpaceWars — {cat.Objects.Count:N0} LEO objects · SATCAT: {cat.Source} " +
                    $"({(cat.Objects.Count > 0 ? 100.0 * cat.WithRcs / cat.Objects.Count : 0):F0}% RCS) · " +
                    $"mean {cat.MeanMassKg:F0} kg / {cat.MeanAreaM2:F1} m²";
        }
        catch (Exception ex) { ResultsBox.Text = "Catalog load failed:\n" + ex.Message; }
        finally { SetBusy(false, null); }
    }

    private void SetBusy(bool busy, string? hint)
    {
        Buttons.IsEnabled = !busy;
        if (hint != null) ChartHint.Text = hint;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private ScenarioInputs? ReadInputs()
    {
        try
        {
            double D(TextBox t) => double.Parse(t.Text, CultureInfo.InvariantCulture);
            return new ScenarioInputs
            {
                AltKm = D(AltKm), IncDeg = D(IncDeg), NailCount = (int)D(NailCount),
                NailLengthMm = D(NailLen), NailDiameterMm = D(NailDia),
                DispersalSigmaMS = D(SigmaMS), RelVelMS = D(RelVelMS),
                LaunchRatePerYear = D(LaunchRate), LaunchAltKm = D(LaunchAlt),
                Responsive = Responsive.IsChecked == true, LossTolerance = D(LossTol),
                SolarActivity = D(Solar), HorizonYears = D(Horizon),
            };
        }
        catch { ResultsBox.Text = "Invalid input — please check the numeric fields."; return null; }
    }

    private async Task RunAsync(string label, Func<CatalogBundle, ScenarioInputs, (string text, Action draw)> work)
    {
        if (_catalog is null) { ResultsBox.Text = "Catalog still loading…"; return; }
        var inputs = ReadInputs(); if (inputs is null) return;
        UsabilityControls.Visibility = Visibility.Collapsed; // shown again only by Usability
        SetBusy(true, $"Running {label} …");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (text, draw) = await Task.Run(() => work(_catalog, inputs));
            ResultsBox.Text = $"[{label}]  ({sw.Elapsed.TotalSeconds:F1} s)\n\n" + text;
            _redraw = draw; draw();
            ChartHint.Text = "";
        }
        catch (Exception ex) { ResultsBox.Text = $"[{label}] error:\n" + ex; }
        finally { SetBusy(false, null); }
    }

    private async void RunFlux(object s, RoutedEventArgs e) => await RunAsync("Lethality & Flux", (cat, i) =>
        (Scenarios.LethalityAndFlux(cat.Objects, i, cat.MeanAreaM2), () => { }));

    private async void RunEvolve(object s, RoutedEventArgs e) => await RunAsync("Evolve (box)", (cat, i) =>
    {
        var (b, k) = Scenarios.Evolution(cat.Objects, i);
        string t = TrajectoryText(b.Years, b.TotalObjects, k.TotalObjects, b.CatastrophicPerYear, k.CatastrophicPerYear);
        return (t, () => DrawTrajectory(b.Years, b.TotalObjects, k.TotalObjects, "years", "debris objects on orbit"));
    });

    private async void RunCascade(object s, RoutedEventArgs e) => await RunAsync("Cascade (discrete)", (cat, i) =>
    {
        var (b, k) = Scenarios.Cascade(cat.Objects, i);
        string t = TrajectoryText(b.Years, b.TotalObjects, k.TotalObjects, b.CatastrophicPerYear, k.CatastrophicPerYear);
        return (t, () => DrawTrajectory(b.Years, b.TotalObjects, k.TotalObjects, "years", "debris objects on orbit"));
    });

    private async void RunConjunction(object s, RoutedEventArgs e) => await RunAsync("Conjunction (cube)", (cat, i) =>
    {
        var (b, k, gpu) = Scenarios.Conjunction(cat.Objects, i);
        string t = TrajectoryText(b.Years, b.TotalObjects, k.TotalObjects, b.CatastrophicPerYear, k.CatastrophicPerYear)
                 + $"\nengine: {(gpu ? "CUDA sw_propagate_state (GPU)" : "CPU fallback")}"
                 + "\n(geometry is faithful; absolute rate is qualitative — see calibration)";
        return (t, () => DrawTrajectory(b.Years, b.TotalObjects, k.TotalObjects, "years", "debris objects on orbit"));
    });

    private async void RunTipping(object s, RoutedEventArgs e) => await RunAsync("Tipping sweep", (cat, i) =>
    {
        var (rates, growth) = Scenarios.Tipping(cat.Objects, i);
        var sb = new System.Text.StringBuilder($"50-yr growth vs launch rate into {i.LaunchAltKm:F0} km band:\n");
        for (int j = 0; j < rates.Length; j++)
            sb.AppendLine($"  {rates[j],6:F0}/yr → {growth[j],8:F2}×  {(growth[j] > 1 ? "RUNAWAY" : "self-clean")}");
        return (sb.ToString(), () => DrawSeries("launches/yr", "50-yr growth ×", true,
            new Series("growth", AmberC, rates, growth), new Series("threshold", GreenC, new[] { rates[0], rates[^1] }, new[] { 1.0, 1.0 })));
    });

    private double[]? _uAlt, _uYears;
    private double[][]? _uHaz;
    private double _uThr;

    private async void RunUsability(object s, RoutedEventArgs e) => await RunAsync("Usability by altitude", (cat, i) =>
    {
        var (alt, years, haz, thr) = Scenarios.UsabilityOverTime(cat.Objects, i, cat.MeanAreaM2);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Per-satellite collision probability vs altitude (target {cat.MeanAreaM2:F1} m²).");
        sb.AppendLine($"Unusable threshold = {thr:P1}/yr. Drag the Year slider to scrub time.\n");
        sb.AppendLine($"Unusable at yr 0  : {UnusableBands(alt, haz[0], thr)}");
        sb.AppendLine($"Unusable at yr {years.Length - 1,-3}: {UnusableBands(alt, haz[^1], thr)}");
        int pk = Array.IndexOf(haz[^1], haz[^1].Max());
        sb.AppendLine($"\nWorst band ~{alt[pk]:F0} km: {haz[^1][pk]:P1}/yr at horizon.");
        return (sb.ToString(), () =>
        {
            _uAlt = alt; _uYears = years; _uHaz = haz; _uThr = thr;
            UsabilityControls.Visibility = Visibility.Visible;
            YearSlider.Maximum = years.Length - 1;
            YearSlider.Value = years.Length - 1;
            DrawUsabilityYear(years.Length - 1);
            _redraw = () => DrawUsabilityYear((int)YearSlider.Value);
        });
    });

    private void DrawUsabilityYear(int y)
    {
        if (_uAlt is null || _uHaz is null || _uYears is null) return;
        y = Math.Clamp(y, 0, _uHaz.Length - 1);
        YearLabel.Text = ((int)_uYears[y]).ToString();
        DrawSeries("altitude (km)", "collision prob / sat / yr", true,
            new Series("year 0", Color.FromArgb(120, 0x4D, 0xA6, 0xFF), _uAlt, _uHaz[0]),
            new Series($"year {(int)_uYears[y]}", RedC, _uAlt, _uHaz[y]),
            new Series("unusable", AmberC, new[] { _uAlt[0], _uAlt[^1] }, new[] { _uThr, _uThr }));
    }

    private void YearSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uHaz != null) DrawUsabilityYear((int)e.NewValue);
    }

    private static string UnusableBands(double[] alt, double[] haz, double thr)
    {
        var parts = new System.Collections.Generic.List<string>();
        int start = -1;
        for (int s = 0; s <= alt.Length; s++)
        {
            bool over = s < alt.Length && haz[s] >= thr;
            if (over && start < 0) start = s;
            else if (!over && start >= 0)
            {
                double lo = alt[start] - 25, hi = alt[s - 1] + 25;
                parts.Add($"{lo:F0}–{hi:F0} km");
                start = -1;
            }
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    private static string TrajectoryText(double[] yr, double[] baseline, double[] barrel, double[] baseCat, double[] barCat)
    {
        var sb = new System.Text.StringBuilder("  year | baseline | +barrel | +barrel cat/yr\n");
        foreach (int y in new[] { 0, 10, 25, 50 })
        {
            int idx = Math.Min(y, yr.Length - 1);
            sb.AppendLine($"  {(int)yr[idx],4} | {baseline[idx],8:N0} | {barrel[idx],8:N0} | {barCat[idx],6:F1}");
        }
        double g = baseline[^1] / baseline[0];
        sb.AppendLine($"  baseline {baseline[0]:N0} → {baseline[^1]:N0} ({(g > 1.02 ? "supercritical" : "stable/subcritical")})");
        sb.AppendLine($"  extra objects from barrel at end: {barrel[^1] - baseline[^1]:N0}");
        return sb.ToString();
    }

    private void DrawTrajectory(double[] yr, double[] baseline, double[] barrel, string xl, string yl) =>
        DrawSeries(xl, yl, false, new Series("baseline", CyanC, yr, baseline), new Series("+barrel", RedC, yr, barrel));

    private readonly record struct Series(string Name, Color Color, double[] X, double[] Y);

    private void DrawSeries(string xLabel, string yLabel, bool logY, params Series[] series)
    {
        var c = ChartCanvas; c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 40 || h < 40 || series.Length == 0) return;
        double mL = 64, mR = 16, mT = 14, mB = 34;
        double x0 = mL, x1 = w - mR, y0 = h - mB, y1 = mT;

        double xmin = series.Min(s => s.X.Min()), xmax = series.Max(s => s.X.Max());
        double ymin = series.Min(s => s.Y.Where(v => !logY || v > 0).DefaultIfEmpty(0).Min());
        double ymax = series.Max(s => s.Y.Max());
        if (logY) { ymin = Math.Max(ymin, 1e-3); ymax = Math.Max(ymax, ymin * 10); }
        else { ymin = Math.Min(0, ymin); if (ymax <= ymin) ymax = ymin + 1; }
        if (xmax <= xmin) xmax = xmin + 1;
        double TY(double v) => logY ? Math.Log10(Math.Max(v, ymin)) : v;
        double tymin = TY(ymin), tymax = TY(ymax);

        double PX(double x) => x0 + (x - xmin) / (xmax - xmin) * (x1 - x0);
        double PY(double y) => y0 + (TY(y) - tymin) / (tymax - tymin) * (y1 - y0);

        Line Axis(double ax, double ay, double bx, double by) => new()
        { X1 = ax, Y1 = ay, X2 = bx, Y2 = by, Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x52)), StrokeThickness = 1 };
        c.Children.Add(Axis(x0, y0, x1, y0));
        c.Children.Add(Axis(x0, y0, x0, y1));

        // Y grid + labels
        for (int g = 0; g <= 4; g++)
        {
            double frac = g / 4.0, yv = logY ? Math.Pow(10, tymin + frac * (tymax - tymin)) : ymin + frac * (ymax - ymin);
            double py = PY(yv);
            var gl = Axis(x0, py, x1, py); gl.Stroke = new SolidColorBrush(Color.FromArgb(40, 0x5A, 0x8C, 0xC8)); c.Children.Add(gl);
            c.Children.Add(Label(FormatNum(yv), 4, py - 8, Color.FromRgb(0x9F, 0xB2, 0xCC), 10));
        }
        // X labels
        for (int g = 0; g <= 4; g++)
        {
            double xv = xmin + g / 4.0 * (xmax - xmin);
            c.Children.Add(Label(FormatNum(xv), PX(xv) - 12, y0 + 6, Color.FromRgb(0x9F, 0xB2, 0xCC), 10));
        }
        c.Children.Add(Label(xLabel, (x0 + x1) / 2 - 30, h - 16, Color.FromRgb(0x64, 0x7A, 0x99), 10));
        var yLab = Label(yLabel, 0, 0, Color.FromRgb(0x64, 0x7A, 0x99), 10);
        yLab.RenderTransform = new RotateTransform(-90); Canvas.SetLeft(yLab, 2); Canvas.SetTop(yLab, (y0 + y1) / 2 + 40);
        c.Children.Add(yLab);

        // series + legend
        double lx = x1 - 120, ly = y1 + 2;
        foreach (var s in series)
        {
            var pl = new Polyline { Stroke = new SolidColorBrush(s.Color), StrokeThickness = 2 };
            for (int k = 0; k < s.X.Length; k++) pl.Points.Add(new Point(PX(s.X[k]), PY(s.Y[k])));
            c.Children.Add(pl);
            var swatch = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(s.Color) };
            Canvas.SetLeft(swatch, lx); Canvas.SetTop(swatch, ly); c.Children.Add(swatch);
            c.Children.Add(Label(s.Name, lx + 14, ly - 3, s.Color, 11));
            ly += 18;
        }
    }

    private static TextBlock Label(string text, double x, double y, Color color, double size)
    {
        var t = new TextBlock { Text = text, Foreground = new SolidColorBrush(color), FontFamily = new FontFamily("Consolas"), FontSize = size };
        Canvas.SetLeft(t, x); Canvas.SetTop(t, y); return t;
    }

    private static string FormatNum(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e6) return (v / 1e6).ToString("0.#") + "M";
        if (a >= 1e3) return (v / 1e3).ToString("0.#") + "k";
        if (a >= 10 || a == 0) return v.ToString("0");
        return v.ToString("0.##");
    }
}
