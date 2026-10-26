import sys, json, numpy as np
sys.path.insert(0, '.')
from scipy.optimize import minimize
import mux_model, qinj
r = json.load(open('fit1.json')); cp = json.load(open('capfit.json')); c = json.load(open('curves.json'))
d5 = np.array(c['fig12_qinj']['5']); d15 = np.array(c['fig12_qinj']['15'])[::3]
best = [1e9, None]

def q_of(x):
    q = dict(mux_model.DEFAULT_Q, TOXN=10 ** x[0], TOXP=10 ** x[1], CGON=max(x[2], 0) * 1e-10, CGOP=max(x[3], 0) * 1e-10)
    open('lt/MUX507.lib', 'w').write(mux_model.lib(r, cp, q))
    return q

def cost(x):
    q_of(x)
    m5 = qinj.qinj(5, -5, d5[:, 0], 'f'); m15 = qinj.qinj(15, -15, d15[:, 0], 'f')
    e = 3 * np.sqrt(np.mean((m5 - d5[:, 1]) ** 2)) + np.sqrt(np.mean((m15 - d15[:, 1]) ** 2))
    if e < best[0]:
        best[0], best[1] = e, list(x)
        print(f'cost {e:.4f}  TOXN {10**x[0]*1e9:7.1f} nm  TOXP {10**x[1]*1e9:7.1f} nm  CGON {x[2]:.3f}e-10  CGOP {x[3]:.3f}e-10  '
              f'rms5 {np.sqrt(np.mean((m5 - d5[:, 1]) ** 2)):.3f} rms15 {np.sqrt(np.mean((m15 - d15[:, 1]) ** 2)):.3f} pC', flush=True)
    return e

s = minimize(cost, [np.log10(60e-9), np.log10(60e-9), 2.0, 2.0], method='Nelder-Mead',
             options=dict(maxfev=90, xatol=0.01, fatol=0.002, initial_simplex=None))
q = q_of(best[1])
json.dump({k: q[k] for k in ('TOXN', 'TOXP', 'CGON', 'CGOP')}, open('qfit.json', 'w'))
print('best', best)
