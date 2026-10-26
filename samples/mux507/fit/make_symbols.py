"""LTspice symbols for MUX507 (8 channels) and MUX507_2CH, carrying the Monte Carlo defaults."""
import sys
sys.path.insert(0, '.')
import mux_model


def write(path, name, order, pos, rect, extra, line2):
    L = ['Version 4', 'SymbolType BLOCK', 'RECTANGLE Normal ' + ' '.join(map(str, rect))] + extra + [
         f'WINDOW 0 0 {rect[1] - 16} Bottom 2', f'WINDOW 3 0 {rect[3] + 16} Top 2',
         f'SYMATTR Value {name}', 'SYMATTR Prefix X', 'SYMATTR ModelFile MUX507.lib',
         f'SYMATTR SpiceLine {mux_model.MC_LINE}', f'SYMATTR SpiceLine2 {line2}',
         f'SYMATTR Description TI MUX507 transmission-gate model fitted to SBAS803A, uniform Monte Carlo defaults (SPICA)']
    for i, pin in enumerate(order, 1):
        x, y, side = pos[pin]
        L += [f'PIN {x} {y} {side} 8', f'PINATTR PinName {pin}', f'PINATTR SpiceOrder {i}']
    open(path, 'w', newline='').write('\r\n'.join(L) + '\r\n')


order8 = ['VDD', 'DB'] + [f'S{k}B' for k in range(8, 0, -1)] + ['GND', 'A2', 'A1', 'A0', 'EN'] + [f'S{k}A' for k in range(1, 9)] + ['VSS', 'DA']
pos8 = {f'S{k}A': (-160, -336 + 32 * k, 'LEFT') for k in range(1, 9)}
pos8.update({f'S{k}B': (-160, -48 + 32 * k, 'LEFT') for k in range(1, 9)})
pos8.update({'DA': (160, -192, 'RIGHT'), 'DB': (160, 96, 'RIGHT'), 'VDD': (0, -384, 'TOP'),
             'A0': (-112, 384, 'BOTTOM'), 'A1': (-80, 384, 'BOTTOM'), 'A2': (-48, 384, 'BOTTOM'), 'EN': (-16, 384, 'BOTTOM'),
             'GND': (64, 384, 'BOTTOM'), 'VSS': (112, 384, 'BOTTOM')})
write(sys.argv[1] + '/MUX507.asy', 'MUX507', order8, pos8, (-160, -384, 160, 384),
      ['LINE Normal -96 -192 160 -192', 'LINE Normal -96 96 160 96', 'TEXT 0 -40 Center 2 MUX507', 'TEXT 0 -8 Center 1 8:1 diff / dual 8:1'],
      mux_model.mc_mismatch(8))

order2 = ['VDD', 'VSS', 'GND', 'EN', 'A0', 'S1A', 'S2A', 'S1B', 'S2B', 'DA', 'DB']
pos2 = {'S1A': (-128, -144, 'LEFT'), 'S2A': (-128, -112, 'LEFT'), 'S1B': (-128, 48, 'LEFT'), 'S2B': (-128, 80, 'LEFT'),
        'DA': (128, -128, 'RIGHT'), 'DB': (128, 64, 'RIGHT'), 'VDD': (0, -192, 'TOP'),
        'A0': (-64, 192, 'BOTTOM'), 'EN': (-32, 192, 'BOTTOM'), 'GND': (32, 192, 'BOTTOM'), 'VSS': (64, 192, 'BOTTOM')}
write(sys.argv[1] + '/MUX507_2CH.asy', 'MUX507_2CH', order2, pos2, (-128, -192, 128, 192),
      ['LINE Normal -64 -128 128 -128', 'LINE Normal -64 64 128 64', 'TEXT 0 -24 Center 2 MUX507', 'TEXT 0 8 Center 1 ch 1-2 (A0, EN)'],
      mux_model.mc_mismatch(2))


# MUX507_2TO1: classic mux trapezoid (long side = inputs, short side = output, select on the slanted bottom)
L = ['Version 4', 'SymbolType BLOCK',
     'LINE Normal -64 -96 64 -48', 'LINE Normal 64 -48 64 48', 'LINE Normal 64 48 -64 96', 'LINE Normal -64 96 -64 -96',
     'LINE Normal -96 -48 -64 -48', 'LINE Normal -96 48 -64 48', 'LINE Normal 64 0 96 0',
     'LINE Normal -32 -84 -32 -128', 'LINE Normal -32 84 -32 128', 'LINE Normal 32 60 32 112',
     'TEXT -56 -48 Left 1 0', 'TEXT -56 48 Left 1 1', 'TEXT 56 0 Right 1 D',
     'TEXT -8 0 Center 1 MUX', 'TEXT -24 -116 Left 0 VCC', 'TEXT -24 116 Left 0 VSS', 'TEXT 40 100 Left 0 EN',
     'TEXT -100 -56 Right 0 S0', 'TEXT -100 40 Right 0 S1',
     'WINDOW 0 16 -80 Left 2', 'WINDOW 3 16 -112 Left 1',
     'SYMATTR Value MUX507_2TO1', 'SYMATTR Prefix X', 'SYMATTR ModelFile MUX507.lib',
     f'SYMATTR SpiceLine {mux_model.MC_LINE}', 'SYMATTR SpiceLine2 M0={flat(0.036)} M1={flat(0.036)}',
     'SYMATTR Description TI MUX507 as one 2:1 switch, EN = 0 -> S0, EN = 1 -> S1 (SPICA, uniform Monte Carlo defaults)']
for i, (pin, x, y) in enumerate([('VCC', -32, -128), ('VSS', -32, 128), ('EN', 32, 112), ('S0', -96, -48), ('S1', -96, 48), ('D', 96, 0)], 1):
    L += [f'PIN {x} {y} NONE 0', f'PINATTR PinName {pin}', f'PINATTR SpiceOrder {i}']
open(sys.argv[1] + '/MUX507_2TO1.asy', 'w', newline='').write('\r\n'.join(L) + '\r\n')
print('ok')
