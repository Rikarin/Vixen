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
/// <param name="Distance">How far along the ray it was, in the units the mesh is in.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The coordinate is in the unit square and not in texels, because the atlas size is not
///         the mesh's business.</b> One stack paints several sets at several resolutions off one
///         mesh, and a hit that already carried texels would be a hit that had to be recast when the
///         artist changed the resolution. <see cref="PaintProjection.Texel" /> is where the
///         multiplication happens, and it is the same multiplication <c>PaintCoverage</c>
///         rasterises with.
///     </para>
///     <para>
///         ⚠ <b>There is no normal here, and there was one until
///         <a href="https://github.com/Rikarin/Vixen/issues/1075">#1075</a>.</b>
///         <c>PaintFootprint</c> was its only reader, and it now takes the tilt from
///         <see cref="PaintDensity.Normal" /> — the same triangle's plane, measured once, in the
///         basis the layout's Jacobian is expressed in. Two spellings of one plane is what made the
///         two halves of the footprint impossible to compose: a caller could pair a hit on one
///         triangle with a density from another and nothing could tell.
///     </para>
/// </remarks>
readonly record struct PaintHit(int Triangle, Vector3 Barycentric, Vector2 Coordinate, Vector3 Point, float Distance) {
    /// <summary>Whether the ray met the mesh at all.</summary>
    public bool Found => Triangle >= 0;

    /// <summary>A miss.</summary>
    public static PaintHit None { get; } = new(-1, Vector3.Zero, Vector2.Zero, Vector3.Zero, 0f);
}

/// <summary>The whole map from one triangle's own plane to the atlas, in texels per unit of surface.</summary>
/// <param name="PerTangent">Where one unit along <paramref name="Tangent" /> lands in the atlas, in texels.</param>
/// <param name="PerBitangent">And one unit along <paramref name="Bitangent" />.</param>
/// <param name="Tangent">The plane basis's first axis, unit, in the mesh's own space.</param>
/// <param name="Bitangent">Its second: unit, in the same plane, and perpendicular to the first.</param>
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
///     <para>
///         ⚠ <b>The 2×2 itself and the basis it is written in, and it was three scalars until
///         <a href="https://github.com/Rikarin/Vixen/issues/1075">#1075</a>.</b> The singular values
///         and the atlas-space angle below are still the answer a stamp wants, but they are a
///         <em>lossy</em> reading of the map: a caller holding them cannot say where in the atlas a
///         particular direction on the surface goes, so it cannot compose a second map with this
///         one. The grazing tilt is exactly such a second map — a 2×2 in this same plane — and
///         composing it is one multiply and one SVD rather than two SVDs whose shapes cannot be
///         multiplied back together. <see cref="Tilted" /> is that multiply.
///     </para>
///     <para>
///         ⚠ <b>So the basis is part of the answer rather than an implementation detail of it.</b>
///         It is built from the triangle and never from a world axis — see
///         <see cref="PaintProjection.Density" /> — which is what makes it continuous across a
///         sphere; and handing it out is what lets a ray's direction be written in the same
///         coordinates the layout's Jacobian is.
///     </para>
/// </remarks>
readonly record struct PaintDensity(Vector2 PerTangent, Vector2 PerBitangent, Vector3 Tangent, Vector3 Bitangent) {
    /// <summary>The triangle's geometric normal, unit, in the mesh's own space.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived rather than carried, because a stored normal is a fourth number that can
    ///     disagree with the three beside it.</b> <see cref="Tangent" /> and
    ///     <see cref="Bitangent" /> are orthonormal and span the triangle's plane, so their cross
    ///     product <em>is</em> the normal — and the grazing cosine read off it therefore cannot be
    ///     measured on a different triangle from the Jacobian it is composed with, which is what
    ///     <c>PaintHit.Normal</c> allowed.
    /// </remarks>
    public Vector3 Normal => Vector3.Cross(Tangent, Bitangent);

    /// <summary>The stretched direction: the most texels a unit of surface buys.</summary>
    public float Major {
        get {
            Axes(out var major, out _);

            return major;
        }
    }

    /// <summary>The squashed one: the fewest.</summary>
    public float Minor {
        get {
            Axes(out _, out var minor);

            return minor;
        }
    }

    /// <summary>
    ///     Which way <see cref="Major" /> points <b>in the atlas</b>, in radians anticlockwise from
    ///     its first axis. Zero for an isometric map, where there is no stretched direction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <em>left</em> singular vector, and issue
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a> said the right one —
    ///         which is the wrong half and would have shipped a brush whose long axis pointed
    ///         somewhere unrelated on every non-conformal chart.</b> Write the map as
    ///         <c>M = UΣVᵀ</c>. The right singular vectors are directions on the <em>surface</em>:
    ///         which way to walk to be stretched most. The ellipse a screen disc becomes lives in
    ///         the atlas, and its axes are the <em>images</em> of those directions — the columns of
    ///         <c>U</c>. They agree only when <c>M</c> is symmetric, which a texture layout has no
    ///         reason to be, and a fixture whose stretch is axis-aligned cannot tell them apart
    ///         because both come out at zero.
    ///     </para>
    ///     <para>
    ///         <c>U</c>'s columns are the eigenvectors of <c>MMᵀ</c>, which is 2×2 and symmetric, so
    ///         the principal angle is a single <c>atan2</c> off its three entries and nothing here
    ///         can fail to converge. A map with no stretch answers zero, which is the honest reading
    ///         of "there is no long axis" rather than a guard: <see cref="Anisotropy" /> is one
    ///         there, so the angle multiplies nothing.
    ///     </para>
    /// </remarks>
    public float Orientation {
        get {
            var xx = (PerTangent.X * PerTangent.X) + (PerBitangent.X * PerBitangent.X);
            var yy = (PerTangent.Y * PerTangent.Y) + (PerBitangent.Y * PerBitangent.Y);
            var xy = (PerTangent.X * PerTangent.Y) + (PerBitangent.X * PerBitangent.Y);

            return 0.5f * MathF.Atan2(2f * xy, xx - yy);
        }
    }

    /// <summary>The radius of the disc with the same area as the ellipse, per unit of surface.</summary>
    /// <remarks>
    ///     ⚠ <b>The geometric mean, and it is now the ellipse's <em>size</em> rather than a
    ///     compromise about its shape.</b> It used to be both: the stamp kernel was a disc, so a
    ///     stamp on a chart stretched four to one was twice too wide in one direction and twice too
    ///     narrow in the other — <a href="https://github.com/Rikarin/Vixen/issues/1064">#1064</a>.
    ///     <c>PaintBrush.Aspect</c> now carries the shape and this carries the area, which is what
    ///     makes the pair exact: the mean is the radius of the equal-area disc, and
    ///     <see cref="Anisotropy" /> and <see cref="Orientation" /> put it back into an ellipse.
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

    /// <summary>The same map with the grazing tilt composed into it, for a ray that struck the plane.</summary>
    /// <param name="direction">Which way the ray was going, in the mesh's own space. Need not be unit.</param>
    /// <param name="floor">
    ///     How small the cosine is allowed to get before it is capped — <c>PaintFootprint.GrazingFloor</c>.
    /// </param>
    /// <returns>The composed map, in the same basis, or this one when there is no tilt to compose.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ The second half of the ellipse, and it was collapsed to an area factor until
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1075">#1075</a>.</b> A disc on the
    ///         screen lands on a surface tilted away from the viewer as an ellipse whose long axis
    ///         is <c>1 / cos θ</c> times its short one, along the projection of the ray into the
    ///         tangent plane. Dividing the radius by <c>√cos θ</c> keeps that ellipse's <em>area</em>
    ///         and throws its <em>shape</em> away — which at 60° off the normal is a 2:1 error, the
    ///         same order as a 4:1 chart's contribution, and it is worst exactly at the silhouette
    ///         where every stroke reaching round a shape is made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two maps multiply, so there is one SVD and not two.</b> Both are 2×2 in this
    ///         type's own basis: the tilt takes the screen disc to an ellipse <em>on the plane</em>,
    ///         and <see cref="PerTangent" />/<see cref="PerBitangent" /> take the plane to the
    ///         atlas. Their product's singular values and left singular vectors are the atlas
    ///         ellipse exactly. ⚠ <b>Multiplying the two <em>scalars</em> instead is the thing that
    ///         looks like this and is not it</b>, and it is right whenever the tilt axis and the
    ///         chart's stretch axis coincide — which is every fixture whose tilt runs down a
    ///         coordinate axis of the layout.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The area is unchanged by the composition and that is a check rather than a
    ///         coincidence.</b> The tilt matrix has eigenvalues <c>1 / cos θ</c> and one, so its
    ///         determinant is <c>1 / cos θ</c> and <see cref="Area" /> — the square root of the
    ///         product of the singular values — comes out at exactly the old
    ///         <c>Area / √cos θ</c>. The fix moves the shape and leaves the size, so every number
    ///         measured about a brush's size before it still holds.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the cosine is floored, because at the silhouette itself it is zero.</b> The
    ///         true ellipse there is an unbounded strip and an unbounded brush is not one an artist
    ///         can use, so past the floor the stamp is smaller than the truth exactly where the
    ///         truth is that they cannot see what they are painting.
    ///     </para>
    /// </remarks>
    public PaintDensity Tilted(Vector3 direction, float floor) {
        var length = direction.Length();

        if (!(length > 0f) || !float.IsFinite(length)) {
            return this;
        }

        var ray = direction / length;

        // The ray in this map's own basis: two tangential components and the cosine.
        var x = Vector3.Dot(ray, Tangent);
        var y = Vector3.Dot(ray, Bitangent);
        var plane = MathF.Sqrt((x * x) + (y * y));

        if (!(plane > 0f)) {
            // Face-on. The tilt matrix is the identity and the ellipse is the layout's alone.
            return this;
        }

        var stretch = 1f / MathF.Max(MathF.Abs(Vector3.Dot(ray, Normal)), floor);
        var alongX = x / plane;
        var alongY = y / plane;

        // The tilt, as a symmetric 2×2 in this basis: `stretch` along the ray's own tangential
        // direction and one across it, which is `stretch · wwᵀ + ss ᵀ` written out.
        var first = (stretch * alongX * alongX) + (alongY * alongY);
        var cross = (stretch - 1f) * alongX * alongY;
        var second = (stretch * alongY * alongY) + (alongX * alongX);

        // ⚠ Both columns read the *old* pair, so they are named before either is written. A `with`
        // whose initialisers referred to each other would still bind to this instance rather than
        // to the copy, which is correct and is exactly the kind of correct nobody should have to
        // check twice.
        var tangent = PerTangent;
        var bitangent = PerBitangent;

        return this with {
            PerTangent = (tangent * first) + (bitangent * cross),
            PerBitangent = (tangent * cross) + (bitangent * second)
        };
    }

    /// <summary>The larger and smaller singular value of the map.</summary>
    /// <param name="major">The larger.</param>
    /// <param name="minor">The smaller.</param>
    /// <remarks>
    ///     ⚠ <b>Closed form off the two invariants rather than an iteration.</b> The squared singular
    ///     values are the roots of <c>s⁴ − ‖M‖²s² + det² = 0</c>, so they need one square root each
    ///     and nothing that can fail to converge — this runs per pointer-down and once more per
    ///     mirror. The discriminant is floored at zero because it is exactly zero for an isometric
    ///     map, where rounding puts it either side.
    /// </remarks>
    void Axes(out float major, out float minor) {
        var norm = PerTangent.LengthSquared() + PerBitangent.LengthSquared();
        var determinant = (PerTangent.X * PerBitangent.Y) - (PerBitangent.X * PerTangent.Y);
        var root = MathF.Sqrt(MathF.Max((norm * norm) - (4f * determinant * determinant), 0f));

        major = MathF.Sqrt(MathF.Max((norm + root) * 0.5f, 0f));
        minor = MathF.Sqrt(MathF.Max((norm - root) * 0.5f, 0f));
    }
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

        Layout(found.Triangle, out var ua, out var ub, out var uc);

        var weights = found.Barycentric;

        hit = new(
            found.Triangle,
            weights,
            (ua * weights.X) + (ub * weights.Y) + (uc * weights.Z),
            found.Point,
            found.Distance * far
        );

        return true;
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
    /// <returns>The map, in texels per unit of surface, with the basis it is written in.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such triangle.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The map from the triangle's plane to the atlas.</b> Write a tangent vector as
    ///         <c>a·e₁ + b·e₂</c> over the triangle's two edge vectors; the same weights over the
    ///         two coordinate edges give the offset in the atlas, so the map is the 2×2 matrix
    ///         <c>D·E⁻¹</c> in any orthonormal basis of the plane. Its singular values are how far
    ///         the unit circle is stretched, which is precisely the ellipse a screen disc becomes —
    ///         and <see cref="PaintDensity" /> carries the matrix rather than only those values, so
    ///         that the grazing tilt can be composed with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The basis is built from edges scaled to unit length, which is what makes it
    ///         survive a small model.</b> <c>Vector3.Normalize</c> gives up on an <em>absolute</em>
    ///         1e-6 and a cross product is twice the triangle's area, so it falls as the
    ///         <em>square</em> of the model: an equilateral triangle whose side is under about
    ///         1.07e-3 mesh units has a cross product shorter than the tolerance. ⚠ <b>And the
    ///         failure is silent and wrong in the expensive direction</b> — a collapsed basis makes
    ///         the grazing cosine fall to its floor, so the brush comes out about 3.2× too wide,
    ///         face-on, on small meshes only. Dividing the edges by their own lengths first moves
    ///         the tolerance from "how big is this triangle" to "is this triangle degenerate", which
    ///         is the question actually being asked.
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
        var length = e1.Length();
        var reach = e2.Length();

        if (!(length > 0f) || !(reach > 0f)) {
            return default;
        }

        // ⚠ The cross of the *unit* edges, so the test below is "is this triangle degenerate" and
        // not "is this triangle small". See the remarks.
        var tangent = e1 / length;
        var slant = e2 / reach;
        var span = Vector3.Cross(tangent, slant).Length();

        if (!(span > 0f)) {
            return default;
        }

        // The in-plane perpendicular, from the same unit edges: e₂ less its component along e₁,
        // whose length is exactly the sine `span` measured.
        var bitangent = (slant - (tangent * Vector3.Dot(tangent, slant))) / span;

        // The coordinate edges in texels. `Texel` is not called: this is a difference of two
        // coordinates rather than a position, so the same scale applies and the origin does not.
        var d1 = new Vector2((ub.X - ua.X) * width, (ub.Y - ua.Y) * height);
        var d2 = new Vector2((uc.X - ua.X) * width, (uc.Y - ua.Y) * height);

        // In the basis (tangent, bitangent), e₁ is (|e₁|, 0) and e₂ is (e₁·e₂/|e₁|, |e₁×e₂|/|e₁|).
        // Inverting that 2×2 and multiplying by [d₁ d₂] leaves these two columns, with the
        // determinant already divided out — `span · length · reach` being |e₁×e₂|.
        var first = d1 / length;
        var second = ((d2 * length) - (d1 * (Vector3.Dot(e1, e2) / length))) / (span * length * reach);

        return new(first, second, tangent, bitangent);
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
    ///         import was stale. ⚠ <b>It said here until 2026-09-09 that the positions were the one thing
    ///         that type does <em>not</em> keep</b>; it keeps them now, and
    ///         <c>LayerStackMesh.Projection</c> is the built projection, so one resolution feeds the
    ///         layout an artist aims with and the geometry a ray hits.
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
