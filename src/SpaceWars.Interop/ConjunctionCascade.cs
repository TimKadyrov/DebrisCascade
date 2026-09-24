using System;
using System.Collections.Generic;
using System.Linq;
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
    public int CoarsenAbove { get; init; } = 120_000;
    private const int HardCap = 800_000;          // absolute object cap (memory backstop)
    private const int MaxHitsPerStep = 200_000;   // bound the per-step collision list
    public bool UsedGpu { get; private set; }

    /// <summary>
    /// Skip encounters between two catalogued payloads flying in formation — orbital planes within
    /// <see cref="FormationPlaneDeg"/> and semi-major axes within <see cref="FormationDeltaAKm"/>, i.e.
    /// neighbours in one constellation plane. The cube method treats objects sharing a cube as randomly
    /// placed, but operators hold that spacing, so these aren't collision opportunities (calibration:
    /// ~26/yr phantom encounters, 96% Starlink/OneWeb/Qianfan-type pairs). The catalog has no
    /// active/dead flag, so dead satellites still in their slot are skipped too.
    /// </summary>
    public bool ExcludeFormationPairs { get; init; } = true;
    public double FormationPlaneDeg { get; init; } = 1.0;
    public double FormationDeltaAKm { get; init; } = 20.0;

    private bool FormationNeighbours(int i, int j, double[] st)
    {
        if (!ExcludeFormationPairs || _tag[i] != "PAY" || _tag[j] != "PAY") return false;
        if (Math.Abs(_els[i].SemiMajorAxis - _els[j].SemiMajorAxis) > FormationDeltaAKm) return false;
        var hi = Vec3Cross(st[6 * i], st[6 * i + 1], st[6 * i + 2], st[6 * i + 3], st[6 * i + 4], st[6 * i + 5]);
        var hj = Vec3Cross(st[6 * j], st[6 * j + 1], st[6 * j + 2], st[6 * j + 3], st[6 * j + 4], st[6 * j + 5]);
        double c = (hi.X * hj.X + hi.Y * hj.Y + hi.Z * hj.Z) / (Norm(hi) * Norm(hj));
        return Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI < FormationPlaneDeg;
    }

    /// <summary>Non-collision fragmentations (explosions) per year across LEO at the seeded population;
    /// intact objects (≥50 kg) explode at a per-kg rate calibrated to it. 0 disables. See KesslerEvolution.</summary>
    public double ExplosionsPerYear { get; init; } = 4.0;
    public double ExplosionScale { get; init; } = 0.25;
    private const double IntactMinMassKg = 50.0;
    private double _explPerKgSec = -1;
    public double ExplosionsTotal { get; private set; }
    /// <summary>Active debris removal: large intact objects (≥50 kg) removed per year, highest mass ×
    /// collision rate first (the LEGEND criterion); they leave LEO without fragments. See KesslerEvolution.</summary>
    public double RemovalsPerYear { get; init; } = 0.0;
    public double RemovalsTotal { get; private set; }
    private double _removalAccrual;

    public bool Coarsened { get; private set; }

    /// <summary>Ongoing launch traffic into a band (the Kessler driver), with optional economic throttling.</summary>
    public double LaunchRatePerYear { get; set; } = 0.0;
    public double LaunchAltKm { get; set; } = 900.0;
    public bool ResponsiveLaunch { get; set; } = false;
    public double LossTolerancePerYear { get; set; } = 0.02;
    public double OperationalSatAreaM2 { get; set; } = 5.0;
    public double LastLaunchThrottle { get; private set; } = 1.0;
    private double _launchAccrual;

    private static readonly double[] LcEdges = BreakupModel.SizeBinEdges;
    private readonly Random _rng;

    private readonly List<OrbitalElements> _els = new();
    private readonly List<double> _epoch = new(), _mass = new(), _area = new(), _sqrtA = new(), _w = new();
    private readonly List<bool> _alive = new(), _isNail = new();
    // Where each particle came from (catalog type PAY/R/B/DEB/UNK, BG-SMALL, BG-BELT, LAUNCH, FRAG, NAIL)
    // and, for catalog objects, its name — for diagnostics only; physics never reads them.
    private readonly List<string> _tag = new(), _name = new();
    private double _simSec;

    public ConjunctionCascade(int seed = 1) => _rng = new Random(seed);

    // Debris (background field and breakup fragments) uses breakup-model fragment masses;
    // intact objects carry their own SATCAT-derived mass/area.
    private static double MassFromLc(double lc) => BreakupModel.FragmentMassFromLc(lc);
    private static double AreaFromLc(double lc) => BreakupModel.AreaFromLc(lc);

    private void Add(OrbitalElements el, double mass, double area, double weight, bool nail, string tag = "FRAG", string name = "")
    {
        _tag.Add(tag); _name.Add(name);
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
            Add(el, big ? 2400.0 : 180.0, big ? 18.0 : 1.78, 1.0, nail: false, tag: "PAY");
        }
        SeedBackground(backgroundSmallTotal, backgroundSuperParticles, backgroundLargeTotal, nShell, minAltKm, binKm);
    }

    /// <summary>Seed observed objects with SATCAT-derived masses/areas (per object).</summary>
    /// <param name="backgroundLargeTotal">Modelled large-object belt; negative = automatic (none when
    /// the catalog already holds real derelicts and debris, see <see cref="DebrisEnvironment.LargeBeltFor"/>).</param>
    public void SeedFromCatalog(IReadOnlyList<CatalogObject> objects,
        double backgroundSmallTotal = 1_000_000, int backgroundSuperParticles = 3000, double backgroundLargeTotal = -1,
        int nShell = 36, double minAltKm = 200, double binKm = 50)
    {
        if (backgroundLargeTotal < 0) backgroundLargeTotal = DebrisEnvironment.LargeBeltFor(objects);
        foreach (var o in objects)
        {
            double alt = o.Elements.SemiMajorAxis - Constants.EarthRadiusKm;
            if (alt < minAltKm || alt > minAltKm + nShell * binKm) continue;
            Add(o.Elements, o.MassKg, o.AreaM2, 1.0, nail: false, tag: o.ObjectType, name: o.Name);
        }
        SeedBackground(backgroundSmallTotal, backgroundSuperParticles, backgroundLargeTotal, nShell, minAltKm, binKm);
    }

    private void SeedBackground(double backgroundSmallTotal, int backgroundSuperParticles, double backgroundLargeTotal,
        int nShell, double minAltKm, double binKm)
    {
        double lc0 = Math.Sqrt(LcEdges[0] * LcEdges[1]), lc1 = Math.Sqrt(LcEdges[1] * LcEdges[2]);
        var w = new double[nShell]; double wsum = 0;
        for (int s = 0; s < nShell; s++) { w[s] = DebrisEnvironment.SpatialWeight(minAltKm + (s + 0.5) * binKm); wsum += w[s]; }
        if (wsum <= 0) return;

        for (int s = 0; s < nShell; s++)
        {
            double f = w[s] / wsum;
            // Uniform in altitude across the shell (not all at mid-shell: a thin layer would pack the
            // population into fewer cubes and inflate the geometric collision rate by construction).
            double Alt() => minAltKm + (s + _rng.NextDouble()) * binKm;
            int sp = Math.Max(1, (int)Math.Round(backgroundSuperParticles * f));
            double smallShare = backgroundSmallTotal * f;
            for (int k = 0; k < sp; k++)
            {
                bool coarse = _rng.NextDouble() < 0.3;
                double lc = coarse ? lc1 : lc0;
                Add(Circular(Alt(), (30 + 120 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi),
                    MassFromLc(lc), AreaFromLc(lc), smallShare / sp, nail: false, tag: "BG-SMALL");
            }
            // Large-object belt as near-unit-weight particles (not a few mega-weight ones —
            // those blow up the cube-method variance; see MeasureCubeRate calibration).
            double largeShare = backgroundLargeTotal * f;
            int nPay = (int)Math.Round(0.85 * largeShare), nRb = (int)Math.Round(0.15 * largeShare);
            for (int k = 0; k < nPay; k++)
                Add(Circular(Alt(), (30 + 120 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi), 180.0, 1.78, 1.0, false, tag: "BG-BELT");
            for (int k = 0; k < nRb; k++)
                Add(Circular(Alt(), (30 + 120 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi), 2400.0, 18.0, 1.0, false, tag: "BG-BELT");
        }
    }

    public void InjectBarrel(IReadOnlyList<OrbitalElements> nailCloud, NailSpec nail, int superParticles, int totalNails)
    {
        int step = Math.Max(1, nailCloud.Count / superParticles);
        int used = 0; for (int i = 0; i < nailCloud.Count; i += step) used++;
        double wp = (double)totalNails / used;
        for (int i = 0; i < nailCloud.Count; i += step) Add(nailCloud[i], nail.MassKg, nail.MeanCrossSectionM2, wp, nail: true, tag: "NAIL");
    }

    public double TotalObjects() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i]) t += _w[i]; return t; }
    public double TotalNails() { double t = 0; for (int i = 0; i < _w.Count; i++) if (_alive[i] && _isNail[i]) t += _w[i]; return t; }
    private static readonly double TrackableAreaM2 = BreakupModel.AreaFromLc(0.1);
    /// <summary>Objects ≥10 cm (by cross-section; nails excluded) — the catalogued/catalogable population.</summary>
    public double TotalTrackable() => TotalTrackable(double.NegativeInfinity, double.PositiveInfinity);
    /// <summary>Objects ≥10 cm with mean altitude in [lo, hi) km.</summary>
    public double TotalTrackable(double loKm, double hiKm)
    {
        double t = 0;
        for (int i = 0; i < _w.Count; i++)
        {
            if (!_alive[i] || _isNail[i] || _area[i] < TrackableAreaM2) continue;
            double alt = _els[i].SemiMajorAxis - Constants.EarthRadiusKm;
            if (alt >= loKm && alt < hiKm) t += _w[i];
        }
        return t;
    }
    /// <summary>Altitude band [km] reported as the "belt".</summary>
    public double BeltLoKm { get; init; } = 700;
    public double BeltHiKm { get; init; } = 1100;
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

    /// <summary>A pair's collisions this step: <c>Events</c> of them at relative speed <c>Vrel</c>; P/V is
    /// the parent's state, where fragments start. I == J is a self-pair within one super-particle.</summary>
    private readonly struct Hit(int i, int j, bool cat, double vrel, double events, double px, double py, double pz, double vx, double vy, double vz)
    {
        public readonly int I = i, J = j; public readonly bool Cat = cat; public readonly double Vrel = vrel, Events = events;
        public readonly double Px = px, Py = py, Pz = pz, Vx = vx, Vy = vy, Vz = vz;
    }

    public double Step(double dtSec)
    {
        double L = CubeKm, Lm = L * 1000.0, Vcube = Lm * Lm * Lm;
        double dtSub = dtSec / SubSamples;
        // Hits beyond the memory cap are reservoir-sampled (a uniform random subset, later scaled back up),
        // so the cap never favours whichever cubes the dictionary happens to enumerate first.
        var hits = new List<Hit>(); long seen = 0;
        void Offer(Hit h)
        {
            seen++;
            if (hits.Count < MaxHitsPerStep) hits.Add(h);
            else { long r = _rng.NextInt64(seen); if (r < MaxHitsPerStep) hits[(int)r] = h; }
        }

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
                        if (vrel <= 0 || FormationNeighbours(i, j, st)) continue;
                        double sigma = (_sqrtA[i] + _sqrtA[j]); sigma *= sigma; // m^2
                        double lam = _w[i] * _w[j] * sigma * vrel * dtSub / Vcube;
                        int ev = Poisson(lam); if (ev == 0) continue;
                        double mt = Math.Max(_mass[i], _mass[j]), mp = Math.Min(_mass[i], _mass[j]);
                        bool cat = Lethality.IsCatastrophic(mp, vrel, mt);
                        int par = _mass[i] >= _mass[j] ? i : j;
                        Offer(new Hit(i, j, cat, vrel, ev, st[6 * par], st[6 * par + 1], st[6 * par + 2], st[6 * par + 3], st[6 * par + 4], st[6 * par + 5]));
                    }
            }
        }

        // Collisions among the objects one super-particle stands for (weight > 1): the cube loop only
        // pairs distinct particles, so add these well-mixed over the particle's 50 km shell at the mean
        // LEO encounter speed, as the box and discrete engines do for same-class pairs.
        const double selfVrel = 10_000.0;
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i] || _w[i] <= 1) continue;
            var el = _els[i]; if (el.MeanMotion <= 0) continue;
            double alt = el.SemiMajorAxis - Constants.EarthRadiusKm;
            double rLo = (Constants.EarthRadiusKm + Math.Floor(alt / 50.0) * 50.0) * 1000.0, rHi = rLo + 50_000.0;
            double vShell = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
            double lam = 0.5 * _w[i] * (_w[i] - 1) * 4.0 * _area[i] * selfVrel * dtSec / vShell;   // sigma = (2 sqrt A)^2
            int ev = Poisson(lam); if (ev == 0) continue;
            el.ComputeSecularRates();
            var (pos, vel) = el.StateAt(_simSec - _epoch[i] + _rng.NextDouble() * Constants.TwoPi / el.MeanMotion);
            Offer(new Hit(i, i, Lethality.IsCatastrophic(_mass[i], selfVrel, _mass[i]), selfVrel, ev, pos.X, pos.Y, pos.Z, vel.X, vel.Y, vel.Z));
        }

        double scaleUp = hits.Count > 0 && seen > hits.Count ? (double)seen / hits.Count : 1.0;
        double catastrophic = 0;
        foreach (var h in hits)
        {
            if (_els.Count >= HardCap) break; // memory backstop; Run() coalesces after the step
            if (!_alive[h.I] || !_alive[h.J]) continue;
            double mt = Math.Max(_mass[h.I], _mass[h.J]), mp = Math.Min(_mass[h.I], _mass[h.J]);
            int proj = _mass[h.I] <= _mass[h.J] ? h.I : h.J;
            double events = h.Events * scaleUp;
            // Events can't remove more objects than the particles carry; scaling fragments by the same
            // amount keeps mass conserved. A self-pair removes two objects per event from one particle.
            double frac;
            if (h.Cat)
            {
                frac = h.I == h.J ? Math.Min(events, _w[h.I] / 2.0) : Math.Min(events, Math.Min(_w[h.I], _w[h.J]));
                _w[h.I] -= frac; _w[h.J] -= frac;
                if (_w[h.I] <= 1e-9) _alive[h.I] = false; if (_w[h.J] <= 1e-9) _alive[h.J] = false;
                catastrophic += frac;
            }
            else { frac = Math.Min(events, _w[proj]); _w[proj] -= frac; if (_w[proj] <= 1e-9) _alive[proj] = false; }

            // The breakup spectrum uses this encounter's own relative speed.
            double meff = h.Cat ? (mt + mp) : BreakupModel.EffectiveMass(mt, mp, h.Vrel);
            double availMass = h.Cat ? (mt + mp) : Math.Min(mt, 50.0 * mp);
            var cc = new double[FragMass.Length];
            BreakupModel.DistributeFragments(meff, availMass, LcEdges, FragMass, cc);
            SpawnFragments(new Vec3(h.Px, h.Py, h.Pz), new Vec3(h.Vx, h.Vy, h.Vz), cc, frac);
        }

        ApplyExplosions(dtSec);
        ApplyRemoval(dtSec);
        DragStep(dtSec);
        ApplyLaunch(dtSec);
        _simSec += dtSec;
        return catastrophic;
    }

    /// <summary>Remove this step's share of <see cref="RemovalsPerYear"/>: intact objects ranked by mass ×
    /// their collision rate in their 50 km shell (well-mixed, like the box model), highest first.</summary>
    private void ApplyRemoval(double dtSec)
    {
        if (RemovalsPerYear <= 0) return;
        _removalAccrual += RemovalsPerYear * dtSec / (365.25 * Constants.SecondsPerDay);
        if (_removalAccrual < 1.0) return;
        const int nShell = 36; const double minAlt = 200, bin = 50, reM = 6_378_135.0;
        var W = new double[nShell]; var WA = new double[nShell]; var WsA = new double[nShell];
        int Shell(int i) { double alt = _els[i].SemiMajorAxis - Constants.EarthRadiusKm; int s = (int)Math.Floor((alt - minAlt) / bin); return s >= 0 && s < nShell ? s : -1; }
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i]) continue; int s = Shell(i); if (s < 0) continue;
            W[s] += _w[i]; WA[s] += _w[i] * _area[i]; WsA[s] += _w[i] * _sqrtA[i];
        }
        var cand = new List<(double Score, int I)>();
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i] || _isNail[i] || _mass[i] < IntactMinMassKg) continue;
            int s = Shell(i); if (s < 0) continue;
            double rLo = reM + (minAlt + s * bin) * 1000, rHi = rLo + bin * 1000;
            double V = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
            double rate = (_area[i] * W[s] + WA[s] + 2 * _sqrtA[i] * WsA[s]) / V;   // ∝ collisions/s per object
            cand.Add((_mass[i] * rate, i));
        }
        foreach (var (_, i) in cand.OrderByDescending(c => c.Score))
        {
            if (_removalAccrual <= 1e-9) break;
            double take = Math.Min(_removalAccrual, _w[i]);
            _w[i] -= take; if (_w[i] <= 1e-9) _alive[i] = false;
            _removalAccrual -= take; RemovalsTotal += take;
        }
    }

    private void ApplyLaunch(double dtSec)
    {
        if (LaunchRatePerYear <= 0) return;
        const double secYr = 3.15576e7;

        double throttle = 1.0;
        if (ResponsiveLaunch)
        {
            double lo = LaunchAltKm - 25, hi = LaunchAltKm + 25;
            double rLo = (Constants.EarthRadiusKm + lo) * 1000, rHi = (Constants.EarthRadiusKm + hi) * 1000;
            double Vband = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
            double sqrtSat = Math.Sqrt(OperationalSatAreaM2), loss = 0;
            for (int i = 0; i < _els.Count; i++)
            {
                if (!_alive[i]) continue;
                double alt = _els[i].SemiMajorAxis - Constants.EarthRadiusKm;
                if (alt < lo || alt >= hi) continue;
                double sig = sqrtSat + _sqrtA[i]; sig *= sig;
                loss += _w[i] * sig;
            }
            double lossYr = loss / Vband * 10_000.0 * secYr;
            throttle = Math.Max(0.0, 1.0 - lossYr / LossTolerancePerYear);
        }
        LastLaunchThrottle = throttle;

        _launchAccrual += LaunchRatePerYear * throttle * dtSec / secYr;
        if (_launchAccrual < 1.0) return;
        double batch = _launchAccrual; _launchAccrual = 0;
        Add(Circular(LaunchAltKm, (45 + 45 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi), 180.0, 1.78, 0.85 * batch, false, tag: "LAUNCH");
        Add(Circular(LaunchAltKm, (45 + 45 * _rng.NextDouble()) * Constants.DegToRad, _rng.NextDouble() * Constants.TwoPi, _rng.NextDouble() * Constants.TwoPi), 2400.0, 18.0, 0.15 * batch, false, tag: "LAUNCH");
    }

    private static readonly double[] FragMass = Enumerable.Range(0, LcEdges.Length - 1).Select(c => MassFromLc(Math.Sqrt(LcEdges[c] * LcEdges[c + 1]))).ToArray();
    private static readonly double[] FragArea = Enumerable.Range(0, LcEdges.Length - 1).Select(c => AreaFromLc(Math.Sqrt(LcEdges[c] * LcEdges[c + 1]))).ToArray();

    /// <summary>Explosions this step: Poisson events on intact objects weighted by weight·mass; fragments
    /// start from a random point on the parent's orbit.</summary>
    private void ApplyExplosions(double dtSec)
    {
        if (ExplosionsPerYear <= 0) return;
        var idx = new List<int>(); var cum = new List<double>(); double acc = 0;
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i] || _isNail[i] || _mass[i] < IntactMinMassKg) continue;
            acc += _w[i] * _mass[i]; idx.Add(i); cum.Add(acc);
        }
        if (_explPerKgSec < 0) _explPerKgSec = acc > 0 ? ExplosionsPerYear / (365.25 * Constants.SecondsPerDay) / acc : 0;
        if (_explPerKgSec <= 0 || acc <= 0) return;
        int k = Poisson(_explPerKgSec * acc * dtSec);
        for (int e = 0; e < k && _w.Count < HardCap; e++)
        {
            double x = _rng.NextDouble() * acc;
            int lo = 0, hi = cum.Count - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (cum[mid] < x) lo = mid + 1; else hi = mid; }
            int p = idx[lo];
            if (!_alive[p]) continue;
            var el = _els[p]; if (el.MeanMotion <= 0) continue;
            el.ComputeSecularRates();
            var (pos, vel) = el.StateAt(_rng.NextDouble() * Constants.TwoPi / el.MeanMotion);
            double frac = Math.Min(1.0, _w[p]);
            var cnt = new double[FragMass.Length];
            BreakupModel.DistributeExplosionFragments(_mass[p], LcEdges, FragMass, cnt, ExplosionScale);
            SpawnFragments(pos, vel, cnt, frac);
            _w[p] -= frac; if (_w[p] <= 1e-9) _alive[p] = false;
            ExplosionsTotal += frac;
        }
    }

    private void SpawnFragments(Vec3 pos, Vec3 vel, double[] cnt, double eventFraction)
    {
        int nc = cnt.Length;
        for (int c = 0; c < nc && _w.Count < HardCap; c++)
        {
            double weight = cnt[c] * eventFraction; if (weight < 1e-6) continue;
            var dv = new Vec3(FragmentDeltaVKmS * NextGaussian(), FragmentDeltaVKmS * NextGaussian(), FragmentDeltaVKmS * NextGaussian());
            var frag = OrbitalElements.FromStateVector(pos, vel + dv);
            if (frag.Eccentricity >= 1 || frag.SemiMajorAxis <= Constants.EarthRadiusKm) continue;
            Add(frag, FragMass[c], FragArea[c], weight, nail: false);
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
        var repTag = new Dictionary<(int, int, bool), (string Tag, string Name)>();
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i]) continue;
            int skey = (int)((_els[i].SemiMajorAxis - Constants.EarthRadiusKm) / 50.0);
            int mkey = (int)Math.Round(Math.Log(_mass[i]) * 4);
            var key = (skey, mkey, _isNail[i]);
            if (!acc.TryGetValue(key, out var v)) { v = new double[3]; v[1] = _mass[i]; v[2] = _area[i]; acc[key] = v; rep[key] = _els[i]; repTag[key] = (_tag[i], _name[i]); }
            v[0] += _w[i];
        }
        _els.Clear(); _epoch.Clear(); _mass.Clear(); _area.Clear(); _sqrtA.Clear(); _w.Clear(); _alive.Clear(); _isNail.Clear();
        _tag.Clear(); _name.Clear();
        foreach (var (key, v) in acc)
        {
            _els.Add(rep[key]); _epoch.Add(_simSec); _mass.Add(v[1]); _area.Add(v[2]);
            _sqrtA.Add(Math.Sqrt(v[2])); _w.Add(v[0]); _alive.Add(true); _isNail.Add(key.Item3);
            _tag.Add(repTag[key].Tag); _name.Add(repTag[key].Name);
        }
        Coarsened = true;
    }

    private void Compact()
    {
        int w = 0;
        for (int r = 0; r < _els.Count; r++)
        {
            if (!_alive[r]) continue;
            if (w != r) { _els[w] = _els[r]; _epoch[w] = _epoch[r]; _mass[w] = _mass[r]; _area[w] = _area[r]; _sqrtA[w] = _sqrtA[r]; _w[w] = _w[r]; _alive[w] = true; _isNail[w] = _isNail[r]; _tag[w] = _tag[r]; _name[w] = _name[r]; }
            w++;
        }
        int rem = _els.Count - w;
        if (rem > 0)
        {
            _els.RemoveRange(w, rem); _epoch.RemoveRange(w, rem); _mass.RemoveRange(w, rem); _area.RemoveRange(w, rem);
            _sqrtA.RemoveRange(w, rem); _w.RemoveRange(w, rem); _alive.RemoveRange(w, rem); _isNail.RemoveRange(w, rem);
            _tag.RemoveRange(w, rem); _name.RemoveRange(w, rem);
        }
    }

    /// <summary>
    /// Calibration probe: expected collisions in <paramref name="dtSec"/> and the Σλ-weighted mean
    /// encounter speed, from the cube method on the frozen current population (no removal/spawn).
    /// </summary>
    public (double ExpectedCollisions, double MeanVrelMS, double ExpectedCatastrophic) MeasureCubeRate(double dtSec)
    {
        double L = CubeKm, Lm = L * 1000.0, Vcube = Lm * Lm * Lm, dtSub = dtSec / SubSamples;
        double sumLam = 0, sumLamV = 0, sumCat = 0;
        for (int sub = 0; sub < SubSamples; sub++)
        {
            double[] st = PropagateState(_simSec + _rng.NextDouble() * 5400.0);
            var cubes = new Dictionary<long, List<int>>();
            for (int i = 0; i < _els.Count; i++)
            {
                if (!_alive[i]) continue;
                long ix = (long)Math.Floor(st[6 * i] / L) + 1024, iy = (long)Math.Floor(st[6 * i + 1] / L) + 1024, iz = (long)Math.Floor(st[6 * i + 2] / L) + 1024;
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
                        double vrel = Math.Sqrt(dvx * dvx + dvy * dvy + dvz * dvz) * 1000.0;
                        if (vrel <= 0 || FormationNeighbours(i, j, st)) continue;
                        double sig = _sqrtA[i] + _sqrtA[j]; sig *= sig;
                        double lam = _w[i] * _w[j] * sig * vrel * dtSub / Vcube;
                        sumLam += lam; sumLamV += lam * vrel;
                        if (Lethality.IsCatastrophic(Math.Min(_mass[i], _mass[j]), vrel, Math.Max(_mass[i], _mass[j]))) sumCat += lam;
                    }
            }
        }
        return (sumLam, sumLam > 0 ? sumLamV / sumLam : 0, sumCat);
    }

    /// <summary>
    /// Calibration probe: the cube method's expected collisions (all, and catastrophic) in
    /// <paramref name="dtSec"/>, split by the encounter's relative speed into bins with the given edges
    /// [km/s]. Frozen population: nothing is removed or spawned.
    /// </summary>
    public (double[] All, double[] Catastrophic) MeasureCubeRateBySpeed(double dtSec, double[] edgesKmS)
    {
        double L = CubeKm, Lm = L * 1000.0, Vcube = Lm * Lm * Lm, dtSub = dtSec / SubSamples;
        var all = new double[edgesKmS.Length - 1]; var cat = new double[edgesKmS.Length - 1];
        for (int sub = 0; sub < SubSamples; sub++)
        {
            double[] st = PropagateState(_simSec + _rng.NextDouble() * 5400.0);
            var cubes = new Dictionary<long, List<int>>();
            for (int i = 0; i < _els.Count; i++)
            {
                if (!_alive[i]) continue;
                long ix = (long)Math.Floor(st[6 * i] / L) + 1024, iy = (long)Math.Floor(st[6 * i + 1] / L) + 1024, iz = (long)Math.Floor(st[6 * i + 2] / L) + 1024;
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
                        double v = Math.Sqrt(dvx * dvx + dvy * dvy + dvz * dvz);   // km/s
                        if (FormationNeighbours(i, j, st)) continue;
                        int b = Array.FindLastIndex(edgesKmS, e => e <= v);
                        if (b < 0 || b >= all.Length) continue;
                        double sig = _sqrtA[i] + _sqrtA[j]; sig *= sig;
                        double lam = _w[i] * _w[j] * sig * v * 1000.0 * dtSub / Vcube;
                        all[b] += lam;
                        if (Lethality.IsCatastrophic(Math.Min(_mass[i], _mass[j]), v * 1000.0, Math.Max(_mass[i], _mass[j]))) cat[b] += lam;
                    }
            }
        }
        return (all, cat);
    }

    /// <summary>
    /// Diagnostic for the cube method's slow encounters: expected collisions in <paramref name="dtSec"/>
    /// among pairs slower than <paramref name="maxVrelKmS"/>, broken down by source-tag pair, by name
    /// family pair (first word of the catalog name, e.g. STARLINK), by the angle between the two orbital
    /// planes, and by the difference in semi-major axis. Frozen population. Unfiltered: formation
    /// neighbours are included, so this shows what <see cref="ExcludeFormationPairs"/> removes.
    /// </summary>
    public (double Total, Dictionary<string, double> ByType, Dictionary<string, double> ByFamily,
            double[] PlaneAngleHist, double[] DeltaAHist) MeasureSlowPairs(double dtSec, double maxVrelKmS,
            double[] planeAngleEdgesDeg, double[] deltaAEdgesKm)
    {
        double L = CubeKm, Lm = L * 1000.0, Vcube = Lm * Lm * Lm, dtSub = dtSec / SubSamples;
        var byType = new Dictionary<string, double>(); var byFam = new Dictionary<string, double>();
        var ang = new double[planeAngleEdgesDeg.Length - 1]; var dA = new double[deltaAEdgesKm.Length - 1];
        double total = 0;
        static string Family(string tag, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return tag;
            var first = name.Trim().ToUpperInvariant().Split(new[] { ' ', '-', '(', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string f = first.Length > 0 ? first[0] : tag;
            return tag == "DEB" ? f + " DEB" : f;
        }
        static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a} + {b}" : $"{b} + {a}";
        static int Bin(double[] edges, double x) { int b = Array.FindLastIndex(edges, e => e <= x); return b >= edges.Length - 1 ? edges.Length - 2 : b; }

        for (int sub = 0; sub < SubSamples; sub++)
        {
            double[] st = PropagateState(_simSec + _rng.NextDouble() * 5400.0);
            var cubes = new Dictionary<long, List<int>>();
            for (int i = 0; i < _els.Count; i++)
            {
                if (!_alive[i]) continue;
                long ix = (long)Math.Floor(st[6 * i] / L) + 1024, iy = (long)Math.Floor(st[6 * i + 1] / L) + 1024, iz = (long)Math.Floor(st[6 * i + 2] / L) + 1024;
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
                        double v = Math.Sqrt(dvx * dvx + dvy * dvy + dvz * dvz);   // km/s
                        if (v <= 0 || v >= maxVrelKmS) continue;
                        double sig = _sqrtA[i] + _sqrtA[j]; sig *= sig;
                        double lam = _w[i] * _w[j] * sig * v * 1000.0 * dtSub / Vcube;
                        total += lam;
                        string tk = PairKey(_tag[i], _tag[j]), fk = PairKey(Family(_tag[i], _name[i]), Family(_tag[j], _name[j]));
                        byType[tk] = byType.GetValueOrDefault(tk) + lam;
                        byFam[fk] = byFam.GetValueOrDefault(fk) + lam;
                        // angle between the orbit normals h = r × v, from the sampled states
                        Vec3 H(int k) => Vec3Cross(st[6 * k], st[6 * k + 1], st[6 * k + 2], st[6 * k + 3], st[6 * k + 4], st[6 * k + 5]);
                        var hi = H(i); var hj = H(j);
                        double c = (hi.X * hj.X + hi.Y * hj.Y + hi.Z * hj.Z) / (Norm(hi) * Norm(hj));
                        ang[Bin(planeAngleEdgesDeg, Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI)] += lam;
                        dA[Bin(deltaAEdgesKm, Math.Abs(_els[i].SemiMajorAxis - _els[j].SemiMajorAxis))] += lam;
                    }
            }
        }
        return (total, byType, byFam, ang, dA);
    }

    private static Vec3 Vec3Cross(double rx, double ry, double rz, double vx, double vy, double vz)
        => new(ry * vz - rz * vy, rz * vx - rx * vz, rx * vy - ry * vx);
    private static double Norm(Vec3 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);

    /// <summary>
    /// Control for calibration: give every object a random orbital plane orientation (node) and a
    /// random position along its orbit, keeping altitude, eccentricity and inclination. This breaks up
    /// families that share planes (constellations, sun-synchronous orbits) but keeps the latitude
    /// structure that inclinations create.
    /// </summary>
    public void ScramblePlanes()
    {
        for (int i = 0; i < _els.Count; i++)
        {
            var el = _els[i];
            el.Raan = _rng.NextDouble() * Constants.TwoPi;
            el.MeanAnomaly = _rng.NextDouble() * Constants.TwoPi;
            _els[i] = el;
        }
    }

    /// <summary>Well-mixed (kinetic) expected collisions for the frozen population, mean-altitude shells.</summary>
    public double MeasureKineticRate(double dtSec, double vrelMS, int nShell = 36, double minAlt = 200, double binKm = 50)
    {
        const double reM = 6_378_135.0;
        var sumW = new double[nShell]; var sumWA = new double[nShell]; var sumWsqrtA = new double[nShell]; var V = new double[nShell];
        for (int s = 0; s < nShell; s++)
        {
            double rLo = reM + (minAlt + s * binKm) * 1000, rHi = reM + (minAlt + (s + 1) * binKm) * 1000;
            V[s] = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
        }
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i]) continue;
            double alt = _els[i].SemiMajorAxis - Constants.EarthRadiusKm;
            int s = (int)((alt - minAlt) / binKm);
            if (s < 0 || s >= nShell) continue;
            sumW[s] += _w[i]; sumWA[s] += _w[i] * _area[i]; sumWsqrtA[s] += _w[i] * _sqrtA[i];
        }
        double total = 0;
        for (int s = 0; s < nShell; s++)
            if (V[s] > 0) total += vrelMS / V[s] * (sumW[s] * sumWA[s] + sumWsqrtA[s] * sumWsqrtA[s]) * dtSec;
        return total;
    }

    /// <summary>
    /// Well-mixed expected <i>catastrophic</i> collisions for the frozen population at one encounter
    /// speed — what the box model counts (it uses 10 km/s for every pair). Particles are grouped by
    /// (shell, mass, area) so the pair sum stays small.
    /// </summary>
    public double MeasureKineticCatastrophic(double dtSec, double vrelMS, int nShell = 36, double minAlt = 200, double binKm = 50)
    {
        const double reM = 6_378_135.0;
        var groups = new Dictionary<(int S, double M, double A), double>();
        for (int i = 0; i < _els.Count; i++)
        {
            if (!_alive[i]) continue;
            int s = (int)((_els[i].SemiMajorAxis - Constants.EarthRadiusKm - minAlt) / binKm);
            if (s < 0 || s >= nShell) continue;
            var key = (s, _mass[i], _area[i]);
            groups[key] = groups.GetValueOrDefault(key) + _w[i];
        }
        double total = 0;
        foreach (var shell in groups.GroupBy(kv => kv.Key.S))
        {
            double rLo = reM + (minAlt + shell.Key * binKm) * 1000, rHi = rLo + binKm * 1000;
            double V = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
            var g = shell.ToArray();
            for (int a = 0; a < g.Length; a++)
                for (int b = a; b < g.Length; b++)
                {
                    double mt = Math.Max(g[a].Key.M, g[b].Key.M), mp = Math.Min(g[a].Key.M, g[b].Key.M);
                    if (!Lethality.IsCatastrophic(mp, vrelMS, mt)) continue;
                    double sig = Math.Sqrt(g[a].Key.A) + Math.Sqrt(g[b].Key.A); sig *= sig;
                    double pairs = a == b ? 0.5 * g[a].Value * g[a].Value : g[a].Value * g[b].Value;
                    total += pairs * sig * vrelMS / V * dtSec;
                }
        }
        return total;
    }

    public CascadeResult Run(double horizonYears = 50, double dtDays = 60)
    {
        var yr = new List<double> { 0 }; var tot = new List<double> { TotalObjects() }; var trk = new List<double> { TotalTrackable() }; var belt = new List<double> { TotalTrackable(BeltLoKm, BeltHiKm) };
        var cs = new List<double> { TotalCrossSection() }; var cpy = new List<double> { 0 }; var nl = new List<double> { TotalNails() };
        double catAccum = 0, t = 0, nextYear = 0, doneDays = 0, totalDays = horizonYears * 365.25;
        // Last step is shortened so the run ends exactly on the horizon (and records its final year).
        while (doneDays < totalDays - 1e-9)
        {
            double d = Math.Min(dtDays, totalDays - doneDays);
            catAccum += Step(d * Constants.SecondsPerDay);
            Compact();
            if (_els.Count > CoarsenAbove) Coalesce();
            doneDays += d; t = doneDays / 365.25;
            if (t >= nextYear + 1 - 1e-9)
            {
                nextYear += 1;
                yr.Add(t); tot.Add(TotalObjects()); trk.Add(TotalTrackable()); belt.Add(TotalTrackable(BeltLoKm, BeltHiKm)); cs.Add(TotalCrossSection()); cpy.Add(catAccum); nl.Add(TotalNails());
                catAccum = 0;
            }
        }
        return new CascadeResult { Years = yr.ToArray(), TotalObjects = tot.ToArray(), TotalCrossSection = cs.ToArray(), CatastrophicPerYear = cpy.ToArray(), SurvivingNails = nl.ToArray(), TrackableObjects = trk.ToArray(), BeltTrackableObjects = belt.ToArray() };
    }
}
