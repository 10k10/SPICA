# SPICA architecture: analysis graphs

Status: **agreed direction** (2026-11-19). Steps 1 and 2 of section 10 are done.

- **Step 1:** the engine is its own project (`src/SPICA.Engine`) and holds the graph model (`Graph/`).
- **Step 2:** the app has a Studies tab with the editor:
  - the block library as a tree with search;
  - the canvas, where blocks are dragged, wired with the mouse (typed, no loops), given a new block that
    fits from a port's menu, removed, panned and zoomed;
  - the properties of the selected block, with lists of the nets, tests, criteria, errors and signals;
  - templates, and the studies the old tabs stand for.

Blocks do not run yet (step 3).

This document guides the move from today's fixed tabs (Setup, Qualification, Queue, Results) to one modular app
where every analysis is a graph of blocks.

## 1. Why

Today each kind of analysis has its own tab, its own way to run and its own way to judge. The
analysis types are:

- the tolerance / error budget campaign;
- the DO-160 qualification.

Every new need would add another tab with another mix of the same steps:

- surge;
- EMI susceptibility;
- input shorts;
- performance requirements;
- derating.

Yet every analysis we run on a circuit follows the same steps:

```
 STIMULUS     ->  VARIATION     ->  SIMULATION  ->  COLLECTION     ->  EVALUATION     ->  OUTPUT
 what is          how the           LTspice         what is kept       how it is          what is
 applied          circuit varies    (queue)         (.meas, .raw)      judged             delivered
```

The app is rebuilt around that: **a study is a graph of blocks, wired like Simulink.**

- **Blocks:** each one does one step.
- **Wires:** they carry typed data between blocks.

Each analysis type is then just a set of blocks; it no longer needs a tab of its own.

## 2. Principles

1. **The schematic is never edited.** Sources, faults, `.meas`, `.save` and `.tran` are injected into
   the generated netlist, as today.
2. **One project = one or more circuits + several studies (graphs).** A study may hold several candidate
   circuits put through the same stimuli and judged by the same criteria (section 5a). The `.spica` file
   stores everything as readable text. The GUI and `SPICA-cli` run exactly the same graph.
3. **Simulating is expensive, judging is cheap.** Simulation results are cached by the hash of what
   produced them (netlist variant + condition + engine options). If a limit or a criterion changes,
   only the blocks after the simulation run again, in seconds.
4. **Every evaluation produces the same kind of record**, so that one report and one requirements
   matrix serve every study. A record holds:
   - the requirement ID and the criterion;
   - the case and the condition;
   - the value, the limit, the margin and the verdict;
   - the evidence (the plot).
5. **Worst case is driven by an objective.** The worst case for hold-up is not the worst case for
   overvoltage, so a worst-case search always names the measurement it pushes, and in which
   direction.
6. **External data comes in through adapters.** Component ratings come from the company's database.
   SPICA defines the contract (part numbers in, ratings out as JSON/CSV), and a small script or exe,
   kept by the user, answers it. SPICA never knows the database's API.
7. **Library values are the user's.** Test-waveform libraries (DO-160, susceptibility, ...) and
   derating rules are text files the user reviews. The ones SPICA writes are templates, marked as
   unverified until the user says otherwise.

## 3. Wires: what flows between blocks

Ports are typed. The editor only lets a wire join ports of the same type. A graph cannot loop.

| Type | Carries | Example producer |
|---|---|---|
| **Stimulus** | what is applied to a circuit and where: a supply waveform into a net, a fault between two nets, a noise injection. The wire shows the net it goes into (e.g. `net IN`) | Supply Waveform, Fault, Susceptibility |
| **Circuit** | a set of *variants*: netlists made from a schematic, one per test case (e.g. one per DO-160 test). Each variant is tagged with its **candidate** (which schematic it came from) | Schematic, Candidates, Supply Waveform, Fault |
| **Conditions** | a set of named *conditions*. Each one holds temperature, tolerance settings (nominal, an edge, a corner, a WC point) and the number of MC runs | Nominal, Monte Carlo, Corners, Worst-Case Search, Combine |
| **Data** | results per (variant x condition): `.meas` table, `.raw` files, run status | Simulate |
| **Table** | derived values per (variant x condition x run): component stresses, spectra | Stress, Spectrum |
| **Ratings** | component ratings by reference designator | Parts Database |
| **Verdicts** | uniform verdict records (principle 4) | every evaluation block |

A block may have several inputs of the same type (e.g. Report takes any number of Verdicts).

## 4. Blocks

### Stimulus (-> Stimulus), drawn on the left, feeding the circuit

Stimuli come first, left to right, because they act on the circuit. The table below lists Supply
Waveform, Fault and Susceptibility with the circuit blocks for reference, but they output **Stimulus**
into a Schematic's input, and are not placed between circuit blocks.

### Circuit (Circuit -> Circuit)

| Block | Does | Today |
|---|---|---|
| Schematic | loads the `.asc`, lists nets, currents, `.meas`, tolerances; its name is the candidate's name. Takes any number of Stimulus inputs; each input port shows the net it drives ("stimulus at IN") | Setup tab |
| Candidates | joins several Schematic blocks into one Circuit wire, and maps **logical signals** to each candidate's own names (e.g. `IN` = `V(in)` in A, `V(vin)` in B; `OUT` = `V(out)` / `V(vo)`) | new |
| Supply Waveform | replaces the source on a net with a library waveform; one variant per ticked test | Qualification (DO-160 Sec. 16) |
| Fault | a switch or resistor between two nets (input to GND, to 28 V, open) from time t; one variant per fault | new |
| Susceptibility | injects a noise source into a net through a coupling model (sine, fixed or swept; pulses) | new |
| Measure | adds `.meas` statements and signals to save | Setup tab (.meas come from the schematic) |
| Analysis | sets the analysis (.tran length, step) when no stimulus block does | implicit today |

### Variation (-> Conditions)

| Block | Does |
|---|---|
| Nominal | one condition, every tolerance at its nominal value |
| Monte Carlo | N runs, paired or not, seed |
| Corners | named conditions: temperatures x tolerance edges chosen by the user (e.g. "85 C + C min + load max") |
| Worst-Case Search | takes a Circuit and an objective (a measurement, minimise or maximise); runs its own screening; outputs the worst point found as a condition named "worst case for X" |
| Combine | product (every A with every B) or union of condition sets |

The Worst-Case Search is how one study uses another's result. For example, it finds the combination
that drains the hold-up fastest, and the qualification then runs there as one more column of the grid
(section 6).

### Simulation (Circuit + Conditions -> Data)

**Simulate** is the only block that runs LTspice. It expands *variants x conditions* into queue
campaigns: local or remote, with chunking and the scheduler as today. It reuses cached results
(principle 3).

### Collection (Data -> Table)

| Block | Does |
|---|---|
| Stress | from the `.raw`: voltage, current and power of every component (or of chosen ones). It reports each one's peak and average, inside a window or over the whole run |
| Spectrum | FFT of a signal, for susceptibility and ripple |

### Evaluation (-> Verdicts)

| Block | Inputs | Does | Today |
|---|---|---|---|
| Limits | Data | signal above / below / between, in a window anchored to the test's events | Pass / Fail criteria |
| Error Budget | Data | the error budget (mean +/- k sigma, input-referred, TF) | Error Budget |
| Spec | Data | LSL/USL per measurement, yield, Cpk | Specs |
| Survival | Table + Ratings | absolute maximum ratings not exceeded | new |
| Derating | Table + Ratings + rules file | stress within the allowed % of rating (internal standard, editable rules by part type and parameter, temperature-dependent) | new |
| Susceptibility | Data | output deviation during injection within limit | new |

Every evaluation block takes an optional requirement ID per criterion.

### External data

| Block | Does |
|---|---|
| Parts Database | gets the BOM (reference designator -> part number), then calls the user's adapter or reads a CSV/JSON export; outputs Ratings |

### Output (Verdicts / Data -> files)

| Block | Does |
|---|---|
| Report | an HTML section per input; the document-style report of 0.8 |
| Requirements Matrix | requirement x condition, PASS/FAIL with margin, links to evidence |
| Plots | the waveform plots of 0.8 (supply on top, one plot per criterion); with candidates, one colour per candidate on the same axes |
| Compare | candidates side by side: verdicts and margins per criterion and condition, statistics of chosen measurements, and overlaid waveforms |
| Export | CSV of the verdicts or of the data |

## 5. Examples

```
Hold-up, DO-160 interruptions, at every corner and at the worst case found:

 [Schematic] -> [Supply Waveform: DO-160 16, cat A, net in, interruptions]
                     |
                     |          [Corners: -55/25/85 C x C min/nom]
                     |              |
                     |          [Worst-Case Search: minimise holdup_min] --+
                     |              |                                      |
                     +-----------> [Combine: union] <----------------------+
                     |                   |
                     +-------------> [Simulate] -> [Limits: V(bulk) >= 18 V ...] -> [Report]
                                                                             \--> [Requirements Matrix]

Derating, nominal and worst case:

 [Schematic] -> [Simulate] <- [Nominal + Corners]
                    |
                 [Stress: all parts] -> [Derating: rules.txt] <- [Parts Database: adapter]
                                                   |
                                                [Report]
```

## 5a. Comparing candidate circuits

The same stimulus, variation and criteria are applied to two or more candidate circuits, and their
results are compared.

```
 [Schematic: A (TVS + bulk 470u)] --+
 [Schematic: B (active limiter)]  --+--> [Candidates: IN, OUT mapped] --> [Supply Waveform: DO-160 surges, net IN]
 [Schematic: C (B + 680u)]        --+                                            |
                                                         [Corners] ------> [Simulate] --> [Limits: OUT <= 32 V] --> [Compare]
                                                                                     \--------------------------> [Plots]
```

- **Logical signals.** Stimulus and criteria refer to logical signals (`IN`, `OUT`, `PGOOD`), so they
  are written once. The Candidates block says what each one is in each schematic. A plain `V(out)` also
  works when every candidate uses the same net name.
- **The candidate is one more axis.** Every result is per candidate x variant x condition. Simulate
  runs them all as one batch of queue campaigns, and the cache is per candidate.
- **What Compare shows:**
  - a verdict grid with a column group per candidate;
  - each criterion's worst value and margin per candidate (a bar chart);
  - the distributions of chosen measurements overlaid (MC);
  - the waveforms of the same test and condition overlaid, one colour per candidate, on linked axes.
- **Visual comparison without criteria.** A study with no evaluation block, only Plots or Compare, is
  enough to look at the outputs of the candidates under the same stimulus.

## 6. Results: the grid

The Results view shows, for each evaluation block, a grid of test/variant x condition. Each cell holds
the verdict and the margin. Clicking a cell opens the evidence (plots, runs).

```
                       -55 C   25 C   85 C   85 C+Cmin   worst case (holdup)
 Interruption 10 ms     PASS   PASS   PASS     FAIL -1.2 V     FAIL -2.0 V
 Interruption 50 ms     PASS   PASS   FAIL     FAIL            FAIL
 Surge 40 V             PASS   PASS   PASS     PASS            PASS
```

With candidates, the grid has one column group per candidate (or a selector to show one at a time).

## 7. Editor

The editor is a canvas in the app's own style:

- **Canvas:** blocks are boxes with ports on their sides and wires are drawn between them; the canvas
  can be dragged, zoomed and panned.
- **Palette:** the blocks by family, on the left.
- **Properties:** the selected block's properties, on the right. Today's panels become those property
  panels; for example, a standard's sub-tab becomes the Supply Waveform's properties.
- **Each block shows its state:** not configured, ready, cached, running, done, error.
- **Run:** runs the whole graph or everything up to one block.
- **Look:** the app keeps its Windows 98 look and its own icons. Block headers and the block library use
  the same icon set as the menus.
- **Where results go:** the editor is the Studies tab. Results and waveform plots stay in their own tabs,
  as today (Studies | Queue | Results); they do not share the screen with the editor.
- **Layout (chosen 2026-11-19):**
  - **Left:** the block library as a tree by family, with search. It lists names and icons only; the
    description box under it says what a block takes and gives.
  - **Top:** a toolbar of large Win98 buttons that only control the flow, and never create blocks:
    - Open and Save;
    - Run study, Run up to the block, Stop and Run remote;
    - Validate graph and Clear cache;
    - Queue, Results and Report;
    - Options and Help.
  - **Right:** the properties of the selected block.
  - **Not decided yet:** a "connect to..." list on an output port, offering only the compatible
    blocks.

Queue and Results stay as views common to every study.

## 8. File format

The `.spica` file keeps its INI-like text. A study is:

```
[Study Holdup]
[Node Holdup.1]
Type=Schematic
At=40,80
[Node Holdup.2]
Type=SupplyWaveform
At=220,80
Library=do160g_s16_28vdc
Category=A
Net=in
Tests=Power interruption, single, 10 ms|...
[Wire Holdup]
1.circuit=2.circuit
2.circuit=5.circuit
```

An old `.spica` (no study sections) is read into a default study:

- Schematic -> Monte Carlo -> Simulate -> Error Budget / Spec -> Report;
- its qualification settings, if any, become a second study.

Nothing is lost.

## 9. Code layout

| Project | Contents |
|---|---|
| `SPICA.Engine` | netlist, LTspice, campaign, queue, listener, `.raw`, results, statistics, graph model and execution, block implementations. No UI |
| `SPICA` (app) | the editor, the property panels, queue and results views. Single exe as today |
| `SPICA.Cli` | `run <project> [--study name]`, `listen`, `status`; runs graphs |

A block is a class with:

- typed input/output ports;
- properties, with load and save;
- an `Execute` step;
- in the app, a property panel.

The graph runner:

1. sorts the blocks;
2. turns Simulate blocks into queue campaigns;
3. waits for them;
4. runs the rest.

## 10. Migration, in releasable steps

1. `SPICA.Engine` as its own project; graph model, file format, legacy conversion, and runner. Old
   tabs still work on top of it.
2. Graph editor (canvas, palette, properties) and templates.
3. Qualification as blocks (Supply Waveform, Limits, Plots), Conditions / Corners, the results grid.
4. Tolerance analysis as blocks (Monte Carlo, Worst-Case Search, Error Budget, Spec); old tabs removed.
5. New blocks: Fault, Stress, Survival, Derating (rules + adapter), Susceptibility.
6. Remote and CLI for graphs.

## 11. Open questions

- Derating rules: the file layout for the internal standard (by part family, parameter, temperature).
- Parts database export: confirm it can give ratings by part number (CSV/JSON).
- Susceptibility: the first coupling models and levels (templates for the user to review).
- Candidates: whether a candidate may also be the same schematic with different parameter values
  (e.g. `.param Cbulk`), to compare options without copying the `.asc`.
- How BOM part numbers are read: from the schematic's SpiceModel/Value attributes, or from a separate
  BOM file.
