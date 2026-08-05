#!/usr/bin/env python3
"""Procedural PBR texture generation for Vixen's samples.

No PIL, no ImageMagick — PNG is written directly (IHDR/IDAT/IEND with zlib
scanlines), which keeps this dependency-free on any machine with numpy.
"""

import hashlib
import struct
import zlib

import numpy as np


# ── PNG ─────────────────────────────────────────────────────────────────────

def write_png(path, rgba):
    """rgba: (h, w, 4) uint8."""
    h, w, _ = rgba.shape
    raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as handle:
        handle.write(png)

    return len(png)


# ── noise ───────────────────────────────────────────────────────────────────

def _rng(seed):
    return np.random.default_rng(int(hashlib.sha256(seed.encode()).hexdigest()[:8], 16))


def value_noise(size, cells, seed):
    """Tileable smoothed value noise in [0, 1]."""
    g = _rng(seed).random((cells, cells)).astype(np.float32)
    # Wrap by tiling the lattice, then bilinear-sample with smoothstep weights.
    g = np.pad(g, ((0, 1), (0, 1)), mode="wrap")

    t = (np.arange(size, dtype=np.float32) + 0.5) * cells / size
    i = np.floor(t).astype(np.int32)
    f = t - i
    f = f * f * (3.0 - 2.0 * f)

    ix, iy = np.meshgrid(i, i, indexing="xy")
    fx, fy = np.meshgrid(f, f, indexing="xy")

    a = g[iy, ix]
    b = g[iy, ix + 1]
    c = g[iy + 1, ix]
    d = g[iy + 1, ix + 1]

    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def fbm(size, cells, octaves, seed, gain=0.5):
    out = np.zeros((size, size), dtype=np.float32)
    amplitude = 1.0
    total = 0.0

    for octave in range(octaves):
        out += amplitude * value_noise(size, cells * (2 ** octave), f"{seed}/{octave}")
        total += amplitude
        amplitude *= gain

    return out / total


def normalise(a):
    lo, hi = float(a.min()), float(a.max())
    return (a - lo) / (hi - lo) if hi > lo else np.zeros_like(a)


# ── maps ────────────────────────────────────────────────────────────────────

def normal_map(height, strength=2.0):
    """Tangent-space normal from a height field, +Y up, stored unsigned."""
    dx = np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)
    dy = np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)

    n = np.stack([-dx * strength, -dy * strength, np.ones_like(height)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)

    return (n * 0.5 + 0.5)


def rgba(rgb, alpha=None):
    a = np.ones(rgb.shape[:2], dtype=np.float32) if alpha is None else alpha
    out = np.concatenate([rgb, a[..., None]], axis=-1)

    return np.clip(out * 255.0 + 0.5, 0, 255).astype(np.uint8)


def tint(mask, low, high):
    """Ramp a scalar field between two linear RGB colours."""
    low = np.array(low, dtype=np.float32)
    high = np.array(high, dtype=np.float32)

    return low + (high - low) * mask[..., None]
