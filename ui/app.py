"""
Janela principal CustomTkinter do SPICA — Spice Parallel Instance Circuit Analyzer.
"""
from __future__ import annotations

import customtkinter as ctk

from config import ProjectConfig
from ui.panel_config import PanelConfig
from ui.panel_pairs import PanelPairs
from ui.panel_jobs import PanelJobs
from ui.panel_results import PanelResults


class App(ctk.CTk):
    def __init__(self):
        super().__init__()
        self.title("SPICA — Spice Parallel Instance Circuit Analyzer")
        self.geometry("1100x720")
        self.minsize(900, 600)

        self.config_obj = ProjectConfig()

        self._build()

    # ------------------------------------------------------------------
    def _build(self):
        self.grid_rowconfigure(0, weight=1)
        self.grid_rowconfigure(1, weight=0)
        self.grid_columnconfigure(1, weight=1)

        # ── Sidebar esquerda
        sidebar = ctk.CTkFrame(self, width=130, corner_radius=0)
        sidebar.grid(row=0, column=0, sticky="nsew")
        sidebar.grid_rowconfigure(10, weight=1)  # empurra botões pro topo

        ctk.CTkLabel(
            sidebar, text="SPICA", font=("Arial", 15, "bold"),
        ).grid(row=0, column=0, padx=12, pady=(18, 2))
        ctk.CTkLabel(
            sidebar, text="Spice Parallel\nInstance Circuit\nAnalyzer",
            font=("Arial", 7), justify="center", text_color="gray",
        ).grid(row=1, column=0, padx=12, pady=(0, 12))

        nav_items = [
            ("Config",     self._show_config),
            ("Pairs",      self._show_pairs),
            ("Simulation", self._show_jobs),
            ("Results",    self._show_results),
        ]
        self._nav_buttons: dict[str, ctk.CTkButton] = {}
        for i, (name, cmd) in enumerate(nav_items, start=2):
            btn = ctk.CTkButton(
                sidebar, text=name, width=110,
                command=cmd,
                fg_color="transparent",
                hover_color=("#3a5f8a", "#1f3a5a"),
                anchor="w",
            )
            btn.grid(row=i, column=0, padx=8, pady=3)
            self._nav_buttons[name] = btn

        # Botão tema
        self._theme_btn = ctk.CTkButton(
            sidebar, text="☀ Light", width=110,
            command=self._toggle_theme,
            fg_color="transparent",
        )
        self._theme_btn.grid(row=11, column=0, padx=8, pady=(0, 14))

        # ── Área de conteúdo principal
        self._content = ctk.CTkFrame(self, corner_radius=0, fg_color="transparent")
        self._content.grid(row=0, column=1, sticky="nsew", padx=0, pady=0)
        self._content.grid_rowconfigure(0, weight=1)
        self._content.grid_columnconfigure(0, weight=1)

        # Instanciar painéis
        self._panel_config = PanelConfig(
            self._content, config=self.config_obj,
            on_meas_detected=self._on_meas_detected,
        )
        self._panel_pairs  = PanelPairs(self._content, config=self.config_obj)
        self._panel_jobs   = PanelJobs(
            self._content, config=self.config_obj,
            on_simulation_done=self._on_simulation_done,
        )
        self._panel_results = PanelResults(self._content, config=self.config_obj)

        for panel in (self._panel_config, self._panel_pairs,
                      self._panel_jobs, self._panel_results):
            panel.grid(row=0, column=0, sticky="nsew")

        # ── Barra de status inferior
        status_bar = ctk.CTkFrame(self, height=36, corner_radius=0)
        status_bar.grid(row=1, column=0, columnspan=2, sticky="ew")
        status_bar.grid_columnconfigure(0, weight=1)

        self._status_label = ctk.CTkLabel(
            status_bar, text="Pronto.", anchor="w", font=("Arial", 9),
        )
        self._status_label.grid(row=0, column=0, padx=12, sticky="w")

        ctk.CTkButton(
            status_bar, text="▶ Simular", width=110,
            command=self._quick_run,
        ).grid(row=0, column=1, padx=(0, 6), pady=3)

        ctk.CTkButton(
            status_bar, text="📄 Relatório PDF", width=130,
            command=self._quick_report,
        ).grid(row=0, column=2, padx=(0, 12), pady=3)

        # Mostrar tela inicial
        self._show_config()

    # ------------------------------------------------------------------
    def _show_panel(self, panel):
        panel.tkraise()

    def _show_config(self):
        self._set_active_nav("Config")
        self._show_panel(self._panel_config)

    def _show_pairs(self):
        # Salva config antes de trocar de painel
        self._panel_config.apply_to_config()
        self._set_active_nav("Pairs")
        self._show_panel(self._panel_pairs)

    def _show_jobs(self):
        self._panel_config.apply_to_config()
        self._panel_pairs.apply_to_config()
        self._set_active_nav("Simulation")
        self._show_panel(self._panel_jobs)

    def _show_results(self):
        self._set_active_nav("Results")
        self._show_panel(self._panel_results)

    def _set_active_nav(self, name: str):
        for btn_name, btn in self._nav_buttons.items():
            if btn_name == name:
                btn.configure(fg_color=("#1a6faf", "#1a3a5c"))
            else:
                btn.configure(fg_color="transparent")

    # ------------------------------------------------------------------
    def _toggle_theme(self):
        current = ctk.get_appearance_mode()
        if current == "Dark":
            ctk.set_appearance_mode("light")
            self._theme_btn.configure(text="🌙 Dark")
        else:
            ctk.set_appearance_mode("dark")
            self._theme_btn.configure(text="☀ Light")

    # ------------------------------------------------------------------
    def _on_meas_detected(self, names: list):
        self._panel_pairs.set_meas_names(names)
        self._status_label.configure(
            text=f"{len(names)} medida(s) detectada(s): {', '.join(names[:6])}"
                 + ("…" if len(names) > 6 else "")
        )

    def _on_simulation_done(self, merged_data: dict):
        self._status_label.configure(
            text=f"Simulação concluída — {merged_data['n_runs']} runs, "
                 f"{merged_data['n_failed']} falhas."
        )
        # Passa os resultados para o painel
        self._panel_results.load_results(merged_data)
        self._show_results()

    def _quick_run(self):
        self._show_jobs()

    def _quick_report(self):
        self._show_results()
        self._panel_results._generate_pdf()
