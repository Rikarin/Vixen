// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>Where a ray met the mesh, and everything the atlas half of a stamp needs from it.</summary>
/// <param name="Triangle">Which triangle, indexed as <see cref="PaintProjection" /> holds them, or -1 for a miss.</param>
/// <param name="Barycentric">The weights of that triangle's three corners at the hit.</param>
/// <param name="Coordinate">The texture coordinate there, interpolated — the unit square, not texels.</param>
/// <param name="Point">Where on the surface it landed, in the mesh's own space.</param>
/// <param name="Normal">The triangle's geometric normal, unit, for the grazing half of the footprint.</param>
/// <param name="Distance">How far along the ray it was, in the units the mesh is in.</param>
/// <remarks>
///     ⚠ <b>The coordinate is in the unit square and not in texels, because the atlas size is not the
///     mesh's business.</b> One stack paints several sets at several resolutions off one mesh, and a
///     hit that already carried texels would be a hit that had to be recast when the artist changed
///     the resolution. <see cref="PaintProjection.Texel" /> is where the multiplication happens, and
///     it is the same multiplication <c>PaintCoverage</c> rasterises with.
/// </remarks>
readonly record struct PaintHit(
    int Triangle,
    Vector3 Barycentric,
    Vector2 Coordinate,
    Vector3 Point,
    Vector3 Normal,
    float Distance
) {
    /// <summary>Whether the ray met the mesh at all.</summary>
    public bool Found => Triangle >= 0;

    /// <summary>A miss.</summary>
    public static PaintHit None { get; } = new(-1, Vector3.Zero, Vector2.Zero, Vector3.Zero, Vector3.UnitY, 0f);
}

/// <summary>How many texels of the atlas one unit of surface is worth, along each of its two axes.</summary>
/// <param name="Major">The stretched direction: the most texels a unit of surface buys.</param>
/// <param name="Minor">The squashed one: the fewest.</param>
/// <remarks>
///     <para>
///         <b>⚠ Two numbers and not one, and that is the whole finding of doc 48 § M9's second
///         box.</b> A brush is a disc on the screen; its footprint in the atlas is an
///         <em>ellipse</em>, because the map from the surface to the atlas is an arbitrary linear
///         map on each triangle and only an isometric layout makes it a circle. A conversion that
///         answered with one number would be right on a cube and wrong on everything an artist
///         unwraps, and — this is what makes it expensive to find — it would <em>look</em> right on
///         the cube every test would be written against.
///     </para>
///     <para>
///         ⚠ <b>And it is measured on the hit triangle rather than on the island.</b>
///         <c>UvDensity.Measure</c> answers texels per square unit <em>per chart</em>, which is the
///         number a packer needs and the average of what a brush wants: a chart whose corner is
///         stretched four to one and whose middle is not has one density and two answers. This is
///         the triangle's own Jacobian, so the answer moves as the pointer crosses the chart.
///     </para>
/// </remarks>
readonly record struct PaintDensity(float Major, float Minor) {
    /// <summary>The radius of the disc with the same area as the ellipse, per unit of surface.</summary>
    /// <remarks>
    ///     ⚠ <b>The geometric mean, which is a compromise stated rather than hidden.</b> The stamp
    ///     kernel is a disc — <c>TerrainBrush.WeightAt</c> takes one radius — so a stamp on a
    ///     stretched chart must be wrong in one direction. Sizing by <see cref="Major" /> paints
    ///     past where the artist swept, by <see cref="Anisotropy" />, in the squashed direction;
    ///     sizing by <see cref="Minor" /> leaves a sliver the artist cannot see and cannot fix
    ///     without a second pass. The mean is wrong by the square root either way and preserves the
    ///     painted area, which is the same choice a mip selector makes for the same reason. The
    ///     actual fix is an elliptical stamp and it is <b>not</b> here — see the project README.
    /// </remarks>
    public float Area => MathF.Sqrt(Major * Minor);

    /// <summary>How far from a circle the footprint is: one for an isometric chart, upwards for a stretched one.</summary>
    /// <remarks>
    ///     ⚠ <b>Infinite for a chart with no area, and that is the honest answer rather than a
    ///     guard.</b> A triangle whose three coordinates are collinear occupies no texels at all, so
    ///     there is no radius that paints it; a caller reading a finite number there would be
    ///     reading one this type invented.
    /// </remarks>
    public float Anisotropy => Minor > 0f ? Major / Minor : float.PositiveInfinity;

    /// <summary>Whether the triangle carries a usable layout at all.</summary>
    public bool IsMeasurable => Minor > 0f && float.IsFinite(Major);
}

/// <summary>
///     The mesh a stack is painted on, as the one question a 3D viewport asks of it: what texel is
///     under this ray.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13's <em>first</em> front end, and the half issue
///         <a href="https://github.com/Rikarin/Vixen/issues/574">#574</a> had none of.</b>
///         <c>PaintSession</c>'s remarks name three things a surface owes and this is the first two:
///         a pointer position turned into texels, and a screen-space brush size turned into a radius
///         in texels. The third — the mirrors — is <see cref="PaintSymmetry" />, and
///         <see cref="PaintProjector" /> is where all three meet.
///     </para>
///     <para>
///         ⚠ <b>No raycaster was written and that was the first decision.</b>
///         <see cref="TriangleTree" /> is a median-split BVH in <c>Vixen.Core.Mathematics</c> that
///         already answers with the triangle, the barycentric weights and the distance — which is
///         exactly and only what an interpolated coordinate needs. It was moved there out of
///         <c>Vixen.Rendering.DistanceFields</c> the moment retopology became its second caller;
///         this is its third, and a second implementation beside it would have been the defect this
///         workstream keeps finding rather than a feature.
///     </para>
///     <para>
///         ⚠ <b><see cref="TriangleTree.Raycast(Vector3, Vector3)" /> bounds the search at the
///         <em>length of the direction</em>, so a unit direction is a search radius of one unit.</b>
///         That is deliberate there — a bake's search radius is a fraction of the model's diagonal,
///         and doc 41's scale rule is why — and it is a trap for a picking ray, where the caller has
///         a normalised direction and a camera that may be a hundred units away. A picker that
///         passed the unit direction would miss every mesh further off than one unit and would look
///         exactly like a broken raycast on a large model and a working one on a small one.
///         <see cref="TryHit" /> scales the direction by a reach taken from the tree's own bounds
///         for that reason, and converts the fraction it gets back into a distance.
///     </para>
///     <para>
///         ⚠ <b>Positions and coordinates are two parallel arrays over one index list, so the
///         raycast and the layout cannot disagree about which triangle is which.</b> That is also
///         why a triangle missing either is dropped rather than clamped, exactly as
///         <c>LayerStackMesh.Triangulate</c> drops one: a clamped corner is a triangle at the origin
///         of the atlas, which rasterises as covered and would take a hit no artist aimed at.
///     </para>
/// </remarks>
sealed class PaintProjection {
    readonly Vector3[] positions;
    readonly Vector2[] coordinates;
    readonly int[] indices;
    readonly TriangleTree tree;
    readonly float reach;

    PaintProjection(Vector3[] positions, Vector2[] coordinates, int[] indices) {
        this.positions = positions;
        this.coordinates = coordinates;
        this.indices = indices;

        tree = new(positions, indices);

        var bounds = tree.Bounds;
        reach = Vector3.Distance(bounds.Minimum, bounds.Maximum);
    }

    /// <summary>How many triangles carry both a position and a coordinate.</summary>
    public int Triangles => indices.Length / 3;

    /// <summary>The box the mesh fits inside, in its own space.</summary>
    public BoundingBox Bounds => tree.Bounds;

    /// <summary>What ray meets what texel.</summary>
    /// <param name="ray">The ray, in the mesh's own space. Its direction need not be normalised.</param>
    /// <param name="hit">Where it landed.</param>
    /// <returns>Whether it landed anywhere.</returns>
    /// <remarks>
    ///     ⚠ <b>The direction is normalised here and then lengthened, rather than taken as the
    ///     caller sent it.</b> See this type's remarks: the tree reads the direction's length as the
    ///     search radius, and a viewport's ray carries a unit direction because every other consumer
    ///     of one wants a distance in world units back. Normalising and scaling by the diagonal —
    ///     plus how far outside the box the eye is — makes the search cover the mesh from any camera
    ///     position and keeps <see cref="PaintHit.Distance" /> in the mesh's units.
    /// </remarks>
    public bool TryHit(Ray ray, out PaintHit hit) {
        hit = PaintHit.None;

        var length = ray.Direction.Length();

        if (Triangles == 0 || !(length > 0f) || !float.IsFinite(length)) {
            return false;
        }

        var direction = ray.Direction / length;

        // The diagonal plus however far outside the box the eye is, so the segment reaches the far
        // side of the mesh from anywhere. A zero reach — a tree over one degenerate triangle — would
        // make every ray parallel to itself, so it is floored.
        var outside = Vector3.Distance(ray.Origin, Bounds.Center);
        var far = MathF.Max(reach + outside, 1e-4f);
        var found = tree.Raycast(ray.Origin, direction * far);

        if (found.Triangle < 0) {
            return false;
        }

        Corners(found.Triangle, out var a, out var b, out var c);
        Layout(found.Triangle, out var ua, out var ub, out var uc);

        var weights = found.Barycentric;

        hit = new(
            found.Triangle,
            weights,
            (ua * weights.X) + (ub * weights.Y) + (uc * weights.Z),
            found.Point,
            Facing(a, b, c),
            found.Distance * far
        );

        return true;
    }

    /// <summary>The triangle's geometric normal, at any scale.</summary>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second.</param>
    /// <param name="c">The third.</param>
    /// <returns>The unit normal, or zero for a degenerate triangle.</returns>
    /// <remarks>
    ///     ⚠ <b>Not <c>Vector3.Normalize(Cross(…))</c>, which gives up on an <em>absolute</em>
    ///     length.</b> <c>MathUtil.ZeroTolerance</c> is 1e-6 and a cross product is twice the
    ///     triangle's area, so an equilateral triangle whose side is under about 1.07e-3 mesh units
    ///     has a cross product shorter than the tolerance and <c>Normalize</c> answers
    ///     <c>Zero</c> — on a perfectly ordinary triangle whose corners are a millimetre apart.
    ///     ⚠ <b>And the failure is silent and wrong in the expensive direction</b>: a zero normal
    ///     makes <see cref="PaintFootprint" />'s grazing cosine fall to its floor, so the brush comes
    ///     out about 3.2× too wide, face-on, on small meshes only. The edges are scaled to unit
    ///     length before the cross, which moves the tolerance from "how big is this triangle" to
    ///     "is this triangle degenerate", which is the question actually being asked.
    /// </remarks>
    static Vector3 Facing(Vector3 a, Vector3 b, Vector3 c) {
        var first = b - a;
        var second = c - a;
        var one = first.Length();
        var two = second.Length();

        if (one <= 0f || two <= 0f) {
            return Vector3.Zero;
        }

        return Vector3.Normalize(Vector3.Cross(first / one, second / two));
    }

    /// <summary>Where a coordinate lands in an atlas of a size.</summary>
    /// <param name="coordinate">The coordinate, in the unit square.</param>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The position, in texels.</returns>
    /// <remarks>
    ///     ⚠ <b>Coordinate times size, with no half-texel anywhere, because
    ///     <c>PaintCoverage.FromTriangles</c> rasterises with exactly that rule.</b> The two have to
    ///     agree or a hit lands one texel off the island the artist aimed at, and the stroke is
    ///     refused — or worse, dilated — at a boundary the pointer was nowhere near. One rule and
    ///     not two is the whole of why this is a method rather than a multiplication at each call.
    /// </remarks>
    public static Vector2 Texel(Vector2 coordinate, int width, int height) =>
        new(coordinate.X * width, coordinate.Y * height);

    /// <summary>How much of the atlas a unit of surface is worth on one triangle.</summary>
    /// <param name="triangle">Which triangle, as <see cref="PaintHit.Triangle" /> reports it.</param>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The two densities, in texels per unit of surface.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such triangle.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The singular values of the map from the triangle's plane to the atlas.</b> Write a
    ///         tangent vector as <c>a·e₁ + b·e₂</c> over the triangle's two edge vectors; the same
    ///         weights over the two coordinate edges give the offset in the atlas, so the map is the
    ///         2×2 matrix <c>D·E⁻¹</c> in any orthonormal basis of the plane. Its singular values
    ///         are how far the unit circle is stretched, which is precisely the ellipse a screen
    ///         disc becomes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The basis is built from the triangle and never from a world axis.</b> A basis
    ///         taken from, say, the largest component of the normal is discontinuous across the
    ///         sphere, so two neighbouring triangles either side of that switch would report
    ///         densities that differ by nothing physical — a brush that changed size as the pointer
    ///         crossed a seam that is not in the layout.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A degenerate triangle answers zero rather than a very large number.</b> Either
    ///         degeneracy — no area on the mesh, or no area in the atlas — makes the map singular,
    ///         and a caller that divided by the answer would get a brush the size of the atlas from
    ///         a triangle that owns none of it. <see cref="PaintDensity.IsMeasurable" /> is the
    ///         question to ask.
    ///     </para>
    /// </remarks>
    public PaintDensity Density(int triangle, int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegative(triangle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(triangle, Triangles);

        Corners(triangle, out var a, out var b, out var c);
        Layout(triangle, out var ua, out var ub, out var uc);

        var e1 = b - a;
        var e2 = c - a;
        var span = Vector3.Cross(e1, e2).Length();
        var length = e1.Length();

        if (!(span > 0f) || !(length > 0f)) {
            return default;
        }

        // The coordinate edges in texels. `Texel` is not called: this is a difference of two
        // coordinates rather than a position, so the same scale applies and the origin does not.
        var d1 = new Vector2((ub.X - ua.X) * width, (ub.Y - ua.Y) * height);
        var d2 = new Vector2((uc.X - ua.X) * width, (uc.Y - ua.Y) * height);

        // The plane basis is (e₁/|e₁|, ⟂), in which e₁ is (|e₁|, 0) and e₂ is (e₁·e₂/|e₁|, span/|e₁|).
        // Inverting that 2×2 and multiplying by [d₁ d₂] leaves these two columns, with the
        // determinant — which is exactly `span` — already divided out.
        var first = d1 / length;
        var second = ((d2 * length) - (d1 * (Vector3.Dot(e1, e2) / length))) / span;

        Singular(first, second, out var major, out var minor);

        return new(major, minor);
    }

    /// <summary>The mesh, as the triangles that carry both geometry and a layout.</summary>
    /// <param name="meshes">The imported meshes a set resolved to, in the order they were declared.</param>
    /// <param name="refusal">Why there is none, or empty.</param>
    /// <returns>The projection, or <see langword="null" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meshes" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same <c>MeshData</c> <c>LayerStackMesh</c> already reads, and deliberately
    ///         not a second read of the model.</b> That type resolves a stack's model through the
    ///         project's import artefacts and falls back to the source file, with five distinct
    ///         refusals about which of those failed; a projection that opened the model itself would
    ///         be a second opinion about all five, and the two would disagree the first time an
    ///         import was stale. What it does <em>not</em> keep is the positions, which is the one
    ///         thing a raycast needs — see this project's README.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every failure is a returned sentence and none is an exception</b>, for
    ///         <c>LayerStackMesh</c>'s reason: this is asked from a pointer-down and a throw out of
    ///         one takes the editor's frame with it.
    ///     </para>
    /// </remarks>
    public static PaintProjection? Open(IReadOnlyList<MeshData> meshes, out string refusal) {
        ArgumentNullException.ThrowIfNull(meshes);

        refusal = "";

        List<Vector3> points = [];
        List<Vector2> layout = [];
        List<int> triangles = [];
        var dropped = 0;

        foreach (var mesh in meshes) {
            var offset = points.Count;
            var vertices = mesh.Positions;
            var uvs = mesh.TexCoords;
            var order = mesh.Indices;

            for (var vertex = 0; vertex < vertices.Length; vertex++) {
                points.Add(vertices[vertex]);
                layout.Add(vertex < uvs.Length ? uvs[vertex] : Vector2.Zero);
            }

            for (var triangle = 0; triangle + 2 < order.Length; triangle += 3) {
                var a = order[triangle];
                var b = order[triangle + 1];
                var c = order[triangle + 2];

                // ⚠ Both arrays, and the coordinate one is the test that fires. A mesh with no atlas
                // has an empty `TexCoords` and a perfectly good `Positions`, so a check on the
                // geometry alone would build a tree that raycasts beautifully and answers (0, 0) for
                // every hit — a brush that paints one corner of the atlas whatever the artist aims at.
                if ((uint)a >= (uint)uvs.Length || (uint)b >= (uint)uvs.Length || (uint)c >= (uint)uvs.Length
                    || (uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length
                    || (uint)c >= (uint)vertices.Length) {
                    dropped++;

                    continue;
                }

                triangles.Add(offset + a);
                triangles.Add(offset + b);
                triangles.Add(offset + c);
            }
        }

        if (triangles.Count == 0) {
            refusal = dropped > 0
                ? $"None of this mesh's {dropped} triangles has texture coordinates on all three corners, "
                + "so there is no layout to project a stroke into. Unwrap it before painting on it."
                : "This mesh has no triangles with both geometry and texture coordinates, so there is "
                + "nothing to aim at.";

            return null;
        }

        return new([.. points], [.. layout], [.. triangles]);
    }

    /// <summary>The triangles themselves, for a caller that has geometry and no import.</summary>
    /// <param name="positions">The vertex positions.</param>
    /// <param name="coordinates">Their texture coordinates, one per position.</param>
    /// <param name="indices">Three indices per triangle.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The two arrays disagree, or the indices are not whole triangles.</exception>
    public static PaintProjection Over(Vector3[] positions, Vector2[] coordinates, int[] indices) {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(indices);

        if (positions.Length != coordinates.Length) {
            throw new ArgumentException(
                $"{positions.Length} positions and {coordinates.Length} coordinates cannot be one vertex "
                + "list, and a raycast that indexed them separately would report a hit on one triangle "
                + "and the layout of another.",
                nameof(coordinates)
            );
        }

        if (indices.Length % 3 != 0) {
            throw new ArgumentException($"{indices.Length} indices is not a whole number of triangles.", nameof(indices));
        }

        return new([.. positions], [.. coordinates], [.. indices]);
    }

    /// <summary>The larger and smaller singular value of the 2×2 matrix with these two columns.</summary>
    /// <param name="first">Its first column.</param>
    /// <param name="second">Its second.</param>
    /// <param name="major">The larger.</param>
    /// <param name="minor">The smaller.</param>
    /// <remarks>
    ///     ⚠ <b>Closed form off the two invariants rather than an iteration.</b> The squared singular
    ///     values are the roots of <c>s⁴ − ‖M‖²s² + det² = 0</c>, so they need one square root each
    ///     and nothing that can fail to converge — this runs per pointer-down and once more per
    ///     mirror. The discriminant is floored at zero because it is exactly zero for an isometric
    ///     map, where rounding puts it either side.
    /// </remarks>
    static void Singular(Vector2 first, Vector2 second, out float major, out float minor) {
        var norm = first.LengthSquared() + second.LengthSquared();
        var determinant = (first.X * second.Y) - (second.X * first.Y);
        var root = MathF.Sqrt(MathF.Max((norm * norm) - (4f * determinant * determinant), 0f));

        major = MathF.Sqrt(MathF.Max((norm + root) * 0.5f, 0f));
        minor = MathF.Sqrt(MathF.Max((norm - root) * 0.5f, 0f));
    }

    void Corners(int triangle, out Vector3 a, out Vector3 b, out Vector3 c) {
        var slot = triangle * 3;

        a = positions[indices[slot]];
        b = positions[indices[slot + 1]];
        c = positions[indices[slot + 2]];
    }

    void Layout(int triangle, out Vector2 a, out Vector2 b, out Vector2 c) {
        var slot = triangle * 3;

        a = coordinates[indices[slot]];
        b = coordinates[indices[slot + 1]];
        c = coordinates[indices[slot + 2]];
    }
}
