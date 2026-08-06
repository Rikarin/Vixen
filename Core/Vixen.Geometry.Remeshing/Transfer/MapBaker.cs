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

        if (target.TexCoords.Length != target.CornerCount) {
            throw new ArgumentException(
                "The target has no texture-coordinate layer, so there is no atlas to bake into.",
                nameof(target)
            );
        }

        var resolution = settings.Resolution;
        var texels = resolution * resolution;
        var normals = new Vector3[texels];
        var displacement = new float[texels];
        var coverage = new bool[texels];
        var warnings = new List<string>();

        var surface = SourceSurface.From(source);

        if (surface.TriangleCount == 0) {
            warnings.Add("The source triangulated to nothing, so there was nothing to bake.");

            return Empty(settings, normals, displacement, coverage, warnings);
        }

        var radius = surface.Diagonal * MathF.Max(settings.SearchRadius, 0f);

        if (radius <= 0f) {
            warnings.Add("The source has no extent, so the search radius is zero and every ray misses.");
        }

        var shading = target.Normals.Length == target.CornerCount ? target.Normals.ToArray() : Geometric(target);
        var covered = 0;
        var missed = 0;
        var range = 0f;

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

                Rasterize(
                    surface,
                    settings,
                    uv,
                    points,
                    shaded,
                    radius,
                    normals,
                    displacement,
                    coverage,
                    ref covered,
                    ref missed,
                    ref range
                );
            }
        }

        var dilated = Dilate(settings, normals, displacement, coverage);

        if (covered == 0) {
            warnings.Add("No chart covered any texel — the target's coordinates are outside the unit square.");
        }

        return new() {
            Resolution = resolution,
            Normals = normals,
            Displacement = displacement,
            Coverage = coverage,
            Space = settings.Space,
            Covered = covered,
            Dilated = dilated,
            Missed = missed,
            DisplacementRange = range,
            Warnings = warnings
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
        Vector3[] normals,
        float[] displacement,
        bool[] coverage,
        ref int covered,
        ref int missed,
        ref float range
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
                if (coverage[index]) {
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

                coverage[index] = true;
                covered++;

                var found = Probe(surface, point, along, radius, out var struck, out var distance);

                if (!found) {
                    missed++;
                }

                normals[index] = settings.Space == BakeSpace.Object ? struck : ToTangent(struck, along, frame);
                displacement[index] = distance;
                range = MathF.Max(range, MathF.Abs(distance));
            }
        }
    }

    /// <summary>Casts along the normal both ways and takes the nearer source surface.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction handed to the tree has the search radius as its <i>length</i></b>, so
    ///     the hit comes back as a fraction in <c>(0, 1]</c> and there is no second limit that could
    ///     disagree with it. That is also why the radius is relative: it is a fraction of the
    ///     source's diagonal, computed once, rather than a distance somebody has to keep in step with
    ///     the model's units.
    /// </remarks>
    static bool Probe(
        SourceSurface surface,
        Vector3 point,
        Vector3 along,
        float radius,
        out Vector3 normal,
        out float distance
    ) {
        normal = along;
        distance = 0f;

        if (radius > 0f) {
            var outward = surface.Tree.Raycast(point, along * radius);
            var inward = surface.Tree.Raycast(point, -along * radius);

            var hit = outward.Triangle >= 0 && (inward.Triangle < 0 || outward.Distance <= inward.Distance)
                ? (Hit: outward, Sign: 1f)
                : (Hit: inward, Sign: -1f);

            if (hit.Hit.Triangle >= 0) {
                normal = surface.NormalAt(hit.Hit.Triangle, hit.Hit.Barycentric);
                distance = hit.Hit.Distance * radius * hit.Sign;

                return true;
            }
        }

        // Nothing along the normal. The closest point is the honest fallback: at worst it is the
        // right source measured from slightly the wrong place, and a default normal here would be a
        // flat patch in the middle of a chart that reads as a modelling error.
        var closest = surface.Tree.Closest(point);

        if (closest.Triangle < 0) {
            return false;
        }

        normal = surface.NormalAt(closest.Triangle, closest.Barycentric);

        var offset = closest.Point - point;
        distance = MathF.Sqrt(closest.DistanceSquared) * (Vector3.Dot(offset, along) < 0f ? -1f : 1f);

        return false;
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
    static int Dilate(BakeSettings settings, Vector3[] normals, float[] displacement, bool[] coverage) {
        var resolution = settings.Resolution;
        var filled = (bool[]) coverage.Clone();
        var total = 0;

        Span<int> offsets = stackalloc int[4];

        for (var round = 0; round < settings.Gutter; round++) {
            var added = new List<(int Index, Vector3 Normal, float Displacement)>();

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

                    var sum = Vector3.Zero;
                    var height = 0f;
                    var found = 0;

                    foreach (var neighbour in offsets) {
                        if (neighbour < 0 || !filled[neighbour]) {
                            continue;
                        }

                        sum += normals[neighbour];
                        height += displacement[neighbour];
                        found++;
                    }

                    if (found > 0) {
                        added.Add((index, sum / found, height / found));
                    }
                }
            }

            foreach (var (index, normal, height) in added) {
                var unit = ScaleSafe.Unit(normal);

                normals[index] = unit.LengthSquared() > 0f ? unit : normal;
                displacement[index] = height;
                filled[index] = true;
                total++;
            }
        }

        return total;
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

    /// <summary>A bake that found nothing to do.</summary>
    static BakedMaps Empty(
        BakeSettings settings,
        Vector3[] normals,
        float[] displacement,
        bool[] coverage,
        List<string> warnings
    ) =>
        new() {
            Resolution = settings.Resolution,
            Normals = normals,
            Displacement = displacement,
            Coverage = coverage,
            Space = settings.Space,
            Covered = 0,
            Dilated = 0,
            Missed = 0,
            DisplacementRange = 0f,
            Warnings = warnings
        };
}
