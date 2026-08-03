// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>One point on a goal curve.</summary>
/// <param name="Phase">Where in the clip, in <c>[0, 1]</c>.</param>
/// <param name="Value">What the goal was there.</param>
public readonly record struct TrajectoryKey(float Phase, Vector3 Value);

/// <summary>One orientation on a goal curve.</summary>
/// <param name="Phase">Where in the clip, in <c>[0, 1]</c>.</param>
/// <param name="Value">What the goal's rotation was there.</param>
public readonly record struct TrajectoryRotationKey(float Phase, Quaternion Value);

/// <summary>One place on a surface, on a goal curve.</summary>
/// <param name="Phase">Where in the clip, in <c>[0, 1]</c>.</param>
/// <param name="Value">Where on the shape.</param>
public readonly record struct SurfacePathKey(float Phase, SurfacePoint Value);

/// <summary>How much error a decimation pass may introduce, per kind of curve.</summary>
/// <param name="Position">In metres, for the origin and offset polylines.</param>
/// <param name="Rotation">In radians.</param>
/// <param name="Surface">In normalised surface units, where one is the whole of an axis.</param>
/// <remarks>
///     ⚠ <b>Spelled out rather than written <c>new()</c></b>, for
///     <see cref="CurveCompressionSettings" />'s reason: a positional <c>record struct</c>'s
///     parameterless constructor zeroes its fields instead of applying the parameter defaults, so
///     <c>new()</c> and <c>default</c> both mean a tolerance of zero.
/// </remarks>
public readonly record struct TrajectoryTolerance(
    float Position = 1e-3f,
    float Rotation = 8.7e-3f,
    float Surface = 2e-3f
) {
    /// <summary>A millimetre, half a degree, and a fifth of a percent of a surface.</summary>
    public static TrajectoryTolerance Default => new(1e-3f, 8.7e-3f, 2e-3f);
}

/// <summary>A polyline over clip phase, decimated against a tolerance.</summary>
/// <remarks>
///     <para>
///         The runtime side is a phase-keyed lookup and a lerp, which is the whole of it: a goal that
///         moves is a goal sampled per phase, and nothing about the arbiter or the solver needs to
///         know that it did.
///     </para>
///     <para>
///         <b>Phase, so a retimed clip needs no re-marking</b> — the same reason a constraint tag's
///         span is normalised.
///     </para>
/// </remarks>
public sealed class TrajectoryCurve {
    readonly TrajectoryKey[] keys;

    /// <summary>Builds a curve from keys already in phase order.</summary>
    /// <param name="keys">The keys.</param>
    public TrajectoryCurve(params ReadOnlySpan<TrajectoryKey> keys) => this.keys = keys.ToArray();

    /// <summary>A curve that is one value everywhere.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The curve.</returns>
    public static TrajectoryCurve Constant(Vector3 value) => new(new TrajectoryKey(0f, value));

    /// <summary>How many keys survived.</summary>
    public int Count => keys.Length;

    /// <summary>The keys.</summary>
    /// <returns>The keys.</returns>
    public ReadOnlySpan<TrajectoryKey> Keys => keys;

    /// <summary>What the curve is at a phase.</summary>
    /// <param name="phase">Where in the clip, in <c>[0, 1]</c>.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    ///     Clamped at the ends rather than wrapped. A trajectory that should loop says so by having a
    ///     key at zero and a matching one at one, which is a fact about the content — guessing it here
    ///     would make a reach that ends where it ends snap back to its start on the last frame.
    /// </remarks>
    public Vector3 Sample(float phase) {
        if (keys.Length == 0) {
            return Vector3.Zero;
        }

        var (before, after, amount) = Span(phase);
        return before == after ? keys[before].Value : Vector3.Lerp(keys[before].Value, keys[after].Value, amount);
    }

    /// <summary>Removes the keys a linear sampler would have produced anyway.</summary>
    /// <param name="source">The keys, in phase order.</param>
    /// <param name="tolerance">How far the sampled curve may move, in the keys' own units.</param>
    /// <param name="report">How much came out.</param>
    /// <returns>The curve.</returns>
    /// <remarks>
    ///     Ramer–Douglas–Peucker, with the error measured the way a linear sampler would see it —
    ///     at the dropped key's own phase, against the line between the keys that survive around it —
    ///     rather than as a perpendicular distance in a space where one axis is time and the others
    ///     are metres.
    /// </remarks>
    public static TrajectoryCurve Decimate(
        ReadOnlySpan<TrajectoryKey> source,
        float tolerance,
        out CurveCompressionReport report
    ) {
        if (source.Length <= 2) {
            report = new(source.Length, source.Length, source.Length > 0 ? 1 : 0, source.Length > 0 ? 1 : 0);
            return new(source);
        }

        var keep = new bool[source.Length];

        keep[0] = true;
        keep[^1] = true;

        Simplify(source, 0, source.Length - 1, MathF.Max(tolerance, 0f), keep);

        List<TrajectoryKey> kept = [];

        for (var index = 0; index < source.Length; index++) {
            if (keep[index]) {
                kept.Add(source[index]);
            }
        }

        // A curve that no longer moves is one key, not two saying the same thing.
        if (kept.Count == 2 && (kept[0].Value - kept[1].Value).Length() <= tolerance) {
            kept.RemoveAt(1);
        }

        report = new(source.Length, kept.Count, 1, kept.Count > 0 ? 1 : 0);
        return new([.. kept]);
    }

    static void Simplify(ReadOnlySpan<TrajectoryKey> source, int from, int to, float tolerance, bool[] keep) {
        if (to - from < 2) {
            return;
        }

        var worst = 0f;
        var at = -1;

        for (var index = from + 1; index < to; index++) {
            var span = source[to].Phase - source[from].Phase;

            var amount = MathF.Abs(span) <= 1e-9f
                ? 0f
                : MathUtil.Saturate((source[index].Phase - source[from].Phase) / span);

            var error = (source[index].Value - Vector3.Lerp(source[from].Value, source[to].Value, amount)).Length();

            if (error > worst) {
                worst = error;
                at = index;
            }
        }

        if (at < 0 || worst <= tolerance) {
            return;
        }

        keep[at] = true;

        Simplify(source, from, at, tolerance, keep);
        Simplify(source, at, to, tolerance, keep);
    }

    (int Before, int After, float Amount) Span(float phase) {
        var high = keys.Length - 1;

        if (high == 0 || phase <= keys[0].Phase) {
            return (0, 0, 0f);
        }

        if (phase >= keys[high].Phase) {
            return (high, high, 0f);
        }

        var low = 0;

        while (high - low > 1) {
            var middle = (low + high) / 2;

            if (keys[middle].Phase <= phase) {
                low = middle;
            } else {
                high = middle;
            }
        }

        var span = keys[high].Phase - keys[low].Phase;
        return (low, high, MathF.Abs(span) <= 1e-9f ? 0f : (phase - keys[low].Phase) / span);
    }
}

/// <summary>An orientation over clip phase.</summary>
public sealed class TrajectoryRotationCurve {
    readonly TrajectoryRotationKey[] keys;

    /// <summary>Builds a curve from keys already in phase order.</summary>
    /// <param name="keys">The keys.</param>
    public TrajectoryRotationCurve(params ReadOnlySpan<TrajectoryRotationKey> keys) => this.keys = keys.ToArray();

    /// <summary>How many keys survived.</summary>
    public int Count => keys.Length;

    /// <summary>The keys.</summary>
    /// <returns>The keys.</returns>
    public ReadOnlySpan<TrajectoryRotationKey> Keys => keys;

    /// <summary>What the curve is at a phase.</summary>
    /// <param name="phase">Where in the clip.</param>
    /// <returns>The rotation.</returns>
    /// <remarks>Slerped, not nlerped: a goal that turns through ninety degrees between two keys is
    ///     the ordinary case for a throw, and nlerp would make it hurry through the middle.</remarks>
    public Quaternion Sample(float phase) {
        if (keys.Length == 0) {
            return Quaternion.Identity;
        }

        var (before, after, amount) = Span(phase);

        return before == after
            ? keys[before].Value
            : Quaternion.Slerp(keys[before].Value, keys[after].Value, amount);
    }

    (int Before, int After, float Amount) Span(float phase) {
        var high = keys.Length - 1;

        if (high == 0 || phase <= keys[0].Phase) {
            return (0, 0, 0f);
        }

        if (phase >= keys[high].Phase) {
            return (high, high, 0f);
        }

        var low = 0;

        while (high - low > 1) {
            var middle = (low + high) / 2;

            if (keys[middle].Phase <= phase) {
                low = middle;
            } else {
                high = middle;
            }
        }

        var span = keys[high].Phase - keys[low].Phase;
        return (low, high, MathF.Abs(span) <= 1e-9f ? 0f : (phase - keys[low].Phase) / span);
    }

    /// <summary>Removes the keys a slerping sampler would have produced anyway.</summary>
    /// <param name="source">The keys, in phase order.</param>
    /// <param name="tolerance">How far the sampled rotation may turn, in radians.</param>
    /// <param name="report">How much came out.</param>
    /// <returns>The curve.</returns>
    public static TrajectoryRotationCurve Decimate(
        ReadOnlySpan<TrajectoryRotationKey> source,
        float tolerance,
        out CurveCompressionReport report
    ) {
        if (source.Length <= 2) {
            report = new(source.Length, source.Length, source.Length > 0 ? 1 : 0, source.Length > 0 ? 1 : 0);
            return new(source);
        }

        var keep = new bool[source.Length];

        keep[0] = true;
        keep[^1] = true;

        Simplify(source, 0, source.Length - 1, MathF.Max(tolerance, 0f), keep);

        List<TrajectoryRotationKey> kept = [];

        for (var index = 0; index < source.Length; index++) {
            if (keep[index]) {
                kept.Add(source[index]);
            }
        }

        report = new(source.Length, kept.Count, 1, kept.Count > 0 ? 1 : 0);
        return new([.. kept]);
    }

    static void Simplify(ReadOnlySpan<TrajectoryRotationKey> source, int from, int to, float tolerance, bool[] keep) {
        if (to - from < 2) {
            return;
        }

        var worst = 0f;
        var at = -1;

        for (var index = from + 1; index < to; index++) {
            var span = source[to].Phase - source[from].Phase;

            var amount = MathF.Abs(span) <= 1e-9f
                ? 0f
                : MathUtil.Saturate((source[index].Phase - source[from].Phase) / span);

            var error = Angle(source[index].Value, Quaternion.Slerp(source[from].Value, source[to].Value, amount));

            if (error > worst) {
                worst = error;
                at = index;
            }
        }

        if (at < 0 || worst <= tolerance) {
            return;
        }

        keep[at] = true;

        Simplify(source, from, at, tolerance, keep);
        Simplify(source, at, to, tolerance, keep);
    }

    static float Angle(Quaternion from, Quaternion to) =>
        2f * MathF.Acos(MathF.Abs(MathUtil.Clamp(Quaternion.Dot(from, to), -1f, 1f)));
}

/// <summary>A path across a proxy shape's surface, over clip phase.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is what makes a slide reproduce on a rail of a different length and radius.</b> A
///         world-space offset polyline replays the same centimetres on every body; a path in
///         normalised surface coordinates replays the same <em>fractions</em>, so a hand that ran from
///         a fifth of the way along a rail to four fifths of the way along it does that on a rail of
///         any size.
///     </para>
///     <para>
///         ⚠ <b><c>U</c> is unwrapped before decimation and wrapped on the way out.</b> It is an angle
///         on a round shape, so a slide that crosses the seam reads as a jump from 0.98 to 0.02 — and
///         a decimator handed that either keeps every key around the seam or, worse, averages through
///         it and sends the contact the long way round the limb.
///     </para>
/// </remarks>
public sealed class SurfacePath {
    readonly SurfacePathKey[] keys;

    /// <summary>Builds a path from keys already in phase order.</summary>
    /// <param name="keys">The keys.</param>
    public SurfacePath(params ReadOnlySpan<SurfacePathKey> keys) => this.keys = keys.ToArray();

    /// <summary>How many keys survived.</summary>
    public int Count => keys.Length;

    /// <summary>The keys.</summary>
    /// <returns>The keys.</returns>
    public ReadOnlySpan<SurfacePathKey> Keys => keys;

    /// <summary>Where on the surface the path is at a phase.</summary>
    /// <param name="phase">Where in the clip.</param>
    /// <returns>The place.</returns>
    /// <remarks>
    ///     The face comes from the key at or before the phase rather than being interpolated: a face
    ///     index is a name, and half way between the top of a box and its side is not a place.
    /// </remarks>
    public SurfacePoint Sample(float phase) {
        if (keys.Length == 0) {
            return SurfacePoint.Side;
        }

        var (before, after, amount) = Span(phase);
        var start = keys[before].Value;

        if (before == after) {
            return start;
        }

        var end = keys[after].Value;

        return new(start.Face, Wrap(start.U + (Delta(start.U, end.U) * amount)), MathUtil.Lerp(start.V, end.V, amount));
    }

    /// <summary>Removes the keys a lerping sampler would have produced anyway.</summary>
    /// <param name="source">The keys, in phase order.</param>
    /// <param name="tolerance">How far the sampled place may move, in normalised units.</param>
    /// <param name="report">How much came out.</param>
    /// <returns>The path.</returns>
    public static SurfacePath Decimate(
        ReadOnlySpan<SurfacePathKey> source,
        float tolerance,
        out CurveCompressionReport report
    ) {
        if (source.Length == 0) {
            report = default;
            return new();
        }

        // Unwrapped into a plain polyline, decimated as one, and re-wrapped. Doing it any other way
        // means the seam decides how well the curve compresses.
        var flat = new TrajectoryKey[source.Length];
        var running = source[0].Value.U;

        flat[0] = new(source[0].Phase, new(running, source[0].Value.V, source[0].Value.Face));

        for (var index = 1; index < source.Length; index++) {
            running += Delta(source[index - 1].Value.U, source[index].Value.U);
            flat[index] = new(source[index].Phase, new(running, source[index].Value.V, source[index].Value.Face));
        }

        var curve = TrajectoryCurve.Decimate(flat, tolerance, out report);
        var kept = new SurfacePathKey[curve.Count];

        for (var index = 0; index < curve.Count; index++) {
            var key = curve.Keys[index];
            kept[index] = new(key.Phase, new((int)MathF.Round(key.Value.Z), Wrap(key.Value.X), key.Value.Y));
        }

        return new(kept);
    }

    (int Before, int After, float Amount) Span(float phase) {
        var high = keys.Length - 1;

        if (high == 0 || phase <= keys[0].Phase) {
            return (0, 0, 0f);
        }

        if (phase >= keys[high].Phase) {
            return (high, high, 0f);
        }

        var low = 0;

        while (high - low > 1) {
            var middle = (low + high) / 2;

            if (keys[middle].Phase <= phase) {
                low = middle;
            } else {
                high = middle;
            }
        }

        var span = keys[high].Phase - keys[low].Phase;
        return (low, high, MathF.Abs(span) <= 1e-9f ? 0f : (phase - keys[low].Phase) / span);
    }

    /// <summary>The shortest way round from one angle to another.</summary>
    static float Delta(float from, float to) {
        var difference = (to - from) % 1f;

        return difference switch {
            > 0.5f => difference - 1f,
            < -0.5f => difference + 1f,
            _ => difference
        };
    }

    static float Wrap(float value) {
        var wrapped = value % 1f;
        return wrapped < 0f ? wrapped + 1f : wrapped;
    }
}

/// <summary>One captured moment of a contact, as the decomposition sees it.</summary>
/// <param name="Phase">Where in the clip.</param>
/// <param name="Origin">Where the frame the contact was expressed in was.</param>
/// <param name="Offset">Where the contact was relative to that frame.</param>
/// <param name="Point">Where on the shape it was, if the frame was a surface.</param>
/// <param name="Rotation">Which way the contact faced.</param>
public readonly record struct TrajectorySample(
    float Phase,
    Vector3 Origin,
    Vector3 Offset,
    SurfacePoint Point,
    Quaternion Rotation
);

/// <summary>What a decomposition removed, per polyline.</summary>
/// <param name="Origin">The frame's own path.</param>
/// <param name="Offset">The contact relative to it.</param>
/// <param name="Surface">The path across the surface.</param>
/// <param name="Rotation">The orientation.</param>
/// <remarks>
///     Four reports rather than one, for <see cref="CurveCompressionSettings" />'s reason: they
///     compress very differently and a single number would have to be wrong for three of them. What
///     an author needs to see is which of the four a tolerance actually cost anything.
/// </remarks>
public readonly record struct TrajectoryReport(
    CurveCompressionReport Origin,
    CurveCompressionReport Offset,
    CurveCompressionReport Surface,
    CurveCompressionReport Rotation
) {
    /// <summary>How many keys went in, across all four.</summary>
    public int KeysBefore => Origin.KeysBefore + Offset.KeysBefore + Surface.KeysBefore + Rotation.KeysBefore;

    /// <summary>How many came out.</summary>
    public int KeysAfter => Origin.KeysAfter + Offset.KeysAfter + Surface.KeysAfter + Rotation.KeysAfter;

    /// <summary>How much of the original is left, as a fraction.</summary>
    public float Ratio => KeysBefore > 0 ? (float)KeysAfter / KeysBefore : 1f;
}

/// <summary>A goal that moves: the whole curve, decomposed and decimated.</summary>
/// <remarks>
///     <para>
///         A constant goal covers a contact that does not move. A hand <em>sliding along</em> a rail,
///         or tracking a target through a throw, is this.
///     </para>
///     <para>
///         ⚠ <b>Two polylines rather than one, because they compress very differently.</b> The frame's
///         origin usually barely moves while the offset carries all the shape, so decimating their sum
///         would keep every key either of them needed. Split, the origin often survives as two keys
///         and the offset as a handful. The price is that the two errors add, so each is decimated
///         against half the authored tolerance — which is stated here rather than discovered by
///         somebody measuring a curve that was twice as far out as they asked for.
///     </para>
/// </remarks>
public sealed class GoalTrajectory {
    /// <summary>Builds a trajectory from curves that are already decimated.</summary>
    /// <param name="origin">Where the frame was.</param>
    /// <param name="offset">Where the contact was relative to it.</param>
    /// <param name="surface">The path across the surface, or <see langword="null" /> for none.</param>
    /// <param name="rotation">The orientation, or <see langword="null" /> for none.</param>
    public GoalTrajectory(
        TrajectoryCurve origin,
        TrajectoryCurve offset,
        SurfacePath? surface = null,
        TrajectoryRotationCurve? rotation = null
    ) {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(offset);

        Origin = origin;
        Offset = offset;
        Surface = surface;
        Rotation = rotation;
    }

    /// <summary>Where the frame the contact was expressed in went, as authored.</summary>
    /// <remarks>
    ///     Not replayed at runtime — the frame is resolved live, which is the point of a frame. What
    ///     it is for is <see cref="Reconstruct" />: the authored path, so a tolerance can be checked
    ///     against what it cost rather than against a number somebody typed.
    /// </remarks>
    public TrajectoryCurve Origin { get; }

    /// <summary>Where the contact was relative to its frame, in the frame's own axes.</summary>
    public TrajectoryCurve Offset { get; }

    /// <summary>Where on the shape the contact ran, or <see langword="null" />.</summary>
    public SurfacePath? Surface { get; }

    /// <summary>Which way the contact faced, or <see langword="null" />.</summary>
    public TrajectoryRotationCurve? Rotation { get; }

    /// <summary>Whether it names a path across a surface.</summary>
    public bool HasSurface => Surface is { Count: > 0 };

    /// <summary>The authored path at a phase — origin plus offset, as stored.</summary>
    /// <param name="phase">Where in the clip.</param>
    /// <returns>Where the contact was.</returns>
    public Vector3 Reconstruct(float phase) => Origin.Sample(phase) + Offset.Sample(phase);

    /// <summary>Decomposes and decimates a captured contact.</summary>
    /// <param name="samples">The captured moments, in phase order.</param>
    /// <param name="tolerance">How much error each polyline may introduce.</param>
    /// <param name="report">What came out.</param>
    /// <param name="rotations">Whether the orientation is worth keeping at all.</param>
    /// <returns>The trajectory.</returns>
    public static GoalTrajectory Decompose(
        ReadOnlySpan<TrajectorySample> samples,
        TrajectoryTolerance tolerance,
        out TrajectoryReport report,
        bool rotations = false
    ) {
        var origins = new TrajectoryKey[samples.Length];
        var offsets = new TrajectoryKey[samples.Length];
        var points = new SurfacePathKey[samples.Length];
        var turns = new TrajectoryRotationKey[rotations ? samples.Length : 0];
        var surfaced = false;

        for (var index = 0; index < samples.Length; index++) {
            var sample = samples[index];

            origins[index] = new(sample.Phase, sample.Origin);
            offsets[index] = new(sample.Phase, sample.Offset);
            points[index] = new(sample.Phase, sample.Point);
            surfaced |= sample.Point != default;

            if (rotations) {
                turns[index] = new(sample.Phase, sample.Rotation);
            }
        }

        // Halved, because the two errors add when the curves are summed. Saying so is cheaper than
        // having somebody measure a path twice as far out as the tolerance they asked for.
        var half = MathF.Max(tolerance.Position, 0f) * 0.5f;

        var origin = TrajectoryCurve.Decimate(origins, half, out var originReport);
        var offset = TrajectoryCurve.Decimate(offsets, half, out var offsetReport);

        SurfacePath? surface = null;
        TrajectoryRotationCurve? turned = null;
        CurveCompressionReport surfaceReport = default;
        CurveCompressionReport turnReport = default;

        if (surfaced) {
            surface = SurfacePath.Decimate(points, tolerance.Surface, out surfaceReport);
        }

        if (rotations) {
            turned = TrajectoryRotationCurve.Decimate(turns, tolerance.Rotation, out turnReport);
        }

        report = new(originReport, offsetReport, surfaceReport, turnReport);

        return new(origin, offset, surface, turned);
    }
}

/// <summary>A frame that moves through a clip.</summary>
/// <param name="Base">Where the goal is, resolved live every frame.</param>
/// <param name="Path">How it moves.</param>
/// <remarks>
///     <para>
///         A wrapper rather than a sixth kind of frame, because "this goal moves" is orthogonal to
///         "this goal is on a socket" — and every combination of the two is one somebody wants. A
///         trajectory over a <see cref="SurfaceFrame" /> is a hand sliding along a rail; over a
///         <see cref="EntityFrame" /> it is a hand tracking a thrown object; over a
///         <see cref="WorldFrame" /> it is a scripted reach.
///     </para>
///     <para>
///         ⚠ <b>The base frame is resolved live and only the offset is replayed.</b> The authored
///         origin polyline is not played back — a rail that has moved since the clip was captured is
///         the ordinary case, and replaying where it used to be would put the hand in the air.
///     </para>
/// </remarks>
public sealed record TrajectoryFrame(IConstraintFrame Base, GoalTrajectory Path) : IConstraintFrame {
    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        ArgumentNullException.ThrowIfNull(Base);
        ArgumentNullException.ThrowIfNull(Path);

        var phase = context.Phase;
        var source = Base;

        if (Path.HasSurface && Base is SurfaceFrame surface) {
            // The whole of what makes a slide portable: the coordinate is replaced per phase, so the
            // frame resolves somewhere else on the same shape rather than somewhere else in the world.
            source = new SurfaceFrame(surface.Coordinate with { Point = Path.Surface!.Sample(phase) });
        }

        if (!source.TryResolve(context, out frame)) {
            return false;
        }

        var offset = Path.Offset.Sample(phase);
        var rotation = frame.Rotation;

        if (Path.Rotation is { Count: > 0 } turns) {
            rotation = Quaternion.Concatenate(turns.Sample(phase), rotation);
        }

        // The offset is in the frame's axes and in metres — not multiplied by the frame's scale,
        // which for a surface frame is the shape's size. A contact eased two centimetres along a rail
        // is two centimetres on a rail of any radius.
        frame = new(
            new BoneTransform(frame.Origin + frame.DirectionToModel(offset), rotation, frame.Scale)
        );

        return true;
    }
}
