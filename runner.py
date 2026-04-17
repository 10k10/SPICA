"""
Wrapper do SimRunner do spicelib com suporte a callback de progresso e cancelamento.

API correta do spicelib (v1.x):
  - LTspice.create_from(path)  → retorna a CLASSE configurada (não instância)
  - SimRunner(simulator=LTspiceClass, ...)
  - runner.run(asc_path)       → RunTask
  - runner.wait_completion()   → aguarda todos
  - task.get_results()         → (raw_file, log_file)
"""
from __future__ import annotations

import logging
import threading
import time
from pathlib import Path
from typing import Callable, List, Optional, Tuple

from config import ProjectConfig

logger = logging.getLogger(__name__)

DEFAULT_TIMEOUT = 300


def _resolve_ltspice_exe(config: ProjectConfig) -> str:
    """
    Retorna o caminho do executável LTSpice, tentando várias fontes.
    Levanta FileNotFoundError se não encontrar.
    """
    # 1. Caminho explícito na config
    if config.ltspice_path and Path(config.ltspice_path).exists():
        return config.ltspice_path

    # 2. Detecção automática via spicelib (se disponível)
    try:
        from spicelib.simulators.ltspice_simulator import LTspice
        if LTspice.is_available():
            exe = LTspice.spice_exe
            # spice_exe pode ser lista ou string
            if isinstance(exe, (list, tuple)):
                exe = exe[0]
            if exe and Path(exe).exists():
                return str(exe)
    except Exception:
        pass

    # 3. Caminhos padrão
    from config import _default_ltspice_path
    default = _default_ltspice_path()
    if Path(default).exists():
        return default

    raise FileNotFoundError(
        f"Executável LTSpice não encontrado.\n"
        f"Configure o caminho em Config > 'Caminho do LTSpice'.\n"
        f"Último tentado: {config.ltspice_path or default}"
    )


def run_simulations(
    chunk_paths: List[str],
    config: ProjectConfig,
    progress_callback: Callable[[int, int, str], None],
    cancel_event: threading.Event,
    timeout: int = DEFAULT_TIMEOUT,
) -> List[str]:
    """
    Dispara N simulações LTSpice usando subprocess com stagger de seed.

    Usa subprocess direto (não o runner do spicelib) para garantir que cada
    processo LTSpice seja iniciado com um gap de tempo suficiente para que o
    seed baseado em relógio seja diferente entre chunks.
    """
    return _run_with_subprocess(
        chunk_paths, config, progress_callback, cancel_event, timeout
    )


# ---------------------------------------------------------------------------
# Backend spicelib
# ---------------------------------------------------------------------------

def _run_with_spicelib(
    chunk_paths: List[str],
    config: ProjectConfig,
    progress_callback: Callable,
    cancel_event: threading.Event,
    timeout: int,
    SimRunner,
    LTspice,
) -> List[str]:
    """
    Usa SimRunner + LTspice.create_from() conforme a API real do spicelib.
    """
    output_dir = Path(config.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    ltspice_path = config.ltspice_path

    # Cria a classe do simulador configurada com o executável informado.
    # create_from() retorna a própria classe (classmethod), não uma instância.
    if LTspice.is_available() and not ltspice_path:
        SimClass = LTspice  # usa o detectado automaticamente
    else:
        try:
            SimClass = LTspice.create_from(ltspice_path)
        except FileNotFoundError as exc:
            raise FileNotFoundError(
                f"LTSpice nao encontrado em: {ltspice_path}\n"
                "Configure o caminho correto no painel Config."
            ) from exc

    runner = SimRunner(
        simulator=SimClass,
        output_folder=str(output_dir),
        parallel_sims=config.n_cores,
        timeout=timeout,
    )

    n_total   = len(chunk_paths)
    tasks     = []          # lista de (idx, asc_path, RunTask)
    log_paths = []
    errors    = []

    progress_callback(0, n_total, "Enviando chunks para o SimRunner...")

    # Enfileira as simulações com atraso entre cada uma para garantir
    # seeds diferentes no MC do LTSpice (LTSpice 24 usa o relógio do sistema
    # como semente; processos iniciados no mesmo milissegundo ficam com o
    # mesmo seed e produzem resultados idênticos).
    SEED_STAGGER_S = 2.0   # segundos entre lançamentos de chunks

    for idx, asc_path in enumerate(chunk_paths):
        if cancel_event.is_set():
            progress_callback(idx, n_total, "Cancelado antes de iniciar.")
            return log_paths

        if idx > 0:
            # Aguarda antes de lançar o próximo chunk para garantir seed distinto
            elapsed = 0.0
            while elapsed < SEED_STAGGER_S:
                if cancel_event.is_set():
                    progress_callback(idx, n_total, "Cancelado antes de iniciar.")
                    return log_paths
                time.sleep(0.2)
                elapsed += 0.2

        try:
            task = runner.run(asc_path)
            if task is not None:
                tasks.append((idx, asc_path, task))
                progress_callback(idx, n_total,
                                   f"Chunk {idx+1}/{n_total}: enfileirado.")
            else:
                msg = f"Chunk {idx+1}: runner.run() retornou None."
                logger.error(msg)
                errors.append(msg)
                progress_callback(idx + 1, n_total,
                                   f"Chunk {idx+1}/{n_total}: erro ao enfileirar.")
        except Exception as exc:
            msg = f"Chunk {idx+1} falhou ao enfileirar: {exc}"
            logger.error(msg, exc_info=True)
            errors.append(msg)
            progress_callback(idx + 1, n_total,
                               f"Chunk {idx+1}/{n_total}: ERRO — {exc}")

    if not tasks:
        raise RuntimeError("Nenhuma simulacao foi enfileirada com sucesso.")

    # Aguarda conclusão com polling para suportar cancelamento
    completed = set()
    progress_callback(0, n_total, "Simulacoes em andamento...")

    while len(completed) < len(tasks):
        if cancel_event.is_set():
            runner.kill_all_ltspice()
            progress_callback(len(completed), n_total, "Cancelado pelo usuario.")
            break

        for idx, asc_path, task in tasks:
            if idx in completed:
                continue
            if task.is_alive():
                continue

            # Tarefa concluída
            completed.add(idx)
            try:
                result = task.get_results()
            except Exception as exc:
                msg = f"Chunk {idx+1}: get_results() falhou: {exc}"
                logger.error(msg)
                errors.append(msg)
                progress_callback(len(completed), n_total,
                                   f"Chunk {idx+1}/{n_total}: ERRO — {exc}")
                continue

            if result is None:
                msg = f"Chunk {idx+1}: simulacao falhou (resultado None)."
                logger.error(msg)
                errors.append(msg)
                progress_callback(len(completed), n_total,
                                   f"Chunk {idx+1}/{n_total}: FALHOU")
                continue

            raw_file, log_file = result
            if log_file and Path(log_file).exists():
                log_paths.append(str(log_file))
                progress_callback(len(completed), n_total,
                                   f"Chunk {idx+1}/{n_total}: concluido OK")
            else:
                # Tenta inferir .log do .asc ou do raw_file
                fallback = _find_log_fallback(asc_path, raw_file)
                if fallback:
                    log_paths.append(fallback)
                    progress_callback(len(completed), n_total,
                                       f"Chunk {idx+1}/{n_total}: OK (log fallback)")
                else:
                    msg = f"Chunk {idx+1}: .log nao encontrado."
                    errors.append(msg)
                    progress_callback(len(completed), n_total,
                                       f"Chunk {idx+1}/{n_total}: sem .log")

        if len(completed) < len(tasks):
            time.sleep(0.5)

    if errors:
        logger.warning("%d chunk(s) com erro:\n%s", len(errors), "\n".join(errors))

    progress_callback(n_total, n_total,
                      f"Concluido: {len(log_paths)}/{n_total} chunks com sucesso.")
    return log_paths


def _find_log_fallback(asc_path: str, raw_file) -> Optional[str]:
    """Tenta localizar o .log a partir do .asc ou do raw_file."""
    candidates = []
    if asc_path:
        candidates.append(Path(asc_path).with_suffix(".log"))
    if raw_file:
        candidates.append(Path(str(raw_file)).with_suffix(".log"))
    for c in candidates:
        if c.exists():
            return str(c)
    return None


# ---------------------------------------------------------------------------
# Backend subprocess (fallback)
# ---------------------------------------------------------------------------

def _run_with_subprocess(
    chunk_paths: List[str],
    config: ProjectConfig,
    progress_callback: Callable,
    cancel_event: threading.Event,
    timeout: int,
) -> List[str]:
    import subprocess

    ltspice_exe = _resolve_ltspice_exe(config)

    output_dir = Path(config.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    n_total   = len(chunk_paths)
    log_paths: List[str] = []
    active:    List[dict] = []
    queued     = list(enumerate(chunk_paths))
    completed  = 0
    last_launch_time: float = 0.0   # monotonic timestamp do último lançamento

    SEED_STAGGER_S = 2.0   # segundos mínimos entre lançamentos consecutivos

    progress_callback(0, n_total, "Iniciando simulacoes (subprocess)...")

    while queued or active:
        if cancel_event.is_set():
            for entry in active:
                try:
                    entry["proc"].kill()
                except Exception:
                    pass
            progress_callback(completed, n_total, "Cancelado pelo usuario.")
            break

        # Lança novos processos respeitando o stagger de seed
        while queued and len(active) < config.n_cores:
            now = time.monotonic()
            since_last = now - last_launch_time
            if last_launch_time > 0 and since_last < SEED_STAGGER_S:
                # Ainda não passou tempo suficiente; aguarda fora deste loop
                break

            idx, asc_path = queued.pop(0)
            try:
                proc = subprocess.Popen(
                    [ltspice_exe, "-b", "-Run", "-ascii", asc_path],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
                last_launch_time = time.monotonic()
                active.append({
                    "proc": proc, "asc_path": asc_path,
                    "idx": idx, "start_time": time.monotonic(),
                })
                progress_callback(completed, n_total,
                                   f"Chunk {idx+1}/{n_total}: rodando (PID {proc.pid})...")
            except Exception as exc:
                logger.error("Falha ao lancar chunk %d: %s", idx + 1, exc)
                progress_callback(completed, n_total,
                                   f"Chunk {idx+1}/{n_total}: ERRO ao lancar — {exc}")
                completed += 1

        still_running = []
        for entry in active:
            proc    = entry["proc"]
            elapsed = time.monotonic() - entry["start_time"]
            ret     = proc.poll()

            if ret is None:
                if elapsed > timeout:
                    proc.kill()
                    logger.error("Chunk %d timeout", entry["idx"] + 1)
                    progress_callback(completed, n_total,
                                       f"Chunk {entry['idx']+1}/{n_total}: TIMEOUT")
                    completed += 1
                else:
                    still_running.append(entry)

            elif ret == 0:
                # Procura o .log na mesma pasta do .asc
                asc_p = Path(entry["asc_path"])
                log_p = asc_p.with_suffix(".log")
                if not log_p.exists():
                    log_p = output_dir / (asc_p.stem + ".log")
                if log_p.exists():
                    log_paths.append(str(log_p))
                    completed += 1
                    progress_callback(completed, n_total,
                                       f"Chunk {entry['idx']+1}/{n_total}: concluido OK")
                else:
                    logger.error("Chunk %d: .log nao encontrado", entry["idx"] + 1)
                    completed += 1
                    progress_callback(completed, n_total,
                                       f"Chunk {entry['idx']+1}/{n_total}: sem .log")
            else:
                logger.error("Chunk %d retornou codigo %d", entry["idx"] + 1, ret)
                completed += 1
                progress_callback(completed, n_total,
                                   f"Chunk {entry['idx']+1}/{n_total}: ERRO (codigo {ret})")

        active = still_running
        if active or queued:
            time.sleep(0.5)

    progress_callback(n_total, n_total,
                      f"Concluido: {len(log_paths)}/{n_total} chunks com sucesso.")
    return log_paths


# ---------------------------------------------------------------------------
# Teste rápido
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    from pathlib import Path
    try:
        from spicelib.simulators.ltspice_simulator import LTspice
        print(f"LTspice disponivel: {LTspice.is_available()}")
        print(f"Executavel: {LTspice.spice_exe}")

        from config import _default_ltspice_path
        path = _default_ltspice_path()
        print(f"Caminho detectado pelo config: {path}")
        print(f"Existe: {Path(path).exists()}")

        if Path(path).exists():
            SimClass = LTspice.create_from(path)
            print(f"create_from OK: {SimClass.spice_exe}")
    except Exception as exc:
        print(f"Erro: {exc}")
