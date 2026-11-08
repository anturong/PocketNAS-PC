#!/usr/bin/env python3
"""从权威位图重建 PocketNAS 标志的矢量源（brand/logo/*.svg）。

为什么要有这个脚本：
    仓库里唯一的权威是 src/PocketNAS.PC/Assets/brand/pocketnas_256.png。
    手机端 app/src/main/res/drawable/app_icon.xml 是 **自适应图标的前景层**，
    按 Android 规范留了安全边距，p 只占画布一半，与 PC 主标志不是同一构图，
    不能直接当主标志的矢量源用（照抄会让所有图标缩水一圈）。

依赖：
    pip install potracer pillow numpy

用法：
    python tools/vectorize-logo.py            # 重建 5 个 SVG
    python tools/vectorize-logo.py --check    # 只打印比对结果，不写文件
"""
from __future__ import annotations

import argparse
import pathlib
import sys

import numpy as np
import potrace
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src" / "PocketNAS.PC" / "Assets" / "brand" / "pocketnas_256.png"
OUT = ROOT / "brand" / "logo"

VIEW = 108.0          # 输出 viewBox 边长，全站统一
ORANGE = "#FD692D"
BG_SOLID = "#1A1A22"
BG_A = "#1E1E28"
BG_B = "#15151D"


def load_masks():
    im = Image.open(SRC).convert("RGBA")
    px = np.array(im).astype(int)
    r, g, b, a = px[..., 0], px[..., 1], px[..., 2], px[..., 3]
    side = im.size[0]
    alpha = a > 110
    orange = (a > 110) & (r > 170) & (g > 55) & (g < 170) & (b < 120)
    white = (a > 110) & (r > 235) & (g > 235) & (b > 235)
    return side, alpha, orange, white


def corner_radius(alpha: np.ndarray) -> float:
    """圆角半径用面积法反解：圆角矩形面积 = W·H − (4−π)r²。

    直接读顶行的第一个像素会被抗锯齿带偏（顶行其实还压着一段圆弧），
    面积法在 256px 图上稳定得到 56px（21.9%），与文档记的 22% 吻合。
    """
    ys, xs = np.where(alpha)
    if not len(xs):
        raise SystemExit("alpha 通道全空，源图有问题")
    w = xs.max() - xs.min() + 1
    h = ys.max() - ys.min() + 1
    area = int(alpha.sum())
    r2 = (w * h - area) / (4 - np.pi)
    return float(np.sqrt(max(r2, 0.0)))


def curve_to_path(path_obj, scale: float, side: int, only_outer: bool = True) -> str:
    """把 potrace 结果转成 SVG path d。

    potracer 对这里的两色图会输出两条顶层曲线：
      curve0 = 背景（bbox 覆盖整幅画布），curve1 = 橙色 p。
    直接全画会把底色铺满、p 变成反色，所以先按 bbox 把「整幅」那条剔掉。
    """
    out = []

    def emit(curve):
        sp = curve.start_point
        out.append(f"M{sp.x * scale:.2f},{sp.y * scale:.2f}")
        for seg in curve.segments:
            if seg.is_corner:
                c = seg.c
                out.append(f"L{c.x * scale:.2f},{c.y * scale:.2f}")
            else:
                c1, c2, ep = seg.c1, seg.c2, seg.end_point
                out.append(
                    f"C{c1.x * scale:.2f},{c1.y * scale:.2f} "
                    f"{c2.x * scale:.2f},{c2.y * scale:.2f} "
                    f"{ep.x * scale:.2f},{ep.y * scale:.2f}"
                )
        out.append("Z")

    def bbox(curve):
        xs, ys = [curve.start_point.x], [curve.start_point.y]
        for seg in curve.segments:
            e = seg.c if seg.is_corner else seg.end_point
            xs.append(e.x)
            ys.append(e.y)
        return min(xs), min(ys), max(xs), max(ys)

    for curve in path_obj.curves:
        x0, y0, x1, y1 = bbox(curve)
        if (x1 - x0) > side * 0.9 and (y1 - y0) > side * 0.9:
            continue  # 整幅画布那条＝背景，跳过
        # 主标志的白点是「画上去的白圆」而不是洞，所以只取外轮廓；
        # 洞留给单独的 <circle> 表达（否则深底上会漏出底色）。
        emit(curve)
        if not only_outer:
            for child in curve.children or []:
                emit(child)
    return "".join(out)


def trace(mask: np.ndarray, side: int):
    bmp = potrace.Bitmap(mask)
    return bmp.trace(turdsize=4, alphamax=1.0, opticurve=1, opttolerance=0.2)


def measure_dot(white: np.ndarray, side: int) -> tuple[float, float, float]:
    ys, xs = np.where(white)
    if not len(xs):
        raise SystemExit("没找到白点，阈值需要调整")
    cx, cy = xs.mean(), ys.mean()
    area = len(xs)
    r = (area / np.pi) ** 0.5
    return cx, cy, r


def svg(doc: str, body: str, title: str) -> str:
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        f"{doc}"
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {VIEW:g} {VIEW:g}" '
        f'width="{VIEW:g}" height="{VIEW:g}" role="img" aria-label="PocketNAS">\n'
        f"  <title>{title}</title>\n"
        f"{body}</svg>\n"
    )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只打印测量值")
    args = ap.parse_args()

    side, alpha, orange, white = load_masks()
    scale = VIEW / side
    radius = corner_radius(alpha) * scale
    cx, cy, dr = measure_dot(white, side)
    d_p = curve_to_path(trace(orange, side), scale, side, only_outer=True)

    print(f"源图 {side}px  缩放 {scale:.5f}")
    print(f"圆角 {radius:.2f}/{VIEW:g} = {radius / VIEW:.1%}")
    print(f"白点 圆心 ({cx * scale:.2f},{cy * scale:.2f}) r={dr * scale:.2f}")
    ys, xs = np.where(orange)
    print(
        "p bbox "
        f"{xs.min() * scale:.1f},{ys.min() * scale:.1f} → "
        f"{xs.max() * scale:.1f},{ys.max() * scale:.1f}"
    )
    print(f"path 长度 {len(d_p)} 字符")

    if args.check:
        return 0

    OUT.mkdir(parents=True, exist_ok=True)
    r = radius
    sq = (
        f"M{r:g},0h{VIEW - 2 * r:g}a{r:g},{r:g} 0 0 1 {r:g},{r:g}"
        f"v{VIEW - 2 * r:g}a{r:g},{r:g} 0 0 1 -{r:g},{r:g}"
        f"h-{VIEW - 2 * r:g}a{r:g},{r:g} 0 0 1 -{r:g},-{r:g}"
        f"v-{VIEW - 2 * r:g}a{r:g},{r:g} 0 0 1 {r:g},-{r:g}z"
    )
    p = f'  <path fill="{ORANGE}" d="{d_p}"/>\n'
    dot = f'  <circle fill="#FFFFFF" cx="{cx * scale:.2f}" cy="{cy * scale:.2f}" r="{dr * scale:.2f}"/>\n'
    dot_dark = dot.replace("#FFFFFF", BG_SOLID)

    # 单色版：节点要「挖空」而不是画白点，否则放在浅底上会是一块白斑。
    # 做法是把同样的圆再写成一条子路径，交给 fill-rule=evenodd 挖洞。
    dcx, dcy, drv = cx * scale, cy * scale, dr * scale
    d_mono = (
        d_p
        + f"M{dcx - drv:.2f},{dcy:.2f}"
        f"a{drv:.2f},{drv:.2f} 0 1,0 {2 * drv:.2f},0"
        f"a{drv:.2f},{drv:.2f} 0 1,0 {-2 * drv:.2f},0Z"
    )

    docs = {
        "pocketnas-logo.svg": (
            "<!-- PocketNAS 主标志 · 渐变深底版（默认版）\n"
            f"     {VIEW:g}×{VIEW:g} 网格，圆角 {r:.1f}。橙色 p 为品牌记号，碗中白点为 NAS 节点。\n"
            "     由 tools/vectorize-logo.py 从 pocketnas_256.png 反推，形状与生产位图一致；\n"
            "     要改形状请改位图母版后重跑脚本，不要手改路径坐标。 -->\n",
            f'  <defs>\n    <linearGradient id="pnsBg" x1="0" y1="0" x2="{VIEW:g}" y2="{VIEW:g}" '
            f'gradientUnits="userSpaceOnUse">\n'
            f'      <stop offset="0" stop-color="{BG_A}"/>\n'
            f'      <stop offset="1" stop-color="{BG_B}"/>\n'
            f"    </linearGradient>\n  </defs>\n"
            f'  <path fill="url(#pnsBg)" d="{sq}"/>\n' + p + dot,
            "PocketNAS",
        ),
        "pocketnas-logo-flat.svg": (
            "<!-- PocketNAS 主标志 · 纯色深底版（小尺寸、印刷、单色场景用）\n"
            "     与渐变版唯一差别：底色是纯色，没有渐变。\n"
            "     16–32px 渲染时优先用这张，避免渐变在低分辨率下显脏。 -->\n",
            f'  <path fill="{BG_SOLID}" d="{sq}"/>\n' + p + dot,
            "PocketNAS（纯色底）",
        ),
        "pocketnas-mark-on-dark.svg": (
            "<!-- PocketNAS 记号 · 深底版（无背景方块）\n"
            "     橙色 p + 白色节点，直接放在 #15151D / #1A1A22 / 深色照片上。\n"
            "     背景透明，由使用方提供底色。 -->\n",
            p + dot,
            "PocketNAS 记号（深底）",
        ),
        "pocketnas-mark-on-light.svg": (
            "<!-- PocketNAS 记号 · 浅底版（无背景方块）\n"
            "     橙色 p + 深色节点。用于白纸、浅灰底、文档页眉。\n"
            "     节点必须换深色，否则白点落在白纸上会消失、碗变成一个实心团。 -->\n",
            p + dot_dark,
            "PocketNAS 记号（浅底）",
        ),
        "pocketnas-mark-mono.svg": (
            "<!-- PocketNAS 记号 · 单色版（currentColor）\n"
            "     整块只有一个颜色，节点是「挖空」不是画白点——放在任何底上都成立，\n"
            "     也适合激光刻字、水印、单色印刷、favicon 的 alpha 蒙版。\n"
            "     默认给橙色，外部 CSS 的 color（如 .logo{color:#fff}）可以覆盖。 -->\n",
            '  <style>svg{color:' + ORANGE + '}</style>\n'
            f'  <path fill="currentColor" fill-rule="evenodd" d="{d_mono}"/>\n',
            "PocketNAS 记号（单色）",
        ),
    }
    for name, (doc, body, title) in docs.items():
        (OUT / name).write_text(svg(doc, body, title), encoding="utf-8")
        print("wrote", name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
