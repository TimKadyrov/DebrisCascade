using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SpaceWars.Core;
using SpaceWars.Interop;
using IOPath = System.IO.Path;

namespace SpaceWars.Wpf;

/// <summary>
/// The analysis tool. Each view runs one of the deck's analyses on the current inputs and draws it the
/// way the deck does: the metric is debris ≥10 cm in the 700–1,100 km belt, growth is "×" over today.
/// </summary>
public partial class MainWindow : Window
{
    private CatalogBundle? _catalog;
    private Action? _redraw;

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
            Title = $"SpaceWars — {cat.Objects.Count:N0} LEO objects ({cat.ActiveCount:N0} working) · SATCAT: {cat.Source} " +
                    $"({(cat.Objects.Count > 0 ? 100.0 * cat.WithRcs / cat.Objects.Count : 0):F0}% RCS)";
        }
        catch (Exception ex) { ResultsBox.Text = "Catalog load failed:\n" + ex.Message; SetBusy(false, null); return; }
        SetBusy(false, null);
        var args = Environment.GetCommandLineArgs();
        int r = Array.IndexOf(args, "--render");
        if (r >= 0 && r + 1 < args.Length) await RenderAllAsync(args[r + 1], args.Skip(r + 2).ToArray());
    }

    /// <summary>
    /// <c>--render &lt;folder&gt; [view ...]</c>: run every view (or the named ones) on the default inputs, save each chart and its text there,
    /// then exit — for checking the charts and for documentation.
    /// </summary>
    private async Task RenderAllAsync(string dir, string[] only)
    {
        Directory.CreateDirectory(dir);
        var views = new (string File, Button Button)[]
        {
            ("evolve", BtnEvolve), ("cascade", BtnCascade), ("conjunction", BtnConj), ("usability", BtnUse),
            ("barrels", BtnBarrels), ("comparison", BtnCompare), ("tipping", BtnTip), ("operators", BtnOps),
            ("working", BtnWorking), ("removal", BtnRemoval), ("nasa", BtnNasa),
        };
        foreach (var (file, button) in views.Where(v => only.Length == 0 || only.Contains(v.File)))
        {
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(300);
            while (!Buttons.IsEnabled) await Task.Delay(250);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            SaveChart(IOPath.Combine(dir, file + ".png"));
            File.WriteAllText(IOPath.Combine(dir, file + ".txt"), ResultsBox.Text);
        }
        Close();
    }

    private void SaveChart(string path)
    {
        const double scale = 1.5;
        double w = ChartPanel.ActualWidth, h = ChartPanel.ActualHeight;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * scale), (int)(h * scale),
            96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        // Draw through a VisualBrush: rendering the panel directly would keep its offset in the window.
        var dv = new System.Windows.Media.DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0F, 0x17)), null, new Rect(0, 0, w, h));
            ctx.DrawRectangle(new System.Windows.Media.VisualBrush(ChartPanel), null, new Rect(0, 0, w, h));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path); enc.Save(fs);
    }

    private void SavePng(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = "spacewars-chart.png" };
        if (dlg.ShowDialog(this) != true) return;
        SaveChart(dlg.FileName);
        File.WriteAllText(IOPath.ChangeExtension(dlg.FileName, ".txt"), ResultsBox.Text);
    }

    private void SetBusy(bool busy, string? hint)
    {
        Buttons.IsEnabled = !busy;
        if (hint != null) ChartHint.Text = hint;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private ScenarioInputs? ReadInputs()
    {
        // TryParse, not Parse: a bad field is reported by name instead of throwing a FormatException.
        string? bad = null;
        double D(TextBox t)
        {
            if (double.TryParse(t.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double v)) return v;
            bad ??= t.Name; return 0;
        }
        var inputs = new ScenarioInputs
        {
            AltKm = D(AltKm), IncDeg = D(IncDeg), NailCount = (int)D(NailCount),
            NailLengthMm = D(NailLen), NailDiameterMm = D(NailDia),
            DispersalSigmaMS = D(SigmaMS), RelVelMS = D(RelVelMS),
            LaunchRatePerYear = D(LaunchRate), LaunchAltKm = D(LaunchAlt),
            Responsive = Responsive.IsChecked == true, LossTolerance = D(LossTol),
            ExplosionsPerYear = D(Explosions), RemovalsPerYear = D(Removals),
            WorkingSatellites = Working.IsChecked == true,
            DisposalSuccess = D(DisposalPct) / 100.0, RocketBodyDisposal = D(RocketBodyPct) / 100.0,
            AvoidanceSuccess = D(AvoidancePct) / 100.0, SatelliteLifetimeYears = D(LifetimeYr),
            SolarActivity = D(Solar), HorizonYears = D(Horizon),
        };
        if (bad is null) return inputs;
        ResultsBox.Text = $"Invalid number in field '{bad}' — use digits with '.' as the decimal point.";
        return null;
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

    /// <summary>Draw a chart now and on every resize.</summary>
    private Action Show(Chart chart) => () => chart.Draw(ChartCanvas);

    // ---------------------------------------------------------------- nail physics ------------------

    private async void RunFlux(object s, RoutedEventArgs e) => await RunAsync("Lethality & Flux", (cat, i) =>
        (Scenarios.LethalityAndFlux(cat.Objects, i, cat.MeanAreaM2), () => ChartCanvas.Children.Clear()));

    // ---------------------------------------------------------------- the three engines -------------

    private async void RunEvolve(object s, RoutedEventArgs e) => await RunAsync("Evolve (box)", (cat, i) =>
    {
        var (b, k) = Scenarios.Evolution(cat.Objects, i);
        return Trajectory(i, "box model", b.Years, b.BeltTrackableObjects, k.BeltTrackableObjects, b.TrackableObjects,
            b.TotalObjects, k.TotalObjects, b.CatastrophicPerYear, b.WorkingSatellites, null);
    });

    private async void RunCascade(object s, RoutedEventArgs e) => await RunAsync("Cascade (discrete)", (cat, i) =>
    {
        var (b, k) = Scenarios.Cascade(cat.Objects, i);
        string note = i.WorkingSatellites
            ? "Note: the discrete engine doesn't model working satellites; every satellite here is dead from day one. Use Evolve (box) or Conjunction (cube)."
            : "One seed of a stochastic engine: the deck quotes 8-seed means.";
        return Trajectory(i, "discrete engine, seed 1", b.Years, b.BeltTrackableObjects, k.BeltTrackableObjects, b.TrackableObjects,
            b.TotalObjects, k.TotalObjects, b.CatastrophicPerYear, [], note);
    });

    private async void RunConjunction(object s, RoutedEventArgs e) => await RunAsync("Conjunction (cube)", (cat, i) =>
    {
        var (b, k, gpu) = Scenarios.Conjunction(cat.Objects, i);
        return Trajectory(i, $"cube engine, seed 1 ({(gpu ? "CUDA" : "CPU")})", b.Years, b.BeltTrackableObjects, k.BeltTrackableObjects,
            b.TrackableObjects, b.TotalObjects, k.TotalObjects, b.CatastrophicPerYear, b.WorkingSatellites,
            "One seed of a stochastic engine: the deck quotes 8-seed means.");
    });

    /// <summary>The deck's metric over time: belt debris ≥10 cm with and without the barrel (plus the working fleet).</summary>
    private (string, Action) Trajectory(ScenarioInputs i, string engine, double[] yr, double[] belt, double[] beltBarrel,
        double[] leo, double[] all, double[] allBarrel, double[] cat, double[] working, string? note)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  year | belt ≥10 cm | + barrel | LEO ≥10 cm | all objects (incl. 1–10 cm) | + barrel | catastrophic/yr");
        foreach (int y in new[] { 0, 10, 25, 50 }.Where(y => y <= yr[^1] + 1e-9).Append((int)Math.Round(yr[^1])).Distinct())
        {
            int j = Array.FindIndex(yr, t => t >= y - 1e-6); if (j < 0) j = yr.Length - 1;
            sb.AppendLine($"  {(int)Math.Round(yr[j]),4} | {belt[j],11:N0} | {beltBarrel[j],8:N0} | {leo[j],10:N0} | {all[j],27:N0} | {allBarrel[j],8:N0} | {cat[j],6:F1}");
        }
        sb.AppendLine($"\nBelt debris ≥10 cm: {Fmt.Times(belt[^1] / belt[0])} over {yr[^1]:F0} yr; the barrel adds {Pct(beltBarrel[^1] / belt[^1] - 1)} to the belt " +
                      $"and {Pct(allBarrel[^1] / all[^1] - 1)} to all objects.");
        if (working.Length > 0 && working.Max() > 0)
            sb.AppendLine($"Working satellites (not debris): {working[0]:N0} → {working[^1]:N0}  " +
                          $"({i.DisposalSuccess:P0} deorbited, {i.RocketBodyDisposal:P0} rocket bodies, {i.AvoidanceSuccess:P0} avoided, {i.SatelliteLifetimeYears:0.#}-yr life)");
        if (note != null) sb.AppendLine(note);

        var ch = new Chart
        {
            XLabel = "years", YLabel = "belt debris ≥10 cm (700–1,100 km)", YMin = 0,
            Tag = $"{engine} · {Launches(i)} · barrel {i.NailCount:N0} nails at {i.AltKm:F0} km",
        };
        ch.Lines.Add(new("baseline", Palette.Blue, yr, belt, Fill: true));
        ch.Lines.Add(new("+ barrel", Palette.Red, yr, beltBarrel, Dashed: true));
        ch.Notes.Add(new(yr[^1], belt[^1], Fmt.Times(belt[^1] / belt[0]), Palette.Blue));
        if (working.Length > 0 && working.Max() > 0)
        {
            ch.Lines.Add(new("working satellites (right axis)", Palette.Green, yr, working, Axis2: true, Thickness: 1.6));
            ch.Y2Label = "working satellites"; ch.Y2Color = Palette.Green; ch.Y2Min = 0;
        }
        return (sb.ToString(), Show(ch));
    }

    // ---------------------------------------------------------------- altitude ----------------------

    private double[]? _uAlt, _uYears, _uToday, _uLife;
    private double[][]? _uHaz;
    private double _uThr, _uBarrelAlt;

    private async void RunUsability(object s, RoutedEventArgs e) => await RunAsync("Usability by altitude", (cat, i) =>
    {
        var (alt, years, haz, thr) = Scenarios.UsabilityOverTime(cat.Objects, i, cat.MeanAreaM2);
        var today = Scenarios.Box(cat.Objects, i).SatelliteHazardByShell(cat.MeanAreaM2, i.RelVelMS);
        var life = Scenarios.FragmentLifetimeYears(alt, i.SolarActivity);
        var sb = new StringBuilder();
        sb.AppendLine($"Per-satellite chance a year of a lethal hit, by altitude (a {cat.MeanAreaM2:F1} m² satellite).");
        sb.AppendLine($"Operators walk away at {thr:P1} a year. Orange (right axis): years for drag to remove a 3–10 cm fragment.");
        int pk = Array.IndexOf(today, today.Max());
        sb.AppendLine($"\nToday: peak {today[pk]:P2}/yr at {alt[pk]:F0} km. Over the walk-away line: {UnusableBands(alt, today, thr)}.");
        sb.AppendLine($"With the barrel at {i.AltKm:F0} km, year 0: over the line: {UnusableBands(alt, haz[0], thr)}; year {years[^1]:F0}: {UnusableBands(alt, haz[^1], thr)}.");
        sb.AppendLine("Drag the Year slider to scrub time.");
        return (sb.ToString(), () =>
        {
            _uAlt = alt; _uYears = years; _uHaz = haz; _uThr = thr; _uToday = today; _uLife = life; _uBarrelAlt = i.AltKm;
            UsabilityControls.Visibility = Visibility.Visible;
            YearSlider.Maximum = years.Length - 1;
            YearSlider.Value = 0;
            DrawUsabilityYear(0);
            _redraw = () => DrawUsabilityYear((int)YearSlider.Value);
        });
    });

    private void DrawUsabilityYear(int y)
    {
        if (_uAlt is null || _uHaz is null || _uYears is null || _uToday is null || _uLife is null) return;
        y = Math.Clamp(y, 0, _uHaz.Length - 1);
        YearLabel.Text = ((int)_uYears[y]).ToString();
        var ch = new Chart
        {
            XLabel = "altitude (km)", YLabel = "collision risk per satellite (per year)", YFmt = Fmt.Pct, YMin = 0,
            Y2Label = "years to clear a 3–10 cm fragment (log)", LogY2 = true, Y2Fmt = Fmt.Years, Y2Min = 0.05, Y2Max = 3e4,
            Tag = "box model · today's catalog · drag lifetimes", Headroom = 1.45, BottomExtra = 26,
        };
        ch.Bands.Add(new(700, 1100, Palette.Alpha(Palette.Red, 26), "700–1,100 km belt"));
        ch.Lines.Add(new("today", Palette.Blue, _uAlt, _uToday, Fill: true));
        ch.Lines.Add(new($"with the barrel at {_uBarrelAlt:F0} km, year {(int)_uYears[y]}", Palette.Red, _uAlt, _uHaz[y], Dashed: true, Thickness: 1.6));
        ch.Lines.Add(new("years to clear a 3–10 cm fragment", Palette.Orange, _uAlt, _uLife, Axis2: true));
        ch.Refs.Add(new(_uThr, $"operators walk away ({_uThr:P0}/yr)", Palette.Amber));
        ch.Draw(ChartCanvas);
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

    // ---------------------------------------------------------------- the barrel at scale -----------

    private async void RunBarrels(object s, RoutedEventArgs e) => await RunAsync("Scale check (barrels)", (cat, i) =>
    {
        var (n, belt, all, asatBelt, asatAll) = Scenarios.Barrels(cat.Objects, i);
        var sb = new StringBuilder($"Barrels dumped at {i.AltKm:F0} km, vs the same run without them, after {i.HorizonYears:F0} yr:\n");
        sb.AppendLine("  barrels | belt ≥10 cm | all objects (incl. 1–10 cm)");
        for (int k = 0; k < n.Length; k++) sb.AppendLine($"  {n[k],7:N0} | {Pct(belt[k] - 1),11} | {Pct(all[k] - 1),10}");
        sb.AppendLine($"\nOne ASAT strike (1 t satellite, 865 km): belt {Pct(asatBelt - 1)}, all objects {Pct(asatAll - 1)}.");
        sb.AppendLine($"≈ {(asatBelt - 1) / Math.Max(1e-12, belt[0] - 1):F0} barrels on the belt metric, {(asatAll - 1) / Math.Max(1e-12, all[0] - 1):F0} on all objects.");
        if (Math.Abs(i.AltKm - 900) > 1) sb.AppendLine("Tip: set the release altitude to 900 km to match the deck's scale check.");
        var ch = new Chart
        {
            YLabel = "added after the horizon, vs no barrels", LogY = true, YFmt = v => Pct(v / 100), BarLabel = v => Pct(v / 100),
            Categories = n.Select(k => $"{k:N0} barrel{(k == 1 ? "" : "s")}").ToArray(), YMin = 0.01, Headroom = 8,
            Tag = $"box model · barrels at {i.AltKm:F0} km · {i.HorizonYears:F0} years",
        };
        ch.Bars.Add(new("belt debris ≥10 cm", Palette.Amber, belt.Select(r => Math.Max(1e-6, (r - 1) * 100)).ToArray()));
        ch.Bars.Add(new("all objects, incl. 1–10 cm", Palette.Blue, all.Select(r => Math.Max(1e-6, (r - 1) * 100)).ToArray()));
        ch.Refs.Add(new((asatBelt - 1) * 100, $"one ASAT strike, belt: {Pct(asatBelt - 1)}", Palette.Red, LabelLeft: true));
        ch.Refs.Add(new(100, "doubled", Palette.Mute, LabelLeft: true));
        return (sb.ToString(), Show(ch));
    });

    private async void RunComparison(object s, RoutedEventArgs e) => await RunAsync("Comparison", (cat, i) =>
    {
        var rows = Scenarios.Comparison(cat.Objects, i);
        var sb = new StringBuilder($"Extra belt debris ≥10 cm after {i.HorizonYears:F0} yr, compared with adding nothing:\n");
        foreach (var r in rows) sb.AppendLine($"  {r.Label,-38} +{r.Extra:N0}");
        sb.AppendLine("\nTraffic, not the barrel, is what moves the belt.");
        var col = rows.Select(r => r.Kind switch { "barrel" => Palette.Amber, "asat" => Palette.Orange, "working" => Palette.Blue, _ => Palette.Red }).ToArray();
        var ch = new Chart
        {
            YLabel = $"extra belt debris ≥10 cm after {i.HorizonYears:F0} yr", LogY = true, YMin = 1,
            BarLabel = v => "+" + Fmt.Num(double.Parse(v.ToString("G2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
            Categories = rows.Select(r => r.Label).ToArray(),
            Tag = $"box model · traffic at {i.LaunchAltKm:F0} km, ASAT 865 km",
        };
        ch.Bars.Add(new("", Palette.Red, rows.Select(r => Math.Max(1, r.Extra)).ToArray(), col));
        return (sb.ToString(), Show(ch));
    });

    // ---------------------------------------------------------------- traffic -----------------------

    private async void RunTipping(object s, RoutedEventArgs e) => await RunAsync("Tipping sweep", (cat, i) =>
    {
        var (rates, never, working, today) = Scenarios.Tipping(cat.Objects, i);
        var sb = new StringBuilder($"Belt debris ≥10 cm after {i.HorizonYears:F0} yr (today {today:N0}), vs objects added a year at {i.LaunchAltKm:F0} km:\n");
        sb.AppendLine($"  per year | never deorbited{(working != null ? " | working, deorbited" : "")}");
        for (int j = 0; j < rates.Length; j++)
            sb.AppendLine($"  {rates[j],8:F0} | {Fmt.Times(never[j]),15}{(working != null ? $" | {Fmt.Times(working[j]),19}" : "")}");
        sb.AppendLine("\n85% satellites, 15% rocket bodies. Never deorbited = dead from day one (a stress test, not a forecast).");
        if (working != null) sb.AppendLine($"Working: {i.DisposalSuccess:P0} deorbited, {i.RocketBodyDisposal:P0} rocket bodies, {i.AvoidanceSuccess:P0} of tracked conjunctions avoided.");
        var ch = new Chart
        {
            XLabel = $"satellites + rocket bodies added per year at {i.LaunchAltKm:F0} km, for {i.HorizonYears:F0} years",
            YLabel = "belt debris ≥10 cm, × today", LogY = true, YFmt = Fmt.Times, YMin = 0.8, Headroom = 2,
            Tag = "box model · growth over today's belt objects ≥10 cm",
        };
        ch.Lines.Add(new("never deorbited", Palette.Red, rates, never, Markers: true));
        if (working != null) ch.Lines.Add(new("working, deorbited at end of life", Palette.Blue, rates, working, Markers: true));
        ch.Refs.Add(new(1, "×1 = no growth", Palette.Mute));
        foreach (int j in new[] { 0, Array.IndexOf(rates, 50.0), Array.IndexOf(rates, 200.0), Array.IndexOf(rates, 500.0), rates.Length - 1 }.Where(j => j >= 0).Distinct())
            ch.Notes.Add(new(rates[j], never[j], rates[j] == 0 ? $"nothing added {Fmt.Times(never[j])}" : $"{rates[j]:F0}/yr {Fmt.Times(never[j])}", Palette.Red, Dx: 12, Dy: rates[j] == 0 ? -20 : 2));
        return (sb.ToString(), Show(ch));
    });

    private async void RunOperators(object s, RoutedEventArgs e) => await RunAsync("Operators quit", (cat, i) =>
    {
        var (yr, constant, responsive, quit, rate) = Scenarios.Operators(cat.Objects, i);
        var sb = new StringBuilder($"{rate:F0} objects a year at {i.LaunchAltKm:F0} km; operators throttle back as the risk rises and stop at {i.LossTolerance:P0}/yr.\n");
        sb.AppendLine(double.IsNaN(quit) ? "Operators never quit within the horizon." : $"They stop launching in year {quit:F0}.");
        sb.AppendLine($"Belt debris ≥10 cm: {constant[0]:N0} → {constant[^1]:N0} if they keep launching, → {responsive[^1]:N0} if they quit.");
        if (!double.IsNaN(quit))
        {
            int q = Array.FindIndex(yr, t => t >= quit - 1e-9);
            sb.AppendLine($"After they quit it still grows {Fmt.Times(responsive[^1] / responsive[q])}: abandoning a band doesn't save it once the cascade runs.");
        }
        var ch = new Chart
        {
            XLabel = "years", YLabel = "belt debris ≥10 cm", LogY = true,
            Tag = $"box model · {rate:F0}/yr at {i.LaunchAltKm:F0} km{(i.WorkingSatellites ? ", working satellites" : ", never deorbited")}",
        };
        if (!double.IsNaN(quit)) ch.Bands.Add(new(quit, yr[^1], Palette.Alpha(Palette.Mute, 30), $"operators stop launching (yr {quit:F0})"));
        ch.Lines.Add(new($"{rate:F0}/yr, launching throughout", Palette.Red, yr, constant));
        ch.Lines.Add(new("responsive: operators throttle, then quit", Palette.Blue, yr, responsive));
        return (sb.ToString(), Show(ch));
    });

    private async void RunWorking(object s, RoutedEventArgs e) => await RunAsync("Working satellites", (cat, i) =>
    {
        var (rates, set, g) = Scenarios.WorkingSweep(cat.Objects, i);
        var sb = new StringBuilder($"Belt debris ≥10 cm after {i.HorizonYears:F0} yr, × today, by disposal / rocket-body / avoidance setting:\n");
        sb.AppendLine("  " + string.Join(" | ", new[] { "per year" }.Concat(set.Select(x => x.Name.Split(' ')[0]))));
        for (int r = 0; r < rates.Length; r++)
            sb.AppendLine($"  {rates[r],8:F0} | " + string.Join(" | ", Enumerable.Range(0, set.Length).Select(k => Fmt.Times(g[r, k]))));
        sb.AppendLine("\nSatellites work for the set lifetime, hold orbit, dodge tracked (≥10 cm) objects (89% can manoeuvre),");
        sb.AppendLine("then are deorbited or left dead. Nothing dodges 1–10 cm debris; a hit there leaves a dead satellite.");
        var colors = new[] { Palette.Red, Palette.Orange, Palette.Blue, Palette.Green, Palette.Amber };
        var ch = new Chart
        {
            YLabel = $"belt debris ≥10 cm after {i.HorizonYears:F0} yr, × today", LogY = true, YFmt = Fmt.Times, BarLabel = Fmt.Times, YMin = 0.8, Headroom = 3,
            Categories = rates.Select(r => $"{r:F0} satellites + rocket bodies a year at {i.LaunchAltKm:F0} km").ToArray(),
            Tag = "box model · deorbited satellites / rocket bodies / conjunctions avoided",
        };
        for (int k = 0; k < set.Length; k++)
        {
            string name = set[k].Pmd < 0 ? set[k].Name : $"{set[k].Name}: {set[k].Pmd:P0} / {set[k].Rb:P0} / {set[k].Avoid:P0}";
            if (k == set.Length - 1) name = set[k].Name;
            ch.Bars.Add(new(name, colors[k % colors.Length], Enumerable.Range(0, rates.Length).Select(r => g[r, k]).ToArray()));
        }
        ch.Refs.Add(new(1, "", Palette.Mute));
        return (sb.ToString(), Show(ch));
    });

    private async void RunRemoval(object s, RoutedEventArgs e) => await RunAsync("Removal", (cat, i) =>
    {
        var (rem, none, launched, rate, today) = Scenarios.Removal(cat.Objects, i);
        double Flat(double[] g) { for (int k = 1; k < rem.Length; k++) if (g[k] <= 1 && g[k - 1] > 1) return rem[k - 1] + (g[k - 1] - 1) / (g[k - 1] - g[k]) * (rem[k] - rem[k - 1]); return g[0] <= 1 ? 0 : double.NaN; }
        double f0 = Flat(none), f1 = Flat(launched);
        var sb = new StringBuilder($"Belt debris ≥10 cm after {i.HorizonYears:F0} yr, × today ({today:N0}), vs large dead objects removed a year (riskiest first):\n");
        sb.AppendLine($"  removed/yr | nothing added | {rate:F0}/yr added");
        for (int k = 0; k < rem.Length; k++) sb.AppendLine($"  {rem[k],10:F0} | {Fmt.Times(none[k]),13} | {Fmt.Times(launched[k]),10}");
        sb.AppendLine($"\nHolds the belt flat: ~{(double.IsNaN(f0) ? ">100" : f0.ToString("F0"))}/yr with nothing added, ~{(double.IsNaN(f1) ? ">100" : f1.ToString("F0"))}/yr with {rate:F0}/yr added.");
        var ch = new Chart
        {
            XLabel = "large dead objects removed per year (riskiest first)", YLabel = $"belt debris ≥10 cm after {i.HorizonYears:F0} yr, × today",
            YFmt = Fmt.Times, YMin = 0, Tag = $"box model · removals from year 0{(i.WorkingSatellites ? " · working satellites" : "")}",
        };
        ch.Lines.Add(new($"{rate:F0}/yr added{(i.WorkingSatellites ? "" : ", never deorbited")}", Palette.Red, rem, launched, Markers: true));
        ch.Lines.Add(new("nothing added", Palette.Green, rem, none, Markers: true));
        ch.Refs.Add(new(1, "×1 = held flat", Palette.Mute));
        if (!double.IsNaN(f0)) ch.Notes.Add(new(f0, 1, $"~{f0:F0}/yr holds it flat", Palette.Green));
        if (!double.IsNaN(f1)) ch.Notes.Add(new(f1, 1, $"~{f1:F0}/yr with {rate:F0}/yr added", Palette.Red));
        return (sb.ToString(), Show(ch));
    });

    // ---------------------------------------------------------------- against NASA ------------------

    private async void RunNasa(object s, RoutedEventArgs e) => await RunAsync("NASA comparison", (cat, i) =>
    {
        var n = Scenarios.NasaComparison(cat.Objects, i);
        double Mean(double[] v, int from, int to) => Enumerable.Range(from, Math.Max(1, to - from + 1)).Where(k => k < v.Length).Select(k => v[k]).DefaultIfEmpty(0).Average();
        int last = n.Years.Length - 1;
        double first10 = Mean(n.CatastrophicTrackedOnly, 1, 10), last10 = Mean(n.CatastrophicTrackedOnly, last - 9, last);
        double first10All = Mean(n.CatastrophicAllSizes, 1, 10), last10All = Mean(n.CatastrophicAllSizes, last - 9, last);
        string F(double v) => double.IsNaN(v) ? ">20" : v.ToString("F0");
        var sb = new StringBuilder("The model on NASA's terms: nothing added, no collision avoidance, every satellite dead from day one.\n\n");
        sb.AppendLine("  what                        | NASA / IADC                                   | this model");
        sb.AppendLine($"  growth with no launches     | LEO ≈ constant for 50 yr, then up (1)         | belt ×{n.BeltGrowthNoExplosions:0.00} without explosions, ×{n.BeltGrowth:0.00} with {i.ExplosionsPerYear:0.#}/yr");
        sb.AppendLine($"  removals to stabilise       | ~5 a year (2, 3): 90% disposal, no explosions  | ~{F(n.HoldFlatNoExplosions)}/yr without explosions, ~{F(n.HoldFlat)}/yr with");
        sb.AppendLine($"  catastrophic collisions/yr  | 0.11–0.20 (4); 0.054 (1), from ~10k objects   | ≥10 cm only: {first10:0.00} (first decade), {last10:0.00} (last)");
        sb.AppendLine($"                              |                                               | incl. 1–10 cm impactors: {first10All:0.00}, {last10All:0.00}");
        sb.AppendLine($"\nLEO objects ≥10 cm here: {n.LeoTrackable[0]:N0} → {n.LeoTrackable[^1]:N0} (the low, unmanoeuvred Starlink shells decay first).");
        sb.AppendLine("Collision rate grows roughly with the square of the population, and today's is larger than NASA's starting");
        sb.AppendLine("points (2006, 2009), which accounts for most of the gap. NASA's runs also allow no avoidance or future explosions;");
        sb.AppendLine("the IADC runs add regular launches with 90% end-of-life disposal.");
        sb.AppendLine("\n(1) Liou & Johnson 2006, Science   (2) Liou, Johnson & Hill 2010, Acta Astronautica   (3) Liou 2011, Adv. Space Res.");
        sb.AppendLine("(4) IADC comparison study 2013: one catastrophic collision every 5–9 years, six agencies' models");

        var ch = new Chart
        {
            XLabel = "years, nothing added", YLabel = "catastrophic collisions per year (log)", LogY = true, YMin = 0.03, Headroom = 4,
            Y2Label = "LEO objects ≥10 cm", Y2Color = Palette.Mute, Y2Min = 0, Y2Fmt = Fmt.Num,
            Tag = "box model · no launches, no avoidance · NASA figures from the sources listed",
        };
        var yr = n.Years.Skip(1).ToArray();
        ch.YBands.Add(new(1.0 / 9, 1.0 / 5, Palette.Alpha(Palette.Green, 45), "IADC 2013: one every 5–9 years"));
        ch.Refs.Add(new(10.8 / 200, "LEGEND, no launches (Liou & Johnson 2006)", Palette.Green, LabelLeft: true));
        ch.Lines.Add(new("this model, ≥10 cm objects only", Palette.Blue, yr, n.CatastrophicTrackedOnly.Skip(1).ToArray()));
        ch.Lines.Add(new("this model, incl. 1–10 cm impactors", Palette.Blue, yr, n.CatastrophicAllSizes.Skip(1).ToArray(), Dashed: true, Thickness: 1.4));
        ch.Lines.Add(new("LEO objects ≥10 cm (right axis)", Palette.Mute, n.Years, n.LeoTrackable, Axis2: true, Thickness: 1.4));
        return (sb.ToString(), Show(ch));
    });

    // ---------------------------------------------------------------- helpers -----------------------

    private static string Pct(double f) => f >= 0.1 ? $"+{f * 100:0}%" : f >= 0.001 ? $"+{f * 100:0.0#}%" : $"{(f >= 0 ? "+" : "")}{f * 100:0.###}%";

    private static string Launches(ScenarioInputs i) => i.LaunchRatePerYear <= 0 ? "nothing added"
        : $"{i.LaunchRatePerYear:F0}/yr at {i.LaunchAltKm:F0} km{(i.WorkingSatellites ? ", working" : ", never deorbited")}";
}
