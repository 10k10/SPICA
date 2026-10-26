# MUX507 LTspice model

A model of the TI MUX507 (8:1 differential or dual 8:1 precision mux), built from the datasheet
(SBAS803A) for LTspice. Its structure follows the device: each switch is the transmission gate shown
in datasheet Fig. 38, an NMOS and a PMOS in parallel with their bodies on VSS and VDD and gates driven
rail to rail. The transistor parameters were fitted to the datasheet curves. Those curves were read
exactly from the PDF's vector paths, not by eye.

| File | |
|---|---|
| `MUX507.lib` | subcircuits `MUX507` (8 channels), `MUX507_2CH` (channels 1 and 2) and `MUX507_2TO1` (one 2:1 switch) |
| `MUX507.asy`, `MUX507_2CH.asy`, `MUX507_2TO1.asy` | symbols with the Monte Carlo defaults; keep them next to the `.lib` and your schematic |
| `mux507_rc_glitch.asc` | demo (`MUX507_2TO1`): 3 kHz sine → RC low-pass → S0, toggling with S1, against an identical filter with no mux |
| `mux507_scan_160k.asc` | demo of the real use: RC-filtered sine read at 160 kSPS per channel with a GND discharge between readings, sampled by an ideal ADC |
| `fit/` | extraction, fitting, model and verification scripts plus the extracted datasheet curves |

## Pins and logic

- **`MUX507`:** VDD DB S8B..S1B GND A2 A1 A0 EN S1A..S8A VSS DA (SOIC order, NC pins left out).
- **`MUX507_2CH`:** VDD VSS GND EN A0 S1A S2A S1B S2B DA DB.
  - A0 = 0 connects S1A to DA and S1B to DB; A0 = 1 connects S2A to DA and S2B to DB.
  - EN = 0 turns everything off.
  - The six unused channels stay inside as permanently-off switches, so DA/DB keep the real drain
    capacitance. At nominal values it is identical to the 8-channel model.
- **`MUX507_2TO1`:** VCC VSS EN S0 S1 D, drawn as the classic mux trapezoid.
  - EN = 0 connects S0 to D; EN = 1 connects S1 to D. One channel is always on.
  - EN is wired as the chip's A0 with the chip's EN tied high, so a switch-over has the
    address-transition timing (tt, break-before-make). The logic reference GND is node 0.
  - Channels 3–8 stay on D as off switches; the B side is left out (it does not touch D).
    At nominal values the demo gives the same results as with `MUX507_2CH` (glitch 5.47 mV, −33.8 ppm).
  - Mismatch parameters are `M0` and `M1`.
- **Logic thresholds:** TTL, 1.4 V referred to GND.

## Monte Carlo (instance parameters)

The symbols carry uniform defaults over typ ± (max − typ) from the datasheet. `flat(x)` is uniform in
[−x, x]. SPICA's paired generator takes all of them over. Edit them on the instance (SpiceLine and
SpiceLine2); `RONK=1 CKS=1 CKD=1 QK=0` with the `M..` parameters at 0 gives a nominal part.

| Parameter | Datasheet basis | Default |
|---|---|---|
| `RONK` Ron of the whole part | 125 typ / 170 max Ω | `1+flat(0.36)` |
| `M1A` … `M8B` per-switch Ron offset | ΔRon 6 typ / 9 max Ω | `flat(0.036)` each |
| `CKS` source-side capacitance | CS(OFF) 2.1 / 3.0 pF | `1+flat(0.43)` |
| `CKD` drain-side capacitance | CD(OFF) 6.4 / 7.5 pF | `1+flat(0.17)` |
| `QK` charge-injection imbalance | ±0.9 pC full range at ±15 V (no max published at ±5 V) | `flat(1)`: ±0.3 pC at ±5 V |
| `TDON` on delay | tON 82 / 136 ns | `65n+flat(54n)` |
| `TDOFF` off delay | tOFF 63 / 78 ns | `31n+flat(15n)` |
| `TDA` address-decoder delay | tt 97 / 143 ns | `15n+flat(46n)`, clamped ≥ 0 |

Break-before-make is enforced in every sample: the on delay is at least the off delay + 22.5 ns,
which keeps the measured tBBM ≥ 30 ns as the datasheet guarantees. The fastest-on/slowest-off corner gives 40.7 ns.

## How well it matches the datasheet (nominal part, LTspice vs datasheet, 25 °C unless stated)

| Quantity | ±5 V (your case) | Other supplies |
|---|---|---|
| Ron vs VS (Fig. 3) | 2.6 % rms, peak 645 vs 646 Ω | ±7 V 4.3 %, 12 V 2.9 %, ±15 V 8 % (model low by ~15 Ω at high VGS) |
| Ron vs temperature (Fig. 4, 12 V) | | 2.9–4.7 % rms from −40 to 125 °C |
| CS(OFF), CD(OFF), CD(ON) (Fig. 20, 24) | predicted 2.3 / 7.8 / 10.1 pF at VS = 0 | 3–5 % rms at ±15 V and 12 V |
| tON / tOFF / tt / tBBM | predicted 94 / 62 / 101 / 53 ns | ±15 V: 81 / 63 / 92 / 42 ns vs datasheet 82 / 63 / 97 / 30–54 |
| Charge injection (Fig. 12) | −0.02 pC nominal vs datasheet −0.19…+0.11 pC | magnitude right, shape not reproduced |

Not modelled: leakage (1 pA typical, 10 nA max at 85 °C, which is 47 µV through 4.7 kΩ), ESD clamps,
and behaviour above about 10 MHz.

## What the demo shows (±5 V, 2 V peak sine, switch-on at the sine peaks)

| | 4.7 kΩ / 1 nF | 330 Ω / 10 nF |
|---|---|---|
| Dip on the filter node at switch-on, 20 pF on DA | 53 mV | 5.5 mV (5.0–5.9 mV over 200 Monte Carlo parts) |
| Same, 100 pF on DA | 186 mV | 20 mV |
| Error of the 3 kHz RMS value, 20 pF on DA | −518 ppm | −34 ppm (−32 to −35 ppm) |

The dip is charge sharing: DA still holds the previous channel's voltage, and at switch-on
CD(ON) plus everything on DA (`CLOAD`) shares charge with the filter capacitor:
ΔV ≈ (C_D + C_LOAD) / (C_F + C_D + C_LOAD) × (V_prev − V_S). Ron hardly matters, and charge injection
adds about 0.02 mV. So the capacitance on DA (buffer or ADC input, PCB) is the number to get right.

Timestep: the demo with a 1 ns maximum step gives the same results (within 0.1 %) in 126 s;
with LTspice's automatic step it takes 1 s.

## Scan demo (`mux507_scan_160k.asc`)

The filtered signal is on S0 and S1 is GND (discharge). `FS` is the sample rate per channel. The frame
1/FS = `NCH` x (`TGND` + `TSIG`). By default the channel is on for 390.6 ns every 6.25 µs (1/16 of the time).
Change `TGND` (discharge time) and `TSIG` follows. Outside its slot D sits on GND, because every other channel
is preceded by a discharge slot. An ideal sample-and-hold takes the ADC sample at the end of the slot
plus `TADC` (negative = earlier). The load is the op-amp input, 8 MΩ || 1.5 pF (`RLOAD`, `CLOAD`).

Each connection drains the filter capacitor into the discharged drain (~10 pF chip + `CLOAD`). The filter
recovers through RF with a time constant of about 4 µs, which is not complete within the 6.25 µs frame.
The result is almost entirely a gain error.

At ±5 V the channel really connects about 140 ns after the EN edge, and D has settled about 180 ns after it.
It stays on until about 90 ns after the slot ends. So in a 390 ns slot, only the last ~100 ns (plus ~50 ns
after the edge) give a valid sample.

Nominal part, 2 V 3 kHz, gain error (= error of an RMS reading) vs sample point:

| ADC sample (from slot end) | 330 Ω / 10 nF | 4.7 kΩ / 1 nF | 100 Ω / 33 nF |
|---|---|---|---|
| −150 ns | −4725 ppm (switch still turning on) | −1.5 % | −2748 ppm |
| −100 ns | −1107 ppm | −1.22 % | −372 ppm |
| 0 (end of slot) | −1070 ppm | −1.19 % | −361 ppm |
| +50 ns | −1130 ppm (switch turning off) | −1.19 % | −415 ppm |
| Dip on the filter | 2.2 mV | 25 mV | 0.67 mV |

SPICA, 200 paired Monte Carlo runs (mux + 1 % R + 5 % C), 330 Ω / 10 nF, end of slot: gain error
−1071 ± 86 ppm (range −1254 .. −906), all runs inside the valid sampling window. About 5 s per run.
