"""User scenario: 3 kHz sine -> RC low-pass -> MUX507 S1A, other channel on S2A, load on DA.
Error = filter node (and DA) minus an identical RC filter without the mux."""
import sys, time
sys.path.insert(0, '.')
import ltrun


def net(rf, cf, cl=20e-12, amp=2.0, voth=0.0, tsw=416.6667e-6, maxstep=None, extra=''):
    ms = f' {maxstep}' if maxstep else ''
    return f"""* MUX507 switching glitch on an RC-filtered 3 kHz input (+-5 V supplies)
.include MUX507.lib
VDD vdd 0 5
VSS vss 0 -5
VEN en 0 3.3
Vsig sig 0 SINE(0 {amp} 3k)
R1 sig s1 {rf}
C1 s1 0 {cf}
R1r sig ref {rf}
C1r ref 0 {cf}
V2 s2 0 {voth}
CL da 0 {cl}
RL da 0 10Meg
* A0 = 0 -> S1A on, A0 = 1 -> S2A on; starts on channel 2, toggles every tsw
VA0 a0 0 PULSE(3.3 0 {tsw} 10n 10n {tsw} {2 * tsw})
X1 vdd db 0 0 0 0 0 0 0 0 0 0 0 a0 en s1 s2 0 0 0 0 0 0 vss da MUX507
.tran 0 3.3333m 0{ms}
.meas TRAN err_rms RMS V(s1)-V(ref) FROM 0 TO 3.3333m
.meas TRAN err_min MIN V(s1)-V(ref) FROM 0 TO 3.3333m
.meas TRAN err_max MAX V(s1)-V(ref) FROM 0 TO 3.3333m
.meas TRAN ref_rms RMS V(ref) FROM 0 TO 3.3333m
.meas TRAN s1_rms RMS V(s1) FROM 0 TO 3.3333m
.meas TRAN glitch MIN V(s1)-V(ref) FROM {tsw} TO {tsw + 20e-6}
.meas TRAN dmin MIN V(da)-V(ref) FROM {tsw + 2e-6} TO {2 * tsw}
.meas TRAN dmax MAX V(da)-V(ref) FROM {tsw + 2e-6} TO {2 * tsw}
.meas TRAN t_cross WHEN abs(V(s1)-V(ref))=100u FALL=1 TD={tsw + 50e-9}
.meas TRAN t_settle PARAM t_cross-{tsw}
.meas TRAN rms_err_ppm PARAM (s1_rms-ref_rms)/ref_rms*1e6
.meas TRAN glitch2 MAX V(s1)-V(ref) FROM {3 * tsw} TO {3 * tsw + 20e-6}
.save V(s1) V(ref) V(da)
{extra}
.end
"""


def go(tag, **kw):
    t = time.time()
    base, log = ltrun.run(f'app/{tag}.net', net(**kw), ascii=False)
    m = ltrun.meas(log)
    m['wall'] = time.time() - t
    return m


if __name__ == '__main__':
    keys = ['glitch', 'glitch2', 't_settle', 'dmin', 'dmax', 'err_rms', 'rms_err_ppm', 'wall']
    for cl in (20e-12, 100e-12):
        for name, rf, cf in (('A_4k7_1n', 4.7e3, 1e-9), ('B_330_10n', 330, 10e-9)):
            m = go(f'{name}_{cl*1e12:.0f}p', rf=rf, cf=cf, cl=cl)
            print(f'{name} CL={cl*1e12:.0f}p', {k: f'{m.get(k, float("nan")):.4g}' for k in keys})
