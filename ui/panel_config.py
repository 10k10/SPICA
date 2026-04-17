"""
Painel de configuração do projeto.
Detecta .meas automaticamente ao carregar o .asc.
O .step param é gerenciado de forma totalmente automática pelo chunk_builder.
"""
from __future__ import annotations

import os
import re
from pathlib import Path
from tkinter import filedialog, messagebox
from typing import Callable, List, Optional

import customtkinter as ctk

from config import ProjectConfig, _default_ltspice_path


# ---------------------------------------------------------------------------
# Parsing do .asc
# ---------------------------------------------------------------------------

def _extract_directives_from_asc(content: str) -> List[str]:
    """
    Extrai todas as diretivas SPICE ativas de um arquivo .asc.

    Formato:
      TEXT <x> <y> <align> <size> !<diretiva>
      TEXT <x> <y> <align> <size> !linha1\\nlinha2\\n...

    Linhas com ';' em vez de '!' são comentários — ignoradas.
    """
    directives: List[str] = []
    text_re = re.compile(
        r"^TEXT\s+-?\d+\s+-?\d+\s+\S+\s+\d+\s+!(.+)$",
        re.IGNORECASE | re.MULTILINE,
    )
    for match in text_re.finditer(content):
        raw = match.group(1)
        for sub in raw.split("\\n"):
            sub = sub.strip()
            if sub:
                directives.append(sub)
    return directives


def _parse_meas_names(directives: List[str]) -> List[str]:
    """
    Extrai nomes de medidas de linhas .meas / .measure.

    Formatos:
      .meas [TRAN|AC|DC|NOISE|OP] <name> ...
      .measure [TRAN|AC|DC|NOISE|OP] <name> ...
    """
    meas_re = re.compile(
        r"^\.meas(?:ure)?\s+(?:(?:tran|ac|dc|noise|op)\s+)?(\w+)",
        re.IGNORECASE,
    )
    names: List[str] = []
    for d in directives:
        m = meas_re.match(d)
        if m:
            name = m.group(1).lower()
            if name not in names:
                names.append(name)
    return names


# ---------------------------------------------------------------------------
# Painel
# ---------------------------------------------------------------------------

class PanelConfig(ctk.CTkFrame):
    def __init__(self, master, config: ProjectConfig,
                 on_meas_detected: Optional[Callable[[List[str]], None]] = None,
                 **kwargs):
        super().__init__(master, **kwargs)
        self.config_obj       = config
        self.on_meas_detected = on_meas_detected
        self._build()

    # ------------------------------------------------------------------
    def _build(self):
        self.grid_columnconfigure(1, weight=1)
        row = 0

        def lbl(text, r, col=0):
            ctk.CTkLabel(self, text=text, anchor="w").grid(
                row=r, column=col, padx=(12, 6), pady=4, sticky="w")

        def entry(r, default="", width=340, col=1, colspan=1):
            e = ctk.CTkEntry(self, width=width)
            e.insert(0, default)
            e.grid(row=r, column=col, columnspan=colspan,
                   padx=4, pady=4, sticky="ew")
            return e

        # Nome do projeto
        lbl("Nome do Projeto:", row)
        self._name = entry(row, self.config_obj.name)
        row += 1

        # Arquivo .asc — auto-detecta .meas ao selecionar
        lbl("Arquivo .asc:", row)
        self._asc = entry(row, self.config_obj.asc_path)
        self._asc.bind("<FocusOut>", lambda _e: self._auto_detect())
        self._asc.bind("<Return>",   lambda _e: self._auto_detect())
        ctk.CTkButton(self, text="Browse…", width=80,
                      command=self._browse_asc).grid(
                          row=row, column=2, padx=(4, 12), pady=4)
        row += 1

        # Executável LTSpice
        lbl("Executável LTSpice:", row)
        self._ltspice = entry(row, self.config_obj.ltspice_path)
        ctk.CTkButton(self, text="Browse…", width=80,
                      command=self._browse_ltspice).grid(
                          row=row, column=2, padx=(4, 12), pady=4)
        row += 1

        # Pasta de saída
        lbl("Pasta de saída:", row)
        self._outdir = entry(row, self.config_obj.output_dir)
        ctk.CTkButton(self, text="Browse…", width=80,
                      command=self._browse_outdir).grid(
                          row=row, column=2, padx=(4, 12), pady=4)
        row += 1

        # Total de runs
        lbl("Total de runs:", row)
        self._runs = ctk.CTkEntry(self, width=120)
        self._runs.insert(0, str(self.config_obj.total_runs))
        self._runs.grid(row=row, column=1, padx=4, pady=4, sticky="w")
        row += 1

        # Núcleos paralelos
        lbl("Núcleos paralelos:", row)
        cores_frame = ctk.CTkFrame(self, fg_color="transparent")
        cores_frame.grid(row=row, column=1, padx=4, pady=4, sticky="w")
        self._cores_label = ctk.CTkLabel(cores_frame,
                                          text=str(self.config_obj.n_cores),
                                          width=30)
        self._cores_label.pack(side="left", padx=(0, 6))
        max_cores = os.cpu_count() or 8
        self._cores_slider = ctk.CTkSlider(
            cores_frame, from_=1, to=max_cores,
            number_of_steps=max_cores - 1,
            command=self._on_cores_change, width=220)
        self._cores_slider.set(self.config_obj.n_cores)
        self._cores_slider.pack(side="left")
        row += 1

        # Autor / Versão
        lbl("Autor:", row)
        self._author = entry(row, self.config_obj.author, width=200)
        row += 1
        lbl("Versão:", row)
        self._version = entry(row, self.config_obj.version, width=80)
        row += 1

        # ── Medidas detectadas (atualizada automaticamente)
        ctk.CTkLabel(self, text="Medidas detectadas (.meas):",
                     anchor="w", font=("Arial", 10, "bold")).grid(
                         row=row, column=0, columnspan=3,
                         padx=12, pady=(14, 2), sticky="w")
        row += 1

        self._meas_box = ctk.CTkTextbox(self, height=72, state="disabled",
                                         font=("Courier New", 10))
        self._meas_box.grid(row=row, column=0, columnspan=3,
                             padx=12, pady=(0, 6), sticky="ew")
        row += 1

        self.grid_rowconfigure(row, weight=1)

        # Se o asc já estava configurado (ex: config carregada), detecta agora
        if self.config_obj.asc_path and Path(self.config_obj.asc_path).exists():
            self.after(100, self._auto_detect)

    # ------------------------------------------------------------------
    def _on_cores_change(self, val):
        self._cores_label.configure(text=str(int(val)))

    def _browse_asc(self):
        path = filedialog.askopenfilename(
            title="Selecionar arquivo .asc",
            filetypes=[("LTSpice Schematics", "*.asc"), ("Todos", "*.*")],
        )
        if path:
            self._asc.delete(0, "end")
            self._asc.insert(0, path)
            self._auto_detect()

    def _browse_ltspice(self):
        path = filedialog.askopenfilename(
            title="Selecionar executável LTSpice",
            filetypes=[("Executável", "*.exe"), ("Todos", "*.*")],
            initialdir=r"C:\Program Files",
        )
        if path:
            self._ltspice.delete(0, "end")
            self._ltspice.insert(0, path)

    def _browse_outdir(self):
        path = filedialog.askdirectory(title="Selecionar pasta de saída")
        if path:
            self._outdir.delete(0, "end")
            self._outdir.insert(0, path)

    # ------------------------------------------------------------------
    def _auto_detect(self):
        """Lê o .asc e extrai as .meas. Chamado automaticamente."""
        asc_path = self._asc.get().strip()
        if not asc_path or not Path(asc_path).exists():
            return

        try:
            content = Path(asc_path).read_text(encoding="latin-1", errors="replace")
        except Exception:
            return

        directives = _extract_directives_from_asc(content)
        meas_names = _parse_meas_names(directives)

        self._meas_box.configure(state="normal")
        self._meas_box.delete("1.0", "end")
        if meas_names:
            self._meas_box.insert(
                "end",
                f"  {len(meas_names)} medida(s) encontrada(s):\n"
                f"  " + "   ".join(meas_names),
            )
            if self.on_meas_detected:
                self.on_meas_detected(meas_names)
        else:
            self._meas_box.insert(
                "end",
                "  Nenhuma medida .meas encontrada.\n"
                "  (verifique se as diretivas não estão comentadas com ';')",
            )
        self._meas_box.configure(state="disabled")

    # ------------------------------------------------------------------
    def apply_to_config(self) -> ProjectConfig:
        cfg = self.config_obj
        cfg.name         = self._name.get().strip()
        cfg.asc_path     = self._asc.get().strip()
        cfg.ltspice_path = self._ltspice.get().strip()
        cfg.output_dir   = self._outdir.get().strip()
        cfg.author       = self._author.get().strip()
        cfg.version      = self._version.get().strip() or "1.0"
        # step_param_name mantém o padrão "run" — gerenciado automaticamente

        try:
            cfg.total_runs = int(self._runs.get())
        except ValueError:
            messagebox.showwarning("Aviso", "Total de runs inválido. Usando 1000.")
            cfg.total_runs = 1000
        cfg.n_cores = int(self._cores_slider.get())
        return cfg
