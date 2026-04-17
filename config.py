from dataclasses import dataclass, field
from typing import Optional, Literal
import os


@dataclass
class SpecLimits:
    lsl: Optional[float] = None   # Lower Spec Limit
    usl: Optional[float] = None   # Upper Spec Limit


@dataclass
class PairConfig:
    """Um par de medidas entrada/saída com TF e specs."""
    meas_in: str = ""
    meas_out: str = ""
    label: str = ""
    tf_type: Literal[
        "linear",
        "linear_fit",
        "origin_fit",
        "poly2_fit",
        "db",
        "error_abs",
        "error_rel_pct",
        "custom"
    ] = "linear_fit"
    tf_custom_expr: Optional[str] = None
    spec: SpecLimits = field(default_factory=SpecLimits)
    unit: str = ""


def _default_ltspice_path() -> str:
    user = os.getenv("USERNAME", os.getenv("USER", "user"))
    candidates = [
        # LTSpice 24+ (ADI, instalação por usuário — mais comum)
        rf"C:\Users\{user}\AppData\Local\Programs\ADI\LTspice\LTspice.exe",
        # LTSpice XVII legacy (Program Files)
        r"C:\Program Files\LTC\LTspiceXVII\XVIIx64.exe",
        # LTSpice ADI instalação global
        r"C:\Program Files\ADI\LTspice\LTspice.exe",
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    # Fallback: deixa o spicelib tentar detectar automaticamente
    try:
        from spicelib.simulators.ltspice_simulator import LTspice
        if LTspice.is_available() and LTspice.spice_exe:
            return LTspice.spice_exe[0]
    except Exception:
        pass
    return candidates[0]


@dataclass
class ProjectConfig:
    """Configuração completa de um projeto de simulação."""
    name: str = "Projeto"
    asc_path: str = ""
    ltspice_path: str = field(default_factory=_default_ltspice_path)
    output_dir: str = "./mc_results"
    total_runs: int = 1000
    n_cores: int = field(default_factory=lambda: max(1, (os.cpu_count() or 2) - 1))
    step_param_name: str = "run"
    pairs: list = field(default_factory=list)  # list[PairConfig]
    author: str = ""
    version: str = "1.0"


if __name__ == "__main__":
    cfg = ProjectConfig(name="Teste")
    cfg.pairs.append(PairConfig(
        meas_in="meas_vin",
        meas_out="meas_vout",
        label="Ganho de tensão",
        tf_type="linear_fit",
        spec=SpecLimits(lsl=-0.5, usl=0.5),
        unit="%"
    ))
    print(cfg)
