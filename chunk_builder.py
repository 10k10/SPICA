"""
Divide o total de runs em chunks e gera N cópias do .asc com ranges distintos.

Lógica de seed do PRNG do LTSpice:
  O LTSpice usa PRNG sequencial. Chunks com ranges contíguos
  (1..1000, 1001..2000, …) consomem sequências distintas, garantindo
  amostras decorrelacionadas sem modificar o seed.

Formato do .asc:
  Diretivas SPICE ficam em linhas TEXT com prefixo '!':
    TEXT x y Left 2 !.step param run 1 1000
  Blocos multi-linha usam '\\n' literal dentro da string TEXT.
"""
from __future__ import annotations

import re
from pathlib import Path
from typing import List, Tuple

from config import ProjectConfig


# ---------------------------------------------------------------------------
# Ranges
# ---------------------------------------------------------------------------

def _compute_chunk_ranges(total: int, n_chunks: int) -> List[Tuple[int, int]]:
    """
    Divide [0, total-1] em n_chunks ranges contíguos (0-indexado).
    O remainder é distribuído nos primeiros chunks.
    Retorna lista de (start, end) — ambos inclusivos.

    Exemplo com 10 runs / 4 chunks:
      (0, 2), (3, 5), (6, 7), (8, 9)

    A indexação 0-based garante que o LTSpice use o valor do parâmetro
    `.step param run` como semente distinta para cada run, evitando
    correlação entre chunks mesmo quando o relógio do sistema é fonte
    de seed (o valor do param sobrescreve a semente interna do MC).
    """
    base, remainder = divmod(total, n_chunks)
    ranges: List[Tuple[int, int]] = []
    start = 0
    for i in range(n_chunks):
        size = base + (1 if i < remainder else 0)
        ranges.append((start, start + size - 1))
        start += size
    return ranges


# ---------------------------------------------------------------------------
# Localização da diretiva .step param no .asc
# ---------------------------------------------------------------------------

def _find_step_line_idx(lines: List[str], param_name: str) -> int:
    """
    Procura a linha que contém '.step param <param_name>' no .asc.

    Busca dentro de linhas TEXT (formato .asc) e também em linhas
    de netlist puras (.net / .cir).

    Retorna o índice da linha ou -1 se não encontrar.
    """
    # Pattern para dentro de TEXT lines do .asc:
    #   TEXT ... !... .step param <name> ...
    # A diretiva pode estar em qualquer posição após o '!'
    asc_text_re = re.compile(
        r"^TEXT\b.*?!\s*(?:.*?\\n)*?.*?\.step\s+param\s+"
        + re.escape(param_name) + r"\s+",
        re.IGNORECASE,
    )
    # Pattern para netlist puro (linha começa com .step)
    netlist_re = re.compile(
        r"^\s*\.step\s+param\s+" + re.escape(param_name) + r"\s+",
        re.IGNORECASE,
    )

    for idx, line in enumerate(lines):
        if asc_text_re.search(line) or netlist_re.match(line):
            return idx
    return -1


def _replace_step_range_in_line(line: str, param_name: str,
                                  start: int, end: int) -> str:
    """
    Substitui start/end na diretiva .step param dentro de uma linha do .asc.

    Sempre garante que o incremento seja 1 (inteiro por passo), pois sem ele
    o LTSpice usa um número interno de pontos em vez de um passo por inteiro.

    Exemplos:
      '.step param run 0 999'     -> '.step param run 0 249 1'
      '.step param run 0 999 1'   -> '.step param run 0 249 1'
      TEXT ... !.step param run 0 999  -> TEXT ... !.step param run 0 249 1
    """
    step_re = re.compile(
        r"(\.step\s+param\s+" + re.escape(param_name)
        + r"\s+)([\d.eE+\-]+)(\s+)([\d.eE+\-]+)((?:\s+[\d.eE+\-]+)?)(.*)",
        re.IGNORECASE,
    )

    def _replacer(m):
        # grupo 5 = incremento antigo (pode ser vazio); grupo 6 = resto da linha
        tail = m.group(6)  # tudo após o incremento (geralmente vazio em .asc)
        return f"{m.group(1)}{start}{m.group(3)}{end} 1{tail}"

    new_line = step_re.sub(_replacer, line)
    if new_line == line:
        return line
    if not new_line.endswith("\n"):
        new_line += "\n"
    return new_line


# ---------------------------------------------------------------------------
# Inserção do .step param quando não existe
# ---------------------------------------------------------------------------

def _insert_step_directive(lines: List[str], param_name: str,
                             start: int, end: int) -> List[str]:
    """
    Adiciona uma linha TEXT com .step param ao final do .asc.

    O incremento '1' é explícito para garantir que o LTSpice execute um
    passo por inteiro (sem ele, o simulador usa um número interno de pontos).
    """
    directive = f"TEXT 0 0 Left 2 !.step param {param_name} {start} {end} 1\n"
    new_lines = list(lines)
    new_lines.append(directive)
    return new_lines


# ---------------------------------------------------------------------------
# Função principal
# ---------------------------------------------------------------------------

def build_chunks(config: ProjectConfig) -> List[str]:
    """
    Gera as cópias do .asc com ranges ajustados para cada chunk.

    Comportamento:
    - Se '.step param <name> start end' existe → substitui start/end por chunk
    - Se não existe → insere automaticamente uma linha TEXT com o range do chunk

    Retorna lista de caminhos dos .asc gerados (um por core).
    """
    asc_path = Path(config.asc_path)
    if not asc_path.exists():
        raise FileNotFoundError(f"Arquivo .asc nao encontrado: {asc_path}")

    output_dir = Path(config.output_dir)
    chunks_dir = output_dir / "chunks"
    chunks_dir.mkdir(parents=True, exist_ok=True)

    # Lê preservando encoding original do LTSpice (Latin-1)
    try:
        content = asc_path.read_text(encoding="latin-1")
    except UnicodeDecodeError:
        content = asc_path.read_text(encoding="utf-8", errors="replace")

    lines = content.splitlines(keepends=True)

    step_idx = _find_step_line_idx(lines, config.step_param_name)

    n_chunks = config.n_cores
    ranges   = _compute_chunk_ranges(config.total_runs, n_chunks)

    if step_idx == -1:
        # Sempre insere automaticamente — não depende de flag nem do arquivo original
        lines = _insert_step_directive(
            lines, config.step_param_name, 0, config.total_runs - 1
        )
        step_idx = len(lines) - 1

    chunk_paths: List[str] = []
    for i, (start, end) in enumerate(ranges):
        new_lines = list(lines)
        new_lines[step_idx] = _replace_step_range_in_line(
            lines[step_idx], config.step_param_name, start, end
        )
        chunk_file = chunks_dir / f"chunk_{i:03d}.asc"
        chunk_file.write_text("".join(new_lines), encoding="latin-1")
        chunk_paths.append(str(chunk_file))

    return chunk_paths


def get_chunk_ranges(config: ProjectConfig) -> List[Tuple[int, int]]:
    """Retorna apenas os ranges sem gerar arquivos (útil para UI)."""
    return _compute_chunk_ranges(config.total_runs, config.n_cores)


# ---------------------------------------------------------------------------
# Teste rápido
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    # ── Ranges
    print("Ranges para 1000 runs / 4 cores:")
    for r in _compute_chunk_ranges(1000, 4):
        print(f"  {r[0]:5d} .. {r[1]:5d}  ({r[1]-r[0]+1} runs)")

    print("\nRanges para 10000 runs / 8 cores:")
    total_check = 0
    for r in _compute_chunk_ranges(10000, 8):
        sz = r[1] - r[0] + 1
        total_check += sz
        print(f"  {r[0]:6d} .. {r[1]:6d}  ({sz} runs)")
    print(f"  Total verificado: {total_check}")

    # ── Substituicao em linha TEXT do .asc
    samples = [
        # Linha TEXT padrao
        "TEXT -208 -80 Left 2 !.step param run 1 1000\n",
        # Multi-diretiva (\\n literal dentro do TEXT)
        "TEXT -208 -80 Left 2 !.tran 1m\\n.step param run 1 1000\\n.meas tran vout MAX V(out)\n",
        # Netlist puro
        ".step param run 1 1000\n",
    ]
    print("\nTeste de substituicao (.step param run 1 1000 -> 2501..5000):")
    for s in samples:
        result = _replace_step_range_in_line(s, "run", 2501, 5000)
        print(f"  IN:  {s.strip()}")
        print(f"  OUT: {result.strip()}")
        print()

    # ── Deteccao
    from ui.panel_config import _extract_directives_from_asc, _parse_step_param
    asc_content = """Version 4
SHEET 1 1000 1000
TEXT -208 -80 Left 2 !.tran 1m\\n.step param run 1 1000
TEXT -208 -120 Left 2 !.meas TRAN vout MAX V(out)
TEXT -208 -160 Left 2 !.meas TRAN vin MAX V(in)
"""
    directives = _extract_directives_from_asc(asc_content)
    print("Diretivas extraidas:")
    for d in directives:
        print(f"  {d}")

    step = _parse_step_param(directives)
    print(f"\nStep detectado: {step}")

    from ui.panel_config import _parse_meas_names
    meas = _parse_meas_names(directives)
    print(f"Meas detectadas: {meas}")
