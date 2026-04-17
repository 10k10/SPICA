# CLAUDE.md — SPICA

Project context and conventions for Claude Code sessions.

## What this project is

**SPICA — Spice Parallel Instance Circuit Analyzer** is a Python desktop application for LTSpice Monte Carlo simulation and statistical analysis.  
It runs parallel LTSpice simulations, merges the results, fits transfer functions, computes Cp/Cpk/sigma/yield, and generates PDF reports.

## Stack

| Layer | Library |
|---|---|
| UI | CustomTkinter (tkinter wrapper) |
| LTSpice process control | subprocess (direct Popen — NOT spicelib SimRunner) |
| Log parsing | spicelib `LTSpiceLogReader` (`from spicelib.log.ltsteps import LTSpiceLogReader`) |
| Statistics | numpy + scipy |
| Plots | matplotlib (Agg backend for off-screen rendering; TkAgg inside the GUI) |
| Report templating | Jinja2 (HTML) |
| PDF generation | reportlab (primary) → WeasyPrint (fallback, requires GTK) |
| CSV export | stdlib csv |

## File map

```
SPICA/                   ← project root (C:\Users\rob\Desktop\SPICA)
├── main.py              — entry point, builds App window
├── config.py            — ProjectConfig, PairConfig, SpecLimits dataclasses
├── chunk_builder.py     — splits total_runs into N chunk .asc files
├── runner.py            — launches LTSpice subprocesses with seed stagger
├── log_merger.py        — reads .log files via spicelib, merges arrays
├── analyzer.py          — TF computation, normality tests, Cp/Cpk, extrapolation
├── plotter.py           — 6 matplotlib figure functions (return Figure, no show)
├── reporter.py          — generate_report() → PDF/HTML; export_csv()
├── templates/
│   └── report.html      — Jinja2 HTML template for the report
└── ui/
    ├── app.py           — CTkTabview container wiring all panels
    ├── panel_config.py  — Config panel (auto-detects .meas on .asc load)
    ├── panel_pairs.py   — Pairs panel (meas_in/out, TF type, spec limits)
    ├── panel_jobs.py    — Jobs panel (run/cancel simulation, progress)
    └── panel_results.py — Results panel (summary table, plots, PDF, CSV)
```

## Critical design decisions

### Simulation chunking and seeding
- `chunk_builder.py` always injects `.step param run 0 N-1 1` into each chunk `.asc`, overriding whatever is in the original file. No user action required.
- `runner.py` uses **subprocess.Popen directly** (not spicelib SimRunner) to control exact process launch timing.
- A `SEED_STAGGER_S = 2.0` second gap is enforced between consecutive LTSpice process launches so each gets a different clock-based MC seed.
- Chunks run **in parallel** — after the stagger ramp-up (`(n_chunks-1) × 2s`) all processes are alive simultaneously.

### spicelib import — correct path
```python
# CORRECT
from spicelib.log.ltsteps import LTSpiceLogReader

# WRONG — fails silently and falls back to regex parser
from spicelib import LTSpiceLogReader
```

### PDF generation chain
1. WeasyPrint (best quality, fails on Windows without GTK)
2. reportlab Platypus (pure Python, always available — primary on Windows)
3. HTML fallback

### .asc file format
LTSpice stores directives as `TEXT x y Left 2 !<directive>` lines.  
Multi-line blocks use `\\n` literal inside the TEXT string.  
`panel_config.py:_extract_directives_from_asc()` handles both formats.

### Normality testing
`analyzer.py:test_normality()` runs Shapiro-Wilk (primary), KS, and Anderson-Darling.  
A measurement is considered normal if ≥ 2 of 3 tests pass.  
Shapiro-Wilk is subsampled to 5000 points when n > 5000.

### Plot style conventions (`plotter.py`)
- All figures saved at **200 dpi**
- Scatter: `s=12, alpha=0.5, edgecolors='#1a3a5c', linewidths=0.3`
- Histogram: `edgecolor='black', alpha=0.70`
- Annotation boxes: `facecolor='wheat', alpha=0.85`
- Grid on every axis: `linestyle='--', alpha=0.3`
- Shapiro-Wilk result (W, p, verdict) annotated directly on histogram

## Key constraints / things that burned us

- **Do not** use `from spicelib import LTSpiceLogReader` — wrong module path.
- **Do not** use `spicelib.SimRunner` for launching — it batches process spawns internally, bypassing the seed stagger.
- **Do not** rely on `.step param` in the original `.asc` — chunk_builder always writes its own.
- **Do not** add `add_step_if_missing` back to `ProjectConfig` — it was removed; the step is always injected.
- WeasyPrint import must be wrapped in `(ImportError, OSError, Exception)` — it raises OSError on Windows without GTK.
- xhtml2pdf fails with `'NotImplementedType' object is not iterable` on our HTML template — use reportlab instead.

## Running

```bash
cd "C:\Users\rob\Desktop\SPICA"
python main.py
```

## Packaging to .exe

```bash
pip install pyinstaller
pyinstaller spica_mc.spec   # see README for full spec file
```

Fix needed before packaging: `reporter.py:_get_templates_dir()` must use `sys.executable` path when `sys.frozen` is True.
