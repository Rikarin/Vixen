// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>What a goal asks for. There are four, and the fifth candidate is always a composite.</summary>
public enum GoalKind : byte {
    /// <summary>A joint, or an offset from one, should be at a point.</summary>
    Position,

    /// <summary>A joint's rotation should align with one.</summary>
    Orientation,

    /// <summary>An axis on a joint should point at something.</summary>
    Aim,

    /// <summary>Two joints should be a certain distance apart.</summary>
    Distance
}

/// <summary>Whether a goal says where to be, or how far to move from where the animation put you.</summary>
/// <remarks>
///     ⚠ <b>A decision rather than a variant, and it had to be here from the start.</b> A recoil is
///     an offset from the aim pose and an absolute goal would fight the aim; a secondary-motion or
///     physics pass produces displacements and has no other way to reach the solver; an impact
///     reaction has to compose with whatever else is running. Adding this later would mean every
///     arbiter, every error metric and every piece of temporal state growing a second case, and
///     <see cref="IConstraintArbiter" />'s blend rule being rewritten rather than extended —
///     additive contributions <b>sum</b> where absolute ones average.
/// </remarks>
public enum GoalMode : byte {
    /// <summary>Be here.</summary>
    Absolute,

    /// <summary>Be this far from wherever the animation put you.</summary>
    Additive
}

/// <summary>Which joints a goal is allowed to move.</summary>
/// <param name="First">The joint at the top of the chain — a shoulder, a hip.</param>
/// <param name="Effector">The joint at the end — a wrist, an ankle.</param>
/// <remarks>
///     <b>The chain is the arbitration key.</b> Two goals that move the same joints have to agree
///     with each other; two that do not are independent and never meet. Grouping by chain rather
///     than by effector is what makes a hand goal and an elbow-hint goal one conversation.
/// </remarks>
public readonly record struct ChainSpec(int First, int Effector) : IComparable<ChainSpec> {
    /// <summary>A chain that moves one joint and nothing above it.</summary>
    /// <param name="joint">The joint.</param>
    /// <returns>The chain.</returns>
    public static ChainSpec Single(int joint) => new(joint, joint);

    /// <summary>A chain from a named joint to a named joint.</summary>
    /// <param name="skeleton">The skeleton to look the names up in.</param>
    /// <param name="first">The joint at the top.</param>
    /// <param name="effector">The joint at the end.</param>
    /// <returns>The chain.</returns>
    public static ChainSpec From(Skeleton skeleton, string first, string effector) {
        ArgumentNullException.ThrowIfNull(skeleton);
        return new(skeleton.IndexOf(first), skeleton.IndexOf(effector));
    }

    /// <summary>Whether both ends name a joint at all.</summary>
    public bool IsSome => First >= 0 && Effector >= 0;

    /// <inheritdoc />
    public int CompareTo(ChainSpec other) {
        var order = First.CompareTo(other.First);
        return order != 0 ? order : Effector.CompareTo(other.Effector);
    }

    /// <summary>Orders two chains.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts before the second.</returns>
    public static bool operator <(ChainSpec left, ChainSpec right) => left.CompareTo(right) < 0;

    /// <summary>Orders two chains.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts before or with the second.</returns>
    public static bool operator <=(ChainSpec left, ChainSpec right) => left.CompareTo(right) <= 0;

    /// <summary>Orders two chains.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts after the second.</returns>
    public static bool operator >(ChainSpec left, ChainSpec right) => left.CompareTo(right) > 0;

    /// <summary>Orders two chains.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts after or with the second.</returns>
    public static bool operator >=(ChainSpec left, ChainSpec right) => left.CompareTo(right) >= 0;
}

/// <summary>Which detail levels a goal is worth solving at.</summary>
/// <param name="Min">The nearest level it applies to. Zero is the highest detail.</param>
/// <param name="Max">The furthest level it applies to.</param>
/// <remarks>
///     Dropping out of range eases the goal out rather than snapping it. The governor that decides
///     what a frame can afford is not here — this is the declaration it reads.
/// </remarks>
public readonly record struct LodRange(byte Min, byte Max) {
    /// <summary>Every level.</summary>
    public static LodRange All => new(0, byte.MaxValue);

    /// <summary>Whether a level is in range.</summary>
    /// <param name="lod">The level.</param>
    /// <returns>Whether it is.</returns>
    public bool Contains(byte lod) => lod >= Min && lod <= Max;
}

/// <summary>What a constraint asks the pose to do.</summary>
/// <remarks>
///     <para>
///         A class rather than a struct, and mutable in exactly one place. A goal is created once,
///         lives for as long as the thing that wants it, and is read every frame — so the identity
///         matters, and the only field a frame may change is <see cref="Weight" />, which is what a
///         handle exposes.
///     </para>
///     <para>
///         Everything a clip tag can express, a goal added from game code can express, and they
///         arbitrate together with no special case.
///     </para>
///     <para>
///         ⚠ <b>There is no residual on a goal, and that is not an omission.</b> A goal reached
///         through a clip's track is one object shared by every character playing that clip, so a
///         per-goal error field would have a hundred writers and one value. Residuals belong to the
///         <em>instance</em>: <see cref="ConstraintHandle.Residual" /> for a goal a game added, and
///         <see cref="ConstraintStack.Residual(ConstraintTrack, int)" /> for one a clip carries.
///     </para>
/// </remarks>
public abstract class ConstraintGoal {
    /// <summary>What it asks for.</summary>
    public abstract GoalKind Kind { get; }

    /// <summary>The joint it is about.</summary>
    public required int Effector { get; init; }

    /// <summary>Where the goal is, or <see langword="null" /> for a goal that needs no frame.</summary>
    public IConstraintFrame? Goal { get; init; }

    /// <summary>Which joints may move to satisfy it.</summary>
    /// <remarks>
    ///     Left unset, the goal moves its effector and nothing above it. Almost every real goal sets
    ///     it, because "put the hand there" without an arm to bend is not a request anything can
    ///     satisfy.
    /// </remarks>
    public ChainSpec Chain { get; init; } = new(-1, -1);

    /// <summary>The chain as the solver sees it, with the effector filled in when nobody said.</summary>
    public ChainSpec Solved => Chain.IsSome ? Chain : ChainSpec.Single(Effector);

    /// <summary>Whether it says where to be, or how far to move.</summary>
    public GoalMode Mode { get; init; }

    /// <summary>
    ///     Where an additive offset was measured against, or <see langword="null" /> for the live pose.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The frame an offset is measured against is not the frame it is applied in.</b> A
    ///     recoil is captured against the clip's own first frame and applied against whatever the
    ///     character is currently aiming at, which is what lets it be authored once.
    /// </remarks>
    public IConstraintFrame? Reference { get; init; }

    /// <summary>How much it matters relative to others on the same chain.</summary>
    /// <remarks>
    ///     A multiplier and a tie-break, not a hard ordering. The integer is meaningless on its own;
    ///     what an author picks from is a project's declared ladder.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>What other systems know it as, for querying and suppression.</summary>
    public Symbol Label { get; init; }

    /// <summary>Which detail levels it is worth solving at.</summary>
    public LodRange Lods { get; init; } = LodRange.All;

    /// <summary>The most of it that is ever applied, in <c>[0, 1]</c>.</summary>
    public float MaxWeight { get; init; } = 1f;

    /// <summary>How long it takes to fade in once it resolves, in seconds.</summary>
    public float EaseIn { get; init; } = 0.15f;

    /// <summary>How long it takes to fade out once it stops resolving, in seconds.</summary>
    public float EaseOut { get; init; } = 0.25f;

    /// <summary>How much of it is wanted, in <c>[0, 1]</c>. The one field a frame may change.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Where in its clip it is, for a goal that moves.</summary>
    /// <remarks>
    ///     Written by the stack for a goal a clip carries, and by the game for one it added — a throw
    ///     driven from code is a trajectory whose phase is the game's to advance. Ignored by every
    ///     frame but <see cref="TrajectoryFrame" />.
    /// </remarks>
    public float Phase { get; set; }

    /// <summary>Where the pole for a two-bone solve is, in model space. Zero keeps the current bend.</summary>
    public Vector3 Pole { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Kind}{(Mode is GoalMode.Additive ? "+" : "")} on {Effector} via [{Chain.First}..{Chain.Effector}]";
}

/// <summary>A joint, or an offset from one, should be at a point.</summary>
public sealed class PositionGoal : ConstraintGoal {
    /// <inheritdoc />
    public override GoalKind Kind => GoalKind.Position;

    /// <summary>Where in the goal's frame the point is.</summary>
    /// <remarks>
    ///     For an additive goal this is a displacement, in the frame's own axes, from wherever the
    ///     animation put the effector.
    /// </remarks>
    public Vector3 Offset { get; init; }

    /// <summary>Where on the effector joint the point being placed is.</summary>
    public Vector3 EffectorOffset { get; init; }

    /// <summary>
    ///     Half-extents of the volume the point may be anywhere inside, in the frame's own axes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One branch in the error function, and it buys most of what authored intent actually
    ///     is.</b> "The hand is exactly here" is rarely what somebody meant; "the hand is somewhere on
    ///     this shelf" usually is, and a region says so instead of over-constraining the arm.
    /// </remarks>
    public Vector3 Region { get; init; }

    /// <summary>The nearest acceptable point to one that is offered.</summary>
    /// <param name="frame">The resolved frame.</param>
    /// <param name="from">The point, in model space.</param>
    /// <returns>The nearest point inside the region, in model space.</returns>
    public Vector3 Nearest(in Frame frame, Vector3 from) {
        if (Region == Vector3.Zero) {
            return frame.ToModel(Offset);
        }

        var local = frame.ToFrame(from) - Offset;
        var extent = new Vector3(MathF.Abs(Region.X), MathF.Abs(Region.Y), MathF.Abs(Region.Z));

        return frame.ToModel(Offset + Vector3.Clamp(local, -extent, extent));
    }
}

/// <summary>A joint's rotation should align with one.</summary>
public sealed class OrientationGoal : ConstraintGoal {
    /// <inheritdoc />
    public override GoalKind Kind => GoalKind.Orientation;

    /// <summary>Which rotation, in the goal's frame.</summary>
    /// <remarks>For an additive goal, a rotation composed onto whatever the animation produced.</remarks>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;

    /// <summary>How far off it may be and still count as satisfied, in radians.</summary>
    public float Region { get; init; }
}

/// <summary>An axis on a joint should point at something.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Parameterised by angular deviation and an authored distance, not by a target
///         point.</b> "Point at this position" retargets badly: the same authored deviation applied
///         from a different origin, or at a different distance, either overshoots or falls short, and
///         it is worst exactly where it is most visible — a character spraying past the window it was
///         authored to spray into.
///     </para>
///     <para>
///         So the deviation is applied to the <em>current</em> origin-to-frame vector and then scaled
///         by <see cref="AuthoredDistance" /> over the current distance. Aim at something twice as far
///         and the angular correction halves, which is what keeps the point of aim on the object
///         rather than on the angle.
///     </para>
/// </remarks>
public sealed class AimGoal : ConstraintGoal {
    /// <inheritdoc />
    public override GoalKind Kind => GoalKind.Aim;

    /// <summary>Which way the joint faces in its own space.</summary>
    public Vector3 Axis { get; init; } = Vector3.Forward;

    /// <summary>Where on the joint the aim starts, in the joint's own space.</summary>
    public Vector3 Origin { get; init; }

    /// <summary>How far off the origin-to-frame vector the aim was authored.</summary>
    public Quaternion Deviation { get; init; } = Quaternion.Identity;

    /// <summary>How far away the thing being aimed at was when it was authored, in metres.</summary>
    /// <remarks>Zero means the deviation is applied as it stands, with no rescale.</remarks>
    public float AuthoredDistance { get; init; }

    /// <summary>How far off it may be and still count as satisfied, in radians.</summary>
    public float Region { get; init; }

    /// <summary>Where the aim should end up pointing, given where the frame turned out to be.</summary>
    /// <param name="frame">The resolved frame.</param>
    /// <param name="origin">Where the aim starts, in model space.</param>
    /// <returns>The point to aim at, in model space.</returns>
    public Vector3 Target(in Frame frame, Vector3 origin) {
        var toFrame = frame.Origin - origin;
        var distance = toFrame.Length();

        if (distance <= 1e-5f) {
            return frame.Origin;
        }

        var scale = AuthoredDistance > 0f ? AuthoredDistance / distance : 1f;
        var direction = Quaternion.Transform(toFrame / distance, ScaleRotation(Deviation, scale));

        return origin + (direction * distance);
    }

    /// <summary>The same rotation through a fraction of its angle.</summary>
    /// <param name="rotation">The rotation.</param>
    /// <param name="scale">The fraction. May be more than one.</param>
    /// <returns>The scaled rotation.</returns>
    /// <remarks>
    ///     Not a <c>Slerp</c> from the identity, because the scale is routinely greater than one — a
    ///     target closer than the clip was authored against needs a <em>larger</em> correction — and
    ///     extrapolating a spherical interpolation past its ends is not something to rely on. Axis
    ///     and angle, multiplied, rebuilt.
    /// </remarks>
    public static Quaternion ScaleRotation(Quaternion rotation, float scale) {
        var normalized = Quaternion.Normalize(rotation);
        var w = MathUtil.Clamp(normalized.W, -1f, 1f);
        var half = MathF.Acos(w);
        var sin = MathF.Sqrt(MathF.Max(1f - (w * w), 0f));

        if (sin <= 1e-6f) {
            return Quaternion.Identity;
        }

        var axis = new Vector3(normalized.X / sin, normalized.Y / sin, normalized.Z / sin);
        return Quaternion.FromAxisAngle(axis, half * 2f * scale);
    }
}

/// <summary>Two joints should be a certain distance apart.</summary>
/// <remarks>
///     The one goal with no frame: both ends are joints on the same body. Two hands on a rifle, a
///     hand that must not leave a rail, a chin that must clear a shoulder.
/// </remarks>
public sealed class DistanceGoal : ConstraintGoal {
    /// <inheritdoc />
    public override GoalKind Kind => GoalKind.Distance;

    /// <summary>The other joint.</summary>
    public required int Other { get; init; }

    /// <summary>The closest they may be, in metres.</summary>
    /// <remarks>For an additive goal, an offset applied to the animated separation.</remarks>
    public float Min { get; init; }

    /// <summary>The furthest they may be, in metres.</summary>
    public float Max { get; init; } = float.PositiveInfinity;

    /// <summary>How far outside the interval a separation is, signed.</summary>
    /// <param name="separation">The separation, in metres.</param>
    /// <returns>Negative if too close, positive if too far, zero inside.</returns>
    public float Excess(float separation) =>
        separation < Min ? separation - Min
        : separation > Max ? separation - Max
        : 0f;
}
