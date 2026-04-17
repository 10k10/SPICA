"""
Painel do job scheduler: progresso em tempo real das simulações.
"""
from __future__ import annotations

import queue
import threading
from typing import Callable, List, Optional

import customtkinter as ctk

from config import ProjectConfig
from chunk_builder import build_chunks, get_chunk_ranges


class _ChunkRow(ctk.CTkFrame):
    """Uma linha de status para um chunk individual."""

    def __init__(self, master, idx: int, start: int, end: int, **kwargs):
        super().__init__(master, fg_color="transparent", **kwargs)
        self.grid_columnconfigure(2, weight=1)

        self._label = ctk.CTkLabel(
            self,
            text=f"Chunk {idx+1:02d}: runs {start}..{end}",
            width=200, anchor="w",
        )
        self._label.grid(row=0, column=0, padx=(4,8), sticky="w")

        self._status = ctk.CTkLabel(self, text="Aguardando", width=110, anchor="w")
        self._status.grid(row=0, column=1, padx=(0,8), sticky="w")

        self._bar = ctk.CTkProgressBar(self, width=180, height=10)
        self._bar.set(0)
        self._bar.grid(row=0, column=2, padx=(0,4), sticky="ew")

    def set_running(self):
        self._status.configure(text="Rodando…", text_color="#f0a020")
        self._bar.set(0.5)

    def set_done(self):
        self._status.configure(text="Concluído ✓", text_color="#2d7a2d")
        self._bar.set(1.0)

    def set_error(self):
        self._status.configure(text="Erro ✗", text_color="#a00000")
        self._bar.set(0)

    def set_cancelled(self):
        self._status.configure(text="Cancelado", text_color="#888")
        self._bar.set(0)


class PanelJobs(ctk.CTkFrame):
    def __init__(self, master, config: ProjectConfig,
                 on_simulation_done: Optional[Callable[[list], None]] = None,
                 **kwargs):
        super().__init__(master, **kwargs)
        self.config_obj = config
        self.on_simulation_done = on_simulation_done

        self._cancel_event = threading.Event()
        self._progress_queue: queue.Queue = queue.Queue()
        self._chunk_rows: List[_ChunkRow] = []
        self._sim_thread: Optional[threading.Thread] = None

        self._build()

    # ------------------------------------------------------------------
    def _build(self):
        self.grid_rowconfigure(3, weight=1)
        self.grid_columnconfigure(0, weight=1)

        # ── Linha superior de progresso global
        top = ctk.CTkFrame(self, fg_color="transparent")
        top.grid(row=0, column=0, padx=12, pady=(10,4), sticky="ew")
        top.grid_columnconfigure(1, weight=1)

        self._progress_label = ctk.CTkLabel(top, text="Pronto.", anchor="w")
        self._progress_label.grid(row=0, column=0, padx=(0,10), sticky="w")

        self._global_bar = ctk.CTkProgressBar(top, height=14)
        self._global_bar.set(0)
        self._global_bar.grid(row=0, column=1, sticky="ew")

        # ── Botões
        btn_frame = ctk.CTkFrame(self, fg_color="transparent")
        btn_frame.grid(row=1, column=0, padx=12, pady=(4,6), sticky="w")

        self._btn_run = ctk.CTkButton(
            btn_frame, text="▶ Iniciar Simulação",
            command=self._start_simulation,
        )
        self._btn_run.pack(side="left", padx=(0,8))

        self._btn_cancel = ctk.CTkButton(
            btn_frame, text="⬛ Cancelar",
            fg_color="#a00000", hover_color="#c00000",
            command=self._cancel_simulation, state="disabled",
        )
        self._btn_cancel.pack(side="left")

        # ── Lista de chunks (scrollável)
        self._scroll = ctk.CTkScrollableFrame(self, label_text="Status por chunk")
        self._scroll.grid(row=2, column=0, padx=12, pady=(0,4), sticky="nsew")
        self._scroll.grid_columnconfigure(0, weight=1)
        self.grid_rowconfigure(2, weight=1)

        # ── Log de texto
        log_label = ctk.CTkLabel(self, text="Log:", anchor="w")
        log_label.grid(row=3, column=0, padx=12, pady=(4,0), sticky="w")

        self._log = ctk.CTkTextbox(self, height=130, font=("Courier New", 10),
                                    state="disabled")
        self._log.grid(row=4, column=0, padx=12, pady=(2,10), sticky="ew")

    # ------------------------------------------------------------------
    def _rebuild_chunk_rows(self):
        for w in self._chunk_rows:
            w.destroy()
        self._chunk_rows.clear()

        ranges = get_chunk_ranges(self.config_obj)
        for i, (s, e) in enumerate(ranges):
            row = _ChunkRow(self._scroll, idx=i, start=s, end=e)
            row.grid(row=i, column=0, padx=4, pady=2, sticky="ew")
            self._chunk_rows.append(row)

    def _log_append(self, msg: str):
        self._log.configure(state="normal")
        self._log.insert("end", msg + "\n")
        self._log.see("end")
        self._log.configure(state="disabled")

    # ------------------------------------------------------------------
    def _start_simulation(self):
        from chunk_builder import build_chunks
        from runner import run_simulations
        from log_merger import merge_logs

        cfg = self.config_obj

        import os, glob, shutil
        from pathlib import Path
        from tkinter import messagebox

        # ── Validações básicas
        if not cfg.asc_path or not Path(cfg.asc_path).exists():
            messagebox.showerror("Erro", "Arquivo .asc nao encontrado.\nConfigure-o no painel Config.")
            return
        if not cfg.pairs:
            messagebox.showerror("Erro", "Nenhum par definido.\nAdicione pares no painel Pairs.")
            return

        # ── Aviso de total_runs pequeno
        MIN_RUNS = 100
        if cfg.total_runs < MIN_RUNS:
            ok = messagebox.askyesno(
                "Aviso — runs insuficientes",
                f"Total de runs configurado: {cfg.total_runs}\n\n"
                f"Para analise estatistica confiavel recomenda-se no minimo {MIN_RUNS} runs "
                f"(idealmente 500+).\n\n"
                f"Com poucos runs os graficos e metricas ficam imprecisos.\n\n"
                f"Continuar mesmo assim?"
            )
            if not ok:
                return

        # ── Limpeza de runs anteriores na pasta de saida
        out_dir = Path(cfg.output_dir)
        chunks_dir = out_dir / "chunks"

        # Remove logs antigos do spicelib (chunk_*.log, chunk_*_N.log)
        old_logs = list(out_dir.glob("chunk_*.log"))
        old_raws = list(out_dir.glob("chunk_*.raw")) + list(out_dir.glob("chunk_*.op.raw"))
        for f in old_logs + old_raws:
            try:
                f.unlink()
            except Exception:
                pass

        # Remove chunks antigos para forcar rebuild com nova config
        if chunks_dir.exists():
            try:
                shutil.rmtree(chunks_dir)
            except Exception:
                pass

        self._cancel_event.clear()
        self._rebuild_chunk_rows()
        self._btn_run.configure(state="disabled")
        self._btn_cancel.configure(state="normal")
        self._global_bar.set(0)
        self._log.configure(state="normal")
        self._log.delete("1.0", "end")
        self._log.configure(state="disabled")

        def _worker():
            try:
                self._log_queue_put(
                    f"Iniciando: {cfg.total_runs} runs / "
                    f"{cfg.n_cores} chunks / "
                    f"{cfg.total_runs // cfg.n_cores} runs por chunk"
                )
                self._log_queue_put("Construindo chunks do .asc...")
                chunk_paths = build_chunks(cfg)
                self._log_queue_put(f"{len(chunk_paths)} chunks criados.")

                def cb(idx, total, msg):
                    self._progress_queue.put(("progress", idx, total, msg))

                log_paths = run_simulations(
                    chunk_paths, cfg, cb, self._cancel_event
                )

                if not log_paths:
                    self._progress_queue.put(("error", "Nenhum .log gerado."))
                    return

                self._log_queue_put(f"Lendo {len(log_paths)} arquivo(s) .log...")
                merged = merge_logs(log_paths, expected_total=cfg.total_runs)

                actual = merged['n_runs']
                expected = cfg.total_runs
                self._log_queue_put(
                    f"Leitura concluida: {actual} runs, {merged['n_failed']} falhas."
                )

                # Aviso runs insuficientes
                if actual < expected:
                    self._log_queue_put(
                        f"AVISO: esperado {expected} runs, obtido {actual}. "
                        f"Verifique se o .step param usa incremento '1' explicito "
                        f"e se total_runs esta correto na config."
                    )

                # Aviso de chunks correlacionados
                for w in merged.get("warnings", []):
                    self._log_queue_put(f"AVISO SEED: {w}")

                self._progress_queue.put(("done", merged))

            except Exception as exc:
                import traceback
                self._progress_queue.put(("error", f"{exc}\n{traceback.format_exc()}"))

        def _log_queue_put(msg):
            self._progress_queue.put(("log", msg))

        # Injeta helper no worker via closure
        self._log_queue_put = _log_queue_put

        self._sim_thread = threading.Thread(target=_worker, daemon=True)
        self._sim_thread.start()
        self._poll_progress()

    def _poll_progress(self):
        try:
            while True:
                item = self._progress_queue.get_nowait()
                kind = item[0]

                if kind == "progress":
                    _, idx, total, msg = item
                    frac = idx / total if total > 0 else 0
                    self._global_bar.set(frac)
                    self._progress_label.configure(
                        text=f"{idx}/{total} chunks"
                    )
                    self._log_append(msg)
                    # Atualiza row individual
                    if "concluído" in msg.lower() and idx > 0:
                        r_idx = idx - 1
                        if r_idx < len(self._chunk_rows):
                            self._chunk_rows[r_idx].set_done()
                    elif "rodando" in msg.lower():
                        # Tenta inferir qual chunk
                        import re
                        m = re.search(r"Chunk (\d+)/", msg)
                        if m:
                            r_idx = int(m.group(1)) - 1
                            if r_idx < len(self._chunk_rows):
                                self._chunk_rows[r_idx].set_running()
                    elif "erro" in msg.lower() or "error" in msg.lower():
                        import re
                        m = re.search(r"Chunk (\d+)/", msg)
                        if m:
                            r_idx = int(m.group(1)) - 1
                            if r_idx < len(self._chunk_rows):
                                self._chunk_rows[r_idx].set_error()

                elif kind == "log":
                    self._log_append(item[1])

                elif kind == "done":
                    merged = item[1]
                    self._global_bar.set(1.0)
                    self._progress_label.configure(text="Simulação concluída ✓")
                    self._log_append("=== Simulação concluída com sucesso. ===")
                    self._btn_run.configure(state="normal")
                    self._btn_cancel.configure(state="disabled")
                    if self.on_simulation_done:
                        self.on_simulation_done(merged)
                    return

                elif kind == "error":
                    msg = item[1]
                    self._progress_label.configure(text="Erro na simulação ✗")
                    self._log_append(f"ERRO: {msg}")
                    self._btn_run.configure(state="normal")
                    self._btn_cancel.configure(state="disabled")
                    return

        except queue.Empty:
            pass

        # Reagendar polling se a thread ainda estiver viva
        if self._sim_thread and self._sim_thread.is_alive():
            self.after(500, self._poll_progress)
        else:
            self._btn_run.configure(state="normal")
            self._btn_cancel.configure(state="disabled")

    def _cancel_simulation(self):
        self._cancel_event.set()
        self._progress_label.configure(text="Cancelando…")
        self._btn_cancel.configure(state="disabled")
        for row in self._chunk_rows:
            row.set_cancelled()
