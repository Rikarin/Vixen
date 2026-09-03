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
    ///         ⚠ <b>That used to be the honest limit of split screen, and it is not any more.</b> The
    ///         renderer drew one view because a <c>RenderView</c> had no viewport rectangle and
    ///         <c>CameraExtractionSystem</c> filled exactly one. Both halves exist now —
    ///         <see cref="Camera.ViewportRect" /> is the rect and <c>CameraExtractionSystem.Rank</c>
    ///         is what lets a host add one extraction per seat — and <see cref="SplitScreen" /> is
    ///         what writes the rects. The order still decides which seat is which, so it is still
    ///         what a game switching who is watched writes.
    ///     </para>
    /// </remarks>
    public static Entity CreateEye(World world, int channel = 0) {
        ArgumentNullException.ThrowIfNull(world);

        var eye = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        world.Add(eye, Camera.Perspective with { Order = channel });
        world.Add(eye, CameraDirector.Default with { Channel = channel });

        return eye;
    }

    /// <summary>The part of the screen one seat of a split screen owns, as fractions of it.</summary>
    /// <param name="seat">Which seat, counting from zero in the order the cameras are ranked.</param>
    /// <param name="seats">How many seats there are, from one to four.</param>
    /// <returns>The rect to write to <see cref="Camera.ViewportRect" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="seats" /> is outside one to four, or <paramref name="seat" /> is not one of
    ///     them.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>Two seats split horizontally and three or four into quadrants</b>, which is the
    ///         convention every console game of the form uses and for a reason worth writing down: a
    ///         vertical split of a 16:9 screen gives each player 8:9, which is narrower than tall and
    ///         frames a strip of floor with a person standing in it. Halving the height gives 32:9 —
    ///         wide, which is what a third-person camera wants.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Seat zero is the <em>top</em>.</b> A viewport's Y is measured down from the top
    ///         edge, unlike clip space, whose +1 is the top. That is the engine's stated convention
    ///         and it is the one place a split screen can silently come out upside down.
    ///     </para>
    ///     <para>
    ///         Three seats leave the fourth quadrant empty rather than giving one player a double-wide
    ///         bottom half. Both are shipped conventions; this one is chosen because the three
    ///         viewports then have the same shape, and a UI laid out for one seat is laid out for all
    ///         of them.
    ///     </para>
    /// </remarks>
    public static Rectangle SeatRect(int seat, int seats) {
        ArgumentOutOfRangeException.ThrowIfLessThan(seats, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seats, MaxSeats);
        ArgumentOutOfRangeException.ThrowIfNegative(seat);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(seat, seats);

        return seats switch {
            1 => new(0f, 0f, 1f, 1f),
            2 => new(0f, seat * 0.5f, 1f, 0.5f),
            _ => new((seat % 2) * 0.5f, (seat / 2) * 0.5f, 0.5f, 0.5f)
        };
    }

    /// <summary>How many seats a split screen holds.</summary>
    /// <remarks>
    ///     Four, which is what <c>PlayerSlots</c> holds and what <c>AudioListenerSet</c> mixes. A
    ///     fifth would be a quadrant split of a quadrant, and nothing else in the engine is built for
    ///     one.
    /// </remarks>
    public const int MaxSeats = 4;

    /// <summary>Gives each camera its own part of the screen, in the order they are listed.</summary>
    /// <param name="world">The world.</param>
    /// <param name="eyes">The cameras, one per seat, in seat order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There are more than <see cref="MaxSeats" />.</exception>
    /// <exception cref="ArgumentException">One of them is not a live entity carrying a camera.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The rect is half of what a split screen needs and this writes only that half.</b> The
    ///         other half is a <c>RenderView</c> per seat, which is the host's: a view's name is what a
    ///         compositor document binds a node to, so how many there are is a property of the frame
    ///         being drawn rather than of the world. <c>GraphicsOptions.Views</c> is where a game says
    ///         how many, and the host then adds one <c>CameraExtractionSystem</c> per view at
    ///         successive ranks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Seat order is the order of this span, and the frame's order is
    ///         <see cref="Camera.Order" />.</b> They agree when the cameras were made by
    ///         <see cref="CreateEye" />, which sets the order from the channel — and a caller passing
    ///         them out of order gets player two's picture in player one's half with nothing
    ///         reporting it. Passing the span in channel order is the whole of the contract.
    ///     </para>
    /// </remarks>
    public static void SplitScreen(World world, ReadOnlySpan<Entity> eyes) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eyes.Length, MaxSeats);

        for (var seat = 0; seat < eyes.Length; seat++) {
            if (!world.IsAlive(eyes[seat]) || !world.TryGet<Camera>(eyes[seat], out var camera)) {
                throw new ArgumentException($"{eyes[seat]} is not a camera.", nameof(eyes));
            }

            camera.ViewportRect = SeatRect(seat, eyes.Length);
            world.Set(eyes[seat], camera);
        }
    }

    static int ChannelOf(World world, Entity controller) {
        if (!world.IsAlive(controller) || !world.TryGet<PlayerController>(controller, out var player)) {
            throw new ArgumentException($"{controller} is not a player controller.", nameof(controller));
        }

        return player.CameraChannel;
    }
}
