import sys, json, numpy as np
sys.path.insert(0, '.')
import ltrun
c = json.load(open('curves.json'))
UNUSED = ' '.join(f'S{k}A' for k in range(2, 9)) + ' ' + ' '.join(f'S{k}B' for k in range(1, 9))
cases = [('fig3_ron_low', '5', 5, -5), ('fig3_ron_low', '6', 6, -6), ('fig3_ron_low', '7', 7, -7),
         ('fig1_ron_dual', '10', 10, -10), ('fig1_ron_dual', '15', 15, -15), ('fig1_ron_dual', '18', 18, -18),
         ('fig6_ron_single', '12', 12, 0)]
res = {}
for fig, lab, vdd, vss in cases:
    net = f"""* Ron vs VS, datasheet method (IS = 1 mA)
.include MUX507.lib
VDD vdd 0 {vdd}
VSS vss 0 {vss}
VEN en 0 3.3
VS s 0 0
IS da 0 1m
X1 vdd db {' '.join(f'u{k}b' for k in range(8, 0, -1))} 0 0 0 0 en s {' '.join(f'u{k}a' for k in range(2, 9))} vss da MUX507
{chr(10).join(f'Ru{k}a u{k}a 0 1Meg' for k in range(2, 9))}
{chr(10).join(f'Ru{k}b u{k}b 0 1Meg' for k in range(1, 9))}
Rdb db 0 1Meg
.dc VS {vss + 0.02} {vdd - 0.02} 0.05
.end
"""
    base, log = ltrun.run(f'lt/ron_{vdd}_{vss}.net', net)
    st = ltrun.raw(base)[0]
    vs = st['V(s)']; ron = (st['V(s)'] - st['V(da)']) / 1e-3
    d = np.array(c[fig][lab]); ds = np.interp(vs, d[:, 0], d[:, 1])
    e = (ron - ds) / ds * 100
    print(f'+{vdd}/{vss}: LTspice vs datasheet rms {np.sqrt((e**2).mean()):4.1f} %  max {np.abs(e).max():4.1f} %   peak {ron.max():4.0f} / {ds.max():4.0f} ohm')
    res[f'{vdd}/{vss}'] = [vs.tolist(), ron.tolist(), ds.tolist()]
json.dump(res, open('ron_lt.json', 'w'))
