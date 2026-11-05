using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>Where the tolerance of a model parameter comes from.</summary>
    public enum OpBasis
    {
        Guaranteed,   // datasheet min/max: flat over the guaranteed range
        MinTyp,       // a limit (min or max) and the typical value: flat between them
        TypicalX3,    // signed error with a typical value only: flat over +/-3 x typical
        Typical,      // typical value, not varied
        Derived,      // computed from other datasheet numbers (see the note)
        Assumed,      // not in the datasheet: SPICA's assumption (see the note)
    }

    /// <summary>One parameter of a SPICA op-amp model: center value and flat half-width (0 = fixed).</summary>
    public sealed class OpParam
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public string Name, Unit, Note;
        public double Center, Half;
        public OpBasis Basis;
        public bool Varies => Half > 0;

        /// <summary>The value written on the X line: a constant, or a flat() SPICA pairs per run.</summary>
        public string Expression()
        {
            if (!Varies) return OpAmps.Num(Center);
            return Center == 0 ? $"{{flat({OpAmps.Num(Half)})}}" : $"{{{OpAmps.Num(Center)}+flat({OpAmps.Num(Half)})}}";
        }

        public string RangeText()
        {
            string u = Unit == "dB" || Unit == "C" || Unit == "Ohm" ? " " + Unit : Unit ?? "";
            double f = 1;
            if (Unit == "V/s") { f = 1e-6; u = " V/us"; }   // slew rate as datasheets give it
            string E(double v) => f == 1 ? ErrorBudget.Eng(v) : (v * f).ToString("0.##", Inv);
            if (!Varies) return E(Center) + u;
            if (Center == 0) return "+/-" + E(Half) + u;
            return E(Center - Half) + " .. " + E(Center + Half) + u;
        }

        public static string BasisText(OpBasis b)
        {
            switch (b)
            {
                case OpBasis.Guaranteed: return "datasheet limit";
                case OpBasis.MinTyp: return "typical .. limit";
                case OpBasis.TypicalX3: return "3 x typical (no limit given)";
                case OpBasis.Typical: return "typical, fixed";
                case OpBasis.Derived: return "derived";
                default: return "assumed";
            }
        }
    }

    /// <summary>A datasheet part mapped onto one of SPICA's generic models.</summary>
    public sealed class OpAmpPart
    {
        public string Name, Title, Source, Conditions, Core;
        public readonly List<OpParam> Params = new List<OpParam>();
        public string Model => Name + "_MC";
        public bool IsFda => Core == "SPICA_FDA";
        public string Pins => IsFda ? "+IN -IN VOCM V+ V- OUT+ OUT-" : "In+ In- V+ V- OUT";
        public string Symbol => IsFda ? "an FDA symbol with this pin order, e.g. LTspice's AD8139" : "opamp2 (LTspice's generic 5-pin op-amp)";

        public OpAmpPart P(string name, double center, double half, string unit, OpBasis basis, string note)
        {
            Params.Add(new OpParam { Name = name, Center = center, Half = half, Unit = unit, Basis = basis, Note = note });
            return this;
        }
    }

    /// <summary>
    /// SPICA's op-amp library: two generic behavioral models (a voltage-feedback op-amp and a fully
    /// differential amplifier) and datasheet parts mapped onto them. A part keeps every tolerance as an
    /// instance parameter of its X line, so SPICA pairs it like any other mc()/flat() and the
    /// sensitivity and worst-case analyses name it (e.g. "U1.Vos"). The models are built for DC and
    /// low-frequency accuracy (error budgets): one-pole dynamics, slew limit, rail headroom, current limit.
    /// </summary>
    public static class OpAmps
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const string LibraryFile = "SPICA_opamps.lib";

        internal static string Num(double v) => v.ToString("G6", Inv);

        public static readonly List<OpAmpPart> Parts = new List<OpAmpPart>
        {
            new OpAmpPart { Name = "OPA4991", Core = "SPICA_OA", Title = "TI OPA4991: 40 V, rail-to-rail in/out, 4.5 MHz (quad)",
                Source = "TI SBOS969F (Jan 2022)", Conditions = "VS = 2.7..40 V, RL = 10 kOhm, limits at 25 C" }
                .P("Vos", 0, 830e-6, "V", OpBasis.Guaranteed, "max +/-830 uV at 25 C (+/-850 uV at -40..125 C)")
                .P("TcVos", 0, 0.9e-6, "V/C", OpBasis.TypicalX3, "typ +/-0.3 uV/C, no max")
                .P("Vos2", 0, 830e-6, "V", OpBasis.Assumed, "aux input pair (VCM > V+ - 1 V): not specified apart, same limit as Vos")
                .P("VcmX", 1.5, 0, "V", OpBasis.Derived, "input pair crossover: main pair to V+ - 2 V, aux pair above V+ - 1 V")
                .P("Ib", 0, 30e-12, "A", OpBasis.TypicalX3, "typ +/-10 pA, no max")
                .P("Ib2", 0, 30e-12, "A", OpBasis.TypicalX3, "aux pair: same as Ib")
                .P("Ios", 0, 30e-12, "A", OpBasis.TypicalX3, "typ +/-10 pA, no max")
                .P("IbT", 30, 0, "C", OpBasis.Derived, "x2 every 30 C: typical graph (Fig 6-13) ~10x from 25 to 125 C")
                .P("IosT", 30, 0, "C", OpBasis.Derived, "as IbT")
                .P("Kcm", 0, 3.55e-6, "V/V", OpBasis.Guaranteed, "CMRR min 109 dB at VS = 40 V (84 dB at VS = 4 V)")
                .P("Kps", 0, 1e-6, "V/V", OpBasis.Guaranteed, "PSRR max +/-1 uV/V (VS = 4..40 V); used only with VsRef")
                .P("AolDB", 132.5, 12.5, "dB", OpBasis.MinTyp, "min 120, typ 145 dB (VS = 40 V)")
                .P("GBW", 4.5e6, 0, "Hz", OpBasis.Typical, "typ 4.5 MHz")
                .P("SR", 21e6, 0, "V/s", OpBasis.Typical, "typ 21 V/us")
                .P("Vsat", 10e-3, 0, "V", OpBasis.Guaranteed, "headroom to each rail, no load: max 10 mV")
                .P("Ro", 24, 0, "Ohm", OpBasis.Derived, "headroom max 55 mV at 10 kOhm, 250 mV at 2 kOhm (VS = 40 V)")
                .P("Isc", 75e-3, 0, "A", OpBasis.Typical, "short-circuit current typ 75 mA")
                .P("Iq", 560e-6, 0, "A", OpBasis.Typical, "typ 560 uA per amplifier")
                .P("Rid", 540e9, 0, "Ohm", OpBasis.Typical, "540 GOhm || 9 pF")
                .P("Cid", 9e-12, 0, "F", OpBasis.Typical, "")
                .P("Rcm", 6e12, 0, "Ohm", OpBasis.Typical, "6 TOhm || 1 pF")
                .P("Ccm", 1e-12, 0, "F", OpBasis.Typical, ""),

            new OpAmpPart { Name = "OPA4182", Core = "SPICA_OA", Title = "TI OPA4182: 36 V zero-drift, rail-to-rail out, 5 MHz (quad)",
                Source = "TI SBOS936E (Aug 2022)", Conditions = "VS = +/-18 V, RL = 10 kOhm, OPA4182ID limits at 25 C" }
                .P("Vos", 0, 4e-6, "V", OpBasis.Guaranteed, "max +/-4 uV (25 C and -40..125 C)")
                .P("TcVos", 0, 20e-9, "V/C", OpBasis.Guaranteed, "max +/-0.020 uV/C (OPA4182ID)")
                .P("Ib", 0, 350e-12, "A", OpBasis.Guaranteed, "max +/-350 pA at 25 C (+/-1 nA 0..85 C, +/-7 nA -40..125 C)")
                .P("Ios", 0, 700e-12, "A", OpBasis.Guaranteed, "max +/-700 pA at 25 C (+/-3 nA -40..125 C)")
                .P("IbT", 23, 0, "C", OpBasis.Derived, "x2 every 23 C: 350 pA at 25 C -> 7 nA at 125 C")
                .P("IosT", 48, 0, "C", OpBasis.Derived, "x2 every 48 C: 700 pA at 25 C -> 3 nA at 125 C")
                .P("Kcm", 0, 71e-9, "V/V", OpBasis.Guaranteed, "CMRR min 143 dB at +/-18 V (130 dB over temperature, 120 dB at +/-2.25 V)")
                .P("Kps", 0, 50e-9, "V/V", OpBasis.Guaranteed, "PSRR max +/-0.05 uV/V; used only with VsRef")
                .P("AolDB", 157.5, 12.5, "dB", OpBasis.MinTyp, "min 145, typ 170 dB")
                .P("GBW", 5e6, 0, "Hz", OpBasis.Typical, "typ 5 MHz")
                .P("SR", 10e6, 0, "V/s", OpBasis.Typical, "typ 10 V/us")
                .P("Vsat", 15e-3, 0, "V", OpBasis.Guaranteed, "headroom to each rail, no load: max 15 mV (input range only to V+ - 2.5 V)")
                .P("Ro", 54, 0, "Ohm", OpBasis.Derived, "headroom max 110 mV at 10 kOhm, 500 mV at 2 kOhm")
                .P("Isc", 65e-3, 0, "A", OpBasis.Typical, "short-circuit current typ 65 mA")
                .P("Iq", 850e-6, 0, "A", OpBasis.Typical, "typ 0.85 mA per amplifier")
                .P("Rid", 100e6, 0, "Ohm", OpBasis.Typical, "100 MOhm || 3.7 pF")
                .P("Cid", 3.7e-12, 0, "F", OpBasis.Typical, "")
                .P("Rcm", 60e12, 0, "Ohm", OpBasis.Typical, "60 TOhm || 2.3 pF")
                .P("Ccm", 2.3e-12, 0, "F", OpBasis.Typical, ""),

            new OpAmpPart { Name = "LMH6619", Core = "SPICA_OA", Title = "TI LMH6619: 130 MHz, rail-to-rail in/out, single supply (dual)",
                Source = "TI SNOSAV7E (Oct 2012)", Conditions = "V+ = 5 V, V- = 0 V, RL = 1 kOhm; limits at 25 C unless noted" }
                .P("Vos", 0, 750e-6, "V", OpBasis.Guaranteed, "max +/-0.75 mV at 25 C (+/-1.3 mV over temperature)")
                .P("TcVos", 0, 2.4e-6, "V/C", OpBasis.TypicalX3, "typ 0.8 uV/C, no max")
                .P("Vos2", 0, 750e-6, "V", OpBasis.Guaranteed, "npn input pair (VCM = 4.5 V): same limit")
                .P("VcmX", 1.3, 0, "V", OpBasis.Derived, "pnp pair up to V+ - 1.6 V, npn pair from V+ - 1 V (CMRR conditions)")
                .P("Ib", -1.5e-6, 0.9e-6, "A", OpBasis.MinTyp, "pnp pair: typ -1.5 uA, max -2.4 uA (over temperature)")
                .P("Ib2", 1.0e-6, 0.9e-6, "A", OpBasis.MinTyp, "npn pair: typ +1.0 uA, max +1.9 uA (over temperature)")
                .P("Ios", 0, 260e-9, "A", OpBasis.Guaranteed, "max +/-0.26 uA (over temperature)")
                .P("Kcm", 0, 89e-6, "V/V", OpBasis.Guaranteed, "CMRR min 81 dB")
                .P("Kps", 0, 63e-6, "V/V", OpBasis.Guaranteed, "PSRR min 84 dB; used only with VsRef")
                .P("AolDB", 92, 8, "dB", OpBasis.MinTyp, "min 84, typ 100 dB (RL = 1 kOhm)")
                .P("GBW", 55.5e6, 1.5e6, "Hz", OpBasis.MinTyp, "min 54, typ 57 MHz")
                .P("SR", 49.5e6, 5.5e6, "V/s", OpBasis.MinTyp, "min 44, typ 55 V/us")
                .P("Vsat", 40e-3, 0, "V", OpBasis.Derived, "headroom max 73..85 mV at 1 kOhm, 255..275 mV at 150 Ohm")
                .P("Ro", 14, 0, "Ohm", OpBasis.Derived, "as Vsat")
                .P("Isc", 35e-3, 0, "A", OpBasis.Typical, "linear output current typ 35 mA (min 25 mA)")
                .P("Iq", 1.3e-3, 0, "A", OpBasis.Typical, "typ 1.3 mA per channel")
                .P("Rid", 8e6, 0, "Ohm", OpBasis.Assumed, "only RIN = 8 MOhm is given; used for both")
                .P("Cid", 0, 0, "F", OpBasis.Assumed, "not specified: none")
                .P("Rcm", 8e6, 0, "Ohm", OpBasis.Typical, "RIN 8 MOhm, CIN 1.5 pF")
                .P("Ccm", 1.5e-12, 0, "F", OpBasis.Typical, ""),

            new OpAmpPart { Name = "AD8139", Core = "SPICA_FDA", Title = "ADI AD8139: fully differential ADC driver, 410 MHz",
                Source = "ADI AD8139 Rev. C", Conditions = "VS = +/-5 V, VOCM = 0 V, RL,dm = 1 kOhm; limits at 25 C unless noted" }
                .P("Vos", 0, 500e-6, "V", OpBasis.Guaranteed, "max +/-500 uV")
                .P("TcVos", 0, 3.75e-6, "V/C", OpBasis.TypicalX3, "typ 1.25 uV/C, no max")
                .P("Ib", 4e-6, 4e-6, "A", OpBasis.Guaranteed, "typ 2.25 uA, max 8 uA (over temperature): flat 0..8 uA")
                .P("Ios", 0, 0.5e-6, "A", OpBasis.Guaranteed, "max 0.5 uA")
                .P("Kcm", 0, 100e-6, "V/V", OpBasis.Guaranteed, "input CMRR min 80 dB")
                .P("Kps", 0, 17.8e-6, "V/V", OpBasis.Guaranteed, "PSRR min 95 dB; used only with VsRef")
                .P("Voscm", 0, 900e-6, "V", OpBasis.Guaranteed, "output common-mode offset VO,cm - VOCM max +/-900 uV")
                .P("Kocm", 0, 100e-6, "V/V", OpBasis.Derived, "VOCM CMRR min 74 dB (output-referred, measured at G = 1), referred to the input")
                .P("Gocm", 1, 0.001, "V/V", OpBasis.Guaranteed, "VOCM gain 0.999 .. 1.001")
                .P("Rocm", 3.5e6, 0, "Ohm", OpBasis.Typical, "VOCM input resistance 3.5 MOhm")
                .P("AolDB", 114, 0, "dB", OpBasis.Typical, "typ 114 dB")
                .P("GBW", 1.2e9, 0, "Hz", OpBasis.Derived, "fitted: G = 1, RF = RG = 200 Ohm gives the datasheet -3 dB of 410 MHz")
                .P("SR", 800e6, 0, "V/s", OpBasis.Typical, "typ 800 V/us")
                .P("Vsat", 0.2, 0, "V", OpBasis.Guaranteed, "output swing to each rail: 0.20 V (RF = RG = 10 kOhm)")
                .P("Ro", 5, 0, "Ohm", OpBasis.Assumed, "not specified")
                .P("Isc", 100e-3, 0, "A", OpBasis.Typical, "output current 100 mA")
                .P("Iq", 24.5e-3, 0, "A", OpBasis.Typical, "typ 24.5 mA")
                .P("Rid", 600e3, 0, "Ohm", OpBasis.Typical, "differential 600 kOhm")
                .P("Cid", 0, 0, "F", OpBasis.Assumed, "not specified: none")
                .P("Rcm", 1.5e6, 0, "Ohm", OpBasis.Typical, "common mode 1.5 MOhm || 1.2 pF")
                .P("Ccm", 1.2e-12, 0, "F", OpBasis.Typical, ""),
        };

        public static OpAmpPart Find(string model) =>
            Parts.FirstOrDefault(p => p.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ netlist

        /// <summary>
        /// Gives every X line that uses a SPICA part its tolerances (the ones the line does not set itself)
        /// and appends the library when the netlist neither includes it nor defines the parts.
        /// Returns the instances found, "U1 = OPA4991".
        /// </summary>
        public static List<string> Augment(List<string> lines)
        {
            var found = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool defined = false;
            int depth = 0, endAt = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].TrimStart();
                string low = t.ToLowerInvariant();
                if (low.StartsWith(".subckt"))
                {
                    depth++;
                    var tok = Netlist.Tokenize(t);
                    if (tok.Count > 1 && (tok[1].StartsWith("SPICA_", StringComparison.OrdinalIgnoreCase) || Find(tok[1]) != null)) defined = true;
                    continue;
                }
                if (low.StartsWith(".ends")) { depth = Math.Max(0, depth - 1); continue; }
                if ((low.StartsWith(".lib") || low.StartsWith(".inc")) && low.Contains("spica_opamps")) defined = true;
                if (low == ".end" || low.StartsWith(".end ")) { if (endAt < 0) endAt = i; continue; }
                if (depth > 0 || t.Length == 0 || char.ToUpperInvariant(t[0]) != 'X') continue;

                int c = lines[i].IndexOf(';');
                string code = c >= 0 ? lines[i].Substring(0, c) : lines[i];
                string comment = c >= 0 ? lines[i].Substring(c) : "";
                var toks = Netlist.Tokenize(code.Trim());
                int model = toks.Count - 1;
                for (int k = 1; k < toks.Count; k++)
                    if (toks[k].Contains("=") || toks[k].Equals("params:", StringComparison.OrdinalIgnoreCase)) { model = k - 1; break; }
                var part = model > 0 ? Find(toks[model]) : null;
                if (part == null) continue;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int k = model + 1; k < toks.Count; k++)
                {
                    int eq = toks[k].IndexOf('=');
                    if (eq > 0) set.Add(toks[k].Substring(0, eq).Trim());
                }
                var sb = new StringBuilder(code.TrimEnd());
                foreach (var p in part.Params.Where(p => p.Varies && !set.Contains(p.Name)))
                    sb.Append(' ').Append(p.Name).Append('=').Append(p.Expression());
                lines[i] = sb.ToString() + (comment.Length > 0 ? " " + comment : "");
                used.Add(part.Name);
                found.Add($"{Netlist.InstanceName(toks[0])} = {part.Name}");
            }
            if (used.Count > 0 && !defined)
            {
                var lib = LibraryText(Parts.Where(p => used.Contains(p.Name))).Replace("\r\n", "\n").Split('\n');
                lines.InsertRange(endAt >= 0 ? endAt : lines.Count, lib.Where(l => l.Length > 0));
            }
            return found;
        }

        // ------------------------------------------------------------------ library text

        const string CoreOa = @"* SPICA generic voltage-feedback op-amp. Pins as LTspice's opamp2 symbol: In+ In- V+ V- OUT
* Vos/Vos2: offset of the lower/upper input pair (VcmX > 0: the upper pair takes over above V+ - VcmX)
* TcVos: offset drift from 25 C. Ib/Ib2: bias current into each input per pair, Ios: offset current,
* both x2 every IbT/IosT C (0 = no temperature change). Kcm: input offset per volt of common mode
* (from mid-supply), Kps: per volt of supply change from VsRef (0 = off). AolDB, GBW: one-pole open loop,
* SR: slew rate. Vsat: headroom to each rail at no load, Ro: output resistance, Isc: current limit.
.subckt SPICA_OA inp inn vp vn out params: Vos=0 TcVos=0 Vos2=0 VcmX=0 Ib=0 Ib2=0 Ios=0 IbT=0 IosT=0
+ Kcm=0 Kps=0 VsRef=0 AolDB=120 GBW=1Meg SR=1Meg Vsat=10m Ro=50 Isc=50m Iq=1m
+ Rid=1T Cid=1p Rcm=1T Ccm=1p
Iq vp vn {Iq}
Bmid mid 0 V=(V(vp)+V(vn))/2
Bsel sel 0 V=if(VcmX>0, 1/(1+exp(limit(((V(vp)-VcmX)-(V(inp)+V(inn))/2)/20m,-60,60))), 0)
Bib inp mid I=(Ib*(1-V(sel))+Ib2*V(sel))*if(IbT>0,pwr(2,(temp-25)/IbT),1)+Ios/2*if(IosT>0,pwr(2,(temp-25)/IosT),1)
Bin inn mid I=(Ib*(1-V(sel))+Ib2*V(sel))*if(IbT>0,pwr(2,(temp-25)/IbT),1)-Ios/2*if(IosT>0,pwr(2,(temp-25)/IosT),1)
Rid inp inn {Rid}
Cid inp inn {Cid}
Rcp inp mid {Rcm}
Ccp inp mid {Ccm}
Rcn inn mid {Rcm}
Ccn inn mid {Ccm}
Bos ipx inp V=Vos*(1-V(sel))+Vos2*V(sel)+TcVos*(temp-25)+Kcm*((V(inp)+V(inn))/2-V(mid))+if(VsRef>0,Kps*(V(vp)-V(vn)-VsRef),0)
Bg 0 x I=limit(V(ipx,inn),-SR/(2*pi*GBW),SR/(2*pi*GBW))
R1 x 0 {pwr(10,AolDB/20)}
C1 x 0 {1/(2*pi*GBW)}
Bhi hi 0 V=V(vp)-V(mid)-Vsat
Blo lo 0 V=V(vn)-V(mid)+Vsat
Dhi x hi SPICA_DCLAMP
Dlo lo x SPICA_DCLAMP
.model SPICA_DCLAMP D(Ron=1m Roff=1T Vfwd=0 epsilon=1m)
Bo o1 0 V=V(mid)+V(x)
Bout 0 out I=limit((V(o1)-V(out))/Ro,-Isc,Isc)
.ends SPICA_OA";

        const string CoreFda = @"* SPICA generic fully differential amplifier. Pins as LTspice's AD8139 symbol: +IN -IN VOCM V+ V- OUT+ OUT-
* OUT+ - OUT- = gain x (IN+ - IN-): feed back OUT+ to -IN and OUT- to +IN.
* Voscm: output common-mode offset, Gocm: VOCM gain, Kocm: input offset per volt of VOCM (from mid-supply),
* Rocm: VOCM input resistance to mid-supply. The other parameters as SPICA_OA.
.subckt SPICA_FDA inp inn vocm vp vn outp outn params: Vos=0 TcVos=0 Ib=0 Ios=0 IbT=0 IosT=0
+ Kcm=0 Kps=0 VsRef=0 Voscm=0 Kocm=0 Gocm=1 Rocm=1T AolDB=100 GBW=100Meg SR=100Meg
+ Vsat=0.2 Ro=5 Isc=100m Iq=1m Rid=1T Cid=1p Rcm=1T Ccm=1p
Iq vp vn {Iq}
Bmid mid 0 V=(V(vp)+V(vn))/2
Rocm vocm mid {Rocm}
Bib inp mid I=Ib*if(IbT>0,pwr(2,(temp-25)/IbT),1)+Ios/2*if(IosT>0,pwr(2,(temp-25)/IosT),1)
Bin inn mid I=Ib*if(IbT>0,pwr(2,(temp-25)/IbT),1)-Ios/2*if(IosT>0,pwr(2,(temp-25)/IosT),1)
Rid inp inn {Rid}
Cid inp inn {Cid}
Rcp inp mid {Rcm}
Ccp inp mid {Ccm}
Rcn inn mid {Rcm}
Ccn inn mid {Ccm}
Bos ipx inp V=Vos+TcVos*(temp-25)+Kcm*((V(inp)+V(inn))/2-V(mid))+Kocm*(V(vocm)-V(mid))+if(VsRef>0,Kps*(V(vp)-V(vn)-VsRef),0)
Bg 0 x I=limit(V(ipx,inn),-SR/(2*pi*GBW),SR/(2*pi*GBW))
R1 x 0 {pwr(10,AolDB/20)}
C1 x 0 {1/(2*pi*GBW)}
Bhi hi 0 V=V(vp)-V(vn)-2*Vsat
Blo lo 0 V=-(V(vp)-V(vn)-2*Vsat)
Dhi x hi SPICA_DCLAMPF
Dlo lo x SPICA_DCLAMPF
.model SPICA_DCLAMPF D(Ron=1m Roff=1T Vfwd=0 epsilon=1m)
Bcm cm 0 V=V(mid)+Gocm*(V(vocm)-V(mid))+Voscm
Bop o1 0 V=limit(V(cm)+V(x)/2,V(vn)+Vsat,V(vp)-Vsat)
Bon o2 0 V=limit(V(cm)-V(x)/2,V(vn)+Vsat,V(vp)-Vsat)
Boutp 0 outp I=limit((V(o1)-V(outp))/Ro,-Isc,Isc)
Boutn 0 outn I=limit((V(o2)-V(outn))/Ro,-Isc,Isc)
.ends SPICA_FDA";

        /// <summary>The library file: the generic models and one wrapper per part, nominal values as defaults.</summary>
        public static string LibraryText(IEnumerable<OpAmpPart> parts = null)
        {
            var list = (parts ?? Parts).ToList();
            var sb = new StringBuilder();
            sb.Append("* ").Append(LibraryFile).Append(": op-amp models for SPICA Monte Carlo campaigns\r\n");
            sb.Append("* Each part runs at its nominal values in LTspice; SPICA adds the tolerances listed with\r\n");
            sb.Append("* each part to the X line (\"Vos={flat(830u)}\") unless the line sets that parameter itself.\r\n");
            sb.Append("* Models for DC and low-frequency accuracy: one pole, slew limit, rail headroom, current limit.\r\n*\r\n");
            if (list.Any(p => !p.IsFda)) sb.Append(CoreOa.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n*\r\n");
            if (list.Any(p => p.IsFda)) sb.Append(CoreFda.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n*\r\n");
            foreach (var p in list)
            {
                sb.Append("* ").Append(p.Title).Append("\r\n");
                sb.Append("* ").Append(p.Source).Append(": ").Append(p.Conditions).Append("\r\n");
                int w = p.Params.Max(x => x.Name.Length);
                foreach (var q in p.Params)
                {
                    sb.Append("*   ").Append(q.Name.PadRight(w)).Append("  ").Append(q.RangeText().PadRight(22))
                      .Append(OpParam.BasisText(q.Basis).PadRight(30));
                    if (!string.IsNullOrEmpty(q.Note)) sb.Append(q.Note);
                    sb.Append("\r\n");
                }
                string pins = p.IsFda ? "inp inn vocm vp vn outp outn" : "inp inn vp vn out";
                sb.Append(".subckt ").Append(p.Model).Append(' ').Append(pins).Append(" params:");
                foreach (var q in p.Params.Where(q => q.Varies)) sb.Append(' ').Append(q.Name).Append('=').Append(Num(q.Center));
                sb.Append(" VsRef=0\r\n");
                sb.Append("X1 ").Append(pins).Append(' ').Append(p.Core);
                int col = 0;
                foreach (var q in p.Params)
                {
                    if (col++ % 8 == 0) sb.Append("\r\n+");
                    sb.Append(' ').Append(q.Name).Append('=').Append(q.Varies ? "{" + q.Name + "}" : Num(q.Center));
                }
                sb.Append(" VsRef={VsRef}\r\n");
                sb.Append(".ends ").Append(p.Model).Append("\r\n*\r\n");
            }
            return sb.ToString();
        }
    }
}
