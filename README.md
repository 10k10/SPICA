# SPICA — Spice Parallel Instancer Circuit Analysis

Portable Windows app (C# / .NET Framework 4.8 / WinForms, Windows 98 look) that runs LTspice
Monte Carlo campaigns as many parallel LTspice batch instances, tuning the parallelism to the
machine it runs on.

`dist/SPICA.exe` is the whole app (~260 KB). Copy it anywhere; settings (`SPICA.ini`) and
scheduler profiles (`SPICA.profiles`) are written next to it.

## How a campaign works

1. **Open** an `.asc`. SPICA runs `LTspice -netlist`, lists every net (`V(x)`) and device current,
   and finds the `.meas` statements.
2. Pick the **signals to save**. SPICA writes a `.save` directive with only those, so the `.raw`
   stays small. Signals used by `.meas` are always saved (locked), because `.meas` reads the `.raw`.
3. Type the **number of runs**. Any `.step` in the schematic is replaced by SPICA's own
   `.step param run a b 1` per chunk (a single-run chunk uses `.param run=N`, because LTspice refuses a one-point `.step`).
4. **Start**. Each chunk is launched as `LTspice.exe -b chunk.net -ini <private ini>`:
   - The private ini copies the user's LTspice settings and forces `UseClocktoReseedMC=true`
     plus the keys that stop start-up dialogs (analytics / web sync) from blocking batch mode.
     The user's own `LTspice.ini` is never modified.
   - Launches are staggered by 100 ms; at the end a duplicate check flags identical result rows.
   - `.options threads=N` limits the threads each instance may use.
5. Results: `results.csv` (run, temp, status, one column per `.meas`), `summary.txt`, `campaign.log`,
   `campaign.spica` (the setup used, with the actual seed), `samples.csv` (paired mode: every random
   value of every run) and the chunk `.net/.log/.raw` files in `<schematic>_SPICA/<timestamp>/`.

A stepped `.op` makes LTspice report one `.meas` row for the whole sweep, so for `.op` circuits
each chunk gets a dummy first `.step param spica_op list 0 1`, which restores one row per run.

## Stopping, resuming, long campaigns

- `results.csv` is rewritten (atomically, at most once a second) whenever a chunk finishes, and
  `campaign.state` records Running / Stopped / Finished with the progress. A crash or a closed
  window loses only the simulations that were in progress.
- **Stop** offers three choices:
  - **Analyze now**: stop at once and show the finished runs.
  - **Finish running**: start nothing new, wait for the simulations in progress, then analyze.
  - **Discard**: delete the campaign folder, after a confirmation.
- **File > Open Results** on an unfinished folder offers **Resume**. It reloads the campaign's own
  `campaign.spica` (same seed, temperatures and signals) and simulates only the missing and failed runs.
  - In paired mode the result is identical to an uninterrupted campaign: tested on 1200 rows, 0 differ.
  - If the schematic changed since the campaign started, SPICA warns before resuming.
- While a campaign runs, Windows is kept from sleeping. The display may still turn off.

## Setup file (.spica)

Everything needed to repeat a campaign and its analysis: schematic (relative path), runs, seed,
paired mode, temperature mode, saved signals, spec limits per measurement and the TF definition.
`File > Save Setup` writes it; opening an `.asc` also loads `<name>.spica` next to it when present.
Seed 0 means "a new seed per campaign"; the seed actually used goes into the campaign folder.

## Temperature and paired samples

- **Default**: no `.temp`, the schematic decides.
- **Corners** (e.g. -40, 25, 90): every run is simulated at each temperature (`.temp T` per chunk;
  nested `.step temp` cannot be used because LTspice then logs `.meas` for one slice only).
- **Random flat**: each run gets its own temperature in [min, max] (`.temp {spica_temp}` from a table),
  recorded in the `temp` column.
- **Paired samples** (on by default when a temperature mode is chosen): SPICA replaces every
  `mc()`, `flat()` and `gauss()` in its chunk copy with a parameter whose value is a pure function
  of (seed, site, run), injected as `table(run, ...)`. Run 37 is then the same unit at every
  temperature and in any chunk, and the campaign is reproducible from its seed. `.func` bodies that
  draw random numbers are inlined at each call so every call stays independent. Random values
  inside `.subckt` definitions are left to LTspice (warned). LTspice's own `rand()` was not used: its
  consecutive values show a small but systematic correlation (about -0.03).

## Results analysis

Results tab, for the running campaign or any folder opened with `File > Open Results`:

- **Error Budget**: each error the design must keep inside its boundaries, in one of two styles:
  a `.meas` that already is the error, or an output compared run by run with a reference
  (error = output - (gain x reference + offset), or any ideal expression of x and T), optionally
  referred to the input. Boundaries symmetric (+/-) or lower/upper (either side may be open), in
  absolute units, % of reading or % of full scale. Judged per temperature and input band: mean +/-
  k sigma (default 6) must stay inside both boundaries and the error must be bell-shaped
  (Anderson-Darling); PASS / FAIL / CHECK, figures in boxes (mean, sigma, room on each side in
  sigma), a table of every condition, the histogram or error vs input, and the tolerances that drive it.
- **Table**: every run, out-of-spec values in red, filter and sort.
- **Statistics**: n, mean, sigma, min/max, median, 0.135/99.865 % percentiles, skewness, kurtosis,
  Anderson-Darling normality, spec limits, observed yield with exact (Clopper-Pearson) 95 % CI, Cpk,
  normal-model ppm. The sigma analysis (mean +/- k sigma, predicted ppm) is shown only when the
  sample is consistent with a normal distribution; otherwise percentiles are used.
- **Histogram** with normal fit, +/-3 sigma and spec lines.
- **Scatter & TF**: finds the TF: fitted line (gain, offset with CIs, R2), the scatter around it,
  and an optional ideal (`2*x`, `x*(1+50e-6*(T-25))`, SPICE suffixes accepted) with its gain and
  offset error. *Use as ideal in Error Budget* / *Use the fitted line* send it to the Error Budget,
  where the boundaries and the verdict live.
- **Temperature**: corners give per-temperature statistics and, with paired samples, per-unit drift,
  tempco (ppm/°C) and the yield of units passing every spec at all temperatures; a random range gives
  the regression on temperature, tempco and the RSS split of the spread into temperature and tolerances.
- **Sensitivity**: with paired samples SPICA knows every random draw, so it regresses each
  measurement on all random parameters (and on temperature in random-temperature campaigns):
  share of the variance per input (standardized coefficient squared, adding up to R2), signed
  effect, and the shift of the measurement with the input at its tolerance limit, as a tornado chart
  and a table (double-click an input for its scatter plot). Without paired samples: correlation with
  the other measurements.
- **Convergence**: running mean with its 95 % band, running sigma and yield.
- **Charts**: drag a box to zoom (a flat box zooms X only), wheel to zoom (Shift: X, Ctrl: Y),
  middle-drag to pan, double-click to reset, click a legend entry to hide a series; right-click to
  copy or save the chart as PNG or SVG. Histogram view: histogram (bins selectable), cumulative
  distribution or normal probability plot. Scatter axes: measurements, TEMP, random parameters or
  the run number.
- **Live**: while a campaign runs the tab refreshes every second; untick *Live update* to freeze the
  snapshot while you analyse it.
- **Report**: one self-contained HTML file: campaign summary, contents, distribution and
  specification tables, then one section per measurement (key numbers, histogram, convergence,
  temperature) and the transfer function. Charts are vector (SVG), sharp when zoomed or printed;
  printing starts each measurement on a new page.

The statistics were checked against SciPy (Anderson-Darling, skewness, kurtosis, quantiles,
Clopper-Pearson, normal tail ppm, regression standard errors).

## Monitor

Progress, timing, the scheduler's plan and per-instance status, a throughput graph and a resource
meter (CPU and RAM of the whole machine, space used on the output drive) in the style of Windows 98.
The CPU figure is Windows' *% Processor Utility*, the same as Task Manager (it counts turbo clocks,
so it reads higher than the share of busy time). The remaining time plays the runs left out on the
instances round by round, so the last, partly filled round counts whole.

Each run takes longer in a campaign than alone in LTspice (all cores busy: lower clocks, shared
cache and memory bandwidth); the campaign still finishes far sooner than the runs one by one.

## Scheduler (Automatic mode)

One fixed rule, printed in the log as the campaign's plan:

- **Instances** N = physical cores (or the manual value), 1 thread each; fewer if free RAM
  divided by the peak RAM of one instance is smaller. The memory guard only ever lowers N.
- **Run time** x: each instance first simulates one run alone; x is then tracked from LTspice's
  own simulation time.
- **Chunk** k = the runs that fit in about 2 minutes (k = 120 s / x), never more than an equal
  share of what is left (so all instances finish together) and never a .raw over ~1.5 GB.
  Two minutes keeps LTspice start-up (~1 s) under 1 % of the work and saves progress often.
- **Total** about runs x x / N, e.g. `Plan: 1000 runs x 2 s = 33 min of simulation / 6 instances
  = about 6 min; chunks of 59 runs (~118 s) -> 17 launches.`
- x is remembered per machine and circuit, so the next campaign plans from the first chunk.
- A running instance that opens a window (e.g. a dialog) is detected and killed; failed chunks
  are retried run by run.

## Verified on this machine (Ryzen 5 5500X3D, 6C/12T, LTspice 26.0.1)

| Finding | Result |
|---|---|
| Reseed **off**, two runs | identical values, so chunks would repeat the same random sequence |
| Reseed **on**, 4 simultaneous launches | all different |
| `-ini` placement | must come **after** the netlist: `-b file.net -ini x.ini` (before it, LTspice opens the GUI) |
| Private ini without `CaptureAnalytics=false` | LTspice blocks on the analytics dialog |
| 300 runs, small RC (`samples/rc_mc.asc`) | 12 s, 6 instances, no duplicates |
| 400 runs, ~1 s/run (`samples/rc_mc_heavy.asc`) | climbed 6 → 8 → 10 → 12 instances, +47 % vs 6 |
| End-of-campaign chunk floor (1000 fast runs) | 23 s → 16 s |
| Paired corners, `samples/rc_tc.asc` (tc = 4000 ppm/°C) | measured per-unit tempco 4032–4035 ppm/°C vs 4032 expected |
| `.op` dummy step, `samples/divider_op.asc` | 120 runs, every value equal to the exact divider formula (error < 3e-8) |

## Op-amp models with datasheet tolerances

`Tools > Op-amp Models` lists op-amps modelled from their datasheets: OPA4991, OPA4182, LMH6619 (SPICA_OA)
and the AD8139 fully differential amplifier (SPICA_FDA). Place LTspice's `opamp2` symbol (or the AD8139
symbol) and set its value to `<PART>_MC`, e.g. `OPA4991_MC`. When the campaign runs SPICA adds the
part's tolerances to the instance (offset and its drift, bias and offset current, CMRR, PSRR, open-loop
gain, ...), so they are paired and named per unit (`U1.Vos`) in Sensitivity, Worst Case and the Error
Budget. Datasheet limits are flat over the limit; a value given only as typical is flat over 3x typical.
Override any of them on the symbol (`Vos={gauss(277u)}`, or `Vos=0` to hold it). *Save Library* writes
`SPICA_opamps.lib`, so the schematic also runs in LTspice with `.lib SPICA_opamps.lib`. The models are
built for DC and low-frequency accuracy (one pole, slew rate, rails, current limit). Sample:
`samples/opamp_gain2.asc`.

## Heavier sample circuits

Each comes with a ready `.spica` setup next to it (opening the `.asc` loads it): run count, specs,
TF and temperatures. Only built-in LTspice parts are used. Schematics are net-labelled (every pin
carries a net name). Times are single LTspice runs on the machine above; 96-run campaigns all
completed with 0 failures.

| Sample | What it exercises | s/run | Setup |
|---|---|---|---|
| `buck_hysteretic.asc` | 12 → 3.3 V hysteretic buck, BSC050N03LS + MBRS340, 1 A load step. vout, ripple, variable fsw, eff, droop | 0.53 | 500 runs, specs |
| `classd_amp.asc` | ±12 V half bridge, 400 kHz carrier, random input amplitude (`flat`). TF `12*x`: about −2.5 % gain error (switch/inductor R vs 8 Ω) | 0.30 | 500 runs, TF 2 % |
| `sigma_delta.asc` | CT 2nd-order ΣΔ ADC, 1 MHz D flip-flop, 4000 samples. TF `x`; gain scatter from 1 % R1/R2, offset from `gauss` Vos | 0.53 | 500 runs, TF ±0.005 FS (abs) |
| `colpitts_osc.asc` | 10.3 MHz 2N3904 Colpitts, 100 µs at ≤ 1 ns steps. freq, vpp, start-up time | 0.60 | 200 runs × 3 corners, paired |
| `linear_psu.asc` | 50 Hz bridge + 2200 µF + zener/darlington regulator, 300 ms. headroom (dropout) and ripple; Vz/Vbe tempco | 0.26 | 200 runs × 3 corners, paired |

The ΣΔ needs an absolute budget: with a bipolar input, a % budget explodes near 0 V.

## Build

```
dotnet build src/SPICA/SPICA.csproj -c Release
```

`tests/SPICA.Smoke` runs a headless campaign: `SPICA.Smoke.exe <file.asc> <runs> [instances threads]`
(environment: `SPICA_TEMP=corners:-40,25,90` or `random:-40,90`, `SPICA_PAIRED=1`, `SPICA_SEED=n`, `SPICA_NORAW=1`).
`--stats <folder> <meas> [lsl usl]` prints the analysis of a campaign folder; `--ui <folder> <pngdir>` and
`--setup <file> <png> [run]` render the windows off-screen for checking.
`--resume <file.asc> <runs> <corners>` stops a paired campaign part-way, resumes it and compares it with an
uninterrupted one (it must match exactly). It also tests the finish-running stop.

Icons: [alexh/vintage-icons](https://github.com/alexh/vintage-icons) (MIT).
