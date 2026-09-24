using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceWars.Core;

/// <summary>Representative object class: a size bin with derived mass and cross-section.</summary>
public sealed class DebrisClass
{
    public double LcLoM, LcHiM, LcM;   // characteristic-length bin edges & representative [m]
    public double MassKg, AreaM2;
    public bool IsNail;
    public bool IsWorking;   // a working satellite: holds altitude, dodges tracked objects, retires
    public bool IsIntactMass; // an intact-object mass class (satellites, rocket bodies)
    public double AreaToMass => AreaM2 / MassKg;
}

/// <summary>Yearly snapshots from an evolution run.</summary>
public sealed class EvolutionResult
{
    public required double[] Years;
    public required double[] TotalObjects;         // Σ debris+intact population (size classes) across LEO
    public required double[] CatastrophicPerYear;  // catastrophic collisions in that year
    public required double[] SurvivingNails;       // nails still on orbit
    public double[] LaunchFraction = [];           // fraction of nominal launch actually flown (responsive mode)
    /// <summary>Objects ≥10 cm (the catalogued/catalogable population — the standard Kessler metric).
    /// <see cref="TotalObjects"/> is dominated by the modelled 1–10 cm field.</summary>
    public double[] TrackableObjects = [];
    /// <summary>Objects ≥10 cm in the belt (default 700–1100 km) — where a cascade can persist.</summary>
    public double[] BeltTrackableObjects = [];
    /// <summary>Working satellites across LEO (<see cref="KesslerEvolution.WorkingSatellites"/> runs only). They are
    /// not debris, so the other series leave them out.</summary>
    public double[] WorkingSatellites = [];
}

/// <summary>
/// Kessler source–sink evolution as a box model over altitude shells × object classes:
///
///   dN_i/dt = S_i(launch) − N_i/τ_i(drag) + ½ Σ_{j,k} P_i(j,k) R_jk
///
/// R_jk = n_j n_k ⟨σ v⟩ V is the pairwise collision rate in a well-mixed shell; each
/// collision is catastrophic or cratering (EMR vs 40 J/g), and the NASA breakup model
/// supplies P — the fragments dropped into each size class, mass-conserved. Fragments are
/// new colliders (the N² feedback). Drag migrates objects down one shell at a time.
///
/// Size↔mass↔area use the NASA breakup-model relations so the whole model is self-consistent.
/// </summary>
public sealed class KesslerEvolution
{
    public double RelVelMetersPerSec { get; init; } = 10_000.0;
    public double SolarActivity { get; init; } = 1.0;
    /// <summary>
    /// 11-year solar cycle: activity = SolarActivity · e^(A·sin(2πt/11 yr)), so A = ln 2 swings it between half and
    /// double. 0 (default) keeps it constant. Solar maximum heats the upper atmosphere and clears debris faster.
    /// </summary>
    public double SolarCycleAmplitude { get; init; } = 0.0;
    private double _simSec;

    /// <summary>
    /// LEGEND's convention: only objects ≥10 cm take part in collisions (LEGEND simulates nothing smaller). Off by
    /// default — the modelled 1–10 cm field and nails collide too. Used for like-for-like NASA benchmarks.
    /// </summary>
    public bool TrackedOnlyCollisions { get; init; } = false;
    /// <summary>Catastrophic collisions so far, by shell (cumulative), and non-catastrophic ones between objects ≥10 cm.</summary>
    public double[] CatastrophicByShell => (double[])_catByShell.Clone();
    public double NonCatastrophicTrackedTotal { get; private set; }
    private double[] _catByShell = [];

    /// <summary>Ongoing launch traffic: intact objects added per year (the Kessler driver).</summary>
    public double LaunchRatePerYear { get; set; } = 0.0;
    /// <summary>Altitude band the launch traffic populates [km].</summary>
    public double LaunchAltKm { get; set; } = 900.0;

    /// <summary>
    /// When true, launch traffic is economically rational: operators throttle back as the
    /// band's collision hazard to an operational satellite rises, and stop entirely once the
    /// annual loss rate reaches <see cref="LossTolerancePerYear"/>. This makes the environment
    /// self-limiting — a degrading band gets abandoned rather than replenished into an
    /// unphysical unbounded runaway.
    /// </summary>
    public bool ResponsiveLaunch { get; set; } = false;
    public double LossTolerancePerYear { get; set; } = 0.02; // max attrition operators tolerate
    public double OperationalSatAreaM2 { get; set; } = 5.0;
    private double _lastThrottle = 1.0;
    public double LastLaunchThrottle => _lastThrottle;

    /// <summary>
    /// Breakup Δv scatters fragments across neighbouring shells rather than leaving them at
    /// the collision altitude. Expressed as a Gaussian spread width in shells (≈ Δv·2/v_orb·a);
    /// the default ~4 shells ≈ ±200 km ≈ ~150 m/s. This is what couples the shells vertically so
    /// a collision at 900 km seeds 700 and 1100 km — the mechanism a cascade climbs and sinks by.
    /// </summary>
    public double FragmentSpreadShells { get; init; } = 4.0;

    /// <summary>
    /// Smallest projectile (characteristic length [m]) whose <i>non-catastrophic</i> cratering
    /// impacts produce ejecta fragments. 0 = every projectile (the breakup model applied as
    /// written); 0.1 = only ≥10 cm projectiles, the LEGEND convention. Cratering by small debris
    /// multiplies the small population fast, so this is the model's main sensitivity knob.
    /// </summary>
    public double CrateringEjectaMinLcM { get; init; } = 0.0;

    /// <summary>
    /// Non-collision fragmentations per year across LEO — explosions of rocket bodies and derelicts
    /// (residual propellant, batteries) — at the seeded population. Each intact object's chance
    /// scales with its mass, so the rate then follows the intact population as launches add to it
    /// or drag removes it. ~4/yr is roughly the long-run historical average of fragmentation events;
    /// 0 disables. This is the small-debris source that collisions alone don't supply.
    /// </summary>
    public double ExplosionsPerYear { get; init; } = 4.0;
    /// <summary>
    /// Breakup-model explosion scale factor S: N(&gt;Lc) = 6·S·Lc^-1.6. S = 1 is a large rocket-stage
    /// explosion (~240 fragments ≥10 cm). The default 0.25 (~60 fragments ≥10 cm) represents the
    /// average event, most of which are smaller (batteries, minor breakups), and keeps four events a
    /// year to a few hundred new catalogued objects a year, the right order for observed growth.
    /// </summary>
    public double ExplosionScale { get; init; } = 0.25;
    private double _explPerKgSec = -1;   // per-kg explosion rate, calibrated on the first step

    /// <summary>
    /// Active debris removal: large intact objects taken out of orbit per year, from the start of the
    /// run. Each removal takes the object that matters most — the highest mass × collision rate, the
    /// selection criterion of NASA's LEGEND removal studies — so heavy derelicts in the dense belt go
    /// first. Removed objects leave LEO without fragments (and no longer explode). 0 = none.
    /// </summary>
    public double RemovalsPerYear { get; init; } = 0.0;
    /// <summary>Objects removed so far in this model's run.</summary>
    public double RemovalsTotal { get; private set; }

    /// <summary>
    /// Working satellites. Off by default, which keeps every derelict-only run unchanged: there, launched
    /// and catalogued satellites are passive from day one. When on, catalogued satellites on the active
    /// list and new launches enter a separate working class that
    /// <list type="bullet">
    /// <item>holds its altitude (no drag) for about <see cref="SatelliteLifetimeYears"/>, then is deorbited
    /// with probability <see cref="DisposalSuccess"/> or left dead in place;</item>
    /// <item>dodges tracked (≥10 cm) objects — its collision rate with them is cut by
    /// <see cref="ManoeuvrableFraction"/> × <see cref="AvoidanceSuccess"/> — but can't dodge untracked
    /// 1–10 cm debris or nails;</item>
    /// <item>is left dead by any non-catastrophic hit (a mission kill), since then it can't deorbit.</item>
    /// </list>
    /// Launched rocket bodies are disposed of with probability <see cref="RocketBodyDisposal"/>.
    /// Defaults are the evidence-based baseline: 90% disposal (the NASA/FCC/IADC benchmark), 80% for rocket
    /// bodies (ESA 2025, observed), 90% avoidance, 89% of satellites manoeuvrable (McDowell, Sep 2026),
    /// 5-year life (FCC 5-year rule, Starlink practice).
    /// </summary>
    public bool WorkingSatellites { get; init; } = false;
    public double SatelliteLifetimeYears { get; init; } = 5.0;
    public double DisposalSuccess { get; init; } = 0.90;
    public double RocketBodyDisposal { get; init; } = 0.80;
    public double AvoidanceSuccess { get; init; } = 0.90;
    public double ManoeuvrableFraction { get; init; } = 0.89;
    /// <summary>Working satellites deorbited at end of life, and those left dead because disposal failed.</summary>
    public double DisposedTotal { get; private set; }
    public double FailedDisposalTotal { get; private set; }
    /// <summary>Working satellites left dead by a non-catastrophic hit.</summary>
    public double MissionKillsTotal { get; private set; }
    /// <summary>Collisions of working satellites with tracked objects that avoidance prevented.</summary>
    public double AvoidedTotal { get; private set; }

    /// <summary>
    /// Intact objects (satellites, rocket bodies) sit in seven mass classes spaced ×2.37 apart, 32 kg – 5.7 t, instead of
    /// two (the 179 kg and 2.4 t size bins). The grid includes 180 kg and 2,410 kg exactly, so added traffic keeps
    /// its masses; catalogued objects land within ×1.54 of their own mass. Fragment yield grows as mass^0.75, so
    /// the two-class mapping overstated breakups of heavy objects (a 1.55 t rocket body modelled as 2.4 t).
    /// </summary>
    public bool FineIntactMasses { get; init; } = true;
    public static readonly double[] IntactMassGridKg = { 32.0, 76.0, 180.0, 427.0, 1015.0, 2410.0, 5722.0 };

    /// <summary>
    /// Eccentric orbits (e above <see cref="EccentricMinE"/>) are followed object by object instead of being
    /// placed at their mean altitude. Each spends the Kepler time fraction of its orbit in every shell it crosses
    /// (NASA's "effective number"), collides there at that fraction, and decays by orbit-averaged drag, which acts
    /// mostly near perigee: the apogee comes down while the perigee holds, until the orbit is circular. This brings
    /// in objects whose mean altitude is above 2,000 km but whose perigee is in LEO.
    /// </summary>
    public bool EccentricOrbits { get; init; } = true;
    public double EccentricMinE { get; init; } = 0.02;

    /// <summary>Altitude band [km] reported as the "belt" (<see cref="EvolutionResult.BeltTrackableObjects"/>).</summary>
    public double BeltLoKm { get; init; } = 700;
    public double BeltHiKm { get; init; } = 1100;

    private double[]? _spreadKernel;
    private int _spreadSpan;

    private static readonly double[] LcEdges = BreakupModel.SizeBinEdges;
    public int SizeClassCount => LcEdges.Length - 1;   // 6
    /// <summary>
    /// Classes below this index are debris (1 cm – 1 m) and carry breakup-model fragment masses;
    /// classes from it up are intact payloads / rocket bodies and carry intact bulk-density masses.
    /// Launch traffic and the large-object belt go into the intact classes.
    /// </summary>
    public const int IntactClassStart = 4;
    public int NailClass => SizeClassCount;            // appended class index
    /// <summary>Working satellites (<see cref="WorkingSatellites"/>): same size and mass as the ~180 kg intact class.</summary>
    public int WorkingClass => SizeClassCount + 1;
    /// <summary>First of the intact-mass classes (<see cref="IntactMassGridKg"/>).</summary>
    public int IntactGridStart => SizeClassCount + 2;
    private readonly int _nc;                          // size classes, nails, working satellites, intact masses

    private readonly int _nShell;
    private readonly double _minAlt, _binKm;
    private readonly double[] _midAlt, _volM3;
    private readonly DebrisClass[] _cls;
    private readonly double[,] _n;                     // [shell, class]

    public KesslerEvolution(NailSpec nail, int shellCount = 36, double minAltKm = 200, double binKm = 50)
    {
        _nShell = shellCount; _minAlt = minAltKm; _binKm = binKm;
        _midAlt = new double[shellCount]; _volM3 = new double[shellCount];
        const double reM = 6_378_135.0;
        for (int s = 0; s < shellCount; s++)
        {
            double lo = minAltKm + s * binKm, hi = lo + binKm;
            _midAlt[s] = 0.5 * (lo + hi);
            double rLo = reM + lo * 1000, rHi = reM + hi * 1000;
            _volM3[s] = 4.0 / 3.0 * Math.PI * (rHi * rHi * rHi - rLo * rLo * rLo);
        }

        _nc = SizeClassCount + 2 + IntactMassGridKg.Length;
        _cls = new DebrisClass[_nc];
        for (int c = 0; c < SizeClassCount; c++)
        {
            double lo = LcEdges[c], hi = LcEdges[c + 1], lc = Math.Sqrt(lo * hi);
            double mass = c < IntactClassStart ? BreakupModel.FragmentMassFromLc(lc) : BreakupModel.IntactMassFromLc(lc);
            _cls[c] = new DebrisClass { LcLoM = lo, LcHiM = hi, LcM = lc, MassKg = mass, AreaM2 = BreakupModel.AreaFromLc(lc) };
        }
        _cls[NailClass] = new DebrisClass
        {
            LcLoM = 0, LcHiM = 0, LcM = nail.CharacteristicLengthM,
            MassKg = nail.MassKg, AreaM2 = nail.MeanCrossSectionM2, IsNail = true,
        };
        var sat = _cls[IntactClassStart];
        _cls[WorkingClass] = new DebrisClass
        {
            LcLoM = sat.LcLoM, LcHiM = sat.LcHiM, LcM = sat.LcM, MassKg = sat.MassKg, AreaM2 = sat.AreaM2, IsWorking = true,
        };
        for (int g = 0; g < IntactMassGridKg.Length; g++)
        {
            double lc = LcForIntactMass(IntactMassGridKg[g]);
            _cls[IntactGridStart + g] = new DebrisClass
            {
                LcLoM = lc, LcHiM = lc, LcM = lc, MassKg = IntactMassGridKg[g], AreaM2 = BreakupModel.AreaFromLc(lc), IsIntactMass = true,
            };
        }
        _n = new double[_nShell, _nc];
        _catByShell = new double[_nShell];
    }

    /// <summary>Characteristic length [m] of an intact object of the given mass (inverse of the bulk-density law).</summary>
    private static double LcForIntactMass(double kg)
    {
        double lo = 0.01, hi = 50;
        for (int it = 0; it < 80; it++) { double mid = Math.Sqrt(lo * hi); if (BreakupModel.IntactMassFromLc(mid) < kg) lo = mid; else hi = mid; }
        return Math.Sqrt(lo * hi);
    }

    private int GridClassFor(double kg)
    {
        int best = 0; double bestD = double.MaxValue, lm = Math.Log(Math.Max(kg, 1e-6));
        for (int g = 0; g < IntactMassGridKg.Length; g++)
        {
            double d = Math.Abs(Math.Log(IntactMassGridKg[g]) - lm);
            if (d < bestD) { bestD = d; best = g; }
        }
        return IntactGridStart + best;
    }

    /// <summary>Class for a ~180 kg satellite and a ~2.4 t rocket body (launch traffic, the modelled large belt, dead satellites).</summary>
    private int SatClass => FineIntactMasses ? GridClassFor(180) : IntactClassStart;
    private int RocketBodyClass => FineIntactMasses ? GridClassFor(2410) : IntactClassStart + 1;

    /// <summary>Intact classes that can explode or be removed: the two large size bins and the intact-mass grid.</summary>
    private IEnumerable<int> IntactPool()
    {
        for (int c = IntactClassStart; c < SizeClassCount; c++) yield return c;
        for (int c = IntactGridStart; c < _nc; c++) yield return c;
    }

    public IReadOnlyList<DebrisClass> Classes => _cls;

    private int ShellOf(double altKm)
    {
        if (altKm < _minAlt) return -1;
        int s = (int)((altKm - _minAlt) / _binKm);
        return s < _nShell ? s : -1;
    }

    /// <summary>
    /// Seed the initial environment from the real catalog (trackable intacts placed by
    /// altitude) plus a modelled small-debris background (1–10 cm) distributed by the same
    /// altitude profile. Masses are approximated pending SATCAT/RCS.
    /// </summary>
    public void SeedFromCatalog(IReadOnlyList<OrbitalElements> catalog,
        double largeIntactFraction = 0.15, double backgroundSmallTotal = 1_000_000,
        double backgroundLargeTotal = 8_000)
    {
        // Observed payloads (no SATCAT): split by a fixed large-intact fraction.
        foreach (var el in catalog)
        {
            int s = ShellOf(el.SemiMajorAxis - Constants.EarthRadiusKm);
            if (s < 0) continue;
            _n[s, RocketBodyClass] += largeIntactFraction;
            _n[s, SatClass] += 1.0 - largeIntactFraction;
        }
        SeedBackground(backgroundSmallTotal, backgroundLargeTotal);
    }

    /// <summary>Seed observed objects with SATCAT-derived masses, mapped to the nearest size class.</summary>
    /// <param name="backgroundLargeTotal">Modelled large-object belt; negative = automatic (none when
    /// the catalog already holds real derelicts and debris, see <see cref="DebrisEnvironment.LargeBeltFor"/>).</param>
    public void SeedFromCatalog(IReadOnlyList<CatalogObject> objects,
        double backgroundSmallTotal = 1_000_000, double backgroundLargeTotal = -1)
    {
        if (backgroundLargeTotal < 0) backgroundLargeTotal = DebrisEnvironment.LargeBeltFor(objects);
        // Intact-mass classes take the mean mass and area of the catalogued objects filed in them (the catalog's
        // own RCS-derived values), so a class stands for its objects rather than for a grid point.
        var sumM = new double[_nc]; var sumA = new double[_nc]; var cnt = new int[_nc];
        foreach (var o in objects)
        {
            int c = WorkingSatellites && o.IsActive ? WorkingClass
                  : FineIntactMasses && o.IsIntact ? GridClassFor(o.MassKg) : NearestSizeClass(o.MassKg);
            if (_cls[c].IsIntactMass) { sumM[c] += o.MassKg; sumA[c] += o.AreaM2; cnt[c]++; }
            if (EccentricOrbits && c != WorkingClass && o.Elements.Eccentricity > EccentricMinE)
            {
                AddEccentric(o.Elements.SemiMajorAxis, o.Elements.SemiMajorAxis * (1 - o.Elements.Eccentricity), c, 1.0);
                continue;
            }
            int s = ShellOf(o.Elements.SemiMajorAxis - Constants.EarthRadiusKm);
            if (s < 0) continue;
            _n[s, c] += 1.0;
        }
        for (int c = 0; c < _nc; c++)
        {
            if (cnt[c] == 0) continue;
            double area = sumA[c] / cnt[c], lc = BreakupModel.LcFromArea(area);
            _cls[c].MassKg = sumM[c] / cnt[c]; _cls[c].AreaM2 = area; _cls[c].LcM = _cls[c].LcLoM = _cls[c].LcHiM = lc;
        }
        foreach (var o in _ecc) if (_cls[o.C].IsIntactMass) UpdateResidence(o);
        SeedBackground(backgroundSmallTotal, backgroundLargeTotal);
    }

    private int NearestSizeClass(double massKg)
    {
        int best = 0; double bestD = double.MaxValue;
        double lm = Math.Log(Math.Max(massKg, 1e-6));
        for (int c = 0; c < SizeClassCount; c++)
        {
            double d = Math.Abs(Math.Log(_cls[c].MassKg) - lm);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private void SeedBackground(double backgroundSmallTotal, double backgroundLargeTotal)
    {
        // Modelled populations placed by the realistic debris altitude profile: the 1–10 cm
        // lethal-untracked field and the large-object belt that the trackable catalog omits.
        var w = new double[_nShell]; double wsum = 0;
        for (int s = 0; s < _nShell; s++) { w[s] = DebrisEnvironment.SpatialWeight(_midAlt[s]); wsum += w[s]; }
        if (wsum <= 0) return;
        for (int s = 0; s < _nShell; s++)
        {
            double f = w[s] / wsum;
            _n[s, 0] += 0.7 * backgroundSmallTotal * f;
            _n[s, 1] += 0.3 * backgroundSmallTotal * f;
            _n[s, SatClass] += 0.85 * backgroundLargeTotal * f;
            _n[s, RocketBodyClass] += 0.15 * backgroundLargeTotal * f;
        }
    }

    /// <summary>Inject the barrel of nails into the release shell.</summary>
    public void InjectBarrel(double altKm, double nailCount)
    {
        int s = ShellOf(altKm);
        if (s >= 0) _n[s, NailClass] += nailCount;
    }

    /// <summary>
    /// Inject a one-off breakup — an ASAT strike or a large accidental collision — of a
    /// <paramref name="targetKg"/> object hit by a <paramref name="projectileKg"/> one at the given
    /// altitude. Its breakup-model fragments spread across neighbouring shells like any collision's.
    /// </summary>
    public void InjectBreakup(double altKm, double targetKg, double projectileKg)
    {
        int s = ShellOf(altKm); if (s < 0) return;
        bool cat = Lethality.IsCatastrophic(projectileKg, RelVelMetersPerSec, targetKg);
        double meff = BreakupModel.EffectiveMass(targetKg, projectileKg, RelVelMetersPerSec);
        double avail = cat ? targetKg + projectileKg : Math.Min(targetKg, 50.0 * projectileKg);
        DepositFragments(_n, s, meff, 1.0, avail);
    }

    public double TotalDebris()
    {
        double t = 0;
        for (int s = 0; s < _nShell; s++) for (int c = 0; c < _nc; c++) if (!_cls[c].IsNail && !_cls[c].IsWorking) t += _n[s, c];
        foreach (var o in _ecc) t += o.W * o.F.Sum();
        return t;
    }
    /// <summary>Objects ≥10 cm across LEO (size classes whose lower edge is ≥10 cm; nails excluded).</summary>
    public double TotalTrackable() => TotalTrackable(double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>Objects ≥10 cm in shells whose mid-altitude lies in [lo, hi) km.</summary>
    public double TotalTrackable(double loKm, double hiKm)
    {
        double t = 0;
        for (int s = 0; s < _nShell; s++)
        {
            if (_midAlt[s] < loKm || _midAlt[s] >= hiKm) continue;
            for (int c = 0; c < _nc; c++) if (IsTrackedSize(c) && !_cls[c].IsWorking) t += _n[s, c];
            foreach (var o in _ecc) if (IsTrackedSize(o.C)) t += o.W * o.F[s];
        }
        return t;
    }

    public double TotalNails()
    {
        double t = 0; for (int s = 0; s < _nShell; s++) t += _n[s, NailClass]; return t;
    }

    /// <summary>Shell mid-altitudes [km].</summary>
    public double[] MidAltitudesKm => (double[])_midAlt.Clone();

    /// <summary>
    /// Annual expected impacts on one operational satellite (area <paramref name="satAreaM2"/>)
    /// at each shell — i.e. its per-year collision/mission-kill probability. Above the operator
    /// loss tolerance the orbit is effectively unusable.
    /// </summary>
    public double[] SatelliteHazardByShell(double satAreaM2, double relVelMetersPerSec)
    {
        const double secYr = 3.15576e7;
        double sqrtSat = Math.Sqrt(satAreaM2);
        var haz = new double[_nShell];
        for (int s = 0; s < _nShell; s++)
        {
            double V = _volM3[s]; if (V <= 0) continue;
            double sum = 0;
            for (int c = 0; c < _nc; c++)
            {
                double sigma = Math.Pow(sqrtSat + Math.Sqrt(_cls[c].AreaM2), 2.0);
                sum += _n[s, c] / V * sigma;
            }
            foreach (var o in _ecc) sum += o.W * o.F[s] / V * Math.Pow(sqrtSat + Math.Sqrt(_cls[o.C].AreaM2), 2.0);
            haz[s] = sum * relVelMetersPerSec * secYr;
        }
        return haz;
    }

    /// <summary>Advance by dt seconds. Returns the number of catastrophic collisions in the step.</summary>
    public double Step(double dtSec)
    {
        var dN = new double[_nShell, _nc];
        double catastrophic = 0;
        var E = EccentricPresence();
        var eLoss = new double[_nShell, _nc];
        // Remove `amt` objects of class c in shell s, shared between residents and eccentric visitors by presence.
        void Take(int s, int c, double amt)
        {
            double tot = _n[s, c] + E[s, c], share = tot > 0 ? E[s, c] / tot : 0;
            dN[s, c] -= amt * (1 - share); eLoss[s, c] += amt * share;
        }

        for (int s = 0; s < _nShell; s++)
        {
            double V = _volM3[s];
            for (int j = 0; j < _nc; j++)
            {
                double nj = _n[s, j] + E[s, j]; if (nj <= 0) continue;
                for (int k = j; k < _nc; k++)
                {
                    double nk = _n[s, k] + E[s, k]; if (nk <= 0) continue;
                    bool bothTracked = IsTrackedSize(j) && IsTrackedSize(k);
                    if (TrackedOnlyCollisions && !bothTracked) continue;
                    double sigma = Math.Pow(Math.Sqrt(_cls[j].AreaM2) + Math.Sqrt(_cls[k].AreaM2), 2.0);
                    double pairRate = (j == k ? 0.5 * nj * nk : nj * nk) / V * sigma * RelVelMetersPerSec;
                    double events = pairRate * dtSec;
                    if (events <= 0) continue;

                    // A working satellite dodges a tracked partner; untracked debris and nails still hit it.
                    if (_cls[j].IsWorking || _cls[k].IsWorking)
                    {
                        int other = _cls[j].IsWorking ? k : j;
                        if (IsTracked(other))
                        {
                            double f = ManoeuvrableFraction * AvoidanceSuccess;
                            AvoidedTotal += events * f; events *= 1.0 - f;
                        }
                    }

                    double mj = _cls[j].MassKg, mk = _cls[k].MassKg;
                    double mt = Math.Max(mj, mk), mp = Math.Min(mj, mk);
                    int projClass = mj <= mk ? j : k;
                    bool cat = Lethality.IsCatastrophic(mp, RelVelMetersPerSec, mt);

                    // Clamp so a single step can't remove more than exists.
                    if (cat)
                    {
                        events = Math.Min(events, Math.Min(nj, nk));
                        if (events <= 0) continue;
                        Take(s, j, events); Take(s, k, events);
                        catastrophic += events; _catByShell[s] += events;
                    }
                    else
                    {
                        events = Math.Min(events, projClass == j ? nj : nk);
                        if (events <= 0) continue;
                        Take(s, projClass, events);
                        if (bothTracked) NonCatastrophicTrackedTotal += events;

                        // A working satellite that survives the hit is still dead (mission kill): it can no
                        // longer manoeuvre or deorbit, so it joins the intact derelicts.
                        int target = projClass == j ? k : j;
                        if (_cls[target].IsWorking && target != projClass)
                        {
                            double kill = Math.Min(events, Math.Max(0.0, _n[s, target] + dN[s, target]));
                            dN[s, target] -= kill; dN[s, SatClass] += kill; MissionKillsTotal += kill;
                        }
                    }

                    if (!cat && _cls[projClass].LcM < CrateringEjectaMinLcM) continue;
                    double meff = cat ? (mt + mp) : BreakupModel.EffectiveMass(mt, mp, RelVelMetersPerSec);
                    double availMass = cat ? (mt + mp) : Math.Min(mt, 50.0 * mp);
                    DepositFragments(dN, s, meff, events, availMass);
                }
            }
        }

        ApplyEccentricLosses(E, eLoss);
        ApplyExplosions(dN, dtSec);

        // Apply, clamping to [0, SaturationCap]. The cap keeps a violent (physically absurd)
        // runaway finite instead of overflowing to Infinity/NaN — a saturated cell still reads
        // as "ran away" without breaking the explicit integrator.
        const double SaturationCap = 1e13;
        for (int s = 0; s < _nShell; s++)
            for (int c = 0; c < _nc; c++)
            {
                double v = _n[s, c] + dN[s, c];
                _n[s, c] = v > SaturationCap ? SaturationCap : (v > 0 ? v : 0);
            }

        DragMigrate(dtSec);
        EccentricDrag(dtSec);
        ApplyRetirement(dtSec);
        ApplyLaunch(dtSec);
        ApplyRemoval(dtSec);
        _simSec += dtSec;
        return catastrophic;
    }

    /// <summary>Size classes ≥10 cm (not nails): what LEGEND simulates.</summary>
    private bool IsTrackedSize(int c) => !_cls[c].IsNail && _cls[c].LcLoM >= 0.1 - 1e-12;

    /// <summary>Tracked (≥10 cm, catalogued) classes: the ones a working satellite can see coming and dodge.</summary>
    private bool IsTracked(int c) => _cls[c].IsWorking || (!_cls[c].IsNail && _cls[c].LcLoM >= 0.1 - 1e-12);

    /// <summary>
    /// End of life: working satellites retire at 1/<see cref="SatelliteLifetimeYears"/> per year; a share
    /// <see cref="DisposalSuccess"/> is deorbited (leaves LEO), the rest are left dead in place.
    /// </summary>
    private void ApplyRetirement(double dtSec)
    {
        if (!WorkingSatellites || SatelliteLifetimeYears <= 0) return;
        double f = 1.0 - Math.Exp(-dtSec / (SatelliteLifetimeYears * 365.25 * Constants.SecondsPerDay));
        for (int s = 0; s < _nShell; s++)
        {
            double retire = _n[s, WorkingClass] * f; if (retire <= 0) continue;
            _n[s, WorkingClass] -= retire;
            double dead = retire * (1.0 - DisposalSuccess);
            _n[s, SatClass] += dead;
            DisposedTotal += retire - dead; FailedDisposalTotal += dead;
        }
    }

    public double TotalWorking()
    {
        double t = 0; for (int s = 0; s < _nShell; s++) t += _n[s, WorkingClass]; return t;
    }

    /// <summary>
    /// Remove this step's share of <see cref="RemovalsPerYear"/> from the intact (shell, class) cells
    /// with the highest per-object mass × collision rate, highest first.
    /// </summary>
    private void ApplyRemoval(double dtSec)
    {
        double budget = RemovalsPerYear * dtSec / (365.25 * Constants.SecondsPerDay);
        if (budget <= 0) return;
        var cells = new List<(double Score, int S, int C)>();
        for (int s = 0; s < _nShell; s++)
        {
            double V = _volM3[s];
            foreach (int c in IntactPool())
            {
                if (_n[s, c] <= 0) continue;
                double rate = 0;   // collisions per second for one object of class c in shell s
                for (int k = 0; k < _nc; k++)
                    rate += _n[s, k] / V * Math.Pow(Math.Sqrt(_cls[c].AreaM2) + Math.Sqrt(_cls[k].AreaM2), 2.0) * RelVelMetersPerSec;
                cells.Add((_cls[c].MassKg * rate, s, c));
            }
        }
        foreach (var (_, s, c) in cells.OrderByDescending(x => x.Score))
        {
            double take = Math.Min(budget, _n[s, c]);
            _n[s, c] -= take; RemovalsTotal += take; budget -= take;
            if (budget <= 0) break;
        }
    }

    private void ApplyLaunch(double dtSec)
    {
        if (LaunchRatePerYear <= 0) return;
        int s = ShellOf(LaunchAltKm); if (s < 0) return;

        double throttle = 1.0;
        if (ResponsiveLaunch)
        {
            // Annual probability an operational satellite in this band is struck (any impact =
            // mission loss). Operators cut launches proportionally, to zero at the tolerance.
            double V = _volM3[s], sqrtSat = Math.Sqrt(OperationalSatAreaM2), lossPerSec = 0;
            for (int c = 0; c < _nc; c++)
            {
                double sigma = Math.Pow(sqrtSat + Math.Sqrt(_cls[c].AreaM2), 2.0);
                lossPerSec += (_n[s, c] / V) * sigma * RelVelMetersPerSec;
            }
            double lossPerYear = lossPerSec * 365.25 * Constants.SecondsPerDay;
            throttle = Math.Max(0.0, 1.0 - lossPerYear / LossTolerancePerYear);
        }
        _lastThrottle = throttle;

        double add = LaunchRatePerYear * throttle * dtSec / (365.25 * Constants.SecondsPerDay);
        if (WorkingSatellites)
        {
            _n[s, WorkingClass] += 0.85 * add;                   // working satellites
            _n[s, RocketBodyClass] += 0.15 * add * (1.0 - RocketBodyDisposal); // rocket bodies left behind
            return;
        }
        _n[s, SatClass] += 0.85 * add;         // ~180 kg intacts (payloads / debris)
        _n[s, RocketBodyClass] += 0.15 * add;  // ~2.4 t rocket bodies
    }

    // ------------------------------------------------------------------ eccentric orbits ----------

    private sealed class Ecc
    {
        public double A, Rp, W;   // semi-major axis and perigee radius [km], objects represented
        public int C;             // class
        public double[] F = [];   // time fraction in each shell
        public double AF;         // semi-major axis F was computed at
        public double Rate;       // orbit-averaged da/dt [km/s]
        public double ARate = double.NaN; public int RateAge;
    }
    private readonly List<Ecc> _ecc = new();

    /// <summary>Eccentric objects being followed individually, and their total LEO presence (Σ weight × time in LEO).</summary>
    public int EccentricCount => _ecc.Count;
    public double EccentricPresenceTotal => _ecc.Sum(o => o.W * o.F.Sum());

    private void AddEccentric(double aKm, double rpKm, int cls, double w)
    {
        if (rpKm - Constants.EarthRadiusKm < AtmosphericDrag.ReentryAltitudeKm) return;
        var o = new Ecc { A = aKm, Rp = rpKm, W = w, C = cls };
        UpdateResidence(o);
        if (o.F.Sum() > 0) _ecc.Add(o);
    }

    /// <summary>Fraction of the orbit's time with radius below R (Kepler: r = a(1 − e cos E), M = E − e sin E).</summary>
    private static double TimeBelow(double R, double a, double e)
    {
        double rp = a * (1 - e), ra = a * (1 + e);
        if (R <= rp) return 0; if (R >= ra) return 1;
        double E = Math.Acos(Math.Clamp((1 - R / a) / e, -1, 1));
        return (E - e * Math.Sin(E)) / Math.PI;
    }

    private void UpdateResidence(Ecc o)
    {
        double e = 1 - o.Rp / o.A, re = Constants.EarthRadiusKm;
        var f = new double[_nShell];
        for (int s = 0; s < _nShell; s++)
        {
            double lo = re + _minAlt + s * _binKm, hi = lo + _binKm;
            f[s] = TimeBelow(hi, o.A, e) - TimeBelow(lo, o.A, e);
        }
        o.F = f; o.AF = o.A;
    }

    private double[,] EccentricPresence()
    {
        var E = new double[_nShell, _nc];
        foreach (var o in _ecc)
            for (int s = 0; s < _nShell; s++) if (o.F[s] > 0) E[s, o.C] += o.W * o.F[s];
        return E;
    }

    /// <summary>Collisions that took eccentric objects: each loses its share (weight × time in that shell).</summary>
    private void ApplyEccentricLosses(double[,] E, double[,] eLoss)
    {
        foreach (var o in _ecc)
        {
            double w0 = o.W, lost = 0;
            for (int s = 0; s < _nShell; s++)
                if (o.F[s] > 0 && E[s, o.C] > 0 && eLoss[s, o.C] > 0) lost += eLoss[s, o.C] * (w0 * o.F[s] / E[s, o.C]);
            o.W = Math.Max(0, w0 - lost);
        }
        _ecc.RemoveAll(o => o.W <= 1e-9);
    }

    /// <summary>
    /// Orbit-averaged drag on an eccentric orbit: da/dt = −(a²/μ)·C_D·(A/m)·⟨ρ v³⟩ over the orbit (the energy loss
    /// rate; for a circular orbit this is the usual −ρ C_D (A/m) √(μa)). The perigee holds while the apogee comes
    /// down; once the orbit is nearly circular the object joins its shell's population.
    /// </summary>
    private void EccentricDrag(double dtSec)
    {
        if (_ecc.Count == 0) return;
        double activity = SolarActivity * (SolarCycleAmplitude == 0 ? 1.0
            : Math.Exp(SolarCycleAmplitude * Math.Sin(2 * Math.PI * _simSec / (11.0 * 365.25 * Constants.SecondsPerDay))));
        foreach (var o in _ecc)
        {
            if (double.IsNaN(o.ARate) || Math.Abs(o.A - o.ARate) > 1.0 || ++o.RateAge >= (SolarCycleAmplitude == 0 ? 36 : 6))
            {
                o.Rate = AtmosphericDrag.OrbitAveragedDecayRateKmPerSec(o.A, 1 - o.Rp / o.A, _cls[o.C].AreaToMass, activity);
                o.ARate = o.A; o.RateAge = 0;
            }
            o.A = Math.Max(o.Rp, o.A + o.Rate * dtSec);
            if (Math.Abs(o.A - o.AF) > 1.0) UpdateResidence(o);
        }
        // Nearly circular now: hand over to the shell population (or gone, if below the model).
        foreach (var o in _ecc.Where(o => 1 - o.Rp / o.A < 0.5 * EccentricMinE).ToList())
        {
            int s = ShellOf(o.A - Constants.EarthRadiusKm);
            if (s >= 0) _n[s, o.C] += o.W;
            o.W = 0;
        }
        _ecc.RemoveAll(o => o.W <= 1e-9);
    }

    private void DepositFragments(double[,] dN, int s, double meff, double events, double availMass)
    {
        // Mass-limited fragment count per event in each size class (small bins filled first).
        Span<double> mass = stackalloc double[SizeClassCount];
        Span<double> cnt = stackalloc double[SizeClassCount];
        for (int c = 0; c < SizeClassCount; c++) mass[c] = _cls[c].MassKg;
        BreakupModel.DistributeFragments(meff, availMass, LcEdges, mass, cnt);
        DepositCounts(dN, s, cnt, events);
    }

    /// <summary>
    /// Explosions this step: intact objects (the intact classes) fragment at a per-kg rate calibrated
    /// so the seeded population produces <see cref="ExplosionsPerYear"/>; each removes its parent and
    /// deposits breakup-model explosion fragments, spread across shells like collision debris.
    /// </summary>
    private void ApplyExplosions(double[,] dN, double dtSec)
    {
        if (ExplosionsPerYear <= 0) return;
        if (_explPerKgSec < 0)
        {
            double m = 0;
            foreach (int c in IntactPool()) for (int s = 0; s < _nShell; s++) m += _n[s, c] * _cls[c].MassKg;
            _explPerKgSec = m > 0 ? ExplosionsPerYear / (365.25 * Constants.SecondsPerDay) / m : 0;
        }
        if (_explPerKgSec <= 0) return;

        Span<double> mass = stackalloc double[SizeClassCount];
        Span<double> cnt = stackalloc double[SizeClassCount];
        for (int c = 0; c < SizeClassCount; c++) mass[c] = _cls[c].MassKg;
        foreach (int c in IntactPool())
        {
            BreakupModel.DistributeExplosionFragments(_cls[c].MassKg, LcEdges, mass, cnt, ExplosionScale);
            for (int s = 0; s < _nShell; s++)
            {
                double events = Math.Min(_n[s, c] * _cls[c].MassKg * _explPerKgSec * dtSec, _n[s, c]);
                if (events <= 0) continue;
                dN[s, c] -= events;
                ExplosionsTotal += events;
                DepositCounts(dN, s, cnt, events);
            }
        }
    }

    /// <summary>Explosions so far in this model's run (fractional — the box model is continuous).</summary>
    public double ExplosionsTotal { get; private set; }

    private void DepositCounts(double[,] dN, int s, ReadOnlySpan<double> cnt, double events)
    {
        EnsureSpreadKernel();
        for (int c = 0; c < SizeClassCount; c++)
        {
            double amount = events * cnt[c];
            if (amount <= 0) continue;
            // Scatter across neighbouring shells; fragments landing outside the modelled
            // altitude range are lost (reentry below, escape above).
            for (int d = -_spreadSpan; d <= _spreadSpan; d++)
            {
                int ss = s + d;
                if (ss < 0 || ss >= _nShell) continue;
                dN[ss, c] += amount * _spreadKernel![d + _spreadSpan];
            }
        }
    }

    private void EnsureSpreadKernel()
    {
        if (_spreadKernel != null) return;
        double sigma = Math.Max(0.01, FragmentSpreadShells);
        _spreadSpan = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = new double[2 * _spreadSpan + 1]; double sum = 0;
        for (int d = -_spreadSpan; d <= _spreadSpan; d++) { double v = Math.Exp(-(d * d) / (2 * sigma * sigma)); k[d + _spreadSpan] = v; sum += v; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        _spreadKernel = k;
    }

    private void DragMigrate(double dtSec)
    {
        double activity = SolarActivity * (SolarCycleAmplitude == 0 ? 1.0
            : Math.Exp(SolarCycleAmplitude * Math.Sin(2 * Math.PI * _simSec / (11.0 * 365.25 * Constants.SecondsPerDay))));
        for (int s = 0; s < _nShell; s++)
        {
            double a = Constants.EarthRadiusKm + _midAlt[s];
            for (int c = 0; c < _nc; c++)
            {
                if (_cls[c].IsWorking) continue;   // station-keeping: working satellites hold their altitude
                double pop = _n[s, c]; if (pop <= 0) continue;
                double rateKmDay = -AtmosphericDrag.SemiMajorAxisDecayRateKmPerSec(a, _cls[c].AreaToMass, activity) * 86400.0;
                if (rateKmDay <= 1e-9) continue;
                double tauSec = (_binKm / rateKmDay) * 86400.0;
                double moved = pop * (1.0 - Math.Exp(-dtSec / tauSec));
                _n[s, c] -= moved;
                if (s > 0) _n[s - 1, c] += moved; // else: reenters, leaves LEO
            }
        }
    }

    /// <summary>Run to the horizon, recording yearly snapshots.</summary>
    public EvolutionResult Run(double horizonYears = 50, double dtDays = 10)
    {
        var years = new List<double>(); var tot = new List<double>(); var trk = new List<double>(); var belt = new List<double>();
        var catPy = new List<double>(); var nails = new List<double>(); var lf = new List<double>(); var work = new List<double>();

        double catAccum = 0, nextYear = 0; double t = 0, doneDays = 0, totalDays = horizonYears * 365.25;
        years.Add(0); tot.Add(TotalDebris()); trk.Add(TotalTrackable()); belt.Add(TotalTrackable(BeltLoKm, BeltHiKm)); catPy.Add(0); nails.Add(TotalNails()); lf.Add(_lastThrottle); work.Add(TotalWorking());

        // Last step is shortened so the run ends exactly on the horizon (and records its final year).
        while (doneDays < totalDays - 1e-9)
        {
            double d = Math.Min(dtDays, totalDays - doneDays);
            catAccum += Step(d * Constants.SecondsPerDay);
            doneDays += d; t = doneDays / 365.25;
            if (t >= nextYear + 1 - 1e-9)
            {
                nextYear += 1;
                years.Add(t); tot.Add(TotalDebris()); trk.Add(TotalTrackable()); belt.Add(TotalTrackable(BeltLoKm, BeltHiKm)); catPy.Add(catAccum); nails.Add(TotalNails()); lf.Add(_lastThrottle); work.Add(TotalWorking());
                catAccum = 0;
            }
        }
        return new EvolutionResult
        {
            Years = years.ToArray(), TotalObjects = tot.ToArray(),
            CatastrophicPerYear = catPy.ToArray(), SurvivingNails = nails.ToArray(),
            LaunchFraction = lf.ToArray(), TrackableObjects = trk.ToArray(), BeltTrackableObjects = belt.ToArray(), WorkingSatellites = work.ToArray(),
        };
    }
}
