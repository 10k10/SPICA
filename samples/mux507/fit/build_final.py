import sys, json
sys.path.insert(0, '.')
import mux_model
r = json.load(open('fit1.json')); c = json.load(open('capfit.json')); q = dict(mux_model.DEFAULT_Q, **json.load(open('qfit.json')))
ks, kd = (float(a) for a in sys.argv[2:4]) if len(sys.argv) > 3 else (1.0, 1.0)
c = dict(c, CNS=c['CNS'] * ks, CPPS=c['CPPS'] * ks, CND=c['CND'] * kd, CPPD=c['CPPD'] * kd)
open(sys.argv[1], 'w').write(mux_model.lib(r, c, q))
