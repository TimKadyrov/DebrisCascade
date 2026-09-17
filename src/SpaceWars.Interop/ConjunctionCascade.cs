using System;
using System.Collections.Generic;
using SpaceWars.Core;

namespace SpaceWars.Interop;

/// <summary>
/// Tier-3 per-object cascade using Liou's Cube method (2003). Every object is individual:
/// each step the whole population is propagated to real ECI state (GPU sw_propagate_state,
/// CPU fallback), binned into small spatial cubes, and a collision is drawn only between
/// objects that actually share a cube — with the true relative velocity of that encounter.
/// Fragments are spawned at the real collision point/velocity plus a breakup Δv, so they
/// enter whatever orbits (and shells) the geometry dictates. Unlike the box/discrete models,
/// there is NO mean-altitude binning in the collision step, so eccentric objects colliding
/// at apogee/perigee — across shells — are captured directly.
///
/// Super-particles (weight = real objects represented) keep it tractable; a coarsening pass
/// bounds the particle count in a violent runaway without capping the represented total.
/// </summary>
public sealed class ConjunctionCascade
{
    public double CubeKm { get; init; } = 20.0;
    public int SubSamples { get; init; } = 4;         // phase samples per step (Cube averaging)
    public double FragmentDeltaVKmS { get; init; } = 0.1;
    public double SolarActivity { get; init; } = 1.0;
    public int CoarsenAbove { get; init; } = 200_000;
    public bool UsedGpu { get; private set; }
    public bool Coarsened { get; private set; }

    private static readonly double[] LcEdges = { 0.01, 0.0316, 0.1, 0.316, 1.0, 3.16, 10.0 };
    private readonly Random _rng;

    private readonly List<OrbitalElements> _els = new();
    private readonly List<double> _epoch = new(), _mass = new(), _area = new(), _sqrtA = new(), _w = new();
    private readonly List<bool> _alive = new(), _isNail = new();
    private double _simSec;

    public ConjunctionCascade(int seed = 1) => _rng = new Random(seed);

    private static double BulkDensity(double lc) => lc < 0.08 ? 2698.9 : 92.937 * Math.Pow(lc, -0.74);
    private static double MassFromLc(double lc) => BulkDensity(lc) * (Math.PI / 6.0) * lc * lc * lc;
    private static double AreaFromLc(double lc) => 0.556945 * Math.Pow(lc, 2.0047);

    private void Add(OrbitalElements el, double mass, double area, double weight, bool nail)
    {
        _els.Add(el); _epoch.Add(_simSec); _mass.Add(mass); _area.Add(area);
        _sqrtA.Add(Math.Sqrt(area)); _w.Add(weight); _alive.Add(true); _isNail.Add(nail);
    }

    private static OrbitalElements Circular(double altKm, double incRad, double raan, double m)
    {
        double a = Constants.EarthRadiusKm + altKm;
        double rev = Math.Sqrt(Constants.Mu / (a * a * a)) * Constants.SecondsPerDay / Constants.TwoPi;
        return OrbitalElements.FromMeanMotionRevPerDay(rev, 0.001, incRad, raan, 0, m);
    }

    public void SeedFromCatalog(IReadOnlyList<OrbitalElements> catalog,
        double largeIntactFraction = 0.15, double backgroundSmallTotal = 1_000_000,
        int backgroundSuperParticles = 3000, double backgroundLargeTotal = 8_000,
        int nShell = 36, double minAltKm = 200, double binKm = 50)
    {
        foreach (var el in catalog)
        {
            double alt = el.SemiMajorAxis - Constants.EarthRadiusKm;
            if (alt < minAltKm || alt > minAltKm + nShell * binKm) continue;
            bool big = _rng.NextDouble() < largeIntactFraction;
            Add(el, big ? 2400.0 : 180.0, big ? 18.0 : 1.78, 1.0, nail: false);
        }

        double lc0 = Math.Sqrt(LcEdges[0] * LcEdges[1]), lc1 = Math.Sqrt(LcEdges[1] * LcEdges[2]);
        var w = new double[nShell]; double wsum = 0;
        for (int s = 0; s < nShell; s++) { w[s] = DebrisEnvironment.SpatialWeight(minAltKm + (s + 0.5) * binKm); wsum += w[s]; }
        if (wsum <= 0) return;

        for (int s = 0; s < nShell; s++)
        {
            double f = w[s] / wsum, alt = minAltKm + (s + 0.5) * binKm;
            int sp = Math.Max(1, (int)Math.Round(backgroundSuperParticles * f));
            double smallShare = backgroundSmallTotal * f;
            for (int k = 0; k < sp; k++)
            {
                bool coarse = _rng.NextDouble() < 0.3;
                double lc = coarse ? lc1 : lc0;
                Add(Circular(alt, (30 + 120 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi),
                    MassFromLc(lc), AreaFromLc(lc), smallShare / sp, nail: false);
            }
            double largeShare = backgroundLargeTotal * f;
            if (largeShare > 1e-6)
            {
                Add(Circular(alt, (30 + 120 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi), 180.0, 1.78, 0.85 * largeShare, false);
                Add(Circular(alt, (30 + 120 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi), 2400.0, 18.0, 0.15 * largeShare, false);
            }
        }
    }

    public void InjectBarrel(IReadOnlyList<OrbitalElements> nailCloud, NailSpec nail, int superParticles, int totalNails)
    {
        int step = Math.Max(1, nailCloud.Count / superParticles);
        int used = 0; for (int i = 0; i < nailCloud.Count; i += step) used++;
        double wp = (double)totalNails / used;
        for (int i = 0; i < nailCloud.Count; i += step) Add(nailCloud[i], nail.MassKg, nail.MeanCrossSectionM2, wp, nail: true);
    }

    public double TotalObjects() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i]) t += _w[i]; return t; }
    public double TotalNails() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i] && _isNail[i]) t += _w[i]; return t; }
    public double TotalCrossSection() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i]) t += _w[i] * _area[i]; return t; }

    private double NextGaussian() { double u1 = 1 - _rng.NextDouble(), u2 = 1 - _rng.NextDouble(); return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(Constants.TwoPi * u2); }
    private int Poisson(double lam)
    {
        if (lam <= 0) return 0;
        if (lam > 1e6) lam = 1e6;
        if (lam < 30) { double l = Math.Exp(-lam), p = 1; int k = 0; do { k++; p *= _rng.NextDouble(); } while (p > l); return k - 1; }
        return Math.Max(0, (int)Math.Round(lam + Math.Sqrt(lam) * NextGaussian()));
    }

    private double[] PropagateState(double sampleTimeSec)
    {
        var epoch = _epoch.ToArray();
        if (Cuda.Available)
        {
            try { UsedGpu = true; return Cuda.PropagateState(_els, epoch, sampleTimeSec); }
            catch { UsedGpu = false; }
        }
        var s = new double[_els.Count * 6];
        for (int i = 0; i < _els.Count; i++)
        {
            var (p, v) = _els[i].StateAt(sampleTimeSec - epoch[i]);
            s[6 * i] = p.X; s[6 * i + 1] = p.Y; s[6 * i + 2] = p.Z;
            s[6 * i + 3] = v.X; s[6 * i + 4] = v.Y; s[6 * i + 5] = v.Z;
        }
        return s;
    }

    private readonly struct Hit(int i, int j, bool cat, double px, double py, double pz, double vx, double vy, double vz)
    { public readonly int I = i, J = j; public readonly bool Cat = cat; public readonly double Px = px, Py = py, Pz = pz, Vx = vx, Vy = vy, Vz = vz; }

    public double Step(double dtSec)
    {
        double L = CubeKm, Lm = L * 1000.0, Vcube = Lm * Lm * Lm;
        double dtSub = dtSec / SubSamples;
        var hits = new List<Hit>();

        for (int sub = 0; sub < SubSamples; sub++)
        {
            double tSample = _simSec + _rng.NextDouble() * 5400.0; // random phase within ~one orbit
            double[] st = PropagateState(tSample);

            var cubes = new Dictionary<long, List<int>>();
            for (int i = 0; i < _els.Count; i++)
            {
                if (!_alive[i]) continue;
                long ix = (long)Math.Floor(st[6 * i] / L) + 1024;
                long iy = (long)Math.Floor(st[6 * i + 1] / L) + 1024;
                long iz = (long)Math.Floor(st[6 * i + 2] / L) + 1024;
                long key = (ix << 42) | (iy << 21) | iz;
                if (!cubes.TryGetValue(key, out var lst)) { lst = new List<int>(); cubes[key] = lst; }
                lst.Add(i);
            }

            foreach (var lst in cubes.Values)
            {
                int m = lst.Count; if (m < 2) continue;
                for (int p = 0; p < m; p++)
                    for (int q = p + 1; q < m; q++)
                    {
                        int i = lst[p], j = lst[q];
                        double dvx = st[6 * i + 3] - st[6 * j + 3], dvy = st[6 * i + 4] - st[6 * j + 4], dvz = st[6 * i + 5] - st[6 * j + 5];
                        double vrel = Math.Sqrt(dvx * dvx + dvy * dvy + dvz * dvz) * 1000.0; // m/s
                        if (vrel <= 0) continue;
                        double sigma = (_sqrtA[i] + _sqrtA[j]); sigma *= sigma; // m^2
                        double lam = _w[i] * _w[j] * sigma * vrel * dtSub / Vcube;
                        int ev = Poisson(lam);
                        for (int e = 0; e < ev; e++)
                        {
                            double mt = Math.Max(_mass[i], _mass[j]), mp = Math.Min(_mass[i], _mass[j]);
                            bool cat = Lethality.IsCatastrophic(mp, vrel, mt);
                            int par = _mass[i] >= _mass[j] ? i : j;
                            hits.Add(new Hit(i, j, cat, st[6 * par], st[6 * par + 1], st[6 * par + 2], st[6 * par + 3], st[6 * par + 4], st[6 * par + 5]));
                        }
                    }
            }
        }

        double catastrophic = 0;
        foreach (var h in hits)
        {
            if (!_alive[h.I] || !_alive[h.J]) continue;
            double mt = Math.Max(_mass[h.I], _mass[h.J]), mp = Math.Min(_mass[h.I], _mass[h.J]);
            int proj = _mass[h.I] <= _mass[h.J] ? h.I : h.J;
            double vrel = Math.Sqrt(h.Vx * h.Vx + h.Vy * h.Vy + h.Vz * h.Vz) * 1000.0; // parent speed as encounter proxy
            if (h.Cat)
            {
                _w[h.I] -= 1; _w[h.J] -= 1;
                if (_w[h.I] <= 0) _alive[h.I] = false; if (_w[h.J] <= 0) _alive[h.J] = false;
                catastrophic++;
            }
            else { _w[proj] -= 1; if (_w[proj] <= 0) _alive[proj] = false; }

            double relForBreak = 10_000.0; // representative closing speed for the breakup spectrum
            double meff = h.Cat ? (mt + mp) : BreakupModel.EffectiveMass(mt, mp, relForBreak);
            double availMass = h.Cat ? (mt + mp) : Math.Min(mt, 50.0 * mp);
            SpawnFragments(new Vec3(h.Px, h.Py, h.Pz), new Vec3(h.Vx, h.Vy, h.Vz), meff, availMass);
        }

        DragStep(dtSec);
        _simSec += dtSec;
        return catastrophic;
    }

    private void SpawnFragments(Vec3 pos, Vec3 vel, double meff, double availMass)
    {
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
        for (int c = 0; c < nc; c++)
        {
            double weight = cnt[c] * scale; if (weight < 1e-6) continue;
            var dv = new Vec3(FragmentDeltaVKmS * NextGaussian(), FragmentDeltaVKmS * NextGaussian(), FragmentDeltaVKmS * NextGaussian());
            var frag = OrbitalElements.FromStateVector(pos, vel + dv);
            if (frag.Eccentricity >= 1 || frag.SemiMajorAxis <= Constants.EarthRadiusKm) continue;
            Add(frag, mcls[c], acls[c], weight, nail: false);
        }
    }

    private void DragStep(double dtSec)
    {
        double reentryA = Constants.EarthRadiusKm + AtmosphericDrag.ReentryAltitudeKm;
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i]) continue;
            var el = _els[i];
            double rate = -AtmosphericDrag.SemiMajorAxisDecayRateKmPerSec(el.SemiMajorAxis, _area[i] / _mass[i], SolarActivity);
            double a = el.SemiMajorAxis - rate * dtSec;
            if (a <= reentryA) { _alive[i] = false; continue; }
            el.SemiMajorAxis = a; el.MeanMotion = Math.Sqrt(Constants.Mu / (a * a * a)); el.ComputeSecularRates();
            _els[i] = el;
        }
    }

    private void Coalesce()
    {
        var acc = new Dictionary<(int, int, bool), double[]>();
        var rep = new Dictionary<(int, int, bool), OrbitalElements>();
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i]) continue;
            int skey = (int)((_els[i].SemiMajorAxis - Constants.EarthRadiusKm) / 50.0);
            int mkey = (int)Math.Round(Math.Log(_mass[i]) * 4);
            var key = (skey, mkey, _isNail[i]);
            if (!acc.TryGetValue(key, out var v)) { v = new double[3]; v[1] = _mass[i]; v[2] = _area[i]; acc[key] = v; rep[key] = _els[i]; }
            v[0] += _w[i];
        }
        _els.Clear(); _epoch.Clear(); _mass.Clear(); _area.Clear(); _sqrtA.Clear(); _w.Clear(); _alive.Clear(); _isNail.Clear();
        foreach (var (key, v) in acc)
        {
            _els.Add(rep[key]); _epoch.Add(_simSec); _mass.Add(v[1]); _area.Add(v[2]);
            _sqrtA.Add(Math.Sqrt(v[2])); _w.Add(v[0]); _alive.Add(true); _isNail.Add(key.Item3);
        }
        Coarsened = true;
    }

    private void Compact()
    {
        int w = 0;
        for (int r = 0; r < _els.Count; r++)
        {
            if (!_alive[r]) continue;
            if (w != r) { _els[w] = _els[r]; _epoch[w] = _epoch[r]; _mass[w] = _mass[r]; _area[w] = _area[r]; _sqrtA[w] = _sqrtA[r]; _w[w] = _w[r]; _alive[w] = true; _isNail[w] = _isNail[r]; }
            w++;
        }
        int rem = _els.Count - w;
        if (rem > 0)
        {
            _els.RemoveRange(w, rem); _epoch.RemoveRange(w, rem); _mass.RemoveRange(w, rem); _area.RemoveRange(w, rem);
            _sqrtA.RemoveRange(w, rem); _w.RemoveRange(w, rem); _alive.RemoveRange(w, rem); _isNail.RemoveRange(w, rem);
        }
    }

    public CascadeResult Run(double horizonYears = 50, double dtDays = 60)
    {
        double dtSec = dtDays * Constants.SecondsPerDay;
        int steps = (int)Math.Round(horizonYears * 365.25 / dtDays);
        var yr = new List<double> { 0 }; var tot = new List<double> { TotalObjects() };
        var cs = new List<double> { TotalCrossSection() }; var cpy = new List<double> { 0 }; var nl = new List<double> { TotalNails() };
        double catAccum = 0, t = 0, nextYear = 0;
        for (int i = 0; i < steps; i++)
        {
            catAccum += Step(dtSec);
            Compact();
            if (_els.Count > CoarsenAbove) Coalesce();
            t += dtDays / 365.25;
            if (t >= nextYear + 1)
            {
                nextYear += 1;
                yr.Add(t); tot.Add(TotalObjects()); cs.Add(TotalCrossSection()); cpy.Add(catAccum); nl.Add(TotalNails());
                catAccum = 0;
            }
        }
        return new CascadeResult { Years = yr.ToArray(), TotalObjects = tot.ToArray(), TotalCrossSection = cs.ToArray(), CatastrophicPerYear = cpy.ToArray(), SurvivingNails = nl.ToArray() };
    }
}
