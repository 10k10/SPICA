"""Demo schematic: RC-filtered 3 kHz sine read by a scanning MUX507 (160 kSPS slots, GND discharge
between readings), sampled by an ideal ADC at the end of the slot and compared with a no-mux filter."""
import sys
sys.path.insert(0, r'C:\Users\rob\AppData\Local\Temp\claude\C--Users-rob\a944bbba-7108-4c43-ae50-24da814ad824\scratchpad')
import gen_samples
from gen_samples import asc

# LTspice SpecialFunctions\sample: in+, in-, CLK, S/H, out, com
gen_samples.PINS['SpecialFunctions\\sample'] = [(-80, -32), (-80, 0), (-80, 32), (-80, 64), (96, 16), (-80, 96)]
gen_samples.WIDE.add('SpecialFunctions\\sample')

parts = [
    ('voltage', 'VDD', '5', ['vdd', '0']),
    ('voltage', 'VSS', '5', ['0', 'vss']),
    ('voltage', 'VEN', 'PULSE(3.3 0 {T0} 10n 10n {TSIG-10n} {TFRAME})', ['en', '0']),
    ('voltage', 'VCLK', 'PULSE(0 1 {T0+TADC} 1n 1n 100n {TFRAME})', ['clk', '0']),
    ('voltage', 'Vsig', 'SINE(0 {AMP} 3k)', ['sig', '0']),
    ('res', 'R1', '{RFX}', ['sig', 'filt']),
    ('cap', 'C1', '{CFX}', ['filt', '0']),
    ('res', 'R1ref', '{RFX}', ['sig', 'ref']),
    ('cap', 'C1ref', '{CFX}', ['ref', '0']),
    ('cap', 'CL', '{CLOAD}', ['da', '0']),
    ('res', 'RL', '{RLOAD}', ['da', '0']),
    ('SpecialFunctions\\sample', 'A1', None, ['da', 'ref', 'clk', '0', 'errh', '0']),
    ('SpecialFunctions\\sample', 'A2', None, ['ref', '0', 'clk', '0', 'refh', '0']),
]
directives = [
    '.param AMP=2 RF=330 CF=10n CLOAD=1.5p RLOAD=8Meg',
    ';.param RF=4.7k CF=1n',
    '.param RFX=mc(RF,0.01) CFX=mc(CF,0.05)',
    '.param FS=160k NCH=8 TGND=200n TADC=488n',
    '.param TFRAME={1/FS} TSIG={TFRAME/NCH-TGND} T0={TFRAME-TSIG} TM0={T0+TADC+1u}',
    '.tran 0 3.3333m 0',
    '.meas TRAN err_max MAX V(errh) FROM {TM0} TO 3.3333m',
    '.meas TRAN err_min MIN V(errh) FROM {TM0} TO 3.3333m',
    '.meas TRAN err_rms RMS V(errh) FROM {TM0} TO 3.3333m',
    '.meas TRAN err_pk_ppm PARAM max(abs(err_max),abs(err_min))/AMP*1e6',
    '.meas TRAN ref_rms RMS V(refh) FROM {TM0} TO 3.3333m',
    '.meas TRAN sxy INTEG V(errh)*V(refh) FROM {TM0} TO 3.3333m',
    '.meas TRAN sxx INTEG V(refh)*V(refh) FROM {TM0} TO 3.3333m',
    '.meas TRAN gain_ppm PARAM sxy/sxx*1e6',
    '.meas TRAN resid_rms PARAM sqrt(max(err_rms*err_rms-(sxy/sxx*ref_rms)*(sxy/sxx*ref_rms),0))',
    '.meas TRAN filt_dip MIN V(filt)-V(ref) FROM 0 TO 3.3333m',
]
notes = [
    'SPICA sample: MUX507_2TO1 (+-5 V) reading an RC-filtered 3 kHz sine in a scan. S0 = signal, S1 = GND (discharge).',
    'FS = sample rate per channel; frame 1/FS = NCH x (TGND + TSIG). Per channel: GND for TGND (200 ns), then the input for TSIG',
    '(781.25 - 200 = 581.25 ns), once every 6.25 us. Change TGND and TSIG follows.',
    'the rest of the frame D sits on GND. The other channels are each preceded by a GND slot, so D is at 0 V whenever this channel connects.',
    'The ADC samples TADC after the channel-selection edge (488 ns); A1 holds V(da)-V(ref), A2 holds V(ref).',
    'At +-5 V the switch connects ~140 ns after the edge and turns off ~90 ns after the slot ends: keep TADC inside that window.',
    'err_* = sampled error vs the same filter without the mux (V). gain_ppm = least-squares gain error of the samples (= RMS-reading error);',
    'resid_rms = what is left, mostly an ~8 ns timing shift of the sampled waveform (does not change an RMS reading).',
    'filt_dip = deepest dip on the filter node. CLOAD/RLOAD = op-amp input on D. Swap the RF/CF lines for 4.7k/1n.',
    'U1 carries the uniform Monte Carlo defaults; set RONK=1 CKS=1 CKD=1 QK=0 on U1 (SpiceLine) for a nominal part.',
]

text = asc(parts, directives, notes, cols=6)
pins = {'S0': (-96, -48), 'S1': (-96, 48), 'D': (96, 0), 'VCC': (-32, -128), 'VSS': (-32, 128), 'EN': (32, 112)}
nets = {'S0': 'filt', 'S1': '0', 'D': 'da', 'VCC': 'vdd', 'VSS': 'vss', 'EN': 'en'}
x, y = 1680, 336
extra = [f'FLAG {x + px} {y + py} {nets[n]}' for n, (px, py) in pins.items()]
extra += [f'SYMBOL MUX507_2TO1 {x} {y} R0', 'SYMATTR InstName U1']
text = text.replace('SHEET 1 1600 1200', 'SHEET 1 2000 1200')
head, tail = text.split('\r\nSYMBOL ', 1)
text = head + '\r\n' + '\r\n'.join(extra) + '\r\nSYMBOL ' + tail
open(sys.argv[1], 'w', newline='').write(text)
print('ok')
