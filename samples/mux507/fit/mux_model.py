"""Write the MUX507 LTspice models (transmission-gate topology, parameters fitted to the datasheet).

Two subcircuits share one switch cell:
  MUX507      full part, 8 channel pairs (A0..A2, EN)
  MUX507_2CH  channels 1 and 2 only (A0, EN); channels 3..8 stay in as permanently-off switches
              so DA/DB keep the full 8-switch drain capacitance.
  MUX507_2TO1 one 2:1 switch (S0, S1 -> D) whose EN pin selects the channel: wired as the chip's
              A0 with the chip's EN tied high, so switching has the address-transition timing.
Every Monte Carlo quantity is an instance parameter, so SPICA's paired generator (or LTspice's
own flat()/mc()) can drive it from the schematic.
"""
import json

L_UM = 2.0
KP = 20e-6
CJ = 1e-3          # F/m^2: junction caps are given as CJ * area so each instance can scale them
QC = 0.03e-12      # charge-injection imbalance cap: QK = +-1 -> +-0.3 pC at +-5 V, +-0.9 pC at +-15 V

HEADER = """\
* MUX507 - 36 V, 8:1 differential / dual 8:1 precision analog multiplexer (TI SBAS803A)
* LTspice model built for SPICA: NMOS || PMOS transmission gate (datasheet Fig 38), bodies at
* VSS / VDD, gates driven rail to rail; level-1 parameters fitted to the datasheet curves:
*   Ron vs VS         Fig 1, 3, 6, 8   +-5 V: 2.3 % rms, +-15 V: 8 % rms
*   Ron vs temp       Fig 4            2.9 .. 4.7 % rms, -40 .. 125 C
*   CS/CD(OFF), CD(ON) Fig 20, 24      3 .. 5 % rms
*   tON/tOFF/tt/tBBM  table (+-15 V)   81 / 63 / 92 / 42 ns vs 82 / 63 / 97 / 30..54 ns
*   charge injection  Fig 12           magnitude only (nominal ~0 pC; QK spreads it)
* Not modelled: leakage (1 pA typ, 10 nA max @85 C), ESD clamps, behaviour above ~10 MHz.
*
* Logic: TTL threshold 1.4 V referred to GND. EN = 0 -> all off. A2..A0 = k-1 -> SkA-DA and SkB-DB on.
*
* Instance parameters (nominal = default; the symbols carry uniform Monte Carlo defaults built
* from the datasheet as typ +- (max - typ)):
*   RONK   Ron scale of the whole part              typ 125 / max 170 ohm   -> 1 +- 0.36
*   MkA/MkB  per-switch Ron offset (fraction)       dRON typ 6 / max 9 ohm   -> +- 0.036 each
*   CKS    source-side capacitance scale            CS(OFF) 2.1 / 3.0 pF     -> 1 +- 0.43
*   CKD    drain-side capacitance scale             CD(OFF) 6.4 / 7.5 pF     -> 1 +- 0.17
*   QK     charge-injection imbalance, -1 .. 1      +-0.9 pC full range @+-15 V
*   TDON   EN/address -> gate on delay             tON  82 / 136 ns         -> 65n +- 54n
*   TDOFF  EN/address -> gate off delay            tOFF 63 /  78 ns         -> 31n +- 15n
*   TDA    extra address-decoder delay             tt   97 / 143 ns         -> 15n +- 46n (>= 0)
*   Break-before-make is enforced: on-delay >= off-delay + 22.5 ns (datasheet tBBM >= 30 ns).
"""


def switch_cell(r, c, q):
    wn = r['BETAN'] / KP * L_UM
    wp = r['BETAP'] / KP * L_UM
    area = lambda pf: f'{pf * 1e-12 / CJ:.4g}'
    return [
        '* one switch: pad cap on S, series metal/well resistance, NMOS || PMOS, injection trim caps',
        '.subckt MUX507_SW S D GN GP VDD VSS GND params: RK=1 CKS=1 CKD=1 QK=0',
        f'Cpad S GND {{{c["CPS"]:.4g}p*CKS}}',
        f'Rs S s1 {{{r["REXT"] / 2:.4g}*RK}} tc={q["RTC"]}',
        f'Rd D d1 {{{r["REXT"] / 2:.4g}*RK}} tc={q["RTC"]}',
        f'MN d1 GN s1 VSS MUX507_N W={{{wn:.5g}u/RK}} L={L_UM}u AD={{{area(c["CND"])}*CKD}} AS={{{area(c["CNS"])}*CKS}}',
        f'MP d1 GP s1 VDD MUX507_P W={{{wp:.5g}u/RK}} L={L_UM}u AD={{{area(c["CPPD"])}*CKD}} AS={{{area(c["CPPS"])}*CKS}}',
        f'Cqp GP d1 {{{QC:.3g}*max(QK,0)+1e-18}}',
        f'Cqn GN d1 {{{QC:.3g}*max(-QK,0)+1e-18}}',
        '.ends MUX507_SW', '',
        f'.model MUX507_N NMOS(level=1 VTO={r["VTON"]:.5g} GAMMA={r["GAMN"]:.5g} PHI={r["PHI"]:.4g} KP={KP:g} '
        f'TOX={q["TOXN"]:.4g} CGSO={q["CGON"]:.4g} CGDO={q["CGON"]:.4g} CJ={CJ:g} PB={c["PB"]:.4g} MJ={c["MJ"]:.4g} TNOM=25)',
        f'.model MUX507_P PMOS(level=1 VTO={-r["VTOP"]:.5g} GAMMA={r["GAMP"]:.5g} PHI={r["PHI"]:.4g} KP={KP:g} '
        f'TOX={q["TOXP"]:.4g} CGSO={q["CGOP"]:.4g} CGDO={q["CGOP"]:.4g} CJ={CJ:g} PB={c["PB"]:.4g} MJ={c["MJ"]:.4g} TNOM=25)', '']


def body(q, active, addr_bits):
    """Decoder, delays and gate drivers for channels 1..active; channels active+1..8 are off dummies."""
    out = ['.param TDONE={max(TDON,TDOFF+22.5n)} TDAE={max(TDA,0)}',
           'Ben en_l 0 V=V(EN,GND)>1.4']
    for i in range(addr_bits):
        out.append(f'Ba{i} a{i}_l 0 V=delay(V(A{i},GND)>1.4,{{TDAE}})')
    for k in range(active):
        bits = [(k >> i) & 1 for i in range(3)]
        cond = ' & '.join([f'{"" if bits[i] else "!"}V(a{i}_l)>0.5' for i in reversed(range(addr_bits))])
        n = k + 1
        out += [f'Bsel{n} sel{n} 0 V=V(en_l)>0.5 & {cond}',
                f'Bdly{n} dly{n} 0 V=min(delay(V(sel{n}),{{TDONE}}),delay(V(sel{n}),{{TDOFF}}))',
                f'Rg{n} dly{n} ctl{n} 1k', f'Cg{n} ctl{n} 0 {{TEDGE/1k}}',
                f'Bgn{n} gn{n} VSS V=(V(VDD)-V(VSS))*V(ctl{n})',
                f'Bgp{n} gp{n} VDD V=-(V(VDD)-V(VSS))*V(ctl{n})']
        for side in 'AB':
            out.append(f'X{side}{n} S{n}{side} D{side} gn{n} gp{n} VDD VSS GND MUX507_SW '
                       f'params: RK={{RONK*(1+M{n}{side})}} CKS={{CKS}} CKD={{CKD}} QK={{QK}}')
    for n in range(active + 1, 9):
        for side in 'AB':
            out.append(f'X{side}{n} GND D{side} VSS VDD VDD VSS GND MUX507_SW params: RK={{RONK}} CKS={{CKS}} CKD={{CKD}} QK=0')
    out += ['* supply current (45 uA VDD, 26 uA VSS typ)', 'IqA VDD GND 19u', 'IqB VDD VSS 26u']
    return out


def body_2to1():
    """EN = the chip's A0 (chip EN tied high, GND = node 0): S0 = channel 1A, S1 = channel 2A; the B side
    does not touch D and is left out; channels 3A..8A stay as off switches on D."""
    out = ['.param TDONE={max(TDON,TDOFF+22.5n)} TDAE={max(TDA,0)}',
           'Ba0 a0_l 0 V=delay(V(EN)>1.4,{TDAE})']
    for n, (s, cond) in enumerate([('S0', '!V(a0_l)>0.5'), ('S1', 'V(a0_l)>0.5')], 1):
        out += [f'Bsel{n} sel{n} 0 V={cond}',
                f'Bdly{n} dly{n} 0 V=min(delay(V(sel{n}),{{TDONE}}),delay(V(sel{n}),{{TDOFF}}))',
                f'Rg{n} dly{n} ctl{n} 1k', f'Cg{n} ctl{n} 0 {{TEDGE/1k}}',
                f'Bgn{n} gn{n} VSS V=(V(VCC)-V(VSS))*V(ctl{n})',
                f'Bgp{n} gp{n} VCC V=-(V(VCC)-V(VSS))*V(ctl{n})',
                f'X{n} {s} D gn{n} gp{n} VCC VSS 0 MUX507_SW params: RK={{RONK*(1+M{n - 1})}} CKS={{CKS}} CKD={{CKD}} QK={{QK}}']
    for n in range(3, 9):
        out.append(f'X{n} 0 D VSS VCC VCC VSS 0 MUX507_SW params: RK={{RONK}} CKS={{CKS}} CKD={{CKD}} QK=0')
    out += ['* supply current (45 uA VCC, 26 uA VSS typ)', 'IqA VCC 0 19u', 'IqB VCC VSS 26u']
    return out


def params(q, active):
    mis = ' '.join(f'M{n}{s}=0' for n in range(1, active + 1) for s in 'AB')
    return [f'+ params: RONK=1 CKS=1 CKD=1 QK=0 TDON={q["TDON"]} TDOFF={q["TDOFF"]} TDA={q["TDA"]} TEDGE={q["TEDGE"]}',
            f'+ {mis}']


def lib(r, c, q):
    pins8 = 'VDD DB ' + ' '.join(f'S{k}B' for k in range(8, 0, -1)) + ' GND A2 A1 A0 EN ' + \
            ' '.join(f'S{k}A' for k in range(1, 9)) + ' VSS DA'
    out = [HEADER,
           '* MUX507: pins in SOIC/TSSOP order without NC pins:',
           '*   1 VDD, 2 DB, 4..11 S8B..S1B, 12 GND, 15 A2, 16 A1, 17 A0, 18 EN, 19..26 S1A..S8A, 27 VSS, 28 DA',
           f'.subckt MUX507 {pins8}'] + params(q, 8) + body(q, 8, 3) + ['.ends MUX507', '',
           '* MUX507_2CH: channels 1 and 2 of the same part. A0 = 0 -> S1A-DA, S1B-DB; A0 = 1 -> S2A-DA, S2B-DB; EN = 0 -> off.',
           '.subckt MUX507_2CH VDD VSS GND EN A0 S1A S2A S1B S2B DA DB'] + params(q, 2) + body(q, 2, 1) + \
          ['.ends MUX507_2CH', '',
           '* MUX507_2TO1: one 2:1 switch of the same part. EN = 0 -> S0-D, EN = 1 -> S1-D (one channel always on).',
           '* EN is the chip A0 (TTL 1.4 V vs ground, chip EN tied high): tt / tBBM timing. GND is node 0.',
           '.subckt MUX507_2TO1 VCC VSS EN S0 S1 D',
           f'+ params: RONK=1 CKS=1 CKD=1 QK=0 TDON={q["TDON"]} TDOFF={q["TDOFF"]} TDA={q["TDA"]} TEDGE={q["TEDGE"]} M0=0 M1=0'] + \
          body_2to1() + ['.ends MUX507_2TO1', ''] + switch_cell(r, c, q)
    return '\n'.join(out)


# Monte Carlo defaults carried by the symbols (uniform, typ +- (max - typ)); flat(x) is uniform in [-x, x]
MC_LINE = 'RONK={1+flat(0.36)} CKS={1+flat(0.43)} CKD={1+flat(0.17)} QK={flat(1)} TDON={65n+flat(54n)} TDOFF={31n+flat(15n)} TDA={15n+flat(46n)}'


def mc_mismatch(active):
    return ' '.join(f'M{n}{s}={{flat(0.036)}}' for n in range(1, active + 1) for s in 'AB')


DEFAULT_Q = dict(TDON='65n', TDOFF='31n', TDA='15n', TEDGE='5n', TOXN=60e-9, TOXP=60e-9, CGON=0.2e-9, CGOP=0.2e-9, RTC='0.0056,2.6e-5')

if __name__ == '__main__':
    import sys
    r = json.load(open(sys.argv[1]))
    c = json.load(open(sys.argv[2]))
    q = dict(DEFAULT_Q, **(json.load(open(sys.argv[3])) if len(sys.argv) > 4 else {}))
    open(sys.argv[-1], 'w').write(lib(r, c, q))
