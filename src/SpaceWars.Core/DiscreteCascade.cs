using System;
using System.Collections.Generic;

namespace SpaceWars.Core;

/// <summary>Yearly snapshots from a discrete cascade run.</summary>
public sealed class CascadeResult
{
    public required double[] Years;
    public required double[] TotalObjects;      // Σ weight of live objects (real objects represented)
    public required double[] TotalCrossSection; // Σ weight·area [m²] — how "clogged" LEO is
    public required double[] CatastrophicPerYear;
    public required double[] SurvivingNails;
}

/// <summary>
/// Discrete, per-object Monte-Carlo debris cascade. Unlike the aggregate box model, every
/// object here is an individual with its own orbit; a fragmentation event spawns new
/// objects whose orbital elements come from the parent orbit plus a breakup Δv, so they
/// scatter across altitudes and become projectiles in their own right — the literal
/// "each new piece is a collider" feedback.
///
/// To stay tractable, objects are super-particles carrying a weight = number of real
/// objects represented (standard PIC technique); collision statistics use the weights.
/// Collision hazard per shell is the well-mixed rate ½·Σ w_i w_j ⟨σ v⟩ / V; each event's
/// fragment spectrum comes from the NASA breakup model, mass-conserved. Drag decays every
/// object by its own area-to-mass ratio.
/// </summary>
public sealed class DiscreteCascade
{
    public double RelVelMetersPerSec { get; init; } = 10_000.0;
    public double SolarActivity { get; init; } = 1.0;
    public double FragmentDeltaVKmS { get; init; } = 0.1; // per-axis breakup Δv spread
    public int MaxObjects { get; init; } = 150_000;       // coarsening trigger (see below)

    /// <summary>Ongoing launch traffic: intact objects added per year (the Kessler driver).</summary>
    public double LaunchRatePerYear { get; set; } = 0.0;
    public double LaunchAltKm { get; set; } = 900.0;
    private double _launchAccrual;

    /// <summary>Economically rational launch: throttle back as the band's hazard rises, stop at the tolerance.</summary>
    public bool ResponsiveLaunch { get; set; } = false;
    public double LossTolerancePerYear { get; set; } = 0.02;
    public double OperationalSatAreaM2 { get; set; } = 5.0;
    private double _lastThrottle = 1.0;
    public double LastLaunchThrottle => _lastThrottle;

    // MaxObjects is a coarsening trigger, not a population cap: past it, super-particles are
    // merged within (shell, mass) bins so the represented real-object total (carried in the
    // weights) stays UNBOUNDED and a true runaway is never artificially truncated. HardCap is
    // only an out-of-memory backstop that a single step's spawns cannot exceed.
    private const int HardCap = 2_000_000;
    public bool CoarsenedThisRun { get; private set; }

    private readonly int _nShell;
    private readonly double _minAlt, _binKm;
    private readonly double[] _volM3;
    private readonly Random _rng;

    // Structure-of-arrays object store.
    private readonly List<double> _a = new(), _e = new(), _inc = new(), _raan = new(),
        _argp = new(), _m = new(), _mass = new(), _area = new(), _sqrtA = new(), _w = new();
    private readonly List<bool> _alive = new();
    private readonly List<bool> _isNail = new();

    private static readonly double[] LcEdges = { 0.01, 0.0316, 0.1, 0.316, 1.0, 3.16, 10.0 };

    public DiscreteCascade(int shellCount = 36, double minAltKm = 200, double binKm = 50, int seed = 1)
    {
        _nShell = shellCount; _minAlt = minAltKm; _binKm = binKm; _rng = new Random(seed);
        _volM3 = new double[shellCount];
        const double reM = 6_378_135.0;
        for (int s = 0; s < shellCount; s++)
        {
            double lo = minAltKm + s * binKm, hi = lo + binKm;
            double rLo = reM + lo * 1000, rHi = reM + hi * 1000;
            _volM3[s] = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
        }
    }

    private static double BulkDensity(double lc) => lc < 0.08 ? 2698.9 : 92.937 * Math.Pow(lc, -0.74);
    private static double MassFromLc(double lc) => BulkDensity(lc) * (Math.PI / 6.0) * lc * lc * lc;
    private static double AreaFromLc(double lc) => 0.556945 * Math.Pow(lc, 2.0047);

    private void Add(in OrbitalElements el, double mass, double area, double weight, bool nail)
    {
        _a.Add(el.SemiMajorAxis); _e.Add(el.Eccentricity); _inc.Add(el.Inclination);
        _raan.Add(el.Raan); _argp.Add(el.ArgPerigee); _m.Add(el.MeanAnomaly);
        _mass.Add(mass); _area.Add(area); _sqrtA.Add(Math.Sqrt(area));
        _w.Add(weight); _alive.Add(true); _isNail.Add(nail);
    }

    private int ShellOf(double aKm)
    {
        double alt = aKm - Constants.EarthRadiusKm;
        if (alt < _minAlt) return -1;
        int s = (int)((alt - _minAlt) / _binKm);
        return s < _nShell ? s : -1;
    }

    /// <summary>Seed intacts from the catalog plus a modelled 1–10 cm background.</summary>
    public void SeedFromCatalog(IReadOnlyList<OrbitalElements> catalog,
        double largeIntactFraction = 0.15, double backgroundSmallTotal = 1_000_000,
        int backgroundSuperParticles = 4000, double backgroundLargeTotal = 8_000)
    {
        // Observed payloads at their real altitudes (no SATCAT: fixed large fraction).
        foreach (var el in catalog)
        {
            int s = ShellOf(el.SemiMajorAxis); if (s < 0) continue;
            bool big = _rng.NextDouble() < largeIntactFraction;
            Add(el, big ? 2400.0 : 180.0, big ? 18.0 : 1.78, 1.0, nail: false);
        }
        SeedBackground(backgroundSmallTotal, backgroundSuperParticles, backgroundLargeTotal);
    }

    /// <summary>Seed observed objects with SATCAT-derived masses/areas (per object).</summary>
    public void SeedFromCatalog(IReadOnlyList<CatalogObject> objects,
        double backgroundSmallTotal = 1_000_000, int backgroundSuperParticles = 4000, double backgroundLargeTotal = 8_000)
    {
        foreach (var o in objects)
        {
            if (ShellOf(o.Elements.SemiMajorAxis) < 0) continue;
            Add(o.Elements, o.MassKg, o.AreaM2, 1.0, nail: false);
        }
        SeedBackground(backgroundSmallTotal, backgroundSuperParticles, backgroundLargeTotal);
    }

    private void SeedBackground(double backgroundSmallTotal, int backgroundSuperParticles, double backgroundLargeTotal)
    {
        double lc0 = Math.Sqrt(LcEdges[0] * LcEdges[1]), lc1 = Math.Sqrt(LcEdges[1] * LcEdges[2]);
        var w = new double[_nShell]; double wsum = 0;
        for (int s = 0; s < _nShell; s++) { w[s] = DebrisEnvironment.SpatialWeight(_minAlt + (s + 0.5) * _binKm); wsum += w[s]; }
        if (wsum <= 0) return;

        OrbitalElements RandEl(double rev) => OrbitalElements.FromMeanMotionRevPerDay(rev, 0.001,
            (30 + 120 * _rng.NextDouble()) * Constants.DegToRad,
            _rng.NextDouble() * Constants.TwoPi, 0, _rng.NextDouble() * Constants.TwoPi);

        for (int s = 0; s < _nShell; s++)
        {
            double f = w[s] / wsum;
            double alt = _minAlt + (s + 0.5) * _binKm;
            double a = Constants.EarthRadiusKm + alt;
            double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;

            int sp = Math.Max(1, (int)Math.Round(backgroundSuperParticles * f));
            double smallShare = backgroundSmallTotal * f;
            if (smallShare > 0)
                for (int k = 0; k < sp; k++)
                {
                    bool coarse = _rng.NextDouble() < 0.3;
                    double lc = coarse ? lc1 : lc0;
                    Add(RandEl(rev), MassFromLc(lc), AreaFromLc(lc), smallShare / sp, nail: false);
                }

            double largeShare = backgroundLargeTotal * f;
            if (largeShare > 1e-6)
            {
                Add(RandEl(rev), 180.0, 1.78, 0.85 * largeShare, nail: false);
                Add(RandEl(rev), 2400.0, 18.0, 0.15 * largeShare, nail: false);
            }
        }
    }

    /// <summary>Inject the barrel as nail super-particles carrying the real nail mass/area.</summary>
    public void InjectBarrel(IReadOnlyList<OrbitalElements> nailCloud, NailSpec nail, int superParticles, int totalNails)
    {
        int step = Math.Max(1, nailCloud.Count / superParticles);
        int used = 0; for (int i = 0; i < nailCloud.Count; i += step) used++;
        double weightPer = (double)totalNails / used;
        for (int i = 0; i < nailCloud.Count; i += step)
            Add(nailCloud[i], nail.MassKg, nail.MeanCrossSectionM2, weightPer, nail: true);
    }

    public double TotalObjects() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i]) t += _w[i]; return t; }
    public double TotalNails() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i] && _isNail[i]) t += _w[i]; return t; }
    public double TotalCrossSection() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i]) t += _w[i] * _area[i]; return t; }

    private double NextGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble(), u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(Constants.TwoPi * u2);
    }
    private int Poisson(double lam)
    {
        if (lam <= 0) return 0;
        if (lam < 30) { double l = Math.Exp(-lam), p = 1; int k = 0; do { k++; p *= _rng.NextDouble(); } while (p > l); return k - 1; }
        return Math.Max(0, (int)Math.Round(lam + Math.Sqrt(lam) * NextGaussian()));
    }

    /// <summary>Advance dt seconds; returns catastrophic collision count. Collisions then drag.</summary>
    public double Step(double dtSec)
    {
        int n = _w.Count;
        // Per-shell aggregates and weighted (w·√A) partner-sampling lists.
        var sumW = new double[_nShell]; var sumWA = new double[_nShell]; var sumWsqrtA = new double[_nShell];
        var idxByShell = new List<int>[_nShell];
        for (int s = 0; s < _nShell; s++) idxByShell[s] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (!_alive[i]) continue;
            int s = ShellOf(_a[i]); if (s < 0) continue;
            sumW[s] += _w[i]; sumWA[s] += _w[i] * _area[i]; sumWsqrtA[s] += _w[i] * _sqrtA[i];
            idxByShell[s].Add(i);
        }

        double catastrophic = 0;
        for (int s = 0; s < _nShell; s++)
        {
            var lst = idxByShell[s];
            if (lst.Count < 2) continue;
            double V = _volM3[s];
            double rate = RelVelMetersPerSec / V * (sumW[s] * sumWA[s] + sumWsqrtA[s] * sumWsqrtA[s]);
            int events = Poisson(rate * dtSec);
            if (events <= 0) continue;

            // cumulative w·√A for weighted partner picks in this shell
            var cum = new double[lst.Count]; double acc = 0;
            for (int t = 0; t < lst.Count; t++) { acc += _w[lst[t]] * _sqrtA[lst[t]]; cum[t] = acc; }

            for (int ev = 0; ev < events && _w.Count < HardCap; ev++)
            {
                int i = lst[PickWeighted(cum, acc)];
                int j = lst[PickWeighted(cum, acc)];
                if (!_alive[i] || !_alive[j]) continue;
                // i==j is a valid intra-population collision when the super-particle stands for
                // ≥2 real objects (matches the self-pair term in the shell rate). This is what
                // keeps merged/coarsened populations — and same-class intact-vs-intact events —
                // from being silently dropped.
                if (i == j && _w[i] < 2) continue;

                double mt = Math.Max(_mass[i], _mass[j]), mp = Math.Min(_mass[i], _mass[j]);
                int proj = _mass[i] <= _mass[j] ? i : j;
                int parent = _mass[i] >= _mass[j] ? i : j;
                bool cat = Lethality.IsCatastrophic(mp, RelVelMetersPerSec, mt);

                if (cat) { _w[i] -= 1; _w[j] -= 1; if (_w[i] <= 0) _alive[i] = false; if (_w[j] <= 0) _alive[j] = false; catastrophic++; }
                else { _w[proj] -= 1; if (_w[proj] <= 0) _alive[proj] = false; }

                double meff = cat ? (mt + mp) : BreakupModel.EffectiveMass(mt, mp, RelVelMetersPerSec);
                double availMass = cat ? (mt + mp) : Math.Min(mt, 50.0 * mp);
                SpawnFragments(parent, meff, availMass);
            }
        }

        DragStep(dtSec);
        ApplyLaunch(dtSec);
        return catastrophic;
    }

    private void ApplyLaunch(double dtSec)
    {
        if (LaunchRatePerYear <= 0 || _w.Count >= HardCap) return;

        double throttle = 1.0;
        if (ResponsiveLaunch)
        {
            int ls = ShellOf(Constants.EarthRadiusKm + LaunchAltKm);
            if (ls >= 0)
            {
                double V = _volM3[ls], sqrtSat = Math.Sqrt(OperationalSatAreaM2), loss = 0;
                for (int i = 0; i < _w.Count; i++)
                {
                    if (!_alive[i] || ShellOf(_a[i]) != ls) continue;
                    double sigma = Math.Pow(sqrtSat + _sqrtA[i], 2.0);
                    loss += _w[i] * sigma;
                }
                double lossPerYear = loss / V * RelVelMetersPerSec * 365.25 * Constants.SecondsPerDay;
                throttle = Math.Max(0.0, 1.0 - lossPerYear / LossTolerancePerYear);
            }
        }
        _lastThrottle = throttle;

        _launchAccrual += LaunchRatePerYear * throttle * dtSec / (365.25 * Constants.SecondsPerDay);
        if (_launchAccrual < 1.0) return; // batch fractional launches into whole intacts

        double batch = _launchAccrual; _launchAccrual = 0;
        double a = Constants.EarthRadiusKm + LaunchAltKm;
        double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        OrbitalElements Make() => OrbitalElements.FromMeanMotionRevPerDay(rev, 0.001,
            (45 + 45 * _rng.NextDouble()) * Constants.DegToRad,
            _rng.NextDouble() * Constants.TwoPi, 0, _rng.NextDouble() * Constants.TwoPi);
        Add(Make(), 180.0, 1.78, 0.85 * batch, nail: false);   // payloads
        Add(Make(), 2400.0, 18.0, 0.15 * batch, nail: false);  // rocket bodies
    }

    private int PickWeighted(double[] cum, double total)
    {
        double x = _rng.NextDouble() * total;
        int lo = 0, hi = cum.Length - 1;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (cum[mid] < x) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private void SpawnFragments(int parent, double meff, double availMass)
    {
        var pel = new OrbitalElements
        {
            SemiMajorAxis = _a[parent], Eccentricity = _e[parent], Inclination = _inc[parent],
            Raan = _raan[parent], ArgPerigee = _argp[parent], MeanAnomaly = _m[parent],
            MeanMotion = Math.Sqrt(Constants.Mu / Math.Pow(_a[parent], 3)),
        };
        pel.ComputeSecularRates();
        double period = Constants.TwoPi / pel.MeanMotion;
        var (pos, vel) = pel.StateAt(_rng.NextDouble() * period);

        // mass-conserving fragment counts per size class
        int nc = LcEdges.Length - 1;
        Span<double> cnt = stackalloc double[8]; Span<double> mcls = stackalloc double[8]; Span<double> acls = stackalloc double[8];
        double fragMass = 0;
        for (int c = 0; c < nc; c++)
        {
            double lc = Math.Sqrt(LcEdges[c] * LcEdges[c + 1]);
            mcls[c] = MassFromLc(lc); acls[c] = AreaFromLc(lc);
            double num = BreakupModel.CountLargerThan(meff, LcEdges[c]) - BreakupModel.CountLargerThan(meff, LcEdges[c + 1]);
            cnt[c] = num > 0 ? num : 0; fragMass += cnt[c] * mcls[c];
        }
        double scale = (fragMass > availMass && fragMass > 0) ? availMass / fragMass : 1.0;

        for (int c = 0; c < nc && _w.Count < HardCap; c++)
        {
            double weight = cnt[c] * scale;
            if (weight < 1e-6) continue;
            var dv = new Vec3(FragmentDeltaVKmS * NextGaussian(), FragmentDeltaVKmS * NextGaussian(), FragmentDeltaVKmS * NextGaussian());
            var frag = OrbitalElements.FromStateVector(pos, vel + dv);
            if (frag.Eccentricity >= 1 || frag.SemiMajorAxis <= Constants.EarthRadiusKm) continue; // escaped/decayed instantly
            Add(frag, mcls[c], acls[c], weight, nail: false);
        }
    }

    private void DragStep(double dtSec)
    {
        double reentryA = Constants.EarthRadiusKm + AtmosphericDrag.ReentryAltitudeKm;
        for (int i = 0; i < _w.Count; i++)
        {
            if (!_alive[i]) continue;
            double aoverm = _area[i] / _mass[i];
            double rate = -AtmosphericDrag.SemiMajorAxisDecayRateKmPerSec(_a[i], aoverm, SolarActivity); // km/s >0
            _a[i] -= rate * dtSec;
            if (_a[i] <= reentryA) _alive[i] = false;
        }
    }

    private void Compact()
    {
        int w = 0;
        for (int r = 0; r < _w.Count; r++)
        {
            if (!_alive[r]) continue;
            if (w != r)
            {
                _a[w] = _a[r]; _e[w] = _e[r]; _inc[w] = _inc[r]; _raan[w] = _raan[r]; _argp[w] = _argp[r];
                _m[w] = _m[r]; _mass[w] = _mass[r]; _area[w] = _area[r]; _sqrtA[w] = _sqrtA[r];
                _w[w] = _w[r]; _alive[w] = true; _isNail[w] = _isNail[r];
            }
            w++;
        }
        int remove = _w.Count - w;
        if (remove > 0)
        {
            _a.RemoveRange(w, remove); _e.RemoveRange(w, remove); _inc.RemoveRange(w, remove);
            _raan.RemoveRange(w, remove); _argp.RemoveRange(w, remove); _m.RemoveRange(w, remove);
            _mass.RemoveRange(w, remove); _area.RemoveRange(w, remove); _sqrtA.RemoveRange(w, remove);
            _w.RemoveRange(w, remove); _alive.RemoveRange(w, remove); _isNail.RemoveRange(w, remove);
        }
    }

    /// <summary>
    /// Merge super-particles within each (shell, mass, kind) bin into one representative,
    /// summing weights. Preserves total real-object count and per-shell collision aggregates
    /// so a runaway is carried in the weights, not the particle count.
    /// </summary>
    private void Coalesce()
    {
        var acc = new Dictionary<(int s, int mkey, bool nail), double[]>();
        for (int i = 0; i < _w.Count; i++)
        {
            if (!_alive[i]) continue;
            double alt = _a[i] - Constants.EarthRadiusKm;
            int s = alt < _minAlt ? 0 : (int)((alt - _minAlt) / _binKm);
            if (s >= _nShell) s = _nShell - 1; if (s < 0) s = 0;
            int mkey = (int)Math.Round(Math.Log(_mass[i]) * 4);
            var key = (s, mkey, _isNail[i]);
            if (!acc.TryGetValue(key, out var v)) { v = new double[9]; v[7] = _mass[i]; v[8] = _area[i]; acc[key] = v; }
            double w = _w[i];
            v[0] += w; v[1] += w * _a[i]; v[2] += w * _e[i]; v[3] += w * _inc[i];
            v[4] += w * _raan[i]; v[5] += w * _argp[i]; v[6] += w * _m[i];
        }

        _a.Clear(); _e.Clear(); _inc.Clear(); _raan.Clear(); _argp.Clear(); _m.Clear();
        _mass.Clear(); _area.Clear(); _sqrtA.Clear(); _w.Clear(); _alive.Clear(); _isNail.Clear();
        foreach (var (key, v) in acc)
        {
            double w = v[0]; if (w <= 0) continue;
            _a.Add(v[1] / w); _e.Add(v[2] / w); _inc.Add(v[3] / w); _raan.Add(v[4] / w);
            _argp.Add(v[5] / w); _m.Add(v[6] / w); _mass.Add(v[7]); _area.Add(v[8]);
            _sqrtA.Add(Math.Sqrt(v[8])); _w.Add(w); _alive.Add(true); _isNail.Add(key.nail);
        }
        CoarsenedThisRun = true;
    }

    public CascadeResult Run(double horizonYears = 50, double dtDays = 15)
    {
        double dtSec = dtDays * Constants.SecondsPerDay;
        int steps = (int)Math.Round(horizonYears * 365.25 / dtDays);
        var yr = new List<double> { 0 }; var tot = new List<double> { TotalObjects() };
        var cs = new List<double> { TotalCrossSection() }; var cpy = new List<double> { 0 }; var nl = new List<double> { TotalNails() };

        double catAccum = 0, t = 0, nextYear = 0;
        for (int i = 0; i < steps; i++)
        {
            catAccum += Step(dtSec);
            if (i % 8 == 7) Compact();
            if (_w.Count > MaxObjects) Coalesce();
            t += dtDays / 365.25;
            if (t >= nextYear + 1)
            {
                nextYear += 1;
                yr.Add(t); tot.Add(TotalObjects()); cs.Add(TotalCrossSection()); cpy.Add(catAccum); nl.Add(TotalNails());
                catAccum = 0;
            }
        }
        return new CascadeResult
        {
            Years = yr.ToArray(), TotalObjects = tot.ToArray(), TotalCrossSection = cs.ToArray(),
            CatastrophicPerYear = cpy.ToArray(), SurvivingNails = nl.ToArray(),
        };
    }
}
