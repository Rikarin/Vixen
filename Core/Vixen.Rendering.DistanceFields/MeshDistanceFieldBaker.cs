// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>Turns a triangle soup into a <see cref="MeshDistanceField" />.</summary>
/// <remarks>
///     <para>
///         Two halves that fail for different reasons, so they are solved separately.
///     </para>
///     <para>
///         <b>The distance is exact.</b> Every sample takes the true distance to the nearest triangle
///         from <see cref="TriangleTree" />, not a propagated one. Sweeping a chamfer or vector
///         distance transform over the grid is much faster and is the usual answer, and it is
///         approximate in a way that is invisible until a tracer takes a step slightly too long and
///         passes through a wall. An exact field is checkable against a closed form, which is the
///         exit criterion this bake was written to meet.
///     </para>
///     <para>
///         <b>The sign is voted on.</b> The textbook answer — count how many times a ray crosses the
///         surface, odd is inside — needs the mesh to be closed, and meshes are not closed. Artists
///         ship facades with no back, walls that are one quad, and shells with holes where something
///         else was meant to cover them, and a parity test on any of those inverts a whole region.
///         So each sample casts rays in many directions and asks a softer question: <i>how much of
///         the sky, from here, is a face seen from behind?</i> A point inside solid geometry sees
///         backfaces almost everywhere; a point outside sees them almost nowhere; and a point under
///         an open shell sees them over exactly the fraction of the sphere the shell covers, which is
///         what <see cref="DistanceFieldBuildSettings.BackfaceThreshold" /> is a dial on. This is the
///         approach Unreal takes for the same reason, and it degrades where parity inverts.
///     </para>
///     <para>
///         <b>The directions are a Fibonacci sphere, not random.</b> A spiral of
///         <see cref="DistanceFieldBuildSettings.SignRayCount" /> points covers the sphere about as
///         evenly as anything cheap does, and it takes no seed — so two bakes of one mesh are
///         byte-identical without a random source having to be threaded through the bake and pinned.
///         It also has no axis-aligned direction in it, which matters more than it sounds: a mesh
///         made of axis-aligned quads is exactly the mesh an axis-aligned ray hits edge-on.
///     </para>
/// </remarks>
public static class MeshDistanceFieldBaker {
    /// <summary>The golden angle, which is what makes a Fibonacci spiral cover a sphere evenly.</summary>
    const float GoldenAngle = 2.399963f;

    /// <summary>Bakes a field over a triangle soup.</summary>
    /// <param name="vertices">The positions.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <param name="settings">How finely, and how hard to work at the sign.</param>
    /// <returns>The field.</returns>
    /// <exception cref="ArgumentException">There are no triangles, or the indices are not whole triangles.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    public static MeshDistanceField Bake(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int> indices,
        DistanceFieldBuildSettings settings = default
    ) {
        settings.Validate();

        if (indices.Length == 0) {
            throw new ArgumentException("A field over no triangles describes nothing.", nameof(indices));
        }

        var tree = new TriangleTree(vertices, indices);
        var bounds = Expand(tree.Bounds, settings.BoundsExpansion);
        var resolution = ResolutionFor(bounds, settings.Resolution);
        var distances = new float[resolution.Volume];

        var directions = new Vector3[settings.SignRayCount];
        FillSphere(directions);

        var field = new MeshDistanceField(bounds, resolution, distances);
        var cell = field.CellSize;

        // One slice of constant Z per work item. Samples never read each other, so the split changes
        // nothing about the result — asserted by a test rather than left as a claim.
        void Slice(int z) {
            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    var position = bounds.Minimum + (cell * new Vector3(x, y, z));
                    var distance = MathF.Sqrt(tree.DistanceSquared(position));
                    var inside = IsInside(tree, position, directions, settings.BackfaceThreshold);

                    distances[field.Index(x, y, z)] = inside ? -distance : distance;
                }
            }
        }

        if (settings.Parallel && resolution.Z > 1) {
            System.Threading.Tasks.Parallel.For(0, resolution.Z, Slice);
        } else {
            for (var z = 0; z < resolution.Z; z++) {
                Slice(z);
            }
        }

        return field;
    }

    /// <summary>Whether enough of the sphere, seen from a point, is a face seen from behind.</summary>
    /// <param name="tree">The geometry.</param>
    /// <param name="position">The point.</param>
    /// <param name="directions">Which ways to look.</param>
    /// <param name="threshold">What fraction of backfaces counts as inside.</param>
    /// <returns>Whether the point is inside.</returns>
    /// <remarks>
    ///     A ray that hits nothing votes outside by saying nothing, which is the correct reading: it
    ///     escaped, so from that direction there is nothing above the point.
    /// </remarks>
    static bool IsInside(TriangleTree tree, Vector3 position, ReadOnlySpan<Vector3> directions, float threshold) {
        var backfaces = 0;

        foreach (var direction in directions) {
            if (tree.Raycast(position, direction, out var backface) && backface) {
                backfaces++;
            }
        }

        return backfaces >= threshold * directions.Length;
    }

    /// <summary>Grows a box by a fraction of its own size, and off zero where it is flat.</summary>
    /// <param name="bounds">The box.</param>
    /// <param name="fraction">How much of its size to add on every side.</param>
    /// <returns>The grown box.</returns>
    /// <remarks>
    ///     A flat mesh — a ground plane, a single quad — has a zero extent along one axis, and a
    ///     fraction of zero is zero. Falling back to a fraction of the box's <i>longest</i> side
    ///     gives that axis a thickness to sample across, without which the field would be one grid
    ///     point deep and could not be interpolated at all.
    /// </remarks>
    static BoundingBox Expand(BoundingBox bounds, float fraction) {
        var size = bounds.Size;
        var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        var flat = MathF.Max(longest * fraction, MathUtil.ZeroTolerance);

        // Proportional per axis, so an elongated mesh is not padded into a cube — the fallback is
        // for an axis with no extent at all, not for one that merely has little.
        float Margin(float extent) => extent > MathUtil.ZeroTolerance ? extent * fraction : flat;

        var margin = new Vector3(Margin(size.X), Margin(size.Y), Margin(size.Z));

        return new(bounds.Minimum - margin, bounds.Maximum + margin);
    }

    /// <summary>How many samples along each axis, so that voxels are as near cubic as they can be.</summary>
    /// <param name="bounds">The box the field covers.</param>
    /// <param name="longestAxisResolution">How many samples the longest axis is asked for.</param>
    /// <returns>The per-axis counts, never below two.</returns>
    static Int3 ResolutionFor(BoundingBox bounds, int longestAxisResolution) {
        var size = bounds.Size;
        var longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));

        if (longest <= MathUtil.ZeroTolerance) {
            return new(2, 2, 2);
        }

        int Axis(float extent) =>
            Math.Max(2, (int)MathF.Round(longestAxisResolution * (extent / longest)));

        return new(Axis(size.X), Axis(size.Y), Axis(size.Z));
    }

    /// <summary>Fills a span with directions spiralling evenly over the sphere.</summary>
    /// <param name="directions">Where to put them.</param>
    static void FillSphere(Span<Vector3> directions) {
        for (var index = 0; index < directions.Length; index++) {
            var z = 1f - (2f * (index + 0.5f) / directions.Length);
            var radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
            var angle = index * GoldenAngle;

            directions[index] = new(radius * MathF.Cos(angle), radius * MathF.Sin(angle), z);
        }
    }
}
