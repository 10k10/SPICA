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

- **Table**: every run, out-of-spec values in red, filter and sort.
- **Statistics**: n, mean, sigma, min/max, median, 0.135/99.865 % percentiles, skewness, kurtosis,
  Anderson-Darling normality, spec limits, observed yield with exact (Clopper-Pearson) 95 % CI, Cpk,
  normal-model ppm. The sigma analysis (mean +/- k sigma, predicted ppm) is shown only when the
  sample is consistent with a normal distribution; otherwise percentiles are used.
- **Histogram** with normal fit, +/-3 sigma and spec lines.
- **Scatter & TF**: best average linear TF (gain, offset with CIs, R2), through-origin gain, an
  optional theoretical TF (`1.93*x`, `x*(1+50e-6*(T-25))`, SPICE suffixes accepted) with an error
  budget in % or absolute units: per-sample error, % inside budget with CI, k-sigma bound, sigma
  margin, and the split into gain error, offset error and random scatter combined by RSS and as a
  linear sum, plus what remains after trimming gain, or gain and offset.
- **Temperature**: corners give per-temperature statistics and, with paired samples, per-unit drift,
  tempco (ppm/°C) and the yield of units passing every spec at all temperatures; a random range gives
  the regression on temperature, tempco and the RSS split of the spread into temperature and tolerances.
- **Convergence**: running mean with its 95 % band, running sigma and yield.
- **Report**: one HTML file with every table, chart and narrative.

The statistics were checked against SciPy (Anderson-Darling, skewness, kurtosis, quantiles,
Clopper-Pearson, normal tail ppm, regression standard errors).

## Adaptive scheduler (Automatic mode)

- Starts at one instance per physical core with 1 thread each (or from the best configuration
  saved for this machine + circuit).
- Measures throughput per configuration from LTspice's own simulation time, so the score does
  not depend on chunk size.
- Hill-climbs: more instances, fewer instances, or half the instances with twice the threads;
  keeps a change only if throughput improves by more than 3 %. It stops probing when too little work is left.
- Chunk size: big enough that LTspice start-up (~0.9–1.3 s here) stays under ~5 % of the
  work, capped in duration so the controller can react, and shrunk near the end (guided
  self-scheduling) so all instances finish together, but never below one start-up worth of simulation per chunk.
- Memory guard from the observed peak RAM per instance; a running instance that opens a window
  (e.g. a dialog) is detected and killed; failed chunks are retried run by run.

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
