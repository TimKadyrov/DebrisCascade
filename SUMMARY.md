# SpaceWars — Study Summary

*Can a launched "barrel of nails" render low-Earth orbit unusable via Kessler syndrome?*

Every number below comes from one scripted run (`SpaceWars.Cli --deck` → `data/deck_numbers.json`)
on the live Space-Track catalog. Growth figures are over 50 years.

## The study

We set out to answer, quantitatively rather than rhetorically, whether launching a "barrel of
nails" into low-Earth orbit could render LEO unusable through Kessler syndrome. The modeling
chain is grounded in real data. It uses every object currently on orbit from Space-Track: 29,779
in LEO, comprising 16,916 payloads, 9,956 catalogued debris pieces, 1,573 rocket bodies and 1,334
unidentified objects, 97% with radar cross-sections. Those cross-sections give per-object masses and
areas. Orbits are propagated with GPU-accelerated J2 on an RTX 5090. Fragmentation follows the
NASA Standard Breakup Model, including its own area-to-mass ratios for fragments. Three
independent cascade engines of increasing fidelity run the evolution: an aggregate source–sink
ODE, a discrete super-particle Monte Carlo, and a conjunction "cube" model that resolves
collisions by real orbit-crossing geometry. We added ongoing launch traffic, an
economically-rational "operators retreat" feedback, and explosions of rocket bodies and derelicts
(~4 a year, average size) as a source of debris besides collisions. The headline metric is the
standard one for Kessler studies: the population of objects **≥10 cm**, reported both for all of
LEO and for the 700–1,100 km belt.

The verdict: **a barrel of nails is a genuine mission-kill weapon, but it neither triggers
Kessler nor ends LEO.** A single ~4 g nail is lethal to any satellite it strikes, yet at ~0.8 J/g
it is far below the 40 J/g needed to *shatter* a 260 kg satellite; you'd need a ~200 g bolt for
that. The ~10⁴-fragment clouds that drive a cascade come from large-on-large collisions:
260 kg on 260 kg makes ~29,000 fragments ≥1 cm. Nails instead flood the lethal-but-untrackable
1–10 cm field. Dumped at 900 km, ~100–240 barrels double that field within 50 years (the range
brackets how much debris small impacts throw off: the breakup model as written vs NASA's LEGEND
convention). But no number of barrels doubles the ≥10 cm belt: even 1,000 barrels add only
~16%. One ASAT strike on a 1 t satellite at 865 km adds as many ≥10 cm objects as ~23 barrels.

## Altitude is the whole story

LEO is strongly stratified by altitude, and almost every conclusion flips with it.

- **Below ~600 km, drag is a free janitor.** Fragments rain out in months to a few years and dead
  satellites within about a decade. A barrel at 550 km loses half its nails in ~1.5 years. While
  it lasts, though, it makes its shell the most dangerous in LEO: about 1.1% a year chance of a
  lethal hit per satellite, ~5× normal.
- **A big breakup low down is recoverable.** A 2.2 t breakup at 480 km (like Cosmos-1408) raises
  local collision risk ~20%, and drag clears it within about a year.
- **The 700–1,100 km belt is the "bad neighborhood".** Fragments there last decades to a century
  and intact objects centuries. It is where the real debris belt lives (the Fengyun-1C,
  Cosmos–Iridium and Cosmos-1408 clouds, Envisat, decades of rocket bodies). Today the
  per-satellite risk peaks there at ~0.5% a year: the worst in LEO, but well below the ~2% a year
  at which operators abandon a band, and satellites fly there now.
- **The belt already grows on its own — slowly.** With no launches at all, its ≥10 cm population
  grows ~36% over 50 years (box ×1.36; the stochastic engines' 8-seed means are ×1.21 and ×1.18).
  Explosions set that rate: ×1.15 without them, ×2.1 if every one were a large stage blast. This
  matches NASA's LEGEND finding that LEO debris keeps growing even without new launches. Growing
  is not doomed: even if the per-satellite risk grew as fast as the ≥10 cm belt, it would reach
  only ~0.7% a year in 50 years, far below the ~2% walk-away line. And the growth is slow
  enough to control (see below).
- **Above ~1,000 km, debris is effectively permanent.** Even small fragments stay for centuries
  to millennia. This is where OneWeb (~1,200 km) and several proposed mega-constellations
  operate, so a low collision risk there *today* means "under-populated", not "safe".

## What actually tips LEO

Launch traffic into the belt, not any single object. Here a "launch" means one intact object left
at 900 km: 85% ~180 kg satellites and 15% ~2.4 t rocket bodies, none ever manoeuvred or deorbited,
so each is a derelict from day one. No ASAT is involved (an ASAT is a separate one-off breakup).
It is a stress test of disposal failure, not a forecast of useful traffic. Sustained launches into
900 km multiply the belt's ≥10 cm population ×2.8 at 50 a year, ×16 at 200 a year, and ×87 at 500
a year (with ~1,500–1,700 catastrophic collisions a year by year 50). The stochastic engines agree
on the direction but run somewhat milder: at 50 a year, discrete ×2.0 and cube ×2.2 (8-seed means;
seeds span ×1.6–2.7). Rational operators don't save it. At 500
launches a year they throttle back as the risk rises and stop entirely by year ~28, yet the belt
still doubles afterwards. The decision that governs LEO's long-term survival is how much mass is
placed into the high, un-cleaned bands and how little of it is removed. Against that, a barrel
of nails is a rounding error.

## What holds it flat

Removal and passivation. Taking the riskiest large derelicts out of orbit each year (the heaviest,
in the densest shells: NASA LEGEND's selection criterion) bends the belt. About 10 removals a year
hold its ≥10 cm population flat over 50 years with no new launches, and about 6 if old stages are
also made safe (vented, batteries discharged) so they can't explode, close to LEGEND's published
~5 a year. The stochastic engines confirm it: 10 removals a year gives discrete ×1.07 and cube
×1.02 (8-seed means). Launches raise the bill: with 50 derelicts a year added to the belt it takes
about 49 removals a year.
The belt is not doomed; it is a maintenance problem whose size is set by launch policy.

## What the model can and can't say

- **Main uncertainties.** The explosion rate and size set the no-launch baseline (belt ×1.15 with
  none, ×1.36 at the assumed ~4 a year, ×2.1 if all were large stage blasts). Whether small
  fragments' cratering impacts throw off new debris changes the 1–10 cm results 2–3×, but barely
  moves the ≥10 cm belt. Both are reported.
- **Not modelled.** Degradation debris (paint flakes, insulation), station-keeping and collision
  avoidance (beyond constellation formation-keeping), end-of-life disposal of launched objects
(each stays a derelict), the solar cycle. The modelled 1–10 cm field still thins over
  the horizon, which is why the ≥10 cm population, not the raw total, is the headline metric.
- **The engines agree.** Calibrated on the real catalog's orbit geometry (10,000 snapshots), the
  cube method's collision rate matches the box model's within ~5%, both overall (1.01×) and for
  catastrophic collisions (0.96×). Real geometry shifts some encounters from head-on to shallow
  crossings but barely changes the totals. One artifact had to go first: satellites flying in
  formation in one constellation plane (Starlink, OneWeb, …) share cubes without ever closing on
  each other, and the cube method counted ~26 phantom encounters a year among them; those pairs
  are now excluded.
- **Stochastic engines are noisy.** Single discrete or cube runs range ×0.8–×1.8 for the belt;
  quote their 8-seed means.
- **Scope.** Results are order-of-magnitude estimates from a stylised model, not operational
  forecasts.

---

*Generated from the SpaceWars model chain. See [README.md](README.md) for the code and how to
reproduce these results (`dotnet run --project src/SpaceWars.Cli -- --deck`).*
