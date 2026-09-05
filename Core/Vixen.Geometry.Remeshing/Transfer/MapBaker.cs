// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Which frame a baked normal is expressed in.</summary>
public enum BakeSpace : byte {
    /// <summary>The output surface's own tangent frame, derived from the atlas.</summary>
    /// <remarks>
    ///     The usual answer, because a tangent-space map survives the mesh being deformed — which
    ///     for a skinned character is the entire point of baking one.
    /// </remarks>
    Tangent,

    /// <summary>The mesh's own space, with no frame at all.</summary>
    /// <remarks>
    ///     ⚠ Unambiguous and undeformable. Worth having for a static prop, and worth having as the
    ///     thing to compare against when a tangent-space bake looks wrong, because it removes the
    ///     handedness convention from the question.
    /// </remarks>
    Object
}

/// <summary>Which of the mesh maps to measure, beyond the normal and the displacement.</summary>
/// <remarks>
///     <para>
///         <b>The normal map and the displacement map are not in here because they are not
///         optional.</b> They fall out of the one ray the bake already casts, and the seven below are
///         further measurements at the same texel, on the surface point that ray found.
///     </para>
///     <para>
///         ⚠ <b><see cref="AmbientOcclusion" />, <see cref="BentNormal" /> and
///         <see cref="Thickness" /> are the expensive ones and nothing else here is.</b> They are the
///         only three that cast rays of their own — <see cref="BakeSettings.OcclusionSamples" /> of
///         them per texel, against the same tree — and the three of them together cost what one of
///         them costs, because they share both the sample set and the loop. The other four are
///         arithmetic on a hit the bake already has.
///     </para>
/// </remarks>
[Flags]
public enum MeshMaps {
    /// <summary>Just the normal and the displacement, which is what a bake has always returned.</summary>
    None = 0,

    /// <summary>The unoccluded fraction of the cosine-weighted hemisphere.</summary>
    AmbientOcclusion = 1,

    /// <summary>The average unoccluded direction, from the same rays.</summary>
    BentNormal = 2,

    /// <summary>Mean curvature, interpolated from the source's cotangent Laplacian.</summary>
    Curvature = 4,

    /// <summary>The occluded fraction of the same hemisphere, inverted through the surface.</summary>
    Thickness = 8,

    /// <summary>The surface point, normalised into the source's bounding box.</summary>
    Position = 16,

    /// <summary>The source's normal, in the source's own space.</summary>
    WorldNormal = 32,

    /// <summary>The source's face group, nearest-sampled and never filtered.</summary>
    Id = 64,

    /// <summary>All seven.</summary>
    All = AmbientOcclusion | BentNormal | Curvature | Thickness | Position | WorldNormal | Id
}

/// <summary>What to bake, at what size, and how far to look for the source.</summary>
/// <remarks>
///     ⚠ <b><see cref="SearchRadius" /> is a fraction of the source's bounding-box diagonal and
///     never a distance.</b> A ray cage measured in metres is a claim about how big a model is, and
///     it is the same claim <see cref="ScaleSafe" /> exists to stop this library making — a bake
///     tuned on a character silently finds nothing on the same character exported in centimetres.
/// </remarks>
public sealed record BakeSettings {
    /// <summary>The atlas's edge length in texels. Required, because a gutter is counted in them.</summary>
    public required int Resolution { get; init; }

    /// <summary>How many texels of dilation surround each chart.</summary>
    /// <remarks>
    ///     ⚠ <b>A bake that stops at the chart boundary bleeds background at low mips.</b> Four is
    ///     the usual answer for a 2K atlas — enough for a trilinear tap plus two mip levels — and it
    ///     is the same number <see cref="Vixen.Geometry.Uv.PackSettings.Margin" /> defaults to, which
    ///     is not a coincidence: the gutter has to reach at least as far as the packer's spacing or
    ///     the two disagree about where a chart ends.
    /// </remarks>
    public int Gutter { get; init; } = 4;

    /// <summary>Which frame the normals come back in.</summary>
    public BakeSpace Space { get; init; } = BakeSpace.Tangent;

    /// <summary>How far a ray looks for the source, as a fraction of its bounding-box diagonal.</summary>
    public float SearchRadius { get; init; } = 0.05f;

    /// <summary>Which of the mesh maps to measure beyond the normal and the displacement.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty by default, because three of them cast rays.</b> A caller after a normal map
    ///     gets one for what a normal map costs, and the two measurements that need a hemisphere are
    ///     spent only when something asked for them.
    /// </remarks>
    public MeshMaps Maps { get; init; } = MeshMaps.None;

    /// <summary>How many rays the hemisphere is estimated with, per texel.</summary>
    /// <remarks>
    ///     ⚠ <b>The estimator's error falls as the square root of this</b>, so it is the one setting
    ///     in this record where doubling the cost buys about forty percent. Sixty-four is a preview;
    ///     a shipping bake of a hero asset wants several hundred.
    /// </remarks>
    public int OcclusionSamples { get; init; } = 64;

    /// <summary>How far an occlusion ray reaches, as a fraction of the source's diagonal.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction, for the reason <see cref="SearchRadius" /> is one</b> — and a different
    ///     fraction, because the two answer different questions. The search radius is how far the
    ///     cage strayed from the surface and is small; this is how far away an occluder still counts
    ///     and is a large part of the model. ⚠ <b><see cref="BakedMaps.Thickness" /> saturates at
    ///     it</b>: a part thicker than this reads as fully enclosed, which is why measuring the
    ///     inside of a closed shape wants it at or above one.
    /// </remarks>
    public float OcclusionRadius { get; init; } = 0.5f;
}

/// <summary>A normal map and a displacement map, as pixels, with no file anywhere in sight.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A bake returns pixels and writing them is the caller's job.</b> <c>Core/</c> is
///         under the virtual-path rule — no <c>System.IO.Path</c>, no <c>File</c> — so an asset
///         compiler, a CLI and an editor each write these where their own conventions say, and none
///         of that reaches this assembly.
///     </para>
///     <para>
///         ⚠ <b><see cref="Displacement" /> is in the model's own units and is deliberately not
///         normalised.</b> A displacement map quantized into <c>[0, 1]</c> needs a scale stored
///         beside it or it means nothing, and half of the ways that goes wrong are the scale and the
///         pixels being written by different code. <see cref="DisplacementRange" /> is what a caller
///         quantizes with, and it is measured rather than assumed.
///     </para>
/// </remarks>
public sealed record BakedMaps {
    /// <summary>The edge length, in texels. Both maps are this square.</summary>
    public required int Resolution { get; init; }

    /// <summary>One normal per texel, row-major from the bottom-left, in <see cref="Space" />.</summary>
    public required IReadOnlyList<Vector3> Normals { get; init; }

    /// <summary>One signed distance per texel, in the model's own units.</summary>
    /// <remarks>Positive is outward along the output's normal — the source stands proud of the cage.</remarks>
    public required IReadOnlyList<float> Displacement { get; init; }

    /// <summary>Whether a texel is chart content rather than gutter or background.</summary>
    /// <remarks>
    ///     ⚠ <b>The dilation reads this and never writes it</b>, which is what stops one chart's
    ///     gutter from overwriting the chart next to it in the atlas.
    /// </remarks>
    public required IReadOnlyList<bool> Coverage { get; init; }

    /// <summary>Which frame <see cref="Normals" /> is in.</summary>
    public required BakeSpace Space { get; init; }

    /// <summary>How many texels the charts covered.</summary>
    public required int Covered { get; init; }

    /// <summary>How many the gutter filled afterwards.</summary>
    public required int Dilated { get; init; }

    /// <summary>How many covered texels found no source at all within the search radius.</summary>
    /// <remarks>
    ///     ⚠ Worth reading. A handful is a thin feature the output cut through; a large fraction
    ///     means <see cref="BakeSettings.SearchRadius" /> is smaller than the deviation the remesh
    ///     actually produced, and the map is mostly the fallback rather than the bake.
    /// </remarks>
    public required int Missed { get; init; }

    /// <summary>The largest absolute displacement, which is what a caller quantizes with.</summary>
    public required float DisplacementRange { get; init; }

    /// <summary>The unoccluded fraction of the hemisphere per texel, or null if it was not asked for.</summary>
    /// <remarks>
    ///     One is open sky and zero is sealed. ⚠ <b>Measured at the <i>source</i>'s surface point and
    ///     about the <i>source</i>'s normal</b>, not at the cage's — the whole reason to bake a mesh
    ///     map is that the cage does not have the geometry that does the occluding.
    /// </remarks>
    public IReadOnlyList<float>? AmbientOcclusion { get; init; }

    /// <summary>The average unoccluded direction per texel, or null if it was not asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>In <see cref="Space" />, the same frame <see cref="Normals" /> is in</b>, so a
    ///     tangent-space bake's bent normal is comparable with its normal map texel for texel. It
    ///     comes off the same rays as <see cref="AmbientOcclusion" /> and costs nothing beside it;
    ///     where every ray was blocked it falls back to the surface normal, because a zero vector is
    ///     not a direction.
    /// </remarks>
    public IReadOnlyList<Vector3>? BentNormal { get; init; }

    /// <summary>Mean curvature per texel, in reciprocal model units, or null if it was not asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>A sphere of radius <i>r</i> reads <c>1/r</c>, so this is a length⁻¹ and it moves with
    ///     the model's scale.</b> Positive is convex along the source's own normal — an edge —
    ///     and negative is a crease. <see cref="CurvatureRange" /> is what a caller quantizes with,
    ///     exactly as <see cref="DisplacementRange" /> is. ⚠ An open rim reads zero rather than a
    ///     number: the operator needs a closed one-ring and the missing half of one is not a
    ///     measurement.
    /// </remarks>
    public IReadOnlyList<float>? Curvature { get; init; }

    /// <summary>How enclosed the inside is per texel, or null if it was not asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction and not a distance.</b> It is the occluded fraction of the same hemisphere
    ///     turned through the surface — zero on a single sheet with nothing behind it, one inside a
    ///     closed shape — and it saturates at <see cref="BakeSettings.OcclusionRadius" />, so a
    ///     distance read off it would be a distance clamped to a setting.
    /// </remarks>
    public IReadOnlyList<float>? Thickness { get; init; }

    /// <summary>The surface point per texel, normalised into the source's box, or null.</summary>
    /// <remarks>
    ///     Each axis runs <c>[0, 1]</c> across the source's bounding box. ⚠ An axis with no extent —
    ///     the third one of a flat source — reads zero, because every point on it <i>is</i> the
    ///     minimum and there is nothing to normalise.
    /// </remarks>
    public IReadOnlyList<Vector3>? Position { get; init; }

    /// <summary>The source's normal per texel, in the source's own space, or null.</summary>
    /// <remarks>
    ///     Unrotated and independent of <see cref="Space" />, which is the point: it is the same map
    ///     whether the normal map beside it came back in tangent space or object space.
    /// </remarks>
    public IReadOnlyList<Vector3>? WorldNormal { get; init; }

    /// <summary>The source's material or island index per texel, <c>-1</c> where there is none, or null.</summary>
    /// <remarks>
    ///     ⚠ <b>The face group only where somebody assigned it, and the connected shell otherwise.</b>
    ///     A group id off <c>EditMesh.Regroup</c> is a coplanarity guess, which on a generated or
    ///     sculpted surface is one group per triangle — baked as ids that is confetti, and it is what
    ///     <see cref="Warnings" /> says when a bake had to fall back. See <c>MapBaker.Labels</c>.
    ///     <br />
    ///     ⚠ <b>Nearest, everywhere, including through the gutter.</b> An id is a label and not a
    ///     quantity: dilation copies a neighbour's id rather than averaging four of them, because the
    ///     average of ids 0 and 2 is id 1, which is a material that does not exist — and every
    ///     generator keyed off the id map then grows a hairline of it along every chart border. The
    ///     channel is an <c>int</c> rather than a colour for the same reason; <see cref="MapBaker.IdColour" />
    ///     turns one into a colour at the point the pixels are written, where no filter can reach it.
    /// </remarks>
    public IReadOnlyList<int>? Ids { get; init; }

    /// <summary>The largest absolute curvature, which is what a caller quantizes with.</summary>
    public float CurvatureRange { get; init; }

    /// <summary>What could not be baked.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>docs/plan/41 § D12's bake: the output's normal, cast at the source, into the atlas.</summary>
/// <remarks>
///     <para>
///         <b>This is where the pipeline's arithmetic closes.</b> § D12: a four-million-triangle
///         generated blob is not expensive because it is four million triangles, it is expensive
///         because it is four million triangles <i>of noise with no UVs</i>. Five thousand quads plus
///         a 2K normal map is smaller, looks better under a moving light, subdivides and can be
///         rigged. Retopology without baking is a downgrade; retopology with baking is the pipeline.
///     </para>
///     <para>
///         ⚠ <b>The ray is cast both ways and the nearer hit wins.</b> Casting only outward loses
///         every part of the source the output enclosed, which on a smoothed remesh of a noisy
///         surface is about half of it; casting only inward loses the other half. A cage mesh is the
///         production answer to the ambiguity and it is a thing an artist authors — the nearer of two
///         opposed hits is the answer available to a content build with nobody watching.
///     </para>
///     <para>
///         ⚠ <b>A texel that finds nothing falls back to the closest point rather than to a
///         default.</b> A default normal in the middle of a chart is a flat patch that reads as a
///         modelling error; the closest point is at worst the right answer measured from slightly the
///         wrong place, and <see cref="BakedMaps.Missed" /> says how often it happened.
///     </para>
///     <para>
///         ⚠ <b>Content is rasterized before anything is dilated, in two full passes.</b> Two charts
///         whose texels abut in the atlas is the common case, not the exotic one — the packer's whole
///         job is to make it common — and a dilation interleaved with the rasterization would let
///         whichever chart was drawn first bleed its gutter over the second chart's content. The
///         gutter only ever writes where <see cref="BakedMaps.Coverage" /> is false.
///     </para>
/// </remarks>
public static class MapBaker {
    /// <summary>How far off the surface an occlusion ray starts, as a fraction of the diagonal.</summary>
    /// <remarks>
    ///     ⚠ <b>Relative, and the one number in this file that has to be.</b> A ray leaving a point
    ///     that lies exactly on a triangle strikes it at zero distance about half the time, so the
    ///     origin is nudged along the normal first — and an absolute nudge is the same claim about
    ///     the model's size that <see cref="BakeSettings.SearchRadius" /> exists to refuse. Ten
    ///     thousandths of the diagonal is far enough clear of the surface's own floating-point
    ///     thickness and far short of any feature an occlusion map resolves.
    /// </remarks>
    const float SelfHitBias = 1e-4f;

    /// <summary>Bakes a normal and a displacement map from the source onto the output's atlas.</summary>
    /// <param name="source">The high-resolution surface. Read, never modified.</param>
    /// <param name="target">The remeshed output. Must carry texture coordinates.</param>
    /// <param name="settings">The size, the gutter and the search radius.</param>
    /// <returns>The pixels, and what was measured about them.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not positive, or the gutter is negative.</exception>
    /// <exception cref="ArgumentException">The target has no texture-coordinate layer to bake into.</exception>
    public static BakedMaps Bake(EditMesh source, EditMesh target, BakeSettings settings) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Resolution);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.Gutter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.OcclusionSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(settings.OcclusionRadius);

        if (target.TexCoords.Length != target.CornerCount) {
            throw new ArgumentException(
                "The target has no texture-coordinate layer, so there is no atlas to bake into.",
                nameof(target)
            );
        }

        var resolution = settings.Resolution;
        var buffers = new BakeBuffers(settings);
        var warnings = new List<string>();

        var surface = SourceSurface.From(source);

        if (surface.TriangleCount == 0) {
            warnings.Add("The source triangulated to nothing, so there was nothing to bake.");

            return Assemble(settings, buffers, 0, warnings);
        }

        if (buffers.Ids is not null) {
            buffers.Labels = Labels(source, warnings);
        }

        var radius = surface.Diagonal * MathF.Max(settings.SearchRadius, 0f);

        if (radius <= 0f) {
            warnings.Add("The source has no extent, so the search radius is zero and every ray misses.");
        }

        var shading = target.Normals.Length == target.CornerCount ? target.Normals.ToArray() : Geometric(target);

        // ⚠ Once for the whole source, not once per texel. The Laplacian is a property of the source
        // mesh and a million texels asking it the same question is the shape of bug § D12's "the
        // expensive half is already built" is about — the tree is built once for the same reason.
        var curvature = settings.Maps.HasFlag(MeshMaps.Curvature) ? MeanCurvature.Build(surface) : null;

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);

            for (var corner = 1; corner + 1 < loop.Length; corner++) {
                Span<int> slots = [entry.Start, entry.Start + corner, entry.Start + corner + 1];

                var uv = new Vector2[3];
                var points = new Vector3[3];
                var shaded = new Vector3[3];

                for (var at = 0; at < 3; at++) {
                    uv[at] = target.TexCoords[slots[at]];
                    points[at] = target.Positions[loop[slots[at] - entry.Start]];
                    shaded[at] = shading[slots[at]];
                }

                Rasterize(surface, settings, uv, points, shaded, radius, curvature, buffers);
            }
        }

        var dilated = Dilate(settings, buffers);

        if (buffers.Covered == 0) {
            warnings.Add("No chart covered any texel — the target's coordinates are outside the unit square.");
        }

        return Assemble(settings, buffers, dilated, warnings);
    }

    /// <summary>Which id each source face bakes as: the group somebody assigned, or its shell.</summary>
    /// <param name="source">The mesh being baked from.</param>
    /// <param name="warnings">What the bake could not do, appended to when the groups are a guess.</param>
    /// <returns>An id per face.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A face group is a material boundary only where somebody assigned it, and reading it
    ///         as one otherwise is the § D12 defect at its loudest.</b> <c>EditMesh.FromTriangles</c>
    ///         ends in <c>Regroup</c>, whose groups are coplanar connected components — on a generated
    ///         or sculpted blob almost no two adjacent triangles are within half a degree, so every
    ///         triangle is its own group and 25 439 of them measured 13 965. Baked straight, an id map
    ///         of that is per-triangle confetti that <see cref="IdColour" /> paints in as many hues,
    ///         and nothing about it fails: it looks like an id map. <c>FeatureDetector</c>,
    ///         <c>Charter</c> and <c>SeamGraph</c> all gate on <see cref="MeshGroupSource" /> already;
    ///         this was the one consumer that did not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Shells rather than a refusal, because § D12 asks for "the source's material
    ///         <i>or island</i> index" and the island is the half that survives a guess.</b> A
    ///         connected component is a fact about the mesh whatever its groups mean — two props in
    ///         one file are two ids, and one closed blob is one id, which is the honest answer for a
    ///         surface with no material boundaries on it rather than a hole in the output. A caller
    ///         that knows the real assignment sets <c>EditMesh.GroupSource</c> and gets its own ids.
    ///     </para>
    /// </remarks>
    static int[] Labels(EditMesh source, List<string> warnings) {
        var labels = new int[source.FaceCount];

        if (source.GroupSource is MeshGroupSource.Assigned) {
            for (var face = 0; face < labels.Length; face++) {
                labels[face] = source.Faces[face].Group;
            }

            return labels;
        }

        List<int> shells = [];
        var count = MeshCollision.Shells(source, shells);

        shells.CopyTo(labels);

        warnings.Add(
            $"The source's face groups came from EditMesh.Regroup's coplanarity guess rather than from "
            + $"an assignment, so they are not material boundaries — on a faceted surface they are one "
            + $"group per triangle. The id map holds the {count} connected shell(s) instead. Set "
            + "EditMesh.GroupSource to Assigned on a mesh whose groups are materials somebody chose."
        );

        return labels;
    }

    /// <summary>A distinct colour for an id, for the caller that has to write pixels rather than ints.</summary>
    /// <param name="id">A face group, or <c>-1</c> for a texel with no source.</param>
    /// <returns>A colour in <c>[0, 1]³</c>, black for <c>-1</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>Distinct rather than pretty, and a pure function of the id.</b> Hues are spaced by
    ///     the golden ratio, which is what keeps the first dozen ids far apart on the wheel instead
    ///     of the first three being three shades of red; the saturation and value are fixed so that
    ///     two ids never differ only in brightness, which is the difference a mask threshold cannot
    ///     see. ⚠ <b>Nothing interpolates this.</b> It is applied to a texel's id after the bake,
    ///     never to a blend of two of them — the map that gets filtered is the one that grows a
    ///     fourth material along every border.
    /// </remarks>
    public static Vector3 IdColour(int id) {
        if (id < 0) {
            return Vector3.Zero;
        }

        var hue = ((id * 0.6180339887f) % 1f) * 6f;
        var sector = (int) hue;
        var fraction = hue - sector;

        const float value = 0.95f;
        const float low = value * (1f - 0.65f);

        var rising = low + ((value - low) * fraction);
        var falling = value - ((value - low) * fraction);

        return sector switch {
            0 => new(value, rising, low),
            1 => new(falling, value, low),
            2 => new(low, value, rising),
            3 => new(low, falling, value),
            4 => new(rising, low, value),
            _ => new(value, low, falling)
        };
    }

    /// <summary>Rasterizes one chart triangle conservatively and bakes every texel it touches.</summary>
    static void Rasterize(
        SourceSurface surface,
        BakeSettings settings,
        Vector2[] uv,
        Vector3[] points,
        Vector3[] shaded,
        float radius,
        float[]? curvature,
        BakeBuffers buffers
    ) {
        var resolution = settings.Resolution;

        var minimum = Vector2.Min(uv[0], Vector2.Min(uv[1], uv[2])) * resolution;
        var maximum = Vector2.Max(uv[0], Vector2.Max(uv[1], uv[2])) * resolution;

        // ⚠ Floor and ceiling rather than a round, and then one texel of slack on each side. A
        // triangle that ends at x = 4.0 exactly still touches the *edge* of texel 3, and conservative
        // coverage counts a shared edge — the separating-axis test is what decides, so the bounds
        // only have to be generous enough not to exclude a candidate it would have accepted.
        var x0 = Math.Clamp((int) MathF.Floor(minimum.X) - 1, 0, resolution - 1);
        var y0 = Math.Clamp((int) MathF.Floor(minimum.Y) - 1, 0, resolution - 1);
        var x1 = Math.Clamp((int) MathF.Ceiling(maximum.X) + 1, 0, resolution - 1);
        var y1 = Math.Clamp((int) MathF.Ceiling(maximum.Y) + 1, 0, resolution - 1);

        if (maximum.X < 0f || maximum.Y < 0f || minimum.X > resolution || minimum.Y > resolution) {
            return;
        }

        var scaled = new Vector2[3];

        for (var at = 0; at < 3; at++) {
            scaled[at] = uv[at] * resolution;
        }

        var frame = Frame(uv, points);

        for (var y = y0; y <= y1; y++) {
            for (var x = x0; x <= x1; x++) {
                var index = (y * resolution) + x;

                // ⚠ First chart to claim a texel keeps it, in face order. Two charts overlapping in
                // the atlas is a packing defect rather than a bake one, and a bake that blended them
                // would hide it; a bake that let the later one win would make the answer depend on
                // face order, which § D14 forbids just as firmly.
                if (buffers.Coverage[index]) {
                    continue;
                }

                Vector2 low = new(x, y);
                Vector2 high = new(x + 1, y + 1);

                if (!AtlasRaster.Overlaps(scaled[0], scaled[1], scaled[2], low, high)) {
                    continue;
                }

                var weights = AtlasRaster.Barycentric(low + new Vector2(0.5f, 0.5f), scaled[0], scaled[1], scaled[2]);

                var point = (points[0] * weights.X) + (points[1] * weights.Y) + (points[2] * weights.Z);
                var along = ScaleSafe.Unit((shaded[0] * weights.X) + (shaded[1] * weights.Y) + (shaded[2] * weights.Z));

                if (along.LengthSquared() <= 0f) {
                    along = frame.Normal;
                }

                buffers.Coverage[index] = true;
                buffers.Covered++;

                var sample = Probe(surface, point, along, radius);

                if (!sample.Struck) {
                    buffers.Missed++;
                }

                buffers.Normals[index] = settings.Space == BakeSpace.Object
                    ? sample.Normal
                    : ToTangent(sample.Normal, along, frame);

                buffers.Displacement[index] = sample.Distance;
                buffers.DisplacementRange = MathF.Max(buffers.DisplacementRange, MathF.Abs(sample.Distance));

                if (settings.Maps != MeshMaps.None) {
                    Measure(surface, settings, curvature, buffers, index, sample, along, frame);
                }
            }
        }
    }

    /// <summary>The six further measurements at a texel whose source point the probe already found.</summary>
    /// <remarks>
    ///     ⚠ <b>Every one of them is made at the <i>source</i>'s point and about the source's
    ///     normal, never the cage's.</b> The cage is a few thousand quads that deliberately do not
    ///     have the geometry doing the occluding, and an occlusion measured on it is a picture of the
    ///     cage — which is the failure mode that makes a baked AO map look like a smoothed version of
    ///     the thing it was supposed to capture.
    /// </remarks>
    static void Measure(
        SourceSurface surface,
        BakeSettings settings,
        float[]? curvature,
        BakeBuffers buffers,
        int index,
        SourceSample sample,
        Vector3 along,
        (Vector3 Tangent, Vector3 Bitangent, Vector3 Normal) frame
    ) {
        if (buffers.Position is { } position) {
            position[index] = Normalised(surface.Bounds, sample.Point);
        }

        if (buffers.WorldNormal is { } world) {
            world[index] = sample.Normal;
        }

        if (buffers.Ids is { } ids && buffers.Labels is { } labels && sample.Triangle >= 0) {
            ids[index] = labels[surface.FaceOf(sample.Triangle)];
        }

        if (curvature is not null && buffers.Curvature is { } curve && sample.Triangle >= 0) {
            var slots = surface.PositionsOf(sample.Triangle);

            var value = (curvature[slots[0]] * sample.Barycentric.X)
                + (curvature[slots[1]] * sample.Barycentric.Y)
                + (curvature[slots[2]] * sample.Barycentric.Z);

            curve[index] = value;
            buffers.CurvatureRange = MathF.Max(buffers.CurvatureRange, MathF.Abs(value));
        }

        if (!BakeBuffers.NeedsRays(settings.Maps)) {
            return;
        }

        Occlude(
            surface,
            settings,
            index,
            sample.Point,
            sample.Normal,
            out var open,
            out var unoccluded,
            out var thickness
        );

        if (buffers.Occlusion is { } occlusion) {
            occlusion[index] = open;
        }

        if (buffers.Bent is { } average) {
            average[index] = settings.Space == BakeSpace.Object
                ? unoccluded
                : ToTangent(unoccluded, along, frame);
        }

        if (buffers.Thickness is { } inside) {
            inside[index] = thickness;
        }
    }

    /// <summary>One hemisphere of rays, answering occlusion, the bent normal and thickness together.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One loop, three accumulators, and the second cast is the first one mirrored.</b>
    ///         § D12 says the bent normal is "the average unoccluded direction from the same rays —
    ///         one accumulator, no second pass", and thickness is "the same hemisphere, inverted":
    ///         casting a second, independently generated set for either would cost twice as much and
    ///         answer about a different set of directions, so a bent normal would not agree with the
    ///         occlusion beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The occlusion is the plain mean of the samples and carries no <c>cos θ</c>
    ///         weight</b>, because <see cref="HemisphereSampler" /> draws the directions <i>from</i>
    ///         the cosine density. Weighting them again would compute the cosine-squared integral,
    ///         which is a darker map that still looks plausible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ray starts a bias off the surface, along the normal it was built about.</b>
    ///         A ray leaving a point that lies exactly on the triangle it came from strikes that
    ///         triangle at zero distance about as often as not, and the map is then uniformly black
    ///         for reasons nothing in it shows. The bias is a fraction of the diagonal for the same
    ///         reason every other tolerance here is.
    ///     </para>
    /// </remarks>
    static void Occlude(
        SourceSurface surface,
        BakeSettings settings,
        int texel,
        Vector3 point,
        Vector3 normal,
        out float open,
        out Vector3 unoccluded,
        out float thickness
    ) {
        var count = settings.OcclusionSamples;
        var reach = surface.Diagonal * MathF.Max(settings.OcclusionRadius, 0f);
        var bias = surface.Diagonal * SelfHitBias;
        var (tangent, bitangent) = HemisphereSampler.Basis(normal);
        var turn = HemisphereSampler.Turn(texel);
        var wanted = settings.Maps.HasFlag(MeshMaps.Thickness);

        var clear = 0;
        var enclosed = 0;
        var sum = Vector3.Zero;

        for (var sample = 0; sample < count; sample++) {
            var local = HemisphereSampler.Local(sample, count, turn);
            var direction = (tangent * local.X) + (bitangent * local.Y) + (normal * local.Z);

            if (reach <= 0f || surface.Tree.Raycast(point + (normal * bias), direction * reach).Triangle < 0) {
                clear++;
                sum += direction;
            }

            if (!wanted) {
                continue;
            }

            // The same direction reflected through the tangent plane, which is what makes this the
            // same hemisphere seen from the other side rather than a second set of samples.
            var inward = direction - (normal * (2f * local.Z));

            if (reach > 0f && surface.Tree.Raycast(point - (normal * bias), inward * reach).Triangle >= 0) {
                enclosed++;
            }
        }

        open = clear / (float) count;
        thickness = enclosed / (float) count;

        var unit = ScaleSafe.Unit(sum);

        // Every ray blocked leaves nothing to average, and a zero vector is not a direction. The
        // surface normal is the honest answer for a texel that can see nothing at all.
        unoccluded = unit.LengthSquared() > 0f ? unit : normal;
    }

    /// <summary>A point as a fraction of the source's bounding box, on each axis.</summary>
    static Vector3 Normalised(BoundingBox bounds, Vector3 point) {
        var size = bounds.Maximum - bounds.Minimum;
        var offset = point - bounds.Minimum;

        return new(
            size.X > 0f ? offset.X / size.X : 0f,
            size.Y > 0f ? offset.Y / size.Y : 0f,
            size.Z > 0f ? offset.Z / size.Z : 0f
        );
    }

    /// <summary>Casts along the normal both ways and takes the nearer source surface.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction handed to the tree has the search radius as its <i>length</i></b>, so
    ///     the hit comes back as a fraction in <c>(0, 1]</c> and there is no second limit that could
    ///     disagree with it. That is also why the radius is relative: it is a fraction of the
    ///     source's diagonal, computed once, rather than a distance somebody has to keep in step with
    ///     the model's units.
    /// </remarks>
    static SourceSample Probe(SourceSurface surface, Vector3 point, Vector3 along, float radius) {
        if (radius > 0f) {
            var outward = surface.Tree.Raycast(point, along * radius);
            var inward = surface.Tree.Raycast(point, -along * radius);

            var hit = outward.Triangle >= 0 && (inward.Triangle < 0 || outward.Distance <= inward.Distance)
                ? (Hit: outward, Sign: 1f)
                : (Hit: inward, Sign: -1f);

            if (hit.Hit.Triangle >= 0) {
                return new(
                    true,
                    hit.Hit.Triangle,
                    hit.Hit.Barycentric,
                    hit.Hit.Point,
                    surface.NormalAt(hit.Hit.Triangle, hit.Hit.Barycentric),
                    hit.Hit.Distance * radius * hit.Sign
                );
            }
        }

        // Nothing along the normal. The closest point is the honest fallback: at worst it is the
        // right source measured from slightly the wrong place, and a default normal here would be a
        // flat patch in the middle of a chart that reads as a modelling error.
        var closest = surface.Tree.Closest(point);

        if (closest.Triangle < 0) {
            // ⚠ The cage's own point and normal, because there is no source to speak of — an empty
            // tree, or a radius of zero on a source with no extent. A position map full of zeroes
            // would be a claim about where the surface is; the cage is at least where the texel is.
            return new(false, -1, Vector3.Zero, point, along, 0f);
        }

        var offset = closest.Point - point;

        return new(
            false,
            closest.Triangle,
            closest.Barycentric,
            closest.Point,
            surface.NormalAt(closest.Triangle, closest.Barycentric),
            MathF.Sqrt(closest.DistanceSquared) * (Vector3.Dot(offset, along) < 0f ? -1f : 1f)
        );
    }

    /// <summary>The tangent frame a chart triangle implies, from its texture-coordinate gradient.</summary>
    /// <remarks>
    ///     <para>
    ///         Lengyel's construction: the two texture-space edge vectors are inverted against the
    ///         two world edge vectors, which gives the directions <c>u</c> and <c>v</c> increase in.
    ///         Gram–Schmidt against the interpolated normal makes it orthonormal, and the sign of the
    ///         bitangent against the cross product carries the handedness — a mirrored chart has the
    ///         other one, and getting it wrong inverts every mirrored surface in the bake.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The determinant is an <i>area</i> in texture space, so the guard on it has to be
    ///         relative.</b> It is <c>du₁ × du₂</c>, which carries the coordinates' units squared;
    ///         an absolute epsilon on it is a claim about how large a chart is, and a chart occupying
    ///         a hundredth of a 4K atlas has determinants around <c>1e-8</c>. Compared against the
    ///         product of the two edges' own lengths instead, which makes the ratio a squared sine
    ///         and the comparison dimensionless — the same shape as
    ///         <c>TriangleTree</c>'s Möller–Trumbore guard.
    ///     </para>
    /// </remarks>
    static (Vector3 Tangent, Vector3 Bitangent, Vector3 Normal) Frame(Vector2[] uv, Vector3[] points) {
        var e1 = points[1] - points[0];
        var e2 = points[2] - points[0];
        var normal = ScaleSafe.Unit(Vector3.Cross(ScaleSafe.Unit(e1), ScaleSafe.Unit(e2)));

        var d1 = uv[1] - uv[0];
        var d2 = uv[2] - uv[0];
        var determinant = (d1.X * d2.Y) - (d2.X * d1.Y);
        var span = d1.LengthSquared() * d2.LengthSquared();

        if (determinant * determinant <= MathUtil.ZeroTolerance * MathUtil.ZeroTolerance * span) {
            return (Fallback(normal), Vector3.Cross(normal, Fallback(normal)), normal);
        }

        var inverse = 1f / determinant;
        var tangent = ((e1 * d2.Y) - (e2 * d1.Y)) * inverse;
        var bitangent = ((e2 * d1.X) - (e1 * d2.X)) * inverse;

        var straight = ScaleSafe.Unit(tangent - (normal * Vector3.Dot(normal, tangent)));

        if (straight.LengthSquared() <= 0f) {
            straight = Fallback(normal);
        }

        var crossed = Vector3.Cross(normal, straight);

        return (straight, Vector3.Dot(crossed, bitangent) < 0f ? -crossed : crossed, normal);
    }

    /// <summary>Any unit vector perpendicular to another, for a chart whose gradient said nothing.</summary>
    static Vector3 Fallback(Vector3 normal) {
        var axis = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var perpendicular = ScaleSafe.Unit(Vector3.Cross(normal, axis));

        return perpendicular.LengthSquared() > 0f ? perpendicular : Vector3.UnitX;
    }

    /// <summary>A source normal expressed in the output's tangent frame at this texel.</summary>
    /// <remarks>
    ///     The frame is rebuilt against the <i>interpolated</i> normal rather than the triangle's
    ///     flat one, so a smooth surface's baked map is smooth across a triangle boundary instead of
    ///     stepping at every edge of the cage.
    /// </remarks>
    static Vector3 ToTangent(Vector3 normal, Vector3 along, (Vector3 Tangent, Vector3 Bitangent, Vector3 Normal) frame) {
        var tangent = ScaleSafe.Unit(frame.Tangent - (along * Vector3.Dot(along, frame.Tangent)));

        if (tangent.LengthSquared() <= 0f) {
            tangent = Fallback(along);
        }

        var bitangent = Vector3.Cross(along, tangent);

        if (Vector3.Dot(bitangent, frame.Bitangent) < 0f) {
            bitangent = -bitangent;
        }

        return new(Vector3.Dot(normal, tangent), Vector3.Dot(normal, bitangent), Vector3.Dot(normal, along));
    }

    /// <summary>Grows the charts outward into the background, never over another chart's content.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>ImpostorBake</c> solves the same problem as a compute shader and the idea
    ///         transfers even though the code does not</b> — it is one layer above this assembly and
    ///         it needs a device. Rounds of four-neighbour flood, each round reading what the last one
    ///         wrote, which is a jump flood's cheap cousin and is exact for a distance of
    ///         <see cref="BakeSettings.Gutter" /> texels.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Coverage</c> is read and never written, so a chart's gutter cannot overwrite
    ///         the chart beside it.</b> The filled set is a second array; a texel joins it and never
    ///         joins <c>Coverage</c>, so the next round can spread from it and the content pass's
    ///         answer stays authoritative.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each round commits after it finishes, not as it goes.</b> Writing in place would
    ///         let a texel filled early in the scan seed one later in the same round, so the gutter
    ///         would reach further to the right and upward than to the left and downward — a
    ///         directional bias that shows up as a lopsided halo at low mips.
    ///     </para>
    /// </remarks>
    static int Dilate(BakeSettings settings, BakeBuffers buffers) {
        var resolution = settings.Resolution;
        var filled = (bool[]) buffers.Coverage.Clone();
        var pending = new List<int>();
        var total = 0;

        Span<int> offsets = stackalloc int[4];

        for (var round = 0; round < settings.Gutter; round++) {
            pending.Clear();

            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    var index = (y * resolution) + x;

                    if (filled[index]) {
                        continue;
                    }

                    offsets[0] = x > 0 ? index - 1 : -1;
                    offsets[1] = x + 1 < resolution ? index + 1 : -1;
                    offsets[2] = y > 0 ? index - resolution : -1;
                    offsets[3] = y + 1 < resolution ? index + resolution : -1;

                    var normal = Vector3.Zero;
                    var height = 0f;
                    var open = 0f;
                    var unoccluded = Vector3.Zero;
                    var curve = 0f;
                    var inside = 0f;
                    var position = Vector3.Zero;
                    var world = Vector3.Zero;
                    var found = 0;

                    foreach (var neighbour in offsets) {
                        if (neighbour < 0 || !filled[neighbour]) {
                            continue;
                        }

                        // ⚠ Nearest, and only for this channel. An id is a label: the mean of ids 0
                        // and 2 is id 1, a material that exists nowhere in the source, and every
                        // generator keyed off the map then grows a hairline of it along every chart
                        // border. The first filled neighbour in a fixed order is a real id and is
                        // the same id on every run — which averaging is not, and which "whichever
                        // the scan happened to reach" would not be either.
                        if (found == 0 && buffers.Ids is { } ids) {
                            ids[index] = ids[neighbour];
                        }

                        normal += buffers.Normals[neighbour];
                        height += buffers.Displacement[neighbour];
                        found++;

                        if (buffers.Occlusion is { } occlusion) {
                            open += occlusion[neighbour];
                        }

                        if (buffers.Bent is { } average) {
                            unoccluded += average[neighbour];
                        }

                        if (buffers.Curvature is { } curvature) {
                            curve += curvature[neighbour];
                        }

                        if (buffers.Thickness is { } thickness) {
                            inside += thickness[neighbour];
                        }

                        if (buffers.Position is { } points) {
                            position += points[neighbour];
                        }

                        if (buffers.WorldNormal is { } normals) {
                            world += normals[neighbour];
                        }
                    }

                    if (found == 0) {
                        continue;
                    }

                    // ⚠ The value is written now and the texel is committed at the end of the round.
                    // The scan reads `filled` and never the values, so an early write cannot seed a
                    // later texel in the same round — and it is the flag, not the write, that would
                    // let the gutter reach further right and upward than left and downward.
                    buffers.Normals[index] = Unit(normal / found);
                    buffers.Displacement[index] = height / found;

                    Write(buffers.Occlusion, index, open / found);
                    Write(buffers.Curvature, index, curve / found);
                    Write(buffers.Thickness, index, inside / found);
                    Write(buffers.Position, index, position / found);
                    Write(buffers.Bent, index, Unit(unoccluded / found));
                    Write(buffers.WorldNormal, index, Unit(world / found));

                    pending.Add(index);
                }
            }

            foreach (var index in pending) {
                filled[index] = true;
                total++;
            }
        }

        return total;
    }

    /// <summary>Writes into a channel that may not have been asked for.</summary>
    static void Write<T>(T[]? channel, int index, T value) {
        if (channel is not null) {
            channel[index] = value;
        }
    }

    /// <summary>A direction, or the sum itself when four neighbours cancelled and left nothing.</summary>
    static Vector3 Unit(Vector3 value) {
        var unit = ScaleSafe.Unit(value);

        return unit.LengthSquared() > 0f ? unit : value;
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

    /// <summary>The buffers as a result, including a bake that found nothing to do.</summary>
    static BakedMaps Assemble(BakeSettings settings, BakeBuffers buffers, int dilated, List<string> warnings) =>
        new() {
            Resolution = settings.Resolution,
            Normals = buffers.Normals,
            Displacement = buffers.Displacement,
            Coverage = buffers.Coverage,
            Space = settings.Space,
            Covered = buffers.Covered,
            Dilated = dilated,
            Missed = buffers.Missed,
            DisplacementRange = buffers.DisplacementRange,
            AmbientOcclusion = buffers.Occlusion,
            BentNormal = buffers.Bent,
            Curvature = buffers.Curvature,
            Thickness = buffers.Thickness,
            Position = buffers.Position,
            WorldNormal = buffers.WorldNormal,
            Ids = buffers.Ids,
            CurvatureRange = buffers.CurvatureRange,
            Warnings = warnings
        };

    /// <summary>What one probe found: where on the source, which triangle, and how far away.</summary>
    /// <param name="Struck">Whether a ray hit, rather than the closest-point fallback answering.</param>
    /// <param name="Triangle">The source triangle, or <c>-1</c> when there was no source at all.</param>
    /// <param name="Barycentric">Where on that triangle, as weights summing to one.</param>
    /// <param name="Point">The point on the source, which every further measurement is made at.</param>
    /// <param name="Normal">The source's interpolated normal there.</param>
    /// <param name="Distance">Signed, positive when the source stands proud of the cage.</param>
    readonly record struct SourceSample(
        bool Struck,
        int Triangle,
        Vector3 Barycentric,
        Vector3 Point,
        Vector3 Normal,
        float Distance
    );
}
