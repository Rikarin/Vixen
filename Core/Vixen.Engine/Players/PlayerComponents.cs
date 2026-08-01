// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Players;

/// <summary>The buttons a player can be holding, as one field.</summary>
/// <remarks>
///     <para>
///         A bitfield rather than a bool each, because <see cref="MoveIntent" /> goes on a wire every
///         tick and several ticks at a time
///         ([16](../../../docs/plan/16-networking.md) § Prediction) — eight booleans is eight bytes
///         where eight bits will do.
///     </para>
///     <para>
///         <b>The high byte is the game's.</b> The engine names the eight verbs every game with a
///         character has; a game needing a ninth writes <c>(MoveButtons)(1 &lt;&lt; 8)</c> and gives
///         it a name of its own. Widening this enum later would be a wire break and a scene break at
///         once, so the room is reserved now rather than found later.
///     </para>
/// </remarks>
[Flags]
public enum MoveButtons : ushort {
    /// <summary>Nothing held.</summary>
    None = 0,

    /// <summary>Jump.</summary>
    Jump = 1 << 0,

    /// <summary>Crouch.</summary>
    Crouch = 1 << 1,

    /// <summary>Sprint.</summary>
    Sprint = 1 << 2,

    /// <summary>The primary action — fire, swing, use.</summary>
    Primary = 1 << 3,

    /// <summary>The secondary action.</summary>
    Secondary = 1 << 4,

    /// <summary>Aim down sights, or whatever focusing means.</summary>
    Aim = 1 << 5,

    /// <summary>Interact with whatever is in front.</summary>
    Interact = 1 << 6,

    /// <summary>Reload.</summary>
    Reload = 1 << 7,

    /// <summary>Every bit the engine has named. Everything above it belongs to the game.</summary>
    EngineMask = 0x00FF
}

/// <summary>What a player is asking for this tick: where to go, where to look, what is held.</summary>
/// <remarks>
///     <para>
///         <b>The one seam between input, movement and the network</b>, and the reason each of those
///         can live in an assembly that cannot see the other two. <c>PlayerInputSystem</c> writes it
///         from a device, <c>CharacterMovementSystem</c> in <c>Vixen.Physics</c> reads it, and
///         <c>Vixen.Net.Engine</c> quantizes it onto the wire — three layers agreeing on one struct
///         instead of three translations that have to be kept in step.
///         [29](../../../docs/plan/29-players-and-possession.md) is the argument.
///     </para>
///     <para>
///         <b>An absolute <see cref="Yaw" />, not a yaw delta.</b> A delta is what a mouse produces
///         and it is the wrong thing to carry: two machines integrating deltas drift apart, and a
///         server handed a delta has nothing it can refuse. What crosses every boundary here is where
///         the player <i>is</i> looking, which an authority is free to reject outright.
///     </para>
///     <para>
///         <b>It is held twice — once by the controller and once by the pawn.</b> The pawn's copy is
///         derived: <c>PossessionSystem</c> overwrites it every frame from whatever is driving, the
///         way <c>WorldTransform</c> is overwritten from the hierarchy. Anything writing the pawn's
///         copy directly has to have no <see cref="PossessedBy" /> for the write to survive. The
///         alternative — every movement chunk following the edge back to its controller — turns a
///         sequential sweep into two random accesses per entity to save four copies at the top of the
///         frame.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct MoveIntent {
    /// <summary>
    ///     Where the player wants to go, in the frame their look direction defines. X is right,
    ///     Y is forward, and the magnitude is never expected to exceed one.
    /// </summary>
    public Vector2 Move;

    /// <summary>Which way the player is looking, in radians about the world's up axis.</summary>
    public float Yaw;

    /// <summary>How far up the player is looking, in radians. Positive looks up.</summary>
    public float Pitch;

    /// <summary>What is held.</summary>
    public MoveButtons Buttons;

    /// <summary>Whether a button is held.</summary>
    /// <param name="button">The button, or several to test for any of them.</param>
    /// <returns>Whether any named bit is set.</returns>
    public readonly bool IsHeld(MoveButtons button) => (Buttons & button) != 0;

    /// <summary>The direction the intent points, in world space.</summary>
    /// <returns>A world-space direction, or zero if the player is not asking to move.</returns>
    /// <remarks>
    ///     The yaw only, never the pitch: a character walking forward while looking at the sky walks
    ///     along the ground. A flying pawn that wants the pitch composes it from
    ///     <see cref="ControlRotation" /> instead, which is why this is a helper and not the stored
    ///     value.
    /// </remarks>
    public readonly Vector3 WorldDirection() {
        if (Move.X == 0f && Move.Y == 0f) {
            return Vector3.Zero;
        }

        var sin = MathF.Sin(Yaw);
        var cos = MathF.Cos(Yaw);

        // The yaw's own basis: forward is -Z at zero yaw, right is +X, and both turn with it. Built
        // here rather than through a quaternion because two sines and a pair of multiplies is the
        // whole of it, and this runs once per player per fixed step.
        var forward = new Vector3(-sin, 0f, -cos);
        var right = new Vector3(cos, 0f, -sin);

        return (right * Move.X) + (forward * Move.Y);
    }
}

/// <summary>Which way a player is looking. Held by the controller, not by the body.</summary>
/// <remarks>
///     <para>
///         <b>The single decision this whole subsystem is arranged around.</b> Aim on the pawn dies
///         with the pawn, so every game that puts it there writes code to carry it across a respawn,
///         a vehicle entry and a spectator transition — three times, differently. Aim on the
///         controller survives all three because the controller was never in the world's way.
///         Unreal calls this <c>ControlRotation</c> and the name is kept.
///     </para>
///     <para>
///         <b>No roll.</b> A player looks with two angles; a camera that rolls is doing it as an
///         effect, which is <c>CameraLens.Dutch</c>'s job and is composed after everything here.
///     </para>
///     <para>
///         The clamps default to the same ±80° <see cref="Cameras.PovAim" /> uses. A first-person
///         camera whose limits disagreed with the aim it is fed would let the player aim at something
///         it refuses to show them.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct ControlRotation {
    /// <summary>Where the player is turned to, in radians about the world's up axis.</summary>
    public float Yaw;

    /// <summary>How far up they are looking, in radians. Positive looks up.</summary>
    public float Pitch;

    /// <summary>The furthest down they may look, in radians.</summary>
    public float MinimumPitch;

    /// <summary>The furthest up they may look, in radians.</summary>
    public float MaximumPitch;

    /// <summary>Level, facing -Z, able to look 80° up and 80° down.</summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, for the reason <see cref="Cameras.Camera" /> and
    ///     <see cref="Cameras.VirtualCamera" /> both give: a zeroed <see cref="ControlRotation" /> has
    ///     a zero minimum and a zero maximum, so it is pinned exactly level and the player's mouse
    ///     appears to be broken in one axis.
    /// </remarks>
    public static ControlRotation Default => new() {
        Yaw = 0f,
        Pitch = 0f,
        MinimumPitch = MathUtil.DegreesToRadians(-80f),
        MaximumPitch = MathUtil.DegreesToRadians(80f)
    };

    /// <summary>Turns by two angles, wrapping the yaw and clamping the pitch.</summary>
    /// <param name="yaw">How far to turn, in radians. Positive turns left.</param>
    /// <param name="pitch">How far to tilt, in radians. Positive looks up.</param>
    /// <remarks>
    ///     <b>The yaw is wrapped rather than accumulated.</b> A player who spins in one direction for
    ///     an hour accumulates an angle whose floating-point precision has visibly coarsened, and the
    ///     symptom is a mouse that becomes gritty after a long session. It is also what makes the
    ///     angle quantizable to ten bits on the wire.
    /// </remarks>
    public void Turn(float yaw, float pitch) {
        Yaw = MathUtil.WrapAngle(Yaw + yaw);
        Pitch = MathUtil.Clamp(Pitch + pitch, MinimumPitch, MaximumPitch);
    }

    /// <summary>The direction being looked along, in world space.</summary>
    /// <returns>A unit vector.</returns>
    /// <remarks>
    ///     Built from the two angles directly rather than through
    ///     <c>Quaternion.FromYawPitchRoll</c>, which would pitch about the world's X axis instead of
    ///     the turned one — the same rotation only while the yaw is zero. <c>PovAim</c>'s stage in
    ///     <c>VirtualCameraSystem</c> makes the identical construction, and the two must agree.
    /// </remarks>
    public readonly Vector3 Forward() {
        var pitch = MathUtil.Clamp(Pitch, MinimumPitch, MaximumPitch);
        var cosPitch = MathF.Cos(pitch);

        return new(-cosPitch * MathF.Sin(Yaw), MathF.Sin(pitch), -cosPitch * MathF.Cos(Yaw));
    }

    /// <summary>The full look rotation, pitch included.</summary>
    /// <returns>The rotation.</returns>
    public readonly Quaternion ToRotation() => Transform.LookRotation(Forward(), Vector3.Up);

    /// <summary>The yaw alone, which is what a character standing on the ground turns by.</summary>
    /// <returns>The rotation.</returns>
    public readonly Quaternion YawRotation() => Quaternion.FromAxisAngle(Vector3.UnitY, Yaw);
}

/// <summary>A player: the seat, not the body.</summary>
/// <remarks>
///     <para>
///         It holds what survives a death — which slot on this machine the player is, which
///         connection they are, and which camera channel is theirs — and nothing that describes a
///         thing in the world. What it is currently driving is <see cref="Possessing" />, and that is
///         a separate component because it is the part that changes.
///     </para>
///     <para>
///         ⚠ <b><see cref="Owner" /> is a <see cref="uint" /> and not a <c>PlayerId</c>.</b>
///         <c>PlayerId</c> lives in <c>Vixen.Net.Sessions</c>, and <c>Vixen.Engine</c> may not
///         reference <c>Vixen.Net</c> — networking is optional and nothing below it may depend on it
///         ([16](../../../docs/plan/16-networking.md)). <c>NetworkSpawn.Owner</c> is a raw
///         <see cref="uint" /> for exactly this reason and documents itself the same way. Zero is the
///         local machine in both places.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PlayerController {
    /// <summary>Which seat at this machine, from zero. Split screen is more than one.</summary>
    /// <remarks>
    ///     The same number a game gives <c>InputActions.GamepadSlot</c>, so that two players loading
    ///     one <c>.vxinput</c> read different pads. There is no <c>LocalPlayer</c> object because
    ///     with this, the gamepad slot and the camera channel there is nothing left for one to hold.
    /// </remarks>
    public byte Slot;

    /// <summary>Which connection this player is, as a <c>PlayerId</c>. Zero is the local machine.</summary>
    public uint Owner;

    /// <summary>Which <c>CameraDirector</c> channel shows this player their game.</summary>
    /// <remarks>
    ///     One game in one window leaves every one of these at zero and never thinks about it. A
    ///     split-screen game gives each player a channel, which is what stops one player's trigger
    ///     volume taking the other player's camera.
    /// </remarks>
    public int CameraChannel;

    /// <summary>Whether input reaches this player at all.</summary>
    /// <remarks>
    ///     False is what a cutscene, a menu and a death animation want: the controller keeps its aim
    ///     and its possession, and simply stops being told about the device. Clearing the intent
    ///     rather than freezing it is deliberate — a player whose sprint was held when the menu
    ///     opened should not still be sprinting behind it.
    /// </remarks>
    public bool AcceptsInput;

    /// <summary>Seat zero on the local machine, on camera channel zero, accepting input.</summary>
    /// <remarks>
    ///     A property rather than a <c>default</c>, because a zeroed <see cref="PlayerController" />
    ///     does not accept input — a controller created with <c>default</c> would be silently deaf
    ///     rather than visibly broken. The same trap <c>VirtualCamera.Default</c> exists to avoid.
    /// </remarks>
    public static PlayerController Default => new() {
        Slot = 0,
        Owner = 0,
        CameraChannel = 0,
        AcceptsInput = true
    };
}

/// <summary>What a controller is driving. Absent when it is driving nothing.</summary>
/// <remarks>
///     <para>
///         Absent rather than <see cref="Entity.Null" />, so "is possessing something" is an
///         archetype question and the driven controllers can be found with a mask test — the same
///         argument <see cref="Parent" /> makes for roots.
///     </para>
///     <para>
///         ⚠ <b>Not <c>[DataContract]</c>, so no scene can carry one.</b> An entity handle names a
///         slot in the world that issued it and means nothing in another — the line
///         <c>CameraTargets</c> and <c>PhysicsBody</c> are already on. A scene therefore places pawns
///         and shots, and something running in the world decides who drives what.
///     </para>
/// </remarks>
[Component]
public struct Possessing {
    /// <summary>The pawn.</summary>
    public Entity Pawn;
}

/// <summary>What is driving a pawn. Absent when nothing is.</summary>
/// <remarks>
///     The other half of <see cref="Possessing" />, so that a pawn can find its controller without a
///     scan. <see cref="Player" /> is the only supported way to write either: both can be set
///     directly, and everything that does will eventually produce a pawn that believes it is
///     possessed by a controller that has forgotten it.
/// </remarks>
[Component]
public struct PossessedBy {
    /// <summary>The controller.</summary>
    public Entity Controller;
}

/// <summary>The shot that shows a player what they are driving. Absent when nothing does.</summary>
/// <remarks>
///     <para>
///         Unreal's view target, and the reason this document needs no camera manager: possession
///         writes <c>CameraTargets</c> onto this shot, and [26](../../../docs/plan/26-virtual-cameras.md)'s
///         director blends to it because the answer changed. There is no <c>SetViewTarget</c> call,
///         no blend curve to pass and nothing to remember to undo.
///     </para>
///     <para>
///         ⚠ <b>Not <c>[DataContract]</c></b>, for the reason <see cref="Possessing" /> gives.
///     </para>
/// </remarks>
[Component]
public struct ViewTarget {
    /// <summary>The <c>VirtualCamera</c> entity that watches this player's pawn.</summary>
    public Entity Shot;
}
