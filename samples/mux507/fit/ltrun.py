"""Small helpers: run LTspice in batch on a netlist, read the ASCII .raw and the .meas values."""
import os, re, subprocess
import numpy as np

LT = os.path.join(os.environ['LOCALAPPDATA'], r'Programs\ADI\LTspice\LTspice.exe')


def run(path, text, ascii=True):
    with open(path, 'w') as f:
        f.write(text)
    subprocess.run([LT, '-b'] + (['-ascii'] if ascii else []) + [path], check=True, timeout=1800)
    base = os.path.splitext(path)[0]
    log = open(base + '.log', encoding='latin-1').read()
    return base, log


def meas(log):
    out = {}
    for m in re.finditer(r'^(\w+): .*?=\s*([-+0-9.eE]+)', log, re.M):
        out[m.group(1).lower()] = float(m.group(2))
    return out


def step_meas(log):
    """.meas tables of stepped runs -> {name: [values in step order]}."""
    out, name = {}, None
    for line in log.splitlines():
        m = re.match(r'^Measurement: (\w+)', line)
        if m:
            name = m.group(1).lower(); out[name] = []; continue
        if name:
            parts = line.split()
            if len(parts) >= 2 and parts[0].isdigit():
                try:
                    out[name].append(float(parts[1]))
                except ValueError:
                    out[name].append(float('nan'))
            elif not line.strip():
                name = None
    return out


def raw(base):
    """ASCII raw -> list of (variables dict name->array) per step."""
    txt = open(base + '.raw', encoding='latin-1').read()
    hdr, data = txt.split('Values:', 1)
    names = re.findall(r'^\s*\d+\s+(\S+)\s+\S+', hdr.split('\nVariables:', 1)[1], re.M)
    nv = len(names)
    # the ASCII format is: index <tab> value, then (nv-1) values on following lines
    tok = data.split()
    vals, i = [], 0
    while i < len(tok):
        i += 1                      # point index
        vals.append([float(tok[i + k].split(',')[0]) for k in range(nv)])
        i += nv
    a = np.array(vals)
    # split steps where the sweep variable restarts
    x = a[:, 0]
    cuts = [0] + [j for j in range(1, len(x)) if x[j] < x[j - 1]] + [len(x)]
    return [dict(zip(names, a[cuts[s]:cuts[s + 1]].T)) for s in range(len(cuts) - 1)]
