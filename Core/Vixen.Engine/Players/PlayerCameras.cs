// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Players;

/// <summary>The two entities a player's camera is made of.</summary>
/// <param name="Eye">
///     The real <see cref="Camera" />, with the <see cref="CameraDirector" /> that drives it.
/// </param>
/// <param name="Shot">The <see cref="VirtualCamera" /> the director is currently blending towards.</param>
/// <remarks>
///     Two entities and not one, because [26](../../../docs/plan/26-virtual-cameras.md) separates
///     "where a point of view is" from "which point of view is live". A game adding a second shot for
///     a cutscene gives it a higher priority on the same channel and never touches either of these.
/// </remarks>
public readonly record struct PlayerCamera(Entity Eye, Entity Shot);

/// <summary>The camera rigs that need to know where a player is looking.</summary>
/// <remarks>
///     <para>
///         <b>Why these are in the engine when a tuned rig belongs in a sample.</b>
///         [29](../../../docs/plan/29-players-and-possession.md) is firm that the engine ships
///         mechanism rather than a ruleset, and a camera <i>preset</i> would be a ruleset. These are
///         not presets: a first-person camera and a third-person orbit are the two rigs that cannot be
///         assembled from outside this assembly, because both are steered by
///         <see cref="ControlRotation" /> and the write that carries it into
///         <see cref="PovAim" /> and <see cref="OrbitBody" /> is <see cref="PossessionSystem" />'s.
///         Everything a game would actually tune — the distance, the height, the damping, the lens —
///         is an argument here and a component afterwards.
///     </para>
///     <para>
///         Both build on doc 26's stages and add nothing to them. A game that wants a different rig
///         writes the same three component adds with different values, which is what the source of
///         each of these is.
///     </para>
/// </remarks>
public static class PlayerCameras {
    /// <summary>A camera at the player's eyes, looking exactly where they are aiming.</summary>
    /// <param name="world">The world.</param>
    /// <param name="controller">The player.</param>
    /// <param name="eyeHeight">How far above the pawn's origin the eyes are, in metres.</param>
    /// <returns>The camera and its shot.</returns>
    /// <exception cref="ArgumentException"><paramref name="controller" /> is not a player.</exception>
    /// <remarks>
    ///     <see cref="HardLockBody" /> and <see cref="PovAim" />, which is what those two components
    ///     exist for — <c>HardLockBody</c>'s own remarks name a first-person camera first. There is
    ///     nothing to damp in either: a first-person camera that lagged the mouse is a first-person
    ///     camera that feels broken, and one that lagged the body would swim.
    /// </remarks>
    public static PlayerCamera FirstPerson(World world, Entity controller, float eyeHeight = 1.65f) {
        ArgumentNullException.ThrowIfNull(world);

        var channel = ChannelOf(world, controller);
        var eye = CreateEye(world, channel);

        var shot = VirtualCameras.Create(
            world,
            VirtualCamera.Default with { Channel = channel },
            default
        );

        // World space rather than the target's, and the difference is only visible on a pawn that
        // turns: a vertical offset is the same vector either way, and the world-space one keeps
        // working on a character whose facing is animation's business rather than physics'.
        world.Add(shot, new HardLockBody { Offset = new(0f, eyeHeight, 0f), InTargetSpace = false });
        world.Add(shot, PovAim.Default);

        Player.BindCamera(world, controller, shot);
        return new(eye, shot);
    }

    /// <summary>A camera orbiting behind the player, at the angle they are aiming.</summary>
    /// <param name="world">The world.</param>
    /// <param name="controller">The player.</param>
    /// <param name="distance">How far behind, in metres.</param>
    /// <param name="shoulderHeight">How far above the pawn's origin the camera orbits and looks.</param>
    /// <param name="damping">How long the orbit takes to remove 99 % of its error, in seconds.</param>
    /// <returns>The camera and its shot.</returns>
    /// <exception cref="ArgumentException"><paramref name="controller" /> is not a player.</exception>
    /// <remarks>
    ///     <para>
    ///         <see cref="OrbitBody" /> rather than <see cref="FollowBody" />, and the difference is
    ///         the whole point: <c>FollowBody.Behind</c> swings round as the <i>target</i> turns, which
    ///         is right for a camera watching a car and wrong for one a player is steering.
    ///         <c>OrbitBody</c>'s own remarks say it reads no device and expects gameplay to write its
    ///         two angles — <see cref="PossessionSystem" /> is what writes them, from
    ///         <see cref="ControlRotation" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The shot carries a <see cref="CameraOcclusion" />, and it does nothing until a
    ///         host answers for it.</b> What the camera is allowed to pass through is a question about
    ///         a world's solidity, and <c>Vixen.Engine</c> references no physics — which is what keeps
    ///         <c>Vixen.Physics</c> an optional subsystem. So the component is here, at the pivot the
    ///         other two stages already use, and <c>VirtualCameraSystem.Occlusion</c> is the one line a
    ///         game with an implementation writes.
    ///     </para>
    /// </remarks>
    public static PlayerCamera ThirdPerson(
        World world,
        Entity controller,
        float distance = 4f,
        float shoulderHeight = 1.4f,
        float damping = 0.3f
    ) {
        ArgumentNullException.ThrowIfNull(world);

        var channel = ChannelOf(world, controller);
        var eye = CreateEye(world, channel);

        var shot = VirtualCameras.Create(
            world,
            VirtualCamera.Default with { Channel = channel },
            default
        );

        world.Add(
            shot,
            OrbitBody.At(distance, damping: damping) with { PivotOffset = new(0f, shoulderHeight, 0f) }
        );

        // Looking at the shoulder rather than at the target's origin, which is at the character's
        // feet — a camera aimed there frames a patch of floor with a person standing at the top of it.
        world.Add(shot, new HardLookAim { TrackedOffset = new(0f, shoulderHeight, 0f) });

        // The avoider, at the same pivot the other two stages use. It costs nothing in a game that
        // supplies no ICameraOcclusion — the stage returns before it looks at a chunk — and a game
        // that supplies one gets the behaviour every third-person camera is expected to have without
        // knowing that the offsets on three components have to agree.
        world.Add(shot, CameraOcclusion.Default() with { PivotOffset = new(0f, shoulderHeight, 0f) });

        Player.BindCamera(world, controller, shot);
        return new(eye, shot);
    }

    /// <summary>The real camera and its director, on a player's own channel.</summary>
    /// <param name="world">The world.</param>
    /// <param name="channel">The channel, which is the player's.</param>
    /// <returns>The camera entity.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="Camera.Order" /> is the channel</b>, and that is doing real work rather
    ///         than being tidy. <c>CameraExtractionSystem</c> fills one <c>RenderView</c> from the
    ///         lowest-ordered camera in the world, so seat zero is the one on screen and a game
    ///         switching which player is watched writes an order rather than destroying a camera.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is also the honest limit of split screen today.</b> Two players get two
    ///         cameras, two directors and two independent sets of shots, and all of it simulates
    ///         correctly — but the renderer draws one view, because a <c>RenderView</c> has no
    ///         viewport rectangle and <c>CameraExtractionSystem</c> fills exactly one. Showing both at
    ///         once needs a view per player and a rect on each, which is doc 06's work and not this
    ///         document's.
    ///     </para>
    /// </remarks>
    public static Entity CreateEye(World world, int channel = 0) {
        ArgumentNullException.ThrowIfNull(world);

        var eye = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        world.Add(eye, Camera.Perspective with { Order = channel });
        world.Add(eye, CameraDirector.Default with { Channel = channel });

        return eye;
    }

    static int ChannelOf(World world, Entity controller) {
        if (!world.IsAlive(controller) || !world.TryGet<PlayerController>(controller, out var player)) {
            throw new ArgumentException($"{controller} is not a player controller.", nameof(controller));
        }

        return player.CameraChannel;
    }
}
