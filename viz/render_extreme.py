"""Render the extreme-case visualization: the 800-1000 km debris belt today vs after a
launch-driven runaway (~65x debris). Synthetic populations on realistic LEO orbits."""
import os, math
import numpy as np
import matplotlib, matplotlib.transforms
matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = os.path.dirname(os.path.abspath(__file__))
RE = 6378.135
TWO_PI = 2 * math.pi

def make_positions(N, seed):
    rng = np.random.default_rng(seed)
    a = RE + rng.uniform(700, 1100, N)
    e = np.abs(rng.normal(0, 0.004, N))
    inc = np.radians(rng.choice([82, 98.7, 65, 74, 51, 86], N, p=[.22,.28,.14,.14,.12,.10]) + rng.normal(0, 3, N))
    raan = rng.uniform(0, TWO_PI, N); argp = rng.uniform(0, TWO_PI, N); Marr = rng.uniform(0, TWO_PI, N)
    E = Marr.copy()
    for _ in range(10):
        E = E - (E - e*np.sin(E) - Marr)/(1 - e*np.cos(E))
    nu = np.arctan2(np.sqrt(1-e*e)*np.sin(E), np.cos(E)-e)
    r = a*(1 - e*np.cos(E))
    cu, su = np.cos(argp+nu), np.sin(argp+nu); cO, sO = np.cos(raan), np.sin(raan); ci, si = np.cos(inc), np.sin(inc)
    x = r*(cu*cO - su*sO*ci); y = r*(cu*sO + su*cO*ci); z = r*(su*si)
    return x, z, y  # pole = Y

def earth(ax):
    u, v = np.mgrid[0:TWO_PI:60j, 0:math.pi:30j]
    ex = RE*np.cos(u)*np.sin(v); ey = RE*np.sin(u)*np.sin(v); ez = RE*np.cos(v)
    ax.plot_surface(ex, ez, ey, color="#0e2d4e", alpha=0.97, linewidth=0, shade=True, zorder=0)
    ax.plot_wireframe(ex, ez, ey, color="#2a4a6b", linewidth=0.3, alpha=0.3, rstride=6, cstride=6)

# Growth factor from the model export (CLI --deck): belt objects >=10 cm after 50 yr with 500 objects/yr left in it.
import json
_r = json.load(open(os.path.join(HERE, "..", "data", "deck_numbers.json"), encoding="utf-8"))["conventions"]["asWritten"]["responsive500"]
_g = _r["constantBelt"][-1] / _r["constantBelt"][0]
frames = [
    dict(name="extreme1", N=4000, seed=1, color="#7fa8d0", s=3.0, alpha=0.55, cmap=None,
         label="Today", sub=f"~{_r['constantBelt'][0]/1e3:.0f}k objects ≥10 cm in the belt"),
    dict(name="extreme2", N=60000, seed=2, color=None, s=1.3, alpha=0.35, cmap="autumn",
         label="After 50 yr", sub=f"500 satellites + rocket bodies a year,\nnever deorbited: ~{_g:.0f}× objects ≥10 cm"),
]

for k, fr in enumerate(frames, 1):
    x, y, z = make_positions(fr["N"], fr["seed"])
    fig = plt.figure(figsize=(9, 9), facecolor="#06080e")
    ax = fig.add_subplot(111, projection="3d"); ax.set_facecolor("#06080e")
    earth(ax)
    if fr["cmap"]:
        rr = np.sqrt(x*x + y*y + z*z) - RE
        ax.scatter(x, y, z, s=fr["s"], c=rr, cmap=fr["cmap"], vmin=700, vmax=1100,
                   alpha=fr["alpha"], depthshade=False, edgecolors="none")
    else:
        ax.scatter(x, y, z, s=fr["s"], c=fr["color"], alpha=fr["alpha"], depthshade=False, edgecolors="none")
    lim = 7900
    ax.set_xlim(-lim, lim); ax.set_ylim(-lim, lim); ax.set_zlim(-lim, lim)
    ax.set_box_aspect((1,1,1)); ax.set_axis_off(); ax.view_init(elev=20, azim=40)
    ax.text2D(0.04, 0.95, "LEO DEBRIS BELT · 700–1,100 km", transform=ax.transAxes,
              color="#4da6ff", fontsize=24, family="monospace", weight="bold")
    ax.text2D(0.04, 0.88, fr["label"], transform=ax.transAxes,
              color=("#FF5A52" if k == 2 else "#e7eef8"), fontsize=28, weight="bold")
    ax.text2D(0.04, 0.055, fr["sub"], transform=ax.transAxes, color="#9fb2cc", fontsize=20, family="monospace")
    out = os.path.join(HERE, "frames", f"{fr['name']}.png")
    # Fixed square crop (not a tight bbox): both frames come out the same square size, so a long caption
    # can't widen one and squash its globe when the slide fits it into a square box.
    fig.savefig(out, dpi=130, facecolor="#06080e",
                bbox_inches=matplotlib.transforms.Bbox([[0.85, 0.85], [8.15, 8.15]]))
    plt.close(fig)
    print("saved", out, "N=", fr["N"])
