# SpaceWars — Barrel-of-Nails LEO Debris Model

An academic risk assessment: *can a launched "barrel of nails" render low-Earth orbit
unusable via Kessler syndrome?* The tool pulls the real satellite catalog, propagates it
on the GPU, and runs the collision/breakup/cascade physics to answer the question with
numbers instead of intuition.

**Headline finding.** A single barrel of nails is a mission-kill weapon and a persistent
nuisance at high altitude, but it does **not** trigger Kessler syndrome or render LEO
unusable. LEO's debris belt (~800–1000 km) is *already near-critical*; whether it runs away
is governed by **launch and removal policy**, against which a barrel of nails is a rounding
error (~hundreds of barrels / thousands of tonnes would be needed even to nudge a band).

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
| `SpaceWars.Tests` | 26 physics/GPU validation tests |

## Build & run

```bash
# Build the CUDA engine (needs the CUDA toolkit + MSVC; RTX-class GPU)
src/SpaceWars.Native/build.bat

# Build & test the .NET solution
dotnet test

# Run the assessment against the live catalog
dotnet run --project src/SpaceWars.Cli

# Optional: fuller per-object RCS via Space-Track (falls back to CelesTrak if unset)
export SPACETRACK_USER=you@example.com SPACETRACK_PASS=...   # credentials read from the env only
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
| `--responsive [--loss-tol <frac>]` | economically rational launch (self-limiting) |
| `--tipping` | critical launch-rate sweep |
| `--barrel-threshold` | how many barrels tip a band |
| `--calibrate` | cube-method rate calibration (geometric vs well-mixed) |
| `--charts` / `--export <file>` | write data for the visualizations |

## Visualizations

- **Nail Cloud Orbital Decay** — interactive 3D globe (three.js): the cloud spreading and
  decaying over a 3-year timeline. Built from `viz/part1.html + data/scene.json + viz/part2.html`.
- **Kessler Analysis** — four-chart summary (Chart.js). Built from
  `viz/charts_part1.html + data/charts.json + viz/charts_part2.html`.

## Key assumptions & caveats

- Nail: 75 × 3 mm carbon steel ≈ 4.16 g; catastrophic threshold 40 J/g (NASA SBM).
- Debris altitude profile is a stylized ORDEM/MASTER-like distribution (peak ~850 km);
  seeding it correctly is essential — tying it to the payload catalog understates Kessler.
- Object masses/areas are class-representative (SATCAT/RCS refinement is future work).
- Object masses/areas come from SATCAT: RCS as cross-section (CelesTrak numeric, or Space-Track
  RCS_SIZE categories), mass from the NASA size↔mass law. CelesTrak's active feed is RCS-sparse
  (~3% of the LEO set); Space-Track credentials give far fuller coverage.
- Box model uses a well-mixed shell assumption; the conjunction Cube method is geometrically
  faithful (real cross-shell crossings) BUT `--calibrate` shows its *absolute* rate is
  cube-size-dependent with super-particles (λ∝1/V_cube variance), so it is **not quotable** as
  implemented — use the box model for rates and the conjunction model for geometry. Converging
  it needs near-unit-weight particles (~10⁶ objects, feasible on the GPU). Both bracket the real
  near-critical margin.
