// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Core.Mathematics;

/// <summary>One control point of a <see cref="Spline" />.</summary>
/// <param name="Position">Where it is.</param>
/// <param name="TangentIn">
///     The tangent the curve arrives on, as an offset from <paramref name="Position" />.
/// </param>
/// <param name="TangentOut">
///     The tangent the curve leaves on, as an offset from <paramref name="Position" />.
/// </param>
/// <param name="Roll">How far the frame is twisted about the tangent at this point, in radians.</param>
/// <remarks>
///     <para>
///         <b>Two tangents rather than one, so a corner is expressible.</b> A single tangent forces
///         the curve to be smooth everywhere, and the thing a road and a camera track both need is the
///         occasional hard corner — which is <c>TangentIn ≠ −TangentOut</c>. Mirroring them is what
///         <see cref="Spline.SmoothTangents" /> does, and it is a choice the author makes per point.
///     </para>
///     <para>
///         <b>Offsets, not directions.</b> Their length is the curve's speed leaving the point, which
///         is what a Hermite segment is parameterised by. Normalising them here would make every
///         segment take the same shape regardless of how far apart its ends are.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct SplinePoint(
    Vector3 Position,
    Vector3 TangentIn,
    Vector3 TangentOut,
    float Roll = 0f
) {
    /// <summary>A point with no tangents, which makes the segments through it straight.</summary>
    /// <param name="position">Where it is.</param>
    /// <returns>The point.</returns>
    public static SplinePoint At(Vector3 position) => new(position, Vector3.Zero, Vector3.Zero);

    /// <summary>A point whose two tangents mirror one another, so the curve is smooth through it.</summary>
    /// <param name="position">Where it is.</param>
    /// <param name="tangent">The outgoing tangent; the incoming one is its negation.</param>
    /// <param name="roll">The twist about the tangent, in radians.</param>
    /// <returns>The point.</returns>
    public static SplinePoint Smooth(Vector3 position, Vector3 tangent, float roll = 0f) =>
        new(position, -tangent, tangent, roll);
}

/// <summary>Where a spline is at some parameter, and which way it is facing.</summary>
/// <param name="Position">The point on the curve.</param>
/// <param name="Tangent">The unit direction of travel.</param>
/// <param name="Normal">The unit up direction, after the point's roll.</param>
/// <param name="Binormal">The unit side direction. <c>Tangent × Normal</c>.</param>
public readonly record struct SplineFrame(Vector3 Position, Vector3 Tangent, Vector3 Normal, Vector3 Binormal);

/// <summary>
///     A path through space: control points, cubic segments between them, and an arc length.
/// </summary>
/// <remarks>
///     <para>
///         <b>One spline, two consumers, and that is why it is here rather than in either of them.</b>
///         [docs/plan/26] wanted one for a camera dolly and declined to invent it, because "it would
///         make it the second spline in the engine the moment anything else needs one";
///         [docs/plan/31 § T8] is that moment, for roads and rivers that deform a terrain. A curve is
///         arithmetic and both callers already reference this assembly, so it costs no new project
///         reference in either direction.
///     </para>
///     <para>
///         <b>Cubic Hermite, because it is the form whose control points are on the curve.</b> A
///         Bézier's handles are not points of the path, so an author dragging one is not dragging the
///         road; a B-spline's are not either. Hermite is the same family of curves with the
///         parameterisation an editor wants — and a Catmull-Rom is exactly this with the tangents
///         computed, which is what <see cref="SmoothTangents" /> does.
///     </para>
///     <para>
///         ⚠ <b>The parameter is not distance, and confusing the two is the classic bug in this
///         type.</b> <see cref="Evaluate" /> takes a segment-space parameter, where an integer is a
///         control point; a camera moving at a constant <em>parameter</em> rate speeds up through
///         wide-open segments and crawls through tight ones. <see cref="EvaluateAtDistance" /> is the
///         one that moves at a constant speed, and it costs a table lookup.
///     </para>
/// </remarks>
public sealed class Spline {
    /// <summary>How many pieces each segment is measured in when building the length table.</summary>
    /// <remarks>
    ///     Sixteen is where the measured length of a quarter-circle segment stops improving in the
    ///     fourth decimal, which is far below what anything reads it for. It is a constant rather than
    ///     a setting because an arc length that depended on a quality knob would make two machines
    ///     that set it differently place a camera in different places.
    /// </remarks>
    public const int SamplesPerSegment = 16;

    readonly SplinePoint[] points;
    readonly float[] cumulative;

    /// <summary>Builds a spline through control points.</summary>
    /// <param name="controlPoints">The points, in order. At least two for an open spline.</param>
    /// <param name="closed">Whether the last point joins back to the first.</param>
    /// <exception cref="ArgumentException">There are too few points.</exception>
    public Spline(ReadOnlySpan<SplinePoint> controlPoints, bool closed = false) {
        if (controlPoints.Length < 2) {
            throw new ArgumentException(
                $"A spline needs at least two control points; {controlPoints.Length} were given.",
                nameof(controlPoints)
            );
        }

        points = controlPoints.ToArray();
        IsClosed = closed;

        cumulative = new float[(SegmentCount * SamplesPerSegment) + 1];
        Measure();
    }

    /// <summary>The control points, in order.</summary>
    public ReadOnlySpan<SplinePoint> Points => points;

    /// <summary>Whether the last point joins back to the first.</summary>
    public bool IsClosed { get; }

    /// <summary>How many cubic segments there are.</summary>
    public int SegmentCount => IsClosed ? points.Length : points.Length - 1;

    /// <summary>The largest parameter <see cref="Evaluate" /> accepts.</summary>
    public float MaxParameter => SegmentCount;

    /// <summary>How long the curve is, in the units its positions are in.</summary>
    /// <remarks>
    ///     Measured rather than derived: a cubic's arc length has no closed form, so this is the sum
    ///     of <see cref="SamplesPerSegment" /> chords per segment and is therefore a slight
    ///     under-estimate. Under rather than over is the right direction to be wrong in — a camera
    ///     told the track is shorter than it is stops at the end rather than past it.
    /// </remarks>
    public float Length => cumulative[^1];

    /// <summary>Where the curve is at a parameter.</summary>
    /// <param name="t">
    ///     Segment space: 0 is the first point, 1 the second, and <see cref="MaxParameter" /> the end.
    ///     Clamped for an open spline and wrapped for a closed one.
    /// </param>
    /// <returns>The position.</returns>
    public Vector3 Evaluate(float t) {
        var (segment, local) = Locate(t);
        var (start, end) = EndsOf(segment);

        var l2 = local * local;
        var l3 = l2 * local;

        // Hermite basis. The two tangent terms are the point's own offsets, which is why a control
        // point with zero tangents produces a straight line rather than a degenerate curve.
        var h00 = (2f * l3) - (3f * l2) + 1f;
        var h10 = l3 - (2f * l2) + local;
        var h01 = (-2f * l3) + (3f * l2);
        var h11 = l3 - l2;

        return (start.Position * h00)
            + (start.TangentOut * h10)
            + (end.Position * h01)
            + (-end.TangentIn * h11);
    }

    /// <summary>Which way the curve is heading at a parameter.</summary>
    /// <param name="t">The parameter, as <see cref="Evaluate" /> takes it.</param>
    /// <returns>The unit tangent.</returns>
    /// <remarks>
    ///     The analytic derivative, not a difference of two samples. A difference is wrong by half a
    ///     step everywhere and wrong by a lot at a corner, and the frame built on it visibly lags the
    ///     curve — which on a camera dolly reads as the camera turning after it has already gone round
    ///     the bend.
    /// </remarks>
    public Vector3 Tangent(float t) {
        var (segment, local) = Locate(t);
        var (start, end) = EndsOf(segment);

        var l2 = local * local;

        var d00 = (6f * l2) - (6f * local);
        var d10 = (3f * l2) - (4f * local) + 1f;
        var d01 = (-6f * l2) + (6f * local);
        var d11 = (3f * l2) - (2f * local);

        var derivative = (start.Position * d00)
            + (start.TangentOut * d10)
            + (end.Position * d01)
            + (-end.TangentIn * d11);

        // A segment can be genuinely stationary. Two cases, and they need different answers: a
        // straight segment authored with no tangents has a zero derivative at its own ends and a
        // perfectly good chord to fall back on, while two coincident points have neither. Note that
        // Vector3.Normalize answers Zero rather than NaN for a degenerate input — so the fallback has
        // to be chosen on the length, not detected afterwards by looking for a NaN that never comes.
        if (derivative.LengthSquared() > 1e-12f) {
            return Vector3.Normalize(derivative);
        }

        var chord = end.Position - start.Position;
        return chord.LengthSquared() > 1e-12f ? Vector3.Normalize(chord) : Vector3.UnitZ;
    }

    /// <summary>The full orientation at a parameter: where, which way, and which way is up.</summary>
    /// <param name="t">The parameter, as <see cref="Evaluate" /> takes it.</param>
    /// <param name="worldUp">
    ///     Which way is up in the world. The frame's normal is built against it, so a curve heading
    ///     straight along it has no defined side and falls back to a stable axis.
    /// </param>
    /// <returns>The frame, with the point's roll applied.</returns>
    /// <remarks>
    ///     A reference-vector frame rather than a parallel-transport one. Parallel transport is
    ///     smoother through a loop and is <em>path-dependent</em>: the frame at a parameter would
    ///     depend on where the walk started, so two callers sampling the same spline at the same place
    ///     could disagree. A road banked by <see cref="SplinePoint.Roll" /> is what an author wants
    ///     control of anyway.
    /// </remarks>
    public SplineFrame FrameAt(float t, Vector3 worldUp) {
        var position = Evaluate(t);
        var tangent = Tangent(t);

        var up = worldUp.LengthSquared() > 1e-12f ? Vector3.Normalize(worldUp) : Vector3.UnitY;
        var binormal = Vector3.Cross(tangent, up);

        if (binormal.LengthSquared() < 1e-8f) {
            // Heading straight up or straight down: any side is as good as any other, so pick one
            // deterministically rather than letting a normalisation of nearly-zero decide.
            binormal = Vector3.Cross(tangent, Vector3.UnitZ);

            if (binormal.LengthSquared() < 1e-8f) {
                binormal = Vector3.Cross(tangent, Vector3.UnitX);
            }
        }

        binormal = Vector3.Normalize(binormal);
        var normal = Vector3.Normalize(Vector3.Cross(binormal, tangent));

        var roll = RollAt(t);

        if (roll != 0f) {
            var (sin, cos) = MathF.SinCos(roll);
            var rolled = (normal * cos) + (binormal * sin);
            binormal = (binormal * cos) - (normal * sin);
            normal = rolled;
        }

        return new(position, tangent, normal, binormal);
    }

    /// <summary>The parameter at which the curve has travelled a distance.</summary>
    /// <param name="distance">How far along, in the positions' units. Clamped to the curve.</param>
    /// <returns>The parameter, for <see cref="Evaluate" />.</returns>
    public float ParameterAtDistance(float distance) {
        if (!(distance > 0f)) {
            return 0f;
        }

        if (distance >= Length) {
            return MaxParameter;
        }

        // The table is monotonic by construction, so a binary search is exact rather than a guess
        // that has to be refined.
        var low = 0;
        var high = cumulative.Length - 1;

        while (high - low > 1) {
            var middle = (low + high) / 2;

            if (cumulative[middle] <= distance) {
                low = middle;
            } else {
                high = middle;
            }
        }

        var span = cumulative[high] - cumulative[low];
        var fraction = span > 1e-9f ? (distance - cumulative[low]) / span : 0f;

        return (low + fraction) / SamplesPerSegment;
    }

    /// <summary>Where the curve is after travelling a distance along it.</summary>
    /// <param name="distance">How far along. Clamped to the curve.</param>
    /// <returns>The position.</returns>
    /// <remarks>
    ///     What a camera dolly and a spline mesh both want, and what <see cref="Evaluate" /> is not:
    ///     equal distances here are equal distances in the world, where equal parameters are not.
    /// </remarks>
    public Vector3 EvaluateAtDistance(float distance) => Evaluate(ParameterAtDistance(distance));

    /// <summary>The point of the curve nearest to a place.</summary>
    /// <param name="target">The place.</param>
    /// <param name="parameter">The parameter there.</param>
    /// <returns>The distance from <paramref name="target" /> to the curve.</returns>
    /// <remarks>
    ///     A scan of the length table followed by a bounded refinement between its neighbours. Exact
    ///     minimisation of a cubic's distance is a quintic root-find; this is accurate to well inside
    ///     the width of anything that asks — which is a road's half-width, or how far a camera is from
    ///     its track.
    /// </remarks>
    public float DistanceTo(Vector3 target, out float parameter) {
        var best = float.PositiveInfinity;
        var bestIndex = 0;

        for (var index = 0; index < cumulative.Length; index++) {
            var distance = Vector3.DistanceSquared(Evaluate(index / (float)SamplesPerSegment), target);

            if (distance < best) {
                best = distance;
                bestIndex = index;
            }
        }

        var step = 1f / SamplesPerSegment;
        var centre = bestIndex * step;
        var low = MathF.Max(0f, centre - step);
        var high = MathF.Min(MaxParameter, centre + step);

        // Ternary search over one span, which is unimodal there because the curve is smooth and the
        // table already localised the minimum to a neighbourhood of it.
        for (var iteration = 0; iteration < 32 && high - low > 1e-6f; iteration++) {
            var third = (high - low) / 3f;
            var a = low + third;
            var b = high - third;

            if (Vector3.DistanceSquared(Evaluate(a), target) < Vector3.DistanceSquared(Evaluate(b), target)) {
                high = b;
            } else {
                low = a;
            }
        }

        parameter = (low + high) * 0.5f;
        return Vector3.Distance(Evaluate(parameter), target);
    }

    /// <summary>
    ///     Catmull-Rom tangents for positions, so a path through points can be authored as points.
    /// </summary>
    /// <param name="positions">The positions, in order.</param>
    /// <param name="closed">Whether the path joins back to its start.</param>
    /// <param name="tension">
    ///     0 makes the classic Catmull-Rom; 1 makes every tangent zero, which is a polyline.
    /// </param>
    /// <returns>The control points, with tangents.</returns>
    /// <remarks>
    ///     Each tangent is half the chord between a point's two neighbours, which is the standard
    ///     construction and the reason a Catmull-Rom passes through all of its points. The ends of an
    ///     open path take the chord to their one neighbour rather than a reflected phantom point: a
    ///     phantom overshoots, and an overshooting road runs off the end of itself.
    /// </remarks>
    public static SplinePoint[] SmoothTangents(
        ReadOnlySpan<Vector3> positions,
        bool closed = false,
        float tension = 0f
    ) {
        if (positions.Length < 2) {
            throw new ArgumentException(
                $"A spline needs at least two positions; {positions.Length} were given.",
                nameof(positions)
            );
        }

        var scale = (1f - Math.Clamp(tension, 0f, 1f)) * 0.5f;
        var result = new SplinePoint[positions.Length];

        for (var index = 0; index < positions.Length; index++) {
            var previous = index > 0
                ? positions[index - 1]
                : closed
                    ? positions[^1]
                    : positions[index];

            var next = index < positions.Length - 1
                ? positions[index + 1]
                : closed
                    ? positions[0]
                    : positions[index];

            var tangent = (next - previous) * scale;
            result[index] = SplinePoint.Smooth(positions[index], tangent);
        }

        return result;
    }

    float RollAt(float t) {
        var (segment, local) = Locate(t);
        var (start, end) = EndsOf(segment);
        return start.Roll + ((end.Roll - start.Roll) * local);
    }

    (SplinePoint Start, SplinePoint End) EndsOf(int segment) =>
        (points[segment], points[IsClosed ? (segment + 1) % points.Length : segment + 1]);

    (int Segment, float Local) Locate(float t) {
        if (float.IsNaN(t)) {
            return (0, 0f);
        }

        if (IsClosed) {
            var wrapped = t % SegmentCount;

            if (wrapped < 0f) {
                wrapped += SegmentCount;
            }

            var index = Math.Min((int)wrapped, SegmentCount - 1);
            return (index, wrapped - index);
        }

        var clamped = Math.Clamp(t, 0f, MaxParameter);
        var segment = Math.Min((int)clamped, SegmentCount - 1);
        return (segment, clamped - segment);
    }

    void Measure() {
        var previous = Evaluate(0f);
        cumulative[0] = 0f;

        for (var sample = 1; sample < cumulative.Length; sample++) {
            var current = Evaluate(sample / (float)SamplesPerSegment);
            cumulative[sample] = cumulative[sample - 1] + Vector3.Distance(previous, current);
            previous = current;
        }
    }
}
