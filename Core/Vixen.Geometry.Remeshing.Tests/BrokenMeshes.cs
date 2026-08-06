// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The deliberately awful corpus: every way an input can be broken, built in code.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D19 already settled the shape of this.</b> "<c>MeshShapes</c> is twelve
///         parametric shapes, <c>MeshBoolean</c> is CSG and <c>MeshOperations</c> is the rest… the
///         corpus becomes a test fixture built by a script." Generated rather than checked in, so it
///         is reviewable as code, is the same on every machine, and costs the repository nothing.
///     </para>
///     <para>
///         ⚠ <b>No test in this project reads a file.</b> There is real TRELLIS output on the author's
///         machine — sixteen GLB exports, thirteen to twenty-five thousand triangles each, fully
///         unwelded at a ratio of exactly three — and it is untracked, so a test that depended on it
///         would pass for one person and fail for everyone else. It is also the wrong fixture for
///         half of this: those are TRELLIS's <i>post-processed</i> exports, decimated and atlased,
///         rather than the raw four-million-triangle marching-cubes extraction
///         docs/plan/41 § B1 describes. A good weld case and a poor pre-remesh one.
///     </para>
///     <para>
///         Each entry is one named defect, so a failure names what broke rather than "the corpus".
///         docs/plan/41's robustness criterion — two hundred broken meshes, never an exception and
///         never a hang — has <see cref="Corpus" /> to run against.
///     </para>
/// </remarks>
static class BrokenMeshes {
    /// <summary>Every mesh here, with the defect each one carries.</summary>
    /// <returns>Name and mesh, in a fixed order.</returns>
    public static IEnumerable<(string Name, EditMesh Mesh)> Corpus() {
        yield return ("empty", new EditMesh());
        yield return ("single-vertex", SingleVertex());
        yield return ("single-triangle", SingleTriangle());
        yield return ("zero-area-triangle", ZeroArea());
        yield return ("zero-length-edge", ZeroLengthEdge());
        yield return ("staircase-sphere", StaircaseSphere());
        yield return ("specks", Specks());
        yield return ("non-manifold-edge", TJunction());
        yield return ("self-intersecting", SelfIntersecting());
        yield return ("slivers", Slivers());
        yield return ("duplicate-faces", DuplicateFaces());
        yield return ("open-surface", OpenSurface());
        yield return ("disconnected-pile", DisconnectedPile());
        yield return ("mobius", Mobius());
        yield return ("inconsistent-winding", InconsistentWinding());
        yield return ("unwelded", Unwelded(MeshShapes.Create(ShapeKind.Sphere)));
        yield return ("two-components-40-percent", TwoComponents(0.4f));
        yield return ("two-components-hundredth-of-a-percent", TwoComponents(0.0001f));
    }

    /// <summary>One position and no faces.</summary>
    public static EditMesh SingleVertex() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);

        return mesh;
    }

    /// <summary>One triangle: a component of one, with three boundary edges and no inside.</summary>
    public static EditMesh SingleTriangle() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(Vector3.UnitX);
        mesh.AddPosition(Vector3.UnitZ);
        mesh.AddFace([0, 1, 2]);

        return mesh;
    }

    /// <summary>Three collinear positions — a face with no area, no normal and no plane.</summary>
    public static EditMesh ZeroArea() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(new(1f, 0f, 0f));
        mesh.AddPosition(new(2f, 0f, 0f));
        mesh.AddFace([0, 1, 2]);

        return mesh;
    }

    /// <summary>A quad whose two halves share an edge of length zero.</summary>
    public static EditMesh ZeroLengthEdge() {
        var mesh = new EditMesh();

        mesh.AddPosition(Vector3.Zero);
        mesh.AddPosition(new(1f, 0f, 0f));
        mesh.AddPosition(new(1f, 0f, 1f));
        mesh.AddPosition(new(1f, 0f, 1f));
        mesh.AddPosition(new(0f, 0f, 1f));

        mesh.AddFace([0, 1, 2]);
        mesh.AddFace([0, 3, 4]);

        return mesh;
    }

    /// <summary>A sphere snapped to a voxel grid — marching cubes' staircase at the cell frequency.</summary>
    /// <param name="voxel">The cell size, as a fraction of the radius.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     ⚠ This is the input docs/plan/41 § B1 says the whole conditioning stage exists for, and it
    ///     is what makes the pre-remesh's case: every one of these facets is a real dihedral angle
    ///     that a curvature estimate will read as a feature, so a field solved on it aligns to the
    ///     voxel grid rather than to the sphere.
    /// </remarks>
    public static EditMesh StaircaseSphere(float voxel = 0.12f) {
        var source = MeshShapes.Create(
            ShapeParameters.Default(ShapeKind.Sphere) with { Sides = 40, Steps = 20 }
        );

        var positions = new Vector3[source.PositionCount];

        for (var index = 0; index < positions.Length; index++) {
            var point = source.Positions[index];

            positions[index] = new(
                MathF.Round(point.X / voxel) * voxel,
                MathF.Round(point.Y / voxel) * voxel,
                MathF.Round(point.Z / voxel) * voxel
            );
        }

        return EditMesh.FromTriangles(positions, source.Triangulate());
    }

    /// <summary>A body plus the debris an SDF hallucinated round it.</summary>
    /// <param name="count">How many specks.</param>
    /// <returns>The mesh.</returns>
    public static EditMesh Specks(int count = 12) {
        var mesh = MeshShapes.Create(ShapeKind.Sphere);
        var speck = MeshShapes.Create(ShapeParameters.Default(ShapeKind.Box) with { Size = new(0.02f) });

        for (var index = 0; index < count; index++) {
            var angle = index * 0.7f;

            MeshOperations.Append(
                mesh,
                speck,
                Matrix4x4.FromTranslation(
                    new(MathF.Cos(angle) * 1.6f, MathF.Sin(angle * 1.3f) * 1.6f, MathF.Sin(angle) * 1.6f)
                )
            );
        }

        return mesh;
    }

    /// <summary>A wall meeting a floor in a T: one edge with three faces, and doc 24's ordinary case.</summary>
    /// <returns>The mesh. Six triangles, of which four are the floor and two are the wall.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the case that decides cut against merge.</b> The three faces along the seam
    ///     are all real: two halves of a floor and the bottom of a wall standing on it. Merging any
    ///     two of them invents a surface. Cutting the seam keeps every triangle and costs one duplicate
    ///     position pair, which is why the assertion afterwards is that the triangle count did not
    ///     move.
    /// </remarks>
    public static EditMesh TJunction() {
        var mesh = new EditMesh();

        // The seam, running along x at z = 0.
        mesh.AddPosition(new(-1f, 0f, 0f));
        mesh.AddPosition(new(1f, 0f, 0f));

        // The floor, on both sides of it.
        mesh.AddPosition(new(-1f, 0f, -1f));
        mesh.AddPosition(new(1f, 0f, -1f));
        mesh.AddPosition(new(-1f, 0f, 1f));
        mesh.AddPosition(new(1f, 0f, 1f));

        // The wall, standing on it.
        mesh.AddPosition(new(-1f, 1f, 0f));
        mesh.AddPosition(new(1f, 1f, 0f));

        mesh.AddFace([2, 3, 1]);
        mesh.AddFace([2, 1, 0]);
        mesh.AddFace([0, 1, 5]);
        mesh.AddFace([0, 5, 4]);
        mesh.AddFace([0, 6, 7]);
        mesh.AddFace([0, 7, 1]);

        return mesh;
    }

    /// <summary>Two boxes through one another, appended rather than booleaned.</summary>
    public static EditMesh SelfIntersecting() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        MeshOperations.Append(
            mesh,
            MeshShapes.Create(ShapeKind.Box),
            Matrix4x4.FromTranslation(new(0.9f, 0.9f, 0.9f))
        );

        return mesh;
    }

    /// <summary>A strip of triangles so thin they have no meaningful normal.</summary>
    public static EditMesh Slivers(int count = 24) {
        var mesh = new EditMesh();

        for (var index = 0; index <= count; index++) {
            mesh.AddPosition(new(index * 0.1f, 0f, 0f));
            mesh.AddPosition(new(index * 0.1f, 0f, 1e-6f));
        }

        for (var index = 0; index < count; index++) {
            mesh.AddFace([index * 2, (index * 2) + 2, (index * 2) + 1]);
            mesh.AddFace([(index * 2) + 1, (index * 2) + 2, (index * 2) + 3]);
        }

        return mesh;
    }

    /// <summary>A box with every face listed a second time, so every edge has four.</summary>
    public static EditMesh DuplicateFaces() {
        var source = MeshShapes.Create(ShapeKind.Box);
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position);
        }

        for (var pass = 0; pass < 2; pass++) {
            for (var face = 0; face < source.FaceCount; face++) {
                mesh.AddFace(source.CornersOf(face), source.Faces[face].Group);
            }
        }

        return mesh;
    }

    /// <summary>A grid with no rim closed, which every boundary rule has to survive.</summary>
    public static EditMesh OpenSurface() =>
        MeshShapes.Create(ShapeParameters.Default(ShapeKind.Plane) with { Sides = 6, Steps = 6 });

    /// <summary>Several primitives in the same file, sharing nothing.</summary>
    public static EditMesh DisconnectedPile() {
        var mesh = MeshShapes.Create(ShapeKind.Box);

        MeshOperations.Append(mesh, MeshShapes.Create(ShapeKind.Sphere), Matrix4x4.FromTranslation(new(6f, 0f, 0f)));
        MeshOperations.Append(mesh, MeshShapes.Create(ShapeKind.Cone), Matrix4x4.FromTranslation(new(0f, 0f, 6f)));
        MeshOperations.Append(mesh, MeshShapes.Create(ShapeKind.Torus), Matrix4x4.FromTranslation(new(6f, 0f, 6f)));

        return mesh;
    }

    /// <summary>A Möbius band: one component with no consistent winding at all.</summary>
    /// <param name="segments">How many rings round it. Must be even for the seam to land cleanly.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     ⚠ <b>The point is that this is not broken.</b> There is no assignment of windings that
    ///     makes every pair of neighbours agree, because the surface has one side. So the flood fill
    ///     must report it into <see cref="ConditioningReport.Unorientable" /> rather than declare a
    ///     winner — a conditioner that "fixed" this produced something wrong and said nothing.
    /// </remarks>
    public static EditMesh Mobius(int segments = 32) {
        var mesh = new EditMesh();

        for (var index = 0; index < segments; index++) {
            var angle = MathF.Tau * index / segments;
            var half = angle * 0.5f;
            var centre = new Vector3(MathF.Cos(angle) * 2f, 0f, MathF.Sin(angle) * 2f);
            var radial = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var across = (radial * MathF.Sin(half)) + (Vector3.UnitY * MathF.Cos(half));

            mesh.AddPosition(centre - (across * 0.5f));
            mesh.AddPosition(centre + (across * 0.5f));
        }

        for (var index = 0; index < segments; index++) {
            var a = index * 2;
            var b = a + 1;

            // ⚠ The last ring joins the first with its two sides swapped, and that is the half
            // twist. Every other ring joins the next one straight.
            var next = (index + 1) % segments;
            var c = index + 1 == segments ? next * 2 : (next * 2) + 1;
            var d = index + 1 == segments ? (next * 2) + 1 : next * 2;

            mesh.AddFace([a, b, c]);
            mesh.AddFace([a, c, d]);
        }

        return mesh;
    }

    /// <summary>A closed box with a third of its faces turned inside out.</summary>
    public static EditMesh InconsistentWinding() {
        var source = MeshShapes.Create(ShapeKind.Box);
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position);
        }

        for (var face = 0; face < source.FaceCount; face++) {
            var corners = source.CornersOf(face);

            if (face % 3 == 0) {
                mesh.AddFace([corners[2], corners[1], corners[0]], source.Faces[face].Group);
            } else {
                mesh.AddFace(corners, source.Faces[face].Group);
            }
        }

        return mesh;
    }

    /// <summary>The same mesh with every triangle carrying its own three positions.</summary>
    /// <param name="source">What to take apart.</param>
    /// <returns>A mesh with three times as many positions and no shared edges at all.</returns>
    /// <remarks>
    ///     ⚠ <b>What every measured TRELLIS export looks like: a position ratio of exactly three.</b>
    ///     Until this is welded the mesh has no edges — every one of them is a boundary — so a
    ///     conditioner that skipped step one would find no adjacency and report a component per
    ///     triangle.
    /// </remarks>
    public static EditMesh Unwelded(EditMesh source) {
        ArgumentNullException.ThrowIfNull(source);

        var indices = source.Triangulate();
        var mesh = new EditMesh();

        Span<int> loop = stackalloc int[3];

        for (var triangle = 0; triangle * 3 < indices.Length; triangle++) {
            for (var corner = 0; corner < 3; corner++) {
                loop[corner] = mesh.AddPosition(source.Positions[indices[(triangle * 3) + corner]]);
            }

            mesh.AddFace(loop);
        }

        return mesh;
    }

    /// <summary>Two spheres, the second a chosen fraction of the first's surface area.</summary>
    /// <param name="fraction">The smaller's area as a fraction of the larger's.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     ⚠ <b>Both sides of the de-speck threshold live here.</b> Forty percent is a character's
    ///     separate eyeball and must survive; a hundredth of a percent is an SDF's hallucinated
    ///     debris and must not. Area goes as the square of the radius, so the radius is the root of
    ///     the fraction.
    /// </remarks>
    public static EditMesh TwoComponents(float fraction) {
        var mesh = MeshShapes.Create(ShapeKind.Sphere);
        var scale = MathF.Sqrt(fraction);

        MeshOperations.Append(
            mesh,
            MeshShapes.Create(ShapeKind.Sphere),
            Matrix4x4.Compose(new(scale), Quaternion.Identity, new(8f, 0f, 0f))
        );

        return mesh;
    }

    /// <summary>The same mesh with every n-gon already ear-clipped, as one triangle per face.</summary>
    /// <param name="source">What to flatten.</param>
    /// <returns>A mesh of triangles only, over the same positions.</returns>
    /// <remarks>
    ///     ⚠ <b>What a scale-invariance fixture has to be built from, and the reason is a
    ///     scale-sensitivity in <see cref="EditMesh.Triangulate" /> rather than in anything here.</b>
    ///     Ear clipping projects a face into its own plane and tests <c>Cross(a, b, c) &lt;= 0f</c>
    ///     against exact zero. A generated quad is not exactly planar, so which corner is an ear can
    ///     land either way under a change of scale: measured on <see cref="Specks" />, 1248 of 2016
    ///     indices differ between the same mesh at a thousandth and a thousand times its size. Scaling
    ///     <i>after</i> triangulating takes that out of the comparison, so that what is left is a
    ///     statement about conditioning.
    /// </remarks>
    public static EditMesh Triangulated(EditMesh source) {
        ArgumentNullException.ThrowIfNull(source);

        var indices = source.Triangulate();
        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position);
        }

        for (var triangle = 0; triangle * 3 < indices.Length; triangle++) {
            mesh.AddFace(indices.AsSpan(triangle * 3, 3));
        }

        return mesh;
    }

    /// <summary>The same mesh, every position multiplied by a factor.</summary>
    /// <param name="source">What to scale.</param>
    /// <param name="factor">By how much.</param>
    /// <returns>A new mesh with the same topology at a different size.</returns>
    public static EditMesh Scaled(EditMesh source, float factor) {
        ArgumentNullException.ThrowIfNull(source);

        var mesh = new EditMesh();

        foreach (var position in source.Positions) {
            mesh.AddPosition(position * factor);
        }

        for (var face = 0; face < source.FaceCount; face++) {
            mesh.AddFace(source.CornersOf(face), source.Faces[face].Group);
        }

        return mesh;
    }
}
