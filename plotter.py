"""
Todos os gráficos matplotlib.
Cada função retorna matplotlib.figure.Figure — sem plt.show().
Salvar com fig.savefig(path, dpi=200, bbox_inches='tight').
"""
from __future__ import annotations

import warnings
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from scipy import stats
from typing import Optional

from config import PairConfig, SpecLimits

# ---------------------------------------------------------------------------
# Paleta e estilo global
# ---------------------------------------------------------------------------

C_BLUE   = "steelblue"
C_NAVY   = "#1a3a5c"
C_RED    = "#c0392b"
C_GREEN  = "#27ae60"
C_ORANGE = "#e67e22"
C_PURPLE = "#6a3fa0"
C_GRAY   = "#555555"
C_LIGHT  = "#f5f5f5"
C_WHEAT  = "wheat"

_ANNO_BOX  = dict(boxstyle="round,pad=0.35", facecolor=C_WHEAT, alpha=0.85)
_ANNO_WARN = dict(boxstyle="round,pad=0.35", facecolor="#fff3cd", alpha=0.9)

def _apply_style(ax):
    """Aplica estilo consistente: grid sutil, spines limpos."""
    ax.grid(True, linestyle="--", alpha=0.30, linewidth=0.6)
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)

def _label_with_unit(text: str, unit: str) -> str:
    return f"{text} [{unit}]" if unit else text


# ---------------------------------------------------------------------------
# 1. Scatter Input × Output com fit
# ---------------------------------------------------------------------------

def plot_scatter_with_fit(
    arr_in:  np.ndarray,
    arr_out: np.ndarray,
    tf_result:    dict,
    pair:         PairConfig,
    stats_result: dict,
) -> plt.Figure:
    mask = np.isfinite(arr_in) & np.isfinite(arr_out)
    xi, xo = arr_in[mask], arr_out[mask]

    fig, ax = plt.subplots(figsize=(8, 5.5))
    fig.patch.set_facecolor("white")

    # Scatter com ponto visível + borda fina (estilo TFF)
    ax.scatter(xi, xo, s=12, alpha=0.50,
               color=C_BLUE, edgecolors=C_NAVY, linewidths=0.3,
               rasterized=True, label="MC data")

    if tf_result.get("fit_fn") is not None:
        x_line = np.linspace(xi.min(), xi.max(), 300)
        y_line = tf_result["fit_fn"](x_line)
        ax.plot(x_line, y_line, color=C_RED, lw=2.0,
                label="Fit", zorder=5)

        residuals = xo - tf_result["fit_fn"](xi)
        sigma = residuals.std(ddof=1)
        ax.fill_between(x_line, y_line - 3*sigma, y_line + 3*sigma,
                        color=C_BLUE, alpha=0.08, label="±3σ banda")

    # Anotação dos coeficientes
    fc = tf_result.get("fit_coeffs", {})
    if fc.get("degenerate"):
        ax.annotate(
            f"⚠ {fc.get('degenerate_note', 'Entrada constante — fit indisponível.')}",
            xy=(0.03, 0.97), xycoords="axes fraction",
            fontsize=8, va="top", color=C_ORANGE,
            bbox=_ANNO_WARN, wrap=True,
        )
    else:
        lines = []
        if "a"  in fc: lines.append(f"a  = {fc['a']:.5g}")
        if "b"  in fc: lines.append(f"b  = {fc['b']:.5g}")
        if "c"  in fc: lines.append(f"c  = {fc['c']:.5g}")
        if "R2" in fc and not np.isnan(fc["R2"]):
            lines.append(f"R² = {fc['R2']:.6f}")
        if "RMSE" in fc:
            rmse_s = f"{fc['RMSE']:.4g}" + (f" {pair.unit}" if pair.unit else "")
            lines.append(f"RMSE = {rmse_s}")
        if lines:
            ax.annotate("\n".join(lines),
                        xy=(0.03, 0.97), xycoords="axes fraction",
                        fontsize=9, va="top", bbox=_ANNO_BOX)

    _apply_style(ax)
    ax.set_xlabel(_label_with_unit("Entrada", pair.unit), fontsize=9)
    ax.set_ylabel(_label_with_unit("Saída",   pair.unit), fontsize=9)
    ax.set_title(f"{pair.label} — Scatter Input × Output",
                 fontsize=11, fontweight="bold")
    ax.legend(fontsize=8)
    fig.tight_layout()
    return fig


# ---------------------------------------------------------------------------
# 2. Erro × Entrada
# ---------------------------------------------------------------------------

def plot_error_vs_input(
    arr_in:       np.ndarray,
    error_arr:    np.ndarray,
    pair:         PairConfig,
    stats_result: dict,
) -> plt.Figure:
    mask = np.isfinite(arr_in) & np.isfinite(error_arr)
    xi, err = arr_in[mask], error_arr[mask]

    fig, ax = plt.subplots(figsize=(8, 5.0))
    fig.patch.set_facecolor("white")

    ax.scatter(xi, err, s=10, alpha=0.45,
               color="coral", edgecolors="darkred", linewidths=0.3,
               rasterized=True)
    ax.axhline(0, color=C_GRAY, lw=1.0, ls="--")

    mean_e = stats_result["mean"]
    std_e  = stats_result["std"]

    ax.axhline(mean_e, color=C_RED, lw=1.5, ls="--",
               label=f"µ = {mean_e:.3e}")
    for n_sig, color, ls, lbl in [
        (1, C_BLUE,   ":",  "±1σ"),
        (3, C_ORANGE, "--", "±3σ"),
    ]:
        ax.axhline(mean_e + n_sig*std_e, color=color, lw=1.2, ls=ls, label=lbl)
        ax.axhline(mean_e - n_sig*std_e, color=color, lw=1.2, ls=ls)

    spec = pair.spec
    if spec.usl is not None:
        ax.axhline(spec.usl, color=C_RED, lw=1.8, ls="-.",
                   label=f"USL = {spec.usl}")
    if spec.lsl is not None:
        ax.axhline(spec.lsl, color=C_RED, lw=1.8, ls="-.",
                   label=f"LSL = {spec.lsl}")

    # Detectar tendência sistemática nos resíduos
    if len(xi) > 10:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            slope_r, _, r_val, _, _ = stats.linregress(xi, err)
        r2_resid = r_val ** 2
        if r2_resid > 0.1:
            ax.annotate(
                f"Padrão sistemático detectado (R² = {r2_resid:.3f})",
                xy=(0.03, 0.04), xycoords="axes fraction",
                fontsize=8, color=C_ORANGE, bbox=_ANNO_WARN,
            )

    _apply_style(ax)
    ax.set_xlabel(_label_with_unit("Entrada", pair.unit), fontsize=9)
    ax.set_ylabel(_label_with_unit("Erro / TF", pair.unit), fontsize=9)
    ax.set_title(f"{pair.label} — Erro × Entrada",
                 fontsize=11, fontweight="bold")
    ax.legend(fontsize=8, ncol=2)
    fig.tight_layout()
    return fig


# ---------------------------------------------------------------------------
# 3. Histograma
# ---------------------------------------------------------------------------

def plot_histogram(
    error_arr:       np.ndarray,
    pair:            PairConfig,
    stats_result:    dict,
    normality_result: dict,
    alt_dist:        Optional[dict] = None,
) -> plt.Figure:
    data  = error_arr[np.isfinite(error_arr)]
    n     = len(data)
    mean_e = stats_result["mean"]
    std_e  = stats_result["std"]

    fig, ax = plt.subplots(figsize=(8, 5.0))
    fig.patch.set_facecolor("white")

    n_bins = min(50, max(20, n // 30))
    counts, bins, _ = ax.hist(
        data, bins=n_bins, density=True,
        color=C_BLUE, alpha=0.70,
        edgecolor="black", linewidth=0.5,
        label="Dados",
    )

    # Colorir barras fora dos spec limits
    spec = pair.spec
    lsl = spec.lsl if spec.lsl is not None else -np.inf
    usl = spec.usl if spec.usl is not None else  np.inf
    if spec.lsl is not None or spec.usl is not None:
        for i, (lo, hi) in enumerate(zip(bins[:-1], bins[1:])):
            if lo < lsl or hi > usl:
                ax.bar(lo, counts[i], width=hi - lo,
                       color=C_RED, alpha=0.45, align="edge")

    # Curva normal
    x_fit = np.linspace(data.min() - 2*std_e, data.max() + 2*std_e, 400)
    ax.plot(x_fit, stats.norm.pdf(x_fit, mean_e, std_e),
            color=C_RED, lw=2.0,
            label=f"Normal (µ={mean_e:.4g}, σ={std_e:.4g})")

    # Fit alternativo
    if alt_dist and alt_dist.get("fit_ok") and alt_dist.get("dist_obj") is not None:
        try:
            y_alt = alt_dist["dist_obj"].pdf(x_fit, *alt_dist["params"])
            ax.plot(x_fit, y_alt, color=C_PURPLE, lw=1.6, ls="--",
                    label=f"Alt: {alt_dist['dist_name']}")
        except Exception:
            pass

    # Marcadores σ
    for n_sig, color, ls, lbl in [
        (1, C_BLUE,   ":",  "±1σ"),
        (3, C_ORANGE, "--", "±3σ"),
        (6, C_PURPLE, ":",  "±6σ"),
    ]:
        for sign in (+1, -1):
            xv = mean_e + sign * n_sig * std_e
            if data.min() - 3*std_e < xv < data.max() + 3*std_e:
                ax.axvline(xv, color=color, lw=1.3, ls=ls,
                           label=lbl if sign == 1 else None)
    ax.axvline(mean_e, color=C_NAVY, lw=1.8, ls="--", label="µ")

    # Spec limits
    if spec.usl is not None:
        ax.axvline(spec.usl, color=C_RED, lw=1.8, ls="-.",
                   label=f"USL={spec.usl}")
    if spec.lsl is not None:
        ax.axvline(spec.lsl, color=C_RED, lw=1.8, ls="-.",
                   label=f"LSL={spec.lsl}")

    # Shapiro-Wilk diretamente no gráfico (como TFF)
    sw_text = ""
    if n >= 8:
        test_data = data
        if n > 5000:
            rng_sw = np.random.default_rng(42)
            test_data = rng_sw.choice(data, size=5000, replace=False)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            w_stat, p_val = stats.shapiro(test_data)
        verdict = "NORMAL" if p_val > 0.05 else "NÃO NORMAL"
        sw_text = f"Shapiro-Wilk\nW = {w_stat:.4f}\np = {p_val:.3e}\n→ {verdict}"
    elif normality_result:
        sw_text = normality_result.get("conclusion", "")
    if sw_text:
        ax.annotate(sw_text, xy=(0.97, 0.97), xycoords="axes fraction",
                    fontsize=8, va="top", ha="right", bbox=_ANNO_BOX)

    yield_str = ""
    if stats_result.get("yield_observed_pct") is not None:
        yield_str = f"  |  Yield = {stats_result['yield_observed_pct']:.2f}%"
    ax.set_title(f"{pair.label} — Distribuição  (n={n}{yield_str})",
                 fontsize=11, fontweight="bold")
    ax.set_xlabel(_label_with_unit("Valor", pair.unit), fontsize=9)
    ax.set_ylabel("Densidade", fontsize=9)
    ax.legend(fontsize=7, ncol=2)
    _apply_style(ax)
    fig.tight_layout()
    return fig


# ---------------------------------------------------------------------------
# 4. Q-Q Plot
# ---------------------------------------------------------------------------

def plot_qq(error_arr: np.ndarray, normality_result: dict) -> plt.Figure:
    data = np.sort(error_arr[np.isfinite(error_arr)])
    n    = len(data)

    fig, ax = plt.subplots(figsize=(5.5, 5.5))
    fig.patch.set_facecolor("white")

    theoretical = stats.norm.ppf(np.linspace(0.5/n, 1 - 0.5/n, n))
    mean_d = data.mean()
    std_d  = data.std(ddof=1)
    sample_q = (data - mean_d) / std_d if std_d > 0 else data - mean_d

    # Banda de confiança 95% (bootstrap)
    np.random.seed(0)
    bands = np.array([np.sort(np.random.randn(n)) for _ in range(200)])
    low95  = np.percentile(bands, 2.5,  axis=0)
    high95 = np.percentile(bands, 97.5, axis=0)

    in_band = (sample_q >= low95) & (sample_q <= high95)
    ax.scatter(theoretical[ in_band],  sample_q[ in_band],
               s=8, alpha=0.55, color=C_BLUE, edgecolors=C_NAVY,
               linewidths=0.3, rasterized=True)
    if (~in_band).any():
        ax.scatter(theoretical[~in_band], sample_q[~in_band],
                   s=10, alpha=0.75, color=C_RED, label="Fora da banda 95%",
                   edgecolors="darkred", linewidths=0.3)

    ax.fill_between(theoretical, low95, high95,
                    alpha=0.12, color=C_GREEN, label="Banda 95%")
    ax.plot([theoretical.min(), theoretical.max()],
            [theoretical.min(), theoretical.max()],
            color=C_GRAY, lw=1.8, ls="--", label="Linha de referência")

    # Pearson r (como TFF) + anotações de assimetria
    r_qq = np.corrcoef(theoretical, sample_q)[0, 1]
    anno_lines = [f"Correlação Q-Q\nr = {r_qq:.6f}"]

    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        sk   = float(stats.skew(data))
        kurt = float(stats.kurtosis(data))
    if sk > 0.8:
        anno_lines.append("Cauda direita pesada")
    elif sk < -0.8:
        anno_lines.append("Cauda esquerda pesada")
    if abs(kurt) > 2:
        anno_lines.append("Leptocúrtica" if kurt > 0 else "Platicúrtica")

    ax.annotate("\n".join(anno_lines),
                xy=(0.04, 0.96), xycoords="axes fraction",
                fontsize=9, va="top", bbox=_ANNO_BOX)

    _apply_style(ax)
    ax.set_xlabel("Quantis teóricos (Normal)", fontsize=9)
    ax.set_ylabel("Quantis da amostra (padronizados)", fontsize=9)
    ax.set_title("Q-Q Plot", fontsize=11, fontweight="bold")
    ax.legend(fontsize=8)
    fig.tight_layout()
    return fig


# ---------------------------------------------------------------------------
# 5. Sigma Gauge
# ---------------------------------------------------------------------------

def plot_sigma_gauge(stats_result: dict) -> plt.Figure:
    sigma = stats_result.get("sigma_level")

    fig, ax = plt.subplots(figsize=(4.0, 2.8))
    fig.patch.set_facecolor("white")
    ax.set_xlim(0, 6)
    ax.set_ylim(0, 1)
    ax.set_yticks([])

    zones = [
        (0, 3, "#c0392b", "< 3σ"),
        (3, 4, "#e67e22", "3–4σ"),
        (4, 6, "#27ae60", "4–6σ"),
    ]
    for x0, x1, color, label in zones:
        ax.barh(0.5, x1 - x0, left=x0, height=0.55,
                color=color, alpha=0.75, align="center")

    if sigma is not None:
        sc = max(0.0, min(6.0, sigma))
        ax.plot([sc, sc], [0.08, 0.92], color="black", lw=3.5, zorder=5)
        ax.annotate(
            f"{sigma:.2f}σ",
            xy=(sc, 0.94), ha="center", fontsize=11,
            fontweight="bold", color="black",
        )

    ax.set_xticks([0, 1, 2, 3, 4, 5, 6])
    ax.set_xlabel("Sigma Level", fontsize=9)
    ax.set_title("Capability (Sigma)", fontsize=11, fontweight="bold")
    legend_patches = [mpatches.Patch(color=z[2], alpha=0.75, label=z[3]) for z in zones]
    ax.legend(handles=legend_patches, fontsize=7, loc="lower right")
    ax.grid(True, axis="x", linestyle="--", alpha=0.30, linewidth=0.6)
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)
    fig.tight_layout()
    return fig


# ---------------------------------------------------------------------------
# 6. Tabela de Extrapolação
# ---------------------------------------------------------------------------

def plot_extrapolation_table(stats_result: dict) -> Optional[plt.Figure]:
    gauss_ext = stats_result.get("gaussian_extrapolation")
    sigma_lv  = stats_result.get("sigma_level")

    if not gauss_ext:
        return None

    table_data = gauss_ext["sigma_table"]
    levels     = list(table_data.keys())
    col_labels = ["Sigma", "Yield (%)", "DPM", "Status"]
    rows = []
    for lv in levels:
        row_data = table_data[lv]
        sigma_n  = float(lv[:-1])
        status   = ("✓ Atual"
                    if sigma_lv is not None
                    and abs(sigma_n - sigma_lv) < 0.5
                    and sigma_n <= sigma_lv + 0.5
                    else "")
        rows.append([lv,
                     f"{row_data['yield']:.4f}",
                     f"{row_data['dpm']:.1f}",
                     status])

    fig, ax = plt.subplots(figsize=(6, 3.2))
    fig.patch.set_facecolor("white")
    ax.axis("off")

    tbl = ax.table(
        cellText=rows,
        colLabels=col_labels,
        loc="center",
        cellLoc="center",
    )
    tbl.auto_set_font_size(False)
    tbl.set_fontsize(9)
    tbl.scale(1, 1.5)

    for (r, c), cell in tbl.get_celld().items():
        cell.set_edgecolor("#cccccc")
        if r == 0:
            cell.set_facecolor(C_NAVY)
            cell.set_text_props(color="white", fontweight="bold")
        elif rows[r - 1][3] == "✓ Atual":
            cell.set_facecolor("#d4edda")
        elif r % 2 == 0:
            cell.set_facecolor(C_LIGHT)

    ax.set_title("Extrapolação Gaussiana",
                 fontsize=11, fontweight="bold", pad=12)
    fig.tight_layout()
    return fig


# ---------------------------------------------------------------------------
# Teste rápido
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import sys, os
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), "tests"))
    from mock_data import generate_mock_merged_data, generate_mock_project_config
    from analyzer import analyze_pair

    cfg    = generate_mock_project_config()
    pair   = cfg.pairs[0]
    merged = generate_mock_merged_data(n=2000, distribution="normal")
    result = analyze_pair(pair, merged)

    tf_vals = result["tf"]["values"]

    figs = {
        "scatter":        plot_scatter_with_fit(
                              result["arr_in"], result["arr_out"],
                              result["tf"], pair, result["stats"]),
        "error_vs_input": plot_error_vs_input(
                              result["arr_in"], tf_vals, pair, result["stats"]),
        "histogram":      plot_histogram(
                              tf_vals, pair, result["stats"],
                              result["normality"], result["alt_dist"]),
        "qq":             plot_qq(tf_vals, result["normality"]),
        "sigma_gauge":    plot_sigma_gauge(result["stats"]),
        "extrap_table":   plot_extrapolation_table(result["stats"]),
    }

    out_dir = os.path.join(os.path.dirname(__file__), "tests", "figures")
    os.makedirs(out_dir, exist_ok=True)
    for name, fig in figs.items():
        if fig is not None:
            path = os.path.join(out_dir, f"{name}.png")
            fig.savefig(path, dpi=200, bbox_inches="tight")
            print(f"Salvo: {path}")
            plt.close(fig)
    print("Todos os gráficos gerados em tests/figures/")
