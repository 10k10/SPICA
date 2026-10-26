"""Check every Monte Carlo knob at its extremes, on MUX507_2CH (and 2CH == 8CH at nominal), +-5 V."""
import sys
sys.path.insert(0, '.')
import ltrun

HEAD = '.include MUX507.lib\nVDD vdd 0 5\nVSS vss 0 -5\n'


def x2(par='', s1a='s', s2a='u2a', en='en', a0='0', da='da'):
    return f'X1 vdd vss 0 {en} {a0} {s1a} {s2a} u1b u2b {da} db MUX507_2CH {par}\nRu2a u2a 0 1Meg\nRu1b u1b 0 1Meg\nRu2b u2b 0 1Meg\nRdb db 0 1Meg\n'


def x8(par=''):
    return (f'X1 vdd db {" ".join(f"u{k}b" for k in range(8, 0, -1))} 0 0 0 0 en s {" ".join(f"u{k}a" for k in range(2, 9))} vss da MUX507 {par}\n'
            + '\n'.join(f'Ru{k}a u{k}a 0 1Meg' for k in range(2, 9)) + '\n' + '\n'.join(f'Ru{k}b u{k}b 0 1Meg' for k in range(1, 9)) + '\nRdb db 0 1Meg\n')


def ron(xline, tag):
    net = HEAD + 'VEN en 0 3.3\nVS s 0 0.7\nIS da 0 1m\n' + xline + '.op\n.meas OP r FIND (V(s)-V(da))/1m\n.end\n'
    return ltrun.meas(ltrun.run(f'mc/{tag}.net', '* ron\n' + net, ascii=False)[1])['r']


def cap(xline, tag, en):
    net = HEAD + f'VEN en 0 {en}\nVT da 0 0 AC 1\nRs s 0 1G\n' + xline + \
        '.ac lin 3 0.99Meg 1.01Meg\n.meas AC c FIND -Im(I(VT))/(2*pi*1Meg) AT 1Meg\n.end\n'
    log = ltrun.run(f'mc/{tag}.net', '* cap\n' + net, ascii=False)[1]
    import re
    return 10 ** (float(re.search(r'\(([-0-9.e]+)dB', log).group(1)) / 20) * 1e12


def qinj(xline, tag):
    net = HEAD + 'VEN en 0 PWL(0 3.3 20u 3.3 20.01u 0)\nVS s 0 0\nCL da 0 1n\n' + xline + \
        '.save V(da)\n.tran 0 24u 0 20n\n.meas TRAN vb FIND V(da) AT 19.9u\n.meas TRAN va FIND V(da) AT 23u\n.meas TRAN qj PARAM 1n*(va-vb)\n.end\n'
    return ltrun.meas(ltrun.run(f'mc/{tag}.net', '* q\n' + net, ascii=False)[1])['qj'] * 1e12


def bbm(par, tag):
    net = HEAD + 'VEN en 0 3.3\nVA0 a0 0 PULSE(0 3.3 1u 5n 5n 2u 4u)\nVS1 s 0 3\nRL da 0 300\nCL da 0 35p\n' + \
        x2(par, s1a='s', s2a='s', a0='a0') + '.save V(da) V(a0)\n.tran 0 4u 0 0.5n\n' + \
        '.meas TRAN vfin FIND V(da) AT 0.95u\n.meas TRAN t_break WHEN V(da)=0.8*vfin FALL=1 TD=0.5u\n' + \
        '.meas TRAN t_make WHEN V(da)=0.8*vfin RISE=1 TD=1u\n.meas TRAN tbbm PARAM t_make-t_break\n' + \
        '.meas TRAN t_a WHEN V(a0)=1.65 RISE=1\n.meas TRAN tt PARAM t_make-t_a\n.end\n'
    m = ltrun.meas(ltrun.run(f'mc/{tag}.net', '* bbm\n' + net, ascii=False)[1])
    return m['tbbm'] * 1e9, m['tt'] * 1e9


import os
os.makedirs('mc', exist_ok=True)
import shutil
shutil.copy('lt/MUX507.lib', 'mc/MUX507.lib')
r8, r2 = ron(x8(), 'r8'), ron(x2(), 'r2')
print(f'Ron @0.7 V nominal: 8CH {r8:.1f}  2CH {r2:.1f} ohm')
for k in (0.64, 1.36):
    print(f'  RONK={k}: {ron(x2(f"RONK={k}"), f"rk{k}"):.1f} ohm (x{ron(x2(f"RONK={k}"), f"rk{k}") / r2:.3f})')
print(f'  M1A=+0.036: {ron(x2("M1A=0.036"), "m1"):.1f} ohm')
for en, nm in ((0, 'CD(OFF)'), (3.3, 'CD(ON)')):
    c8, c2 = cap(x8(), f'c8{en}', en), cap(x2(), f'c2{en}', en)
    lo, hi = cap(x2('CKS=0.57 CKD=0.83'), f'cl{en}', en), cap(x2('CKS=1.43 CKD=1.17'), f'ch{en}', en)
    print(f'{nm} @0 V: 8CH {c8:.2f}  2CH {c2:.2f} pF;  min/max caps {lo:.2f} .. {hi:.2f} pF')
for qk in (-1, 0, 1):
    print(f'QK={qk:+}: charge injection {qinj(x2(f"QK={qk}"), f"q{qk}"):+.3f} pC')
for par in ('', 'TDON=11n TDOFF=46n TDA=0', 'TDON=119n TDOFF=16n TDA=61n'):
    b, t = bbm(par, 'b' + str(abs(hash(par)) % 1000))
    print(f'{par or "nominal":28}: tBBM {b:5.1f} ns, address->make {t:6.1f} ns')
