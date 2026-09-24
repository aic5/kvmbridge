"""Generate original 144px Stream Deck icons using only the Python standard library."""
from pathlib import Path
import struct,zlib
OUT=Path(__file__).resolve().parents[1]/'streamdeck/icons'
DIGITS=['010/110/010/010/111','110/001/010/100/111','110/001/010/001/110','101/101/111/001/001']
COLORS=[(64,164,255),(62,203,152),(246,181,64),(178,128,255)]

def png(path,pixels,w,h):
    def chunk(t,b): return struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b)&0xffffffff)
    raw=b''.join(b'\0'+bytes(pixels[y*w*3:(y+1)*w*3]) for y in range(h))
    path.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(raw,9))+chunk(b'IEND',b''))

OUT.mkdir(parents=True,exist_ok=True)
all_pixels=[]
for n,(digit,color) in enumerate(zip(DIGITS,COLORS),1):
    pixels=bytearray([17,24,39]*144*144)
    rects=[]
    def rect(x,y,w,h,c):
        rects.append((x,y,w,h,c))
        for yy in range(y,y+h):
            for xx in range(x,x+w): pixels[(yy*144+xx)*3:(yy*144+xx)*3+3]=bytes(c)
    rect(18,20,108,88,color);rect(24,26,96,76,(17,24,39))
    rect(66,108,12,12,color);rect(45,120,54,6,color)
    for y,row in enumerate(digit.split('/')):
        for x,bit in enumerate(row):
            if bit=='1': rect(54+x*12,34+y*12,11,11,(245,248,255))
    png(OUT/f'computer-{n}.png',pixels,144,144)
    svg='<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144"><rect width="144" height="144" fill="#111827"/>'
    for x,y,w,h,c in rects: svg+=f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="#{c[0]:02x}{c[1]:02x}{c[2]:02x}"/>'
    (OUT/f'computer-{n}.svg').write_text(svg+'</svg>\n')
    all_pixels.append(pixels)
preview=bytearray()
for y in range(144):
    for p in all_pixels: preview.extend(p[y*144*3:(y+1)*144*3])
png(OUT/'preview.png',preview,576,144)
print('Generated four original icons and contact sheet')
