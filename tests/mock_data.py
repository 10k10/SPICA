"""
Dados sintéticos para desenvolvimento e testes sem LTSpice instalado.
"""
import numpy as np
from scipy.stats import skewnorm


def generate_mock_merged_data(
    n: int = 1000,
    distribution: str = "normal",
    seed: int = 42,
) -> dict:
    """
    Retorna um dict no formato do log_merger:
    {
        'measures': {name: array[N], ...},
        'n_runs': int,
        'n_failed': int,
        'failed_indices': list[int],
    }

    Parâmetros
    ----------
    n : número de runs
    distribution : 'normal' | 'skewnorm' | 'bimodal' | 'heavy_tails'
    seed : seed do RNG
    """
    rng = np.random.default_rng(seed)

    # Sinal de entrada: tensão 0.5 V a 2.0 V com ruído pequeno
    vin = rng.uniform(0.5, 2.0, n)

    # Parâmetros ideais do circuito: ganho = 2.0, offset = 0.1 V
    gain_ideal = 2.0
    offset_ideal = 0.1

    if distribution == "normal":
        # Variações gaussianas no ganho e offset
        gain_err = rng.normal(0, 0.005, n)      # 0.5% 1-sigma
        offset_err = rng.normal(0, 0.002, n)    # 2 mV 1-sigma
    elif distribution == "skewnorm":
        gain_err = skewnorm.rvs(a=4, loc=0, scale=0.005, size=n, random_state=seed)
        offset_err = rng.normal(0, 0.002, n)
    elif distribution == "bimodal":
        mask = rng.random(n) > 0.6
        gain_err = np.where(mask,
                            rng.normal(0.005, 0.002, n),
                            rng.normal(-0.005, 0.002, n))
        offset_err = rng.normal(0, 0.002, n)
    elif distribution == "heavy_tails":
        gain_err = rng.standard_t(df=3, size=n) * 0.003
        offset_err = rng.normal(0, 0.002, n)
    else:
        raise ValueError(f"distribution desconhecida: {distribution!r}")

    vout = (gain_ideal + gain_err) * vin + (offset_ideal + offset_err)

    # Introduz ~1% de falhas (NaN)
    n_failed = max(1, int(n * 0.01))
    failed_idx = rng.choice(n, size=n_failed, replace=False).tolist()
    vout[failed_idx] = np.nan

    return {
        "measures": {
            "meas_vin": vin,
            "meas_vout": vout,
        },
        "n_runs": n,
        "n_failed": n_failed,
        "failed_indices": failed_idx,
    }


def generate_mock_project_config():
    """Retorna um ProjectConfig populado para testes."""
    import sys
    import os
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
    from config import ProjectConfig, PairConfig, SpecLimits

    cfg = ProjectConfig(
        name="Mock Amplifier",
        asc_path="mock_circuit.asc",
        output_dir="./mc_results_mock",
        total_runs=1000,
        n_cores=4,
        author="Engenheiro Teste",
        version="1.0",
    )
    cfg.pairs = [
        PairConfig(
            meas_in="meas_vin",
            meas_out="meas_vout",
            label="Ganho de Tensão",
            tf_type="linear_fit",
            spec=SpecLimits(lsl=-1.0, usl=1.0),
            unit="%",
        )
    ]
    return cfg


if __name__ == "__main__":
    for dist in ("normal", "skewnorm", "bimodal", "heavy_tails"):
        d = generate_mock_merged_data(n=500, distribution=dist)
        vin = d["measures"]["meas_vin"]
        vout = d["measures"]["meas_vout"]
        valid = ~np.isnan(vout)
        print(
            f"{dist:12s}: n={d['n_runs']}, failed={d['n_failed']}, "
            f"vin=[{vin.min():.3f}, {vin.max():.3f}], "
            f"vout_mean={np.nanmean(vout):.4f}"
        )
