"""Demo schematic: 3 kHz sine -> RC low-pass -> MUX507 (symbol), with a no-mux reference filter."""
import sys
sys.path.insert(0, r'C:\Users\rob\AppData\Local\Temp\claude\C--Users-rob\a944bbba-7108-4c43-ae50-24da814ad824\scratchpad')
from gen_samples import asc

parts = [
    ('voltage', 'VDD', '5', ['vdd', '0']),
    ('voltage', 'VSS', '5', ['0', 'vss']),
    ('voltage', 'VEN', 'PULSE(3.3 0 416.667u 10n 10n 416.667u 833.333u)', ['en', '0']),
    ('voltage', 'Vsig', 'SINE(0 {AMP} 3k)', ['sig', '0']),
    ('voltage', 'V2', '{VOTHER}', ['other', '0']),
    ('res', 'R1', '{RFX}', ['sig', 'filt']),
    ('cap', 'C1', '{CFX}', ['filt', '0']),
    ('res', 'R1ref', '{RFX}', ['sig', 'ref']),
    ('cap', 'C1ref', '{CFX}', ['ref', '0']),
    ('cap', 'CL', '{CLOAD}', ['da', '0']),
    ('res', 'RL', '10Meg', ['da', '0']),
]
directives = [
    '.param AMP=2 VOTHER=0 CLOAD=20p',
    '.param RF=330 CF=10n',
    ';.param RF=4.7k CF=1n',
    '.param RFX=mc(RF,0.01) CFX=mc(CF,0.05)',
    '.tran 0 3.3333m 0',
    '.meas TRAN glitch_pos MIN V(filt)-V(ref) FROM 416.667u TO 436.667u',
    '.meas TRAN glitch_neg MAX V(filt)-V(ref) FROM 1.25m TO 1.27m',
    '.meas TRAN err_rms RMS V(filt)-V(ref) FROM 0 TO 3.3333m',
    '.meas TRAN filt_rms RMS V(filt) FROM 0 TO 3.3333m',
    '.meas TRAN ref_rms RMS V(ref) FROM 0 TO 3.3333m',
    '.meas TRAN rms_err_ppm PARAM (filt_rms-ref_rms)/ref_rms*1e6',
    '.meas TRAN da_err_min MIN V(da)-V(ref) FROM 418.667u TO 833.333u',
]
notes = [
    'SPICA sample: MUX507_2TO1 (+-5 V) switching an RC-filtered 3 kHz sine. EN toggles S1 <-> S0 every 416.7 us,',
    'so S0 connects at the +peak (416.7 us) and at the -peak (1.25 ms) of the sine: worst case for charge sharing.',
    'R1ref/C1ref = the same filter (same random R, C per run) without the mux. glitch_* = dip at connection, rms_err_ppm = error of the RMS value.',
    'CLOAD = everything on DA after the mux (buffer/ADC input + PCB): it usually matters more than the mux itself.',
    'Swap the .param lines to compare 330R/10n with 4.7k/1n. U1 carries the uniform Monte Carlo defaults (Ron, mismatch, caps, Qinj, timing);',
    'set e.g. RONK=1 CKS=1 CKD=1 QK=0 on U1 (SpiceLine) for a nominal part.',
    'No .tran max step is needed: the model has smooth gate edges, and 1 ns gives the same result ~130x slower.',
]

text = asc(parts, directives, notes, cols=6)
# MUX507_2TO1 symbol placed to the right of the grid; every pin gets a net label
pins = {'S0': (-96, -48), 'S1': (-96, 48), 'D': (96, 0), 'VCC': (-32, -128), 'VSS': (-32, 128), 'EN': (32, 112)}
nets = {'S0': 'filt', 'S1': 'other', 'D': 'da', 'VCC': 'vdd', 'VSS': 'vss', 'EN': 'en'}
x, y = 1680, 336
extra = [f'FLAG {x + px} {y + py} {nets.get(n, "0")}' for n, (px, py) in pins.items()]
extra += [f'SYMBOL MUX507_2TO1 {x} {y} R0', 'SYMATTR InstName U1']
text = text.replace('SHEET 1 1600 1200', 'SHEET 1 2000 1200')
head, tail = text.split('\r\nSYMBOL ', 1)
text = head + '\r\n' + '\r\n'.join(extra) + '\r\nSYMBOL ' + tail
open(sys.argv[1], 'w', newline='').write(text)
print('ok')
