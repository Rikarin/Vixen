// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Which faces of the source a ray may stop at.</summary>
/// <remarks>
///     A face is met from the front when the ray runs against its normal — the winding is
///     anticlockwise seen from the side it faces.
/// </remarks>
enum RayFacing : byte {
    /// <summary>Either side, which is what every ray did before the bake could choose.</summary>
    Any,

    /// <summary>Only a face the ray runs against the normal of.</summary>
    Front,

    /// <summary>Only a face the ray runs along the normal of.</summary>
    Back
}

/// <summary>The bake's own ray tree: a surface-area-heuristic hierarchy, four children wide.</summary>
/// <remarks>
///     <para>
///         <b>Why a bake does not use <see cref="TriangleTree" />.</b> That tree is a median split with
///         one box test per node, which is the right size for a transfer asking a few thousand
///         closest-point questions. A 4K occlusion bake asks about four billion ray questions: on a
///         two-million-triangle source it managed 0.19 M rays a second on one core, and nothing about
///         the arithmetic there is wasteful on its own terms. The gain is structural: the surface area
///         heuristic stops rays visiting boxes they have no business in, four boxes and four triangles
///         are tested at a time, and an occlusion ray stops at its first hit.
///     </para>
///     <para>
///         ⚠ <b>Vectors find the candidates and the scalar test decides.</b> A hit from the wide test
///         is checked again by the same scale-relative rules <see cref="TriangleTree" /> uses, so a ray
///         starting on a surface is rejected at its own origin here exactly as it is there, at any
///         model scale. An absolute epsilon inside the vector test would have been a claim about how
///         big a model is.
///     </para>
///     <para>
///         ⚠ <b>The build is parallel and its output is not.</b> A subtree's nodes occupy a slot range
///         derived from its triangle count alone and the flattening walks the result in one fixed
///         order, so the same mesh is the same tree on any thread count. A bake's bytes are a content
///         hash, which is the only reason that matters.
///     </para>
///     <para>
///         ⚠ <b>Immutable once built, and every query keeps its state on the stack</b>, which is what
///         lets the casting loop share one tree across every thread with no lock.
///     </para>
/// </remarks>
sealed class BakeTree {
    /// <summary>The most triangles a leaf holds, which is the width of a triangle block.</summary>
    const int LeafSize = 4;

    /// <summary>How deep the binary build goes before it stops consulting the heuristic.</summary>
    /// <remarks>
    ///     A wide node consumes at least one binary level, and a traversal's stack holds at most three
    ///     siblings per level, so this is what makes <see cref="StackSize" /> a proof, not a guess.
    /// </remarks>
    const int MaxDepth = 64;

    /// <summary>How many entries a traversal's stack can ever need.</summary>
    const int StackSize = MaxDepth * 3 + 4;

    /// <summary>How many split candidates are weighed on an axis.</summary>
    const int Bins = 16;

    /// <summary>How many triangles a range must have before splitting it is worth another thread.</summary>
    const int ParallelRange = 4096;

    /// <summary>The cost of a box test against a triangle test, in the heuristic.</summary>
    /// <remarks>
    ///     A quarter, since a leaf costs a vector triangle test per four triangles and a node costs a
    ///     vector box test per four children. Only the ratio matters.
    /// </remarks>
    const float TraversalCost = 0.25f;

    /// <summary>A stand-in for the inverse of a zero component, finite so that 0 × it is not a NaN.</summary>
    const float ParallelInverse = 1e30f;

    readonly Vector3[] vertices;
    readonly int[] indices;
    readonly WideNode[] nodes;
    readonly TriangleBlock[] blocks;

    /// <summary>How many triangles the tree holds.</summary>
    public int TriangleCount { get; }

    /// <summary>Builds a tree over a triangle soup.</summary>
    /// <param name="vertices">The positions, copied.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="members">Which of those triangles to hold, or null for all of them.</param>
    /// <exception cref="ArgumentException">The indices are not a whole number of triangles.</exception>
    public BakeTree(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, IReadOnlyList<int>? members = null) {
        if (indices.Length % 3 != 0) {
            throw new ArgumentException(
                $"{indices.Length} indices is not a whole number of triangles.",
                nameof(indices)
            );
        }

        this.vertices = [..vertices];
        this.indices = [..indices];
        TriangleCount = members?.Count ?? indices.Length / 3;

        var binary = new Builder(this.vertices, this.indices, members);
        var flattened = new Flattener(binary, this.vertices, this.indices);

        flattened.Run();

        nodes = [..flattened.Nodes];
        blocks = [..flattened.Blocks];
    }

    /// <summary>Whether anything lies along a ray.</summary>
    /// <param name="origin">Where it starts.</param>
    /// <param name="direction">Where it ends, relative to the origin: its length is the reach.</param>
    /// <param name="facing">Which faces count: any, only those the ray meets from the front, or from behind.</param>
    /// <returns>Whether it strikes a triangle that counts.</returns>
    /// <remarks>
    ///     ⚠ <b>The first hit ends it, which is most of why an occlusion ray is cheap.</b> A visibility
    ///     question does not care which triangle answered, so no box is tested against a nearer bound
    ///     and no sort happens. It does nothing to a ray that reaches open sky, which is the expensive
    ///     kind and the reason <see cref="TraversalCost" /> matters.
    /// </remarks>
    [SkipLocalsInit]
    public bool Occluded(Vector3 origin, Vector3 direction, RayFacing facing = RayFacing.Any) {
        if (TriangleCount == 0) {
            return false;
        }

        var ray = new Ray(origin, direction);
        Span<int> stack = stackalloc int[StackSize];
        Span<float> order = stackalloc float[LeafSize];
        var depth = 0;
        stack[depth++] = 0;

        ref var firstNode = ref MemoryMarshal.GetArrayDataReference(nodes);
        ref var firstBlock = ref MemoryMarshal.GetArrayDataReference(blocks);

        while (depth > 0) {
            var child = stack[--depth];

            if (child < 0) {
                ref var block = ref Unsafe.Add(ref firstBlock, ~child);

                if (Occludes(ref block, in ray, facing)) {
                    return true;
                }

                continue;
            }

            ref var node = ref Unsafe.Add(ref firstNode, child);
            var mask = Slabs(ref node, in ray, Vector128.Create(1f), out var entering);
            var from = depth;

            // ⚠ Nearest first even though any hit will do. A ray starts on a surface and is usually
            // blocked by something close, and the box it would otherwise open first may be across the
            // model, which is a cache miss to learn nothing. Measured on two million triangles: 12%
            // faster than the branch-free unordered push, which won on a small mesh and lost here.
            while (mask != 0) {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(mask);

                mask &= mask - 1;

                var enter = Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref entering), slot);
                var at = depth++;

                while (at > from && order[at - from - 1] < enter) {
                    order[at - from] = order[at - from - 1];
                    stack[at] = stack[at - 1];
                    at--;
                }

                order[at - from] = enter;
                stack[at] = node.Child(slot);
            }
        }

        return false;
    }

    /// <summary>The nearest hit along a ray, on either face.</summary>
    /// <param name="origin">Where it starts.</param>
    /// <param name="direction">Where it ends, relative to the origin: its length is the reach.</param>
    /// <param name="facing">Which faces count. One that does not is passed through, not stopped at.</param>
    /// <returns>The hit, with its distance as a fraction of <paramref name="direction" />, or a miss.</returns>
    [SkipLocalsInit]
    public TriangleHit Raycast(Vector3 origin, Vector3 direction, RayFacing facing = RayFacing.Any) {
        var best = new TriangleHit(-1, origin, float.PositiveInfinity, Vector3.Zero, false);

        if (TriangleCount == 0) {
            return best;
        }

        var ray = new Ray(origin, direction);
        var nearest = 1f;

        Span<int> stack = stackalloc int[StackSize];
        Span<float> entries = stackalloc float[StackSize];
        var depth = 0;

        stack[depth] = 0;
        entries[depth++] = 0f;

        ref var firstNode = ref MemoryMarshal.GetArrayDataReference(nodes);
        ref var firstBlock = ref MemoryMarshal.GetArrayDataReference(blocks);

        while (depth > 0) {
            depth--;

            var child = stack[depth];

            // ⚠ Tested again on the way off the stack. Its entry was measured against the bound as it
            // stood when it was pushed, and a hit since may have shrunk that bound past it.
            if (entries[depth] > nearest) {
                continue;
            }

            if (child < 0) {
                ref var block = ref Unsafe.Add(ref firstBlock, ~child);

                Nearest(ref block, in ray, facing, ref nearest, ref best);

                continue;
            }

            ref var node = ref Unsafe.Add(ref firstNode, child);
            var mask = Slabs(ref node, in ray, Vector128.Create(nearest), out var entering);
            var from = depth;

            while (mask != 0) {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(mask);

                mask &= mask - 1;

                var enter = entering.GetElement(slot);
                var at = depth++;

                // Insertion, far first, so that the nearest child is popped first. There are four
                // entries at most, and a closest-hit query that visits near boxes first is what
                // lets the shrinking bound cull the rest.
                while (at > from && entries[at - 1] < enter) {
                    entries[at] = entries[at - 1];
                    stack[at] = stack[at - 1];
                    at--;
                }

                entries[at] = enter;
                stack[at] = node.Child(slot);
            }
        }

        return best;
    }

    /// <summary>The nearest point on the surface.</summary>
    /// <param name="point">The query.</param>
    /// <returns>Which triangle, where on it, and how far, or a triangle of <c>-1</c> over an empty tree.</returns>
    [SkipLocalsInit]
    public ClosestTriangle Closest(Vector3 point) {
        var found = new ClosestTriangle(-1, point, float.PositiveInfinity, Vector3.Zero);

        if (TriangleCount == 0) {
            return found;
        }

        Span<int> stack = stackalloc int[StackSize];
        Span<float> entries = stackalloc float[StackSize];
        var depth = 0;

        stack[depth] = 0;
        entries[depth++] = 0f;

        var (px, py, pz) = (Vector128.Create(point.X), Vector128.Create(point.Y), Vector128.Create(point.Z));

        while (depth > 0) {
            depth--;

            var child = stack[depth];

            if (entries[depth] >= found.DistanceSquared) {
                continue;
            }

            if (child < 0) {
                ref var block = ref blocks[~child];

                for (var slot = 0; slot < LeafSize; slot++) {
                    var triangle = block.Triangle(slot);

                    if (triangle < 0) {
                        break;
                    }

                    Corners(triangle, out var a, out var b, out var c);

                    var nearest = TriangleTree.ClosestPointOnTriangle(point, a, b, c, out var weights);
                    var distance = Vector3.DistanceSquared(point, nearest);

                    if (distance < found.DistanceSquared) {
                        found = new(triangle, nearest, distance, weights);
                    }
                }

                continue;
            }

            ref var node = ref nodes[child];

            var dx = Vector128.Max(Vector128.Max(node.MinX - px, px - node.MaxX), Vector128<float>.Zero);
            var dy = Vector128.Max(Vector128.Max(node.MinY - py, py - node.MaxY), Vector128<float>.Zero);
            var dz = Vector128.Max(Vector128.Max(node.MinZ - pz, pz - node.MaxZ), Vector128<float>.Zero);
            var squared = dx * dx + dy * dy + dz * dz;
            var from = depth;

            for (var slot = 0; slot < LeafSize; slot++) {
                var entry = squared.GetElement(slot);

                if (node.Child(slot) == Absent || entry >= found.DistanceSquared) {
                    continue;
                }

                var at = depth++;

                while (at > from && entries[at - 1] < entry) {
                    entries[at] = entries[at - 1];
                    stack[at] = stack[at - 1];
                    at--;
                }

                entries[at] = entry;
                stack[at] = node.Child(slot);
            }
        }

        return found;
    }

    /// <summary>The value of a child slot with nothing in it.</summary>
    const int Absent = int.MaxValue;

    /// <summary>Which of a node's four children a ray enters, and where.</summary>
    /// <returns>A mask of the children entered, in bit order.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Slabs(ref WideNode node, in Ray ray, Vector128<float> limit, out Vector128<float> entering) {
        var lowX = (node.MinX - ray.OriginX) * ray.InverseX;
        var highX = (node.MaxX - ray.OriginX) * ray.InverseX;
        var lowY = (node.MinY - ray.OriginY) * ray.InverseY;
        var highY = (node.MaxY - ray.OriginY) * ray.InverseY;
        var lowZ = (node.MinZ - ray.OriginZ) * ray.InverseZ;
        var highZ = (node.MaxZ - ray.OriginZ) * ray.InverseZ;

        var near = Vector128.MaxNative(
            Vector128.MaxNative(Vector128.MinNative(lowX, highX), Vector128.MinNative(lowY, highY)),
            Vector128.MinNative(lowZ, highZ)
        );

        var far = Vector128.MinNative(
            Vector128.MinNative(Vector128.MaxNative(lowX, highX), Vector128.MaxNative(lowY, highY)),
            Vector128.MaxNative(lowZ, highZ)
        );

        entering = Vector128.MaxNative(near, Vector128<float>.Zero);

        // ⚠ An unused slot is ruled out by what it holds and not by its box. An inverted box is not
        // empty to a slab test: the two planes swap, and it reads as all of space.
        var open = Vector128.LessThanOrEqual(entering, Vector128.MinNative(far, limit)).AsInt32()
            & ~Vector128.Equals(node.Children, Vector128.Create(Absent));

        return open.ExtractMostSignificantBits();
    }

    /// <summary>Which of a block's triangles a ray may strike.</summary>
    /// <remarks>
    ///     Möller–Trumbore on four triangles, with the comparisons multiplied through by the determinant
    ///     so that the division is never done: it is the slowest instruction here, and nearly every
    ///     candidate is rejected without needing what it would have produced. The scalar test decides
    ///     what a survivor is worth.
    ///     <br />
    ///     ⚠ <b>Every comparison is written so that a NaN fails it</b>, which is how an unused slot and
    ///     a degenerate triangle drop out without a branch of their own.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Candidates(ref TriangleBlock block, in Ray ray, Vector128<float> limit) {
        var acrossX = ray.DirectionY * block.EdgeZ2 - ray.DirectionZ * block.EdgeY2;
        var acrossY = ray.DirectionZ * block.EdgeX2 - ray.DirectionX * block.EdgeZ2;
        var acrossZ = ray.DirectionX * block.EdgeY2 - ray.DirectionY * block.EdgeX2;

        var determinant = block.EdgeX1 * acrossX + block.EdgeY1 * acrossY + block.EdgeZ1 * acrossZ;
        var sign = determinant & Vector128.Create(-0f);
        var size = Vector128.Abs(determinant);

        var toX = ray.OriginX - block.X;
        var toY = ray.OriginY - block.Y;
        var toZ = ray.OriginZ - block.Z;

        var u = (toX * acrossX + toY * acrossY + toZ * acrossZ) ^ sign;

        var alongX = toY * block.EdgeZ1 - toZ * block.EdgeY1;
        var alongY = toZ * block.EdgeX1 - toX * block.EdgeZ1;
        var alongZ = toX * block.EdgeY1 - toY * block.EdgeX1;

        var v = (ray.DirectionX * alongX + ray.DirectionY * alongY + ray.DirectionZ * alongZ) ^ sign;
        var distance = (block.EdgeX2 * alongX + block.EdgeY2 * alongY + block.EdgeZ2 * alongZ) ^ sign;

        var inside = Vector128.GreaterThanOrEqual(u, Vector128<float>.Zero)
            & Vector128.GreaterThanOrEqual(v, Vector128<float>.Zero)
            & Vector128.LessThanOrEqual(u + v, size)
            & Vector128.GreaterThan(distance, Vector128<float>.Zero)
            & Vector128.LessThan(distance, limit * size);

        return inside.ExtractMostSignificantBits();
    }

    /// <summary>Whether a ray strikes anything in a block.</summary>
    static bool Occludes(ref TriangleBlock block, in Ray ray, RayFacing facing) {
        var mask = Candidates(ref block, in ray, Vector128<float>.One);

        while (mask != 0) {
            var slot = System.Numerics.BitOperations.TrailingZeroCount(mask);

            mask &= mask - 1;

            if (Strikes(ref block, slot, in ray, 1f, out _, out var backface, out _) && Counts(facing, backface)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records the nearest hit in a block, shrinking the bound.</summary>
    static void Nearest(
        ref TriangleBlock block,
        in Ray ray,
        RayFacing facing,
        ref float nearest,
        ref TriangleHit best
    ) {
        var mask = Candidates(ref block, in ray, Vector128.Create(nearest));

        while (mask != 0) {
            var slot = System.Numerics.BitOperations.TrailingZeroCount(mask);

            mask &= mask - 1;

            if (!Strikes(ref block, slot, in ray, nearest, out var distance, out var backface, out var weights)
                || !Counts(facing, backface)) {
                continue;
            }

            nearest = distance;
            best = new(block.Triangle(slot), ray.Origin + ray.Direction * distance, distance, weights, backface);
        }
    }

    /// <summary>Whether a hit on a face the ray met from the front, or from behind, is one that counts.</summary>
    /// <remarks>
    ///     ⚠ <b>A face that does not count is passed through rather than stopping the ray</b>, so a
    ///     traversal that drops one keeps looking for the next. Stopping at it would be the old answer
    ///     with the hit thrown away — a miss where the surface behind it was there to be found.
    /// </remarks>
    static bool Counts(RayFacing facing, bool backface) =>
        facing switch {
            RayFacing.Front => !backface,
            RayFacing.Back => backface,
            _ => true
        };

    /// <summary>The scalar test: the same rules <see cref="TriangleTree" /> applies, in the same order.</summary>
    /// <remarks>
    ///     ⚠ <b>Both guards are relative, for the reasons that tree's own remarks give.</b> The
    ///     determinant carries the model's units squared, and a hit distance is a length, so an
    ///     absolute epsilon on either is a statement about how big a model has to be before it can be
    ///     hit at all.
    ///     <br />
    ///     ⚠ <b>It reads the block and never the mesh.</b> On a surface of millions of triangles the
    ///     cost of a ray is cache misses and not arithmetic, and an occlusion ray ends here: following
    ///     an index and three positions would be four more misses per ray to read numbers the block
    ///     already holds.
    /// </remarks>
    static bool Strikes(
        ref TriangleBlock block,
        int slot,
        in Ray ray,
        float limit,
        out float distance,
        out bool backface,
        out Vector3 weights
    ) {
        distance = 0f;
        backface = false;
        weights = Vector3.Zero;

        var a = block.Corner(slot);
        var edge1 = block.Edge1(slot);
        var edge2 = block.Edge2(slot);
        var across = Vector3.Cross(ray.Direction, edge2);
        var determinant = Vector3.Dot(edge1, across);
        var span = edge1.LengthSquared() * across.LengthSquared();

        if (determinant * determinant <= MathUtil.ZeroTolerance * MathUtil.ZeroTolerance * span) {
            return false;
        }

        var inverse = 1f / determinant;
        var toOrigin = ray.Origin - a;
        var u = Vector3.Dot(toOrigin, across) * inverse;

        if (u is < 0f or > 1f) {
            return false;
        }

        var along = Vector3.Cross(toOrigin, edge1);
        var v = Vector3.Dot(ray.Direction, along) * inverse;

        if (v < 0f || u + v > 1f) {
            return false;
        }

        distance = Vector3.Dot(edge2, along) * inverse;

        if (distance <= 0f
            || distance >= limit
            || distance * distance * ray.Direction.LengthSquared()
            <= MathUtil.ZeroTolerance * MathUtil.ZeroTolerance * edge1.LengthSquared()) {
            return false;
        }

        backface = determinant < 0f;
        weights = new(1f - u - v, u, v);

        return true;
    }

    void Corners(int triangle, out Vector3 a, out Vector3 b, out Vector3 c) {
        a = vertices[indices[triangle * 3]];
        b = vertices[indices[triangle * 3 + 1]];
        c = vertices[indices[triangle * 3 + 2]];
    }

    /// <summary>A ray, splatted once so that four-wide tests do not each pay for it.</summary>
    readonly struct Ray {
        public readonly Vector128<float> OriginX;
        public readonly Vector128<float> OriginY;
        public readonly Vector128<float> OriginZ;
        public readonly Vector128<float> DirectionX;
        public readonly Vector128<float> DirectionY;
        public readonly Vector128<float> DirectionZ;
        public readonly Vector128<float> InverseX;
        public readonly Vector128<float> InverseY;
        public readonly Vector128<float> InverseZ;
        public readonly Vector3 Origin;
        public readonly Vector3 Direction;

        public Ray(Vector3 origin, Vector3 direction) {
            Origin = origin;
            Direction = direction;
            OriginX = Vector128.Create(origin.X);
            OriginY = Vector128.Create(origin.Y);
            OriginZ = Vector128.Create(origin.Z);
            DirectionX = Vector128.Create(direction.X);
            DirectionY = Vector128.Create(direction.Y);
            DirectionZ = Vector128.Create(direction.Z);
            InverseX = Vector128.Create(direction.X == 0f ? ParallelInverse : 1f / direction.X);
            InverseY = Vector128.Create(direction.Y == 0f ? ParallelInverse : 1f / direction.Y);
            InverseZ = Vector128.Create(direction.Z == 0f ? ParallelInverse : 1f / direction.Z);
        }
    }

    /// <summary>Four children's boxes, by axis, and what each child is.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct WideNode {
        public Vector128<float> MinX;
        public Vector128<float> MinY;
        public Vector128<float> MinZ;
        public Vector128<float> MaxX;
        public Vector128<float> MaxY;
        public Vector128<float> MaxZ;
        public Vector128<int> Children;

        /// <summary>A child: a node index, or the complement of a block index, or <see cref="Absent" />.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Child(int slot) => Unsafe.Add(ref Unsafe.As<Vector128<int>, int>(ref Children), slot);
    }

    /// <summary>Up to four triangles, each as a corner and two edges, by axis.</summary>
    /// <remarks>An unused slot holds NaN coordinates and a triangle of <c>-1</c>.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    struct TriangleBlock {
        public Vector128<float> X;
        public Vector128<float> Y;
        public Vector128<float> Z;
        public Vector128<float> EdgeX1;
        public Vector128<float> EdgeY1;
        public Vector128<float> EdgeZ1;
        public Vector128<float> EdgeX2;
        public Vector128<float> EdgeY2;
        public Vector128<float> EdgeZ2;
        public Vector128<int> Triangles;

        /// <summary>The triangle in a slot, or <c>-1</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Triangle(int slot) => Unsafe.Add(ref Unsafe.As<Vector128<int>, int>(ref Triangles), slot);

        /// <summary>The first corner in a slot.</summary>
        public Vector3 Corner(int slot) => new(Lane(ref X, slot), Lane(ref Y, slot), Lane(ref Z, slot));

        /// <summary>The first edge in a slot.</summary>
        public Vector3 Edge1(int slot) => new(Lane(ref EdgeX1, slot), Lane(ref EdgeY1, slot), Lane(ref EdgeZ1, slot));

        /// <summary>The second edge in a slot.</summary>
        public Vector3 Edge2(int slot) => new(Lane(ref EdgeX2, slot), Lane(ref EdgeY2, slot), Lane(ref EdgeZ2, slot));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Lane(ref Vector128<float> vector, int slot) =>
            Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vector), slot);
    }

    /// <summary>A binary node, before the tree is flattened.</summary>
    struct BinaryNode {
        public Vector3 Minimum;
        public Vector3 Maximum;

        /// <summary>The first triangle in the order, for a leaf.</summary>
        public int Start;

        /// <summary>How many triangles it has, or zero for an inner node.</summary>
        public int Count;

        /// <summary>The children's slots, for an inner node.</summary>
        public int Left;

        /// <summary>The second child's slot.</summary>
        public int Right;

        /// <summary>Half the surface area of the box.</summary>
        public readonly float Area() {
            var size = Maximum - Minimum;

            return size.X * size.Y + size.Y * size.Z + size.Z * size.X;
        }
    }

    /// <summary>The binary build: binned surface-area heuristic, forked over ranges.</summary>
    /// <remarks>
    ///     ⚠ <b>A range's first node slot is a function of the ranges before it</b>: a subtree over
    ///     <c>n</c> triangles takes at most <c>2n − 1</c> slots, so the right child starts that far
    ///     past the left. Nothing is allocated on a shared counter, so the slots do not depend on which
    ///     thread got there first.
    /// </remarks>
    sealed class Builder {
        readonly Vector3[] minimums;
        readonly Vector3[] maximums;
        readonly Vector3[] centroids;

        public int[] Order { get; }

        public BinaryNode[] Nodes { get; }

        public Builder(Vector3[] vertices, int[] indices, IReadOnlyList<int>? members) {
            var all = indices.Length / 3;
            var count = members?.Count ?? all;

            Order = new int[count];
            minimums = new Vector3[all];
            maximums = new Vector3[all];
            centroids = new Vector3[all];
            Nodes = new BinaryNode[Math.Max(1, 2 * count - 1)];

            Parallel.For(
                0,
                count,
                slot => {
                    var triangle = members?[slot] ?? slot;
                    var a = vertices[indices[triangle * 3]];
                    var b = vertices[indices[triangle * 3 + 1]];
                    var c = vertices[indices[triangle * 3 + 2]];

                    Order[slot] = triangle;
                    minimums[triangle] = Vector3.Min(a, Vector3.Min(b, c));
                    maximums[triangle] = Vector3.Max(a, Vector3.Max(b, c));
                    centroids[triangle] = (a + b + c) / 3f;
                }
            );

            if (count > 0) {
                Split(0, 0, count, 0);
            }
        }

        /// <summary>One candidate side of a split: how many triangles, and their box.</summary>
        struct Bin {
            public int Count;
            public Vector3 Minimum;
            public Vector3 Maximum;

            public static readonly Bin Empty = new() {
                Minimum = new(float.PositiveInfinity), Maximum = new(float.NegativeInfinity)
            };

            public readonly float Area() {
                var size = Maximum - Minimum;

                return Count == 0 ? 0f : size.X * size.Y + size.Y * size.Z + size.Z * size.X;
            }

            public void Add(in Bin other) {
                Count += other.Count;
                Minimum = Vector3.Min(Minimum, other.Minimum);
                Maximum = Vector3.Max(Maximum, other.Maximum);
            }
        }

        void Split(int self, int start, int count, int depth) {
            var bounds = Bin.Empty;
            var centres = Bin.Empty;

            for (var slot = start; slot < start + count; slot++) {
                var triangle = Order[slot];

                bounds.Minimum = Vector3.Min(bounds.Minimum, minimums[triangle]);
                bounds.Maximum = Vector3.Max(bounds.Maximum, maximums[triangle]);
                centres.Minimum = Vector3.Min(centres.Minimum, centroids[triangle]);
                centres.Maximum = Vector3.Max(centres.Maximum, centroids[triangle]);
            }

            ref var node = ref Nodes[self];

            node.Minimum = bounds.Minimum;
            node.Maximum = bounds.Maximum;
            node.Start = start;
            node.Count = count;

            if (count <= LeafSize) {
                return;
            }

            var middle = Partition(start, count, depth, centres, bounds);

            var (left, right) = (self + 1, self + 2 * (middle - start));

            node.Count = 0;
            node.Left = left;
            node.Right = right;

            if (count >= ParallelRange) {
                var forked = middle;

                Parallel.Invoke(
                    () => Split(left, start, forked - start, depth + 1),
                    () => Split(right, forked, start + count - forked, depth + 1)
                );
            } else {
                Split(left, start, middle - start, depth + 1);
                Split(right, middle, start + count - middle, depth + 1);
            }
        }

        /// <summary>Reorders a range around its best split, and says where the split is.</summary>
        /// <remarks>
        ///     ⚠
        ///     <b>
        ///         A leaf of four is a hard limit, so a range the heuristic cannot improve still
        ///         splits
        ///     </b>
        ///     , down the middle of the order. Every triangle on one point, or a pile of
        ///     triangles sharing a centroid, reaches here, and past <see cref="MaxDepth" /> the same
        ///     fallback is what keeps the depth bounded: a median halves the range, so what remains
        ///     cannot go deeper than its own logarithm.
        /// </remarks>
        int Partition(int start, int count, int depth, in Bin centres, in Bin bounds) {
            var bestAxis = -1;
            var bestPlane = 0;
            var bestCost = float.PositiveInfinity;
            var extent = centres.Maximum - centres.Minimum;

            if (depth < MaxDepth - 26) {
                Span<Bin> bins = stackalloc Bin[Bins];
                Span<float> rightAreas = stackalloc float[Bins];
                Span<int> rightCounts = stackalloc int[Bins];

                for (var axis = 0; axis < 3; axis++) {
                    var width = Component(extent, axis);

                    if (width <= 0f) {
                        continue;
                    }

                    var origin = Component(centres.Minimum, axis);
                    var scale = Bins / width;

                    bins.Fill(Bin.Empty);

                    for (var slot = start; slot < start + count; slot++) {
                        var triangle = Order[slot];
                        var at = BinOf(Component(centroids[triangle], axis), origin, scale);

                        bins[at].Count++;
                        bins[at].Minimum = Vector3.Min(bins[at].Minimum, minimums[triangle]);
                        bins[at].Maximum = Vector3.Max(bins[at].Maximum, maximums[triangle]);
                    }

                    var sweep = Bin.Empty;

                    for (var plane = Bins - 1; plane > 0; plane--) {
                        sweep.Add(in bins[plane]);
                        rightAreas[plane] = sweep.Area();
                        rightCounts[plane] = sweep.Count;
                    }

                    sweep = Bin.Empty;

                    for (var plane = 1; plane < Bins; plane++) {
                        sweep.Add(in bins[plane - 1]);

                        if (sweep.Count == 0 || rightCounts[plane] == 0) {
                            continue;
                        }

                        var cost = sweep.Area() * Blocks(sweep.Count)
                            + rightAreas[plane] * Blocks(rightCounts[plane]);

                        // Strictly less, in a fixed axis order, so a tie is the same split on any run.
                        if (cost < bestCost) {
                            bestCost = cost;
                            bestAxis = axis;
                            bestPlane = plane;
                        }
                    }
                }
            }

            if (bestAxis < 0) {
                return Median(start, count, extent);
            }

            var bestOrigin = Component(centres.Minimum, bestAxis);
            var bestScale = Bins / Component(extent, bestAxis);
            var low = start;
            var high = start + count - 1;

            while (low <= high) {
                if (BinOf(Component(centroids[Order[low]], bestAxis), bestOrigin, bestScale) < bestPlane) {
                    low++;
                } else {
                    (Order[low], Order[high]) = (Order[high], Order[low]);
                    high--;
                }
            }

            return low == start || low == start + count ? Median(start, count, extent) : low;
        }

        /// <summary>How many blocks a count of triangles fills, which is what a ray pays for.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Blocks(int count) => (count + LeafSize - 1) / LeafSize;

        /// <summary>Splits a range at its middle, by centroid along the widest axis, ties by id.</summary>
        int Median(int start, int count, Vector3 extent) {
            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            var keys = new (float Key, int Triangle)[count];

            for (var slot = 0; slot < count; slot++) {
                var triangle = Order[start + slot];

                keys[slot] = (Component(centroids[triangle], axis), triangle);
            }

            Array.Sort(keys);

            for (var slot = 0; slot < count; slot++) {
                Order[start + slot] = keys[slot].Triangle;
            }

            return start + count / 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int BinOf(float value, float origin, float scale) => Math.Min((int)((value - origin) * scale), Bins - 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Component(Vector3 value, int axis) =>
            axis switch {
                0 => value.X,
                1 => value.Y,
                _ => value.Z
            };
    }

    /// <summary>Collapses the binary tree into four-wide nodes and vector triangle blocks.</summary>
    /// <remarks>
    ///     ⚠ <b>Depth first and single-threaded, in the one order that decides the result.</b> A
    ///     child that is itself an inner node is flattened straight after its parent's slot, so a walk
    ///     down one side stays in nearby memory, which is most of what a wide tree buys on a machine
    ///     whose cache line is as wide as a node.
    /// </remarks>
    sealed class Flattener(Builder binary, Vector3[] vertices, int[] indices) {
        public List<WideNode> Nodes { get; } = [];

        public List<TriangleBlock> Blocks { get; } = [];

        public void Run() {
            if (binary.Order.Length == 0) {
                return;
            }

            Nodes.Add(default);
            Fill(0, 0);
        }

        void Fill(int wide, int root) {
            Span<int> open = stackalloc int[LeafSize];
            var used = 1;

            open[0] = root;

            // Open the child with the largest surface: it is the one a ray is likeliest to enter, so
            // it is the one worth seeing past in this node.
            while (used < LeafSize) {
                var widest = -1;
                var area = float.NegativeInfinity;

                for (var slot = 0; slot < used; slot++) {
                    ref var candidate = ref binary.Nodes[open[slot]];

                    if (candidate.Count == 0 && candidate.Area() > area) {
                        area = candidate.Area();
                        widest = slot;
                    }
                }

                if (widest < 0) {
                    break;
                }

                ref var opened = ref binary.Nodes[open[widest]];

                open[widest] = opened.Left;
                open[used++] = opened.Right;
            }

            var node = default(WideNode);
            var children = new int[LeafSize];

            var (minX, minY, minZ) = (new float[LeafSize], new float[LeafSize], new float[LeafSize]);
            var (maxX, maxY, maxZ) = (new float[LeafSize], new float[LeafSize], new float[LeafSize]);

            for (var slot = 0; slot < LeafSize; slot++) {
                if (slot >= used) {
                    children[slot] = Absent;
                    (minX[slot], minY[slot], minZ[slot]) = (float.PositiveInfinity, float.PositiveInfinity,
                        float.PositiveInfinity);
                    (maxX[slot], maxY[slot], maxZ[slot]) = (float.NegativeInfinity, float.NegativeInfinity,
                        float.NegativeInfinity);

                    continue;
                }

                ref var child = ref binary.Nodes[open[slot]];

                (minX[slot], minY[slot], minZ[slot]) = (child.Minimum.X, child.Minimum.Y, child.Minimum.Z);
                (maxX[slot], maxY[slot], maxZ[slot]) = (child.Maximum.X, child.Maximum.Y, child.Maximum.Z);

                if (child.Count > 0) {
                    children[slot] = ~Blocks.Count;
                    Blocks.Add(Block(child.Start, child.Count));
                } else {
                    children[slot] = Nodes.Count;
                    Nodes.Add(default);
                }
            }

            node.MinX = Vector128.Create(minX);
            node.MinY = Vector128.Create(minY);
            node.MinZ = Vector128.Create(minZ);
            node.MaxX = Vector128.Create(maxX);
            node.MaxY = Vector128.Create(maxY);
            node.MaxZ = Vector128.Create(maxZ);
            node.Children = Vector128.Create(children);

            CollectionsMarshal.AsSpan(Nodes)[wide] = node;

            for (var slot = 0; slot < used; slot++) {
                if (children[slot] >= 0) {
                    Fill(children[slot], open[slot]);
                }
            }
        }

        TriangleBlock Block(int start, int count) {
            Span<float> x = stackalloc float[LeafSize];
            Span<float> y = stackalloc float[LeafSize];
            Span<float> z = stackalloc float[LeafSize];
            Span<float> ex1 = stackalloc float[LeafSize];
            Span<float> ey1 = stackalloc float[LeafSize];
            Span<float> ez1 = stackalloc float[LeafSize];
            Span<float> ex2 = stackalloc float[LeafSize];
            Span<float> ey2 = stackalloc float[LeafSize];
            Span<float> ez2 = stackalloc float[LeafSize];
            Span<int> triangles = stackalloc int[LeafSize];

            x.Fill(float.NaN);
            y.Fill(float.NaN);
            z.Fill(float.NaN);
            ex1.Fill(float.NaN);
            ey1.Fill(float.NaN);
            ez1.Fill(float.NaN);
            ex2.Fill(float.NaN);
            ey2.Fill(float.NaN);
            ez2.Fill(float.NaN);
            triangles.Fill(-1);

            for (var slot = 0; slot < count; slot++) {
                var triangle = binary.Order[start + slot];
                var a = vertices[indices[triangle * 3]];
                var one = vertices[indices[triangle * 3 + 1]] - a;
                var two = vertices[indices[triangle * 3 + 2]] - a;

                (x[slot], y[slot], z[slot]) = (a.X, a.Y, a.Z);
                (ex1[slot], ey1[slot], ez1[slot]) = (one.X, one.Y, one.Z);
                (ex2[slot], ey2[slot], ez2[slot]) = (two.X, two.Y, two.Z);
                triangles[slot] = triangle;
            }

            return new() {
                X = Vector128.Create(x),
                Y = Vector128.Create(y),
                Z = Vector128.Create(z),
                EdgeX1 = Vector128.Create(ex1),
                EdgeY1 = Vector128.Create(ey1),
                EdgeZ1 = Vector128.Create(ez1),
                EdgeX2 = Vector128.Create(ex2),
                EdgeY2 = Vector128.Create(ey2),
                EdgeZ2 = Vector128.Create(ez2),
                Triangles = Vector128.Create(triangles)
            };
        }
    }
}
