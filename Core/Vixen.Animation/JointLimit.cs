// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation;

/// <summary>How far a joint may turn from where it was modelled.</summary>
/// <remarks>
///     <para>
///         <b>A swing cone and a twist range, about one axis, relative to the bind pose.</b> A
///         shoulder is a wide cone with some twist; an elbow is a narrow cone and almost none; a
///         forearm is the opposite. That is the fidelity a constraint solver can use, and it is the
///         parameterisation that stays well behaved when a correction has to be clamped — which
///         per-axis Euler ranges do not, because clamping three angles in an order changes the answer
///         depending on the order.
///     </para>
///     <para>
///         ⚠ <b>Relative to the bind pose and not to the parent.</b> A rig's bind pose is where the
///         artist put the joint, and "forty degrees from there" is what an artist means. Measuring
///         from the parent's axes would make the same limit mean something different on two rigs that
///         differ only in how their bones are oriented.
///     </para>
///     <para>
///         ⚠ <b><see cref="Free" /> is the default and the zero value is not.</b> A zeroed
///         <see cref="JointLimit" /> is a joint that may not move at all, which is a legitimate thing
///         to author and a catastrophic default — every existing rig would freeze. Nothing reads a
///         limit unless a joint's <c>Limited</c> flag says to, so the two cases cannot be confused.
///     </para>
/// </remarks>
public readonly record struct JointLimit {
    /// <summary>A joint with no limit at all.</summary>
    public static JointLimit Free => new() { Swing = MathF.PI, Twist = MathF.PI };

    /// <summary>How far the twist axis may lean from its bind direction, in radians.</summary>
    public float Swing { get; init; }

    /// <summary>How far the joint may turn about that axis, in radians, either way.</summary>
    public float Twist { get; init; }

    /// <summary>Which way the bone points in the joint's own space. Zero means <c>+Y</c>.</summary>
    /// <remarks>
    ///     Most exporters run a bone down its local Y, which is why that is the default — but the ones
    ///     that do not are common enough that guessing would be worse than a field.
    /// </remarks>
    public Vector3 Axis { get; init; }

    /// <summary>Whether it constrains anything.</summary>
    public bool IsFree => Swing >= MathF.PI - 1e-4f && Twist >= MathF.PI - 1e-4f;

    /// <summary>The twist axis, normalised, with the default filled in.</summary>
    public Vector3 Direction => Axis == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(Axis);

    /// <summary>A cone and a twist, in degrees, which is how an artist says it.</summary>
    /// <param name="swing">The cone's half-angle.</param>
    /// <param name="twist">How far it may turn about its own axis, either way.</param>
    /// <param name="axis">Which way the bone points, or zero for <c>+Y</c>.</param>
    /// <returns>The limit.</returns>
    public static JointLimit Of(float swing, float twist, Vector3 axis = default) =>
        new() {
            Swing = MathUtil.DegreesToRadians(swing),
            Twist = MathUtil.DegreesToRadians(twist),
            Axis = axis
        };

    /// <summary>Brings a rotation back inside the limit.</summary>
    /// <param name="local">The joint's local rotation.</param>
    /// <param name="bind">Its local rotation at bind time.</param>
    /// <param name="clamped">Whether anything was taken off it.</param>
    /// <returns>The rotation, inside the limit.</returns>
    /// <remarks>
    ///     ⚠ <b>Swing and twist are separated before either is clamped.</b> Clamping the whole
    ///     rotation towards the bind pose would pull a joint's twist back every time its swing was
    ///     too wide — a forearm straightening because a shoulder was over-rotated, which reads as the
    ///     solver fighting itself.
    /// </remarks>
    public Quaternion Clamp(Quaternion local, Quaternion bind, out bool clamped) {
        clamped = false;

        if (IsFree) {
            return local;
        }

        var delta = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Conjugate(bind), local));
        var axis = Direction;

        // Swing–twist decomposition: the twist is the part of the rotation about the axis, and the
        // swing is whatever is left once it has been taken off. `Concatenate(a, b)` is "a then b",
        // so a rotation that twists and then swings is `Concatenate(twist, swing)` — and the swing is
        // recovered by taking the twist back off the far end.
        var along = Vector3.Dot(new Vector3(delta.X, delta.Y, delta.Z), axis) * axis;
        var twist = new Quaternion(along.X, along.Y, along.Z, delta.W);

        // A half turn about something square to the axis has no twist component at all, rather than
        // an undefined one — the vector part projects to nothing and W is zero, so this normalises to
        // a division by zero unless it is caught.
        twist = twist.LengthSquared() > 1e-8f ? Quaternion.Normalize(twist) : Quaternion.Identity;

        var swing = Quaternion.Concatenate(Quaternion.Conjugate(twist), delta);
        var limitedSwing = Limit(swing, Swing, out var swungBack);
        var limitedTwist = Limit(twist, Twist, out var turnedBack);

        clamped = swungBack || turnedBack;

        return clamped
            ? Quaternion.Normalize(Quaternion.Concatenate(bind, Quaternion.Concatenate(limitedTwist, limitedSwing)))
            : local;
    }

    /// <summary>The same rotation with its angle brought inside a bound.</summary>
    static Quaternion Limit(Quaternion rotation, float most, out bool clamped) {
        var normalized = Quaternion.Normalize(rotation);
        var w = MathUtil.Clamp(MathF.Abs(normalized.W), -1f, 1f);
        var half = MathF.Acos(w);

        clamped = half * 2f > most + 1e-4f;

        if (!clamped) {
            return rotation;
        }

        var sin = MathF.Sqrt(MathF.Max(1f - (w * w), 0f));

        if (sin <= 1e-6f) {
            return Quaternion.Identity;
        }

        // The sign of W is which way round the shorter arc runs; taking the absolute value above and
        // putting it back here is what keeps a clamp from flipping a rotation to the long way round.
        var sign = normalized.W < 0f ? -1f : 1f;
        var axis = new Vector3(normalized.X * sign / sin, normalized.Y * sign / sin, normalized.Z * sign / sin);

        return Quaternion.FromAxisAngle(axis, most);
    }
}
