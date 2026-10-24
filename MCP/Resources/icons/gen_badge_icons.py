# -*- coding: utf-8 -*-
"""生成整地圖說 ribbon 「色磚 Badge」圖示（Alex 選定）。
高倍取樣(512)再縮 32/16，透明背景 PNG。輸出到 MCP/Resources/icons/。
2026-07-10 Ribbon 整併：主工作流只剩「新整地方案／方案總覽」兩顆核心按鈕（編號 1~2），
其餘（複製樓板／板高程斜板／邊坡坡率／平衡高程試算）為備板工具不編號。
整地檢視／六級坡度／比較出圖／各別出圖已併入「方案總覽」清單動作，不再需要獨立圖示。"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join("MCP", "Resources", "icons")
os.makedirs(OUT, exist_ok=True)

m = 8                      # supersample: 64-grid -> 512px
GREEN=(110,139,61); BLUE=(62,119,168); TEAL=(62,142,138)
AMBER=(200,135,59); WHITE=(255,255,255)
SLATE=(90,107,123); BROWN=(138,106,71)

def blend(bg, a):          # white over bg, pre-composited to opaque
    return tuple(int(bg[i]*(1-a)+255*a) for i in range(3))
NEW_SEC = blend(GREEN, 0.65)
EDGE_SOFT = blend(BROWN, 0.45)
DUPLICATE_BACK = blend(TEAL, 0.55)

def sc(box):  return [v*m for v in box]
def scp(pl):  return [(x*m, y*m) for x, y in pl]

def xf(pts, dx=0.0, dy=0.0, s=1.0, cx=32.0, cy=32.0):
    """縮放＋平移一組座標點（繞 cx,cy 縮放後再位移），用來讓既有圖案主體讓出角章位置。"""
    return [((x-cx)*s+cx+dx, (y-cy)*s+cy+dy) for x, y in pts]

def bx(box, dx=0.0, dy=0.0, s=1.0, cx=32.0, cy=32.0):
    """同 xf，但作用於 [x0,y0,x1,y1] 框（給 ellipse/rounded_rectangle 用）。"""
    (nx0,ny0),(nx1,ny1) = xf([(box[0],box[1]),(box[2],box[3])], dx,dy,s,cx,cy)
    return [nx0,ny0,nx1,ny1]

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
    # 三行條列主體整組右下移＋略縮，讓出左上角圓章位置。
    T = dict(dx=6, dy=6, s=0.82, cx=32, cy=32)
    for cy in (22,32,42):
        d.ellipse(sc(bx([17,cy-3,23,cy+3], **T)), fill=WHITE)
    d.rounded_rectangle(sc(bx([27,19.5,48,24.5], **T)), radius=2.5*T["s"]*m, fill=WHITE)
    d.rounded_rectangle(sc(bx([27,29.5,48,34.5], **T)), radius=2.5*T["s"]*m, fill=WHITE)
    d.rounded_rectangle(sc(bx([27,39.5,42,44.5], **T)), radius=2.5*T["s"]*m, fill=WHITE)
    return img

def ic_duplicate():
    img,d = canvas(); tile(d, TEAL)
    # 複製樓板：後板（半透明白）左上、前板（實白）右下疊出，呼應「複製」意象。
    d.rounded_rectangle(sc([11,11,41,33]), radius=3*m, fill=DUPLICATE_BACK)
    d.rounded_rectangle(sc([22,29,53,51]), radius=3*m, fill=WHITE)
    stroke(d, scp([(27,40),(48,40)]), TEAL, int(2.2*m))
    stroke(d, scp([(27,46),(42,46)]), TEAL, int(2.2*m))
    return img

def ic_balance():
    img,d = canvas(); tile(d, AMBER)
    # 平衡高程試算：天平——支柱、橫樑、兩端吊線與秤盤，呼應二分法試算挖填平衡的意象。
    stroke(d, scp([(32,16),(32,48)]), WHITE, int(3.2*m))
    stroke(d, scp([(16,24),(48,24)]), WHITE, int(3.2*m))
    d.rounded_rectangle(sc([20,48,44,54]), radius=1.5*m, fill=WHITE)
    stroke(d, scp([(16,24),(16,36)]), WHITE, int(2.2*m))
    stroke(d, scp([(48,24),(48,36)]), WHITE, int(2.2*m))
    d.arc(sc([9,33,23,41]), start=0, end=180, fill=WHITE, width=int(2.4*m))
    d.arc(sc([41,33,55,41]), start=0, end=180, fill=WHITE, width=int(2.4*m))
    return img

def ic_elev():
    img,d = canvas(); tile(d, SLATE)
    # 三條水平高程線＋上抬箭頭：板高程/斜板。
    stroke(d, scp([(14,44),(50,44)]), WHITE, int(4*m))
    stroke(d, scp([(14,34),(42,34)]), blend(SLATE,0.7), int(4*m))
    stroke(d, scp([(14,24),(34,24)]), blend(SLATE,0.45), int(4*m))
    stroke(d, scp([(44,30),(44,14)]), WHITE, int(4*m))
    d.polygon(scp([(44,9),(38.5,17),(49.5,17)]), fill=WHITE)
    return img

def ic_edge():
    img,d = canvas(); tile(d, BROWN)
    # 板輪廓＋一條被強調的邊（臨路邊例外坡率）。
    outline = [(16,18),(48,18),(48,40),(32,50),(16,40)]
    d.polygon(scp(outline), outline=EDGE_SOFT, width=int(3.5*m))
    stroke(d, scp([(16,40),(32,50)]), WHITE, int(6*m))   # 強調邊
    return img

ICONS = {"new":ic_new, "overview":ic_overview, "duplicate":ic_duplicate,
         "elev":ic_elev, "edge":ic_edge, "balance":ic_balance}

# 核心入口編號（1~2）；複製樓板／板高程斜板／邊坡坡率／平衡高程試算為備板工具不編號。
NUMBERED = {"new":(1,GREEN), "overview":(2,BLUE)}

_FONT_CANDIDATES = [
    r"C:\Windows\Fonts\msjhbd.ttc",
    r"C:\Windows\Fonts\arialbd.ttf",
    r"C:\Windows\Fonts\segoeuib.ttf",
    r"C:\Windows\Fonts\calibrib.ttf",
]
_font_cache = {}
FONT_PATH_USED = None

def load_bold_font(size):
    global FONT_PATH_USED
    if size in _font_cache:
        return _font_cache[size]
    for path in _FONT_CANDIDATES:
        if os.path.exists(path):
            try:
                font = ImageFont.truetype(path, size)
                _font_cache[size] = font
                FONT_PATH_USED = FONT_PATH_USED or path
                return font
            except Exception:
                continue
    font = ImageFont.load_default()
    _font_cache[size] = font
    FONT_PATH_USED = FONT_PATH_USED or "PIL default (no bold TTF found)"
    return font

def badge(img, d, tile_color, number, cx=18.0, cy=18.0, r=10.2, ring=1.8):
    """左上角融合式編號圓章：tile 同色細環 + 白色實心圓 + tile 同色粗體數字（textbbox 置中）。"""
    d.ellipse(sc([cx-r-ring, cy-r-ring, cx+r+ring, cy+r+ring]), fill=tile_color)
    d.ellipse(sc([cx-r, cy-r, cx+r, cy+r]), fill=WHITE)
    size = int(r*1.7*m)
    font = load_bold_font(size)
    text = str(number)
    bbox = d.textbbox((0,0), text, font=font)
    tw, th = bbox[2]-bbox[0], bbox[3]-bbox[1]
    tx = cx*m - tw/2 - bbox[0]
    ty = cy*m - th/2 - bbox[1]
    d.text((tx,ty), text, font=font, fill=tile_color)

rendered = {}
for key, fn in ICONS.items():
    big = fn()
    if key in NUMBERED:
        num, tcolor = NUMBERED[key]
        badge(big, ImageDraw.Draw(big), tcolor, num)
    rendered[key] = big
    for size in (32, 16):
        small = big.resize((size, size), Image.LANCZOS)
        small.save(os.path.join(OUT, f"{key}_{size}.png"))

# 預覽總圖（給使用者看，不入 DLL）——按 Ribbon 實際左到右順序排列。
order = ["new","overview","duplicate","elev","edge","balance"]
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
print("font used:", FONT_PATH_USED)
print("files:", sorted(os.listdir(OUT)))
