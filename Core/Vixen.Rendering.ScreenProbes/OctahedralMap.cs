// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.ScreenProbes;

/// <summary>The whole sphere of directions as a square of texels, and back.</summary>
/// <remarks>
///     <para>
///         <b>The same fold as <c>Math.EncodeOctahedral</c> in the Raven library, and that is a
///         contract rather than a coincidence.</b> A screen probe's radiance map is written by one
///         side and read by the other, so the direction a texel stands for has to be one function
///         evaluated twice. The shader's version exists first (it encodes G-buffer normals); this is
///         its C# counterpart, with the tie at zero broken the same way — a zero component picks the
///         positive hemisphere — because a convention that differs only on a set of measure zero is
///         exactly the kind that passes every test until a probe sits on an axis.
///     </para>
///     <para>
///         <b>Solid angles are computed exactly, not approximated by a Jacobian at the texel
///         centre.</b> A projection into spherical harmonics is an integral, so every texel has to
///         carry the area of sphere it stands for — <c>SphericalHarmonicsL1.Accumulated</c> says the
///         same thing from the other side. The octahedral map is not equal-area, and the error of a
///         centre-point approximation is largest exactly where the map distorts most, which is where
///         a cheap answer would bias every probe the same way and nobody would trace it back. A texel
///         maps to a spherical polygon whose edges are great-circle arcs <i>within one octant</i> —
///         the pre-normalisation point is affine in the plane there — so the exact answer is: clip
///         the texel against the eight octants, map each piece's corners, and sum the spherical
///         polygon areas. The test that keeps this honest is that the texels of any resolution sum
///         to 4π at double precision.
///     </para>
/// </remarks>
public static class OctahedralMap {
    /// <summary>Folds a direction into the octahedral square.</summary>
    /// <param name="direction">The direction, normalised.</param>
    /// <returns>A point in [-1, 1]².</returns>
    public static Vector2 Encode(Vector3 direction) {
        var scale = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
        var n = direction / scale;

        if (n.Z >= 0f) {
            return new(n.X, n.Y);
        }

        return new(
            SignedOne(n.X) * (1f - MathF.Abs(n.Y)),
            SignedOne(n.Y) * (1f - MathF.Abs(n.X))
        );
    }

    /// <summary>Unfolds a point of the square back into a direction.</summary>
    /// <param name="encoded">A point in [-1, 1]².</param>
    /// <returns>The direction, normalised.</returns>
    public static Vector3 Decode(Vector2 encoded) {
        var z = 1f - MathF.Abs(encoded.X) - MathF.Abs(encoded.Y);

        if (z >= 0f) {
            return Vector3.Normalize(new(encoded.X, encoded.Y, z));
        }

        return Vector3.Normalize(
            new(
                SignedOne(encoded.X) * (1f - MathF.Abs(encoded.Y)),
                SignedOne(encoded.Y) * (1f - MathF.Abs(encoded.X)),
                z
            )
        );
    }

    /// <summary>Where a texel's centre sits in the square.</summary>
    /// <param name="texel">The texel.</param>
    /// <param name="resolution">How many texels the map is along each axis.</param>
    /// <returns>The centre, in [-1, 1]².</returns>
    public static Vector2 TexelCentre(Int2 texel, int resolution) {
        Validate(texel, resolution);

        return new(
            (((texel.X + 0.5f) / resolution) * 2f) - 1f,
            (((texel.Y + 0.5f) / resolution) * 2f) - 1f
        );
    }

    /// <summary>The direction a texel's centre stands for.</summary>
    /// <param name="texel">The texel.</param>
    /// <param name="resolution">How many texels the map is along each axis.</param>
    /// <returns>The direction, normalised.</returns>
    public static Vector3 Direction(Int2 texel, int resolution) => Decode(TexelCentre(texel, resolution));

    /// <summary>The texel a direction lands in.</summary>
    /// <param name="direction">The direction, normalised.</param>
    /// <param name="resolution">How many texels the map is along each axis.</param>
    /// <returns>The texel.</returns>
    /// <remarks>
    ///     Clamped rather than wrapped at the square's edge, because a direction encoding to exactly
    ///     +1 is on the boundary of a texel that exists and a texel that does not — and the octahedral
    ///     wrap rule (the neighbour beyond an edge is the mirrored texel of the other hemisphere) is a
    ///     <i>filtering</i> concern, owed to whatever samples between texels, not to the map.
    /// </remarks>
    public static Int2 Texel(Vector3 direction, int resolution) {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 1);

        var encoded = Encode(direction);

        return new(
            Math.Clamp((int)MathF.Floor((encoded.X * 0.5f + 0.5f) * resolution), 0, resolution - 1),
            Math.Clamp((int)MathF.Floor((encoded.Y * 0.5f + 0.5f) * resolution), 0, resolution - 1)
        );
    }

    /// <summary>How much of the sphere each texel of a map stands for.</summary>
    /// <param name="resolution">How many texels the map is along each axis.</param>
    /// <returns>One entry per texel, row-major, summing to 4π.</returns>
    /// <remarks>
    ///     Computed once and cached per resolution — the table is a property of the resolution and of
    ///     nothing else, and computing it involves clipping every texel against eight octants, which
    ///     is not per-frame work.
    /// </remarks>
    public static ReadOnlyMemory<float> SolidAngles(int resolution) {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 1);

        lock (CachedSolidAngles) {
            if (CachedSolidAngles.TryGetValue(resolution, out var cached)) {
                return cached;
            }

            var table = new float[resolution * resolution];

            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    table[(y * resolution) + x] = (float)ExactSolidAngle(x, y, resolution);
                }
            }

            CachedSolidAngles[resolution] = table;

            return table;
        }
    }

    /// <summary>How much of the sphere one texel stands for.</summary>
    /// <param name="texel">The texel.</param>
    /// <param name="resolution">How many texels the map is along each axis.</param>
    /// <returns>Its solid angle, in steradians.</returns>
    public static float SolidAngle(Int2 texel, int resolution) {
        Validate(texel, resolution);

        return SolidAngles(resolution).Span[(texel.Y * resolution) + texel.X];
    }

    static readonly Dictionary<int, float[]> CachedSolidAngles = [];

    /// <summary>One for a non-negative value, minus one otherwise.</summary>
    /// <remarks>
    ///     Not <see cref="MathF.Sign" />, which answers zero for zero — and the fold needs a zero
    ///     component to still pick a hemisphere. The same rule, for the same reason, as the Raven
    ///     library's <c>Math.SignedOne</c>.
    /// </remarks>
    static float SignedOne(float value) => value >= 0f ? 1f : -1f;

    static void Validate(Int2 texel, int resolution) {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.X);
        ArgumentOutOfRangeException.ThrowIfNegative(texel.Y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.X, resolution);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(texel.Y, resolution);
    }

    // --- The exact texel area, in double precision -----------------------
    //
    // A texel is a square in the plane; the sphere sees it as up to eight spherical polygons, one per
    // octant the square overlaps. Within one octant the decode is affine in the plane — inner octants
    // map (u, v) to (u, v, 1 - sx·u - sy·v), outer corners to the fold below — so a straight edge in
    // the plane is a straight segment in space, which normalises to a great-circle arc. Clip, map the
    // corners, sum the spherical triangle fan. Anything cheaper is an approximation with its largest
    // error at the fold, and anything more general (arbitrary curved edges) is solving a problem the
    // map does not have.

    static double ExactSolidAngle(int x, int y, int resolution) {
        var x0 = ((double)x / resolution * 2.0) - 1.0;
        var x1 = ((double)(x + 1) / resolution * 2.0) - 1.0;
        var y0 = ((double)y / resolution * 2.0) - 1.0;
        var y1 = ((double)(y + 1) / resolution * 2.0) - 1.0;

        Span<double> px = stackalloc double[16];
        Span<double> py = stackalloc double[16];
        Span<double> qx = stackalloc double[16];
        Span<double> qy = stackalloc double[16];

        var total = 0.0;

        for (var sx = -1; sx <= 1; sx += 2) {
            for (var sy = -1; sy <= 1; sy += 2) {
                for (var outer = 0; outer < 2; outer++) {
                    px[0] = x0;
                    py[0] = y0;
                    px[1] = x1;
                    py[1] = y0;
                    px[2] = x1;
                    py[2] = y1;
                    px[3] = x0;
                    py[3] = y1;

                    var count = 4;

                    // The quadrant: sx·u ≥ 0 and sy·v ≥ 0.
                    count = Clip(px, py, qx, qy, count, sx, 0, 0.0);
                    count = Clip(px, py, qx, qy, count, 0, sy, 0.0);

                    // Inner triangle sx·u + sy·v ≤ 1, or the corner beyond it.
                    count = outer == 0
                        ? Clip(px, py, qx, qy, count, -sx, -sy, 1.0)
                        : Clip(px, py, qx, qy, count, sx, sy, -1.0);

                    if (count >= 3) {
                        total += Spherical(px, py, count, sx, sy, outer == 1);
                    }
                }
            }
        }

        return total;
    }

    /// <summary>Sutherland–Hodgman against the half-plane a·u + b·v + c ≥ 0, in place.</summary>
    static int Clip(Span<double> px, Span<double> py, Span<double> qx, Span<double> qy, int count, double a, double b, double c) {
        var written = 0;

        for (var index = 0; index < count; index++) {
            var next = (index + 1) % count;

            var here = (a * px[index]) + (b * py[index]) + c;
            var there = (a * px[next]) + (b * py[next]) + c;

            if (here >= 0.0) {
                qx[written] = px[index];
                qy[written] = py[index];
                written++;
            }

            if ((here > 0.0 && there < 0.0) || (here < 0.0 && there > 0.0)) {
                var t = here / (here - there);

                qx[written] = px[index] + (t * (px[next] - px[index]));
                qy[written] = py[index] + (t * (py[next] - py[index]));
                written++;
            }
        }

        qx[..written].CopyTo(px);
        qy[..written].CopyTo(py);

        return written;
    }

    /// <summary>The solid angle of one clipped piece, as a fan of spherical triangles.</summary>
    static double Spherical(ReadOnlySpan<double> px, ReadOnlySpan<double> py, int count, int sx, int sy, bool outer) {
        Span<double> vx = stackalloc double[16];
        Span<double> vy = stackalloc double[16];
        Span<double> vz = stackalloc double[16];

        var vertices = 0;

        for (var index = 0; index < count; index++) {
            double dx, dy, dz;

            if (outer) {
                dx = sx * (1.0 - (sy * py[index]));
                dy = sy * (1.0 - (sx * px[index]));
                dz = 1.0 - (sx * px[index]) - (sy * py[index]);
            } else {
                dx = px[index];
                dy = py[index];
                dz = 1.0 - (sx * px[index]) - (sy * py[index]);
            }

            var length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            if (length < 1e-15) {
                continue;
            }

            dx /= length;
            dy /= length;
            dz /= length;

            // Clipping can leave two corners of one piece on the same ray; a zero-length arc adds
            // nothing and its triangle would be pure rounding noise.
            if (vertices > 0) {
                var same = (dx * vx[vertices - 1]) + (dy * vy[vertices - 1]) + (dz * vz[vertices - 1]) > 1.0 - 1e-14;

                if (same) {
                    continue;
                }
            }

            vx[vertices] = dx;
            vy[vertices] = dy;
            vz[vertices] = dz;
            vertices++;
        }

        if (vertices >= 2) {
            var closes = (vx[0] * vx[vertices - 1]) + (vy[0] * vy[vertices - 1]) + (vz[0] * vz[vertices - 1]) > 1.0 - 1e-14;

            if (closes) {
                vertices--;
            }
        }

        if (vertices < 3) {
            return 0.0;
        }

        var total = 0.0;

        for (var index = 1; index < vertices - 1; index++) {
            total += Triangle(
                vx[0], vy[0], vz[0],
                vx[index], vy[index], vz[index],
                vx[index + 1], vy[index + 1], vz[index + 1]
            );
        }

        return Math.Abs(total);
    }

    /// <summary>Van Oosterom and Strackee's signed solid angle of one spherical triangle.</summary>
    static double Triangle(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz) {
        var determinant =
            (ax * ((by * cz) - (bz * cy)))
            + (ay * ((bz * cx) - (bx * cz)))
            + (az * ((bx * cy) - (by * cx)));

        var denominator = 1.0
            + ((ax * bx) + (ay * by) + (az * bz))
            + ((bx * cx) + (by * cy) + (bz * cz))
            + ((ax * cx) + (ay * cy) + (az * cz));

        return 2.0 * Math.Atan2(determinant, denominator);
    }
}
