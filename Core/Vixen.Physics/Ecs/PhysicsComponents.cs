// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Physics.Bodies;
using Vixen.Physics.Shapes;

namespace Vixen.Physics.Ecs;

/// <summary>
///     The collision volume an entity has, and what it is made of.
/// </summary>
/// <remarks>
///     <para>
///         The shape is a <see cref="ShapeId" /> into <c>PhysicsWorld.Shapes</c> rather than a
///         reference, which is what keeps this a plain struct in a chunk column instead of a managed
///         component behind a handle indirection — see <c>ManagedComponentStore</c> for why that
///         matters, and <see cref="ShapeId" /> for why it is also the form a scene file and a network
///         message want.
///     </para>
///     <para>
///         An entity with a <see cref="Collider" /> and no <see cref="RigidBody" /> gets a static
///         body. That is the common case by a wide margin — a level is mostly collision geometry that
///         never moves — and making it the one that needs no second component is what stops every
///         piece of scenery from carrying a <c>RigidBody { Motion = Static }</c>.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct Collider {
    /// <summary>The volume.</summary>
    public ShapeId Shape;

    /// <summary>Which layer it is on, and so what it collides with.</summary>
    public PhysicsLayer Layer;

    /// <summary>Friction, roughly 0 (ice) to 1 (rubber).</summary>
    public float Friction;

    /// <summary>Bounciness, 0 (clay) to 1 (perfectly elastic).</summary>
    public float Restitution;

    /// <summary>Whether it reports overlaps instead of resolving them.</summary>
    public bool IsSensor;

    /// <summary>A collider with the engine's default material.</summary>
    /// <param name="shape">The volume.</param>
    /// <returns>The component.</returns>
    /// <remarks>
    ///     A factory rather than <c>default</c>, because a zeroed collider is frictionless — which is
    ///     not obviously wrong until a box slides off a level surface for no reason anyone can see.
    /// </remarks>
    public static Collider Of(ShapeId shape) => new() { Shape = shape, Friction = 0.2f };

    /// <summary>A trigger volume.</summary>
    /// <param name="shape">The volume.</param>
    /// <returns>The component.</returns>
    public static Collider Trigger(ShapeId shape) => new() { Shape = shape, IsSensor = true };
}

/// <summary>
///     Makes an entity's collider move: under the solver, or under whoever owns it.
/// </summary>
/// <remarks>
///     Only meaningful beside a <see cref="Collider" />. On its own it does nothing, because there is
///     nothing to simulate — which is the same rule <c>WorldTransform</c> has about
///     <c>LocalTransform</c>, and for the same reason.
/// </remarks>
[Component]
[DataContract]
public struct RigidBody {
    /// <summary>How it moves.</summary>
    public BodyMotion Motion;

    /// <summary>How carefully its motion is swept.</summary>
    public BodyMotionQuality MotionQuality;

    /// <summary>Its mass in kilograms, or zero to take the shape's.</summary>
    public float Mass;

    /// <summary>How much of the world's gravity applies.</summary>
    public float GravityFactor;

    /// <summary>How quickly linear motion bleeds away, per second.</summary>
    public float LinearDamping;

    /// <summary>How quickly angular motion bleeds away, per second.</summary>
    public float AngularDamping;

    /// <summary>Which axes it may move and turn about.</summary>
    public BodyDegreesOfFreedom DegreesOfFreedom;

    /// <summary>Whether it may fall asleep when it stops moving.</summary>
    public bool AllowSleeping;

    /// <summary>A dynamic body with the engine's defaults.</summary>
    /// <returns>The component.</returns>
    public static RigidBody Dynamic() => new() {
        Motion = BodyMotion.Dynamic,
        GravityFactor = 1f,
        DegreesOfFreedom = BodyDegreesOfFreedom.All,
        AllowSleeping = true
    };

    /// <summary>A kinematic body — moved by its owner, and pushing whatever is in the way.</summary>
    /// <returns>The component.</returns>
    public static RigidBody Kinematic() => Dynamic() with { Motion = BodyMotion.Kinematic };
}

/// <summary>
///     The body an entity has in the physics world. Written by the bridge, never by game code.
/// </summary>
/// <remarks>
///     <para>
///         Present exactly while a body exists, so "has a body" is an archetype question and the
///         bridge finds the entities that still need one with <c>WithNone&lt;PhysicsBody&gt;</c>
///         rather than by scanning for a sentinel.
///     </para>
///     <para>
///         Deliberately not <c>[DataContract]</c>. A body handle means nothing outside the world that
///         issued it, so saving one into a scene file would restore a handle to whatever body
///         happened to take that slot — the same reason <c>Entity</c> is never written to a scene.
///         The bridge rebuilds this from <see cref="Collider" /> on the first sync after a load.
///     </para>
///     <para>
///         ⚠ <b>That absence is also what keeps it out of <c>SceneComponentRegistry</c>, and it is
///         load-bearing.</b> A scene component is one carrying <c>[Component]</c> <i>and</i>
///         <c>[DataContract]</c>; this deliberately has neither, so nothing has to remember to exclude
///         it. Adding either to make it inspectable would make a compiled scene able to name it, which
///         is the failure the paragraph above describes.
///     </para>
/// </remarks>
public struct PhysicsBody {
    /// <summary>The body.</summary>
    public BodyHandle Handle;

    /// <summary>
    ///     What the body was built from, so the bridge can tell a component edit from a no-op.
    /// </summary>
    /// <remarks>
    ///     Cheaper than asking Jolt: the alternative to remembering the shape and the motion type is
    ///     a native call per body per step to find out that nothing changed.
    /// </remarks>
    public ShapeId BuiltShape;

    /// <summary>The motion type the body was built with.</summary>
    public BodyMotion BuiltMotion;

    /// <summary>Whether the body was built as a sensor.</summary>
    public bool BuiltSensor;
}

/// <summary>How fast an entity's body is moving, in metres a second.</summary>
/// <remarks>
///     A mirror of the body's velocity: read after the step, written into the body before it. Present
///     so gameplay code can push a body around without holding a <see cref="PhysicsWorld" /> or a
///     handle, and so the velocity is a component a query can filter and a network layer can replicate.
/// </remarks>
[Component]
[DataContract]
public struct LinearVelocity {
    /// <summary>The velocity.</summary>
    public Vector3 Value;
}

/// <summary>How fast an entity's body is turning, in radians a second about each axis.</summary>
[Component]
[DataContract]
public struct AngularVelocity {
    /// <summary>The velocity.</summary>
    public Vector3 Value;
}

/// <summary>
///     Where the body was at the last two steps, so the renderer can draw somewhere between them.
/// </summary>
/// <remarks>
///     <para>
///         A 60 Hz simulation drawn at 144 Hz shows each step twice and then once, which reads as a
///         stutter no amount of frame pacing fixes — the object really is in the same place for two
///         frames. Interpolating between the last two steps by the accumulator's alpha is what makes
///         it smooth, and it is why <c>FixedStepAccumulator</c> has an <c>Alpha</c> at all.
///     </para>
///     <para>
///         The cost is that what is drawn is always one step behind the simulation. That is the right
///         trade for everything except a camera bolted to the player, which should read the body
///         directly.
///     </para>
/// </remarks>
public struct PhysicsInterpolation {
    /// <summary>Where the body was at the end of the previous step.</summary>
    public Vector3 PreviousPosition;

    /// <summary>Which way it faced at the end of the previous step.</summary>
    public Quaternion PreviousRotation;

    /// <summary>Where the body is at the end of the current step.</summary>
    public Vector3 CurrentPosition;

    /// <summary>Which way it faces at the end of the current step.</summary>
    public Quaternion CurrentRotation;

    /// <summary>The position <c>PhysicsInterpolationSystem</c> last wrote onto the transform.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Provenance, and it is what keeps a character from walking at half speed.</b> This
    ///         smoothing writes <c>LocalTransform</c>, and <c>LocalTransform</c> is an <i>input</i> to
    ///         a <c>CharacterMovement</c>: <c>PhysicsScene.Adopt</c> reads it at the top of
    ///         every step and takes anything that disagrees with the controller as a teleport, because
    ///         that is how a respawn, a checkpoint and a rollback reach a <c>CharacterController</c> at
    ///         all. With <c>alpha</c> at zero — every frame whose delta is one fixed step — the pose
    ///         written here is the <i>previous</i> step's, so the adopt undid every other step and a
    ///         4.5 m/s walk covered 2.25.
    ///     </para>
    ///     <para>
    ///         Recording the exact value written is what lets the bridge tell its own smoothing's pose
    ///         from a pose somebody else wrote, without guessing from geometry. The obvious guess — is
    ///         the transform on the segment between the two poses — swallows the commonest correction
    ///         there is, because a client that mispredicts along its own path is corrected <i>along
    ///         that segment</i>.
    ///     </para>
    ///     <para>
    ///         Position and no rotation, because a character's rotation is owned by its controller and
    ///         a written one is not adopted. A rigid body never reads this at all.
    ///     </para>
    /// </remarks>
    public Vector3 DrawnPosition;
}

/// <summary>
///     Marks an entity whose transform was moved by something other than physics, so the bridge
///     pushes it into the body instead of reading it back.
/// </summary>
/// <remarks>
///     <para>
///         Teleporting is the one case the change-version machinery cannot decide on its own. The
///         bridge writes <c>LocalTransform</c> every step for every dynamic body, so "the transform
///         changed since I last looked" is true every step and says nothing about who changed it.
///         This tag is the answer: game code that sets a transform directly adds it, the bridge acts
///         on it and removes it.
///     </para>
///     <para>
///         A kinematic body does not need this — its transform is authored by definition, and the
///         bridge drives it towards <c>LocalTransform</c> every step.
///     </para>
///     <para>
///         ⚠ <b>It is also what stops the body being drawn sliding there.</b>
///         <see cref="PhysicsInterpolation" /> holds the last two simulated poses and a teleport makes
///         those two the two ends of the jump, so <c>PhysicsScene.Arrive</c> puts both of them on the
///         destination. Without it a teleported body crossed the level over the following step, and on
///         a frame one fixed step long it was drawn at the position it had just left.
///     </para>
///     <para>
///         ⚠ <b>Which is why this is a tag and not a distance the bridge could measure.</b> A body
///         genuinely moving at two hundred metres a second covers the same gap in a step; any
///         threshold that catches one catches the other. Only the caller knows.
///     </para>
///     <para>
///         <b>A character does not need it, and now consumes it anyway.</b> Writing a character's
///         <c>LocalTransform</c> <i>is</i> the teleport — <c>PhysicsScene.Adopt</c> is what makes
///         that true — so the tag adds nothing to the ordinary case. What it does add is certainty in
///         the one case Adopt is unsure about: a teleport that lands exactly where the smoothing last
///         drew the character is indistinguishable from the smoothing's own write, and is refused
///         without it.
///     </para>
///     <para>
///         ⚠ <b>It used to be inert there, which meant it stayed on for ever.</b> The bridge acts on
///         this while walking the entities that have a <see cref="PhysicsBody" />, and a character has
///         a <c>CharacterBody</c> instead — so a tag put on one was neither read nor taken off again,
///         and every consumer that treats it as an event rather than a state then fired on every
///         tick: <c>NetworkRigidBodyCaptureSystem</c> re-adds <c>NetworkTeleport</c>, the transform
///         bridge bumps <c>NetworkTransform.TeleportCount</c>, and a receiver refuses to interpolate
///         that entity again. <c>PhysicsScene.StepCharacters</c> takes it off.
///     </para>
/// </remarks>
public struct PhysicsTeleport : ITagComponent;
