"""
Geração do relatório PDF via Jinja2 + WeasyPrint (ou xhtml2pdf como fallback).
"""
from __future__ import annotations

import logging
import os
from datetime import datetime

import numpy as np
from pathlib import Path
from typing import Optional

logger_rep = logging.getLogger(__name__)

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from jinja2 import Environment, FileSystemLoader

from config import ProjectConfig, PairConfig
from analyzer import analyze_pair
from plotter import (
    plot_scatter_with_fit,
    plot_error_vs_input,
    plot_histogram,
    plot_qq,
    plot_sigma_gauge,
    plot_extrapolation_table,
)


def _save_fig(fig: Optional[plt.Figure], path: Path) -> Optional[str]:
    if fig is None:
        return None
    fig.savefig(str(path), dpi=200, bbox_inches="tight")
    plt.close(fig)
    return str(path)


def _compute_verdict(pairs_data: list[dict]) -> dict:
    n_pass = n_fail = n_marginal = 0
    for item in pairs_data:
        sigma = item["stats"].get("sigma_level")
        yield_obs = item["stats"].get("yield_observed_pct")
        if sigma is not None:
            if sigma >= 4.0:
                n_pass += 1
            elif sigma >= 3.0:
                n_marginal += 1
            else:
                n_fail += 1
        elif yield_obs is not None:
            if yield_obs >= 99.73:
                n_pass += 1
            elif yield_obs >= 99.0:
                n_marginal += 1
            else:
                n_fail += 1
        else:
            n_marginal += 1

    if n_fail > 0:
        verdict = "FAIL"
    elif n_marginal > 0:
        verdict = "MARGINAL"
    else:
        verdict = "PASS"

    return {
        "all_pass": n_fail == 0 and n_marginal == 0,
        "pairs_pass": n_pass,
        "pairs_fail": n_fail,
        "pairs_marginal": n_marginal,
        "verdict": verdict,
    }


def generate_report(
    project: ProjectConfig,
    merged_data: dict,
    output_pdf: str,
    analysis_results: Optional[list[dict]] = None,
) -> str:
    """
    Gera o relatório PDF completo.

    Parâmetros
    ----------
    project          : ProjectConfig com os pares definidos
    merged_data      : dict do log_merger
    output_pdf       : caminho do PDF de saída
    analysis_results : resultados pré-computados (opcional; se None, computa aqui)

    Retorna
    -------
    str : caminho absoluto do PDF gerado
    """
    output_pdf = Path(output_pdf).resolve()
    output_dir = output_pdf.parent
    figures_dir = output_dir / "figures"
    figures_dir.mkdir(parents=True, exist_ok=True)

    # 1. Rodar análise se não foi fornecida
    if analysis_results is None:
        analysis_results = [analyze_pair(pair, merged_data) for pair in project.pairs]

    # 2. Gerar figuras
    pairs_data: list[dict] = []
    for idx, result in enumerate(analysis_results):
        pair    = result["pair"]
        st      = result["stats"]
        nm      = result["normality"]
        alt     = result["alt_dist"]
        tf      = result["tf"]
        tf_vals = tf["values"]

        prefix = figures_dir / f"pair{idx}"

        figs = {
            "scatter": _save_fig(
                plot_scatter_with_fit(result["arr_in"], result["arr_out"], tf, pair, st),
                Path(str(prefix) + "_scatter.png"),
            ),
            "error_vs_input": _save_fig(
                plot_error_vs_input(result["arr_in"], tf_vals, pair, st),
                Path(str(prefix) + "_error_vs_input.png"),
            ),
            "histogram": _save_fig(
                plot_histogram(tf_vals, pair, st, nm, alt),
                Path(str(prefix) + "_histogram.png"),
            ),
            "qq": _save_fig(
                plot_qq(tf_vals, nm),
                Path(str(prefix) + "_qq.png"),
            ),
            "sigma_gauge": _save_fig(
                plot_sigma_gauge(st),
                Path(str(prefix) + "_sigma_gauge.png"),
            ),
            "extrapolation": _save_fig(
                plot_extrapolation_table(st),
                Path(str(prefix) + "_extrapolation.png"),
            ),
        }

        pairs_data.append({
            "pair":      pair,
            "stats":     st,
            "normality": nm,
            "alt_dist":  alt,
            "tf":        tf,
            "figures":   figs,
        })

    # 3. Renderizar template Jinja2
    templates_dir = Path(__file__).parent / "templates"
    env = Environment(
        loader=FileSystemLoader(str(templates_dir)),
        autoescape=False,
    )

    # Filtro auxiliar
    env.tests["not none"] = lambda v: v is not None

    template = env.get_template("report.html")

    context = {
        "project":       project,
        "generated_at":  datetime.now().strftime("%d/%m/%Y %H:%M:%S"),
        "n_runs_total":  merged_data["n_runs"],
        "n_runs_failed": merged_data["n_failed"],
        "pairs":         pairs_data,
        "summary":       _compute_verdict(pairs_data),
    }

    html_str = template.render(**context)

    # 4. Tentar gerar PDF (WeasyPrint → reportlab → HTML fallback)

    # -- Tentativa 1: WeasyPrint (melhor qualidade, requer GTK no Windows)
    if _try_weasyprint(html_str, output_pdf, figures_dir):
        print(f"Relatório PDF gerado: {output_pdf}")
        return str(output_pdf)

    # -- Tentativa 2: reportlab (puro Python, sem deps nativas)
    if _try_reportlab(pairs_data, context, output_pdf):
        print(f"Relatório PDF gerado: {output_pdf}")
        return str(output_pdf)

    # -- Fallback final: HTML
    html_path = output_pdf.with_suffix(".html")
    html_path.write_text(html_str, encoding="utf-8")
    print(
        f"PDF não pôde ser gerado (WeasyPrint requer GTK; reportlab falhou).\n"
        f"HTML salvo em: {html_path}"
    )
    return str(html_path)


def _try_weasyprint(html_str: str, output_pdf: Path, figures_dir: Path) -> bool:
    try:
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            from weasyprint import HTML as WP_HTML
        WP_HTML(
            string=html_str,
            base_url=str(figures_dir.parent) + "/",
        ).write_pdf(str(output_pdf))
        return True
    except Exception:
        return False


def _try_reportlab(pairs_data: list, context: dict, output_pdf: Path) -> bool:
    """
    Gera o PDF diretamente com reportlab (Platypus).
    Não depende de GTK nem de conversão HTML→PDF — usa os PNGs já gerados.
    reportlab é instalado automaticamente como dependência do xhtml2pdf.
    """
    try:
        from reportlab.lib.pagesizes import A4
        from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
        from reportlab.lib.units import cm
        from reportlab.lib import colors
        from reportlab.platypus import (
            SimpleDocTemplate, Paragraph, Spacer, Image as RLImage,
            Table, TableStyle, PageBreak, HRFlowable,
        )
        from reportlab.lib.enums import TA_LEFT, TA_CENTER
    except ImportError:
        return False

    try:
        PAGE_W, PAGE_H = A4
        MARGIN = 1.5 * cm
        USABLE_W = PAGE_W - 2 * MARGIN

        doc = SimpleDocTemplate(
            str(output_pdf),
            pagesize=A4,
            leftMargin=MARGIN, rightMargin=MARGIN,
            topMargin=2 * cm, bottomMargin=2 * cm,
        )

        styles = getSampleStyleSheet()

        s_title = ParagraphStyle("rpt_title", parent=styles["Title"],
                                  fontSize=17, spaceAfter=4)
        s_sub   = ParagraphStyle("rpt_sub", parent=styles["Normal"],
                                  fontSize=9, textColor=colors.gray, spaceAfter=10)
        s_h2    = ParagraphStyle("rpt_h2", parent=styles["Heading2"],
                                  fontSize=12, spaceBefore=6, spaceAfter=4)
        s_norm  = styles["Normal"]

        project = context["project"]
        story   = []

        # ── Cabeçalho
        story.append(Paragraph(
            f"Relatório Monte Carlo — {project.name}", s_title))
        story.append(Paragraph(
            f"Gerado em {context['generated_at']}  |  "
            f"{context['n_runs_total']} runs  |  "
            f"{context['n_runs_failed']} falhas",
            s_sub,
        ))
        story.append(HRFlowable(width="100%", thickness=1,
                                 color=colors.HexColor("#cccccc")))
        story.append(Spacer(1, 0.3 * cm))

        # ── Veredicto geral
        summary = context["summary"]
        v_color = {"PASS": colors.HexColor("#2d7a2d"),
                   "MARGINAL": colors.HexColor("#b85c00"),
                   "FAIL": colors.HexColor("#a00000")}.get(
                       summary["verdict"], colors.black)
        story.append(Paragraph(
            f'<font color="{v_color.hexval() if hasattr(v_color,"hexval") else "#000000"}">'
            f'Veredicto: <b>{summary["verdict"]}</b></font>  '
            f'— {summary["pairs_pass"]} pass / '
            f'{summary["pairs_marginal"]} marginal / '
            f'{summary["pairs_fail"]} fail',
            ParagraphStyle("verdict", parent=s_norm, fontSize=11,
                            spaceAfter=10, textColor=v_color),
        ))

        # ── Seções por par
        def _fmt(v, digits=4):
            if v is None:
                return "—"
            try:
                return f"{float(v):.{digits}g}"
            except Exception:
                return str(v)

        HDR_BG  = colors.HexColor("#2a5caa")
        ROW_ALT = colors.HexColor("#f5f5f5")

        for i, pd in enumerate(pairs_data):
            if i > 0:
                story.append(PageBreak())

            pair = pd["pair"]
            st   = pd["stats"]
            nm   = pd["normality"]

            story.append(Paragraph(
                f"Par {i+1}: {pair.label or f'{pair.meas_in} → {pair.meas_out}'}",
                s_h2,
            ))
            story.append(HRFlowable(width="100%", thickness=0.5,
                                     color=colors.lightgrey))
            story.append(Spacer(1, 0.2 * cm))

            # Tabela de estatísticas
            lsl = pair.spec.lsl
            usl = pair.spec.usl
            spec_str = (
                f"[{_fmt(lsl)}, {_fmt(usl)}] {pair.unit}"
                if (lsl is not None or usl is not None) else "—"
            )
            norm_str = nm.get("conclusion", "—") if nm else "—"

            tbl_data = [
                ["Estatística", "Valor", "Estatística", "Valor"],
                ["Média",          _fmt(st.get("mean")),
                 "Cp",             _fmt(st.get("cp"))],
                ["Desvio padrão",  _fmt(st.get("std")),
                 "Cpk",            _fmt(st.get("cpk"))],
                ["Mínimo",         _fmt(st.get("min")),
                 "Sigma level",    _fmt(st.get("sigma_level"), 3)],
                ["Máximo",         _fmt(st.get("max")),
                 "Yield obs.",     _fmt(st.get("yield_observed_pct"), 3) + " %"],
                ["Skewness",       _fmt(st.get("skewness")),
                 "Spec limits",    spec_str],
                ["Kurtosis",       _fmt(st.get("kurtosis")),
                 "Normalidade",    norm_str],
            ]

            col_w = USABLE_W / 4
            tbl = Table(tbl_data, colWidths=[col_w * 1.4, col_w * 0.6,
                                              col_w * 1.4, col_w * 0.6])
            tbl.setStyle(TableStyle([
                ("BACKGROUND",    (0, 0), (-1, 0), HDR_BG),
                ("TEXTCOLOR",     (0, 0), (-1, 0), colors.white),
                ("FONTNAME",      (0, 0), (-1, 0), "Helvetica-Bold"),
                ("FONTSIZE",      (0, 0), (-1, -1), 8),
                ("ROWBACKGROUNDS",(0, 1), (-1, -1), [colors.white, ROW_ALT]),
                ("GRID",          (0, 0), (-1, -1), 0.3, colors.lightgrey),
                ("TOPPADDING",    (0, 0), (-1, -1), 3),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
                ("LEFTPADDING",   (0, 0), (-1, -1), 5),
            ]))
            story.append(tbl)
            story.append(Spacer(1, 0.4 * cm))

            # Figuras em grade 2×N
            fig_keys  = ["scatter", "histogram", "qq",
                         "error_vs_input", "sigma_gauge", "extrapolation"]
            available = [pd["figures"].get(k)
                         for k in fig_keys
                         if pd["figures"].get(k) and Path(pd["figures"][k]).exists()]

            if available:
                fig_w = (USABLE_W - 0.4 * cm) / 2
                fig_h = fig_w * 0.68

                grid_rows = []
                for j in range(0, len(available), 2):
                    row_cells = []
                    for p in available[j:j + 2]:
                        try:
                            row_cells.append(RLImage(p, width=fig_w, height=fig_h))
                        except Exception:
                            row_cells.append(Spacer(fig_w, fig_h))
                    if len(row_cells) == 1:
                        row_cells.append(Spacer(fig_w, fig_h))
                    grid_rows.append(row_cells)

                fig_tbl = Table(grid_rows, colWidths=[fig_w, fig_w], hAlign="LEFT")
                fig_tbl.setStyle(TableStyle([
                    ("VALIGN",        (0, 0), (-1, -1), "TOP"),
                    ("LEFTPADDING",   (0, 0), (-1, -1), 2),
                    ("RIGHTPADDING",  (0, 0), (-1, -1), 2),
                    ("TOPPADDING",    (0, 0), (-1, -1), 4),
                    ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
                ]))
                story.append(fig_tbl)

        doc.build(story)
        return True

    except Exception as exc:
        logger_rep.warning("reportlab falhou: %s", exc)
        try:
            output_pdf.unlink(missing_ok=True)
        except Exception:
            pass
        return False


# ---------------------------------------------------------------------------
# CSV Export
# ---------------------------------------------------------------------------

def export_csv(
    merged_data: dict,
    analysis_results: list,
    output_csv: str,
) -> str:
    """
    Exporta os dados brutos e as estatísticas por par para um CSV.

    Colunas geradas:
      run  |  <meas_1>  |  <meas_2>  |  ...  |  tf_<pair_label>  |  ...

    Retorna o caminho absoluto do arquivo gerado.
    """
    import csv

    output_csv = Path(output_csv).resolve()
    measures   = merged_data["measures"]
    n_runs     = merged_data["n_runs"]

    # Colunas de medidas brutas
    meas_names  = sorted(measures.keys())
    # Colunas de TF por par
    tf_columns  = []
    tf_arrays   = []
    for result in analysis_results:
        pair  = result["pair"]
        label = pair.label or f"{pair.meas_in}_to_{pair.meas_out}"
        safe  = label.replace(" ", "_").replace("/", "_")
        tf_columns.append(f"tf_{safe}")
        tf_arrays.append(result["tf"]["values"])

    header = ["run"] + meas_names + tf_columns

    with open(str(output_csv), "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(header)
        for i in range(n_runs):
            row = [i + 1]
            for name in meas_names:
                v = measures[name][i] if i < len(measures[name]) else ""
                row.append(f"{v:.8e}" if isinstance(v, float) and np.isfinite(v) else v)
            for arr in tf_arrays:
                v = arr[i] if i < len(arr) else ""
                row.append(f"{v:.8e}" if isinstance(v, (int, float)) and np.isfinite(float(v if v != "" else float("nan"))) else v)
            writer.writerow(row)

    print(f"CSV exportado: {output_csv}  ({n_runs} linhas)")
    return str(output_csv)


# ---------------------------------------------------------------------------
# Teste rápido
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import sys
    sys.path.insert(0, str(Path(__file__).parent / "tests"))
    from mock_data import generate_mock_merged_data, generate_mock_project_config

    cfg    = generate_mock_project_config()
    merged = generate_mock_merged_data(n=500, distribution="normal")

    out = generate_report(
        project=cfg,
        merged_data=merged,
        output_pdf="./mc_results_mock/report_test.pdf",
    )
    print(f"Saída: {out}")
