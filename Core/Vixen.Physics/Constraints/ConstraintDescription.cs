// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;

namespace Vixen.Physics.Constraints;

/// <summary>A constraint in a <see cref="PhysicsWorld" />.</summary>
/// <param name="Value">The world's own id for it. Zero is no constraint.</param>
/// <remarks>
///     The world's id and not Jolt's, because Jolt identifies a constraint by the pointer to it and a
///     freed pointer is reused as readily as a body index — with none of the sequence-number
///     machinery that makes <see cref="BodyHandle" /> safe. A monotonically increasing id never
///     repeats, so a stale handle is always caught.
/// </remarks>
[DataContract]
public readonly record struct ConstraintHandle(uint Value) {
    /// <summary>No constraint.</summary>
    public static ConstraintHandle None => default;

    /// <summary>Whether this names a constraint at all.</summary>
    public bool IsNone => Value == 0;

    /// <summary>Renders the handle.</summary>
    /// <returns>The handle in text.</returns>
    public override string ToString() => IsNone ? "constraint none" : $"constraint #{Value}";
}

/// <summary>Which joint a description asks for.</summary>
public enum ConstraintKind {
    /// <summary>Welds two bodies together. No relative motion at all.</summary>
    Fixed,

    /// <summary>A ball joint: the two points coincide, and rotation is free.</summary>
    Point,

    /// <summary>A door hinge: rotation about one axis, optionally limited and optionally motorised.</summary>
    Hinge,

    /// <summary>A piston: translation along one axis, optionally limited and optionally motorised.</summary>
    Slider,

    /// <summary>A rope or a spring: the two points are held between a minimum and a maximum distance.</summary>
    Distance,

    /// <summary>A cone: the twist axes are held within a half-angle of one another.</summary>
    Cone
}

/// <summary>How a constraint's limits are sprung.</summary>
/// <param name="Frequency">
///     How stiff, in hertz. Zero is a hard limit, which is what a hinge stop wants.
/// </param>
/// <param name="Damping">
///     How quickly it settles, from 0 (rings for ever) to 1 (critically damped).
/// </param>
public readonly record struct ConstraintSpring(float Frequency, float Damping) {
    /// <summary>No spring. The limit is hard.</summary>
    public static ConstraintSpring Hard => default;

    /// <summary>A spring.</summary>
    /// <param name="frequency">How stiff, in hertz.</param>
    /// <param name="damping">How quickly it settles.</param>
    /// <returns>The settings.</returns>
    public static ConstraintSpring Soft(float frequency, float damping = 0.5f) => new(frequency, damping);
}

/// <summary>How a constraint's motor is driving, if it has one.</summary>
public enum ConstraintMotor {
    /// <summary>Not driving. The joint is free within its limits.</summary>
    Off,

    /// <summary>Driving towards a target velocity — a wheel, a conveyor, a fan.</summary>
    Velocity,

    /// <summary>Driving towards a target position or angle — a servo, a powered door.</summary>
    Position
}

/// <summary>Everything needed to create a constraint, in one value.</summary>
/// <remarks>
///     <para>
///         One description for six joints rather than six types, because they overwhelmingly share
///         parameters — two bodies, two anchor points, an axis, a pair of limits, a spring and a
///         motor — and the alternative is six near-identical records plus the dispatch to tell them
///         apart. What each field means for each kind is on the field.
///     </para>
///     <para>
///         <b>Anchors are in world space.</b> Jolt will take either, and world space is the one an
///         author can reason about: the hinge is where the hinge visibly is, and Jolt converts to
///         body-local at creation using the poses the bodies have at that moment. The consequence is
///         that a constraint must be created after both bodies are where they belong.
///     </para>
/// </remarks>
[DataContract]
public record struct ConstraintDescription {
    /// <summary>Which joint.</summary>
    public ConstraintKind Kind { get; init; }

    /// <summary>One of the two bodies.</summary>
    public BodyHandle First { get; init; }

    /// <summary>The other. May be <see cref="BodyHandle.None" /> to pin to the world.</summary>
    /// <remarks>
    ///     Pinning to the world is what makes a swinging sign or a fixed hinge possible without a
    ///     dummy static body. Jolt spells it as a null body, and this spells it as no handle.
    /// </remarks>
    public BodyHandle Second { get; init; }

    /// <summary>Whether the two bodies stop colliding with one another.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Off by default, which is what every existing joint in the engine already gets.</b>
    ///         Turning it on is what a ragdoll's every joint wants, and a door's hinge usually: two
    ///         bodies held a fixed distance apart by a solver, and also pushed apart by a contact,
    ///         fight each other for as long as the joint exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a suppression of the pair and not a property of the joint</b>, which is
    ///         visible in two places. Destroying the constraint hands the pair back to whatever
    ///         <see cref="PhysicsWorld.SetPairCollision" /> last said about it rather than to
    ///         "colliding" — and a second joint over the same pair keeps it suppressed on its own.
    ///         Both follow from Jolt doing this per body pair rather than per constraint, which is
    ///         also why the pairs a ragdoll needs that have no joint at all —
    ///         the two thighs, an upper arm and the chest — are reachable only through
    ///         <see cref="PhysicsWorld.SetPairCollision" />.
    ///     </para>
    ///     <para>
    ///         Ignored when <see cref="Second" /> is <see cref="BodyHandle.None" />: the world anchor
    ///         is a static body with no shape, so there was never a contact to suppress.
    ///     </para>
    /// </remarks>
    public bool SuppressPairCollision { get; init; }

    /// <summary>Where the joint is on the first body, in world space.</summary>
    public Vector3 FirstAnchor { get; init; }

    /// <summary>Where the joint is on the second body, in world space.</summary>
    /// <remarks>
    ///     Usually the same point as <see cref="FirstAnchor" />. Giving them different values is how
    ///     a joint is created with a deliberate offset already in it.
    /// </remarks>
    public Vector3 SecondAnchor { get; init; }

    /// <summary>
    ///     The axis the joint turns about (hinge, cone) or slides along (slider), in world space.
    /// </summary>
    public Vector3 Axis { get; init; }

    /// <summary>
    ///     The lower limit: an angle in radians for a hinge, a distance in metres for a slider or a
    ///     distance joint. Ignored by the kinds that have no limits.
    /// </summary>
    public float LimitMinimum { get; init; }

    /// <summary>The upper limit, in the same units as <see cref="LimitMinimum" />.</summary>
    public float LimitMaximum { get; init; }

    /// <summary>The half-angle of a cone constraint, in radians.</summary>
    public float HalfConeAngle { get; init; }

    /// <summary>How the limits are sprung.</summary>
    public ConstraintSpring Spring { get; init; }

    /// <summary>Whether the motor is driving, and towards what kind of target.</summary>
    public ConstraintMotor Motor { get; init; }

    /// <summary>
    ///     What the motor is driving towards: a velocity in metres or radians a second, or a position
    ///     in metres or radians, depending on <see cref="Motor" />.
    /// </summary>
    public float MotorTarget { get; init; }

    /// <summary>The most force or torque the motor may use. Zero means unlimited.</summary>
    public float MotorMaximum { get; init; }

    /// <summary>Welds two bodies together where they stand.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or none to pin to the world.</param>
    /// <param name="anchor">Where the weld is, in world space.</param>
    /// <returns>The description.</returns>
    public static ConstraintDescription Fixed(BodyHandle first, BodyHandle second, Vector3 anchor) =>
        new() {
            Kind = ConstraintKind.Fixed,
            First = first,
            Second = second,
            FirstAnchor = anchor,
            SecondAnchor = anchor
        };

    /// <summary>A ball joint.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or none to pin to the world.</param>
    /// <param name="anchor">Where the joint is, in world space.</param>
    /// <returns>The description.</returns>
    public static ConstraintDescription Point(BodyHandle first, BodyHandle second, Vector3 anchor) =>
        new() {
            Kind = ConstraintKind.Point,
            First = first,
            Second = second,
            FirstAnchor = anchor,
            SecondAnchor = anchor
        };

    /// <summary>A hinge, free to turn all the way round.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or none to pin to the world.</param>
    /// <param name="anchor">Where the hinge is, in world space.</param>
    /// <param name="axis">Which way its pin points, in world space.</param>
    /// <returns>The description.</returns>
    public static ConstraintDescription Hinge(
        BodyHandle first,
        BodyHandle second,
        Vector3 anchor,
        Vector3 axis
    ) =>
        new() {
            Kind = ConstraintKind.Hinge,
            First = first,
            Second = second,
            FirstAnchor = anchor,
            SecondAnchor = anchor,
            Axis = axis,
            LimitMinimum = -MathUtil.Pi,
            LimitMaximum = MathUtil.Pi
        };

    /// <summary>A slider, free along its whole axis.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or none to pin to the world.</param>
    /// <param name="anchor">Where the joint is, in world space.</param>
    /// <param name="axis">Which way it slides, in world space.</param>
    /// <returns>The description.</returns>
    public static ConstraintDescription Slider(
        BodyHandle first,
        BodyHandle second,
        Vector3 anchor,
        Vector3 axis
    ) =>
        new() {
            Kind = ConstraintKind.Slider,
            First = first,
            Second = second,
            FirstAnchor = anchor,
            SecondAnchor = anchor,
            Axis = axis,
            LimitMinimum = float.MinValue,
            LimitMaximum = float.MaxValue
        };

    /// <summary>A rope: two points held no further apart than a maximum.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or none to pin to the world.</param>
    /// <param name="firstAnchor">Where it attaches to the first, in world space.</param>
    /// <param name="secondAnchor">Where it attaches to the second, in world space.</param>
    /// <param name="minimum">The shortest it may be.</param>
    /// <param name="maximum">The longest it may be.</param>
    /// <returns>The description.</returns>
    public static ConstraintDescription Distance(
        BodyHandle first,
        BodyHandle second,
        Vector3 firstAnchor,
        Vector3 secondAnchor,
        float minimum,
        float maximum
    ) =>
        new() {
            Kind = ConstraintKind.Distance,
            First = first,
            Second = second,
            FirstAnchor = firstAnchor,
            SecondAnchor = secondAnchor,
            LimitMinimum = minimum,
            LimitMaximum = maximum
        };

    /// <summary>A cone: the second body's twist axis held within a half-angle of the first's.</summary>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or none to pin to the world.</param>
    /// <param name="anchor">Where the joint is, in world space.</param>
    /// <param name="twistAxis">The axis the cone is around, in world space.</param>
    /// <param name="halfConeAngle">How far off it may lean, in radians.</param>
    /// <returns>The description.</returns>
    public static ConstraintDescription Cone(
        BodyHandle first,
        BodyHandle second,
        Vector3 anchor,
        Vector3 twistAxis,
        float halfConeAngle
    ) =>
        new() {
            Kind = ConstraintKind.Cone,
            First = first,
            Second = second,
            FirstAnchor = anchor,
            SecondAnchor = anchor,
            Axis = twistAxis,
            HalfConeAngle = halfConeAngle
        };
}
