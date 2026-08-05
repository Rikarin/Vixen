#!/usr/bin/env python3
"""Adds box-projected texture coordinates to the arena's placeholder .obj boxes.

    python3 boxuv.py ../Assets/Models/arena-wall.obj 2.0

⚠ **Without this the textures stream and do not show.** `ModelCompiler` fills a missing texture
coordinate with `Vector2.Zero` — not an error, not a warning — so a mesh with no `vt` samples texel
(0, 0) of its base-colour map at every fragment and draws as one flat colour. The map is resident,
the counters climb, and the frame is the one it was before. The arena's boxes were written without
`vt` because nothing sampled them yet.

**Projected per face, in metres of world, not unwrapped.** A box has no seams worth authoring and
these are placeholders; what matters is that a 64 m wall and a 1.6 m crate get the same texel
density, which a 0..1 unwrap per object is exactly what does not give. The two axes a face is
projected along are the two the face normal is not dominant in, so each face takes world position
directly and the tiling is a distance.

The script rewrites in place and is idempotent: an object that already carries `vt` is rejected
rather than doubled, because the second pass would index coordinates written by the first.
"""

import sys
from pathlib import Path


def project(position, normal, tile):
    """The two world axes a face is textured by, over the tiling."""
    x, y, z = position
    nx, ny, nz = (abs(component) for component in normal)

    if ny >= nx and ny >= nz:
        u, v = x, z
    elif nx >= nz:
        u, v = z, y
    else:
        u, v = x, y

    return u / tile, v / tile


def rewrite(path, tile):
    lines = Path(path).read_text().splitlines()

    if any(line.startswith("vt ") for line in lines):
        raise SystemExit(f"{path}: already has texture coordinates.")

    positions = [tuple(float(n) for n in line.split()[1:4]) for line in lines if line.startswith("v ")]
    normals = [tuple(float(n) for n in line.split()[1:4]) for line in lines if line.startswith("vn ")]

    # One coordinate per (vertex, normal) pair actually referenced, in first-seen order — a corner
    # of a box belongs to three faces and takes a different projection in each.
    coords = {}
    faces = []

    for line in lines:
        if not line.startswith("f "):
            continue

        corners = []

        for token in line.split()[1:]:
            vertex, _, normal = token.partition("//")
            vertex, normal = int(vertex), int(normal)
            key = (vertex, normal)

            if key not in coords:
                coords[key] = (len(coords) + 1, project(positions[vertex - 1], normals[normal - 1], tile))

            corners.append(f"{vertex}/{coords[key][0]}/{normal}")

        faces.append("f " + " ".join(corners))

    out = [line for line in lines if not line.startswith("f ")]
    out += [f"vt {u:.4f} {v:.4f}" for _, (u, v) in sorted(coords.values())]
    out += faces

    Path(path).write_text("\n".join(out) + "\n")


if __name__ == "__main__":
    rewrite(sys.argv[1], float(sys.argv[2]))
