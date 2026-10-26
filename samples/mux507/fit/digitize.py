"""Extract the vector curves of MUX507 datasheet plots into CSV (exact, no eyeballing)."""
import json, sys
import pymupdf as fitz

# page index, frame (x0,y0,x1,y1) in PDF points, axis limits, curve colour -> label
FIGS = {
    'fig1_ron_dual':  (10, (95.3, 120.0, 282.6, 253.1), (-18, 18, 50, 400),
                       {(0.5, 0.0, 0.5): '10', (0.16, 0.36, 0.46): '13.5', (1.0, 0.6, 0.0): '15', (0.0, 0.0, 1.0): '16.5', (1.0, 0.0, 0.0): '18'}),
    'fig3_ron_low':   (10, (94.8, 323.1, 282.3, 456.3), (-8, 8, 100, 700),
                       {(1.0, 0.0, 0.0): '5', (0.0, 0.0, 1.0): '6', (1.0, 0.6, 0.0): '7'}),
    'fig6_ron_single': (10, (346.8, 526.3, 534.3, 659.5), (0, 14, 0, 700),
                       {(1.0, 0.0, 0.0): '10', (0.0, 0.0, 1.0): '12', (1.0, 0.6, 0.0): '14'}),
    'fig8_ron_12':    (11, (347.2, 110.6, 533.9, 243.3), (-12, 12, 0, 250), {(1.0, 0.0, 0.0): '12'}),
    'fig12_qinj':     (11, (343.9, 525.2, 531.7, 658.6), (-15, 15, -2, 2),
                       {(1.0, 0.0, 0.0): '15', (1.0, 0.6, 0.0): '10', (0.16, 0.36, 0.46): '5', (0.0, 0.0, 1.0): '12s'}),
    'fig20_cap':      (13, None, (-15, 15, 0, 30), None),
}


def key(c):
    return tuple(round(v, 2) for v in c) if c else None


def curves(page, frame, lim, colours):
    x0, y0, x1, y1 = frame
    ax0, ax1, ay0, ay1 = lim
    out = {}
    for d in page.get_drawings():
        k = key(d['color'])
        if colours is not None and k not in colours:
            continue
        r = d['rect']
        if r.x0 < x0 - 2 or r.x1 > x1 + 2 or r.y0 < y0 - 2 or r.y1 > y1 + 2 or len(d['items']) < (4 if lim[2] < 0 else 20):
            continue
        pts = []
        for it in d['items']:
            if it[0] == 'l':
                pts += [it[1], it[2]]
            elif it[0] == 'c':
                pts += [it[1], it[4]]
        xy = sorted({(round(ax0 + (p.x - x0) / (x1 - x0) * (ax1 - ax0), 4),
                      round(ay1 - (p.y - y0) / (y1 - y0) * (ay1 - ay0), 4)) for p in pts})
        label = colours[k] if colours else str(k)
        out.setdefault(label, []).extend(xy)
    return {k: sorted(v) for k, v in out.items()}


if __name__ == '__main__':
    doc = fitz.open(sys.argv[1])
    res = {}
    for name, (pg, frame, lim, colours) in FIGS.items():
        if frame is None:
            continue
        res[name] = curves(doc[pg], frame, lim, colours)
        for lab, xy in res[name].items():
            print(name, lab, len(xy), 'x', xy[0][0], '..', xy[-1][0], 'y', min(p[1] for p in xy), '..', max(p[1] for p in xy))
    json.dump(res, open(sys.argv[2], 'w'))
