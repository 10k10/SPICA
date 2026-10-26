"""Fit pad + junction capacitances (NMOS body at VSS, PMOS well at VDD) to CS(OFF)/CD(OFF) vs VS."""
import json, sys
import numpy as np
from scipy.optimize import least_squares

c = json.load(open(sys.argv[1]))
sets = []   # (vdd, vss, which, xs, ys)
for fig, vdd, vss in (('fig20_cap15', 15, -15), ('fig24_cap12', 12, 0)):
    for which in ('CS_OFF', 'CD_OFF'):
        p = np.array(c[fig][which])
        xs = np.linspace(p[0, 0] + 0.3, p[-1, 0] - 0.3, 25)
        sets.append((vdd, vss, which, xs, np.interp(xs, p[:, 0], p[:, 1])))

NAMES = ['CPS', 'CNS', 'CPPS', 'CPD', 'CND', 'CPPD', 'PB', 'MJ']   # pF; D values are per switch (x8)


def f(vr, pb, mj):
    return 1 / (1 + np.maximum(vr, 0) / pb) ** mj


def model(p, vdd, vss, which, v):
    if which == 'CS_OFF':
        return p['CPS'] + p['CNS'] * f(v - vss, p['PB'], p['MJ']) + p['CPPS'] * f(vdd - v, p['PB'], p['MJ'])
    return p['CPD'] + 8 * (p['CND'] * f(v - vss, p['PB'], p['MJ']) + p['CPPD'] * f(vdd - v, p['PB'], p['MJ']))


def resid(x):
    p = dict(zip(NAMES, x))
    return np.concatenate([(model(p, *s[:3], s[3]) - s[4]) / s[4] for s in sets])


s = least_squares(resid, [1, 0.5, 1, 3, 0.2, 0.3, 0.7, 0.5], bounds=([0] * 6 + [0.1, 0.1], [10] * 6 + [5, 2]))
p = dict(zip(NAMES, s.x))
print({k: round(float(v), 4) for k, v in p.items()})
for vdd, vss, which, xs, ys in sets:
    e = (model(p, vdd, vss, which, xs) - ys) / ys * 100
    print(f'{vdd:3}/{vss:4} {which}: rms {np.sqrt((e ** 2).mean()):4.1f} %  max {np.abs(e).max():4.1f} %')
for v in (-4, -2, 0, 2, 4):
    cs, cd = model(p, 5, -5, 'CS_OFF', v), model(p, 5, -5, 'CD_OFF', v)
    print(f'+-5 V prediction at VS={v:+}: CS(OFF) {cs:.2f}  CD(OFF) {cd:.2f}  CD(ON) {cs + cd:.2f} pF')
json.dump(p, open(sys.argv[2], 'w'))
