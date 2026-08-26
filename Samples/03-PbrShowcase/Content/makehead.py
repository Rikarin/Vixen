#!/usr/bin/env python3
# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0
"""Writes Assets/Models/head.gltf — the sample's morphed mesh.

Why a generator and not a checked-in binary
-------------------------------------------
A blend shape is a delta per vertex per shape, and the whole point of the sample is that somebody
can see what the delta does. A `.glb` would put that behind a hex editor. This writes a `.gltf`,
which is JSON with one base64 buffer, so the topology and both shapes are readable in the diff that
introduces them — and regenerating is `python3 Content/makehead.py`.

Sample 13's `Content/*.py` set the precedent: the committed asset is the artefact, the script is how
it was arrived at.

What it makes
-------------
A head: an ellipsoid of latitude/longitude quads, with a nose so that the front is a face and not a
side. Two shapes, both authored to be *large*, because the sample exists to be looked at:

  jawOpen    — the lower front swings down and forward, the way a jaw does.
  browRaise  — a band above the eyes lifts and juts.

⚠ Both shapes carry POSITION *and* NORMAL targets. A shape that moved vertices and left the normals
alone would light the deformed head as though it were still at rest, which reads as a shading bug
rather than as the missing half of a morph — and the normal delta is the half of `MorphScatter`
nothing in a sample had ever exercised.

⚠ glTF stores a target as a delta and Assimp hands Vixen back a whole replacement vertex array, so
`ModelReader` subtracts. Nothing here has to know that; it is written the way glTF says.
"""

import base64
import json
import math
import pathlib
import struct

RINGS = 20        # latitude bands, pole to pole
SEGMENTS = 28     # longitude divisions
RADIUS = (0.42, 0.55, 0.46)   # x, y, z — an ellipsoid rather than a ball


def ellipsoid(u: float, v: float) -> tuple[float, float, float]:
    """A point on the head, u around and v pole to pole, both in 0..1."""
    theta = u * math.tau
    phi = v * math.pi

    x = RADIUS[0] * math.sin(phi) * math.sin(theta)
    y = RADIUS[1] * math.cos(phi)
    z = RADIUS[2] * math.sin(phi) * math.cos(theta)

    # The nose. A face needs one feature that says which way it is looking, or a jaw that drops is
    # a sphere that dents and the picture proves nothing about orientation.
    # ⚠ Wide enough not to fold. A tall narrow spike over three vertices pushes the triangles under
    # it past each other, and the result is a gash of inside-out faces rather than a nose — which is
    # exactly what the first version of this file produced and what a software rasterisation of it
    # showed before anything reached a GPU.
    front = max(0.0, z / RADIUS[2]) ** 2
    height = math.exp(-((y - 0.02) ** 2) / 0.020)
    across = math.exp(-(x ** 2) / 0.012)

    z += 0.10 * front * height * across

    return x, y, z


def build_base() -> tuple[list[tuple[float, float, float]], list[int]]:
    """A closed manifold: the seam is welded and each pole is one vertex.

    ⚠ The obvious latitude/longitude grid — (RINGS+1) x (SEGMENTS+1) vertices, a quad each — is not
    closed. Its first and last columns are separate vertices at the same place, so the seam is two
    open boundaries; and its pole rows are SEGMENTS+1 copies of one point, so the top and bottom
    bands are degenerate triangles with no area and no normal. That mesh renders, and everything
    downstream that wants a *surface* rather than a triangle soup gets it wrong in a different way:
    `ModelCompiler.CompileMeshlets` refuses it outright ("Group 0's boundary moved"), and the
    vertex normals along the seam are averaged over half the triangles they should be, which is a
    visible crease down the front of the face.
    """
    positions = [ellipsoid(0.0, 0.0)]

    for ring in range(1, RINGS):
        for segment in range(SEGMENTS):
            positions.append(ellipsoid(segment / SEGMENTS, ring / RINGS))

    positions.append(ellipsoid(0.0, 1.0))

    north = 0
    south = len(positions) - 1

    def at(ring: int, segment: int) -> int:
        return 1 + ((ring - 1) * SEGMENTS) + (segment % SEGMENTS)

    indices = []

    for segment in range(SEGMENTS):
        indices += [north, at(1, segment), at(1, segment + 1)]

    for ring in range(1, RINGS - 1):
        for segment in range(SEGMENTS):
            a, b = at(ring, segment), at(ring + 1, segment)
            c, d = at(ring, segment + 1), at(ring + 1, segment + 1)

            # Counter-clockwise seen from outside, which is what glTF's default face winding wants.
            indices += [a, b, c, c, b, d]

    for segment in range(SEGMENTS):
        indices += [south, at(RINGS - 1, segment + 1), at(RINGS - 1, segment)]

    return positions, indices


def normals(positions, indices):
    """Area-weighted vertex normals, which is what an exporter would have written."""
    out = [[0.0, 0.0, 0.0] for _ in positions]

    for triangle in range(0, len(indices), 3):
        i, j, k = indices[triangle], indices[triangle + 1], indices[triangle + 2]
        p, q, r = positions[i], positions[j], positions[k]

        ux, uy, uz = q[0] - p[0], q[1] - p[1], q[2] - p[2]
        vx, vy, vz = r[0] - p[0], r[1] - p[1], r[2] - p[2]

        nx = (uy * vz) - (uz * vy)
        ny = (uz * vx) - (ux * vz)
        nz = (ux * vy) - (uy * vx)

        for vertex in (i, j, k):
            out[vertex][0] += nx
            out[vertex][1] += ny
            out[vertex][2] += nz

    result = []

    for n in out:
        length = math.sqrt((n[0] ** 2) + (n[1] ** 2) + (n[2] ** 2))

        # ⚠ An absolute floor rather than a relative one, and it has to be: a degenerate fan at the
        # pole sums to very nearly zero and dividing by that gives infinities, which glTF's accessor
        # min/max would then carry into the file.
        result.append((0.0, 1.0, 0.0) if length < 1e-9 else (n[0] / length, n[1] / length, n[2] / length))

    return result


def jaw_open(position):
    """The lower front swings down and forward."""
    x, y, z = position

    below = max(0.0, -(y - 0.06) / 0.5)
    front = max(0.0, (z + 0.1) / (RADIUS[2] + 0.10))
    fall = min(1.0, below) ** 1.2 * min(1.0, front) ** 1.5

    return (x * -0.12 * fall, -0.34 * fall, 0.14 * fall)


def brow_raise(position):
    """A band above the eyes lifts and juts."""
    x, y, z = position

    band = math.exp(-((y - 0.30) ** 2) / 0.010)
    front = max(0.0, z / (RADIUS[2] + 0.10)) ** 1.5

    return (0.0, 0.13 * band * front, 0.11 * band * front)


def shaped(positions, indices, displace):
    """One shape's position and normal deltas, as glTF wants them."""
    moved = [
        (p[0] + d[0], p[1] + d[1], p[2] + d[2])
        for p, d in ((p, displace(p)) for p in positions)
    ]

    base = normals(positions, indices)
    after = normals(moved, indices)

    return (
        [(m[0] - p[0], m[1] - p[1], m[2] - p[2]) for m, p in zip(moved, positions)],
        [(a[0] - b[0], a[1] - b[1], a[2] - b[2]) for a, b in zip(after, base)],
    )


def pack_vec3(values):
    return b"".join(struct.pack("<3f", *value) for value in values)


def bounds(values):
    return (
        [min(value[axis] for value in values) for axis in range(3)],
        [max(value[axis] for value in values) for axis in range(3)],
    )


def main() -> None:
    positions, indices = build_base()
    base_normals = normals(positions, indices)

    jaw_positions, jaw_normals = shaped(positions, indices, jaw_open)
    brow_positions, brow_normals = shaped(positions, indices, brow_raise)

    blobs = [
        ("POSITION", pack_vec3(positions), positions, 34962),
        ("NORMAL", pack_vec3(base_normals), base_normals, 34962),
        ("JAW_POSITION", pack_vec3(jaw_positions), jaw_positions, 34962),
        ("JAW_NORMAL", pack_vec3(jaw_normals), jaw_normals, 34962),
        ("BROW_POSITION", pack_vec3(brow_positions), brow_positions, 34962),
        ("BROW_NORMAL", pack_vec3(brow_normals), brow_normals, 34962),
    ]

    buffer = bytearray()
    views = []
    accessors = []

    for _, blob, values, target in blobs:
        # ⚠ Four-byte aligned, which glTF requires of a bufferView holding floats and which a reader
        # is entitled to assume. Every blob here is a multiple of twelve, so the padding never fires
        # — it is here so that adding a ushort blob later does not silently produce an invalid file.
        while len(buffer) % 4:
            buffer.append(0)

        views.append({"buffer": 0, "byteOffset": len(buffer), "byteLength": len(blob), "target": target})
        low, high = bounds(values)

        accessors.append({
            "bufferView": len(views) - 1,
            "componentType": 5126,
            "count": len(values),
            "type": "VEC3",
            "min": low,
            "max": high,
        })

        buffer += blob

    while len(buffer) % 4:
        buffer.append(0)

    index_blob = b"".join(struct.pack("<H", value) for value in indices)

    views.append({"buffer": 0, "byteOffset": len(buffer), "byteLength": len(index_blob), "target": 34963})
    accessors.append({
        "bufferView": len(views) - 1,
        "componentType": 5123,
        "count": len(indices),
        "type": "SCALAR",
        "min": [min(indices)],
        "max": [max(indices)],
    })

    buffer += index_blob

    document = {
        "asset": {"version": "2.0", "generator": "Vixen samples — Content/makehead.py"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "Head", "mesh": 0}],
        "meshes": [{
            "name": "Head",

            # ⚠ The mesh's rest weights, and they are zero on purpose. glTF's `weights` is what the
            # file is authored at, and a head shipped mid-expression would make every weight the
            # game sets relative to a pose nobody chose.
            "weights": [0, 0],
            "extras": {"targetNames": ["jawOpen", "browRaise"]},
            "primitives": [{
                "attributes": {"POSITION": 0, "NORMAL": 1},
                "indices": len(accessors) - 1,
                "targets": [
                    {"POSITION": 2, "NORMAL": 3},
                    {"POSITION": 4, "NORMAL": 5},
                ],
            }],
        }],
        "buffers": [{
            "byteLength": len(buffer),
            "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(buffer)).decode("ascii"),
        }],
        "bufferViews": views,
        "accessors": accessors,
    }

    out = pathlib.Path(__file__).resolve().parent.parent / "Assets" / "Models" / "head.gltf"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(document, indent=1) + "\n")

    print(f"{out}: {len(positions)} vertices, {len(indices) // 3} triangles, 2 shapes")


if __name__ == "__main__":
    main()
