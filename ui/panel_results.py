"""
Painel de resultados: tabela resumo + gráficos inline por par.
"""
from __future__ import annotations

from pathlib import Path
from tkinter import filedialog, messagebox
from typing import List, Optional

import customtkinter as ctk
import matplotlib
matplotlib.use("TkAgg")
import matplotlib.pyplot as plt
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg

from config import ProjectConfig
from analyzer import analyze_pair
from plotter import (
    plot_scatter_with_fit,
    plot_error_vs_input,
    plot_histogram,
    plot_qq,
    plot_sigma_gauge,
    plot_extrapolation_table,
)


class PanelResults(ctk.CTkFrame):
    def __init__(self, master, config: ProjectConfig, **kwargs):
        super().__init__(master, **kwargs)
        self.config_obj      = config
        self._merged_data    = None
        self._analysis       = []
        self._canvases: list  = []
        self._build()

    # ------------------------------------------------------------------
    def _build(self):
        self.grid_rowconfigure(2, weight=1)
        self.grid_columnconfigure(0, weight=1)

        # Cabeçalho
        hdr = ctk.CTkFrame(self, fg_color="transparent")
        hdr.grid(row=0, column=0, padx=12, pady=(10,4), sticky="ew")
        hdr.grid_columnconfigure(0, weight=1)

        self._status_label = ctk.CTkLabel(
            hdr, text="Sem resultados. Execute a simulação primeiro.",
            anchor="w",
        )
        self._status_label.grid(row=0, column=0, sticky="w")

        self._btn_report = ctk.CTkButton(
            hdr, text="Gerar Relatório PDF",
            command=self._generate_pdf, state="disabled",
        )
        self._btn_report.grid(row=0, column=1, padx=(8,4))

        self._btn_csv = ctk.CTkButton(
            hdr, text="Exportar CSV",
            command=self._export_csv, state="disabled",
            fg_color="#2d7a2d", hover_color="#1f5c1f", width=110,
        )
        self._btn_csv.grid(row=0, column=2, padx=(0,0))

        # Tabela resumo
        self._summary_frame = ctk.CTkFrame(self)
        self._summary_frame.grid(row=1, column=0, padx=12, pady=(0,6), sticky="ew")

        # Tab por par
        self._tab_view = ctk.CTkTabview(self)
        self._tab_view.grid(row=2, column=0, padx=12, pady=(0,10), sticky="nsew")

    # ------------------------------------------------------------------
    def load_results(self, merged_data: dict):
        """
        Chamado depois que a simulação e a leitura dos logs concluem.
        Computa a análise e popula a UI.
        """
        self._merged_data = merged_data
        self._analysis.clear()
        self._canvases.clear()

        # Limpar tabs antigas
        for tab_name in list(self._tab_view._tab_dict.keys()):
            try:
                self._tab_view.delete(tab_name)
            except Exception:
                pass

        # Rodar análise
        errors = []
        for pair in self.config_obj.pairs:
            try:
                result = analyze_pair(pair, merged_data)
                self._analysis.append(result)
            except Exception as exc:
                errors.append(f"{pair.label}: {exc}")

        if errors:
            messagebox.showwarning(
                "Aviso", "Erros em alguns pares:\n" + "\n".join(errors)
            )

        self._populate_summary()
        self._populate_tabs()

        n_ok   = len(self._analysis)
        n_fail = len(errors)
        self._status_label.configure(
            text=f"{n_ok} par(es) analisado(s)"
                 + (f" — {n_fail} erro(s)" if n_fail else "")
        )
        if n_ok > 0:
            self._btn_report.configure(state="normal")
            self._btn_csv.configure(state="normal")

    # ------------------------------------------------------------------
    def _populate_summary(self):
        # Limpar
        for w in self._summary_frame.winfo_children():
            w.destroy()

        headers = ["Par", "N válidos", "Média", "Std", "Cpk", "Sigma", "Yield obs.", "Status"]
        for col, h in enumerate(headers):
            ctk.CTkLabel(
                self._summary_frame, text=h,
                font=("Arial", 10, "bold"),
                fg_color="#1a3a5c", text_color="white",
                corner_radius=0,
            ).grid(row=0, column=col, padx=1, pady=1, sticky="nsew")
            self._summary_frame.grid_columnconfigure(col, weight=1)

        for row_idx, result in enumerate(self._analysis, start=1):
            st   = result["stats"]
            pair = result["pair"]

            sigma = st.get("sigma_level")
            cpk   = st.get("cpk")
            yield_obs = st.get("yield_observed_pct")
            n_valid = int((~__import__("numpy").isnan(result["tf"]["values"])).sum())

            if sigma is not None:
                if sigma >= 4:
                    status, color = "PASS", "#2d7a2d"
                elif sigma >= 3:
                    status, color = "MARGINAL", "#b85c00"
                else:
                    status, color = "FAIL", "#a00000"
            elif yield_obs is not None:
                if yield_obs >= 99.73:
                    status, color = "PASS", "#2d7a2d"
                elif yield_obs >= 99.0:
                    status, color = "MARGINAL", "#b85c00"
                else:
                    status, color = "FAIL", "#a00000"
            else:
                status, color = "N/A", "#555"

            def _fmt(v, fmt=".4g"):
                return f"{v:{fmt}}" if v is not None else "—"

            values = [
                pair.label,
                str(n_valid),
                _fmt(st["mean"]),
                _fmt(st["std"]),
                _fmt(cpk, ".3f"),
                _fmt(sigma, ".2f"),
                f"{yield_obs:.2f}%" if yield_obs is not None else "—",
                status,
            ]
            bg = "#f5f5f5" if row_idx % 2 == 0 else "#ffffff"
            for col, val in enumerate(values):
                text_color = color if col == 7 else ("black" if bg == "#ffffff" else "#222")
                ctk.CTkLabel(
                    self._summary_frame, text=val,
                    text_color=text_color,
                    font=("Arial", 9, "bold") if col == 7 else ("Arial", 9),
                ).grid(row=row_idx, column=col, padx=2, pady=2, sticky="nsew")

    # ------------------------------------------------------------------
    def _populate_tabs(self):
        for result in self._analysis:
            pair = result["pair"]
            tab_name = pair.label or f"{pair.meas_in}→{pair.meas_out}"

            tab = self._tab_view.add(tab_name)
            tab.grid_rowconfigure(0, weight=1)
            tab.grid_columnconfigure(0, weight=1)

            subtab = ctk.CTkTabview(tab)
            subtab.grid(row=0, column=0, sticky="nsew", padx=4, pady=4)

            st  = result["stats"]
            nm  = result["normality"]
            alt = result["alt_dist"]
            tf  = result["tf"]
            tf_vals = tf["values"]

            plot_specs = [
                ("Scatter",      lambda r=result: plot_scatter_with_fit(
                                    r["arr_in"], r["arr_out"], r["tf"], r["pair"], r["stats"])),
                ("Erro×Entrada", lambda r=result: plot_error_vs_input(
                                    r["arr_in"], r["tf"]["values"], r["pair"], r["stats"])),
                ("Histograma",   lambda r=result: plot_histogram(
                                    r["tf"]["values"], r["pair"], r["stats"],
                                    r["normality"], r["alt_dist"])),
                ("Q-Q",          lambda r=result: plot_qq(
                                    r["tf"]["values"], r["normality"])),
                ("Capability",   lambda r=result: plot_sigma_gauge(r["stats"])),
                ("Extrapolação", lambda r=result: plot_extrapolation_table(r["stats"])),
            ]

            for sub_name, fig_fn in plot_specs:
                sub = subtab.add(sub_name)
                sub.grid_rowconfigure(0, weight=1)
                sub.grid_columnconfigure(0, weight=1)
                # Lazy load: cria figura ao clicar na aba
                self._embed_figure_lazy(sub, fig_fn, sub_name)

    def _embed_figure_lazy(self, parent, fig_fn: callable, name: str):
        """Embute um placeholder; desenha a figura na primeira vez que o frame é exposto."""
        placeholder = ctk.CTkLabel(
            parent, text=f"Carregando {name}…", fg_color="transparent",
        )
        placeholder.pack(expand=True, fill="both")

        def _load(event=None):
            placeholder.pack_forget()
            try:
                fig = fig_fn()
            except Exception as exc:
                ctk.CTkLabel(parent, text=f"Erro: {exc}",
                             text_color="red").pack(expand=True)
                return
            if fig is None:
                ctk.CTkLabel(parent, text="(sem dados para este gráfico)",
                             text_color="#888").pack(expand=True)
                return
            canvas = FigureCanvasTkAgg(fig, master=parent)
            canvas.draw()
            canvas.get_tk_widget().pack(fill="both", expand=True)
            self._canvases.append(canvas)
            plt.close(fig)
            parent.unbind("<Visibility>")

        parent.bind("<Visibility>", _load)

    # ------------------------------------------------------------------
    def _generate_pdf(self):
        if not self._analysis or self._merged_data is None:
            messagebox.showwarning("Aviso", "Nenhum resultado disponível.")
            return

        output_path = filedialog.asksaveasfilename(
            title="Salvar relatório PDF",
            defaultextension=".pdf",
            filetypes=[("PDF", "*.pdf"), ("Todos", "*.*")],
            initialfile=f"mc_report_{self.config_obj.name.replace(' ', '_')}.pdf",
        )
        if not output_path:
            return

        try:
            from reporter import generate_report
            pdf_path = generate_report(
                project=self.config_obj,
                merged_data=self._merged_data,
                output_pdf=output_path,
                analysis_results=self._analysis,
            )
            messagebox.showinfo("Relatório", f"PDF gerado com sucesso:\n{pdf_path}")
        except Exception as exc:
            import traceback
            messagebox.showerror(
                "Erro ao gerar PDF",
                f"{exc}\n\n{traceback.format_exc()[:800]}",
            )

    def _export_csv(self):
        if not self._analysis or self._merged_data is None:
            messagebox.showwarning("Aviso", "Nenhum resultado disponível.")
            return

        output_path = filedialog.asksaveasfilename(
            title="Exportar dados como CSV",
            defaultextension=".csv",
            filetypes=[("CSV", "*.csv"), ("Todos", "*.*")],
            initialfile=f"mc_data_{self.config_obj.name.replace(' ', '_')}.csv",
        )
        if not output_path:
            return

        try:
            from reporter import export_csv
            csv_path = export_csv(
                merged_data=self._merged_data,
                analysis_results=self._analysis,
                output_csv=output_path,
            )
            messagebox.showinfo("CSV", f"Dados exportados:\n{csv_path}")
        except Exception as exc:
            import traceback
            messagebox.showerror(
                "Erro ao exportar CSV",
                f"{exc}\n\n{traceback.format_exc()[:800]}",
            )
