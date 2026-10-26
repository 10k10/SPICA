import sys, json, numpy as np
sys.path.insert(0, '.')
import ltrun

def qinj(vdd, vss, vss_list, tag='q'):
    lines = ['.include MUX507.lib', f'VDD vdd 0 {vdd}', f'VSS vss 0 {vss}',
             'VEN en 0 PWL(0 3.3 20u 3.3 20.01u 0)', 'VS s 0 {VS}', 'CL da 0 1n',
             f'X1 vdd db {" ".join(f"u{k}b" for k in range(8, 0, -1))} 0 0 0 0 en s {" ".join(f"u{k}a" for k in range(2, 9))} vss da MUX507']
    lines += [f'R{n} {n} 0 1G' for n in [f'u{k}b' for k in range(1, 9)] + [f'u{k}a' for k in range(2, 9)] + ['db']]
    lines += [f'.step param VS list {" ".join(f"{v:g}" for v in vss_list)}', '.save V(da)', '.tran 0 24u 0 20n',
              '.meas TRAN vb FIND V(da) AT 19.9u', '.meas TRAN va FIND V(da) AT 23u', '.meas TRAN qj PARAM 1n*(va-vb)', '.end']
    base, log = ltrun.run(f'lt/{tag}_{vdd}.net', '* charge injection, datasheet Fig 32\n' + '\n'.join(lines) + '\n')
    return np.array(ltrun.step_meas(log)['qj']) * 1e12

if __name__ == '__main__':
    c = json.load(open('curves.json'))
    for lab, vdd in (('5', 5), ('15', 15)):
        d = np.array(c['fig12_qinj'][lab]); xs = d[:, 0]
        m = qinj(vdd, -vdd, xs)
        print(f'+-{vdd} V   VS: ' + ' '.join(f'{x:6.1f}' for x in xs))
        print(f'  model pC: ' + ' '.join(f'{v:6.3f}' for v in m))
        print(f'  datasheet: ' + ' '.join(f'{v:6.3f}' for v in d[:, 1]))
