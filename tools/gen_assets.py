"""生成 KeyDisplay.Widget 所需的全部 PNG 资源。

用法:
    python tools\\gen_assets.py

输出:
    KeyDisplay.Widget\\Assets\\*.png   (全部 UWP 图标 / 磁贴 / 启动画面)
    KeyDisplay.Widget\\GameBar\\KeyDisplayMain.png  (Game Bar 小组件图标)
"""

import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "KeyDisplay.Widget", "Assets")
GAMEBAR = os.path.join(ROOT, "KeyDisplay.Widget", "GameBar")

WHITE = (255, 255, 255, 255)
BLACK = (0, 0, 0, 255)
DARK_BG = (13, 13, 13, 255)


def _scaled(base, scale):
    """四舍五入到最近像素（与 UWP 清单校验一致，半值进位）。"""
    return max(1, int(base * scale / 100 + 0.5))


def draw_key(draw, w, h, bg=None):
    """在 w×h 画布中央绘制键帽图形：白色键帽 + 黑色键面。"""
    if bg is not None:
        draw.rectangle([0, 0, w - 1, h - 1], fill=bg)
    s = min(w, h)
    pad = s * 0.15
    keycap = [pad, pad, w - pad, h - pad]
    r = (keycap[2] - keycap[0]) * 0.22
    draw.rounded_rectangle(keycap, radius=r, fill=WHITE)
    inner_pad = s * 0.30
    inner = [inner_pad, inner_pad, w - inner_pad, h - inner_pad]
    ri = (inner[2] - inner[0]) * 0.22
    draw.rounded_rectangle(inner, radius=ri, fill=BLACK)


def make_square(name, base, scales, bg=None):
    for s in scales:
        px = _scaled(base, s)
        img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
        draw_key(ImageDraw.Draw(img), px, px, bg=bg)
        img.save(os.path.join(ASSETS, "%s.scale-%d.png" % (name, s)))


def make_wide(name, w, h, scales, bg=None):
    """宽徽标（如 Wide310x150Logo）为矩形，非正方形。"""
    for s in scales:
        px, py = _scaled(w, s), _scaled(h, s)
        img = Image.new("RGBA", (px, py), (0, 0, 0, 0))
        draw_key(ImageDraw.Draw(img), px, py, bg=bg)
        img.save(os.path.join(ASSETS, "%s.scale-%d.png" % (name, s)))


def make_targetsize(name, sizes):
    for px in sizes:
        img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
        draw_key(ImageDraw.Draw(img), px, px)
        img.save(os.path.join(ASSETS, "%s.targetsize-%d.png" % (name, px)))


def make_altform_unplated_targetsize(name, sizes):
    """官方命名约定：Square44x44Logo.altform-unplated_targetsize-<N>.png"""
    for px in sizes:
        img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
        draw_key(ImageDraw.Draw(img), px, px)
        img.save(os.path.join(ASSETS, "%s.altform-unplated_targetsize-%d.png" % (name, px)))


def make_targetsize_24_altform():
    """24px 特殊命名：Square44x44Logo.targetsize-24_altform-unplated.png"""
    img = Image.new("RGBA", (24, 24), (0, 0, 0, 0))
    draw_key(ImageDraw.Draw(img), 24, 24)
    img.save(os.path.join(ASSETS, "Square44x44Logo.targetsize-24_altform-unplated.png"))


def main():
    os.makedirs(ASSETS, exist_ok=True)
    os.makedirs(GAMEBAR, exist_ok=True)
    scales = [100, 125, 150, 200, 400]

    make_square("Square150x150Logo", 150, scales)
    make_square("Square44x44Logo", 44, scales)
    make_square("StoreLogo", 50, scales)
    make_wide("Wide310x150Logo", 310, 150, scales)
    make_square("SmallTile", 71, scales)
    make_square("LargeTile", 310, scales)
    make_square("LockScreenLogo", 24, [200])

    make_targetsize("Square44x44Logo", [16, 24, 32, 48, 256])
    make_altform_unplated_targetsize("Square44x44Logo", [16, 32, 48, 256])
    make_targetsize_24_altform()

    for s in scales:
        px, py = _scaled(620, s), _scaled(300, s)
        img = Image.new("RGBA", (px, py), (0, 0, 0, 0))
        draw_key(ImageDraw.Draw(img), px, py, bg=DARK_BG)
        img.save(os.path.join(ASSETS, "SplashScreen.scale-%d.png" % s))

    gb = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw_key(ImageDraw.Draw(gb), 64, 64)
    gb.save(os.path.join(GAMEBAR, "KeyDisplayMain.png"))

    total = 0
    for root, _, files in os.walk(ASSETS):
        total += len(files)
    total += 1  # GameBar
    print("Generated %d assets." % total)


if __name__ == "__main__":
    main()