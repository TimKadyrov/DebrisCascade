"""Render orbit-visualization frames of the nail-cloud decay from the model's scene export.
Reads data/scene.json (mean elements), propagates with J2 secular + exponential-atmosphere
drag (same physics as the interactive globe), and saves 3D snapshots at several epochs."""
import json, os, math
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = os.path.dirname(os.path.abspath(__file__))
scene = json.load(open(os.path.join(HERE, "..", "data", "scene.json")))
M = scene["meta"]
RE, MU, J2 = M["earthRadiusKm"], M["muKm3S2"], M["j2"]
TWO_PI = 2 * math.pi
Cd = 2.2

DTAB = np.array([(0,1.225,7.249),(150,2.070e-9,22.523),(180,5.464e-10,29.740),(200,2.789e-10,37.105),
    (250,7.248e-11,45.546),(300,2.418e-11,53.628),(350,9.518e-12,53.298),(400,3.725e-12,58.515),
    (450,1.585e-12,60.828),(500,6.967e-13,63.822),(600,1.454e-13,71.835),(700,3.614e-14,88.667),
    (800,1.170e-14,124.64),(900,5.245e-15,181.05),(1000,3.019e-15,268.00)])

def density(alt, solar=1.0):
    alt = np.clip(alt, 0, None)
    idx = np.clip(np.searchsorted(DTAB[:,0], alt, side="right") - 1, 0, len(DTAB)-1)
    h0, r0, H = DTAB[idx,0], DTAB[idx,1], DTAB[idx,2]
    rho = r0 * np.exp(-(alt - h0) / H)
    return np.where(alt >= 150, rho * solar, rho)

def load(rows):
    a = np.array([r[0] for r in rows]); e = np.array([r[1] for r in rows]); inc = np.array([r[2] for r in rows])
    raan0 = np.array([r[3] for r in rows]); argp0 = np.array([r[4] for r in rows]); m0 = np.array([r[5] for r in rows])
    n = np.sqrt(MU / a**3); p = a*(1-e*e); rp = RE/p
    f = 1.5*J2*rp*rp*n; s2 = np.sin(inc)**2
    raanD = -f*np.cos(inc); argpD = f*(2-2.5*s2); mD = n + f*np.sqrt(1-e*e)*(1-1.5*s2)
    return dict(a=a.copy(), a0=a.copy(), e=e, inc=inc, raan0=raan0, argp0=argp0, m0=m0,
                n0=n, raanD=raanD, argpD=argpD, mD=mD)

def kepler(Marr, e):
    Mw = np.mod(Marr, TWO_PI); E = np.where(e < 0.8, Mw, math.pi)
    for _ in range(12):
        E = E - (E - e*np.sin(E) - Mw)/(1 - e*np.cos(E))
    return E

def positions(o, tsec):
    raan = o["raan0"] + o["raanD"]*tsec; argp = o["argp0"] + o["argpD"]*tsec; Mn = o["m0"] + o["mD"]*tsec
    E = kepler(Mn, o["e"])
    nu = np.arctan2(np.sqrt(1-o["e"]**2)*np.sin(E), np.cos(E)-o["e"])
    r = o["a"]*(1 - o["e"]*np.cos(E))
    cu, su = np.cos(argp+nu), np.sin(argp+nu); cO, sO = np.cos(raan), np.sin(raan); ci, si = np.cos(o["inc"]), np.sin(o["inc"])
    x = r*(cu*cO - su*sO*ci); y = r*(cu*sO + su*cO*ci); z = r*(su*si)
    return x, z, y, r  # pole = Y (matches globe)

def decay_to(o, aoverm, days):
    a = o["a0"].copy(); alive = np.ones(len(a), bool)
    for _ in range(int(days)):
        alt = a - RE
        rate = -Cd*aoverm*density(alt)*np.sqrt(MU*1e9 * a*1000)/1000.0 * 86400.0  # km/day (<0)
        a = a + rate
        alive &= a > (RE + 120)
        a = np.where(alive, a, RE + 119)
    o["a"] = a
    return alive

nails = load(scene["nails"]); cat = load(scene["cat"])
frames = [(6, "Fresh release"), (220, "Spread into a torus"), (520, "Raining out")]

for k, (day, label) in enumerate(frames, 1):
    alive = decay_to(nails, M["nailAoverM"], day)
    decay_to(cat, M["catAoverM"], day)
    tsec = day * 86400.0
    nx, ny, nz, nr = positions(nails, tsec); cx, cy, cz, cr = positions(cat, tsec)

    fig = plt.figure(figsize=(9, 9), facecolor="#06080e")
    ax = fig.add_subplot(111, projection="3d"); ax.set_facecolor("#06080e")
    # Earth
    u, v = np.mgrid[0:TWO_PI:60j, 0:math.pi:30j]
    ex = RE*np.cos(u)*np.sin(v); ey = RE*np.sin(u)*np.sin(v); ez = RE*np.cos(v)
    ax.plot_surface(ex, ez, ey, color="#0e2d4e", alpha=0.95, linewidth=0, shade=True, zorder=0)
    ax.plot_wireframe(ex, ez, ey, color="#2a4a6b", linewidth=0.3, alpha=0.35, rstride=6, cstride=6)
    # catalog (dim)
    ax.scatter(cx, cy, cz, s=2, c="#7fa8d0", alpha=0.35, depthshade=False, edgecolors="none")
    # nails colored by altitude
    a_alive = nails["a"][alive] - RE
    ax.scatter(nx[alive], ny[alive], nz[alive], s=5, c=a_alive, cmap="autumn",
               vmin=120, vmax=650, alpha=0.9, depthshade=False, edgecolors="none")

    lim = 7600
    ax.set_xlim(-lim, lim); ax.set_ylim(-lim, lim); ax.set_zlim(-lim, lim)
    ax.set_box_aspect((1,1,1)); ax.set_axis_off(); ax.view_init(elev=22, azim=35 + k*8)
    frac = alive.mean()
    ax.text2D(0.04, 0.95, f"NAIL CLOUD ORBITAL DECAY", transform=ax.transAxes, color="#4da6ff",
              fontsize=26, family="monospace", weight="bold")
    ax.text2D(0.04, 0.88, f"Day {day} · {label}", transform=ax.transAxes, color="#e7eef8", fontsize=28, weight="bold")
    ax.text2D(0.04, 0.055, f"{frac*100:.0f}% of nails still up",
              transform=ax.transAxes, color="#9fb2cc", fontsize=20, family="monospace")
    out = os.path.join(HERE, "frames", f"frame{k}.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    fig.savefig(out, dpi=130, facecolor="#06080e", bbox_inches="tight", pad_inches=0.2)
    plt.close(fig)
    print(f"saved {out}  (day {day}, {frac*100:.0f}% alive)")
