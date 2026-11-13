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
4. **Add to Queue** (F5). The campaign starts at once, or waits its turn behind the ones already there
   (see *Queue*). Each chunk is launched as `LTspice.exe -b chunk.net -ini <private ini>`:
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

One setup per circuit, for both jobs: **schematic + setup** to simulate, **results + setup** to
analyse. Opening a campaign folder pairs it with the setup of its schematic: the one open now,
else the `.spica` next to the schematic, else the campaign's record as an unsaved setup (save it
with `File > Save Setup As`). Error budget, specs, TF and report edits on the Results tab go into
that setup (`*` in the title, saved with Ctrl+S), so one `.spica` analyses every campaign of the
circuit, old ones included. The `campaign.spica` in a campaign folder is the record of how that
campaign ran; it is used for the analysis only when the schematic is not found.

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
  draw random numbers are inlined at each call so every call stays independent.
  Random values inside sub-circuits are drawn by SPICA too, per instance: SPICA reads the
  sub-circuits the netlist defines and the ones in the files it includes (`.lib`, `.include`,
  found as LTspice finds them, nested sub-circuits and `.model` lines inside them too), gives each
  definition one parameter per random value (`spica_r1..n`) and passes every instance its own
  paired values, named by where they are (`U3.R5`, `U3.X1.Rin`). Library sub-circuits are copied
  into the campaign's netlist renamed (`NAME__spica`); the files are not touched. A `.include`
  file with random values at its own top level (a netlist saved as a library) is placed into the
  netlist so those are paired as well. Encrypted models cannot be read: SPICA warns, and their
  tolerances, if any, stay with LTspice. LTspice's own `rand()` was not used: its
  consecutive values show a small but systematic correlation (about -0.03).

## Results analysis

Results tab, for the running campaign or any folder opened with `File > Open Results`:

- **Error Budget**: each error the design must keep inside its boundaries, in one of two styles:
  a `.meas` that already is the error, or an output compared run by run with a reference
  (error = output - (gain x reference + offset), or any ideal expression of x and T), optionally
  referred to the input. Boundaries symmetric (+/-) or lower/upper (either side may be open), in
  absolute units, % of reading or % of full scale. A `.meas` error may name its input too.
  Judged per temperature and across the input: an input swept in levels is judged level by level;
  an input that varies continuously gets an envelope (mean error fitted up to a cubic in the input,
  its variance as a quadratic, exact for offset and gain tolerances), so mean +/- k sigma widens
  where the error spreads more and is judged at 11 points across the range. Mean +/- k sigma
  (default 6) must stay inside both boundaries and the error must be bell-shaped (Anderson-Darling,
  one test per temperature on the residuals over their local sigma); PASS / FAIL / CHECK, figures
  in boxes (mean, sigma, room on each side in sigma), one row per temperature with its tightest
  point, the error vs input with the envelope and the worst point (or the histogram), and the
  tolerances that drive it.
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
  The circuit's **input** (a random parameter sweeping the operating range, e.g. the input voltage)
  drives the outputs by design and would fill the chart with one 99.99 % bar. It is set apart: SPICA
  first removes its own effect (a cubic in the input, so a nonlinear transfer goes too), then shares
  what is left among the tolerances, each with a term that grows with the input, so a gain error
  counts in full even when the input sweeps around zero. The chart title gives the input's share
  and the narrative the sigma of what is left. The input is found automatically (the reference x of
  an error or of the TF when it is a parameter, or a parameter that sets it, |r| > 0.95) or chosen
  with *Inputs...*; *Range* shows one band of the input (five bands, equal numbers of runs). The
  choice is saved with the setup. In the Error Budget the error is already referred to the ideal TF,
  so an input that still explains most of it is flagged as a systematic error (gain, offset or
  nonlinearity), not a tolerance.
- **Convergence**: running mean with its 95 % band, running sigma and yield.
- **Charts**: drag a box to zoom (a flat box zooms X only), wheel to zoom (Shift: X, Ctrl: Y),
  middle-drag to pan, double-click to reset, click a legend entry to hide a series; right-click to
  copy or save the chart as PNG or SVG. Histogram view: histogram (bins selectable), cumulative
  distribution or normal probability plot. Scatter axes: measurements, TEMP, random parameters or
  the run number.
- **Live**: while a campaign runs the tab refreshes every second; untick *Live update* to freeze the
  snapshot while you analyse it.
- **Report**: one self-contained HTML file. *Results > Report...* asks what goes in:
  - *Error budget only*: one page per error ticked, for checking a requirement: the requirement as
    entered (definition, boundaries, % of full scale also in absolute terms, the k-sigma criterion),
    the verdict and the calculated numbers at the worst point (mean, sigma, mean ± k sigma, room on
    each side, bell-shaped), the histogram, the error across the input with its envelope and against
    temperature, and what drives it;
  - *Full analysis*: the error budget, summary tables and one section per measurement, with the
    measurements and the sections (histogram, sensitivity, temperature, convergence, transfer
    function) chosen by checkboxes.

  The dialog also takes a *Title* (the report's heading; empty = the campaign folder's name; the
  schematic is listed under it) and free *Notes* shown at the top. All of it is saved with
  the setup and the campaign folder. The file is named after the title (else the schematic),
  `<title>_report.html` or `<title>_error_budget.html`, in the campaign folder;
  if that name is taken, a timestamp is added. Setup > *Write the report when it ends* saves it
  automatically when the campaign ends. It is laid out like a datasheet: numbered sections, grid tables,
  numbered figures with their caption underneath, the SPICA icon at the top right and a footer that
  says when it was generated. Charts are vector (SVG), sharp when zoomed or printed;
  printing starts each measurement (and each error of the short report) on a new page.
  The report ends with *Traceability*: when the campaign ran (start, end, simulating time), on
  which computer (name, user, Windows build, CPU, cores, RAM), with which SPICA, LTspice and
  LTspice model library, the seed, and every file simulated with its SHA-256.

## Source bundle (reproducing a campaign)

When a campaign starts, SPICA writes `<schematic>_sources.zip` into the campaign folder: a snapshot
of what is simulated and where.

- the schematic, its hierarchical sheets and the symbols that do not ship with LTspice (found
  beside the schematic, in LTspice's `lib\sym` or in the Control Panel's symbol search paths);
- every model file the netlist uses (`.lib`, `.include`, PWL `file=`), including LTspice's own
  `standard.*` files and anything those files include, found as LTspice finds them: beside the
  schematic, `lib\sub`, `lib\cmp`, the library search paths;
- the setup, named like the schematic, with the campaign's seed, and the netlist SPICA simulated;
- `SPICA_bundle.txt`: start time, computer, Windows, CPU/RAM, SPICA and LTspice versions, the
  date of LTspice's model library, search paths, each file with its SHA-256 and original path.

To run it again: unzip anywhere and open the schematic in SPICA; its setup loads with it, and the
library references are redirected to the copies in the bundle (the status bar says how many), so
the same models are simulated even if the originals changed or the PC is another. With SPICA's
paired generator the results are identical run by run; with LTspice's `mc()` reseeded from the
clock only the statistics repeat. A resumed campaign keeps its first bundle.

The statistics were checked against SciPy (Anderson-Darling, skewness, kurtosis, quantiles,
Clopper-Pearson, normal tail ppm, regression standard errors).

## Queue

**Add to Queue** takes the campaign as the Setup tab shows it at that moment (schematic netlist, runs,
temperatures, signals, seed) and puts it in line. One campaign runs at a time, with all the instances
the scheduler gives it; the next one starts by itself when it ends. The Setup tab is free again as
soon as a campaign is added: change a value, open another schematic, add the next one.

- The **Queue** tab lists every campaign: its analysis, status, progress, when it starts or ends and
  how long it takes (from the scheduler's profile of that circuit, once it has run before), who added
  it (this PC or the listener) and how it ended.
- A waiting campaign can be moved up or down or removed. **Hold the queue** finishes the running
  campaign and starts nothing new. **Stop** acts on the running one (Analyze now, Finish running,
  Discard); the next campaign then starts unless the queue is on hold.
- Double-click (or **Open Results**) shows a campaign in the Results tab: a finished one from its
  folder, the running one live.
- One message tells how the campaigns added here ended, when the last of them has ended, so that a
  night of campaigns leaves one message and not one per campaign.
- Under the list, the campaign that runs: progress, timing, the scheduler's plan and per-instance
  status, a throughput graph and a resource meter (CPU and RAM of the whole machine, space used on the output drive) in the style of Windows 98.
The CPU figure is Windows' *% Processor Utility*, the same as Task Manager (it counts turbo clocks,
so it reads higher than the share of busy time). The remaining time plays the runs left out on the
instances round by round, so the last, partly filled round counts whole.

Each run takes longer in a campaign than alone in LTspice (all cores busy: lower clocks, shared
cache and memory bandwidth); the campaign still finishes far sooner than the runs one by one.

## Listener

**Listener** (toolbar, Ctrl+L) makes this SPICA run whatever campaign appears in a folder, e.g. a share
of a workstation that several people use. It takes two folders: the one to **watch** and the one for
the **results** (suggested from the first: `<watched>_results` beside it; any other folder will do,
except the watched one itself).

- A campaign is a setup (`.spica`) with its schematic beside it, or a bundle (`.zip`). From another PC,
  **File > Send to a Listener Folder** writes the campaign of the Setup tab as such a bundle: schematic,
  setup, netlist and the model files it uses.
- The folder is looked at every 4 seconds. A file is taken when it has stopped changing between two
  looks (it may still be copying), goes to the queue as any other campaign, and its results are written
  to `<results>\<file name>\<timestamp>\`.
- Which campaigns ran and which did not: the listener window lists every file with its status (waiting,
  queued with its place in line, running with progress and the time it ends, done, stopped, failed and
  why) and the results folder. The same is written as text to `SPICA_listener.txt` in the watched
  folder (refreshed at every change and at least every 30 s), so that whoever dropped the file can
  follow it without access to the workstation's screen.
- Each file runs once. A file that changes is a new campaign; **Run Again** repeats one as it is.
  What ran is kept in `SPICA_listener_jobs.tsv` in the results folder, so closing SPICA loses nothing:
  the listener starts again with SPICA, and campaigns that were waiting or running are taken again.

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

## License

Copyright © 2026 10k10. Free to use, copy, modify and share, at home or at work, for any
purpose; it may not be sold. Terms: Apache License 2.0 with the Commons Clause condition, see [LICENSE](LICENSE).

Icons: [alexh/vintage-icons](https://github.com/alexh/vintage-icons) (MIT).
LTspice is a trademark of Analog Devices, Inc.; SPICA is not affiliated with Analog Devices.
