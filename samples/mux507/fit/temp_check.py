import sys, json, numpy as np
sys.path.insert(0, '.')
import ltrun
c = json.load(open('curves.json'))['fig4_ron_temp']
folder = sys.argv[1]
temps = ['-40', '0', '25', '85', '125']
net = f"""* Ron vs VS vs temperature, 12 V single supply (Fig 4)
.include MUX507.lib
.options tnom=25
VDD vdd 0 12
VSS vss 0 0
VEN en 0 3.3
VS s 0 0
IS da 0 1m
X1 vdd db {' '.join(f'u{k}b' for k in range(8, 0, -1))} 0 0 0 0 en s {' '.join(f'u{k}a' for k in range(2, 9))} vss da MUX507
{chr(10).join(f'Ru{k}a u{k}a 0 1Meg' for k in range(2, 9))}
{chr(10).join(f'Ru{k}b u{k}b 0 1Meg' for k in range(1, 9))}
Rdb db 0 1Meg
.step temp list {' '.join(temps)}
.dc VS 0.05 11.95 0.05
.end
"""
base, log = ltrun.run(f'{folder}/ron_temp.net', net)
for t, st in zip(temps, ltrun.raw(base)):
    vs = st['V(s)']; ron = (vs - st['V(da)']) / 1e-3
    d = np.array(c[t]); ds = np.interp(vs, d[:, 0], d[:, 1])
    e = (ron - ds) / ds * 100
    i6 = np.argmin(abs(vs - 2))
    print(f'{t:>4} C: rms {np.sqrt((e**2).mean()):5.1f} %  max {np.abs(e).max():5.1f} %   Ron@2V {ron[i6]:4.0f}/{ds[i6]:4.0f}  peak {ron.max():4.0f}/{ds.max():4.0f} ohm')
