// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A target triangle, with everything a texel on it needs.</summary>
/// <param name="Scaled">Its corners in atlas texels.</param>
/// <param name="Points">Its corners on the target.</param>
/// <param name="Shading">Its corner normals, which frame the result.</param>
/// <param name="Rays">The averaged direction at each corner, which the rays follow.</param>
/// <param name="Cage">Its corners on the cage, or null.</param>
/// <param name="Tangents">Its corners' MikkTSpace tangents, with the bitangent's sign in <c>W</c>.</param>
/// <param name="Normal">The flat normal, for a corner normal that said nothing.</param>
/// <param name="Part">Which part it belongs to, or <c>-1</c>.</param>
sealed record ChartTriangle(
    Vector2[] Scaled,
    Vector3[] Points,
    Vector3[] Shading,
    Vector3[] Rays,
    Vector3[]? Cage,
    Vector4[] Tangents,
    Vector3 Normal,
    int Part
);

/// <summary>Which chart triangle owns each texel of the atlas.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Claiming comes before casting, and the lowest triangle wins.</b> First chart to claim
///         keeps a texel, in face order: two charts overlapping in the atlas is a packing defect and a
///         bake that blended them would hide it, while one that let the later win would make the answer
///         depend on face order, which § D14 forbids just as firmly. Triangles run in parallel, so
///         "first" is a compare on the index and not on the clock.
///     </para>
///     <para>
///         ⚠ <b>Conservative: a texel is claimed if a triangle touches its square</b>, and
///         <see cref="AtlasRaster" /> says why a pixel-centre rule loses a chart's outer row.
///     </para>
/// </remarks>
sealed class BakeCharts {
    /// <summary>The texel is nobody's.</summary>
    const int Unclaimed = int.MaxValue;

    readonly ChartTriangle[] triangles;
    readonly int[] owners;
    readonly int[]? others;
    readonly bool[] rowHasContent;

    BakeCharts(ChartTriangle[] triangles, int[] owners, int[]? others, bool[] rowHasContent) {
        this.triangles = triangles;
        this.owners = owners;
        this.others = others;
        this.rowHasContent = rowHasContent;
    }

    /// <summary>The triangle owning a texel, or null.</summary>
    /// <param name="index">The texel.</param>
    /// <returns>Its owner.</returns>
    public ChartTriangle? Owner(int index) => owners[index] == Unclaimed ? null : triangles[owners[index]];

    /// <summary>A second triangle touching a texel, for a sample that falls off the owner.</summary>
    /// <param name="index">The texel.</param>
    /// <returns>The highest triangle touching it, or null when there is only the owner.</returns>
    /// <remarks>
    ///     ⚠ <b>Only a bake with samples keeps these.</b> Most texels on a triangle edge have exactly two
    ///     triangles, and a sample in the other one clamped onto the owner would read the shared edge
    ///     instead of its own point: a faint line along every edge of the target, on a map whose whole
    ///     reason for being supersampled is to have no such thing. Where three meet, one sample in a
    ///     hundred takes the clamp, which is the same small error at one point.
    /// </remarks>
    public ChartTriangle? Other(int index) =>
        others is null || others[index] < 0 || others[index] == owners[index] ? null : triangles[others[index]];

    /// <summary>Whether a row of the atlas holds no chart at all.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether it can be skipped.</returns>
    public bool RowIsEmpty(int row) => !rowHasContent[row];

    /// <summary>How many rows hold chart content.</summary>
    /// <returns>The denominator of a bake's progress.</returns>
    public long RowsWithContent() => rowHasContent.Count(static row => row);

    /// <summary>Writes which texels are chart content.</summary>
    /// <param name="coverage">One per texel.</param>
    /// <returns>How many are.</returns>
    public int ClaimCoverage(bool[] coverage) {
        var covered = 0;

        for (var index = 0; index < owners.Length; index++) {
            coverage[index] = owners[index] != Unclaimed;
            covered += coverage[index] ? 1 : 0;
        }

        return covered;
    }

    /// <summary>Lays a target's triangles out over its atlas.</summary>
    /// <param name="target">The mesh with the atlas.</param>
    /// <param name="settings">The bake.</param>
    /// <param name="options">How many threads may be used.</param>
    /// <param name="cancellationToken">What stops it, which leaves the charts incomplete.</param>
    /// <returns>The owners.</returns>
    public static BakeCharts Build(
        EditMesh target,
        BakeSettings settings,
        ParallelOptions options,
        CancellationToken cancellationToken
    ) {
        var resolution = settings.Resolution;
        var shading = target.Normals.Length == target.CornerCount ? target.Normals.ToArray() : Geometric(target);
        var averaged = Averaged(target, shading);
        var triangles = Triangles(target, settings, shading, averaged);
        var owners = new int[resolution * resolution];
        var others = settings.Supersampling > 1 ? new int[owners.Length] : null;
        var rows = new bool[resolution];

        if (others is not null) {
            Array.Fill(others, -1);
        }

        Array.Fill(owners, Unclaimed);

        Parallel.For(0, triangles.Length, options, (triangle, loop) => {
            if (cancellationToken.IsCancellationRequested) {
                loop.Stop();

                return;
            }

            var scaled = triangles[triangle].Scaled;

            if (Box(scaled, resolution) is not { } box) {
                return;
            }

            for (var y = box.Y0; y <= box.Y1; y++) {
                var touched = false;

                for (var x = box.X0; x <= box.X1; x++) {
                    if (!AtlasRaster.Overlaps(scaled[0], scaled[1], scaled[2], new(x, y), new(x + 1, y + 1))) {
                        continue;
                    }

                    Lowest(ref owners[(y * resolution) + x], triangle);

                    if (others is not null) {
                        Highest(ref others[(y * resolution) + x], triangle);
                    }

                    touched = true;
                }

                if (touched) {
                    Volatile.Write(ref rows[y], true);
                }
            }
        });

        return new(triangles, owners, others, rows);
    }

    static void Lowest(ref int slot, int triangle) {
        var seen = Volatile.Read(ref slot);

        while (triangle < seen) {
            var was = Interlocked.CompareExchange(ref slot, triangle, seen);

            if (was == seen) {
                break;
            }

            seen = was;
        }
    }

    static void Highest(ref int slot, int triangle) {
        var seen = Volatile.Read(ref slot);

        while (triangle > seen) {
            var was = Interlocked.CompareExchange(ref slot, triangle, seen);

            if (was == seen) {
                break;
            }

            seen = was;
        }
    }

    /// <summary>A target's faces as triangles, each with the tangents a renderer will rebuild for it.</summary>
    /// <remarks>
    ///     ⚠ <b>The basis is computed over the triangles the atlas is rasterised from</b>, so that a quad
    ///     here is split where it is split in the file a renderer reads, and one vector per corner is
    ///     interpolated exactly as a vertex shader's output would be. See <see cref="MikkTSpace" />.
    /// </remarks>
    static ChartTriangle[] Triangles(EditMesh target, BakeSettings settings, Vector3[] shading, Vector3[] averaged) {
        var cage = settings.Cage;
        var parts = settings.Parts?.Target;
        var made = new List<ChartTriangle>(target.FaceCount * 2);
        var corners = new List<Vector3>(target.FaceCount * 6);
        var normals = new List<Vector3>(target.FaceCount * 6);
        var coordinates = new List<Vector2>(target.FaceCount * 6);

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);
            var around = cage is null ? default : cage.CornersOf(face);

            for (var corner = 1; corner + 1 < loop.Length; corner++) {
                Span<int> slots = [entry.Start, entry.Start + corner, entry.Start + corner + 1];

                var scaled = new Vector2[3];
                var points = new Vector3[3];
                var shaded = new Vector3[3];
                var rays = new Vector3[3];
                var outer = cage is null ? null : new Vector3[3];
                var tangents = new Vector4[3];

                for (var at = 0; at < 3; at++) {
                    var slot = slots[at];
                    var position = loop[slot - entry.Start];

                    scaled[at] = target.TexCoords[slot] * settings.Resolution;
                    points[at] = target.Positions[position];
                    shaded[at] = shading[slot];
                    rays[at] = averaged[slot];
                    corners.Add(points[at]);
                    normals.Add(shading[slot]);
                    coordinates.Add(target.TexCoords[slot]);

                    if (outer is not null) {
                        outer[at] = cage!.Positions[around[slot - entry.Start]];
                    }
                }

                var flat = ScaleSafe.Unit(Vector3.Cross(points[1] - points[0], points[2] - points[0]));

                made.Add(new(scaled, points, shaded, rays, outer, tangents, flat, parts?[face] ?? -1));
            }
        }

        var solved = MikkTSpace.Generate([.. corners], [.. normals], [.. coordinates]);

        for (var triangle = 0; triangle < made.Count; triangle++) {
            solved.AsSpan(triangle * 3, 3).CopyTo(made[triangle].Tangents);
        }

        return [.. made];
    }

    /// <summary>Per-corner normals for a target that arrived without a layer.</summary>
    static Vector3[] Geometric(EditMesh target) {
        var normals = new Vector3[target.CornerCount];

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var normal = ScaleSafe.Unit(target.Normal(face));

            for (var at = 0; at < entry.Count; at++) {
                normals[entry.Start + at] = normal;
            }
        }

        return normals;
    }

    /// <summary>Each corner's normal, averaged with every other corner standing on the same point.</summary>
    /// <remarks>
    ///     ⚠ <b>By coordinates and not by position index.</b> A file splits a vertex for a hard edge and
    ///     a weld is the importer's to do, so the two sides of an edge may arrive as two positions that
    ///     are the same point. An average that went by index would leave exactly that edge unaveraged.
    ///     <br />
    ///     ⚠ <b>A corner that agrees with its neighbours is left bit for bit as it was</b>, so a smooth
    ///     surface casts the rays it always cast.
    /// </remarks>
    static Vector3[] Averaged(EditMesh target, Vector3[] shading) {
        Dictionary<Vector3, Vector3> sums = [];
        Dictionary<Vector3, bool> differ = [];

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);

            for (var at = 0; at < entry.Count; at++) {
                var point = target.Positions[loop[at]];
                var normal = shading[entry.Start + at];

                if (sums.TryGetValue(point, out var sum)) {
                    sums[point] = sum + normal;
                    differ[point] = differ[point] || normal != First(sum, normal);
                } else {
                    sums[point] = normal;
                    differ[point] = false;
                    continue;
                }
            }
        }

        var averaged = new Vector3[shading.Length];

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);

            for (var at = 0; at < entry.Count; at++) {
                var point = target.Positions[loop[at]];
                var corner = shading[entry.Start + at];
                var unit = differ[point] ? ScaleSafe.Unit(sums[point]) : corner;

                averaged[entry.Start + at] = unit.LengthSquared() > 0f ? unit : corner;
            }
        }

        return averaged;
    }

    /// <summary>What a running sum tells about whether the corners so far agree with a new one.</summary>
    /// <remarks>Parallel to it means agreement, since a sum of copies of a vector is a multiple of it.</remarks>
    static Vector3 First(Vector3 sum, Vector3 normal) {
        var unit = ScaleSafe.Unit(sum);

        return (unit - normal).LengthSquared() < 1e-10f ? normal : unit;
    }

    /// <summary>The texels a chart triangle could touch, or null where it lies off the atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>Floor and ceiling rather than a round, and then one texel of slack on each side.</b> A
    ///     triangle that ends at x = 4.0 exactly still touches the <i>edge</i> of texel 3, and
    ///     conservative coverage counts a shared edge — the separating-axis test is what decides, so
    ///     the bounds only have to be generous enough not to exclude a candidate it would have
    ///     accepted.
    /// </remarks>
    static (int X0, int Y0, int X1, int Y1)? Box(Vector2[] scaled, int resolution) {
        var minimum = Vector2.Min(scaled[0], Vector2.Min(scaled[1], scaled[2]));
        var maximum = Vector2.Max(scaled[0], Vector2.Max(scaled[1], scaled[2]));

        if (maximum.X < 0f || maximum.Y < 0f || minimum.X > resolution || minimum.Y > resolution) {
            return null;
        }

        return (
            Math.Clamp((int)MathF.Floor(minimum.X) - 1, 0, resolution - 1),
            Math.Clamp((int)MathF.Floor(minimum.Y) - 1, 0, resolution - 1),
            Math.Clamp((int)MathF.Ceiling(maximum.X) + 1, 0, resolution - 1),
            Math.Clamp((int)MathF.Ceiling(maximum.Y) + 1, 0, resolution - 1)
        );
    }
}
