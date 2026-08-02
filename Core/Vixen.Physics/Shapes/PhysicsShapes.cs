// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using JoltPhysicsSharp;
using Vixen.Physics.Interop;
using Quaternion = Vixen.Core.Mathematics.Quaternion;
using Vector3 = Vixen.Core.Mathematics.Vector3;

namespace Vixen.Physics.Shapes;

/// <summary>
///     Every collision volume the world knows about, each built once and shared by everything that
///     asked for the same thing.
/// </summary>
/// <remarks>
///     <para>
///         <b>Registration interns.</b> Registering the same description twice returns the same
///         <see cref="ShapeId" /> and reuses the one native shape behind it. A level of ten thousand
///         identical crates therefore builds one <c>BoxShape</c>, and — because Jolt's shapes are
///         immutable and reference-counted — every body pointing at it shares its bounding volume
///         hierarchy, its mass properties and its cache lines.
///     </para>
///     <para>
///         <b>Native shapes are built lazily and freed together.</b> A description is cheap; a mesh
///         shape is a bounding-volume tree over a hundred thousand triangles. Nothing is built until
///         a body actually asks, and everything is freed when the registry is disposed — which
///         happens after the physics system it fed, because Jolt reads a shape while removing the
///         last body that used it.
///     </para>
///     <para>
///         <b>Not thread-safe, deliberately.</b> Registration happens while a scene is loading or
///         while gameplay code is running, never from inside a step; a lock on the interning table
///         would be paid on every lookup to protect a path that has no concurrency in it. The one
///         place it would matter — the ECS bridge creating bodies — runs on the main thread at a
///         sync point.
///     </para>
/// </remarks>
public sealed class PhysicsShapes : IDisposable {
    /// <summary>
    ///     How far Jolt shrinks a convex shape before rounding it back out, in metres.
    /// </summary>
    /// <remarks>
    ///     Jolt's own default. The convex radius is what makes box-box contacts stable, and it is
    ///     clamped down for a shape too small to hold it — a 2 cm box with a 5 cm radius is a sphere,
    ///     and Jolt says so with a native assertion rather than a return value.
    /// </remarks>
    public const float DefaultConvexRadius = 0.05f;

    /// <summary>The height that means "there is no ground here".</summary>
    /// <remarks>
    ///     <para>
    ///         Jolt's <c>cNoCollisionValue</c>, which is <see cref="float.MaxValue" />. A sample set to
    ///         it produces no triangles, so the two quads around it are a hole — which is what a
    ///         terrain's visibility mask paints, and the reason the hole is punched from the collider
    ///         and the mesh by the same bit.
    ///     </para>
    ///     <para>
    ///         ⚠ Compared with <c>==</c> and not with a tolerance. It is a sentinel, not a large
    ///         height, and a sample near it is a bug in whatever produced it rather than a hole.
    ///     </para>
    /// </remarks>
    public const float NoCollisionHeight = float.MaxValue;

    /// <summary>The largest block a height field's acceleration structure is built over.</summary>
    /// <remarks>Jolt's ceiling. The block size must be a power of two in <c>[2, 8]</c>.</remarks>
    public const int MaxHeightFieldBlockSize = 8;

    /// <summary>The smallest height field Jolt will accelerate.</summary>
    /// <remarks>
    ///     Four samples — three quads — because the smallest block is two samples across and Jolt
    ///     needs at least two blocks along each axis. See <see cref="HeightFieldBlockSize" />.
    /// </remarks>
    public const int MinHeightFieldSampleCount = 4;

    readonly Dictionary<ShapeDescription, ShapeId> interned = [];
    readonly List<ShapeDescription> descriptions = [];
    readonly List<Shape?> built = [];
    readonly List<Vector3[]> pointData = [];
    readonly List<int[]> indexData = [];
    readonly List<CompoundChild[]> childData = [];
    readonly List<float[]> heightData = [];
    readonly List<HeightFieldPlacement> heightPlacements = [];

    /// <summary>How many distinct shapes are registered.</summary>
    public int Count => descriptions.Count;

    /// <summary>How many have actually been built as native shapes.</summary>
    /// <remarks>For a test that wants to prove the laziness, and for a memory report.</remarks>
    public int BuiltCount {
        get {
            var count = 0;

            foreach (var shape in built) {
                if (shape is not null) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Registers a description, or finds the id it already has.</summary>
    /// <param name="description">What the shape is.</param>
    /// <returns>Its id.</returns>
    /// <exception cref="PhysicsShapeException">The description is not a valid shape.</exception>
    public ShapeId Register(in ShapeDescription description) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Validate(description);

        if (interned.TryGetValue(description, out var existing)) {
            return existing;
        }

        descriptions.Add(description);
        built.Add(null);

        var id = new ShapeId(descriptions.Count);
        interned.Add(description, id);
        return id;
    }

    /// <summary>Registers a sphere.</summary>
    /// <param name="radius">Its radius.</param>
    /// <returns>Its id.</returns>
    public ShapeId Sphere(float radius) => Register(ShapeDescription.Sphere(radius));

    /// <summary>Registers a box.</summary>
    /// <param name="halfExtents">Half its size along each axis.</param>
    /// <returns>Its id.</returns>
    public ShapeId Box(Vector3 halfExtents) => Register(ShapeDescription.Box(halfExtents));

    /// <summary>Registers a cube.</summary>
    /// <param name="halfExtent">Half its size along every axis.</param>
    /// <returns>Its id.</returns>
    public ShapeId Box(float halfExtent) => Register(ShapeDescription.Box(halfExtent));

    /// <summary>Registers a capsule standing on the Y axis.</summary>
    /// <param name="halfHeight">Half the height of the cylindrical part.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>Its id.</returns>
    public ShapeId Capsule(float halfHeight, float radius) =>
        Register(ShapeDescription.Capsule(halfHeight, radius));

    /// <summary>Registers a cylinder standing on the Y axis.</summary>
    /// <param name="halfHeight">Half the total height.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>Its id.</returns>
    public ShapeId Cylinder(float halfHeight, float radius) =>
        Register(ShapeDescription.Cylinder(halfHeight, radius));

    /// <summary>Registers an infinite half-space.</summary>
    /// <param name="normal">Which way is up.</param>
    /// <param name="distance">How far from the origin along the normal.</param>
    /// <returns>Its id.</returns>
    public ShapeId Plane(Vector3 normal, float distance = 0f) =>
        Register(ShapeDescription.Plane(normal, distance));

    /// <summary>Registers the convex hull of a point cloud.</summary>
    /// <param name="points">The points. At least four, and not all in one plane.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     The points are copied, because the description that names them outlives the caller's array
    ///     and a hull built from an array somebody later mutated is a shape that stops matching the
    ///     thing it was made from.
    /// </remarks>
    public ShapeId ConvexHull(ReadOnlySpan<Vector3> points) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (points.Length < 4) {
            throw new PhysicsShapeException(
                $"A convex hull needs at least four points; {points.Length} were given.",
                nameof(points)
            );
        }

        pointData.Add(points.ToArray());
        return Register(new() { Kind = ShapeKind.ConvexHull, DataIndex = pointData.Count - 1 });
    }

    /// <summary>Registers a triangle mesh.</summary>
    /// <param name="vertices">The vertices.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     <para>
    ///         Concave, and so usable by static and kinematic bodies only — a mesh has no inertia
    ///         tensor the solver can integrate. This is the shape a level's collision geometry is.
    ///     </para>
    ///     <para>
    ///         <b>A mesh is one-sided, and the winding is which side.</b> A triangle's outward normal
    ///         is <c>(b − a) × (c − a)</c>, so a floor whose triangles are wound the other way is a
    ///         ceiling with nothing under it and everything falls through — the single most common
    ///         way a mesh collider appears not to work at all.
    ///     </para>
    /// </remarks>
    public ShapeId Mesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (vertices.Length < 3) {
            throw new PhysicsShapeException(
                $"A mesh needs at least three vertices; {vertices.Length} were given.",
                nameof(vertices)
            );
        }

        if (indices.Length == 0 || indices.Length % 3 != 0) {
            throw new PhysicsShapeException(
                $"A mesh needs a positive multiple of three indices; {indices.Length} were given.",
                nameof(indices)
            );
        }

        foreach (var index in indices) {
            if ((uint)index >= (uint)vertices.Length) {
                throw new PhysicsShapeException(
                    $"Index {index} is outside the {vertices.Length} vertices given.",
                    nameof(indices)
                );
            }
        }

        pointData.Add(vertices.ToArray());
        indexData.Add(indices.ToArray());

        // Both blobs are appended together, so one index addresses the pair.
        return Register(new() { Kind = ShapeKind.Mesh, DataIndex = pointData.Count - 1 });
    }

    /// <summary>Registers a regular grid of heights over the XZ plane.</summary>
    /// <param name="heights">
    ///     <paramref name="sampleCount" />² samples, row-major in Z then X, so the sample at
    ///     <c>(x, z)</c> is at <c>z * sampleCount + x</c>. <see cref="NoCollisionHeight" /> punches a
    ///     hole.
    /// </param>
    /// <param name="sampleCount">
    ///     How many samples along each axis. A power of two of at least two, which means the grid
    ///     spans one less than a power of two quads.
    /// </param>
    /// <param name="offset">Where sample <c>(0, 0)</c> sits in the body's space.</param>
    /// <param name="scale">Sample spacing in X and Z, and what one unit of height means in Y.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     <para>
    ///         Concave, and so usable by static and kinematic bodies only — the same reason a
    ///         <see cref="Mesh" /> is. This is what a terrain tile collides with, and one tile is one
    ///         shape: a brush that dirtied three tiles rebuilds three of these and leaves the rest of
    ///         the terrain alone. See [docs/plan/31 § D10].
    ///     </para>
    ///     <para>
    ///         <b>The samples are copied</b>, for <see cref="Mesh" />'s reason: the description
    ///         outlives the caller's array, and a shape built from an array somebody later sculpted is
    ///         a collider that has stopped matching its ground.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="sampleCount" /> must be a power of two, and this is checked rather
    ///         than trusted.</b> Jolt requires it to be a multiple of the block size and requires the
    ///         resulting block count to be a power of two; it states both as native assertions, and
    ///         <see cref="DefaultConvexRadius" />'s note applies — those assertions are compiled out of
    ///         the shipped native library, so a violation is silent corruption rather than a crash.
    ///     </para>
    /// </remarks>
    /// <exception cref="PhysicsShapeException">The grid is not one Jolt can accelerate.</exception>
    public ShapeId HeightField(ReadOnlySpan<float> heights, int sampleCount, Vector3 offset, Vector3 scale) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (sampleCount < MinHeightFieldSampleCount || (sampleCount & (sampleCount - 1)) != 0) {
            throw new PhysicsShapeException(
                $"A height field's sample count must be a power of two of at least "
                + $"{MinHeightFieldSampleCount}; it was {sampleCount}. Note that a tile of N quads is "
                + "N + 1 samples, so it is the sample count that is the power of two and the quad count "
                + "that is one less.",
                nameof(sampleCount)
            );
        }

        var expected = (long)sampleCount * sampleCount;

        if (heights.Length != expected) {
            throw new PhysicsShapeException(
                $"A {sampleCount}×{sampleCount} height field needs {expected} samples; {heights.Length} were given.",
                nameof(heights)
            );
        }

        Finite(scale.X, "scale X");
        Finite(scale.Z, "scale Z");
        Positive(scale.X, "sample spacing in X");
        Positive(scale.Z, "sample spacing in Z");

        if (!float.IsFinite(scale.Y) || scale.Y == 0f) {
            throw new PhysicsShapeException(
                $"A height field's Y scale must be finite and non-zero; it was {scale.Y}.",
                nameof(scale)
            );
        }

        Finite(offset.X, "offset X");
        Finite(offset.Y, "offset Y");
        Finite(offset.Z, "offset Z");

        var collidable = 0;

        foreach (var height in heights) {
            if (height == NoCollisionHeight) {
                continue;
            }

            if (!float.IsFinite(height)) {
                throw new PhysicsShapeException(
                    $"A height field's samples must each be finite or exactly {nameof(NoCollisionHeight)}; "
                    + $"one was {height}.",
                    nameof(heights)
                );
            }

            collidable++;
        }

        if (collidable == 0) {
            // Jolt reports this as a failed Create() with no message. Saying it here names the
            // likely cause, which is a hole mask written over the whole tile rather than under it.
            throw new PhysicsShapeException(
                "A height field needs at least one sample that is not a hole; every one of "
                + $"{heights.Length} was {nameof(NoCollisionHeight)}.",
                nameof(heights)
            );
        }

        heightData.Add(heights.ToArray());
        heightPlacements.Add(new(sampleCount, offset, scale));

        // Both blobs are appended together, so one index addresses the pair.
        return Register(new() { Kind = ShapeKind.HeightField, DataIndex = heightData.Count - 1 });
    }

    /// <summary>Registers several shapes rigidly attached to one another.</summary>
    /// <param name="children">The children, each with its offset.</param>
    /// <returns>Its id.</returns>
    /// <remarks>
    ///     A compound of one child is not a compound: it is built as the child itself when the child
    ///     sits at the origin, and as an offset copy of it otherwise. Jolt's static compound refuses
    ///     to hold fewer than two sub-shapes, and the alternative to collapsing it here is that a
    ///     perfectly reasonable authoring case — a prefab whose collider list happens to have one
    ///     entry — asserts inside the native library.
    /// </remarks>
    public ShapeId Compound(ReadOnlySpan<CompoundChild> children) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (children.Length == 0) {
            throw new PhysicsShapeException("A compound shape needs at least one child.", nameof(children));
        }

        foreach (var child in children) {
            if (child.Shape.IsNone || child.Shape.Value > descriptions.Count) {
                throw new PhysicsShapeException(
                    $"Compound child {child.Shape} is not registered in this registry.",
                    nameof(children)
                );
            }
        }

        childData.Add(children.ToArray());
        return Register(new() { Kind = ShapeKind.Compound, DataIndex = childData.Count - 1 });
    }

    /// <summary>What a registered id describes.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The description.</returns>
    /// <exception cref="PhysicsHandleException">The id is not from this registry.</exception>
    public ShapeDescription Describe(ShapeId id) =>
        id.IsNone || id.Value > descriptions.Count
            ? throw new PhysicsHandleException($"{id} is not registered in this registry of {Count}.")
            : descriptions[id.Value - 1];

    /// <summary>The points a hull or a mesh was registered with.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The points, or an empty span for a shape that has none.</returns>
    public ReadOnlySpan<Vector3> PointsOf(ShapeId id) {
        var description = Describe(id);

        return description.Kind is ShapeKind.ConvexHull or ShapeKind.Mesh
            ? pointData[description.DataIndex]
            : [];
    }

    /// <summary>The triangle indices a mesh was registered with.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The indices, or an empty span for anything that is not a mesh.</returns>
    public ReadOnlySpan<int> IndicesOf(ShapeId id) {
        var description = Describe(id);
        return description.Kind is ShapeKind.Mesh ? indexData[description.DataIndex] : [];
    }

    /// <summary>The children a compound was registered with.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The children, or an empty span for anything that is not a compound.</returns>
    public ReadOnlySpan<CompoundChild> ChildrenOf(ShapeId id) {
        var description = Describe(id);
        return description.Kind is ShapeKind.Compound ? childData[description.DataIndex] : [];
    }

    /// <summary>The samples a height field was registered with.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The heights, or an empty span for anything that is not a height field.</returns>
    public ReadOnlySpan<float> HeightsOf(ShapeId id) {
        var description = Describe(id);
        return description.Kind is ShapeKind.HeightField ? heightData[description.DataIndex] : [];
    }

    /// <summary>Where a height field's samples sit.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The placement, or a default one for anything that is not a height field.</returns>
    public HeightFieldPlacement HeightFieldOf(ShapeId id) {
        var description = Describe(id);
        return description.Kind is ShapeKind.HeightField ? heightPlacements[description.DataIndex] : default;
    }

    /// <summary>Which block size Jolt's acceleration structure uses for a given sample count.</summary>
    /// <param name="sampleCount">
    ///     How many samples along each axis. A power of two of at least
    ///     <see cref="MinHeightFieldSampleCount" />.
    /// </param>
    /// <returns>The block size, a power of two in <c>[2, <see cref="MaxHeightFieldBlockSize" />]</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         The largest legal one, because the block is what the structure culls at and a bigger
    ///         block is a shallower tree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Capped at half the grid, and that cap was measured rather than read.</b> Jolt
    ///         needs at least two blocks along each axis — the quadtree it builds over them has no
    ///         root otherwise — so a 4-sample grid takes blocks of 2 and an 8-sample grid takes blocks
    ///         of 4. It reports the violation by returning a null shape from <c>Create()</c> with no
    ///         message, which is why this is a function with a test rather than a constant.
    ///     </para>
    /// </remarks>
    public static int HeightFieldBlockSize(int sampleCount) =>
        Math.Max(2, Math.Min(MaxHeightFieldBlockSize, sampleCount / 2));

    /// <summary>The native shape for an id, building it on the first ask.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The shape, owned by this registry.</returns>
    internal Shape Resolve(ShapeId id) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var description = Describe(id);
        var slot = id.Value - 1;

        if (built[slot] is { } existing) {
            return existing;
        }

        var shape = Build(in description);
        built[slot] = shape;
        return shape;
    }

    Shape Build(in ShapeDescription description) =>
        description.Kind switch {
            ShapeKind.Sphere => new SphereShape(description.Radius),
            ShapeKind.Box => BuildBox(description.Extents),
            ShapeKind.Capsule => new CapsuleShape(description.HalfHeight, description.Radius),
            ShapeKind.Cylinder => BuildCylinder(description.HalfHeight, description.Radius),
            ShapeKind.Plane => BuildPlane(description.Extents, description.Radius),
            ShapeKind.ConvexHull => BuildHull(pointData[description.DataIndex]),
            ShapeKind.Mesh => BuildMesh(pointData[description.DataIndex], indexData[description.DataIndex]),
            ShapeKind.Compound => BuildCompound(childData[description.DataIndex]),
            ShapeKind.HeightField => BuildHeightField(
                heightData[description.DataIndex],
                heightPlacements[description.DataIndex]
            ),
            _ => throw new PhysicsShapeException($"Unknown shape kind {description.Kind}.")
        };

    static BoxShape BuildBox(Vector3 halfExtents) {
        var smallest = MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z));

        // Half the smallest half-extent, so the rounded corners never eat the whole face. Jolt
        // asserts rather than clamping, and the assert is compiled out of the shipped native library.
        var radius = MathF.Min(DefaultConvexRadius, smallest * 0.5f);
        var extents = JoltMath.ToJolt(halfExtents);
        return new BoxShape(in extents, radius);
    }

    static Shape BuildCylinder(float halfHeight, float radius) {
        var convexRadius = MathF.Min(DefaultConvexRadius, MathF.Min(halfHeight, radius) * 0.5f);
        using var settings = new CylinderShapeSettings(halfHeight, radius, convexRadius);
        return settings.Create();
    }

    static Shape BuildPlane(Vector3 normal, float distance) {
        var length = normal.Length();

        if (length < 1e-6f) {
            throw new PhysicsShapeException("A plane's normal must not be zero-length.");
        }

        // Jolt's Plane is (normal, constant) where the constant is −distance along the normal: the
        // surface is where dot(n, p) + c == 0. Getting that sign wrong puts the solid side up and the
        // world's floor becomes its ceiling, which is exactly the bug that looks like broken gravity.
        var unit = normal / length;
        var plane = new System.Numerics.Plane(JoltMath.ToJolt(unit), -distance);
        using var settings = new PlaneShapeSettings(in plane, null, PlaneShapeSettings.DefaultHalfExtent);
        return settings.Create();
    }

    static Shape BuildHull(Vector3[] points) {
        var converted = new System.Numerics.Vector3[points.Length];

        for (var index = 0; index < points.Length; index++) {
            converted[index] = JoltMath.ToJolt(points[index]);
        }

        using var settings = new ConvexHullShapeSettings(converted, DefaultConvexRadius);
        var shape = settings.Create();

        return shape.Handle != nint.Zero
            ? shape
            : throw new PhysicsShapeException(
                $"Jolt could not build a convex hull from {points.Length} points. The usual cause is "
                + "that they are coplanar or coincident."
            );
    }

    static Shape BuildMesh(Vector3[] vertices, int[] indices) {
        var converted = new System.Numerics.Vector3[vertices.Length];

        for (var index = 0; index < vertices.Length; index++) {
            converted[index] = JoltMath.ToJolt(vertices[index]);
        }

        var triangles = new IndexedTriangle[indices.Length / 3];

        for (var triangle = 0; triangle < triangles.Length; triangle++) {
            var first = indices[triangle * 3];
            var second = indices[(triangle * 3) + 1];
            var third = indices[(triangle * 3) + 2];
            triangles[triangle] = new(in first, in second, in third, 0, 0);
        }

        using var settings = new MeshShapeSettings(converted.AsSpan(), triangles.AsSpan());
        var shape = settings.Create();

        return shape.Handle != nint.Zero
            ? shape
            : throw new PhysicsShapeException(
                $"Jolt could not build a mesh from {vertices.Length} vertices and {triangles.Length} triangles."
            );
    }

    static Shape BuildHeightField(float[] heights, in HeightFieldPlacement placement) {
        var offset = JoltMath.ToJolt(placement.Offset);
        var scale = JoltMath.ToJolt(placement.Scale);

        using var settings = new HeightFieldShapeSettings(
            heights.AsSpan(),
            in offset,
            in scale,
            (uint)placement.SampleCount
        ) {
            BlockSize = (uint)HeightFieldBlockSize(placement.SampleCount),

            // Jolt's ceiling, and its default. The compression is per block against that block's own
            // range rather than the whole field's, so eight bits is centimetres on a mountain and not
            // the metres a global quantisation of the same width would give.
            BitsPerSample = 8
        };

        var shape = settings.Create();

        return shape.Handle != nint.Zero
            ? shape
            : throw new PhysicsShapeException(
                $"Jolt could not build a {placement.SampleCount}×{placement.SampleCount} height field."
            );
    }

    Shape BuildCompound(CompoundChild[] children) {
        if (children.Length == 1) {
            var only = children[0];
            var inner = Resolve(only.Shape);

            if (only.Position == Vector3.Zero && only.Rotation == Quaternion.Identity) {
                // Handing the child straight back would put one native shape under two registry
                // slots, and the second Dispose would be a double free. Wrapping it in an identity
                // decorator costs a pointer and keeps ownership one-to-one.
                var identity = System.Numerics.Vector3.Zero;
                var noRotation = System.Numerics.Quaternion.Identity;
                return new RotatedTranslatedShape(in identity, in noRotation, inner);
            }

            var offset = JoltMath.ToJolt(only.Position);
            var rotation = JoltMath.ToJolt(only.Rotation);
            return new RotatedTranslatedShape(in offset, in rotation, inner);
        }

        using var settings = new StaticCompoundShapeSettings();

        for (var index = 0; index < children.Length; index++) {
            var child = children[index];
            var position = JoltMath.ToJolt(child.Position);
            var rotation = JoltMath.ToJolt(child.Rotation);
            settings.AddShape(in position, in rotation, Resolve(child.Shape), (uint)index);
        }

        var shape = settings.Create();

        return shape.Handle != nint.Zero
            ? shape
            : throw new PhysicsShapeException($"Jolt could not build a compound of {children.Length} children.");
    }

    static void Validate(in ShapeDescription description) {
        switch (description.Kind) {
            case ShapeKind.Sphere:
                Positive(description.Radius, "radius");
                break;

            case ShapeKind.Box:
                Positive(description.Extents.X, "half extent X");
                Positive(description.Extents.Y, "half extent Y");
                Positive(description.Extents.Z, "half extent Z");
                break;

            case ShapeKind.Capsule:
            case ShapeKind.Cylinder:
                Positive(description.HalfHeight, "half height");
                Positive(description.Radius, "radius");
                break;

            case ShapeKind.Plane:
            case ShapeKind.ConvexHull:
            case ShapeKind.Mesh:
            case ShapeKind.Compound:
            case ShapeKind.HeightField:
                // The data-carrying kinds are validated where their data arrives, because that is the
                // only place it is in hand — a description holds an index into a blob, not the blob.
                break;

            default:
                throw new PhysicsShapeException($"Unknown shape kind {description.Kind}.");
        }
    }

    static void Positive(float value, string what) {
        if (!(value > 0f) || !float.IsFinite(value)) {
            throw new PhysicsShapeException($"A shape's {what} must be positive and finite; it was {value}.");
        }
    }

    static void Finite(float value, string what) {
        if (!float.IsFinite(value)) {
            throw new PhysicsShapeException($"A shape's {what} must be finite; it was {value}.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Must come after the <see cref="PhysicsWorld" /> that used these shapes. Removing a body
    ///     reads its shape, so freeing shapes first turns world teardown into a use-after-free that
    ///     reports as a segmentation fault with no managed frame anywhere in it.
    /// </remarks>
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;

        // Reverse order: a compound holds its children, so freeing a child first leaves the parent
        // decrementing a reference count in memory the allocator has already taken back.
        for (var index = built.Count - 1; index >= 0; index--) {
            built[index]?.Dispose();
            built[index] = null;
        }
    }
}
