# SPICA — Simulation Platform for Investigation, Compliance and Analysis

Portable Windows app (C# / .NET Framework 4.8 / WinForms, Windows 98 look) for studying a circuit on LTspice.
A study is drawn as a graph of blocks: the schematic, the analyses (nominal, Monte Carlo, worst case), and the
simulation that runs them. SPICA runs it as many parallel LTspice batch instances, tuned to the machine, and shows
the waveforms of every run.

`dist/SPICA-<version>.exe` is the whole app. Copy it anywhere; its settings (`SPICA.ini`) and the run times it
learns (`SPICA.profiles`) are written next to it.

This version is deliberately small: the parts that are here are meant to work well, and the rest comes back
one piece at a time.

## Starting

1. **Open** an LTspice schematic (`.asc`), from File > Open, the toolbar, or by dropping it on the window.
   SPICA makes a study of it, ready to run: the schematic, every tolerance nominal, simulated.
2. **Run** (F5). The study's campaigns go into the queue; the Queue tab follows the one that runs.
3. When the study ends, the **Results** tab opens with its waveforms.
4. **Save** keeps the project (`.spica`, beside the schematic): its studies. Opening the schematic again opens
   its project with it.

## The Workbench

- **Left:** the study shown (with New, Rename and Delete beside it), its blocks as a tree with their state, and
  the block library. Double-click a block in the library, or drag it onto the canvas.
- **Middle:** the canvas.
  - Drag the empty canvas to pan, use the wheel to zoom, press Space to fit the study.
  - Drag from an output to an input of the same colour to wire them; a wire of the wrong type, or one that would
    make a loop, is refused with the reason. Right-click a port to add a block that fits it, already wired.
  - Del removes the block or wire selected.
- **Right:** while a block is selected, its settings. Changes apply at once.

A block that misses something it needs (a schematic, an input not wired, a setting that is not a number) has a
red border, and its settings say what. **Run** says it too: pressed while something is missing, it lists what,
and runs nothing.

| Family | Block | What it does |
|---|---|---|
| Circuit | **Schematic** | An `.asc`, simulated as drawn. Empty: the project's own schematic. |
| Analysis | **Nominal** | One run, every tolerance at its nominal value. |
| | **Monte Carlo** | N units drawn from the tolerances (runs, seed, paired). |
| | **Worst Case** | The nominal unit, then every combination of the tolerance edges: 1 + 2^n runs. |
| Simulation | **Simulate** | Runs the circuit under every analysis wired into it, one campaign each. Save: the signals kept in the `.raw`; the ones the schematic's `.meas` read are always kept (ticked, marked `(.meas)`); none other ticked: all. |

Every analysis has a **Temperature**: empty for the schematic's own, one value, or several (`-55; 25; 85`):
every run at each.

Running a study again reuses what already ran: a campaign whose netlist and analysis did not change comes from
the results on disk at once. Change a block and only what it feeds runs again (the tree marks it ⚡).

## Results

On the left, top to bottom: the studies that ran with their analyses, the runs, the signals. On the right, the
waveforms, in the manner of LTspice:

- tick one or several analyses to overlay them (also from different studies); choose one run, or all of them;
- click a signal in the list to plot it in the pane chosen (the one with the navy frame; click a pane to choose
  it), or drag it onto a pane;
- drag a trace's name to another pane to move it there, or out of the panes to delete it (Esc cancels);
- right-click a pane: **Add Trace** by expression (`2*V(out)`, `V(out)-V(in)`, `V(a,b)` for `V(a)-V(b)`,
  `abs()`, `sqrt()`, `db()`...), add a pane above or below, delete it, reset the zoom, copy or save the picture;
- click a trace's name for cursor 1, again for cursor 2, again to take them off; right-click the name to move
  the cursors, edit the expression or delete the trace. Drag a cursor to move it;
- every pane shares the time axis, within the simulation: drag a box to zoom (every pane follows), the wheel
  zooms, the middle button pans, a double-click or Reset Zoom shows the whole simulation again;
- under the panes, every trace's value at the cursors and the difference, and its average, RMS, min, max and
  peak-to-peak between the cursors (or over the time shown); with both cursors, Δt and 1/Δt;
- every trace has its unit, from the dimensions of what it reads, as in LTspice: V(out)*I(R1) is in W,
  V(out)/I(R1) in Ω, V(out)*V(out) in V², db() in dB, a sum of different units in none. A pane with two units
  gets a second axis on the right, with its own scale; with more, one axis and no unit;
- several temperatures of an analysis are drawn in their own colours;
- right-click a pane, Style: Pale Yellow (the default), Engineering, Paper, Amber, Recorder, Sepia, Graphite, Slate, LTspice,
  Oscilloscope or Blueprint (kept for next time).

The project (`.spica`) keeps each study's last run and how its waveforms were shown: opened, it shows its results,
the panes as they were left. An analysis marked *reused* was not simulated again: the same netlist and analysis had
run before (at the time it gives), and its results were taken from disk. The time can be zoomed in down to ten time steps of the
simulation.

The Folder button on the toolbar opens the study's campaigns on disk.

## Queue

The campaigns of the studies run one after another, each with the whole machine. The list shows each one's
progress and when it should end; Move Up / Move Down change the order of those waiting, Hold the queue lets the
running one finish and starts nothing new. Below, the campaign that runs: progress, time left, throughput, this
PC's CPU, RAM and disk, every LTspice instance, the log.

Stop (toolbar, Shift+F5) stops the whole study. Stop on the Queue tab stops only the campaign that runs: at once,
when the simulations running now finish, or discarding its folder. What a stopped campaign finished can still be
looked at.

## Options

Tools > Options holds what belongs to this PC:

- **LTspice:** found by itself in the usual places (Detect looks again), or chosen with Browse.
- **Parallel instances:** automatic (one LTspice per core, fewer if RAM is short), or a fixed number of
  instances and threads.
- **Results:** where the studies put their campaigns (`<folder>\studies\<study>`). Empty: beside the project, in
  `<name>_SPICA`. Keep it short: LTspice cannot open files past 260 characters of path.

## How a campaign runs

- SPICA converts the schematic with `LTspice -netlist` and splits the runs into chunks, each launched as
  `LTspice.exe -b chunk.net -ini <private ini>`. The private ini copies the user's LTspice settings, reseeds
  `mc()` from the clock and turns off the start-up dialogs; the user's own `LTspice.ini` is never touched.
- Any `.step` in the schematic is replaced by SPICA's own `.step param run a b 1` per chunk. A stepped `.op`
  gets a dummy first step, so `.meas` give one row per run.
- **Nominal and worst case** set every tolerance SPICA can drive (`mc()`, `gauss()`, `flat()`, also inside
  sub-circuits and the libraries they come from) to its nominal value or to an edge (flat: its limits; gaussian:
  ± sigma). **Monte Carlo, paired**: SPICA draws the values itself, as a pure function of (seed, tolerance, run),
  so run 37 is the same unit at every temperature and the campaign is reproducible from its seed. Not paired:
  LTspice's own `mc()`.
- A temperature is set per chunk with `.temp T`.
- The scheduler: N instances = physical cores (or the manual value), fewer if free RAM is short; each first
  measures the run time, then chunks of about 2 minutes, never more than an equal share of what is left. The run
  time is remembered per machine and circuit, so the next campaign plans from the start.
- Each campaign folder holds `results.csv` (run, temperature, status, one column per `.meas`), `summary.txt`,
  `campaign.log`, `campaign.state` and the chunks with their `.raw` files.

## Build

```
dotnet build src/SPICA/SPICA.csproj -c Release
```

The version is in `src/Directory.Build.props`. The engine lives in `src/SPICA.Engine` and is compiled into the
exe; building it on its own checks that it depends on no window code. The design is in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

`tests/SPICA.Smoke` checks the app; each check prints `ok` or `WRONG`:

- `--graph`: the blocks, the wiring, the executors, the cache, the campaign builder and the project file (no LTspice);
- `--studies <asc> <png>`: the window on a schematic, with pictures;
- `--studyrun <asc> <png>`: a study run with LTspice end to end, the waveforms and the cache;
- `--savesel <asc> <png>`: a few signals saved (the `.meas` ones forced); plotting, expressions, cursors and measurements;
- `--opentime <asc>`: how long reading a schematic takes (LTspice, then the cache), and its tolerances;
- `--waves <dir> <signal>...`: every signal asked for is drawn from a campaign folder;
- `--hittest`: the window's edges resize it;
- `<asc> [runs] [instances threads]`: the campaign engine alone (`SPICA_TEMP=-40,25,90`, `SPICA_PAIRED=1`).

## License

Copyright © 2026 10k10. Free to use, copy, modify and share, at home or at work, for any
purpose; it may not be sold. Terms: Apache License 2.0 with the Commons Clause condition, see [LICENSE](LICENSE).

Icons: [alexh/vintage-icons](https://github.com/alexh/vintage-icons) (MIT).
LTspice is a trademark of Analog Devices, Inc.; SPICA is not affiliated with Analog Devices.
