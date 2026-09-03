// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Interop;
using Vixen.Physics.Shapes;
using MathUtil = Vixen.Core.Mathematics.MathUtil;

namespace Vixen.Physics.Characters;

/// <summary>What the character is standing on, if anything.</summary>
public enum CharacterGround {
    /// <summary>On ground it can stand on.</summary>
    Grounded,

    /// <summary>Touching ground, but too steep to stand on. It will slide.</summary>
    Steep,

    /// <summary>Touching something that is not ground at all — a wall, or a body it cannot rest on.</summary>
    Unsupported,

    /// <summary>Touching nothing.</summary>
    Airborne
}

/// <summary>How a character controller behaves.</summary>
/// <remarks>
///     The defaults describe a human: 1.8 m tall in a 0.3 m capsule, able to walk up a 45° slope and
///     a 0.4 m step, weighing 80 kg and able to push about a crate's worth.
/// </remarks>
[DataContract]
public sealed record CharacterControllerSettings {
    /// <summary>The character's collision volume. Usually a capsule.</summary>
    public ShapeId Shape { get; init; }

    /// <summary>Which way is up for this character.</summary>
    public Vector3 Up { get; init; } = Vector3.Up;

    /// <summary>The steepest slope it can stand on, in radians.</summary>
    /// <remarks>
    ///     ⚠ <b>What it was <i>created</i> with, and not what it is now.</b>
    ///     <see cref="CharacterController.MaxSlopeAngle" /> is live and this record is never rewritten
    ///     when it changes — the same relationship <see cref="Shape" /> has with
    ///     <c>CharacterBody.BuiltShape</c>, and for the same reason: a settings record that tracked the
    ///     live value would be a second answer to a question the controller already answers.
    /// </remarks>
    public float MaxSlopeAngle { get; init; } = MathUtil.PiOverFour;

    /// <summary>Its mass in kilograms, which is what it pushes bodies with.</summary>
    public float Mass { get; init; } = 80f;

    /// <summary>The most force it can bring to bear on a body in its way, in newtons.</summary>
    public float MaxPushForce { get; init; } = 100f;

    /// <summary>
    ///     How tall a step it can walk up without jumping.
    /// </summary>
    /// <remarks>
    ///     Stair walking is a separate sweep, and turning it off — a zero here — makes a character
    ///     that catches on every 5 cm lip in the level. It is on by default for that reason.
    ///     <see cref="CharacterController.StepHeight" /> is live; this is what it started at.
    /// </remarks>
    public float StepHeight { get; init; } = 0.4f;

    /// <summary>
    ///     How far down it looks for the floor when walking off a small drop, so it follows the
    ///     ground rather than launching off it.
    /// </summary>
    public float StickToFloorDistance { get; init; } = 0.5f;

    /// <summary>How quickly it pushes itself out of geometry it has ended up inside, in metres a second.</summary>
    public float PenetrationRecoverySpeed { get; init; } = 1f;

    /// <summary>How far ahead it looks for contacts, in metres.</summary>
    public float PredictiveContactDistance { get; init; } = 0.1f;

    /// <summary>Which layer it collides against.</summary>
    public PhysicsLayer Layer { get; init; } = PhysicsLayer.Default;

    /// <summary>Where it starts.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Which way it starts facing.</summary>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
}

/// <summary>
///     A player or an NPC that walks: swept against the world, never solved, and in charge of its own
///     velocity.
/// </summary>
/// <remarks>
///     <para>
///         This is Jolt's <c>CharacterVirtual</c> — a shape that is cast through the world every step
///         and slid along whatever it hits. It has no body in the simulation, so nothing pushes it
///         and it does not fall over, which is what a character wants and what a dynamic capsule
///         famously does not give: a rigid body character sticks on seams, bounces down stairs and
///         tips at the top of a ramp, and every game that has tried it has ended up here.
///     </para>
///     <para>
///         <b>Gravity is the caller's.</b> <see cref="Velocity" /> is whatever was last set, and
///         <see cref="Update" /> moves by it. That is deliberate: a character's vertical motion is a
///         gameplay decision — coyote time, variable jump height, a ladder — and a controller that
///         applied gravity itself would have to be fought for every one of them. The pattern is to
///         add <c>gravity * dt</c> before the update when airborne, and to zero it when grounded.
///     </para>
///     <para>
///         Characters are owned by their world and die with it. Disposing one early removes it from
///         the world's list; disposing the world disposes any that are left.
///     </para>
/// </remarks>
public sealed class CharacterController : IDisposable {
    readonly PhysicsWorld world;
    readonly CharacterVirtual character;
    readonly ObjectLayer layer;

    /// <summary>
    ///     Handed to <c>ExtendedUpdate</c> every step, so a step height changed between two steps is
    ///     honoured by the next one. Not <c>readonly</c> for exactly that reason.
    /// </summary>
    ExtendedUpdateSettings updateSettings;

    float maxSlopeAngle;
    float stepHeight;

    /// <summary>How it was configured.</summary>
    public CharacterControllerSettings Settings { get; }

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Where the character is — the <b>centre</b> of its shape.</summary>
    /// <remarks>
    ///     ⚠ This used to say "bottom-centre", and it was wrong: <c>CharacterControllerTests</c> pins a
    ///     capsule settling at <c>halfHeight + radius</c> above the floor, which is its centre.
    ///     <see cref="CharacterMovement.ShapeOffset" /> is what puts an entity's origin back at the
    ///     character's feet, and it exists because the difference looks like a collision bug rather
    ///     than like a coordinate convention.
    /// </remarks>
    public Vector3 Position {
        get => JoltMath.ToVixen(character.Position);
        set => character.Position = JoltMath.ToJolt(value);
    }

    /// <summary>Which way it faces.</summary>
    public Quaternion Rotation {
        get => JoltMath.ToVixen(character.Rotation);
        set => character.Rotation = JoltMath.ToJolt(value);
    }

    /// <summary>
    ///     How fast it is moving, in metres a second. Set this; <see cref="Update" /> acts on it.
    /// </summary>
    public Vector3 Velocity {
        get => JoltMath.ToVixen(character.LinearVelocity);
        set => character.LinearVelocity = JoltMath.ToJolt(value);
    }

    /// <summary>The steepest slope it can stand on, in radians. Changeable while it walks.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Jolt does <i>not</i> fix this at creation, and the belief that it does is what
    ///         deferred per-character slope limits.</b> <c>CharacterBase</c> carries a setter, which
    ///         recomputes the cosine the ground test actually uses; nothing has to be recreated and no
    ///         contact state is lost. The claim this file and
    ///         <c>Core/Vixen.Physics/README.md</c> used to make — that exposing it "means recreating
    ///         the controller on an edit" — was wrong about the binding.
    ///     </para>
    ///     <para>
    ///         It takes effect on the next sweep, because <see cref="Ground" /> is whatever the last
    ///         one concluded. <see cref="RefreshContacts" /> is how to ask again without moving.
    ///     </para>
    /// </remarks>
    public float MaxSlopeAngle {
        get => maxSlopeAngle;
        set {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            maxSlopeAngle = value;
            character.MaxSlopeAngle = value;
        }
    }

    /// <summary>How tall a step it can walk up without jumping. Changeable while it walks.</summary>
    /// <remarks>
    ///     ⚠ <b>Fixed at creation by this class rather than by Jolt.</b> Stair walking is driven by
    ///     <c>ExtendedUpdateSettings.WalkStairsStepUp</c>, which is an argument to
    ///     <c>ExtendedUpdate</c> and therefore per <i>step</i> — the struct was simply held in a
    ///     <c>readonly</c> field here. A crouching character that should not clear the same lip it
    ///     clears standing needs nothing but this.
    /// </remarks>
    public float StepHeight {
        get => stepHeight;
        set {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            stepHeight = value;
            updateSettings = BuildUpdateSettings(Settings.Up, value, Settings.StickToFloorDistance);
        }
    }

    /// <summary>What it is standing on, as of the last update.</summary>
    public CharacterGround Ground =>
        character.GroundState switch {
            GroundState.OnGround => CharacterGround.Grounded,
            GroundState.OnSteepGround => CharacterGround.Steep,
            GroundState.NotSupported => CharacterGround.Unsupported,
            _ => CharacterGround.Airborne
        };

    /// <summary>Whether it is on ground it can stand on.</summary>
    public bool IsGrounded => character.GroundState == GroundState.OnGround;

    /// <summary>The normal of the ground under it, or up if there is none.</summary>
    public Vector3 GroundNormal => JoltMath.ToVixen(character.GroundNormal);

    /// <summary>How fast the ground under it is moving — a lift, a moving platform.</summary>
    /// <remarks>
    ///     Subtracting this from <see cref="Velocity" /> gives motion relative to the platform, which
    ///     is what a character standing still on a moving lift should have. Adding it back before the
    ///     update is what carries them along.
    /// </remarks>
    public Vector3 GroundVelocity => JoltMath.ToVixen(character.GroundVelocity);

    /// <summary>The body it is standing on, if any.</summary>
    public BodyHandle GroundBody =>
        character.GroundState == GroundState.OnGround ? new(character.GroundBodyId) : BodyHandle.None;

    internal CharacterController(PhysicsWorld world, PhysicsSystem system, CharacterControllerSettings settings) {
        this.world = world;
        Settings = settings;
        layer = new(settings.Layer.Index);

        var characterSettings = new CharacterVirtualSettings {
            Shape = world.Shapes.Resolve(settings.Shape),
            Up = JoltMath.ToJolt(settings.Up),
            MaxSlopeAngle = settings.MaxSlopeAngle,
            Mass = settings.Mass,
            MaxStrength = settings.MaxPushForce,
            PredictiveContactDistance = settings.PredictiveContactDistance,
            PenetrationRecoverySpeed = settings.PenetrationRecoverySpeed
        };

        var position = JoltMath.ToJolt(settings.Position);
        var rotation = JoltMath.ToJolt(settings.Rotation);
        character = new(characterSettings, in position, in rotation, 0ul, system);

        // The live copies of the two the component may edit. Kept here because CharacterBase offers a
        // setter and no getter for the slope angle — it stores the cosine — so a caller reading back
        // what it just wrote would otherwise get an angle that has been through two transcendentals.
        maxSlopeAngle = settings.MaxSlopeAngle;
        stepHeight = settings.StepHeight;

        updateSettings = BuildUpdateSettings(settings.Up, settings.StepHeight, settings.StickToFloorDistance);
    }

    /// <summary>The whole <c>ExtendedUpdateSettings</c>, rebuilt because its properties are init-only.</summary>
    /// <remarks>
    ///     <para>
    ///         Stair walking and floor sticking are given as vectors along the character's up axis, so
    ///         a character on a planet with a different up gets them in the right direction for free.
    ///     </para>
    ///     <para>
    ///         Every field is set, including the three that look like tuning constants. The binding's
    ///         parameterless constructor zeroes the struct rather than filling in Jolt's defaults, and
    ///         a zero <c>WalkStairsStepForwardTest</c> makes the stair sweep test forward by nothing —
    ///         which does not fail loudly, it just walks the character into the step and leaves it
    ///         there. These are Jolt's own values: 2 cm minimum forward, 15 cm forward test, and a 75°
    ///         cut-off for what counts as a wall rather than a stair.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A whole function rather than one assignment because every property of the struct is
    ///         <c>init</c>-only</b>, which is also the reason the step height looked fixed at creation:
    ///         the value is per-update, but nothing could write it after the object initializer ran.
    ///     </para>
    /// </remarks>
    static ExtendedUpdateSettings BuildUpdateSettings(Vector3 up, float stepHeight, float stickToFloorDistance) =>
        new() {
            WalkStairsStepUp = JoltMath.ToJolt(up * stepHeight),
            StickToFloorStepDown = JoltMath.ToJolt(-up * stickToFloorDistance),
            WalkStairsMinStepForward = 0.02f,
            WalkStairsStepForwardTest = 0.15f,
            WalkStairsCosAngleForwardContact = MathF.Cos(MathUtil.DegreesToRadians(75f)),
            WalkStairsStepDownExtra = System.Numerics.Vector3.Zero
        };

    /// <summary>Moves the character by its velocity, sliding along whatever is in the way.</summary>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <remarks>
    ///     Called once per fixed step, from the same phase the world is stepped in and after it — the
    ///     character sweeps against the world as it is now, so sweeping before the step would test
    ///     against the previous frame's platform positions.
    /// </remarks>
    public void Update(float deltaTime) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deltaTime);

        var characterLayer = layer;

        character.ExtendedUpdate(
            deltaTime,
            updateSettings,
            in characterLayer,
            world.JoltSystem,
            default,
            default
        );
    }

    /// <summary>
    ///     Removes the component of a velocity that would push the character into a slope too steep
    ///     to climb.
    /// </summary>
    /// <param name="desired">The velocity the character wants.</param>
    /// <returns>The part of it that is not wasted against the slope.</returns>
    /// <remarks>
    ///     What stops a character from walking into a steep hill and hovering: without this the
    ///     horizontal input keeps pushing, the sweep keeps refusing, and the character sticks to the
    ///     slope instead of sliding back down it.
    /// </remarks>
    public Vector3 CancelVelocityTowardsSteepSlopes(Vector3 desired) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var value = JoltMath.ToJolt(desired);
        return JoltMath.ToVixen(character.CancelVelocityTowardsSteepSlopes(in value));
    }

    /// <summary>Gives the character a different shape, if it fits where it is standing.</summary>
    /// <param name="shape">The new shape.</param>
    /// <param name="maxPenetration">How much overlap to tolerate, in metres.</param>
    /// <returns><see langword="true" /> if the shape was changed.</returns>
    /// <remarks>
    ///     This is what crouching and standing up are. Standing up under a low ceiling returns
    ///     <see langword="false" /> and leaves the character crouched, which is the behaviour that
    ///     needs no special case at the call site.
    /// </remarks>
    public bool TrySetShape(ShapeId shape, float maxPenetration = 0.01f) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var characterLayer = layer;

        return character.SetShape(
            0f,
            world.Shapes.Resolve(shape),
            maxPenetration,
            in characterLayer,
            world.JoltSystem,
            default,
            default
        );
    }

    /// <summary>Re-tests the character's contacts without moving it.</summary>
    /// <remarks>
    ///     After teleporting, so <see cref="Ground" /> answers about where the character is now
    ///     rather than where it was before the jump.
    /// </remarks>
    public void RefreshContacts() {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var characterLayer = layer;
        character.RefreshContacts(in characterLayer, world.JoltSystem, default, default);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        world.Forget(this);
        DisposeInternal();
    }

    internal void DisposeInternal() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;
        character.Dispose();
    }
}
