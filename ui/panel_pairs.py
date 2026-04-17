"""
Painel de seleção e edição de pares meas_in / meas_out.
"""
from __future__ import annotations

from typing import List, Optional
import tkinter as tk

import customtkinter as ctk

from config import PairConfig, SpecLimits, ProjectConfig

TF_TYPES = [
    "linear",
    "linear_fit",
    "origin_fit",
    "poly2_fit",
    "db",
    "error_abs",
    "error_rel_pct",
    "custom",
]


class _PairCard(ctk.CTkFrame):
    """Card editável para um único PairConfig."""

    def __init__(self, master, pair: PairConfig, meas_names: List[str],
                 on_remove: callable, **kwargs):
        super().__init__(master, border_width=1, **kwargs)
        self.pair = pair
        self.meas_names = meas_names or ["—"]
        self.on_remove = on_remove
        self._build()

    def _build(self):
        self.grid_columnconfigure((1, 3, 5), weight=1)
        r = 0

        # ---- Linha 0: meas_in / meas_out / label / botão remover
        ctk.CTkLabel(self, text="Entrada:").grid(row=r, column=0, padx=(8,4), pady=3, sticky="w")
        self._meas_in = ctk.CTkOptionMenu(self, values=self.meas_names, width=160)
        self._meas_in.set(self.pair.meas_in or self.meas_names[0])
        self._meas_in.grid(row=r, column=1, padx=4, pady=3, sticky="ew")

        ctk.CTkLabel(self, text="Saída:").grid(row=r, column=2, padx=(8,4), pady=3, sticky="w")
        self._meas_out = ctk.CTkOptionMenu(self, values=self.meas_names, width=160)
        self._meas_out.set(self.pair.meas_out or self.meas_names[0])
        self._meas_out.grid(row=r, column=3, padx=4, pady=3, sticky="ew")

        ctk.CTkLabel(self, text="Label:").grid(row=r, column=4, padx=(8,4), pady=3, sticky="w")
        self._label_entry = ctk.CTkEntry(self, width=140)
        self._label_entry.insert(0, self.pair.label)
        self._label_entry.grid(row=r, column=5, padx=4, pady=3, sticky="ew")

        ctk.CTkButton(self, text="✕", width=30, fg_color="#a00000",
                      hover_color="#c00000",
                      command=self.on_remove).grid(
                          row=r, column=6, padx=(4,8), pady=3)
        r += 1

        # ---- Linha 1: tf_type / custom expr / unit
        ctk.CTkLabel(self, text="TF Type:").grid(row=r, column=0, padx=(8,4), pady=3, sticky="w")
        self._tf_type = ctk.CTkOptionMenu(
            self, values=TF_TYPES, width=160,
            command=self._on_tf_change,
        )
        self._tf_type.set(self.pair.tf_type)
        self._tf_type.grid(row=r, column=1, padx=4, pady=3, sticky="ew")

        self._custom_label = ctk.CTkLabel(self, text="Expressão:")
        self._custom_entry = ctk.CTkEntry(self, width=220,
                                           placeholder_text="ex: (out - 1.25) / inp * 100")
        if self.pair.tf_custom_expr:
            self._custom_entry.insert(0, self.pair.tf_custom_expr)

        ctk.CTkLabel(self, text="Unidade:").grid(row=r, column=4, padx=(8,4), pady=3, sticky="w")
        self._unit = ctk.CTkEntry(self, width=80)
        self._unit.insert(0, self.pair.unit)
        self._unit.grid(row=r, column=5, padx=4, pady=3, sticky="w")
        r += 1

        # ---- Linha custom (visível condicionalmente)
        self._custom_row = r
        self._on_tf_change(self.pair.tf_type)
        r += 1

        # ---- Linha 2: LSL / USL
        ctk.CTkLabel(self, text="LSL:").grid(row=r, column=0, padx=(8,4), pady=3, sticky="w")
        self._lsl = ctk.CTkEntry(self, width=120, placeholder_text="(opcional)")
        if self.pair.spec.lsl is not None:
            self._lsl.insert(0, str(self.pair.spec.lsl))
        self._lsl.grid(row=r, column=1, padx=4, pady=3, sticky="w")

        ctk.CTkLabel(self, text="USL:").grid(row=r, column=2, padx=(8,4), pady=3, sticky="w")
        self._usl = ctk.CTkEntry(self, width=120, placeholder_text="(opcional)")
        if self.pair.spec.usl is not None:
            self._usl.insert(0, str(self.pair.spec.usl))
        self._usl.grid(row=r, column=3, padx=4, pady=3, sticky="w")

    def _on_tf_change(self, value: str):
        if value == "custom":
            self._custom_label.grid(row=self._custom_row, column=0,
                                     padx=(8,4), pady=2, sticky="w")
            self._custom_entry.grid(row=self._custom_row, column=1,
                                     columnspan=5, padx=4, pady=2, sticky="ew")
        else:
            self._custom_label.grid_remove()
            self._custom_entry.grid_remove()

    def update_meas_names(self, names: List[str]):
        self.meas_names = names or ["—"]
        cur_in  = self._meas_in.get()
        cur_out = self._meas_out.get()
        self._meas_in.configure(values=self.meas_names)
        self._meas_out.configure(values=self.meas_names)
        if cur_in in self.meas_names:
            self._meas_in.set(cur_in)
        if cur_out in self.meas_names:
            self._meas_out.set(cur_out)

    def get_pair(self) -> PairConfig:
        def _float(s: str):
            s = s.strip()
            return float(s) if s else None

        lsl = _float(self._lsl.get())
        usl = _float(self._usl.get())

        tf = self._tf_type.get()
        expr = self._custom_entry.get().strip() if tf == "custom" else None

        return PairConfig(
            meas_in=self._meas_in.get(),
            meas_out=self._meas_out.get(),
            label=self._label_entry.get().strip(),
            tf_type=tf,
            tf_custom_expr=expr,
            spec=SpecLimits(lsl=lsl, usl=usl),
            unit=self._unit.get().strip(),
        )


# ---------------------------------------------------------------------------

class PanelPairs(ctk.CTkFrame):
    def __init__(self, master, config: ProjectConfig, **kwargs):
        super().__init__(master, **kwargs)
        self.config_obj  = config
        self._meas_names: List[str] = []
        self._cards:      List[_PairCard] = []
        self._build()

    def _build(self):
        self.grid_rowconfigure(1, weight=1)
        self.grid_columnconfigure(0, weight=1)

        top = ctk.CTkFrame(self, fg_color="transparent")
        top.grid(row=0, column=0, padx=8, pady=(8,4), sticky="ew")
        ctk.CTkButton(top, text="+ Adicionar par",
                      command=self._add_pair).pack(side="left", padx=(0,8))
        self._count_label = ctk.CTkLabel(top, text="0 par(es)")
        self._count_label.pack(side="left")

        # Frame scrollável para os cards
        self._scroll = ctk.CTkScrollableFrame(self)
        self._scroll.grid(row=1, column=0, padx=8, pady=(0,8), sticky="nsew")
        self._scroll.grid_columnconfigure(0, weight=1)

        # Carregar pares existentes
        for pair in self.config_obj.pairs:
            self._add_pair(pair)

    def _add_pair(self, pair: Optional[PairConfig] = None):
        if pair is None:
            pair = PairConfig(
                meas_in=self._meas_names[0] if self._meas_names else "",
                meas_out=self._meas_names[1] if len(self._meas_names) > 1
                         else (self._meas_names[0] if self._meas_names else ""),
                label=f"Par {len(self._cards)+1}",
            )

        idx = len(self._cards)

        def make_remove(i):
            def _remove():
                self._remove_pair(i)
            return _remove

        card = _PairCard(
            self._scroll, pair=pair,
            meas_names=self._meas_names or ["—"],
            on_remove=make_remove(idx),
        )
        card.grid(row=idx, column=0, padx=4, pady=4, sticky="ew")
        self._cards.append(card)
        self._update_count()

    def _remove_pair(self, idx: int):
        if idx < len(self._cards):
            self._cards[idx].destroy()
            self._cards.pop(idx)
            # Re-grid os restantes
            for i, card in enumerate(self._cards):
                card.grid(row=i, column=0, padx=4, pady=4, sticky="ew")
            self._update_count()

    def _update_count(self):
        n = len(self._cards)
        self._count_label.configure(text=f"{n} par(es)")

    def set_meas_names(self, names: List[str]):
        """Atualiza os dropdowns de todos os cards com as medidas detectadas."""
        self._meas_names = names
        for card in self._cards:
            card.update_meas_names(names)

    def get_pairs(self) -> List[PairConfig]:
        return [c.get_pair() for c in self._cards]

    def apply_to_config(self):
        self.config_obj.pairs = self.get_pairs()
