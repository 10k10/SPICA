# SPICA — Spice Parallel Instance Circuit Analyzer

Desktop application for running and analyzing LTSpice Monte Carlo simulations.  
Parallel simulation execution, statistical analysis (Cp/Cpk, sigma level, yield), and PDF report generation.

## Features

- **Parallel simulation** — splits runs into N chunks, launches them with a seed stagger to guarantee decorrelated MC results
- **Auto .meas detection** — reads `.asc` schematics and extracts measurement names automatically
- **Transfer function analysis** — linear fit, origin fit, polynomial, dB, absolute/relative error
- **Statistics** — mean, std, Cp, Cpk, sigma level, yield, Shapiro-Wilk normality test, distribution fitting
- **PDF report** — per-pair charts (scatter, histogram, Q-Q, error vs input, sigma gauge, extrapolation table)
- **CSV export** — raw per-run measurements and TF values

## Requirements

```
pip install customtkinter spicelib numpy scipy matplotlib jinja2 reportlab xhtml2pdf scikit-learn
```

LTSpice 24 must be installed separately: [ADI LTSpice](https://www.analog.com/en/resources/design-tools-and-calculators/ltspice-simulator.html)

## Quick start

```bash
cd "C:\Users\rob\Desktop\SPICA"
python main.py
```

1. **Config tab** — select your `.asc` file (`.meas` names are detected automatically), set total runs and parallel cores
2. **Pairs tab** — define input/output measurement pairs, TF type, and spec limits (LSL/USL)
3. **Simulation tab** — click ▶ Iniciar Simulação
4. **Results tab** — view summary table and per-pair plots; export PDF or CSV

## Project structure

```
SPICA/
├── main.py              Entry point
├── config.py            ProjectConfig, PairConfig, SpecLimits
├── chunk_builder.py     Generates chunk .asc files with correct .step param ranges
├── runner.py            Launches LTSpice subprocesses with 2s seed stagger
├── log_merger.py        Reads and merges .log files via spicelib
├── analyzer.py          Statistical analysis (TF, normality, Cp/Cpk, extrapolation)
├── plotter.py           Matplotlib figure generators (Agg, 200 dpi)
├── reporter.py          PDF (reportlab) and CSV export
├── templates/
│   └── report.html      Jinja2 HTML report template
└── ui/
    ├── app.py           Main window
    ├── panel_config.py  Configuration panel
    ├── panel_pairs.py   Measurement pairs panel
    ├── panel_jobs.py    Simulation runner panel
    └── panel_results.py Results and export panel
```

## How chunking works

To use multiple CPU cores, the total run count is split into N chunks.  
Each chunk is a copy of the `.asc` with a different `.step param run` range:

```
total_runs = 1000, n_cores = 4
  chunk_000.asc  →  .step param run   0  249 1
  chunk_001.asc  →  .step param run 250  499 1
  chunk_002.asc  →  .step param run 500  749 1
  chunk_003.asc  →  .step param run 750  999 1
```

Chunks are launched 2 seconds apart so each LTSpice process gets a distinct clock-based MC seed. After the ramp-up all chunks run concurrently.

The `.step param run` line is always written by SPICA into the chunk files — you do not need it in your original schematic.

## Packaging to .exe

```bash
pip install pyinstaller

# spica_mc.spec
pyinstaller --onedir --windowed \
  --collect-data customtkinter \
  --collect-data spicelib \
  --add-data "templates;templates" \
  --collect-all scipy \
  --collect-all reportlab \
  main.py
```

> Note: add `_get_templates_dir()` frozen path fix to `reporter.py` before packaging (see CLAUDE.md).

## Report output

The PDF report (generated via reportlab) contains for each pair:
- Stats table: mean, std, min, max, Cp, Cpk, sigma level, yield, spec limits, normality
- Scatter plot (input × output with fit and ±3σ band)
- Histogram (with normal fit, spec limit markers, Shapiro-Wilk annotation)
- Q-Q plot (with 95% bootstrap band and Pearson r)
- Error vs input (residual plot with systematic trend detection)
- Sigma gauge (capability bar)
- Gaussian extrapolation table (1σ–6σ yield and DPM)

## Transfer function types

| Type | Description |
|---|---|
| `linear_fit` | Linear regression y = ax + b; values = residuals |
| `origin_fit` | Fit through origin y = ax; values = residuals |
| `poly2_fit` | 2nd-degree polynomial fit; values = residuals |
| `linear` | Simple ratio y/x per run |
| `db` | 20·log₁₀(|y/x|) per run |
| `error_abs` | y − x per run |
| `error_rel_pct` | (y − x)/x × 100 per run |
| `custom` | User expression using `inp` and `out` numpy arrays |
