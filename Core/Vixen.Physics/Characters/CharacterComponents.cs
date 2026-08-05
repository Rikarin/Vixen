// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Physics.Shapes;

namespace Vixen.Physics.Characters;

/// <summary>How a character is moving, which decides what the rules do to its velocity.</summary>
/// <remarks>
///     <para>
///         A field rather than four systems, because the modes share almost everything — the same
///         acceleration towards the same wanted velocity, differing in what gravity does and what the
///         wanted velocity is built from. Four systems would be four copies of the shared part and
///         four places for a transition to be forgotten.
///     </para>
///     <para>
///         ⚠ <b>Appended, never reordered.</b> This is a <c>byte</c> in a component and therefore in
///         every saved scene and on the wire; inserting a member renumbers the ones after it, and a
///         scene saved before the change loads with its characters flying.
///     </para>
/// </remarks>
public enum CharacterMoveMode : byte {
    /// <summary>On the ground: gravity is cancelled and the ground's own motion is carried.</summary>
    Walking,

    /// <summary>In the air: gravity applies and control is reduced to <see cref="CharacterMovement.AirAcceleration" />.</summary>
    Falling,

    /// <summary>No gravity, and the look pitch steers. A noclip camera, a drone, a jetpack.</summary>
    Flying,

    /// <summary>In water: buoyancy replaces gravity, and the look pitch steers the dive.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fourth member, and the row
    ///         [29 § Where this stops](../../../docs/plan/29-players-and-possession.md) left open.</b>
    ///         That row said "no swimming — it needs water volumes, which do not exist, and a mode that
    ///         could never be entered would be a promise in an enum". They exist now
    ///         ([35 § D11](../../../docs/plan/35-water.md#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number)),
    ///         so the promise is kept rather than made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Entered and left on two thresholds with a gap, never one.</b> A character standing
    ///         in chest-deep water with a 30 cm swell crosses any single threshold twice a second, and
    ///         the symptom is an animation state machine that stutters between wade and swim — see
    ///         <see cref="CharacterMovement.SwimThreshold" />.
    ///     </para>
    ///     <para>
    ///         <b><c>MoveIntent</c> is unchanged, and that is the point.</b> A swimming character
    ///         produces the same intent a walking one does, so nothing about the network path changes —
    ///         which is what a good seam is for. Diving is the existing vertical axis.
    ///     </para>
    /// </remarks>
    Swimming
}

/// <summary>How a character walks. The authored half.</summary>
/// <remarks>
///     <para>
///         Beside a <see cref="CharacterMovement" />, <c>PhysicsScene</c> gives an entity a
///         <see cref="CharacterController" /> and drives it from the entity's <c>MoveIntent</c> — so
///         a walking player is this component, a <c>MoveIntent</c>, and nothing else. What writes the
///         intent is not this assembly's business: a player controller, an AI planner and a replay
///         are indistinguishable from here, which is the point of the seam
///         ([29](../../../docs/plan/29-players-and-possession.md)).
///     </para>
///     <para>
///         <b>The defaults describe the same human <see cref="CharacterControllerSettings" /> does</b>
///         — 1.8 m in a 0.3 m capsule — walking at 4 m/s, sprinting at 7, and jumping about 1.1 m.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct CharacterMovement {
    /// <summary>The collision volume while standing.</summary>
    public ShapeId Shape;

    /// <summary>The collision volume while crouching, or none to make crouching do nothing.</summary>
    /// <remarks>
    ///     Standing up under a low ceiling is <see cref="CharacterController.TrySetShape" /> returning
    ///     false, which leaves the character crouched with no special case at any call site.
    /// </remarks>
    public ShapeId CrouchShape;

    /// <summary>Which layer the character collides against.</summary>
    public PhysicsLayer Layer;

    /// <summary>
    ///     Where the shape's centre sits relative to the entity's origin, in the entity's own frame.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="CharacterController" />'s position is its shape's <i>centre</i></b>, and a
    ///     character mesh's origin is conventionally at its feet. Without this the entity would be
    ///     buried to the waist, or the capsule would float — a discrepancy that looks like a collision
    ///     bug and is a coordinate convention. The default is the standing capsule's own centre:
    ///     0.6 m of half-height plus 0.3 m of radius.
    /// </remarks>
    public Vector3 ShapeOffset;

    /// <summary>The same, for <see cref="CrouchShape" />.</summary>
    /// <remarks>
    ///     ⚠ <b>A shape swap that did not move the character is a shape swap that fails.</b> Growing a
    ///     capsule about a fixed centre drives its bottom half a metre into the floor, and
    ///     <see cref="CharacterController.TrySetShape" /> refuses the overlap — so a crouched character
    ///     could never stand up again, on flat ground, with nothing above it. The bridge moves the
    ///     controller by the difference between the two offsets so the character's <i>feet</i> stay
    ///     put, which is what a player means by standing up.
    /// </remarks>
    public Vector3 CrouchShapeOffset;

    /// <summary>Top speed on the ground, in metres a second.</summary>
    public float WalkSpeed;

    /// <summary>Top speed while <c>MoveButtons.Sprint</c> is held.</summary>
    public float SprintSpeed;

    /// <summary>Top speed while crouched.</summary>
    public float CrouchSpeed;

    /// <summary>How hard the character accelerates towards its wanted speed on the ground, in m/s².</summary>
    /// <remarks>
    ///     <b>An acceleration and not a smoothing factor</b>, unlike the camera's damping. A designer
    ///     tuning movement thinks in "how long to reach top speed", which is speed over this, and the
    ///     linear form composes exactly with a speed cap — an exponential approach never quite arrives
    ///     and so never quite reaches the number written beside it.
    /// </remarks>
    public float Acceleration;

    /// <summary>The same, in the air. Usually much smaller.</summary>
    public float AirAcceleration;

    /// <summary>Downward acceleration while not walking, in m/s². Negative.</summary>
    /// <remarks>
    ///     The character's own rather than the world's, because a character that falls at the world's
    ///     gravity feels floaty in almost every game — the usual answer is roughly twice it, and
    ///     making that a per-character number rather than a global keeps a balloon and a player in one
    ///     scene.
    /// </remarks>
    public float Gravity;

    /// <summary>How fast the character leaves the ground when it jumps, in metres a second.</summary>
    /// <remarks>
    ///     A speed and not a height, because the height is a function of this and
    ///     <see cref="Gravity" /> and a component storing both would let them disagree.
    ///     <see cref="JumpSpeedForHeight" /> converts.
    /// </remarks>
    public float JumpSpeed;

    /// <summary>
    ///     How long after walking off a ledge a jump still works, in seconds.
    /// </summary>
    /// <remarks>
    ///     Coyote time. Players press jump a frame or two after the edge and are certain they pressed
    ///     it before; the mechanism is invisible when it works and the complaint when it is absent is
    ///     "the jump didn't register".
    /// </remarks>
    public float CoyoteTime;

    /// <summary>How long before landing a jump press is remembered, in seconds.</summary>
    /// <remarks>
    ///     The other half of the same forgiveness, at the other end. Together they are why a jump that
    ///     is one frame early and one that is one frame late both work.
    /// </remarks>
    public float JumpBufferTime;

    /// <summary>
    ///     The upward speed a jump is cut to when the button is released early, in metres a second.
    /// </summary>
    /// <remarks>
    ///     Variable jump height, and it is a clamp rather than a multiplier: a multiplier applied once
    ///     a step makes the height depend on how long the release took to notice, which is a jump that
    ///     is different at 60 Hz and at 120.
    /// </remarks>
    public float JumpCutSpeed;

    /// <summary>Whether the character turns to face where its intent says it is looking.</summary>
    /// <remarks>
    ///     Unreal's <c>bUseControllerRotationYaw</c>, and the answer is genuinely per-game: a strafing
    ///     shooter wants it and a character that turns towards its movement does not. Off by default,
    ///     because a character whose facing is animation's business should not have physics writing it.
    /// </remarks>
    public bool TurnWithAim;

    /// <summary>Top speed in water, in metres a second.</summary>
    public float SwimSpeed;

    /// <summary>How hard the character accelerates towards its wanted speed in water, in m/s².</summary>
    /// <remarks>
    ///     Between the ground's and the air's, and closer to the air's: water resists, and a swimmer
    ///     who could change direction as sharply as a walker reads as a fish rather than as a person.
    /// </remarks>
    public float SwimAcceleration;

    /// <summary>How submerged the character has to be to start swimming, 0…1 of the capsule.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Paired with <see cref="WadeThreshold" />, and the gap between them is the whole
    ///         mechanism</b>
    ///         ([35 § D11](../../../docs/plan/35-water.md#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number)).
    ///         A character standing in chest-deep water with a 30 cm swell crosses any single
    ///         threshold twice a second; two thresholds with a gap mean it has to <em>rise</em> a
    ///         stated amount to swim and <em>fall</em> a stated amount to wade. The gap should be at
    ///         least the local wave amplitude the evaluator already reports, expressed as a fraction
    ///         of the capsule.
    ///     </para>
    ///     <para>
    ///         The default pair — swim at 0.8, wade at 0.6 — is a fifth of a 1.8 m capsule, which is
    ///         36 cm of swell before it stutters.
    ///     </para>
    /// </remarks>
    public float SwimThreshold;

    /// <summary>How far the immersion has to fall before a swimmer wades again, 0…1.</summary>
    /// <remarks>See <see cref="SwimThreshold" />; below this, the character walks or falls.</remarks>
    public float WadeThreshold;

    /// <summary>How submerged a floating character settles at, 0…1 of the capsule.</summary>
    /// <remarks>
    ///     ⚠ <b>Where buoyancy exactly cancels gravity, which is what makes it a rest and not a
    ///     bounce.</b> Above it the character sinks, below it the character rises, and at it nothing
    ///     happens — Archimedes as a lerp on one number rather than a spring with a stiffness and a
    ///     damping somebody has to tune together. The default puts a head above the water.
    /// </remarks>
    public float SwimRestImmersion;

    /// <summary>How fast the water damps a swimmer's vertical motion, per second.</summary>
    /// <remarks>
    ///     Without it the restoring force is a spring with no losses, and a character dropped into a
    ///     lake oscillates about the surface for ever. Applied as a linear per-step fraction so a step
    ///     at 60 Hz and one at 120 agree.
    /// </remarks>
    public float SwimDrag;

    /// <summary>How much slower wading is at the wade threshold, 0…1 of the walk speed.</summary>
    /// <remarks>
    ///     § D11's "walking, with a speed multiplier from the depth". Interpolated from one at dry
    ///     land to this at the point the character starts swimming, so there is no step in speed at
    ///     the moment the mode changes.
    /// </remarks>
    public float WadeSpeedScale;

    /// <summary>
    ///     A human: 4 m/s walking, 7 sprinting, 2 crouched, jumping about 1.1 m under doubled gravity.
    /// </summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, for the reason <c>Camera.Perspective</c> gives at
    ///     more length: a zeroed <see cref="CharacterMovement" /> has no speed, no gravity and no
    ///     shape, so a character made with <c>default</c> stands still in mid-air and is not visibly
    ///     misconfigured. <see cref="Shape" /> is still the caller's to fill in — it names a volume
    ///     only a live <c>PhysicsShapes</c> can issue.
    /// </remarks>
    public static CharacterMovement Default => new() {
        Shape = ShapeId.None,
        CrouchShape = ShapeId.None,
        Layer = PhysicsLayer.Default,
        ShapeOffset = new(0f, 0.9f, 0f),
        CrouchShapeOffset = new(0f, 0.6f, 0f),
        WalkSpeed = 4f,
        SprintSpeed = 7f,
        CrouchSpeed = 2f,
        Acceleration = 40f,
        AirAcceleration = 8f,
        Gravity = -19.62f,
        JumpSpeed = 6.5f,
        CoyoteTime = 0.12f,
        JumpBufferTime = 0.12f,
        JumpCutSpeed = 2f,
        TurnWithAim = false,
        SwimSpeed = 2.5f,
        SwimAcceleration = 12f,
        SwimThreshold = 0.8f,
        WadeThreshold = 0.6f,
        SwimRestImmersion = 0.85f,
        SwimDrag = 4f,
        WadeSpeedScale = 0.45f
    };

    /// <summary>The <see cref="JumpSpeed" /> that reaches a given apex under a given gravity.</summary>
    /// <param name="height">How high, in metres.</param>
    /// <param name="gravity">The downward acceleration, in m/s². Negative.</param>
    /// <returns>The upward speed.</returns>
    /// <remarks>
    ///     For the tuning pass where a designer knows the ledge is 1.2 m and wants to clear it. The
    ///     stored value stays a speed, so the pair can never drift out of agreement.
    /// </remarks>
    public static float JumpSpeedForHeight(float height, float gravity) =>
        height <= 0f || gravity >= 0f ? 0f : MathF.Sqrt(-2f * gravity * height);
}

/// <summary>Where a character is in its own motion. Derived every step.</summary>
/// <remarks>
///     <para>
///         <b>Not <c>[DataContract]</c>, so no scene can carry one</b> — the argument
///         <c>CameraShot</c> makes: a file recording half a coyote window would reload into the middle
///         of a motion nobody asked for. The bridge attaches it to any character that has not got one.
///     </para>
///     <para>
///         <b>Every timer is here rather than in a field on a system</b>, and that is what makes the
///         motion replayable. A rollback restores the world; anything the rule reads that the world
///         does not hold is something that will be different the second time, and the symptom is a
///         player who mispredicts on a connection with no loss at all
///         ([16](../../../docs/plan/16-networking.md) § Prediction).
///     </para>
/// </remarks>
[Component]
public struct CharacterState {
    /// <summary>What the rules are currently doing to it.</summary>
    public CharacterMoveMode Mode;

    /// <summary>What it is standing on, as of the last step.</summary>
    public CharacterGround Ground;

    /// <summary>
    ///     How fast it is moving, in metres a second, relative to whatever it is standing on.
    /// </summary>
    /// <remarks>
    ///     Relative, so that a character standing still on a moving lift has a zero velocity and is
    ///     carried — which is what <see cref="CharacterController.GroundVelocity" />'s own remarks
    ///     describe, applied once here instead of in every game.
    /// </remarks>
    public Vector3 Velocity;

    /// <summary>How much longer a jump will still work after leaving the ground, in seconds.</summary>
    public float CoyoteRemaining;

    /// <summary>How much longer a jump press will be remembered, in seconds.</summary>
    public float JumpBufferRemaining;

    /// <summary>Whether the jump button was held at the end of the last step.</summary>
    /// <remarks>
    ///     Held so that a jump is an <i>edge</i> rather than a level: without it, holding the button
    ///     refills the buffer every step and the character jumps again the instant it lands, for ever.
    /// </remarks>
    public bool JumpHeld;

    /// <summary>Whether the character is crouched — which may be because it cannot stand up.</summary>
    public bool IsCrouching;

    /// <summary>How much of the capsule is under the water surface, 0…1.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The only new number swimming needs</b>
    ///         ([35 § D11](../../../docs/plan/35-water.md#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number)),
    ///         and every rule about wading, swimming and climbing out is a threshold on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>State rather than an argument to <see cref="CharacterMotion.Step" />, and that is
    ///         [16](../../../docs/plan/16-networking.md)'s requirement rather than a convenience.</b>
    ///         A predicted step is re-simulated whenever a snapshot disagrees, so everything the rules
    ///         read has to be part of what a rollback restores. An immersion passed in would be
    ///         whatever the water happened to say at correction time, and the correction itself would
    ///         then be wrong.
    ///     </para>
    ///     <para>
    ///         Written by whatever knows where the water is — <c>WaterQuery.Immersion</c> is the one
    ///         definition, and it is monotone in the capsule's height by construction, which is what
    ///         lets the hysteresis below work at all.
    ///     </para>
    /// </remarks>
    public float Immersion;

    /// <summary>Whether it is on ground it can stand on.</summary>
    public readonly bool IsGrounded => Ground == CharacterGround.Grounded;

    /// <summary>Whether it is in water deep enough to swim in.</summary>
    public readonly bool IsSwimming => Mode == CharacterMoveMode.Swimming;
}

/// <summary>The character controller an entity has been given.</summary>
/// <remarks>
///     <para>
///         Attached by <c>PhysicsScene</c> and never by hand. Its absence is the query that finds
///         entities still needing one, exactly as <c>PhysicsBody</c>'s is — <c>WithNone</c> rather
///         than a scan for a sentinel.
///     </para>
///     <para>
///         ⚠ <b>Neither <c>[Component]</c> nor <c>[DataContract]</c>, and the pair of absences is
///         load-bearing.</b> A scene component is one carrying both; this carries neither, so nothing
///         has to remember to exclude a handle that means nothing outside the world that issued it.
///         The same construction <c>PhysicsBody</c> uses, for the same reason, and the bridge rebuilds
///         it from <see cref="CharacterMovement" /> on the first step after a load.
///     </para>
/// </remarks>
public struct CharacterBody {
    /// <summary>Which controller, in the scene's own table.</summary>
    public int Handle;

    /// <summary>The shape the controller was built with, so a swap can be told from a no-op.</summary>
    public ShapeId BuiltShape;

    /// <summary>The offset that shape is centred by, so the writeback and the next swap agree.</summary>
    public Vector3 BuiltOffset;
}
