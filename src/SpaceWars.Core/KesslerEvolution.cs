using System;
using System.Collections.Generic;

namespace SpaceWars.Core;

/// <summary>Representative object class: a size bin with derived mass and cross-section.</summary>
public sealed class DebrisClass
{
    public double LcLoM, LcHiM, LcM;   // characteristic-length bin edges & representative [m]
    public double MassKg, AreaM2;
    public bool IsNail;
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
    private readonly int _nc;                          // total classes incl. nails

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

        _nc = SizeClassCount + 1;
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
        _n = new double[_nShell, _nc];
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
            _n[s, 5] += largeIntactFraction;
            _n[s, 4] += 1.0 - largeIntactFraction;
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
        foreach (var o in objects)
        {
            int s = ShellOf(o.Elements.SemiMajorAxis - Constants.EarthRadiusKm);
            if (s < 0) continue;
            _n[s, NearestSizeClass(o.MassKg)] += 1.0;
        }
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
            _n[s, 4] += 0.85 * backgroundLargeTotal * f;
            _n[s, 5] += 0.15 * backgroundLargeTotal * f;
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
        for (int s = 0; s < _nShell; s++) for (int c = 0; c < SizeClassCount; c++) t += _n[s, c];
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
            for (int c = 0; c < SizeClassCount; c++) if (_cls[c].LcLoM >= 0.1 - 1e-12) t += _n[s, c];
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
            haz[s] = sum * relVelMetersPerSec * secYr;
        }
        return haz;
    }

    /// <summary>Advance by dt seconds. Returns the number of catastrophic collisions in the step.</summary>
    public double Step(double dtSec)
    {
        var dN = new double[_nShell, _nc];
        double catastrophic = 0;

        for (int s = 0; s < _nShell; s++)
        {
            double V = _volM3[s];
            for (int j = 0; j < _nc; j++)
            {
                double nj = _n[s, j]; if (nj <= 0) continue;
                for (int k = j; k < _nc; k++)
                {
                    double nk = _n[s, k]; if (nk <= 0) continue;
                    double sigma = Math.Pow(Math.Sqrt(_cls[j].AreaM2) + Math.Sqrt(_cls[k].AreaM2), 2.0);
                    double pairRate = (j == k ? 0.5 * nj * nk : nj * nk) / V * sigma * RelVelMetersPerSec;
                    double events = pairRate * dtSec;
                    if (events <= 0) continue;

                    double mj = _cls[j].MassKg, mk = _cls[k].MassKg;
                    double mt = Math.Max(mj, mk), mp = Math.Min(mj, mk);
                    int projClass = mj <= mk ? j : k;
                    bool cat = Lethality.IsCatastrophic(mp, RelVelMetersPerSec, mt);

                    // Clamp so a single step can't remove more than exists.
                    if (cat)
                    {
                        events = Math.Min(events, Math.Min(_n[s, j], _n[s, k]));
                        if (events <= 0) continue;
                        dN[s, j] -= events; dN[s, k] -= events;
                        catastrophic += events;
                    }
                    else
                    {
                        events = Math.Min(events, _n[s, projClass]);
                        if (events <= 0) continue;
                        dN[s, projClass] -= events;
                    }

                    if (!cat && _cls[projClass].LcM < CrateringEjectaMinLcM) continue;
                    double meff = cat ? (mt + mp) : BreakupModel.EffectiveMass(mt, mp, RelVelMetersPerSec);
                    double availMass = cat ? (mt + mp) : Math.Min(mt, 50.0 * mp);
                    DepositFragments(dN, s, meff, events, availMass);
                }
            }
        }

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
        ApplyLaunch(dtSec);
        return catastrophic;
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
        _n[s, 4] += 0.85 * add;  // ~180 kg intacts (payloads / debris)
        _n[s, 5] += 0.15 * add;  // ~2.4 t rocket bodies
    }

    private void DepositFragments(double[,] dN, int s, double meff, double events, double availMass)
    {
        // Mass-limited fragment count per event in each size class (small bins filled first).
        Span<double> mass = stackalloc double[SizeClassCount];
        Span<double> cnt = stackalloc double[SizeClassCount];
        for (int c = 0; c < SizeClassCount; c++) mass[c] = _cls[c].MassKg;
        BreakupModel.DistributeFragments(meff, availMass, LcEdges, mass, cnt);

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
        for (int s = 0; s < _nShell; s++)
        {
            double a = Constants.EarthRadiusKm + _midAlt[s];
            for (int c = 0; c < _nc; c++)
            {
                double pop = _n[s, c]; if (pop <= 0) continue;
                double rateKmDay = -AtmosphericDrag.SemiMajorAxisDecayRateKmPerSec(a, _cls[c].AreaToMass, SolarActivity) * 86400.0;
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
        var catPy = new List<double>(); var nails = new List<double>(); var lf = new List<double>();

        double catAccum = 0, nextYear = 0; double t = 0, doneDays = 0, totalDays = horizonYears * 365.25;
        years.Add(0); tot.Add(TotalDebris()); trk.Add(TotalTrackable()); belt.Add(TotalTrackable(BeltLoKm, BeltHiKm)); catPy.Add(0); nails.Add(TotalNails()); lf.Add(_lastThrottle);

        // Last step is shortened so the run ends exactly on the horizon (and records its final year).
        while (doneDays < totalDays - 1e-9)
        {
            double d = Math.Min(dtDays, totalDays - doneDays);
            catAccum += Step(d * Constants.SecondsPerDay);
            doneDays += d; t = doneDays / 365.25;
            if (t >= nextYear + 1 - 1e-9)
            {
                nextYear += 1;
                years.Add(t); tot.Add(TotalDebris()); trk.Add(TotalTrackable()); belt.Add(TotalTrackable(BeltLoKm, BeltHiKm)); catPy.Add(catAccum); nails.Add(TotalNails()); lf.Add(_lastThrottle);
                catAccum = 0;
            }
        }
        return new EvolutionResult
        {
            Years = years.ToArray(), TotalObjects = tot.ToArray(),
            CatastrophicPerYear = catPy.ToArray(), SurvivingNails = nails.ToArray(),
            LaunchFraction = lf.ToArray(), TrackableObjects = trk.ToArray(), BeltTrackableObjects = belt.ToArray(),
        };
    }
}
