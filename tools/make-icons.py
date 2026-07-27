#!/usr/bin/env python3
"""Draws Claude Buddy's icons so the repo doesn't need binary art checked in
by hand. Regenerate with:

    python3 tools/make-icons.py

Outputs (all PNG, RGBA, straight alpha):
  Assets/tray-idle.png        menu-bar / notification-area icon, one per state
  Assets/tray-generating.png
  Assets/tray-waiting.png
  Assets/appicon-1024.png     source art for the .app bundle's .icns

Pure stdlib (zlib + struct) — no Pillow, so it runs on a stock macOS python3.
Everything is drawn by supersampling signed distance fields, which is plenty
for circles and gets us clean edges at any size.
"""

import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(HERE, "Assets")

# Same three colors as OrbWindow.axaml.cs — keep them in sync by hand.
IDLE = (0x5B, 0x7A, 0x94)
GENERATING = (0x8B, 0x6F, 0xD1)
WAITING = (0xE8, 0x98, 0x3B)

SS = 4  # supersampling factor per axis


def write_png(path, size, pixels):
    """pixels: flat list of (r, g, b, a) tuples, row-major, length size*size."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter type 0 (None) per scanline
        for x in range(size):
            r, g, b, a = pixels[y * size + x]
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        out = struct.pack(">I", len(data)) + tag + data
        return out + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8-bit RGBA
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )
    with open(path, "wb") as fh:
        fh.write(png)


def render(size, shade):
    """shade(u, v) -> (r, g, b, alpha 0..1) in a -1..1 coordinate square."""
    pixels = []
    inv = 1.0 / size
    for y in range(size):
        for x in range(size):
            acc_r = acc_g = acc_b = acc_a = 0.0
            for sy in range(SS):
                for sx in range(SS):
                    u = ((x + (sx + 0.5) / SS) * inv) * 2.0 - 1.0
                    v = ((y + (sy + 0.5) / SS) * inv) * 2.0 - 1.0
                    r, g, b, a = shade(u, v)
                    acc_r += r * a
                    acc_g += g * a
                    acc_b += b * a
                    acc_a += a
            n = SS * SS
            a = acc_a / n
            if a <= 0.0005:
                pixels.append((0, 0, 0, 0))
            else:
                # un-premultiply back to straight alpha
                pixels.append(
                    (
                        min(255, max(0, round(acc_r / acc_a))),
                        min(255, max(0, round(acc_g / acc_a))),
                        min(255, max(0, round(acc_b / acc_a))),
                        min(255, max(0, round(a * 255))),
                    )
                )
    return pixels


def tray_shader(color):
    """A ring, not a disc: reads as a distinct shape at 16-22pt on both light
    and dark menu bars, where a solid blob just looks like a smudge."""
    r, g, b = color
    outer = 0.86
    inner = 0.44

    def shade(u, v):
        d = math.hypot(u, v)
        if d > outer or d < inner:
            # faint core so the middle isn't a hole at tiny sizes
            if d < inner:
                return (r, g, b, 0.30)
            return (0, 0, 0, 0.0)
        return (r, g, b, 1.0)

    return shade


def appicon_shader():
    """Orb with a glow, padded like a real macOS icon (art fills ~80%)."""
    core = 0.52
    glow = 0.82

    def shade(u, v):
        d = math.hypot(u, v)
        if d <= core:
            # vertical shade from violet toward slate for a little depth
            t = (v / core + 1.0) * 0.5
            r = GENERATING[0] + (IDLE[0] - GENERATING[0]) * t
            g = GENERATING[1] + (IDLE[1] - GENERATING[1]) * t
            b = GENERATING[2] + (IDLE[2] - GENERATING[2]) * t
            # specular highlight up and to the left
            h = max(0.0, 1.0 - math.hypot(u + 0.18, v + 0.20) / 0.34)
            k = h * h * 0.45
            return (r + (255 - r) * k, g + (255 - g) * k, b + (255 - b) * k, 1.0)
        if d <= glow:
            t = (d - core) / (glow - core)
            return (*GENERATING, (1.0 - t) ** 2 * 0.45)
        return (0, 0, 0, 0.0)

    return shade


def main():
    os.makedirs(ASSETS, exist_ok=True)

    # 64px so the menu bar has retina pixels to downsample from.
    for name, color in (("idle", IDLE), ("generating", GENERATING), ("waiting", WAITING)):
        path = os.path.join(ASSETS, f"tray-{name}.png")
        write_png(path, 64, render(64, tray_shader(color)))
        print("wrote", os.path.relpath(path, HERE))

    path = os.path.join(ASSETS, "appicon-1024.png")
    write_png(path, 1024, render(1024, appicon_shader()))
    print("wrote", os.path.relpath(path, HERE))


if __name__ == "__main__":
    main()
