"""
Leitura e concatenação dos arquivos .log gerados pelo LTSpice.
Usa spicelib.LTSpiceLogReader para extrair as medidas.
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Dict, List, Optional

import numpy as np

logger = logging.getLogger(__name__)


def _read_single_log(log_path: str) -> Dict[str, np.ndarray]:
    """
    Lê um único .log e retorna {meas_name: array}.

    Tenta spicelib primeiro; se não disponível, usa parser simples.
    """
    try:
        from spicelib.log.ltsteps import LTSpiceLogReader
        reader = LTSpiceLogReader(log_path)
        meas_names = reader.get_measure_names()
        data: Dict[str, np.ndarray] = {}
        for name in meas_names:
            try:
                vals = reader[name]
                data[name] = np.asarray(vals, dtype=float)
            except Exception as exc:
                logger.warning("Erro ao ler medida '%s' em %s: %s", name, log_path, exc)
        return data
    except ImportError:
        logger.warning("spicelib não encontrado; usando parser fallback para %s", log_path)
        return _parse_log_fallback(log_path)


def _parse_log_fallback(log_path: str) -> Dict[str, np.ndarray]:
    """
    Parser mínimo para arquivos .log do LTSpice quando spicelib não está disponível.
    Extrai medidas da seção 'Measurement: <name>' / linhas de valores.
    """
    path = Path(log_path)
    try:
        content = path.read_text(encoding="latin-1", errors="replace")
    except FileNotFoundError:
        logger.error("Arquivo .log não encontrado: %s", log_path)
        return {}

    data: Dict[str, List[float]] = {}
    current_meas: Optional[str] = None
    value_pattern = False

    for line in content.splitlines():
        stripped = line.strip()

        if stripped.lower().startswith("measurement:"):
            current_meas = stripped.split(":", 1)[1].strip()
            data.setdefault(current_meas, [])
            value_pattern = False
            continue

        if current_meas and stripped.startswith("step"):
            value_pattern = True
            continue

        if current_meas and value_pattern:
            parts = stripped.split()
            if parts:
                try:
                    data[current_meas].append(float(parts[-1]))
                except ValueError:
                    if parts[-1].lower() in ("failed", "err", "nan"):
                        data[current_meas].append(float("nan"))

    return {k: np.asarray(v, dtype=float) for k, v in data.items()}


def merge_logs(
    log_paths: List[str],
    expected_total: Optional[int] = None,
) -> Dict:
    """
    Lê N arquivos .log e concatena os arrays de medidas.

    Parâmetros
    ----------
    log_paths      : lista de caminhos de .log (um por chunk)
    expected_total : número esperado de runs (para validação)

    Retorna
    -------
    {
        'measures':       {name: array[N]},
        'n_runs':         int,
        'n_failed':       int,
        'failed_indices': list[int],
    }

    Raises
    ------
    ValueError  — se os chunks tiverem conjuntos de medidas incompatíveis
    """
    if not log_paths:
        raise ValueError("Nenhum arquivo .log fornecido.")

    chunks: List[Dict[str, np.ndarray]] = []
    for lp in log_paths:
        try:
            chunk_data = _read_single_log(lp)
            if not chunk_data:
                logger.error("Nenhuma medida lida de %s — chunk ignorado.", lp)
                continue
            chunks.append(chunk_data)
        except Exception as exc:
            logger.error("Falha ao ler %s: %s — chunk ignorado.", lp, exc)

    if not chunks:
        raise ValueError("Todos os chunks falharam na leitura.")

    # Validar que todos têm as mesmas medidas
    ref_keys = set(chunks[0].keys())
    for i, chunk in enumerate(chunks[1:], start=1):
        diff = ref_keys.symmetric_difference(chunk.keys())
        if diff:
            raise ValueError(
                f"Chunk {i} tem medidas diferentes do chunk 0. "
                f"Divergência: {diff}"
            )

    # Concatenar
    merged: Dict[str, np.ndarray] = {}
    for name in ref_keys:
        arrays = [c[name] for c in chunks]
        merged[name] = np.concatenate(arrays)

    # Identificar falhas (NaN ou inf em qualquer medida)
    n_total = next(iter(merged.values())).shape[0]
    fail_mask = np.zeros(n_total, dtype=bool)
    for arr in merged.values():
        fail_mask |= ~np.isfinite(arr)

    failed_idx = np.where(fail_mask)[0].tolist()
    n_failed   = len(failed_idx)

    if n_failed > 0:
        logger.warning("%d runs com NaN/inf descartados (%.1f%%).",
                       n_failed, 100.0 * n_failed / n_total)

    # Validação de total
    if expected_total is not None and n_total != expected_total:
        logger.warning(
            "Total de runs lido (%d) difere do esperado (%d). "
            "Verifique se o .step param tem incremento '1' explicito "
            "(.step param run 0 N 1) e se total_runs esta correto na config.",
            n_total, expected_total,
        )

    # Detecção de chunks correlacionados (seeding identico entre chunks)
    warnings_list: List[str] = []
    if len(chunks) >= 2:
        first_name = next(iter(ref_keys))
        ref_arr = chunks[0][first_name]
        for i, chunk in enumerate(chunks[1:], start=1):
            cmp_arr = chunk[first_name]
            n_cmp = min(len(ref_arr), len(cmp_arr), 20)
            if n_cmp >= 3:
                # Chunks identicos = seeding correlacionado
                if np.allclose(ref_arr[:n_cmp], cmp_arr[:n_cmp], rtol=1e-6):
                    msg = (
                        f"AVISO: chunk 0 e chunk {i} tem valores identicos em "
                        f"'{first_name}'. O LTSpice pode estar usando o mesmo seed "
                        f"para chunks paralelos. Verifique se 'Random MC seed' esta "
                        f"habilitado em File > Preferences > SPICE no LTSpice."
                    )
                    logger.warning(msg)
                    warnings_list.append(msg)

    return {
        "measures":       merged,
        "n_runs":         n_total,
        "n_failed":       n_failed,
        "failed_indices": failed_idx,
        "warnings":       warnings_list,
    }


# ---------------------------------------------------------------------------
# Teste rápido
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import sys
    sys.path.insert(0, str(Path(__file__).parent / "tests"))
    from mock_data import generate_mock_merged_data

    # Simula o dict que viria do merge_logs
    data = generate_mock_merged_data(n=1000, distribution="normal")
    print("merge_logs simulado:")
    for k, v in data["measures"].items():
        finite = np.sum(np.isfinite(v))
        print(f"  {k}: shape={v.shape}, finite={finite}, "
              f"mean={np.nanmean(v):.4f}, std={np.nanstd(v):.4f}")
    print(f"  n_runs={data['n_runs']}, n_failed={data['n_failed']}")
    print(f"  failed_indices (primeiros 5): {data['failed_indices'][:5]}")
