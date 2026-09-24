"""Render the 'usability by altitude' figure with TWO curves on one altitude axis:
  - collision hazard TODAY (density-driven, non-monotonic; peaks at the 800-1000 km belt)
  - debris PERSISTENCE / cleanup time (physics-driven, monotonic; rises without limit)
The point: the hazard curve's fall-off above ~1000 km is 'under-populated today', NOT 'safe'.
Persistence keeps rising, so debris broken high up is effectively permanent.
White card sized to drop into the deck slide (2048x689, ~2.97:1)."""
import os
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager as fm

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "frames", "altitude_persistence.png")

BLUE = "#3E93F0"; BLUE_FILL = "#4DA6FF"; RED = "#FF5A52"; AMBER = "#F2C14E"
ORANGE = "#E8792B"; DOT = "#F5B72E"; INK = "#2A3646"; MUTE = "#7C8AA0"

# --- hazard TODAY: piecewise-linear control points matching the deck's existing curve ---
hz_x = [200, 300, 430, 540, 560, 650, 730, 780, 820, 875, 940, 1000, 1015,
        1080, 1160, 1230, 1300, 1400, 1450, 1520, 1650, 1780, 1850, 1950, 2000]
hz_y = [0.06, 0.11, 0.22, 0.33, 0.35, 0.50, 0.63, 0.66, 0.75, 0.84, 0.81, 0.73, 0.70,
        0.55, 0.42, 0.33, 0.36, 0.45, 0.47, 0.42, 0.30, 0.20, 0.16, 0.11, 0.09]

# --- persistence / cleanup time: monotonic, log-like rise (days -> permanent) ---
ps_x = [200, 400, 550, 700, 800, 900, 1000, 1150, 1300, 1500, 1700, 1900, 2000]
ps_y = [0.03, 0.12, 0.22, 0.34, 0.44, 0.54, 0.63, 0.73, 0.81, 0.88, 0.93, 0.96, 0.97]

# NOTE: no absolute "unusable" line on this figure — the hazard curve is a *relative*
# shape, and today's per-sat hit rate at the 800-1000 km peak is far below the ~2%/yr
# operator walk-away threshold. That absolute crossing is a runaway effect (tipping slide),
# not a present-day state, so drawing it here would falsely imply "already unusable".

fig = plt.figure(figsize=(2048 / 200, 689 / 200), dpi=200, facecolor="white")
ax = fig.add_axes([0.055, 0.135, 0.935, 0.85])
ax.set_facecolor("white")
ax.set_xlim(160, 2040); ax.set_ylim(0, 1.06)

# zones (behind everything)
ax.axvspan(1000, 2040, color=ORANGE, alpha=0.05, lw=0, zorder=0)
ax.axvspan(800, 1000, color=RED, alpha=0.10, lw=0, zorder=1)

# hazard area + line
ax.fill_between(hz_x, hz_y, color=BLUE_FILL, alpha=0.16, zorder=2)
ax.plot(hz_x, hz_y, color=BLUE, lw=3.2, solid_capstyle="round", zorder=4)

# persistence line (thick solid, distinct)
ax.plot(ps_x, ps_y, color=ORANGE, lw=3.4, solid_capstyle="round", zorder=5)

# peak dot
ax.plot([875], [0.84], "o", ms=11, mfc=DOT, mec="white", mew=2, zorder=6)

# --- baked labels ---
def txt(x, y, s, color, size, weight="normal", ha="left", va="center", style="normal"):
    ax.text(x, y, s, color=color, fontsize=size, fontweight=weight, ha=ha, va=va,
            style=style, zorder=7)

txt(250, 0.55, "collision hazard\ntoday", BLUE, 12.5, "bold")
txt(1560, 0.72, "debris persistence\n(cleanup time)", ORANGE, 12.5, "bold", ha="center")
txt(905, 1.00, "800–1000 km", RED, 11.5, "bold", ha="center", va="top")
txt(905, 0.935, "most hazardous · near-critical", RED, 9.5, ha="center", va="top")
txt(905, 0.885, "(still used today)", RED, 8.5, ha="center", va="top", style="italic")
txt(1150, 0.585, "centuries", ORANGE, 9.5, ha="center", va="center")
txt(1760, 0.845, "≈ permanent", ORANGE, 9.5, ha="center", va="center")
txt(1520, 0.035, "no natural cleanup above ~1000 km", "#B79A6A", 9, ha="center", va="bottom")

# axis cosmetics
for sp in ("top", "right"):
    ax.spines[sp].set_visible(False)
for sp in ("left", "bottom"):
    ax.spines[sp].set_color(INK); ax.spines[sp].set_linewidth(1.3)
ax.set_yticks([])
ticks = [200, 500, 800, 1000, 1400, 1700, 2000]
ax.set_xticks(ticks)
ax.set_xticklabels([str(t) for t in ticks], color=INK, fontsize=10)
ax.tick_params(axis="x", length=5, color=INK)
ax.set_xlabel("altitude (km)", color=INK, fontsize=11, labelpad=4)
ax.set_ylabel("relative level  (two independent scales)", color=MUTE, fontsize=9.5, labelpad=6)

fig.savefig(OUT, dpi=200, facecolor="white")
plt.close(fig)
print("saved", OUT)
