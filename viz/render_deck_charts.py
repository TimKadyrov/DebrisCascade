"""Render the presentation's data charts from data/deck_numbers.json (CLI --deck).
Each figure is drawn at its exact slide-box size (inches) and 200 dpi, so font sizes in
points match the slide 1:1. White cards, deck palette."""
import json, os
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter

HERE = os.path.dirname(os.path.abspath(__file__))
D = json.load(open(os.path.join(HERE, "..", "data", "deck_numbers.json"), encoding="utf-8"))
OUT = os.path.join(HERE, "frames")
os.makedirs(OUT, exist_ok=True)

BLUE = "#3E93F0"; BLUE_FILL = "#4DA6FF"; RED = "#FF5A52"; ORANGE = "#E8792B"
AMBER = "#F2C14E"; INK = "#2A3646"; MUTE = "#7C8AA0"; GREEN = "#2BB673"
plt.rcParams.update({"font.family": "DejaVu Sans", "axes.edgecolor": INK, "axes.labelcolor": INK,
                     "xtick.color": INK, "ytick.color": INK})
CW = D["conventions"]["asWritten"]; LG = D["conventions"]["legend"]


def fig(w, h, rect):
    f = plt.figure(figsize=(w, h), dpi=200, facecolor="white")
    ax = f.add_axes(rect); ax.set_facecolor("white")
    for sp in ("top", "right"): ax.spines[sp].set_visible(False)
    return f, ax


def tag(f, text):
    """Small source line in the card's bottom-right corner: which engine and scenario drew it."""
    f.text(0.995, 0.012, text, ha="right", va="bottom", fontsize=7.5, color=MUTE, style="italic")


def save(f, name):
    p = os.path.join(OUT, name); f.savefig(p, dpi=200, facecolor="white"); plt.close(f); print("saved", p)


def kfmt(v, _=None):
    return f"{v/1e6:.1f}M" if v >= 1e6 else (f"{v/1e3:.0f}k" if v >= 1e3 else f"{v:.0f}")


# --- Slide 13: per-satellite hazard today by altitude + drag persistence -----------------
U = D["usability"]
alt = np.array(U["altKm"]); haz = np.array(U["hazardToday"]) * 100; hazB = np.array(U["hazardTodayWithBarrelAt550"]) * 100
fl = np.array(U["fragmentLifetimeYears"]); il = np.array(U["intactLifetimeYears"])
f, ax = fig(11.56, 3.89, [0.075, 0.15, 0.85, 0.80])
ax.axvspan(700, 1100, color=RED, alpha=0.08, lw=0)
ax.fill_between(alt, haz, color=BLUE_FILL, alpha=0.18, lw=0)
ax.plot(alt, haz, color=BLUE, lw=2.4)
ax.plot(alt, hazB, color=BLUE, lw=1.4, ls=(0, (4, 3)))
ax.set_xlim(200, 2000); ax.set_ylim(0, max(hazB.max(), haz.max()) * 1.18)
ax.set_xlabel("altitude (km)", fontsize=10); ax.set_ylabel("collision risk per satellite (%/yr)", fontsize=9.5, color=BLUE)
ax.tick_params(axis="y", colors=BLUE, labelsize=9); ax.tick_params(axis="x", labelsize=9)
pk = int(np.argmax(haz))
ax.annotate(f"today: peak {haz[pk]:.2f}%/yr at {alt[pk]:.0f} km", (alt[pk], haz[pk]), xytext=(alt[pk] + 90, haz[pk] + 0.12),
            fontsize=9.5, color=BLUE, weight="bold", arrowprops=dict(arrowstyle="-", color=BLUE, lw=0.8))
pb = int(np.argmax(hazB))
ax.annotate(f"with one barrel dumped at 550 km: {hazB[pb]:.1f}%/yr", (alt[pb], hazB[pb]), xytext=(alt[pb] + 45, hazB[pb] + 0.02),
            fontsize=9, color=BLUE, ha="left", va="center", arrowprops=dict(arrowstyle="-", color=BLUE, lw=0.8))
ax.text(900, ax.get_ylim()[1] * 0.97, "700–1,100 km belt", color=RED, fontsize=9.5, weight="bold", ha="center", va="top")
ax2 = ax.twinx()
for sp in ("top",): ax2.spines[sp].set_visible(False)
ax2.spines["right"].set_color(ORANGE)
ax2.plot(alt, fl, color=ORANGE, lw=2.4)
ax2.set_yscale("log"); ax2.set_ylim(0.05, 3e4)
ax2.yaxis.set_major_formatter(FuncFormatter(lambda v, _: {0.1: "0.1", 1: "1", 10: "10", 100: "100", 1000: "1k", 10000: ">10k"}.get(v, "")))
ax2.tick_params(axis="y", colors=ORANGE, labelsize=9)
ax2.set_ylabel("years for drag to remove a 3–10 cm fragment (log)", fontsize=9, color=ORANGE)
i1 = int(np.argmin(abs(alt - 1275)))
ax2.text(alt[i1], fl[i1] * 1.6, "3–10 cm fragment", color=ORANGE, fontsize=9, ha="right", weight="bold")
tag(f, "box model · today's catalog · drag lifetimes")
save(f, "deck_altitude.png")

# --- Slide 14: belt growth vs launch rate ------------------------------------------------
t = CW["tipping"]; rates = np.array(t["rates"]); gB = np.array(t["growthBelt"])
f, ax = fig(11.56, 3.89, [0.075, 0.16, 0.90, 0.78])
ax.plot(rates, gB, color=RED, lw=2.6, marker="o", ms=4)
ax.axhline(1, color=MUTE, lw=1, ls=(0, (4, 3)))
ax.set_yscale("log"); ax.set_xlim(0, 1000); ax.set_ylim(0.8, 700)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"×{v:g}"))
ax.set_xlabel("satellites + rocket bodies injected or added per year at 900 km, for 50 years  (85% / 15%; none manoeuvred or deorbited)", fontsize=10)
ax.set_ylabel("belt objects ≥10 cm after 50 yr", fontsize=9.5)
ax.tick_params(labelsize=9)
def gx(g): return f"{g:.1f}" if g < 10 else f"{g:.0f}"
for r, dx, dy in [(0, 90, 1.06), (50, 45, 0.8), (200, 25, 0.75), (500, 25, 0.75), (1000, -20, 1.25)]:
    g = gB[list(rates).index(r)]
    label = f"{r}/yr → ×{gx(g)}" if r else f"nothing added → ×{g:.2f}"
    ax.annotate(label, (r, g), xytext=(r + dx, g * dy), fontsize=9.5, color=RED, weight="bold",
                ha="right" if dx < 0 else "left", arrowprops=dict(arrowstyle="-", color=RED, lw=0.7))
ax.text(990, 1.06, "×1 = no growth", color=MUTE, fontsize=8.5, ha="right", va="bottom")
tag(f, "box model · 50 years · satellites + rocket bodies injected at 900 km, never deorbited")
save(f, "deck_tipping.png")

# --- Slide 15: constant vs responsive launch at 500/yr -----------------------------------
r5 = CW["responsive500"]; yrs = np.array(r5["years"])
f, ax = fig(11.0, 3.75, [0.08, 0.15, 0.88, 0.78])
q = r5["operatorsQuitYear"]
ax.axvspan(q, 50, color=MUTE, alpha=0.08, lw=0)
ax.plot(yrs, r5["constantBelt"], color=RED, lw=2.4, label="500 satellites + rocket bodies/yr injected, never deorbited")
ax.plot(yrs, r5["responsiveBelt"], color=BLUE, lw=2.4, label="responsive: operators throttle, then quit")
ax.axvline(q, color=MUTE, lw=1, ls=(0, (4, 3)))
ax.set_yscale("log"); ax.set_xlim(0, 50)
ax.yaxis.set_major_formatter(FuncFormatter(kfmt))
ax.set_xlabel("years", fontsize=10); ax.set_ylabel("belt objects ≥10 cm", fontsize=9.5); ax.tick_params(labelsize=9)
ax.text(q + 0.6, ax.get_ylim()[0] * 1.15, f"operators stop launching (yr {q:.0f})", color=MUTE, fontsize=9, va="bottom")
ax.text(49.5, r5["constantBelt"][-1] * 0.8, f"×{r5['constantBelt'][-1] / r5['constantBelt'][0]:.0f}", color=RED, fontsize=11, weight="bold", ha="right", va="top")
ax.text(49.5, r5["responsiveBelt"][-1] * 0.62, f"still ×{r5['growthAfterQuitBelt']:.1f} after they quit", color=BLUE, fontsize=10, weight="bold", ha="right", va="top")
ax.legend(loc="upper left", fontsize=9, frameon=False)
tag(f, "box model · 500 injected/yr at 900 km")
save(f, "deck_responsive.png")

# --- Extreme case: belt under 500 objects/yr added ------------------------------
f, ax = fig(5.78, 2.5, [0.14, 0.2, 0.83, 0.72])
cb = np.array(r5["constantBelt"])
ax.fill_between(yrs, cb, color=RED, alpha=0.15, lw=0); ax.plot(yrs, cb, color=RED, lw=2.2)
ax.set_yscale("log"); ax.set_xlim(0, 50)
ax.yaxis.set_major_formatter(FuncFormatter(kfmt)); ax.tick_params(labelsize=8)
ax.set_xlabel("years", fontsize=8.5); ax.set_ylabel("belt objects ≥10 cm", fontsize=8)
ax.text(1, cb[0] * 1.9, f"{kfmt(cb[0])} today", fontsize=8, color=INK, va="bottom")
ax.text(49, cb[-1] * 0.7, kfmt(cb[-1]), fontsize=9, color=RED, weight="bold", ha="right", va="top")
tag(f, "box model · 500 satellites + rocket bodies injected/yr at 900 km, never deorbited")
save(f, "deck_extreme.png")

# --- Slide 19: extra risk from a big low-altitude breakup ---------------------------------
L = D["lowEvent"]; mo = np.array(L["months"]); ex = (np.array(L["hazard"]) - np.array(L["baselineHazard"])) * 100
f, ax = fig(5.79, 2.36, [0.14, 0.21, 0.83, 0.7])
ax.fill_between(mo, ex, color=GREEN, alpha=0.2, lw=0); ax.plot(mo, ex, color=GREEN, lw=2.2)
ax.set_xlim(0, 36); ax.set_ylim(0, ex.max() * 1.25)
ax.set_xticks([0, 6, 12, 24, 36]); ax.tick_params(labelsize=8)
ax.set_xlabel("months after the breakup", fontsize=8.5); ax.set_ylabel("extra risk (%/yr)", fontsize=8)
rel = ex[0] / (L["baselineHazard"][0] * 100) * 100   # ex is already in %/yr
ax.text(1, ex[0] * 1.02, f"+{ex[0]:.3f}%/yr (+{rel:.0f}%)", fontsize=8.5, color=GREEN, weight="bold", va="bottom")
tag(f, "box model · 2.2 t breakup at 480 km")
save(f, "deck_lowevent.png")

# --- Removal slide: belt growth vs removals/yr ---------------------------------------------
R = D["removal"]; rr = np.array(R["removalsPerYear"])
f, ax = fig(11.56, 3.89, [0.075, 0.16, 0.90, 0.78])
ax.axhline(1, color=MUTE, lw=1, ls=(0, (4, 3)))
ax.plot(rr, R["beltGrowthAt50Launches"], color=RED, lw=2.6, marker="o", ms=4, label="50 satellites + rocket bodies/yr injected, never deorbited")
ax.plot(rr, R["beltGrowthNoLaunches"], color=GREEN, lw=2.6, marker="o", ms=4, label="nothing added")
ax.set_xlim(0, 100); ax.set_ylim(0, max(R["beltGrowthAt50Launches"]) * 1.12)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"×{v:g}"))
ax.set_xlabel("large dead objects removed per year (riskiest first)", fontsize=10)
ax.set_ylabel("belt objects ≥10 cm after 50 yr", fontsize=9.5); ax.tick_params(labelsize=9)
for x, y, c, lab, dy in [(R["toHoldFlatNoLaunches"], 1, GREEN, f"~{R['toHoldFlatNoLaunches']:.0f}/yr holds it flat", -0.4),
                         (R["toHoldFlatAt50Launches"], 1, RED, f"~{R['toHoldFlatAt50Launches']:.0f}/yr with 50/yr injected", 0.95)]:
    ax.plot([x], [y], "o", ms=9, mfc="white", mec=c, mew=2, zorder=5)
    ax.annotate(lab, (x, y), xytext=(x + 3, y + dy), fontsize=9.5, color=c, weight="bold",
                arrowprops=dict(arrowstyle="-", color=c, lw=0.7))
ax.text(99, 1.04, "×1 = held flat", color=MUTE, fontsize=8.5, ha="right", va="bottom")
ax.legend(loc="upper right", fontsize=9, frameon=False)
tag(f, "box model · 50 years · removals from year 0")
save(f, "deck_removal.png")

# --- For comparison: every source on one measure (extra belt objects >=10 cm after 50 yr vs adding nothing) ---
bb = CW["baseline"]["beltTrackable"]; b0, bEnd = bb[0], bb[-1]
gb = dict(zip(CW["tipping"]["rates"], CW["tipping"]["growthBelt"])); BR = CW["barrels"]
YARD = [("1 barrel of nails (832 kg)", (BR["ratioBelt"][0] - 1) * bEnd, AMBER),
        ("1 ASAT strike on a 1 t satellite", (CW["asat"]["ratioBelt"] - 1) * bEnd, ORANGE),
        ("any number of barrels (ceiling)", (max(BR["ratioBelt"]) - 1) * bEnd, AMBER),
        ("50 satellites + rocket bodies a year\ninjected, never deorbited", gb[50] * b0 - bEnd, RED),
        ("500 satellites + rocket bodies a year\ninjected, never deorbited", gb[500] * b0 - bEnd, RED)]
f, ax = fig(11.56, 3.89, [0.30, 0.16, 0.66, 0.80])
ys = np.arange(len(YARD))[::-1]
ax.barh(ys, [v for _, v, _ in YARD], color=[c for _, _, c in YARD], height=0.62)
ax.set_xscale("log"); ax.set_xlim(5, 5e6)
ax.set_yticks(ys); ax.set_yticklabels([n for n, _, _ in YARD], fontsize=10)
ax.xaxis.set_major_formatter(FuncFormatter(kfmt)); ax.tick_params(axis="x", labelsize=9)
ax.tick_params(axis="y", length=0); ax.spines["left"].set_visible(False)
ax.set_xlabel(f"extra objects ≥10 cm in the 700–1,100 km belt after 50 years  (vs adding nothing: {kfmt(bEnd)})", fontsize=10)
for y, (_, v, c) in zip(ys, YARD):
    ax.text(v * 1.25, y, f"+{float(f'{v:.2g}'):,.0f}", va="center", fontsize=10, weight="bold", color=INK)
tag(f, "box model · everything at 900 km (ASAT 865 km)")
save(f, "deck_comparison.png")

# --- Working satellites: disposal + avoidance vs never deorbited (belt debris after 50 yr) ---------
if "working" in D:
    W = D["working"]; rates = W["rates"]; sw = {s["setting"]: s for s in W["sweep"]}
    order = [("neverDeorbited", "never deorbited", RED), ("poor", "poor: 70% / 50% / 50%", ORANGE),
             ("baseline", "today's practice: 90% / 80% / 90%", BLUE), ("best", "best: 99% / 95% / 99%", GREEN)]
    groups = [r for r in rates if r > 0]
    f, ax = fig(11.56, 3.89, [0.075, 0.2, 0.90, 0.74])
    bw = 0.19
    for gi, rate in enumerate(groups):
        ri = rates.index(rate)
        for k, (key, lab, col) in enumerate(order):
            g = sw[key]["runs"][ri]["growth"]; x = gi + (k - 1.5) * bw
            ax.bar(x, g, width=bw * 0.92, color=col, label=lab if gi == 0 else None)
            ax.text(x, g * 1.08, f"×{g:.1f}" if g < 10 else f"×{g:.0f}", ha="center", va="bottom", fontsize=9.5, weight="bold", color=INK)
            if key == "baseline":
                cb = next((c for c in W["cubeBaseline"] if c["launchesPerYear"] == rate), None)
                if cb:
                    v = np.array(cb["conjunction"]); m = v.mean()
                    ax.errorbar(x + bw * 0.28, m, yerr=[[m - v.min()], [v.max() - m]], fmt="D", ms=4.5, color=INK, mfc="white",
                                capsize=3, lw=1, label="cube cross-check (8 seeds)" if gi == 0 else None)
    base0 = sw["neverDeorbited"]["runs"][rates.index(0)]["growth"]
    ax.axhline(base0, color=MUTE, lw=1, ls=(0, (4, 3)))
    ax.text(-0.92, base0 * 1.04, f"nothing added ×{base0:.2f}", color=MUTE, fontsize=8.5, ha="left", va="bottom")
    ax.set_yscale("log"); ax.set_ylim(0.8, 200)
    ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"×{v:g}"))
    ax.set_xticks(range(len(groups)))
    ax.set_xticklabels([f"{r:.0f} satellites + rocket bodies a year at 900 km" for r in groups], fontsize=10)
    ax.set_xlim(-0.95, len(groups) - 0.45)
    ax.set_ylabel("belt debris ≥10 cm after 50 yr", fontsize=9.5); ax.tick_params(axis="y", labelsize=9)
    h, l = ax.get_legend_handles_labels(); o = [i for i, x in enumerate(l) if not x.startswith("cube")] + [i for i, x in enumerate(l) if x.startswith("cube")]
    ax.legend([h[i] for i in o], [l[i] for i in o], loc="upper left", fontsize=8.5, frameon=False,
              title="deorbited satellites / rocket bodies / conjunctions avoided", title_fontsize=8.5, ncol=1)
    tag(f, "box model · 50 years · vs today's belt objects ≥10 cm; working satellites not counted as debris")
    save(f, "deck_working.png")

# --- NASA benchmark: the 1 Jan 2006 catalog, no launches, 200 years, vs LEGEND (Liou & Johnson 2006) ---------
BP = os.path.join(HERE, "..", "data", "benchmark_2006.json")
if os.path.exists(BP):
    B = json.load(open(BP, encoding="utf-8")); runs = {r["Name"]: r for r in B["runs"]}
    old = runs.get("before: two intact mass classes, mean-altitude shells"); new = runs.get("finer masses + eccentric orbits")
    cubes = [r for r in B["runs"] if r["Name"].startswith("cube engine")]
    def g50(r):
        y = r["Years"]; k = min(range(len(y)), key=lambda i: abs(y[i] - 50)); return (r["LeoTrackable"][k] / r["LeoTrackable"][0] - 1) * 100
    f, axs = plt.subplots(1, 2, figsize=(5.4, 2.35), dpi=200, facecolor="white")
    f.subplots_adjust(left=0.1, right=0.98, top=0.8, bottom=0.2, wspace=0.45)
    labels = ["NASA\nLEGEND", "old\nbox", "box", "cube"]
    for ax, (title, legend_v, fn) in zip(axs, [("catastrophic collisions\nin 200 years", 10.8, lambda r: r["CatastrophicTotal"]),
                                                ("≥10 cm objects in LEO,\nchange after 50 years (%)", 0.0, g50)]):
        for sp in ("top", "right"): ax.spines[sp].set_visible(False)
        vals = [legend_v, fn(old), fn(new), np.mean([fn(c) for c in cubes]) if cubes else np.nan]
        cols = [GREEN, MUTE, BLUE, ORANGE]
        ax.bar(range(4), vals, color=cols, width=0.62)
        if cubes:
            cv = [fn(c) for c in cubes]
            ax.errorbar(3, np.mean(cv), yerr=[[np.mean(cv) - min(cv)], [max(cv) - np.mean(cv)]], fmt="none", ecolor=INK, capsize=3, lw=1)
        for k, v in enumerate(vals):
            if np.isfinite(v):
                ax.text(k - (0.22 if k == 3 else 0), max(v, 0) + (0.02 * max(abs(x) for x in vals if np.isfinite(x)) + 0.3), f"{v:.1f}" if k != 0 or title.startswith("cat") else "≈0",
                        ha="center", va="bottom", fontsize=7.5, weight="bold", color=INK)
        ax.set_xticks(range(4)); ax.set_xticklabels(labels, fontsize=7); ax.tick_params(axis="y", labelsize=7)
        ax.set_title(title, fontsize=8, color=INK); ax.axhline(0, color=MUTE, lw=0.8)
    tag(f, f"1 Jan 2006 catalog, no launches or explosions, ≥10 cm only · cube: {len(cubes)} seeds")
    save(f, "deck_benchmark.png")
