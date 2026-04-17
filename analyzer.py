"""
Análise estatística completa de resultados Monte Carlo.
"""
from __future__ import annotations

import warnings
import numpy as np
from scipy import stats, optimize
from sklearn.mixture import GaussianMixture
from typing import Optional

from config import PairConfig, SpecLimits


# ---------------------------------------------------------------------------
# Transfer Function
# ---------------------------------------------------------------------------

def compute_tf(
    arr_in: np.ndarray,
    arr_out: np.ndarray,
    tf_type: str,
    tf_custom_expr: Optional[str] = None,
) -> dict:
    """
    Calcula a transfer function para cada run.

    Retorna
    -------
    {
        'values': array[N],
        'fit_coeffs': dict,      # coeficientes do fit (quando aplicável)
        'fit_fn': callable,      # x → y_ideal (quando aplicável)
    }
    """
    mask = np.isfinite(arr_in) & np.isfinite(arr_out)
    xi = arr_in[mask]
    xo = arr_out[mask]

    fit_coeffs: dict = {}
    fit_fn = None
    values = np.full(len(arr_in), np.nan)

    # Detecta entrada constante antes de qualquer fit que exija variação em x.
    # Threshold relativo: std < 1e-9 × |mean| (ou 1e-12 absoluto).
    _x_std = float(np.std(xi)) if len(xi) > 1 else 0.0
    _x_mean_abs = float(abs(np.mean(xi))) if len(xi) > 0 else 0.0
    _x_is_const = _x_std < max(1e-12, 1e-9 * _x_mean_abs)

    if tf_type == "linear":
        with np.errstate(divide="ignore", invalid="ignore"):
            values[mask] = xo / xi

    elif tf_type == "linear_fit":
        if _x_is_const:
            # Entrada constante: regress sem sentido → degrada para error_abs
            # e avisa via fit_coeffs['degenerate']
            mean_xi = float(np.mean(xi))
            fit_fn = lambda x, _m=mean_xi: np.full_like(np.asarray(x, float), _m)  # noqa: E731
            residuals = xo - xo.mean()   # variação ao redor da média da saída
            rmse = float(np.sqrt(np.mean(residuals ** 2)))
            fit_coeffs = {
                "a": float(np.mean(xo)) / mean_xi if mean_xi != 0 else np.nan,
                "b": 0.0, "R2": np.nan, "RMSE": rmse,
                "degenerate": True,
                "degenerate_note": (
                    f"Entrada constante (={mean_xi:.6g}). "
                    "Regressao indisponivel - usando variacao da saida (equiv. error_abs). "
                    "Considere usar tf_type='error_abs' ou 'error_rel_pct'."
                ),
            }
            values[mask] = residuals
        else:
            slope, intercept, r, p, se = stats.linregress(xi, xo)
            fit_fn = lambda x, _s=slope, _i=intercept: _s * x + _i  # noqa: E731
            residuals = xo - fit_fn(xi)
            ss_res = np.sum(residuals ** 2)
            ss_tot = np.sum((xo - xo.mean()) ** 2)
            r2 = 1.0 - ss_res / ss_tot if ss_tot > 0 else np.nan
            rmse = float(np.sqrt(np.mean(residuals ** 2)))
            fit_coeffs = {"a": float(slope), "b": float(intercept),
                          "R2": float(r2), "RMSE": rmse}
            values[mask] = residuals

    elif tf_type == "origin_fit":
        if _x_is_const:
            mean_xi = float(np.mean(xi))
            a_est = float(np.mean(xo)) / mean_xi if mean_xi != 0 else np.nan
            residuals = xo - xo.mean()
            rmse = float(np.sqrt(np.mean(residuals ** 2)))
            fit_coeffs = {
                "a": a_est, "R2": np.nan, "RMSE": rmse,
                "degenerate": True,
                "degenerate_note": (
                    f"Entrada constante (={mean_xi:.6g}). "
                    "Fit pela origem indisponível — usando variação da saída."
                ),
            }
            values[mask] = residuals
        else:
            def _origin(x, a):
                return a * x
            popt, _ = optimize.curve_fit(_origin, xi, xo)
            a = popt[0]
            fit_fn = lambda x, _a=a: _a * x  # noqa: E731
            residuals = xo - fit_fn(xi)
            rmse = float(np.sqrt(np.mean(residuals ** 2)))
            ss_res = np.sum(residuals ** 2)
            ss_tot = np.sum((xo - xo.mean()) ** 2)
            r2 = 1.0 - ss_res / ss_tot if ss_tot > 0 else np.nan
            fit_coeffs = {"a": float(a), "R2": float(r2), "RMSE": rmse}
            values[mask] = residuals

    elif tf_type == "poly2_fit":
        if _x_is_const:
            mean_xi = float(np.mean(xi))
            residuals = xo - xo.mean()
            rmse = float(np.sqrt(np.mean(residuals ** 2)))
            fit_coeffs = {
                "a": 0.0, "b": 0.0, "c": float(np.mean(xo)),
                "R2": np.nan, "RMSE": rmse,
                "degenerate": True,
                "degenerate_note": (
                    f"Entrada constante (={mean_xi:.6g}). "
                    "Fit polinomial indisponível — usando variação da saída."
                ),
            }
            values[mask] = residuals
        else:
            coeffs = np.polyfit(xi, xo, 2)
            fit_fn = np.poly1d(coeffs)
            residuals = xo - fit_fn(xi)
            rmse = float(np.sqrt(np.mean(residuals ** 2)))
            ss_res = np.sum(residuals ** 2)
            ss_tot = np.sum((xo - xo.mean()) ** 2)
            r2 = 1.0 - ss_res / ss_tot if ss_tot > 0 else np.nan
            fit_coeffs = {"a": float(coeffs[0]), "b": float(coeffs[1]),
                          "c": float(coeffs[2]), "R2": float(r2), "RMSE": rmse}
            values[mask] = residuals

    elif tf_type == "db":
        with np.errstate(divide="ignore", invalid="ignore"):
            values[mask] = 20.0 * np.log10(np.abs(xo / xi))

    elif tf_type == "error_abs":
        values[mask] = xo - xi

    elif tf_type == "error_rel_pct":
        with np.errstate(divide="ignore", invalid="ignore"):
            values[mask] = (xo - xi) / xi * 100.0

    elif tf_type == "custom":
        if not tf_custom_expr:
            raise ValueError("tf_custom_expr é obrigatório quando tf_type='custom'")
        safe_ns = {"np": np, "__builtins__": {}}
        inp = xi
        out = xo
        result = eval(tf_custom_expr, safe_ns, {"inp": inp, "out": out})  # noqa: S307
        values[mask] = np.asarray(result, dtype=float)

    else:
        raise ValueError(f"tf_type desconhecido: {tf_type!r}")

    return {"values": values, "fit_coeffs": fit_coeffs, "fit_fn": fit_fn}


# ---------------------------------------------------------------------------
# Normalidade
# ---------------------------------------------------------------------------

def test_normality(arr: np.ndarray) -> dict:
    """Roda Shapiro-Wilk, KS e Anderson-Darling."""
    data = arr[np.isfinite(arr)]
    n = len(data)
    note = ""

    # Shapiro-Wilk (subsample se N > 5000)
    sw_data = data
    if n > 5000:
        rng = np.random.default_rng(0)
        sw_data = rng.choice(data, size=5000, replace=False)
        note = f"Shapiro-Wilk subamostrado para 5000 de {n} observações."
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        sw_stat, sw_p = stats.shapiro(sw_data)

    # Kolmogorov-Smirnov — wrap em catch_warnings para suprimir
    # "invalid value encountered in divide" quando std≈0 (scale=0 na CDF normal)
    mean, std = data.mean(), data.std(ddof=1)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        ks_stat, ks_p = stats.kstest(data, "norm", args=(mean, std))

    # Anderson-Darling
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        ad_result = stats.anderson(data, dist="norm")
    ad_stat = float(ad_result.statistic)
    # critical_values[2] = nível de significância 5% (compatível com versões antigas)
    ad_crit_5 = float(ad_result.critical_values[2])
    ad_passed = ad_stat < ad_crit_5

    shapiro_ok = bool(sw_p > 0.05)
    ks_ok = bool(ks_p > 0.05)
    n_pass = sum([shapiro_ok, ks_ok, ad_passed])
    is_normal = n_pass >= 2

    verdict = "NORMAL" if shapiro_ok else "NÃO NORMAL"
    conclusion = (
        f"Shapiro-Wilk\nW = {sw_stat:.4f}\np = {sw_p:.3e}\n→ {verdict}"
    )

    return {
        "shapiro":   {"stat": float(sw_stat), "p": float(sw_p),  "passed": shapiro_ok},
        "ks":        {"stat": float(ks_stat), "p": float(ks_p),  "passed": ks_ok},
        "anderson":  {"stat": ad_stat, "critical_5pct": ad_crit_5, "passed": ad_passed},
        "is_normal": is_normal,
        "conclusion": conclusion,
        "note": note,
    }


# ---------------------------------------------------------------------------
# Distribuição alternativa
# ---------------------------------------------------------------------------

def fit_alternative_distribution(arr: np.ndarray, normality_result: dict) -> dict:
    """Tenta fits alternativos quando a distribuição não é normal."""
    data = arr[np.isfinite(arr)]

    # Se dados quase-constantes, nenhum fit faz sentido
    _std = float(np.std(data, ddof=1)) if len(data) > 1 else 0.0
    _mean_abs = float(abs(np.mean(data))) if len(data) > 0 else 0.0
    if _std < max(1e-15, 1e-9 * _mean_abs):
        return {
            "dist_name": None, "params": None, "dist_obj": None,
            "ks_p": 0.0, "fit_ok": False,
            "diagnosis": "Dados quase-identicos - fit alternativo indisponivel.",
        }

    candidates = [
        ("skewnorm",  stats.skewnorm),
        ("t",         stats.t),
        ("truncnorm", stats.truncnorm),
    ]

    best: dict = {
        "dist_name": None, "params": None, "dist_obj": None,
        "ks_p": 0.0, "fit_ok": False, "diagnosis": ""
    }

    for name, dist in candidates:
        try:
            with warnings.catch_warnings():
                warnings.simplefilter("ignore")
                params = dist.fit(data)
                ks_stat, ks_p = stats.kstest(data, dist.name, args=params)
            if ks_p > best["ks_p"]:
                best.update({
                    "dist_name": name, "params": params,
                    "dist_obj": dist, "ks_p": float(ks_p),
                    "fit_ok": bool(ks_p > 0.05),
                })
        except Exception:
            continue

    # GMM 2 componentes
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            gmm = GaussianMixture(n_components=2, random_state=0)
            gmm.fit(data.reshape(-1, 1))
            samples, _ = gmm.sample(min(len(data), 5000))
        ks_stat, ks_p = stats.ks_2samp(data, samples.ravel())
        if ks_p > best["ks_p"]:
            best.update({
                "dist_name": "gmm", "params": gmm,
                "dist_obj": None, "ks_p": float(ks_p),
                "fit_ok": bool(ks_p > 0.05),
            })
    except Exception:
        pass

    # Diagnóstico automático
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        try:
            sk = float(stats.skew(data))
            kurt = float(stats.kurtosis(data))
        except Exception:
            sk, kurt = 0.0, 0.0

    if abs(sk) > 1.0:
        direction = "positiva" if sk > 0 else "negativa"
        best["diagnosis"] = f"Assimetria {direction} (skewness={sk:.2f})"
    elif abs(kurt) > 3.0:
        best["diagnosis"] = f"Caudas pesadas (kurtosis={kurt:.2f})"
    else:
        best["diagnosis"] = f"Nao-normalidade (skewness={sk:.2f}, kurtosis={kurt:.2f})"

    return best


# ---------------------------------------------------------------------------
# Estatísticas completas
# ---------------------------------------------------------------------------

def compute_statistics(
    arr: np.ndarray,
    spec: SpecLimits,
    normality_result: dict,
    alt_dist: Optional[dict] = None,
) -> dict:
    """Calcula todas as métricas de qualidade."""
    data = arr[np.isfinite(arr)]
    n = len(data)
    if n == 0:
        raise ValueError("Nenhum dado válido para calcular estatísticas.")

    mean = float(np.mean(data))
    std  = float(np.std(data, ddof=1))
    median = float(np.median(data))
    pct_levels = [0.1, 1, 5, 25, 50, 75, 95, 99, 99.9]
    percentiles = {f"p{p}": float(np.percentile(data, p)) for p in pct_levels}

    # Dados quase-idênticos: skewness/kurtosis causam catastrophic cancellation.
    # Suprimimos o warning e retornamos NaN quando std é desprezível.
    _nearly_const = std < max(1e-15, 1e-9 * abs(mean))
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        skewness = float(stats.skew(data)) if not _nearly_const else float("nan")
        kurtosis = float(stats.kurtosis(data)) if not _nearly_const else float("nan")

    result: dict = {
        "mean": mean, "std": std, "median": median,
        "min": float(np.min(data)), "max": float(np.max(data)),
        "skewness": skewness,
        "kurtosis": kurtosis,
        "percentiles": percentiles,
        "cp": None, "cpk": None, "sigma_level": None,
        "yield_observed_pct": None, "n_out_of_spec": 0,
        "gaussian_extrapolation": None,
        "alt_extrapolation": None,
        "extrapolation_note": "",
    }

    lsl = spec.lsl
    usl = spec.usl
    has_spec = (lsl is not None) or (usl is not None)

    if has_spec:
        # Yield observado
        mask_fail = np.zeros(len(data), dtype=bool)
        if lsl is not None:
            mask_fail |= data < lsl
        if usl is not None:
            mask_fail |= data > usl
        n_fail = int(np.sum(mask_fail))
        result["n_out_of_spec"] = n_fail
        result["yield_observed_pct"] = (1.0 - n_fail / n) * 100.0

        # Cp / Cpk
        if lsl is not None and usl is not None and std > 0:
            cp  = (usl - lsl) / (6.0 * std)
            cpu = (usl - mean) / (3.0 * std)
            cpl = (mean - lsl) / (3.0 * std)
            cpk = min(cpu, cpl)
            result["cp"]  = float(cp)
            result["cpk"] = float(cpk)
            result["sigma_level"] = float(cpk * 3.0)

        # Extrapolação gaussiana
        if normality_result["is_normal"] and std > 0:
            sigma_table = {}
            for s in [1, 2, 3, 4, 5, 6]:
                lo = mean - s * std
                hi = mean + s * std
                if lsl is not None and usl is not None:
                    y = stats.norm.cdf(hi, mean, std) - stats.norm.cdf(lo, mean, std)
                elif usl is not None:
                    y = stats.norm.cdf(usl, mean, std)
                else:
                    y = 1.0 - stats.norm.cdf(lsl, mean, std)
                sigma_table[f"{s}s"] = {
                    "yield": float(y * 100.0),
                    "dpm": float((1.0 - y) * 1e6),
                }

            # Yield real baseado nos spec limits
            if lsl is not None and usl is not None:
                y_real = stats.norm.cdf(usl, mean, std) - stats.norm.cdf(lsl, mean, std)
            elif usl is not None:
                y_real = stats.norm.cdf(usl, mean, std)
            else:
                y_real = 1.0 - stats.norm.cdf(lsl, mean, std)

            result["gaussian_extrapolation"] = {
                "yield_pct": float(y_real * 100.0),
                "dpm": float((1.0 - y_real) * 1e6),
                "sigma_table": sigma_table,
            }
            result["extrapolation_note"] = "Extrapolação gaussiana (distribuição normal confirmada)."

        # Extrapolação alternativa
        elif alt_dist and alt_dist.get("fit_ok") and alt_dist.get("dist_obj") is not None:
            d_obj = alt_dist["dist_obj"]
            params = alt_dist["params"]
            try:
                if lsl is not None and usl is not None:
                    y_real = d_obj.cdf(usl, *params) - d_obj.cdf(lsl, *params)
                elif usl is not None:
                    y_real = d_obj.cdf(usl, *params)
                else:
                    y_real = 1.0 - d_obj.cdf(lsl, *params)
                result["alt_extrapolation"] = {
                    "dist_name": alt_dist["dist_name"],
                    "yield_pct": float(y_real * 100.0),
                    "dpm": float((1.0 - y_real) * 1e6),
                    "warning": f"Extrapolação via {alt_dist['dist_name']}. Interpretar com cautela.",
                }
                result["extrapolation_note"] = (
                    f"Distribuição não-normal. Fit alternativo: {alt_dist['dist_name']}. "
                    f"{alt_dist.get('diagnosis', '')}"
                )
            except Exception as exc:
                result["extrapolation_note"] = (
                    f"Fit alternativo ({alt_dist['dist_name']}) disponível mas extrapolação falhou: {exc}"
                )
        else:
            result["extrapolation_note"] = (
                "Distribuição não-normal e nenhum fit alternativo passou no KS-test. "
                "Use o yield observado como estimativa conservadora. "
                "Considere aumentar o número de runs para melhor cobertura das caudas."
            )

    return result


# ---------------------------------------------------------------------------
# Orquestrador por par
# ---------------------------------------------------------------------------

def analyze_pair(pair: PairConfig, merged_data: dict) -> dict:
    """Orquestra toda a análise para um par meas_in / meas_out."""
    measures = merged_data["measures"]

    if pair.meas_in not in measures:
        raise KeyError(f"Medida '{pair.meas_in}' não encontrada nos dados. "
                       f"Disponíveis: {list(measures.keys())}")
    if pair.meas_out not in measures:
        raise KeyError(f"Medida '{pair.meas_out}' não encontrada nos dados. "
                       f"Disponíveis: {list(measures.keys())}")

    arr_in  = measures[pair.meas_in]
    arr_out = measures[pair.meas_out]

    tf_result = compute_tf(arr_in, arr_out, pair.tf_type, pair.tf_custom_expr)

    # Remove NaN herdados dos dados brutos
    valid = np.isfinite(tf_result["values"])
    tf_values_clean = tf_result["values"][valid]

    normality = test_normality(tf_values_clean)

    alt_dist = None
    if not normality["is_normal"]:
        alt_dist = fit_alternative_distribution(tf_values_clean, normality)

    stats_result = compute_statistics(
        tf_values_clean, pair.spec, normality, alt_dist
    )

    # Propaga aviso de entrada constante para o stats_result
    degenerate_note = tf_result.get("fit_coeffs", {}).get("degenerate_note")
    if degenerate_note:
        prev = stats_result.get("extrapolation_note", "")
        stats_result["extrapolation_note"] = (
            f"[Aviso TF] {degenerate_note}"
            + (f" | {prev}" if prev else "")
        )

    return {
        "pair":       pair,
        "arr_in":     arr_in,
        "arr_out":    arr_out,
        "tf":         tf_result,
        "normality":  normality,
        "alt_dist":   alt_dist,
        "stats":      stats_result,
        "degenerate": bool(degenerate_note),
    }


# ---------------------------------------------------------------------------
# Teste rápido
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import sys, os
    sys.path.insert(0, os.path.join(os.path.dirname(__file__), "tests"))
    from mock_data import generate_mock_merged_data, generate_mock_project_config

    cfg = generate_mock_project_config()
    pair = cfg.pairs[0]

    for dist in ("normal", "skewnorm", "bimodal", "heavy_tails"):
        print(f"\n{'='*60}")
        print(f"Distribuição: {dist}")
        merged = generate_mock_merged_data(n=2000, distribution=dist)
        result = analyze_pair(pair, merged)

        st = result["stats"]
        nm = result["normality"]
        print(f"  Normal: {nm['is_normal']}  "
              f"(SW p={nm['shapiro']['p']:.4f}, "
              f"KS p={nm['ks']['p']:.4f}, "
              f"AD {'pass' if nm['anderson']['passed'] else 'fail'})")
        print(f"  mean={st['mean']:.6f}  std={st['std']:.6f}  "
              f"skew={st['skewness']:.3f}  kurt={st['kurtosis']:.3f}")
        if st["cpk"] is not None:
            print(f"  Cp={st['cp']:.3f}  Cpk={st['cpk']:.3f}  "
                  f"sigma={st['sigma_level']:.2f}")
        if st["yield_observed_pct"] is not None:
            print(f"  Yield obs={st['yield_observed_pct']:.2f}%  "
                  f"n_fail={st['n_out_of_spec']}")
        print(f"  Nota: {st['extrapolation_note'][:80]}")
        if result["alt_dist"]:
            print(f"  Alt dist: {result['alt_dist']['dist_name']}  "
                  f"fit_ok={result['alt_dist']['fit_ok']}  "
                  f"diag: {result['alt_dist']['diagnosis']}")
