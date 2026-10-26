import sys, json, numpy as np
sys.path.insert(0, '.')
import ltrun
c = json.load(open('curves.json'))

def bench(vdd, vss, which, vbs):
    en = 0 if which != 'CD_ON' else 3.3
    sa = {k: f'u{k}a' for k in range(1, 9)}
    drive = 'da' if which != 'CS_OFF' else 'u1a'
    lines = [f'.include MUX507.lib', f'VDD vdd 0 {vdd}', f'VSS vss 0 {vss}', f'VEN en 0 {en}',
             f'VT {drive} 0 {{VB}} AC 1',
             f'X1 vdd db {" ".join(f"u{k}b" for k in range(8, 0, -1))} 0 0 0 0 en {" ".join(sa.values())} vss da MUX507']
    for n in [f'u{k}b' for k in range(1, 9)] + [f'u{k}a' for k in range(1, 9)] + ['da', 'db']:
        if n != drive:
            lines.append(f'R{n} {n} 0 1G')
    lines += [f'.step param VB list {" ".join(f"{v:g}" for v in vbs)}', '.ac lin 3 0.99Meg 1.01Meg',
              '.meas AC cap FIND -Im(I(VT))/(2*pi*1Meg) AT 1Meg', '.end']
    base, log = ltrun.run(f'lt/cap_{which}_{vdd}_{vss}.net', '* capacitance @ 1 MHz\n' + '\n'.join(lines) + '\n')
    return log

import re
def caps(log):
    return [10 ** (float(m) / 20) * 1e12 for m in re.findall(r'\(([-0-9.e]+)dB', log)]

out = {}
for fig, vdd, vss in (('fig20_cap15', 15, -15), ('fig24_cap12', 12, 0)):
    for which in ('CS_OFF', 'CD_OFF', 'CD_ON'):
        d = np.array(c[fig][which]); xs = np.linspace(d[0, 0] + 0.5, d[-1, 0] - 0.5, 9)
        m = np.array(caps(bench(vdd, vss, which, xs))); ds = np.interp(xs, d[:, 0], d[:, 1])
        e = (m - ds) / ds * 100
        print(f'{vdd:3}/{vss:4} {which}: rms {np.sqrt((e**2).mean()):4.1f} %  max {np.abs(e).max():4.1f} %   model {m.min():.2f}..{m.max():.2f} pF, datasheet {ds.min():.2f}..{ds.max():.2f}')
xs = [-4, -2, 0, 2, 4]
for which in ('CS_OFF', 'CD_OFF', 'CD_ON'):
    print('+-5 V', which, [round(v, 2) for v in caps(bench(5, -5, which, xs))], 'pF at VS', xs)
