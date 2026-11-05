"""Generate the Windows icon from the same simple shape as the WPF header."""
import math
import struct
import zlib
from pathlib import Path

GREEN = (33, 87, 68, 255)
MINT = (202, 243, 217, 255)
SHAPE = [(0.5, .22), (.57, .43), (.78, .5), (.57, .57),
         (.5, .78), (.43, .57), (.22, .5), (.43, .43)]

def pixel(x, y):
    dx, dy = max(abs(x-.5)-.23, 0), max(abs(y-.5)-.23, 0)
    if math.hypot(dx, dy) > .23:
        return (0, 0, 0, 0)
    inside = False
    for (a, b), (c, d) in zip(SHAPE, SHAPE[1:] + SHAPE[:1]):
        if (b > y) != (d > y) and x < (c-a)*(y-b)/(d-b)+a:
            inside = not inside
    return MINT if inside else GREEN

def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))

def png(size):
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            colors = [pixel((x+i)/size, (y+j)/size) for i in (.25,.75) for j in (.25,.75)]
            opaque = [v for v in colors if v[3]]
            rgb = [sum(c[k] for c in opaque)//len(opaque) for k in range(3)] if opaque else [0,0,0]
            rows.extend([*rgb, sum(c[3] for c in colors)//4])
    return (b'\x89PNG\r\n\x1a\n' +
            chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))

sizes = [16, 24, 32, 48, 64, 128, 256]
images = [png(size) for size in sizes]
offset = 6 + 16 * len(images)
icon = bytearray(struct.pack('<HHH', 0, 1, len(images)))
for size, data in zip(sizes, images):
    icon.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
Path(__file__).with_name('src').joinpath('Sens', 'Sens.ico').write_bytes(icon + b''.join(images))
