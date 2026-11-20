# SPICA architecture: studies as graphs

Status (2026-11-20): **the clean base.** The app was cut down to the graph and what one study needs end to end:
open a schematic, set up its analyses, run them, look at the waveforms. Everything else was removed (it is in
the history up to 0.9.3-dev) and comes back one piece at a time, on demand, built on this base.

## 1. What a study is

A study is a graph of blocks joined by typed wires, drawn left to right: the circuit and the analyses on the
left, the simulation that runs them on the right. The graph is where every analysis starts; the Queue and the
Results tabs are views common to every study.

| Wire type | Carries | Producer |
|---|---|---|
| **Circuit** | a schematic and its netlist | Schematic |
| **Conditions** | analyses: how the tolerances are set (nominal, N Monte Carlo units, worst-case edges) and at which temperatures | Nominal, Monte Carlo, Worst Case |

A wire only joins ports of the same type, and a graph cannot loop. An input takes one wire unless it says it
takes several (Simulate's analyses).

| Block | Ports | Properties |
|---|---|---|
| Schematic | → Circuit | File (empty: the project's schematic) |
| Nominal | → Conditions | Temperature |
| Monte Carlo | → Conditions | Runs, Seed, Paired, Temperature |
| Worst Case | → Conditions | Sigma, Temperature |
| Simulate | Circuit + Conditions (several) → (results) | Save |

Simulate turns each (circuit, analysis) into one campaign in the queue. Its results (the campaign folders) go to
the Results tab.

## 2. Principles

1. **The schematic is never edited.** SPICA's `.step`, `.temp`, `.save` and tolerance tables are written into
   its own copy of the netlist.
2. **One project = one schematic + its studies.** The `.spica` file is readable INI-like text, saved beside the
   schematic; the results go beside the project (or where the options say).
3. **Simulating is expensive, everything after it is cheap.** A campaign is cached by the hash of its netlist,
   its analysis and its saved signals (`study.key` in its folder). Running a study again takes what did not
   change from disk at once.
4. **What belongs to the PC stays with the PC.** Where LTspice is, how many instances run and where the results
   go are in `SPICA.ini`, not in the project.
5. **A block says what it misses.** The editor draws it red and its settings list what; Run lists it all and
   runs nothing. Nothing is greyed out without saying why.
6. **No dead code.** A feature that is not offered is not kept around half-wired: it is taken out, and brought
   back from the history when it is wanted.

## 3. File format

```
[Project]
Schematic=holdup.asc
[Study holdup]
Wire=1.circuit -> 3.circuit
Wire=2.conditions -> 3.conditions
[Block 1]
Kind=Schematic
At=40,40
File=holdup.asc
[Block 2]
Kind=Nominal
At=40,170
Temperature=25; 85
[Block 3]
Kind=Simulate
At=320,100
Save=
```

`Kind` and `At` are the block's own keys; every other key is a property. A property may hold several lines
(`\n`). A project of an older version opens: its schematic and its studies are read, its other sections are
skipped, and a block of a kind this version does not have stays in the study, marked unknown, until removed.

## 4. Code layout

| Project | Contents |
|---|---|
| `src/SPICA.Engine` | No window code. The netlist (`Netlist`, `Subckts`, `LibraryResolver`), SPICA's tolerance generator (`Sampling`, `WorstCase`), LTspice (`Ltspice`, `RunScheduler`, `HardwareInfo`), the campaign (`CampaignEngine`, `CampaignQueue`, `CampaignState`), the results (`ResultSet`, `RawFile`, `LogParser`), the project (`Project`, `AppSettings`) and the graph (`Graph/`: `Study`, `BlockKind`, `Runner`, `Values`, `Executors`). |
| `src/SPICA` | The window: `MainForm` (frame, project, commands), `MainForm.Studies` (the Workbench), `MainForm.StudyRun` (running a study, the Results tab), `MainForm.Queue`, `StudyCanvas`, `WaveformView`, `OptionsForm`, the Win98 controls. |
| `tests/SPICA.Smoke` | The checks (`--graph`, `--studies`, `--studyrun`, `--hittest`) and the engine alone. |

A block kind is declared once, in `BlockKind`: its family, ports and properties with their defaults. Its executor
(`Executors`) reads its inputs and writes its outputs; Simulate may wait (for its campaigns) and tells the runner
when it is done. The runner runs the blocks in wire order; a block that fails stops what it feeds.

## 5. What comes back next, and how

Each of these was in 0.9.x and is in the history. Each comes back as blocks on this base, when it is asked for,
tested end to end before the next:

- stimuli: supply waveforms from a standard's library into a net, faults (shorts between nets), susceptibility;
- measurements as blocks (`.meas` built in SPICA), more signals to save per study;
- evaluations: limits on a signal in a window, spec limits, error budget, with one kind of verdict record for all;
- the report and the requirements matrix;
- candidates: several schematics through the same study, compared;
- running elsewhere: the listener, Run Remote, the command line.
