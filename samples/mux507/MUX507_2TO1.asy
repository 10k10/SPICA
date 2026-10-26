Version 4
SymbolType BLOCK
LINE Normal -64 -96 64 -48
LINE Normal 64 -48 64 48
LINE Normal 64 48 -64 96
LINE Normal -64 96 -64 -96
LINE Normal -96 -48 -64 -48
LINE Normal -96 48 -64 48
LINE Normal 64 0 96 0
LINE Normal -32 -84 -32 -128
LINE Normal -32 84 -32 128
LINE Normal 32 60 32 112
TEXT -56 -48 Left 1 0
TEXT -56 48 Left 1 1
TEXT 56 0 Right 1 D
TEXT -8 0 Center 1 MUX
TEXT -24 -116 Left 0 VCC
TEXT -24 116 Left 0 VSS
TEXT 40 100 Left 0 EN
TEXT -100 -56 Right 0 S0
TEXT -100 40 Right 0 S1
WINDOW 0 16 -80 Left 2
WINDOW 3 16 -112 Left 1
SYMATTR Value MUX507_2TO1
SYMATTR Prefix X
SYMATTR ModelFile MUX507.lib
SYMATTR SpiceLine RONK={1+flat(0.36)} CKS={1+flat(0.43)} CKD={1+flat(0.17)} QK={flat(1)} TDON={65n+flat(54n)} TDOFF={31n+flat(15n)} TDA={15n+flat(46n)}
SYMATTR SpiceLine2 M0={flat(0.036)} M1={flat(0.036)}
SYMATTR Description TI MUX507 as one 2:1 switch, EN = 0 -> S0, EN = 1 -> S1 (SPICA, uniform Monte Carlo defaults)
PIN -32 -128 NONE 0
PINATTR PinName VCC
PINATTR SpiceOrder 1
PIN -32 128 NONE 0
PINATTR PinName VSS
PINATTR SpiceOrder 2
PIN 32 112 NONE 0
PINATTR PinName EN
PINATTR SpiceOrder 3
PIN -96 -48 NONE 0
PINATTR PinName S0
PINATTR SpiceOrder 4
PIN -96 48 NONE 0
PINATTR PinName S1
PINATTR SpiceOrder 5
PIN 96 0 NONE 0
PINATTR PinName D
PINATTR SpiceOrder 6
