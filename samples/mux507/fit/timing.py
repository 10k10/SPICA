"""tON / tOFF / transition / break-before-make, datasheet test conditions (RL = 300 ohm, CL = 35 pF)."""
import sys
sys.path.insert(0, '.')
import ltrun

folder = sys.argv[1] if len(sys.argv) > 1 else 'lt'


def bench(vdd, vss, vs):
    pins = ' '.join(f'u{k}b' for k in range(8, 0, -1))
    common = f""".include MUX507.lib
VDD vdd 0 {vdd}
VSS vss 0 {vss}
RL da 0 300
CL da 0 35p
{chr(10).join(f'Ru{k}b u{k}b 0 1Meg' for k in range(1, 9))}
Rdb db 0 1Meg
.save V(da) V(en) V(a0)
"""
    on_off = common + f"""VEN en 0 PULSE(0 3.3 1u 5n 5n 2u 4u)
VS1 s1 0 {vs}
X1 vdd db {pins} 0 0 0 0 en s1 {' '.join(f'u{k}a' for k in range(2, 9))} vss da MUX507
{chr(10).join(f'Ru{k}a u{k}a 0 1Meg' for k in range(2, 9))}
.tran 0 4u 0 0.5n
.meas TRAN t_en_up WHEN V(en)=1.65 RISE=1
.meas TRAN vfin FIND V(da) AT 2.9u
.meas TRAN t_on_90 WHEN V(da)=0.9*vfin RISE=1
.meas TRAN t_en_dn WHEN V(en)=1.65 FALL=1
.meas TRAN t_off_10 WHEN V(da)=0.1*vfin FALL=1
.meas TRAN ton PARAM t_on_90-t_en_up
.meas TRAN toff PARAM t_off_10-t_en_dn
"""
    trans = common + f"""VEN en 0 3.3
VA0 a0 0 PULSE(0 3.3 1u 5n 5n 2u 4u)
VS1 s1 0 {vs}
VS2 s2 0 {-vs if vss < 0 else 0}
X1 vdd db {pins} 0 0 0 a0 en s1 s2 {' '.join(f'u{k}a' for k in range(3, 9))} vss da MUX507
{chr(10).join(f'Ru{k}a u{k}a 0 1Meg' for k in range(3, 9))}
.tran 0 4u 0 0.5n
.meas TRAN t_a WHEN V(a0)=1.65 RISE=1
.meas TRAN v1 FIND V(da) AT 0.95u
.meas TRAN v2 FIND V(da) AT 2.9u
.meas TRAN t_90 WHEN V(da)=v1+0.9*(v2-v1) FALL=1
.meas TRAN tt PARAM t_90-t_a
"""
    bbm = common + f"""VEN en 0 3.3
VA0 a0 0 PULSE(0 3.3 1u 5n 5n 2u 4u)
VS1 s1 0 {vs}
X1 vdd db {pins} 0 0 0 a0 en s1 s1 s1 s1 s1 s1 s1 s1 vss da MUX507
.tran 0 4u 0 0.5n
.meas TRAN vfin FIND V(da) AT 0.95u
.meas TRAN t_break WHEN V(da)=0.8*vfin FALL=1 TD=0.5u
.meas TRAN t_make WHEN V(da)=0.8*vfin RISE=1 TD=1u
.meas TRAN tbbm PARAM t_make-t_break
.meas TRAN vdip MIN V(da) FROM 1u TO 1.5u
"""
    out = {}
    for tag, text in (('onoff', on_off), ('tt', trans), ('bbm', bbm)):
        base, log = ltrun.run(f'{folder}/tim_{tag}_{vdd}.net', '* timing\n' + text + '.end\n', ascii=False)
        out.update(ltrun.meas(log))
    return out


for vdd, vss, vs, ds in ((15, -15, 10, 'datasheet tON 82, tOFF 63, tt 97, tBBM 30 ns'),
                         (5, -5, 3, 'no datasheet data at +-5 V')):
    m = bench(vdd, vss, vs)
    print(f'+-{vdd} V: tON {m["ton"] * 1e9:5.1f}  tOFF {m["toff"] * 1e9:5.1f}  tt {m["tt"] * 1e9:5.1f}  '
          f'tBBM {m["tbbm"] * 1e9:5.1f} ns   ({ds})')
