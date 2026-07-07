# -*- coding: utf-8 -*-
"""生成整地圖說 ribbon 「色磚 Badge」圖示（Alex 選定）。
高倍取樣(512)再縮 32/16，透明背景 PNG。輸出到 MCP/Resources/icons/。"""
import os
from PIL import Image, ImageDraw

OUT = os.path.join("MCP", "Resources", "icons")
os.makedirs(OUT, exist_ok=True)

m = 8                      # supersample: 64-grid -> 512px
GREEN=(110,139,61); BLUE=(62,119,168); TEAL=(62,142,138)
AMBER=(200,135,59); PURPLE=(122,107,158); WHITE=(255,255,255)

def blend(bg, a):          # white over bg, pre-composited to opaque
    return tuple(int(bg[i]*(1-a)+255*a) for i in range(3))
NEW_SEC = blend(GREEN, 0.65)
VIEW_WAVE = blend(TEAL, 0.70)
SET_BACK = blend(PURPLE, 0.55)

def sc(box):  return [v*m for v in box]
def scp(pl):  return [(x*m, y*m) for x, y in pl]

def stroke(d, pts, color, w):     # polyline with round joints + round caps
    d.line(pts, fill=color, width=w, joint="curve")
    r = w/2
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x-r, y-r, x+r, y+r], fill=color)

def qbez(p0, c, p1, n=48):
    out=[]
    for i in range(n+1):
        t=i/n; u=1-t
        out.append((u*u*p0[0]+2*u*t*c[0]+t*t*p1[0],
                    u*u*p0[1]+2*u*t*c[1]+t*t*p1[1]))
    return out

def canvas():
    img = Image.new("RGBA", (64*m, 64*m), (0,0,0,0))
    return img, ImageDraw.Draw(img)

def tile(d, color):
    d.rounded_rectangle(sc([6,6,58,58]), radius=15*m, fill=color)

def ic_new():
    img,d = canvas(); tile(d, GREEN)
    stroke(d, scp([(14,44),(26,44),(36,30),(50,30)]), WHITE, int(4.5*m))
    stroke(d, scp([(14,50),(42,50)]), NEW_SEC, int(4.5*m))
    d.ellipse(sc([37,11,53,27]), fill=WHITE)
    stroke(d, scp([(45,14.5),(45,23.5)]), GREEN, int(3*m))
    stroke(d, scp([(40.5,19),(49.5,19)]), GREEN, int(3*m))
    return img

def ic_overview():
    img,d = canvas(); tile(d, BLUE)
    for cy in (22,32,42):
        d.ellipse(sc([17,cy-3,23,cy+3]), fill=WHITE)
    d.rounded_rectangle(sc([27,19.5,48,24.5]), radius=2.5*m, fill=WHITE)
    d.rounded_rectangle(sc([27,29.5,48,34.5]), radius=2.5*m, fill=WHITE)
    d.rounded_rectangle(sc([27,39.5,42,44.5]), radius=2.5*m, fill=WHITE)
    return img

def ic_view():
    img,d = canvas(); tile(d, TEAL)
    wave = qbez((14,40),(24,33),(32,38)) + qbez((32,38),(40,43),(50,37))
    stroke(d, scp(wave), VIEW_WAVE, int(4*m))
    eye = qbez((16,25),(32,13),(48,25)) + qbez((48,25),(32,37),(16,25))
    d.polygon(scp(eye), fill=WHITE)
    d.ellipse(sc([26.5,19.5,37.5,30.5]), fill=TEAL)
    return img

def ic_compare():
    img,d = canvas(); tile(d, AMBER)
    d.rounded_rectangle(sc([17,30,25,46]), radius=1.5*m, fill=WHITE)
    d.rounded_rectangle(sc([28,20,36,46]), radius=1.5*m, fill=WHITE)
    d.rounded_rectangle(sc([39,34,47,46]), radius=1.5*m, fill=WHITE)
    stroke(d, scp([(15,47),(49,47)]), WHITE, int(3*m))
    return img

def ic_set():
    img,d = canvas(); tile(d, PURPLE)
    d.rounded_rectangle(sc([24,15,48,47]), radius=3*m, fill=SET_BACK)
    d.rounded_rectangle(sc([16,20,40,52]), radius=3*m, fill=WHITE)
    stroke(d, scp([(33,20),(33,28),(40,28)]), PURPLE, int(2.5*m))
    stroke(d, scp([(21,34),(35,34)]), PURPLE, int(3*m))
    stroke(d, scp([(21,41),(35,41)]), PURPLE, int(3*m))
    return img

ICONS = {"new":ic_new, "overview":ic_overview, "view":ic_view,
         "compare":ic_compare, "set":ic_set}

rendered = {}
for key, fn in ICONS.items():
    big = fn()
    rendered[key] = big
    for size in (32, 16):
        small = big.resize((size, size), Image.LANCZOS)
        small.save(os.path.join(OUT, f"{key}_{size}.png"))

# 預覽總圖（給使用者看，不入 DLL）
order = ["new","overview","view","compare","set"]
pad, cell = 16, 84
W = pad + len(order)*(cell+pad)
H = pad + cell + pad
prev = Image.new("RGBA", (W, H), (237,234,227,255))
pd = ImageDraw.Draw(prev)
pd.rounded_rectangle([4,4,W-4,H-4], radius=10, outline=(207,200,185,255), width=2)
for i, key in enumerate(order):
    ic = rendered[key].resize((cell, cell), Image.LANCZOS)
    x = pad + i*(cell+pad)
    prev.alpha_composite(ic, (x, pad))
prev.save(os.path.join(OUT, "PREVIEW.png"))

print("OUT:", os.path.abspath(OUT))
print("files:", sorted(os.listdir(OUT)))
