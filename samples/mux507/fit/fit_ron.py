"""Fit a level-1 NMOS||PMOS transmission gate (bodies at VSS/VDD, gates at the rails)
to the MUX507 Ron vs VS curves, reproducing the datasheet test (IS = 1 mA)."""
import json, sys
import numpy as np
from scipy.optimize import brentq, least_squares

c = json.load(open(sys.argv[1]))
I_TEST = 1e-3

# (VDD, VSS, points, weight)
cases = []
def add(curve, vdd, vss, w):
    p = np.array(curve)
    xs = np.linspace(p[0, 0] + 0.02, p[-1, 0] - 0.02, 41)
    cases.append((vdd, vss, xs, np.interp(xs, p[:, 0], p[:, 1]), w))

for lab, v in c['fig3_ron_low'].items():
    add(v, float(lab), -float(lab), 4.0 if lab == '5' else 2.0)   # +-5 V is what the user runs
for lab, v in c['fig1_ron_dual'].items():
    add(v, float(lab), -float(lab), 1.0)
add(c['fig8_ron_12']['12'], 12, -12, 1.0)
for lab, v in c['fig6_ron_single'].items():
    add(v, float(lab), 0.0, 1.0)

NAMES = ['VTON', 'GAMN', 'BETAN', 'VTOP', 'GAMP', 'BETAP', 'PHIN', 'REXT', 'PHIP']


def ids_n(vg, vb, va, vc, p):
    """Current from the higher terminal to the lower one (NMOS, gate vg, bulk vb)."""
    vd, vs = max(va, vc), min(va, vc)
    vt = p['VTON'] + p['GAMN'] * (np.sqrt(p['PHIN'] + max(vs - vb, 0)) - np.sqrt(p['PHIN']))
    vgst, vds = vg - vs - vt, vd - vs
    if vgst <= 0:
        return 0.0
    return p['BETAN'] * ((vgst - vds / 2) * vds if vds < vgst else vgst * vgst / 2)


def ids_p(vg, vb, va, vc, p):
    vs, vd = max(va, vc), min(va, vc)
    vt = p['VTOP'] + p['GAMP'] * (np.sqrt(p['PHIP'] + max(vb - vs, 0)) - np.sqrt(p['PHIP']))
    vgst, vds = vs - vg - vt, vs - vd
    if vgst <= 0:
        return 0.0
    return p['BETAP'] * ((vgst - vds / 2) * vds if vds < vgst else vgst * vgst / 2)


def ron(vs, vdd, vss, p):
    f = lambda vd: ids_n(vdd, vss, vs, vd, p) + ids_p(vss, vdd, vs, vd, p) - I_TEST
    lo = vs - 3.0
    if f(lo) < 0:
        return 3000.0 / I_TEST / 1000 * 1000   # both devices off: 3 V drop not enough
    vd = brentq(f, lo, vs, xtol=1e-9)
    return (vs - vd) / I_TEST + p['REXT']


def resid(x):
    p = dict(zip(NAMES, x))
    r = []
    for vdd, vss, xs, ys, w in cases:
        r += [w * np.log(ron(v, vdd, vss, p) / y) for v, y in zip(xs, ys)]
    return np.array(r)


x0 = [1.0, 1.5, 4e-4, 1.0, 1.5, 4e-4, 0.7, 30, 0.7]
lo = [0.2, 0.0, 1e-5, 0.2, 0.0, 1e-5, 0.05, 0, 0.05]
hi = [4.0, 4.0, 1e-2, 4.0, 4.0, 1e-2, 1.5, 100, 1.5]
best = None
for vt in (0.6, 1.0, 1.6):
    for g in (0.8, 1.6, 2.5):
        x0[0] = x0[3] = vt; x0[1] = x0[4] = g
        s = least_squares(resid, x0, bounds=(lo, hi), x_scale=[1, 1, 1e-4, 1, 1, 1e-4, 1, 30, 1])
        if best is None or s.cost < best.cost:
            best = s
p = dict(zip(NAMES, best.x))
print('fit:', {k: float(f'{v:.5g}') for k, v in p.items()})
for vdd, vss, xs, ys, w in cases:
    m = np.array([ron(v, vdd, vss, p) for v in xs])
    err = (m - ys) / ys * 100
    print(f'VDD={vdd:5} VSS={vss:6}: max|err| {np.abs(err).max():5.1f} %  rms {np.sqrt((err**2).mean()):4.1f} %   '
          f'peak model {m.max():5.0f} / datasheet {ys.max():5.0f} ohm at {xs[m.argmax()]:+.2f} / {xs[ys.argmax()]:+.2f} V')
json.dump(p, open(sys.argv[2], 'w'))
