// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>What kind of water a body is.</summary>
/// <remarks>
///     <para>
///         Unreal's five actors as one enum
///         ([35 § D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline)).
///         They differ in three things — whether the spline closes, whether the surface has one
///         height or many, and whether the body adds water or removes it — and none of those is worth
///         a type.
///     </para>
///     <para>
///         ⚠ <b><see cref="Island" /> is the same mechanism with the sign flipped</b>, which is
///         Unreal's <c>Invert Shape</c> property promoted to the thing it actually is. An island is
///         not a second kind of actor; it is a body whose coverage subtracts.
///     </para>
/// </remarks>
public enum WaterBodyKind {
    /// <summary>A closed spline marking a shoreline, with water extending past it to the horizon.</summary>
    Ocean,

    /// <summary>A closed spline, one height for every point.</summary>
    Lake,

    /// <summary>An open spline. The height varies along it, and it has a direction.</summary>
    River,

    /// <summary>A closed spline standing for a mesh — a pool, an aqueduct, a ship's hold.</summary>
    /// <remarks>
    ///     It still carries a spline, exactly as Unreal's does, because the query API must not have
    ///     two shapes. What differs is what the renderer draws, which is the mesh.
    /// </remarks>
    Custom,

    /// <summary>A closed spline that removes water and raises the ground.</summary>
    Island
}

/// <summary>What a body is like at one point along its spline.</summary>
/// <remarks>
///     <para>
///         <b>Unreal's four channels, because they are the right four</b>
///         ([35 § D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline))
///         — and the fourth is the one everybody forgets until the river is silent.
///     </para>
///     <para>
///         Authored per control point and interpolated along the curve, so a river narrows into a
///         gorge and widens at its mouth without being two bodies.
///     </para>
/// </remarks>
[DataContract("WaterProfilePoint")]
public readonly record struct WaterProfilePoint {
    /// <summary>How far the channel reaches either side of the curve, in metres.</summary>
    /// <remarks>
    ///     What decides the shape of an open body. A closed one is bounded by its own spline, so this
    ///     only sets how far past the shoreline the surface still exists — which is what an ocean
    ///     needs and a lake normally leaves at zero.
    /// </remarks>
    public float HalfWidth { get; init; }

    /// <summary>How far below the surface the bed sits at the body's middle, in metres.</summary>
    public float Depth { get; init; }

    /// <summary>How fast the water moves downstream here, in metres per second.</summary>
    /// <remarks>
    ///     ⚠ <b>Only an open body flows.</b> A lake with a velocity would be a lake whose whole
    ///     surface drifts in one direction and never arrives anywhere, which is not a thing water
    ///     does; a current in a bay is a river body laid through it.
    /// </remarks>
    public float Velocity { get; init; }

    /// <summary>How loud it is here, 0…1.</summary>
    /// <remarks>
    ///     The channel everybody forgets. It carries no rendering meaning at all and exists so that a
    ///     rapid is louder than the pool below it without an author placing emitters by hand.
    /// </remarks>
    public float AudioIntensity { get; init; }

    /// <summary>A stream: four metres wide, one deep, moving at walking pace.</summary>
    public static WaterProfilePoint Stream =>
        new() { HalfWidth = 2f, Depth = 1f, Velocity = 1.4f, AudioIntensity = 0.5f };

    /// <summary>Still water: no width of its own, two metres deep, going nowhere.</summary>
    public static WaterProfilePoint Still =>
        new() { HalfWidth = 0f, Depth = 2f, Velocity = 0f, AudioIntensity = 0.1f };

    /// <summary>This profile blended toward another.</summary>
    /// <param name="other">The other.</param>
    /// <param name="t">How far, 0…1.</param>
    /// <returns>The blend.</returns>
    public WaterProfilePoint Lerp(in WaterProfilePoint other, float t) {
        var k = WaterMath.Saturate(t);

        return new() {
            HalfWidth = HalfWidth + ((other.HalfWidth - HalfWidth) * k),
            Depth = Depth + ((other.Depth - Depth) * k),
            Velocity = Velocity + ((other.Velocity - Velocity) * k),
            AudioIntensity = AudioIntensity + ((other.AudioIntensity - AudioIntensity) * k)
        };
    }
}

/// <summary>What one body says about one place on the ground.</summary>
/// <param name="Coverage">
///     How much of this place the body claims, 0…1. Falls off across the shoreline band.
/// </param>
/// <param name="SurfaceHeight">Where its surface is, in world units.</param>
/// <param name="Flow">How fast the water moves here, in metres per second, on the ground plane.</param>
/// <param name="BedDepth">
///     How far below the surface the body wants the ground, in metres. Zero at the shoreline.
/// </param>
/// <param name="AudioIntensity">How loud it is here, 0…1.</param>
/// <remarks>
///     ⚠ <b>Depth is not in here, and that is
///     [§ D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)'s
///     rule.</b> How deep the water is at a place is <see cref="SurfaceHeight" /> minus whatever the
///     ground turns out to be, computed where it is used; storing it here would be a third number
///     that can disagree with the two it came from. <see cref="BedDepth" /> is a different quantity —
///     what the body would <em>like</em> the ground to be, which is what carving reads.
/// </remarks>
public readonly record struct WaterBodyContribution(
    float Coverage,
    float SurfaceHeight,
    Vector2 Flow,
    float BedDepth,
    float AudioIntensity
) {
    /// <summary>A body that says nothing about this place.</summary>
    public static WaterBodyContribution None => default;
}

/// <summary>A body of water: a spline, a profile, and a kind.</summary>
/// <remarks>
///     <para>
///         <b>The kernel half of
///         [§ D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline).</b>
///         What a scene carries is a component naming a <c>.vxspline</c> and eleven numbers; what the
///         arithmetic needs is a resolved curve and a profile, which is this. No new asset kind and
///         no new spline type — a water body is authored with the same tools a road is, because it is
///         the same object.
///     </para>
///     <para>
///         <b>The shape is a signed distance to the body's boundary, and every kind produces one.</b>
///         An open body's boundary is its centreline pushed out by the profile's half-width; a closed
///         body's is its own spline. That is what lets one <see cref="Sample" /> serve a river, a lake
///         and an ocean, and it is why a transition between two of them is resolved by rasterising
///         both rather than by a material that has to know which two it is between.
///     </para>
///     <para>
///         ⚠ <b>The polygon is built once, in the constructor.</b> Point-in-polygon over a curve
///         re-evaluated per query is the difference between a body a hundred pontoons can ask and one
///         they cannot — and the evaluator's stated property is that ten thousand queries allocate
///         nothing.
///     </para>
/// </remarks>
public sealed class WaterBody {
    /// <summary>How finely a closed spline is flattened into the polygon queries test against.</summary>
    /// <remarks>
    ///     Per segment, matching <see cref="Spline.SamplesPerSegment" /> — which is what the curve's
    ///     own length table uses, so the flattening and the arc length agree about where the curve is.
    ///     A coarser polygon puts the shoreline inside the drawn surface on the outside of every bend.
    /// </remarks>
    public const int BoundarySamples = Spline.SamplesPerSegment;

    readonly Vector2[] boundary;
    readonly float[] boundaryParameters;
    readonly WaterProfilePoint[] points;

    /// <summary>Creates one.</summary>
    /// <param name="kind">What kind of water it is.</param>
    /// <param name="spline">Its curve, closed for every kind but <see cref="WaterBodyKind.River" />.</param>
    /// <param name="profile">The profile at each of the spline's control points, or empty for the default.</param>
    /// <param name="defaults">What a body with no per-point profile is like.</param>
    /// <exception cref="ArgumentNullException"><paramref name="spline" /> is null.</exception>
    /// <exception cref="ArgumentException">The curve does not close when the kind needs it to.</exception>
    public WaterBody(
        WaterBodyKind kind,
        Spline spline,
        ReadOnlySpan<WaterProfilePoint> profile = default,
        WaterProfilePoint defaults = default
    ) {
        ArgumentNullException.ThrowIfNull(spline);

        if (kind != WaterBodyKind.River && !spline.IsClosed) {
            throw new ArgumentException(
                $"A {kind} is bounded by its own spline, so the spline has to close. An open curve "
                + "has no inside, and the containment test would answer arbitrarily depending on "
                + "which way the ray was cast.",
                nameof(spline)
            );
        }

        Kind = kind;
        Spline = spline;
        Defaults = defaults;

        points = profile.IsEmpty ? [] : profile.ToArray();

        if (points.Length is not 0 && points.Length != spline.Points.Length) {
            throw new ArgumentException(
                $"The profile has {points.Length} points and the spline has {spline.Points.Length}. "
                + "A profile is per control point, so a mismatch means one of the two was edited "
                + "without the other — which would silently reassign every value along the curve.",
                nameof(profile)
            );
        }

        boundary = Flatten(spline, out boundaryParameters);
    }

    /// <summary>What kind of water it is.</summary>
    public WaterBodyKind Kind { get; }

    /// <summary>Its curve.</summary>
    public Spline Spline { get; }

    /// <summary>What it is like where the profile says nothing.</summary>
    public WaterProfilePoint Defaults { get; }

    /// <summary>Which body wins where two overlap. Higher is on top.</summary>
    /// <remarks>
    ///     ⚠ <b>Resolved once, when the field is rasterised, and never per pixel.</b> That is the
    ///     whole reason [§ D3](../../docs/plan/35-water.md#d3-the-water-info-texture-is-the-interchange-and-it-is-a-zone-render)
    ///     makes the zone the render unit: a river mouth is where two flow models meet, and a
    ///     material that had to blend them would need to know which two bodies it was between.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>Where a closed body's surface sits, in world units.</summary>
    /// <remarks>
    ///     Ignored by a <see cref="WaterBodyKind.River" />, whose surface follows its own curve —
    ///     that is the whole difference between a river and a bent lake.
    /// </remarks>
    public float SurfaceHeight { get; init; }

    /// <summary>How wide the band is over which coverage falls to zero at the boundary, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero is a hard edge, and a hard edge on water is visible from a long way off.</b> The
    ///     surface meets the ground at a line the eye reads as a cut in the terrain rather than as a
    ///     shore. Two metres is a beach; a quarter of a metre is a canal wall, which is also a real
    ///     thing to want.
    /// </remarks>
    public float ShoreFalloff { get; init; } = 2f;

    /// <summary>How far inside the boundary the bed reaches its full depth, in metres.</summary>
    /// <remarks>
    ///     Unreal's <c>Curve Ramp Width</c>. The bed is at the shoreline's own height at the boundary
    ///     and at <see cref="WaterProfilePoint.Depth" /> this far in, which is what makes a lake a
    ///     bowl rather than a hole with vertical sides.
    /// </remarks>
    public float BedRamp { get; init; } = 4f;

    /// <summary>Whether this body removes water rather than adding it.</summary>
    public bool IsSubtractive => Kind == WaterBodyKind.Island;

    /// <summary>The profile at a parameter along the curve.</summary>
    /// <param name="t">The parameter, 0…<see cref="Spline.MaxParameter" />.</param>
    /// <returns>The profile there.</returns>
    /// <remarks>
    ///     Linear between control points rather than along the curve's own basis, deliberately: a
    ///     width that overshoots between two points is a river that bulges where nobody authored a
    ///     bulge, and the four channels are all quantities an author expects to read off the two ends.
    /// </remarks>
    public WaterProfilePoint ProfileAt(float t) {
        if (points.Length == 0) {
            return Defaults;
        }

        var clamped = Math.Clamp(t, 0f, Spline.MaxParameter);
        var index = (int)MathF.Floor(clamped);
        var fraction = clamped - index;

        if (index >= points.Length - 1) {
            return Spline.IsClosed ? points[^1].Lerp(points[0], fraction) : points[^1];
        }

        return points[index].Lerp(points[index + 1], fraction);
    }

    /// <summary>What this body says about a place on the ground.</summary>
    /// <param name="ground">Where, on the ground plane — world X and Z.</param>
    /// <returns>The contribution, which is <see cref="WaterBodyContribution.None" /> outside it.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Allocation-free and pure.</b> This is what the rasteriser calls per texel and what a
    ///         gameplay query calls per character, and neither may allocate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An open body's flow follows the curve's tangent and a closed body has none.</b>
    ///         See <see cref="WaterProfilePoint.Velocity" /> — a lake whose whole surface drifted in
    ///         one direction is not a thing water does.
    ///     </para>
    /// </remarks>
    public WaterBodyContribution Sample(Vector2 ground) {
        var query = new Vector3(ground.X, 0f, ground.Y);
        var distance = DistanceToCurve(query, out var parameter);
        var profile = ProfileAt(parameter);

        // The signed distance to the body's *boundary*: negative inside. An open body is a channel
        // about its centreline; a closed one is its own polygon.
        var signed = Spline.IsClosed
            ? (Contains(ground) ? -distance : distance) - MathF.Max(profile.HalfWidth, 0f)
            : distance - MathF.Max(profile.HalfWidth, 0f);

        var falloff = MathF.Max(ShoreFalloff, 0f);

        if (signed >= falloff) {
            return WaterBodyContribution.None;
        }

        var coverage = 1f - WaterMath.SmoothStep(0f, falloff, signed);

        if (coverage <= 0f) {
            return WaterBodyContribution.None;
        }

        var surface = Spline.IsClosed && Kind != WaterBodyKind.River
            ? SurfaceHeight
            : Spline.Evaluate(parameter).Y;

        // The bed is at the shoreline at the boundary and at its full depth one ramp width in, which
        // is what makes a body a bowl rather than a hole with vertical sides.
        var bed = profile.Depth * WaterMath.SmoothStep(0f, MathF.Max(BedRamp, 0f), -signed);

        var flow = Vector2.Zero;

        if (!Spline.IsClosed && profile.Velocity != 0f) {
            var tangent = Spline.Tangent(parameter);
            var planar = new Vector2(tangent.X, tangent.Z);
            var length = planar.Length();

            if (length > 0f) {
                flow = planar / length * profile.Velocity;
            }
        }

        return new(coverage, surface, flow, bed, profile.AudioIntensity * coverage);
    }

    /// <summary>Whether a place on the ground is inside a closed body's boundary.</summary>
    /// <param name="ground">Where, on the ground plane.</param>
    /// <returns>Whether it is inside. Always false for an open body.</returns>
    /// <remarks>
    ///     <para>
    ///         The even-odd crossing rule over the flattened boundary. Half-open in Y — a vertex
    ///         exactly at the ray's height counts for one of its two edges and not the other — which
    ///         is what makes a query at the same height as a control point answer once rather than
    ///         zero or twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Even-odd rather than winding, and a self-crossing body is therefore holed.</b>
    ///         That is the honest answer: a spline that crosses itself has an inside that depends on
    ///         which rule you pick, and a lake drawn in a figure of eight should look wrong rather
    ///         than quietly pick one.
    ///     </para>
    /// </remarks>
    public bool Contains(Vector2 ground) {
        if (!Spline.IsClosed) {
            return false;
        }

        var inside = false;

        // ⚠ One short of the end. The polyline repeats its first vertex so the distance walk needs no
        // wrap-around case; including it here would test a zero-length edge, which contributes nothing
        // but says the polygon has a vertex it does not.
        var last = boundary.Length - 1;

        for (int i = 0, j = last - 1; i < last; j = i++) {
            var a = boundary[i];
            var b = boundary[j];

            if (a.Y > ground.Y != b.Y > ground.Y
                && ground.X < ((b.X - a.X) * (ground.Y - a.Y) / (b.Y - a.Y)) + a.X) {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Whether any part of this body reaches a square window on the ground plane.</summary>
    /// <param name="centre">The window's centre.</param>
    /// <param name="halfExtent">Half the window's width, in metres.</param>
    /// <returns>Whether the window and the body overlap.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two half-tests, and the second is the one a boundary walk cannot replace.</b> A
    ///         vertex of the flattened polyline inside the window grown by <see cref="Reach" /> is a
    ///         shoreline the window sees; the window's centre inside the body is a window the
    ///         <em>body</em> contains — a camera in the middle of an ocean is nowhere near any
    ///         shoreline, and a test that only walked the boundary would answer dry ground mid-lake.
    ///     </para>
    ///     <para>
    ///         Against the flattened polyline rather than the curve, for
    ///         <see cref="DistanceToCurve" />'s reason: the polyline is built once in the constructor,
    ///         and re-evaluating the cubic per zone per frame is the cost it exists to avoid.
    ///     </para>
    /// </remarks>
    public bool Reaches(Vector2 centre, float halfExtent) {
        var half = MathF.Max(halfExtent, 0f) + Reach;

        foreach (var point in boundary) {
            if (MathF.Abs(point.X - centre.X) <= half && MathF.Abs(point.Y - centre.Y) <= half) {
                return true;
            }
        }

        return Contains(centre);
    }

    /// <summary>The furthest from its curve this body reaches, in metres.</summary>
    /// <remarks>What sizes the rect a rasteriser has to visit, and what a bounding box is built from.</remarks>
    public float Reach {
        get {
            var widest = points.Length == 0 ? Defaults.HalfWidth : 0f;

            foreach (var point in points) {
                widest = MathF.Max(widest, point.HalfWidth);
            }

            return MathF.Max(widest, 0f) + MathF.Max(ShoreFalloff, 0f);
        }
    }

    /// <summary>How far a place is from the curve on the ground plane, and where along it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Flattened to the ground plane before measuring.</b> <c>Spline.DistanceTo</c> is
    ///         3-D, which is right for a camera on a track and wrong here: a river forty metres below
    ///         a clifftop would be "forty metres away" from a point directly above it, so its channel
    ///         would be narrower at the top of a gorge than at the bottom — and the surface would
    ///         pinch wherever the ground is steep.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against the flattened polyline rather than the curve, and the difference is two
    ///         orders of magnitude.</b> The obvious form scans the curve for a nearest sample and then
    ///         ternary-searches between its neighbours — ninety spline evaluations per query, each a
    ///         cubic Hermite. Rasterising a 256² field against one body is sixty-five thousand of
    ///         those, and it took <em>four seconds</em>: not an amortised cost, a hitch. The polyline
    ///         is built once in the constructor and a point-to-segment projection is six multiplies,
    ///         so the same field is milliseconds — and it is <em>more</em> accurate than the search it
    ///         replaces, because a projection onto a segment is exact where a ternary search over
    ///         thirty-two iterations is not.
    ///     </para>
    ///     <para>
    ///         What it costs is that the curve is a polyline at
    ///         <see cref="BoundarySamples" /> per segment — the same flattening
    ///         <see cref="Contains" /> already tested against, so the shoreline and the containment
    ///         test now agree about where the body is by construction rather than to within a
    ///         sampling difference.
    ///     </para>
    /// </remarks>
    float DistanceToCurve(Vector3 query, out float parameter) {
        var target = new Vector2(query.X, query.Z);
        var best = float.PositiveInfinity;

        parameter = 0f;

        for (var index = 0; index + 1 < boundary.Length; index++) {
            var a = boundary[index];
            var b = boundary[index + 1];
            var edge = b - a;
            var lengthSquared = Vector2.Dot(edge, edge);

            var t = lengthSquared > 0f ? Math.Clamp(Vector2.Dot(target - a, edge) / lengthSquared, 0f, 1f) : 0f;
            var nearest = a + (edge * t);
            var distance = Vector2.DistanceSquared(target, nearest);

            if (distance < best) {
                best = distance;

                var from = boundaryParameters[index];
                parameter = from + ((boundaryParameters[index + 1] - from) * t);
            }
        }

        return MathF.Sqrt(best);
    }

    /// <summary>The curve as a polyline on the ground plane, with the parameter at each vertex.</summary>
    /// <remarks>
    ///     A closed body repeats its first vertex at the end, so the last segment closes the loop and
    ///     the distance walk needs no wrap-around case. <see cref="Contains" /> skips that duplicate by
    ///     walking pairs, which is why the polygon it tests is the same one either way.
    /// </remarks>
    static Vector2[] Flatten(Spline spline, out float[] parameters) {
        var steps = spline.SegmentCount * BoundarySamples;
        var count = steps + 1;

        var polyline = new Vector2[count];
        parameters = new float[count];

        for (var step = 0; step < count; step++) {
            var t = spline.MaxParameter * (step / (float)steps);
            var point = spline.Evaluate(t);

            polyline[step] = new(point.X, point.Z);
            parameters[step] = t;
        }

        return polyline;
    }
}
