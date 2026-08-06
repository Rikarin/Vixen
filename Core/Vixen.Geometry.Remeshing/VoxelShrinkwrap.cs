// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Sample a signed field, extract a fresh surface, and continue from that — the escape hatch.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D3 step 7: opt-in, last, and never the default.</b>
///         <see cref="ConditioningSettings.Shrinkwrap" /> defaults to false and
///         <see cref="ConditioningReport.Shrinkwrapped" /> exists so that a caller reading a report
///         can see that it fired.
///     </para>
///     <para>
///         ⚠ <b>It destroys thin features.</b> Everything thinner than a voxel either closes into a
///         solid or vanishes, so a character's fingers fuse, a railing becomes a wall and a sheet of
///         cloth becomes nothing. It is the answer for input so broken that nothing else will run on
///         it — a soup with no consistent inside, a hundred interpenetrating shells, a scan with more
///         hole than surface — and it is the wrong answer for anything else.
///     </para>
///     <para>
///         ⚠ <b>The sign comes from the generalised winding number rather than from a distance
///         field</b> (<see cref="WindingNumberField" />), which is what lets self-intersecting and
///         open input still have an inside. A signed distance field would need the closed manifold
///         that this step exists because the input does not have.
///     </para>
///     <para>
///         The extraction is dual: one vertex per sign-changing cell, quads across each sign-changing
///         grid edge. Vertices sit at the mean of the cell's crossings rather than at the minimiser of
///         a quadric — plain surface nets rather than dual contouring with a QEF. A QEF recovers sharp
///         features, and this is the one step in the pipeline that has already destroyed them.
///     </para>
/// </remarks>
static class VoxelShrinkwrap {
    /// <summary>The coarsest grid the shrinkwrap will use along the longest axis.</summary>
    public const int MinimumResolution = 8;

    /// <summary>The finest. ⚠ The sample count is the cube of this, and each sample walks a tree.</summary>
    public const int MaximumResolution = 96;

    /// <summary>Replaces a soup with the surface of its winding-number field.</summary>
    /// <param name="soup">The mesh, rewritten in place. Its groups do not survive.</param>
    /// <param name="targetEdgeLength">
    ///     The length to size the voxels by. Zero or less means one sixty-fourth of the longest axis.
    /// </param>
    /// <returns>Whether anything was extracted. A field with no crossings leaves the soup alone.</returns>
    /// <remarks>
    ///     ⚠ <b>The resolution is derived from the model's own extent and never fixed.</b> A voxel
    ///     count expressed in world units is the same mistake as an absolute weld tolerance, one
    ///     dimension up: it decides that a model authored in centimetres gets sixty-four thousand
    ///     times the detail of the same model authored in metres.
    /// </remarks>
    public static bool Run(TriangleSoup soup, float targetEdgeLength) {
        ArgumentNullException.ThrowIfNull(soup);

        if (soup.TriangleCount == 0) {
            return false;
        }

        var box = soup.Bounds;
        var size = box.Maximum - box.Minimum;
        var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));

        if (longest <= 0f) {
            return false;
        }

        var steps = targetEdgeLength > 0f ? (int) MathF.Round(longest / targetEdgeLength) : 64;

        steps = Math.Clamp(steps, MinimumResolution, MaximumResolution);

        var spacing = longest / steps;

        // Two cells of padding all round, so a surface that touches the bounding box still has an
        // outside for the field to change sign against.
        var origin = box.Minimum - new Vector3(spacing * 2f);
        var counts = (
            X: (int) MathF.Ceiling((size.X + (spacing * 4f)) / spacing) + 1,
            Y: (int) MathF.Ceiling((size.Y + (spacing * 4f)) / spacing) + 1,
            Z: (int) MathF.Ceiling((size.Z + (spacing * 4f)) / spacing) + 1
        );

        var field = WindingNumberField.Build([.. soup.Positions], [.. soup.Triangles]);
        var values = new float[counts.X * counts.Y * counts.Z];

        for (var z = 0; z < counts.Z; z++) {
            for (var y = 0; y < counts.Y; y++) {
                for (var x = 0; x < counts.X; x++) {
                    var point = origin + new Vector3(x * spacing, y * spacing, z * spacing);

                    values[Index(counts, x, y, z)] = field.At(point);
                }
            }
        }

        var cells = (X: counts.X - 1, Y: counts.Y - 1, Z: counts.Z - 1);
        var vertices = new int[cells.X * cells.Y * cells.Z];

        Array.Fill(vertices, -1);

        List<Vector3> points = [];

        for (var z = 0; z < cells.Z; z++) {
            for (var y = 0; y < cells.Y; y++) {
                for (var x = 0; x < cells.X; x++) {
                    var placed = Place(values, counts, origin, spacing, x, y, z);

                    if (placed is not { } point) {
                        continue;
                    }

                    vertices[Index(cells, x, y, z)] = points.Count;
                    points.Add(point);
                }
            }
        }

        if (points.Count == 0) {
            return false;
        }

        soup.Positions.Clear();
        soup.Positions.AddRange(points);
        soup.Triangles.Clear();
        soup.Groups.Clear();

        // One quad per sign-changing grid edge, over the four cells that share it. The three loops
        // below are the same walk on the three axes, and the vertex order in each is what makes the
        // quad's normal point from inside to outside.
        for (var z = 0; z < counts.Z; z++) {
            for (var y = 0; y < counts.Y; y++) {
                for (var x = 0; x < counts.X; x++) {
                    var here = Inside(values[Index(counts, x, y, z)]);

                    if (x + 1 < counts.X && here != Inside(values[Index(counts, x + 1, y, z)])
                        && y > 0 && z > 0 && y < cells.Y && z < cells.Z) {
                        Quad(
                            soup,
                            vertices,
                            cells,
                            here,
                            (x, y - 1, z - 1),
                            (x, y, z - 1),
                            (x, y, z),
                            (x, y - 1, z)
                        );
                    }

                    if (y + 1 < counts.Y && here != Inside(values[Index(counts, x, y + 1, z)])
                        && x > 0 && z > 0 && x < cells.X && z < cells.Z) {
                        Quad(
                            soup,
                            vertices,
                            cells,
                            here,
                            (x - 1, y, z - 1),
                            (x - 1, y, z),
                            (x, y, z),
                            (x, y, z - 1)
                        );
                    }

                    if (z + 1 < counts.Z && here != Inside(values[Index(counts, x, y, z + 1)])
                        && x > 0 && y > 0 && x < cells.X && y < cells.Y) {
                        Quad(
                            soup,
                            vertices,
                            cells,
                            here,
                            (x - 1, y - 1, z),
                            (x, y - 1, z),
                            (x, y, z),
                            (x - 1, y, z)
                        );
                    }
                }
            }
        }

        soup.Compact();
        return soup.TriangleCount > 0;
    }

    /// <summary>Where a cell's vertex goes: the mean of the crossings on its twelve edges.</summary>
    static Vector3? Place(
        float[] values,
        (int X, int Y, int Z) counts,
        Vector3 origin,
        float spacing,
        int x,
        int y,
        int z
    ) {
        var total = Vector3.Zero;
        var found = 0;

        for (var corner = 0; corner < 8; corner++) {
            var ax = x + (corner & 1);
            var ay = y + ((corner >> 1) & 1);
            var az = z + ((corner >> 2) & 1);

            for (var axis = 0; axis < 3; axis++) {
                var bit = 1 << axis;

                if ((corner & bit) != 0) {
                    continue;
                }

                var bx = ax + (axis == 0 ? 1 : 0);
                var by = ay + (axis == 1 ? 1 : 0);
                var bz = az + (axis == 2 ? 1 : 0);

                var one = values[Index(counts, ax, ay, az)];
                var two = values[Index(counts, bx, by, bz)];

                if (Inside(one) == Inside(two)) {
                    continue;
                }

                // Linear in the winding number toward the half threshold, which puts the vertex
                // where the surface is rather than in the middle of the edge — a mid-edge placement
                // is a staircase, which is the thing this whole stage exists to remove.
                var span = two - one;
                var amount = span != 0f ? (0.5f - one) / span : 0.5f;

                amount = Math.Clamp(amount, 0f, 1f);

                var from = origin + new Vector3(ax * spacing, ay * spacing, az * spacing);
                var to = origin + new Vector3(bx * spacing, by * spacing, bz * spacing);

                total += Vector3.Lerp(from, to, amount);
                found++;
            }
        }

        return found == 0 ? null : total / found;
    }

    static void Quad(
        TriangleSoup soup,
        int[] vertices,
        (int X, int Y, int Z) cells,
        bool forward,
        (int X, int Y, int Z) a,
        (int X, int Y, int Z) b,
        (int X, int Y, int Z) c,
        (int X, int Y, int Z) d
    ) {
        var q0 = vertices[Index(cells, a.X, a.Y, a.Z)];
        var q1 = vertices[Index(cells, b.X, b.Y, b.Z)];
        var q2 = vertices[Index(cells, c.X, c.Y, c.Z)];
        var q3 = vertices[Index(cells, d.X, d.Y, d.Z)];

        if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0) {
            return;
        }

        if (forward) {
            soup.Add(q0, q1, q2, 0);
            soup.Add(q0, q2, q3, 0);
        } else {
            soup.Add(q0, q2, q1, 0);
            soup.Add(q0, q3, q2, 0);
        }
    }

    static bool Inside(float winding) => winding >= 0.5f;

    static int Index((int X, int Y, int Z) counts, int x, int y, int z) =>
        (((z * counts.Y) + y) * counts.X) + x;
}
