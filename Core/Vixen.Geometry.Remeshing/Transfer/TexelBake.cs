// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>The trees a bake's rays go through: the whole source, and one per matched part.</summary>
/// <remarks>
///     ⚠ <b>A tree per part rather than a filter inside one tree.</b> A filter in a traversal keeps
///     walking boxes full of triangles it will reject, and a ray from a bolt should not pay for the
///     body it lies on.
/// </remarks>
sealed class BakeRays {
    readonly Dictionary<int, BakeTree> parts;

    /// <summary>The whole source, which occlusion always sees.</summary>
    public BakeTree Whole { get; }

    BakeRays(BakeTree whole, Dictionary<int, BakeTree> parts) {
        Whole = whole;
        this.parts = parts;
    }

    /// <summary>What a triangle's rays may strike.</summary>
    /// <param name="part">The triangle's part.</param>
    /// <returns>The tree.</returns>
    public BakeTree For(int part) => parts.GetValueOrDefault(part, Whole);

    /// <summary>Builds the trees.</summary>
    /// <param name="surface">The source.</param>
    /// <param name="source">The source mesh, for its face count.</param>
    /// <param name="target">The target mesh, for its face count.</param>
    /// <param name="match">Which part each face is, or null.</param>
    /// <param name="warnings">What the bake could not do.</param>
    /// <returns>The trees.</returns>
    public static BakeRays Build(
        SourceSurface surface,
        EditMesh source,
        EditMesh target,
        BakeParts? match,
        List<string> warnings
    ) {
        Dictionary<int, BakeTree> trees = [];

        if (match is null) {
            return new(surface.RayTree(), trees);
        }

        Dictionary<int, List<int>> members = [];

        for (var triangle = 0; triangle < surface.TriangleCount; triangle++) {
            var part = match.Source[surface.FaceOf(triangle)];

            if (part < 0) {
                continue;
            }

            if (!members.TryGetValue(part, out var list)) {
                list = [];
                members.Add(part, list);
            }

            list.Add(triangle);
        }

        foreach (var (part, list) in members) {
            trees.Add(part, surface.RayTree(list));
        }

        var unmatched = 0;

        for (var face = 0; face < target.FaceCount; face++) {
            unmatched += match.Target[face] < 0 || !trees.ContainsKey(match.Target[face]) ? 1 : 0;
        }

        if (unmatched > 0) {
            warnings.Add(
                $"{unmatched} of the target's {target.FaceCount} faces matched no part of the source, "
                + "so their rays may strike all of it."
            );
        }

        return new(surface.RayTree(), trees);
    }
}

/// <summary>What one texel's bake does, which is all the work that runs on many threads.</summary>
/// <param name="surface">The source.</param>
/// <param name="rays">The trees.</param>
/// <param name="settings">The bake.</param>
/// <param name="radius">How far a ray looks, as a length.</param>
/// <param name="curvature">The source's curvature per position, or null.</param>
/// <param name="buffers">The atlas.</param>
/// <remarks>
///     ⚠ <b>Every value here is a function of the texel.</b> Nothing carries from one texel to the next
///     and every shared thing is read-only, so a row can run on any thread at any time. A row writes
///     only its own texels, and its totals go through <see cref="BakeBuffers.Merge" />.
/// </remarks>
sealed class TexelBake(
    SourceSurface surface,
    BakeRays rays,
    BakeSettings settings,
    float radius,
    float[]? curvature,
    BakeBuffers buffers
) {
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

    readonly int side = settings.Supersampling;
    readonly int samples = settings.Supersampling * settings.Supersampling;
    readonly int resolution = settings.Resolution;
    readonly bool wantsRays = BakeBuffers.NeedsRays(settings.Maps);
    readonly bool thicknessWanted = settings.Maps.HasFlag(MeshMaps.Thickness);
    readonly float reach = surface.Diagonal * MathF.Max(settings.OcclusionRadius, 0f);
    readonly float bias = surface.Diagonal * SelfHitBias;

    /// <summary>Bakes every claimed texel in a row of the atlas.</summary>
    /// <param name="row">The row.</param>
    /// <param name="charts">Who owns what.</param>
    public void Row(int row, BakeCharts charts) {
        var missed = 0;
        var displacement = 0f;
        var deepest = 0f;
        var ids = samples > 1 && buffers.Ids is not null ? new int[samples] : null;

        for (var x = 0; x < resolution; x++) {
            var index = (row * resolution) + x;

            if (charts.Owner(index) is not { } triangle) {
                continue;
            }

            Texel(index, x, row, triangle, charts.Other(index), ids, ref missed, ref displacement, ref deepest);
        }

        buffers.Merge(missed, displacement, deepest);
    }

    /// <summary>Everything a texel adds up over its samples.</summary>
    struct Sum {
        public Vector3 Normal;
        public float Height;
        public int Struck;
        public float Curve;
        public Vector3 Position;
        public Vector3 World;
        public int Clear;
        public int Enclosed;
        public int Cast;
        public Vector3 Bent;
    }

    void Texel(
        int index,
        int x,
        int y,
        ChartTriangle triangle,
        ChartTriangle? other,
        int[]? ids,
        ref int missed,
        ref float displacement,
        ref float deepest
    ) {
        var sum = default(Sum);

        for (var column = 0; column < side; column++) {
            for (var line = 0; line < side; line++) {
                var at = (line * side) + column;

                Vector2 place = new(x + ((column + 0.5f) / side), y + ((line + 0.5f) / side));

                var holder = other is not null && !Holds(triangle, place) && Holds(other, place) ? other : triangle;

                Sample(index, at, place, holder, ids, ref sum);
            }
        }

        var share = 1f / samples;

        buffers.Normals[index] = samples == 1 ? sum.Normal : Unit(sum.Normal);
        buffers.Displacement[index] = sum.Height * share;
        displacement = MathF.Max(displacement, MathF.Abs(sum.Height * share));
        missed += sum.Struck == 0 ? 1 : 0;

        if (buffers.Position is { } position) {
            position[index] = sum.Position * share;
        }

        if (buffers.WorldNormal is { } world) {
            world[index] = samples == 1 ? sum.World : Unit(sum.World);
        }

        if (ids is not null) {
            buffers.Ids![index] = Mode(ids);
        }

        if (curvature is not null && buffers.Curvature is { } curve) {
            curve[index] = sum.Curve * share;
            deepest = MathF.Max(deepest, MathF.Abs(sum.Curve * share));
        }

        if (!wantsRays) {
            return;
        }

        var cast = MathF.Max(sum.Cast, 1);

        if (buffers.Occlusion is { } occlusion) {
            occlusion[index] = sum.Clear / cast;
        }

        if (buffers.Bent is { } average) {
            average[index] = samples == 1 ? sum.Bent : Unit(sum.Bent);
        }

        if (buffers.Thickness is { } inside) {
            inside[index] = sum.Enclosed / cast;
        }
    }

    /// <summary>Whether a point of the atlas is on a chart triangle, on either winding.</summary>
    static bool Holds(ChartTriangle triangle, Vector2 place) {
        var corners = triangle.Scaled;
        var one = Side(corners[0], corners[1], place);
        var two = Side(corners[1], corners[2], place);
        var three = Side(corners[2], corners[0], place);

        return (one >= 0f && two >= 0f && three >= 0f) || (one <= 0f && two <= 0f && three <= 0f);
    }

    static float Side(Vector2 from, Vector2 to, Vector2 place) =>
        ((to.X - from.X) * (place.Y - from.Y)) - ((to.Y - from.Y) * (place.X - from.X));

    /// <summary>The commonest id, the earliest sample winning a tie.</summary>
    /// <remarks>⚠ An id is a label, so a texel takes one of its samples' ids and never a blend of them.</remarks>
    static int Mode(int[] ids) {
        var best = ids[0];
        var most = 0;

        foreach (var id in ids) {
            var count = 0;

            foreach (var other in ids) {
                count += other == id ? 1 : 0;
            }

            if (count > most) {
                most = count;
                best = id;
            }
        }

        return best;
    }

    void Sample(int index, int at, Vector2 place, ChartTriangle triangle, int[]? ids, ref Sum sum) {
        var scaled = triangle.Scaled;
        var weights = AtlasRaster.Barycentric(place, scaled[0], scaled[1], scaled[2]);
        var point = Blend(triangle.Points, weights);
        var along = ScaleSafe.Unit(Blend(triangle.Shading, weights));

        if (along.LengthSquared() <= 0f) {
            along = triangle.Normal;
        }

        var basis = Basis.At(triangle, weights, along);

        var sample = Cast(triangle, weights, point, Direction(triangle, weights, along, place), along);

        sum.Struck += sample.Struck ? 1 : 0;
        sum.Normal += settings.Space == BakeSpace.Object
            ? sample.Normal
            : basis.Measure(sample.Normal);
        sum.Height += sample.Distance;

        if (settings.Maps != MeshMaps.None) {
            Measure(index, at, sample, basis, ids, ref sum);
        }
    }

    static Vector3 Blend(Vector3[] corners, Vector3 weights) =>
        (corners[0] * weights.X) + (corners[1] * weights.Y) + (corners[2] * weights.Z);

    /// <summary>Which way a sample's ray runs: the smoothed direction, leaning by the skew.</summary>
    Vector3 Direction(ChartTriangle triangle, Vector3 weights, Vector3 along, Vector2 place) {
        var smooth = ScaleSafe.Unit(Blend(triangle.Rays, weights));

        if (smooth.LengthSquared() <= 0f) {
            smooth = along;
        }

        var lean = settings.Skew * (settings.SkewMap?.At(place / resolution) ?? 1f);

        if (lean <= 0f) {
            return smooth;
        }

        var leaned = ScaleSafe.Unit(Vector3.Lerp(smooth, along, MathF.Min(lean, 1f)));

        return leaned.LengthSquared() > 0f ? leaned : along;
    }

    SourceSample Cast(ChartTriangle triangle, Vector3 weights, Vector3 point, Vector3 direction, Vector3 along) {
        var tree = rays.For(triangle.Part);

        if (triangle.Cage is not { } cage) {
            return Probe(tree, point, direction, radius);
        }

        var outer = Blend(cage, weights);
        var offset = outer - point;
        var gap = offset.Length();

        if (gap <= 0f) {
            return Probe(tree, point, direction, radius);
        }

        // From the cage, towards the target, and on past it. With no skew this is the line the two
        // ends of the pair define, which is what an artist moved the cage to change; a leaned ray
        // leaves from as far out and at its own angle.
        var inward = settings.Skew <= 0f ? offset / -gap : -direction;
        var length = gap + radius;
        var hit = tree.Raycast(point - (inward * gap), inward * length);

        return hit.Triangle >= 0
            ? new(
                true,
                hit.Triangle,
                hit.Barycentric,
                hit.Point,
                surface.NormalAt(hit.Triangle, hit.Barycentric),
                gap - (hit.Distance * length)
            )
            : Nearest(tree, point, along);
    }

    /// <summary>Casts along the direction both ways and takes the nearer source surface.</summary>
    /// <remarks>
    ///     ⚠ <b>The direction handed to the tree has the search radius as its <i>length</i></b>, so
    ///     the hit comes back as a fraction in <c>(0, 1]</c> and there is no second limit that could
    ///     disagree with it. That is also why the radius is relative: it is a fraction of the
    ///     source's diagonal, computed once, rather than a distance somebody has to keep in step with
    ///     the model's units.
    /// </remarks>
    SourceSample Probe(BakeTree tree, Vector3 point, Vector3 along, float reachOut) {
        if (reachOut > 0f) {
            var outward = tree.Raycast(point, along * reachOut);
            var inward = tree.Raycast(point, -along * reachOut);

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
                    hit.Hit.Distance * reachOut * hit.Sign
                );
            }
        }

        return Nearest(tree, point, along);
    }

    /// <summary>The closest point, which is the honest fallback when no ray found anything.</summary>
    /// <remarks>
    ///     At worst it is the right source measured from slightly the wrong place, and a default normal
    ///     here would be a flat patch in the middle of a chart that reads as a modelling error.
    /// </remarks>
    SourceSample Nearest(BakeTree tree, Vector3 point, Vector3 along) {
        var closest = tree.Closest(point);

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

    /// <summary>The further measurements at a sample whose source point the probe already found.</summary>
    /// <remarks>
    ///     ⚠ <b>Every one of them is made at the <i>source</i>'s point and about the source's
    ///     normal, never the cage's.</b> The cage is a few thousand quads that deliberately do not
    ///     have the geometry doing the occluding, and an occlusion measured on it is a picture of the
    ///     cage — which is the failure mode that makes a baked AO map look like a smoothed version of
    ///     the thing it was supposed to capture.
    /// </remarks>
    void Measure(int index, int at, SourceSample sample, in Basis basis, int[]? ids, ref Sum sum) {
        if (buffers.Position is not null) {
            sum.Position += Normalised(surface.Bounds, sample.Point);
        }

        if (buffers.WorldNormal is not null) {
            sum.World += sample.Normal;
        }

        if (buffers.Ids is { } written && buffers.Labels is { } labels) {
            var id = sample.Triangle >= 0 ? labels[surface.FaceOf(sample.Triangle)] : -1;

            if (ids is not null) {
                ids[at] = id;
            } else {
                written[index] = id;
            }
        }

        if (curvature is not null && sample.Triangle >= 0) {
            var slots = surface.PositionsOf(sample.Triangle);

            sum.Curve += (curvature[slots[0]] * sample.Barycentric.X)
                + (curvature[slots[1]] * sample.Barycentric.Y)
                + (curvature[slots[2]] * sample.Barycentric.Z);
        }

        if (!wantsRays) {
            return;
        }

        var unoccluded = Occlude(index, at, sample.Point, sample.Normal, ref sum);

        if (buffers.Bent is not null) {
            sum.Bent += settings.Space == BakeSpace.Object ? unoccluded : basis.Measure(unoccluded);
        }
    }

    /// <summary>A sample's share of one hemisphere, answering occlusion, the bent normal and thickness.</summary>
    /// <returns>The average unoccluded direction it found.</returns>
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
    ///         ⚠ <b>Dealt across the samples.</b> Each of a texel's samples takes every n-th direction
    ///         of the one stratified set, so the directions a texel casts do not depend on how finely it
    ///         was divided, and <see cref="BakeSettings.OcclusionSamples" /> is what it costs.
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
    Vector3 Occlude(int texel, int at, Vector3 point, Vector3 normal, ref Sum sum) {
        var count = settings.OcclusionSamples;
        var whole = rays.Whole;
        var (tangent, bitangent) = HemisphereSampler.Basis(normal);
        var turn = HemisphereSampler.Turn(texel);
        var total = Vector3.Zero;

        for (var sample = at; sample < count; sample += samples) {
            var local = HemisphereSampler.Local(sample, count, turn);
            var direction = (tangent * local.X) + (bitangent * local.Y) + (normal * local.Z);

            sum.Cast++;

            if (reach <= 0f || !whole.Occluded(point + (normal * bias), direction * reach)) {
                sum.Clear++;
                total += direction;
            }

            if (!thicknessWanted) {
                continue;
            }

            // The same direction reflected through the tangent plane, which is what makes this the
            // same hemisphere seen from the other side rather than a second set of samples.
            var inward = direction - (normal * (2f * local.Z));

            if (reach > 0f && whole.Occluded(point - (normal * bias), inward * reach)) {
                sum.Enclosed++;
            }
        }

        var unit = ScaleSafe.Unit(total);

        // Every ray blocked leaves nothing to average, and a zero vector is not a direction. The
        // surface normal is the honest answer for a texel that can see nothing at all.
        return unit.LengthSquared() > 0f ? unit : normal;
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

    /// <summary>Any unit vector perpendicular to another, for a chart whose gradient said nothing.</summary>
    static Vector3 Fallback(Vector3 normal) {
        var axis = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var perpendicular = ScaleSafe.Unit(Vector3.Cross(normal, axis));

        return perpendicular.LengthSquared() > 0f ? perpendicular : Vector3.UnitX;
    }

    /// <summary>A direction, or the sum itself when the samples cancelled and left nothing.</summary>
    static Vector3 Unit(Vector3 value) {
        var unit = ScaleSafe.Unit(value);

        return unit.LengthSquared() > 0f ? unit : value;
    }

    /// <summary>The tangent frame at one point of a triangle, as a renderer's pixel shader builds it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Measure" /> is the exact inverse of what a renderer does, and that is the
    ///         whole contract.</b> A shader interpolates the corners' tangent and normal, leaves them
    ///         unnormalised, makes <c>b = sign × cross(n, t)</c> per pixel, and takes
    ///         <c>normalize(x·t + y·b + z·n)</c>. That frame is not orthonormal, so projecting onto its
    ///         axes is not its inverse; these are the three rows of the true inverse, as the original
    ///         <c>mikktspace.h</c> sets them out. Normalising first gives a map that is right at every
    ///         corner and off by a little across every curved triangle.
    ///     </para>
    ///     <para>
    ///         A triangle with no texture area has no tangent to interpolate. It gets any frame about
    ///         the normal, because it covers no texels a renderer can tell apart.
    ///     </para>
    /// </remarks>
    readonly record struct Basis(Vector3 Row0, Vector3 Row1, Vector3 Row2) {
        public static Basis At(ChartTriangle triangle, Vector3 weights, Vector3 along) {
            var corners = triangle.Tangents;
            var n = Blend(triangle.Shading, weights);
            var w = (corners[0] * weights.X) + (corners[1] * weights.Y) + (corners[2] * weights.Z);
            var t = new Vector3(w.X, w.Y, w.Z);

            if (n.LengthSquared() <= 0f) {
                n = along;
            }

            var b = Vector3.Cross(n, t) * (w.W < 0f ? -1f : 1f);
            var row2 = Vector3.Cross(t, b);

            if (row2.LengthSquared() <= MathUtil.ZeroTolerance * MathUtil.ZeroTolerance) {
                t = Fallback(along);
                b = Vector3.Cross(along, t);

                return new(t, b, along);
            }

            var row0 = Vector3.Cross(b, n);
            var side = Vector3.Dot(t, row0) < 0f ? -1f : 1f;

            return new(row0 * side, Vector3.Cross(n, t) * side, row2 * side);
        }

        /// <summary>A direction on the source, as the numbers that put it back through the frame.</summary>
        public Vector3 Measure(Vector3 direction) =>
            Unit(new(Vector3.Dot(direction, Row0), Vector3.Dot(direction, Row1), Vector3.Dot(direction, Row2)));
    }

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
