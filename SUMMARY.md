# DebrisCascade — Study Summary

*Can a launched "barrel of nails" render low-Earth orbit unusable via Kessler syndrome?*

Every number below comes from one scripted run (`DebrisCascade.Cli --deck` → `data/deck_numbers.json`)
on the live Space-Track catalog. Growth figures are over 50 years. Scenario results are means of
16 cube-engine runs (different random seeds); effects smaller than that run-to-run scatter (a
barrel, an ASAT strike) come from the deterministic box model.

## The study

We set out to answer, quantitatively rather than rhetorically, whether launching a "barrel of
nails" into low-Earth orbit could render LEO unusable through Kessler syndrome. A barrel is the
cheapest way to put a debris weapon in orbit: no guidance, no interceptor, just mass and a
dispenser. That makes it worth a number rather than an opinion. The modeling
chain is grounded in real data. It uses every object currently on orbit from Space-Track: 29,779
in LEO, comprising 16,916 payloads, 9,956 catalogued debris pieces, 1,573 rocket bodies and 1,334
unidentified objects, 97% with radar cross-sections. Those cross-sections give per-object masses and
areas. Orbits are propagated with GPU-accelerated J2 on an RTX 5090. Fragmentation follows the
NASA Standard Breakup Model, including its own area-to-mass ratios for fragments. Three
independent cascade engines run the evolution: an aggregate source–sink ODE (the box model), a
discrete super-particle Monte Carlo, and a conjunction "cube" model that propagates every object
on the GPU and resolves collisions by real orbit-crossing geometry. The cube engine draws the
scenario results; the box model resolves the barrel-sized effects; the discrete engine checks
both. We added ongoing launch traffic, an economically-rational "operators retreat" feedback, and
explosions of rocket bodies and dead satellites (~4 a year, average size) as a source of debris
besides collisions. The headline metric is the standard one for Kessler studies: the population of
objects **≥10 cm**, reported both for all of LEO and for the 700–1,100 km belt.

The verdict: **a barrel of nails is a genuine mission-kill weapon, but it neither triggers
Kessler nor ends LEO.** A single ~4 g nail is lethal to any satellite it strikes, yet at ~0.8 J/g
it is far below the 40 J/g needed to *shatter* a 260 kg satellite; you'd need a ~200 g bolt for
that. The ~10⁴-fragment clouds that drive a cascade come from large-on-large collisions:
260 kg on 260 kg makes ~29,000 fragments ≥1 cm. Nails instead flood the lethal-but-untrackable
1–10 cm field. Dumped at 900 km, ~90–230 barrels double that field within 50 years (the range
brackets how much debris small impacts throw off: the breakup model as written vs NASA's LEGEND
convention). But no number of barrels doubles the ≥10 cm belt: 300 barrels put it ~21% above the
no-barrel run after 50 years and 1,000 barrels ~25%, and the curve is flattening. One ASAT strike
on a 1 t satellite at 865 km adds as many ≥10 cm objects as ~22 barrels.

## For comparison

A barrel's effect only means something next to what else moves the belt, so every source is
put on one metric: extra objects ≥10 cm in the 700–1,100 km belt after 50 years, compared with
adding nothing (~9,800 in the box model).

| Source (at 900 km; ASAT at 865 km) | Extra belt objects ≥10 cm |
|---|---|
| 1 barrel of nails (832 kg) | ~17 |
| 1 ASAT strike on a 1 t satellite | ~360 |
| any number of barrels (ceiling) | ~2,500 |
| 50 satellites + rocket bodies a year, never deorbited | ~11,000 |
| 500 satellites + rocket bodies a year, never deorbited | ~710,000 |

That is why the rest of this summary is about traffic rather than the barrel: traffic is the
scale the barrel has to be judged against, and on it the barrel is small.

## Altitude is the whole story

LEO is strongly stratified by altitude, and almost every conclusion flips with it.

- **Below ~600 km, drag is a free janitor.** Fragments rain out in months to a few years and dead
  satellites within about a decade. A barrel at 550 km loses half its nails in ~1.5 years. While
  it lasts, though, it makes its shell the most dangerous in LEO: about 1.1% a year chance of a
  lethal hit per satellite, ~5× normal.
- **A big breakup low down is recoverable.** A 2.2 t breakup at 480 km (like Cosmos-1408) raises
  local collision risk ~16%, and drag clears it within about a year.
- **The 700–1,100 km belt is the "bad neighborhood".** Fragments there last decades to a century
  and intact objects centuries. It is where the real debris belt lives (the Fengyun-1C,
  Cosmos–Iridium and Cosmos-1408 clouds, Envisat, decades of rocket bodies). Today the
  per-satellite risk peaks there at ~0.5% a year: the worst in LEO, but well below the ~2% a year
  at which operators abandon a band, and satellites fly there now. NASA's LEGEND model puts ~60%
  of future catastrophic collisions at 900–1,000 km (Liou & Johnson 2006).
- **The belt already grows on its own — slowly.** With no launches at all, its ≥10 cm population
  grows ~19% over 50 years (cube ×1.19, 16 runs from ×0.90 to ×1.56; box and discrete ×1.21).
  Explosions set that rate: ×1.02 without them, ×1.46 if every one were a large stage blast. This
  matches NASA's LEGEND finding that LEO debris keeps growing even without new launches. Growing
  is not doomed: even if the per-satellite risk grew as fast as the ≥10 cm belt, it would reach
  only ~0.6% a year in 50 years, far below the ~2% walk-away line. And the growth is slow
  enough to control (see below).
- **Above ~1,000 km, debris is effectively permanent.** Even small fragments stay for centuries
  to millennia. This is where OneWeb (~1,200 km) and several proposed mega-constellations
  operate, so a low collision risk there *today* means "under-populated", not "safe".

## What actually tips LEO

Launch traffic into the belt, not any single object. Here a "launch" means one intact object injected
or added at 900 km: 85% ~180 kg satellites and 15% ~2.4 t rocket bodies, none ever manoeuvred or
deorbited. No ASAT is involved (an ASAT is a separate one-off breakup).
It is a stress test of disposal failure, not a forecast of useful traffic. Sustained launches into
900 km multiply the belt's ≥10 cm population ×2.6 at 50 a year, ×18 at 200 a year, and ×59 at 500
a year (with ~350 catastrophic collisions a year by year 50, a count that includes 1–10 cm
fragments shattering small objects). The other engines agree: at 50 a year the box model gives
×2.6 and the discrete engine ×2.0 (8 runs); the box model runs hotter at high traffic (×88 at 500
a year). Rational operators don't save it. At 500 launches a year they throttle back as the risk
rises and stop entirely by year ~27 (20–32 across runs), yet the belt still grows ×2.8 afterwards.
The decision that governs LEO's long-term survival is how much mass is placed into the high,
un-cleaned bands and how little of it is removed. Against that, a barrel of nails is a rounding
error.

## What if satellites work and deorbit

Every traffic number above treats each added object as dead from day one: a stress test of
disposal failure. With working satellites instead, each one works for 5 years, holding its orbit
and dodging tracked (≥10 cm) objects, and is then deorbited. The same 500 a year at 900 km then
grows the belt's ≥10 cm debris **×4.6 instead of ×59** under today's practice:

- 90% of satellites deorbited at end of life (the NASA/FCC/IADC benchmark; SpaceX reports over 99%)
- 80% of rocket bodies disposed of (ESA Space Environment Report 2025)
- 90% of conjunctions with tracked objects avoided, by the 89% of satellites that can manoeuvre
  (McDowell, Sep 2026). Nothing dodges the 1–10 cm field, and a hit there leaves a dead satellite.

| Added a year at 900 km | Never deorbited | Poor (70/50/50) | Today's practice (90/80/90) | Best (99/95/99) |
|---|---|---|---|---|
| 50 | ×2.6 | ×1.3 | ×1.1 | ×1.04 |
| 500 | ×59 | ×18 | ×4.6 | ×1.4 |

(Belt debris ≥10 cm after 50 years over today's ~8,000 belt objects, 16-run means; ×1.19 with
nothing added.) Disposal is the lever: at 500 a year, disposal alone gives ×5.4, avoidance alone
×53, because what feeds the cascade is dead satellites left behind, and avoidance can't prevent
those. Today's practice at 500 a year spans ×3.6–×5.8 across runs; the box model gives ×4.1.

## What holds it flat

Removal and passivation. Taking the riskiest large dead objects out of orbit each year (the heaviest,
in the densest shells: NASA LEGEND's selection criterion) bends the belt. About 10 removals a year
hold its ≥10 cm population flat over 50 years with no new launches, and about 2 if old stages are
also made safe (vented, batteries discharged) so they can't explode; LEGEND's published figure is
~5 a year. The other engines confirm it: 10 removals a year gives box ×0.99 and discrete ×1.07.
Launches raise the bill: with 50 satellites and rocket bodies a year injected into the belt (never
deorbited) it takes about 60 removals a year.
The belt is not doomed; it is a maintenance problem whose size is set by launch policy.

## What the model can and can't say

- **Main uncertainties.** The explosion rate and size set the no-launch baseline (belt ×1.02 with
  none, ×1.19 at the assumed ~4 a year, ×1.46 if all were large stage blasts). Whether small
  fragments' cratering impacts throw off new debris changes the 1–10 cm results 2–3×, but barely
  moves the ≥10 cm belt. Both are reported.
- **Disposal reliability** sets the traffic outcome: at 500 a year the belt grows ×1.4 (99% of
  satellites deorbited) to ×18 (70%). See "What if satellites work and deorbit".
- **Chance.** Single cube runs of the same case differ widely (×0.90–×1.56 for the no-launch
  belt), so every scenario figure is a 16-run mean, with the run range shaded on the charts.
- **Not modelled.** Degradation debris (paint flakes, insulation) and the solar cycle. Working
  satellites that dodge and deorbit appear only in the section above; every other traffic result
  treats each added object as dead from day one. The modelled 1–10 cm field still thins over
  the horizon, which is why the ≥10 cm population, not the raw total, is the headline metric.
- **Checked against NASA: the 2006 benchmark.** NASA's LEGEND study (Liou & Johnson 2006,
  *Science*) projected LEO from the 1 January 2006 catalog with no launches and no explosions for
  200 years, counting ≥10 cm objects only: 10.8 catastrophic collisions, ~60% of them at
  900–1,000 km, and a population roughly flat for ~50 years before it rises. We reran it from the
  same starting point (the 2006 catalog, 7,182 LEO objects, from Space-Track's history). The cube
  engine gives 11.5 catastrophic collisions (5–17 across 16 runs), ~45% at 900–1,000 km, and
  +10% in LEO at 50 years; the box model 9.9 collisions and +15%. Removing ~5 large objects a year
  stabilises LEO in LEGEND (Liou, Johnson & Hill 2010; Liou 2011: regular launches, 90%
  end-of-life disposal, no explosions, 200 years); here ~2 hold the belt flat with explosions
  prevented, ~10 without.
- **The engines agree.** With nothing added, all three grow the belt ×1.19–×1.21. Real orbit
  geometry needed one fix first: satellites flying in formation in one constellation plane
  (Starlink, OneWeb, …) share cubes without ever closing on each other, and the cube method
  counted ~26 phantom encounters a year among them; those pairs are excluded.
- **Scope.** Results are order-of-magnitude estimates from a stylised model, not operational
  forecasts.

---

*Generated from the DebrisCascade model chain. See [README.md](README.md) for the code and how to
reproduce these results (`dotnet run --project src/DebrisCascade.Cli -- --deck`).*
