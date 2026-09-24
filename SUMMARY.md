# SpaceWars — Study Summary

*Can a launched "barrel of nails" render low-Earth orbit unusable via Kessler syndrome?*

## The study

We set out to answer, quantitatively rather than rhetorically, whether launching a "barrel
of nails" into low-Earth orbit could render LEO unusable through Kessler syndrome. To do it
we built a full modeling chain grounded in real data: the live satellite catalog from
CelesTrak, per-object masses and cross-sections derived from Space-Track's radar-cross-section
records (100% coverage of the LEO set), GPU-accelerated J2 orbit propagation on an RTX 5090,
the NASA Standard Breakup Model for fragmentation, and three independent cascade engines of
increasing fidelity — an aggregate source–sink ODE, a discrete super-particle Monte Carlo, and
a conjunction "cube" model that resolves collisions by real orbit-crossing geometry. We added
ongoing launch traffic and an economically-rational "operators retreat" feedback so the
environment could behave like the real one.

The verdict is consistent across all three engines: **a barrel of nails is a genuine
mission-kill weapon and a persistent nuisance, but it neither triggers Kessler nor ends LEO.**
A single ~4 g nail is lethal to any satellite it strikes, yet at ~0.8 J/g it is far below the
40 J/g needed to *shatter* a 260 kg satellite — you'd need a ~200 g bolt for that — so the
barrel produces mission-kills and cratering debris, not the ~10⁴-fragment clouds that actually
drive a cascade. Those come from large-on-large collisions, which the barrel can only
*accelerate*, never *ignite*. To make nails themselves tip a band you'd need thousands of
barrels — thousands of tonnes of steel, more than a year of humanity's entire launch mass —
which is self-evidently absurd. The real Kessler risk is set by launch and removal policy,
against which a barrel is a rounding error.

## Altitude is the whole story

LEO is not one place; it is strongly stratified by altitude, and almost every conclusion flips
depending on where the barrel goes. **Below ~600 km** — the Starlink shell at 550 km, for
instance — atmospheric drag is a free janitor: a nail's orbital lifetime there is only about
1.2 years (as little as 4 months at solar maximum), so a barrel dumped low is a purely
*transient* threat that self-cleans within a couple of years and leaves essentially no lasting
trace. **At 800–1000 km — the "bad neighborhood"** — drag is feeble and lifetimes stretch to
decades and centuries. This is where the real debris belt actually lives (the Fengyun-1C,
Cosmos–Iridium, and Cosmos-1408 fragment clouds, plus decades of abandoned rocket bodies), and
our corrected model shows this band sitting *already marginally supercritical* at today's
population: it creeps upward on its own, with no new launches at all. A barrel placed here
persists for decades and leaves a fingerprint roughly six to seven times larger than the same
barrel at 550 km — and in a band that's already on the knife-edge, that extra mass gets
*amplified* by the runaway rather than harmlessly decaying.

## What the altitude view shows

The "usability by altitude" analysis makes this concrete: the per-satellite annual collision
probability peaks sharply at 800–1000 km, and that is the band that crosses the "orbit becomes
unusable" threshold first — immediately when launch traffic is added, and progressively over
the horizon even without it. A secondary persistent zone sits around 1200–1500 km, precisely
where several proposed mega-constellations (OneWeb, and the planned Chinese systems near
~1160 km) intend to operate, which is why that altitude choice matters far beyond any single
malicious payload. The overarching lesson is that low LEO is largely self-healing while high
LEO is nearly permanent, so risk — whether from a barrel of nails or from ordinary operations
— scales dramatically with altitude, and the decision that governs LEO's long-term survival is
not whether someone launches nails but *how much mass is placed into the high, un-cleaned bands
and how little of it is ever removed.*

## An honest caveat

From our calibration: the well-mixed box model actually *under*counts collisions by a factor
of several (it dilutes objects over the full sphere while real orbits concentrate in latitude
bands), which means its near-critical result is conservative — the true environment is, if
anything, somewhat more collisional than the headline numbers suggest. The conjunction model's
geometry is faithful, but its absolute rate is qualitative pending finer particle resolution.
Both engines bracket the real near-critical margin.

---

*Generated from the SpaceWars model chain. See [README.md](README.md) for the code and how to
reproduce these results.*
