// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>Moves paint from one mesh's layout to another's, by where each texel sits on the surface.</summary>
/// <remarks>
///     <para>
///         <b>Repacked islands and a remeshed model are one case.</b> A saved canvas holds texels and not strokes,
///         so what survives a new layout is the surface: each texel of the new atlas finds its point on the new mesh,
///         the nearest point on the old mesh facing the same way, and reads the old atlas there. No vertex or
///         triangle has to match, which is why a re-export that reordered everything costs nothing.
///     </para>
///     <para>
///         ⚠ <b>Surface the old mesh never had reads as the canvas's background.</b> A point further from the old
///         surface than <see cref="ReachShare" /> of the model is new geometry, and copying the nearest colour onto
///         it would be inventing paint.
///     </para>
/// </remarks>
public static class PaintTransfer {
    /// <summary>How far a point may be from the old surface, as a share of the model's size.</summary>
    const float ReachShare = 0.05f;

    /// <summary>How far past an island paint is dilated, as a stroke's gutter does.</summary>
    const int Gutter = 4;

    const float Unmapped = float.NaN;

    /// <summary>Whether two meshes lay the atlas out the same way, so that paint on one already fits the other.</summary>
    /// <param name="from">The old mesh.</param>
    /// <param name="to">The new mesh.</param>
    /// <returns>Whether their triangles cover the atlas alike, in any order and from any corner.</returns>
    /// <remarks>
    ///     ⚠ <b>Only the layout is compared, on purpose.</b> A texel belongs to its place in the atlas, so a sculpted
    ///     mesh with the same islands keeps its paint by doing nothing. <see cref="Move" /> would resample it, and
    ///     every resample loses a little sharpness. The comparison is exact: a layout that moved by rounding reads as
    ///     changed, which costs a transfer and never paint.
    /// </remarks>
    public static bool SameLayout(PaintProjection from, PaintProjection to) {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Triangles != to.Triangles) {
            return false;
        }

        return Layout(from).SequenceEqual(Layout(to));
    }

    /// <summary>A mesh's atlas triangles in one order, each from its lowest corner, so that order means nothing.</summary>
    static List<(float, float, float, float, float, float)> Layout(PaintProjection mesh) {
        List<(float, float, float, float, float, float)> layout = new(mesh.Triangles);
        for (var triangle = 0; triangle < mesh.Triangles; triangle++) {
            mesh.Atlas(triangle, out var a, out var b, out var c);
            var turned = Lower(c, a) ? (c, a, b) : Lower(b, a) ? (b, c, a) : (a, b, c);
            if (Lower(turned.Item3, turned.Item1)) {
                turned = (turned.Item3, turned.Item1, turned.Item2);
            }

            layout.Add((turned.Item1.X, turned.Item1.Y, turned.Item2.X, turned.Item2.Y, turned.Item3.X, turned.Item3.Y));
        }

        layout.Sort();
        return layout;
    }

    static bool Lower(Vector2 a, Vector2 b) => a.X < b.X || (a.X == b.X && a.Y < b.Y);

    /// <summary>Lays a canvas out on another mesh.</summary>
    /// <param name="canvas">The paint, on the old layout.</param>
    /// <param name="from">The old mesh.</param>
    /// <param name="to">The new mesh, in the same space.</param>
    /// <returns>A new canvas of the same size and channels.</returns>
    public static PaintCanvas Move(PaintCanvas canvas, PaintProjection from, PaintProjection to) {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var (width, height) = (canvas.Width, canvas.Height);
        var (sourceX, sourceY) = Map(from, to, width, height);
        PaintCanvas moved = new(width, height);

        foreach (var usage in canvas.Channels) {
            var image = canvas.Channel(usage);
            var texels = Resampled(image.ToTexels(), sourceX, sourceY, width, height, image.Background);
            moved.Channel(usage).CopyFrom(texels);
        }

        return moved;
    }

    /// <summary>For every texel of the new atlas, where on the old atlas it reads.</summary>
    static (float[] X, float[] Y) Map(PaintProjection from, PaintProjection to, int width, int height) {
        var sourceX = new float[width * height];
        var sourceY = new float[width * height];
        Array.Fill(sourceX, Unmapped);

        var reach = from.Bounds.Extent.Length() * 2f * ReachShare;
        Parallel.For(
            0,
            to.Triangles,
            () => new List<int>(),
            (triangle, _, near) => {
                Draw(triangle, from, to, width, height, reach, near, sourceX, sourceY);
                return near;
            },
            static _ => { }
        );
        return (sourceX, sourceY);
    }

    static void Draw(
        int triangle,
        PaintProjection from,
        PaintProjection to,
        int width,
        int height,
        float reach,
        List<int> near,
        float[] sourceX,
        float[] sourceY
    ) {
        to.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);
        var (ta, tb, tc) = (
            PaintProjection.Texel(ua, width, height),
            PaintProjection.Texel(ub, width, height),
            PaintProjection.Texel(uc, width, height)
        );
        var area = Cross(tb - ta, tc - ta);
        if (area == 0f) {
            return;
        }

        var normal = Vector3.Cross(b - a, c - a);
        var centre = (a + b + c) / 3f;
        var radius = MathF.Max(MathF.Max((a - centre).Length(), (b - centre).Length()), (c - centre).Length());
        var settled = radius * 1.01f + reach * 0.002f;
        from.Overlapping(centre, settled, near);
        var (first, second) = (Math.Max((int)MathF.Floor(MathF.Min(ta.X, MathF.Min(tb.X, tc.X))), 0),
            Math.Min((int)MathF.Ceiling(MathF.Max(ta.X, MathF.Max(tb.X, tc.X))), width - 1));
        var (top, bottom) = (Math.Max((int)MathF.Floor(MathF.Min(ta.Y, MathF.Min(tb.Y, tc.Y))), 0),
            Math.Min((int)MathF.Ceiling(MathF.Max(ta.Y, MathF.Max(tb.Y, tc.Y))), height - 1));

        List<int>? wider = null;
        for (var y = top; y <= bottom; y++) {
            for (var x = first; x <= second; x++) {
                Vector2 texel = new(x + 0.5f, y + 0.5f);
                var u = Cross(tb - texel, tc - texel) / area;
                var v = Cross(tc - texel, ta - texel) / area;
                var w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) {
                    continue;
                }

                var point = a * u + b * v + c * w;
                var found = Nearest(from, near, point, normal, out var distance, out var uv);
                if (!found || distance > settled - (point - centre).Length()) {
                    wider ??= [];
                    found = Further(from, wider, point, normal, reach, ref distance, ref uv) || found;
                }

                if (found && distance <= reach) {
                    var at = y * width + x;
                    var there = PaintProjection.Texel(uv, width, height);
                    sourceX[at] = there.X;
                    sourceY[at] = there.Y;
                }
            }
        }
    }

    static bool Further(
        PaintProjection from,
        List<int> near,
        Vector3 point,
        Vector3 normal,
        float reach,
        ref float distance,
        ref Vector2 uv
    ) {
        for (var radius = reach / 16f; radius <= reach; radius *= 2f) {
            from.Overlapping(point, radius, near);
            if (Nearest(from, near, point, normal, out var closer, out var coordinate) && closer <= radius) {
                distance = closer;
                uv = coordinate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The closest point on these triangles, preferring those facing the way the new surface does.</summary>
    static bool Nearest(
        PaintProjection from,
        List<int> near,
        Vector3 point,
        Vector3 normal,
        out float distance,
        out Vector2 uv
    ) {
        var best = float.PositiveInfinity;
        var facing = false;
        uv = Vector2.Zero;
        foreach (var triangle in near) {
            from.Triangle(triangle, out var a, out var b, out var c, out var ua, out var ub, out var uc);
            var agrees = Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f;
            if (facing && !agrees) {
                continue;
            }

            var weights = Closest(point, a, b, c);
            var length = (a * weights.X + b * weights.Y + c * weights.Z - point).Length();
            if ((agrees && !facing) || length < best) {
                best = length;
                facing = agrees;
                uv = ua * weights.X + ub * weights.Y + uc * weights.Z;
            }
        }

        distance = best;
        return float.IsFinite(best);
    }

    /// <summary>The barycentric weights of the closest point of a triangle.</summary>
    static Vector3 Closest(Vector3 point, Vector3 a, Vector3 b, Vector3 c) {
        var (ab, ac, ap) = (b - a, c - a, point - a);
        var (d1, d2) = (Vector3.Dot(ab, ap), Vector3.Dot(ac, ap));
        if (d1 <= 0f && d2 <= 0f) {
            return new(1f, 0f, 0f);
        }

        var bp = point - b;
        var (d3, d4) = (Vector3.Dot(ab, bp), Vector3.Dot(ac, bp));
        if (d3 >= 0f && d4 <= d3) {
            return new(0f, 1f, 0f);
        }

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
            var along = d1 / (d1 - d3);
            return new(1f - along, along, 0f);
        }

        var cp = point - c;
        var (d5, d6) = (Vector3.Dot(ab, cp), Vector3.Dot(ac, cp));
        if (d6 >= 0f && d5 <= d6) {
            return new(0f, 0f, 1f);
        }

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
            var along = d2 / (d2 - d6);
            return new(1f - along, 0f, along);
        }

        var va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) {
            var along = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return new(0f, 1f - along, along);
        }

        var total = 1f / (va + vb + vc);
        var (v, w) = (vb * total, vc * total);
        return new(1f - v - w, v, w);
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Reads the old atlas where the map says, then lets the paint run a gutter past each island.</summary>
    static byte[] Resampled(byte[] old, float[] sourceX, float[] sourceY, int width, int height, uint background) {
        var texels = new byte[old.Length];
        var held = new bool[width * height];
        Span<byte> empty = stackalloc byte[PaintImage.BytesPerTexel];
        BitConverter.TryWriteBytes(empty, background);
        var nothing = empty.ToArray();
        Parallel.For(
            0,
            height,
            row => {
                for (var x = 0; x < width; x++) {
                    var at = row * width + x;
                    if (float.IsNaN(sourceX[at])) {
                        nothing.CopyTo(texels, at * PaintImage.BytesPerTexel);
                    } else {
                        held[at] = true;
                        Bilinear(old, width, height, sourceX[at], sourceY[at], texels, at * PaintImage.BytesPerTexel);
                    }
                }
            }
        );
        for (var pass = 0; pass < Gutter; pass++) {
            held = Dilated(texels, held, width, height);
        }

        return texels;
    }

    static bool[] Dilated(byte[] texels, bool[] held, int width, int height) {
        var grown = (bool[])held.Clone();
        Parallel.For(
            0,
            height,
            y => {
                for (var x = 0; x < width; x++) {
                    var at = y * width + x;
                    if (held[at]) {
                        continue;
                    }

                    var neighbour = x > 0 && held[at - 1] ? at - 1
                        : x < width - 1 && held[at + 1] ? at + 1
                        : y > 0 && held[at - width] ? at - width
                        : y < height - 1 && held[at + width] ? at + width
                        : -1;
                    if (neighbour >= 0) {
                        Array.Copy(
                            texels,
                            neighbour * PaintImage.BytesPerTexel,
                            texels,
                            at * PaintImage.BytesPerTexel,
                            PaintImage.BytesPerTexel
                        );
                        grown[at] = true;
                    }
                }
            }
        );
        return grown;
    }

    /// <summary>A read between texels, weighted by alpha so that clear paint lends no colour to its neighbours.</summary>
    static void Bilinear(byte[] old, int width, int height, float x, float y, byte[] into, int at) {
        var (left, top) = (x - 0.5f, y - 0.5f);
        var (x0, y0) = ((int)MathF.Floor(left), (int)MathF.Floor(top));
        var (fx, fy) = (left - x0, top - y0);
        Span<float> sum = stackalloc float[PaintImage.BytesPerTexel];
        for (var corner = 0; corner < 4; corner++) {
            var column = Math.Clamp(x0 + (corner & 1), 0, width - 1);
            var line = Math.Clamp(y0 + (corner >> 1), 0, height - 1);
            var weight = ((corner & 1) == 0 ? 1f - fx : fx) * ((corner >> 1) == 0 ? 1f - fy : fy);
            var from = (line * width + column) * PaintImage.BytesPerTexel;
            var alpha = old[from + 3] * weight;
            sum[0] += old[from] * alpha;
            sum[1] += old[from + 1] * alpha;
            sum[2] += old[from + 2] * alpha;
            sum[3] += alpha;
        }

        var share = sum[3] > 0f ? 1f / sum[3] : 0f;
        into[at] = (byte)MathF.Round(sum[0] * share);
        into[at + 1] = (byte)MathF.Round(sum[1] * share);
        into[at + 2] = (byte)MathF.Round(sum[2] * share);
        into[at + 3] = (byte)MathF.Round(sum[3]);
    }
}
