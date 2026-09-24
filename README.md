# SpaceWars — Barrel-of-Nails LEO Debris Model

An academic risk assessment: *can a launched "barrel of nails" render low-Earth orbit
unusable via Kessler syndrome?* The tool pulls the real satellite catalog, propagates it
on the GPU, and runs the collision/breakup/cascade physics to answer the question with
numbers instead of intuition.

**Headline finding.** A single barrel of nails is a mission-kill weapon and a persistent
nuisance at high altitude, but it does **not** trigger Kessler syndrome or render LEO
unusable. LEO's debris belt (700–1,100 km) *already grows slowly on its own*; whether it runs
away is governed by **launch and removal policy**, against which a barrel of nails is a
rounding error (no number of barrels doubles the belt's ≥10 cm population in 50 years; ~10
removals a year hold it flat).

See **[SUMMARY.md](SUMMARY.md)** for a plain-language write-up, including the altitude analysis.

## Pipeline

```
CelesTrak TLEs → CUDA J2 propagation → collision flux (spatial density)
                                     → NASA Standard Breakup Model
                                     → box-model Kessler evolution (source–sink ODE)
                                     → discrete super-particle cascade
                                     → conjunction cascade (Cube method, real geometry)
```

## Projects

| Project | Role |
|---|---|
| `SpaceWars.Core` | Physics: orbital elements + J2 propagator, TLE parser, lethality (EMR/40 J/g), NASA breakup model, exponential-atmosphere drag, spatial-density flux, box-model Kessler evolution, discrete cascade, debris altitude profile |
| `SpaceWars.Native` | CUDA C++ engine (`spacewars_cuda.cu`) — batch J2 propagation + time-averaged density; built to `spacewars_cuda.dll` |
| `SpaceWars.Interop` | P/Invoke bindings (`Cuda`) + the tier-3 conjunction cascade |
| `SpaceWars.Cli` | Assessment CLI and scenario runners |
| `SpaceWars.Tests` | 52 physics/GPU validation tests |

## Build & run

```bash
# Build the CUDA engine (needs the CUDA toolkit + MSVC; RTX-class GPU)
src/SpaceWars.Native/build.bat

# Build & test the .NET solution
dotnet test

# Run the assessment against the live catalog
dotnet run --project src/SpaceWars.Cli

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

## Visualizations

- **Nail Cloud Orbital Decay** — interactive 3D globe (three.js): the cloud spreading and
  decaying over a 3-year timeline. Built from `viz/part1.html + data/scene.json + viz/part2.html`.
- **Kessler Analysis** — four-chart summary (Chart.js). Built from
  `viz/charts_part1.html + data/charts.json + viz/charts_part2.html`.

## Key assumptions & caveats

- Nail: 75 × 3 mm carbon steel ≈ 4.16 g; catastrophic threshold 40 J/g (NASA SBM).
- Debris altitude profile is a stylized ORDEM/MASTER-like distribution (peak ~850 km);
  seeding it correctly is essential — tying it to the payload catalog understates Kessler.
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
- Box model uses a well-mixed shell assumption at 10 km/s. The conjunction Cube method uses real
  orbit geometry; `--calibrate` compares the two on the production population and they agree
  within ~5% (all collisions 1.01×, catastrophic 0.96× at 10,000 snapshots). The cube engine
  skips encounters between catalogued payloads flying in formation (planes within 1°, semi-major
  axes within 20 km: constellation neighbours), which it would otherwise count as ~26 phantom
  collisions a year; `--calibrate-comoving` shows what those pairs are and `--calibrate-speed`
  breaks the rate down by encounter speed. The discrete and cube engines are stochastic: quote
  seed ensembles, not single runs.
