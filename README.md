# DebrisCascade — Barrel-of-Nails LEO Debris Model

An academic risk assessment: *can a launched "barrel of nails" render low-Earth orbit
unusable via Kessler syndrome?* The tool pulls the real satellite catalog, propagates it
on the GPU, and runs the collision/breakup/cascade physics to answer the question with
numbers instead of intuition.

**Headline finding.** A single barrel of nails is a mission-kill weapon and a persistent
nuisance at high altitude, but it does **not** trigger Kessler syndrome or render LEO
unusable. LEO's debris belt (700–1,100 km) *already grows slowly on its own*; whether it runs
away is governed by **launch and removal policy**, against which a barrel of nails is a
rounding error (1,000 barrels add ~25% to the belt's ≥10 cm population in 50 years; ~9
removals a year hold it flat). Disposal reliability is the lever: 500 satellites and rocket bodies
a year left dead at 900 km grow the belt ×60 in 50 years, but with working satellites deorbited at
today's 90% rate, the same traffic grows it only ×4.8. Rerun from NASA's own 2006 starting point,
the cube engine comes close to LEGEND's 200-year collision count: 12.8 catastrophic collisions (LEGEND 10.8).

See **[SUMMARY.md](SUMMARY.md)** for a plain-language write-up, including the altitude analysis.

## Pipeline

```
Space-Track catalog → CUDA J2 propagation → collision flux (spatial density)
                                          → NASA Standard Breakup Model
                                          → box-model Kessler evolution (source–sink ODE)
                                          → discrete super-particle cascade
                                          → conjunction cascade (Cube method, real geometry)
```

## Projects

| Project | Role |
|---|---|
| `DebrisCascade.Core` | Physics: orbital elements + J2 propagator, TLE parser, lethality (EMR/40 J/g), NASA breakup model, exponential-atmosphere drag, spatial-density flux, box-model Kessler evolution, discrete cascade, debris altitude profile |
| `DebrisCascade.Native` | CUDA C++ engine (`debriscascade_cuda.cu`) — batch J2 propagation + time-averaged density; built to `debriscascade_cuda.dll` |
| `DebrisCascade.Interop` | P/Invoke bindings (`Cuda`) + the tier-3 conjunction cascade |
| `DebrisCascade.Cli` | Assessment CLI and scenario runners |
| `DebrisCascade.Wpf` | Interactive analysis tool: the deck's analyses on your own inputs (see below) |
| `DebrisCascade.Tests` | 58 physics/GPU validation tests |

## Build & run

Requirements: Windows (the WPF tool and the Credential Manager lookup are Windows-only), the
.NET 10 SDK, and for the GPU engine the CUDA toolkit (12.x) with MSVC. `build.bat` targets an
RTX 50-series GPU (`-arch=sm_120`) and a Visual Studio 18 install path; change both for other
setups. Without the CUDA DLL every engine still runs, on the CPU (the cube engine much slower).

```bash
# Build the CUDA engine (needs the CUDA toolkit + MSVC; RTX-class GPU)
src/DebrisCascade.Native/build.bat

# Build & test the .NET solution
dotnet test

# Run the assessment against the live catalog
dotnet run --project src/DebrisCascade.Cli

# Optional: fuller per-object RCS via Space-Track (falls back to CelesTrak if unset)
export SPACETRACK_USER=you@example.com SPACETRACK_PASS=...   # or a generic Windows credential named SPACETRACK
```

### CLI flags

| Flag | What it does |
|---|---|
| `--alt <km> --inc <deg> --nails <n> --sigma <m/s>` | barrel deployment |
| `--gpu` | full-population time-averaged density on CUDA + CPU benchmark |
| `--evolve` | 50-yr box-model evolution, baseline vs +barrel |
| `--cascade` | discrete super-particle cascade |
| `--conjunction` | tier-3 Cube-method cascade (real orbit-crossing geometry) |
| `--launch <n/yr> --launch-alt <km>` | ongoing launch traffic (the Kessler driver) |
| `--responsive [--loss-tol <frac>]` | economically rational launch (operators throttle, then quit) |
| `--tipping` | launch-rate sweep and break-even rate |
| `--barrel-threshold` | how many barrels tip a band |
| `--calibrate` | cube vs box collision rates (all and catastrophic) on the production population |
| `--charts` / `--export <file>` | write data for the visualizations |
| `--deck` | every number the presentation quotes → `data/deck_numbers.json` (charts: `viz/render_deck_charts.py`) |
| `--active-only` | use CelesTrak's active satellites instead of the full Space-Track on-orbit catalog |
| `--calibrate-speed` / `--calibrate-comoving` | cube rate by encounter speed (real vs scrambled planes); what the slow pairs are |
| `--benchmark-2006` | NASA benchmark: the 1 Jan 2006 catalog (Space-Track history), no launches, 200 years, vs LEGEND (Liou & Johnson 2006) → `data/benchmark_2006.json` |

### WPF tool

`dotnet run --project src/DebrisCascade.Wpf`. Each button runs one of the deck's analyses on the inputs in
the left panel and draws it the way the deck does: the metric is debris ≥10 cm in the 700–1,100 km
belt, and growth is "×" over today's belt objects.

**Engine.** The engine picked on the left (*Cube*, the default, or *Box model*) drives the top row of
cards: Evolution, Tipping sweep, Operators quit, Working satellites and Removal. On the cube engine
each is a GPU seed ensemble (*Seeds*, default 8): the mean is drawn, the seed range shaded (or as
whiskers on bars), and the box model dashed as a cross-check. A cube run takes about 10 s and an
ensemble runs in parallel; progress shows on the chart. The second row is tied to one engine by what
it needs, and each card says which: Globe needs every object's position (cube); Usability, Scale
check, Comparison and NASA comparison use the box model, which resolves effects smaller than the
cube's seed-to-seed scatter (one barrel adds under 1% to the belt).

| View | Engine | What it shows |
|---|---|---|
| Evolution | selected | belt debris over time, with and without the barrel; the working fleet when ticked |
| Tipping sweep | selected | belt growth vs objects added a year; a second curve for working satellites when ticked |
| Operators quit | selected | launching throughout vs operators throttling back and quitting |
| Working satellites | selected | never deorbited / poor / today's practice / best / your settings, at 50 and 500 a year |
| Removal | selected | belt growth vs large dead objects removed a year, with nothing added and with traffic |
| Globe | cube | every object of one cube run around the Earth every 5 years: intact objects, debris ≥10 cm coloured by altitude, nails, working satellites, and 1–10 cm debris on request; drag to turn, slide through the years |
| Usability by altitude | box | collision risk per satellite by altitude (today and with the barrel, year slider), the belt, the walk-away line, and how long a fragment stays (right axis) |
| Scale check (barrels) | box | belt and all-object change vs number of barrels at the release altitude, with one ASAT strike for scale |
| Comparison | box | barrel, ASAT and traffic on one measure: extra belt objects after the horizon |
| NASA comparison | box | catastrophic collisions a year with nothing added (≥10 cm only and incl. 1–10 cm) against LEGEND and the IADC study, plus growth and removals to stabilise, with sources |
| Engines side by side | all three | box, discrete and cube ensembles on the same inputs, no barrel |
| Lethality & Flux | — | single-nail lethality, drag lifetime and flux (text) |

"Working satellites (deorbit + dodge)" switches the box and cube engines to working satellites with the
disposal, rocket-body, avoidance and lifetime fields below it. **Save PNG** writes the current chart and
its text; `DebrisCascade.Wpf.exe --render <folder> [view ...]` renders every view (or the named ones) on the
default inputs and exits.

## Visualizations

- **Nail Cloud Orbital Decay** — interactive 3D globe (three.js): the cloud spreading and
  decaying over a 3-year timeline. Built from `viz/part1.html + data/scene.json + viz/part2.html`.
- **Kessler Analysis** — four-chart summary (Chart.js). Built from
  `viz/charts_part1.html + data/charts.json + viz/charts_part2.html`.

## Key assumptions & caveats

- Nail: 75 × 3 mm carbon steel ≈ 4.16 g; catastrophic threshold 40 J/g (NASA SBM).
- The modelled 1–10 cm field follows a stylized ORDEM/MASTER-like altitude profile (peak ~850 km).
  With the full Space-Track catalog, objects ≥10 cm are all real; a modelled large-object belt is
  only added when the catalog lacks catalogued debris (`--active-only`).
- Catalog: with Space-Track credentials, every object on orbit (payloads, rocket bodies,
  catalogued debris — ~29,800 in LEO); otherwise CelesTrak's active satellites plus a modelled
  large-object belt. A modelled ~1M-object 1–10 cm field is added either way.
- Object masses/areas come from SATCAT: RCS as cross-section (CelesTrak numeric, or Space-Track
  RCS_SIZE categories). Payloads and rocket bodies get intact masses (bulk-density law);
  debris and breakup fragments get the NASA breakup model's own area-to-mass ratios.
- The headline metric is objects ≥10 cm (all LEO and the 700–1,100 km belt).
- Explosions (non-collision fragmentations of rocket bodies and dead satellites) are a source term in all
  three engines: `ExplosionsPerYear` (default 4/yr at the seeded population, then scaling with the
  intact mass) and the breakup model's explosion law N(>Lc) = 6·S·Lc^-1.6 with `ExplosionScale`
  S = 0.25 (~60 fragments ≥10 cm per event, the average event; S = 1 is a large rocket-stage
  explosion). `--deck` reports the sensitivity to both.
- Active debris removal: `RemovalsPerYear` takes large intact objects out of orbit, highest
  mass × collision rate first (the LEGEND selection criterion); `--deck` reports how many a year
  hold the belt flat. Both explosion and removal rates are fields in the WPF tool.
- Working satellites: `WorkingSatellites` (box and cube engines; off by default, so every other
  result keeps added objects dead from day one). Catalogued satellites on CelesTrak's active list
  and new launches hold their altitude and dodge tracked (≥10 cm) objects: the collision rate with
  those is cut by `ManoeuvrableFraction` × `AvoidanceSuccess` (0.89 × 0.90). Untracked 1–10 cm
  debris can't be dodged, and any non-catastrophic hit leaves the satellite dead. After
  `SatelliteLifetimeYears` (5) a satellite is deorbited with probability `DisposalSuccess` (0.90),
  else left dead in place; launched rocket bodies are disposed of with `RocketBodyDisposal` (0.80).
  The defaults follow the evidence: the NASA/FCC/IADC 90% benchmark, ESA's 2025 rocket-body figure,
  and McDowell's manoeuvrable share of active satellites. `--deck` sweeps poor / today's practice /
  best settings on the cube engine, with the box model as a cross-check. Also a checkbox in the WPF tool.
- Engines in `--deck`: scenario results (tipping, operators, working satellites, removal,
  explosions) are 16-seed cube-engine ensembles run in parallel on the GPU, reported as mean and
  seed range; the barrel, ASAT and comparison figures, usability and the low-altitude event use
  the deterministic box model, because those effects are smaller than the cube's seed scatter.
- NASA benchmark (`--benchmark-2006 --cube-seeds 16`): the 1 Jan 2006 catalog, no launches or
  explosions, 200 years, ≥10 cm only. Cube engine 12.8 catastrophic collisions (7–18), ~46% at
  900–1,000 km, +13% in LEO at 50 years; box model 9.9 and +15%; LEGEND 10.8, ~60%, flat.
  Intact objects use catalog-true mass classes, and eccentric orbits count only their time in LEO.
- Box model uses a well-mixed shell assumption at 10 km/s. The conjunction Cube method uses real
  orbit geometry; `--calibrate` compares the two collision rates on the production population
  (not re-run since the 2026-09 model changes; the 2006 benchmark above is the current check). The cube engine
  skips encounters between catalogued payloads flying in formation (planes within 1°, semi-major
  axes within 20 km: constellation neighbours), which it would otherwise count as ~26 phantom
  collisions a year; `--calibrate-comoving` shows what those pairs are and `--calibrate-speed`
  breaks the rate down by encounter speed. The discrete and cube engines are stochastic: quote
  seed ensembles, not single runs.

## Data sources and terms

- The full on-orbit catalog, SATCAT radar cross-sections and the 2006 historical catalog come from
  [Space-Track.org](https://www.space-track.org). You need your own free account; the tool reads it
  from `SPACETRACK_USER` / `SPACETRACK_PASS` or a generic Windows credential named `SPACETRACK`, and
  never stores or logs the password. Space-Track's user agreement does not allow redistributing its
  data, so the downloaded catalogs are cached locally under `data/` and are not part of this
  repository. Only model outputs are committed.
- Without a Space-Track account the tool falls back to [CelesTrak](https://celestrak.org)
  (`--active-only`).
- The papers the model is built on and checked against are listed, with links, in
  [docs/REFERENCES.md](docs/REFERENCES.md).

## License

[MIT](LICENSE). This covers the code, the presentation, the charts and the data outputs.

## Citing

If you use the model or its results, please cite the repository:

> TimKadyrov. (2026). *DebrisCascade: a barrel-of-nails LEO debris model.* GitHub repository.
> https://github.com/TimKadyrov/DebrisCascade
