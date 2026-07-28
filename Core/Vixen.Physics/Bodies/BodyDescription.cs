// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Physics.Shapes;

namespace Vixen.Physics.Bodies;

/// <summary>How a body is moved.</summary>
public enum BodyMotion {
    /// <summary>Never moves. Level geometry. Cheapest by a wide margin.</summary>
    /// <remarks>
    ///     Two static bodies are never tested against one another, so a static body is not merely a
    ///     dynamic one with infinite mass — it is in a different broad-phase tree.
    /// </remarks>
    Static,

    /// <summary>
    ///     Moved by whoever owns it, and pushes dynamic bodies out of the way without being pushed
    ///     back. Lifts, doors, moving platforms.
    /// </summary>
    /// <remarks>
    ///     Move one with <see cref="PhysicsWorld.MoveKinematic" /> rather than by setting its
    ///     position: teleporting gives it no velocity, so anything standing on it is left behind and
    ///     anything in front of it is passed through rather than pushed.
    /// </remarks>
    Kinematic,

    /// <summary>Moved by the solver: gravity, contacts, forces.</summary>
    Dynamic
}

/// <summary>How carefully a body's motion is swept for collisions.</summary>
public enum BodyMotionQuality {
    /// <summary>
    ///     Tested where it starts and where it ends. Fast, and correct for anything that moves less
    ///     than its own thickness in a step.
    /// </summary>
    Discrete,

    /// <summary>
    ///     Swept along its motion — continuous collision detection. For the bullet and the thrown
    ///     grenade, which at sixty steps a second cross a wall between one position and the next.
    /// </summary>
    /// <remarks>
    ///     Costs a linear cast per step per body, so it is opt-in per body rather than a world
    ///     setting. Turning it on for everything is the reliable way to make a scene that ran at 300
    ///     fps run at 60.
    /// </remarks>
    Continuous
}

/// <summary>Which axes a body is allowed to move and turn about.</summary>
/// <remarks>
///     A 2D game is <see cref="Plane2D" />; an upright character that must not topple is
///     <see cref="Translation" /> with the rotations left out. Locking a degree of freedom here
///     rather than by zeroing velocity every step means the solver never generates the motion in the
///     first place, so a stack of constrained bodies does not fight itself.
/// </remarks>
[Flags]
public enum BodyDegreesOfFreedom {
    /// <summary>Nothing. The body cannot move at all, though it is still solved against.</summary>
    None = 0,

    /// <summary>May move along X.</summary>
    TranslationX = 1 << 0,

    /// <summary>May move along Y.</summary>
    TranslationY = 1 << 1,

    /// <summary>May move along Z.</summary>
    TranslationZ = 1 << 2,

    /// <summary>May turn about X.</summary>
    RotationX = 1 << 3,

    /// <summary>May turn about Y.</summary>
    RotationY = 1 << 4,

    /// <summary>May turn about Z.</summary>
    RotationZ = 1 << 5,

    /// <summary>May move along any axis.</summary>
    Translation = TranslationX | TranslationY | TranslationZ,

    /// <summary>May turn about any axis.</summary>
    Rotation = RotationX | RotationY | RotationZ,

    /// <summary>Everything. The default.</summary>
    All = Translation | Rotation,

    /// <summary>The X–Y plane, turning only about Z. What a 2D game wants.</summary>
    Plane2D = TranslationX | TranslationY | RotationZ
}

/// <summary>Everything needed to create a body, in one value.</summary>
/// <remarks>
///     <para>
///         A record struct with initialisers rather than a builder or a long constructor: a body has
///         a dozen knobs, eleven of which are almost always their default, and
///         <c>new BodyDescription { Shape = box, Motion = BodyMotion.Dynamic }</c> says exactly what
///         is unusual about this one and nothing else.
///     </para>
///     <para>
///         The defaults are Jolt's, except <see cref="Friction" /> and <see cref="Restitution" />,
///         which are Jolt's too — 0.2 and 0 — and are called out because they are the two people
///         reach for first and the two whose defaults are least guessable.
///     </para>
/// </remarks>
[DataContract]
public record struct BodyDescription {
    /// <summary>The collision volume. Required.</summary>
    public ShapeId Shape { get; init; }

    /// <summary>Where the body starts.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Which way it starts.</summary>
    public Quaternion Rotation { get; init; }

    /// <summary>How it moves.</summary>
    public BodyMotion Motion { get; init; }

    /// <summary>How carefully its motion is swept.</summary>
    public BodyMotionQuality MotionQuality { get; init; }

    /// <summary>Which layer it is on, and so what it collides with.</summary>
    public PhysicsLayer Layer { get; init; }

    /// <summary>Its starting linear velocity, in metres a second.</summary>
    public Vector3 LinearVelocity { get; init; }

    /// <summary>Its starting angular velocity, in radians a second about each axis.</summary>
    public Vector3 AngularVelocity { get; init; }

    /// <summary>
    ///     Its mass in kilograms, or zero to take the shape's — density times volume.
    /// </summary>
    /// <remarks>
    ///     Overriding the mass keeps the shape's inertia <i>distribution</i> and scales it, which is
    ///     what somebody who types "80" for a character means. Only a body that needs a specific
    ///     inertia tensor wants anything else, and that is a constraint problem rather than a body one.
    /// </remarks>
    public float Mass { get; init; }

    /// <summary>How much of the world's gravity applies. One is all of it; zero is none.</summary>
    public float GravityFactor { get; init; }

    /// <summary>Friction, roughly 0 (ice) to 1 (rubber). Combined with the other body's.</summary>
    public float Friction { get; init; }

    /// <summary>Bounciness, 0 (clay) to 1 (perfectly elastic). Combined with the other body's.</summary>
    public float Restitution { get; init; }

    /// <summary>How quickly linear motion bleeds away, per second.</summary>
    public float LinearDamping { get; init; }

    /// <summary>How quickly angular motion bleeds away, per second.</summary>
    public float AngularDamping { get; init; }

    /// <summary>Which axes it may move and turn about.</summary>
    public BodyDegreesOfFreedom DegreesOfFreedom { get; init; }

    /// <summary>
    ///     Whether the body reports overlaps instead of resolving them — a trigger volume.
    /// </summary>
    /// <remarks>
    ///     A sensor still needs a motion type and a layer: a trigger bolted to the level is static, a
    ///     trigger carried by a lift is kinematic, and the layer is what decides whose entry it
    ///     notices.
    /// </remarks>
    public bool IsSensor { get; init; }

    /// <summary>Whether the body may fall asleep when it stops moving.</summary>
    /// <remarks>
    ///     On for everything by default, and the single largest reason a scene of a thousand settled
    ///     crates costs nothing. Turn it off only for a body whose motion is driven from outside the
    ///     solver and which would otherwise be asleep when the push arrives.
    /// </remarks>
    public bool AllowSleeping { get; init; }

    /// <summary>Whatever the owner wants to associate with the body. The ECS bridge puts an entity here.</summary>
    public ulong UserData { get; init; }

    /// <summary>A description with the engine's defaults, which is everything but a shape.</summary>
    /// <returns>The description.</returns>
    /// <remarks>
    ///     A method rather than <c>default</c>, because a zeroed description has an identity rotation
    ///     of all zeroes, no gravity, no friction and no sleeping — four wrong answers that all look
    ///     like the physics being broken rather than like a value not being set.
    /// </remarks>
    public static BodyDescription Create() => new() {
        Rotation = Quaternion.Identity,
        Motion = BodyMotion.Dynamic,
        MotionQuality = BodyMotionQuality.Discrete,
        Layer = PhysicsLayer.Default,
        GravityFactor = 1f,
        Friction = 0.2f,
        Restitution = 0f,
        DegreesOfFreedom = BodyDegreesOfFreedom.All,
        AllowSleeping = true
    };

    /// <summary>A dynamic body with a shape, at a place.</summary>
    /// <param name="shape">Its collision volume.</param>
    /// <param name="position">Where it starts.</param>
    /// <returns>The description.</returns>
    public static BodyDescription Dynamic(ShapeId shape, Vector3 position) =>
        Create() with { Shape = shape, Position = position };

    /// <summary>A static body with a shape, at a place.</summary>
    /// <param name="shape">Its collision volume.</param>
    /// <param name="position">Where it is.</param>
    /// <returns>The description.</returns>
    public static BodyDescription Static(ShapeId shape, Vector3 position) =>
        Create() with { Shape = shape, Position = position, Motion = BodyMotion.Static };

    /// <summary>A kinematic body with a shape, at a place.</summary>
    /// <param name="shape">Its collision volume.</param>
    /// <param name="position">Where it starts.</param>
    /// <returns>The description.</returns>
    public static BodyDescription Kinematic(ShapeId shape, Vector3 position) =>
        Create() with { Shape = shape, Position = position, Motion = BodyMotion.Kinematic };

    /// <summary>A sensor — a volume that reports what enters it and stops nothing.</summary>
    /// <param name="shape">Its volume.</param>
    /// <param name="position">Where it is.</param>
    /// <returns>The description.</returns>
    public static BodyDescription Trigger(ShapeId shape, Vector3 position) =>
        Create() with {
            Shape = shape,
            Position = position,
            Motion = BodyMotion.Static,
            IsSensor = true
        };
}
