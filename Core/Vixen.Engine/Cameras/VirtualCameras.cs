// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Cameras;

/// <summary>Making shots, and the checks that are worth making once rather than every frame.</summary>
public static class VirtualCameras {
    /// <summary>Creates a shot with its transform, its targets and the state the stages write.</summary>
    /// <param name="world">The world.</param>
    /// <param name="camera">The shot's settings.</param>
    /// <param name="targets">What it follows and looks at.</param>
    /// <param name="local">
    ///     Where the shot's own entity sits — where it looks from when it has no body — or
    ///     <see langword="null" /> for the origin.
    /// </param>
    /// <returns>The new entity.</returns>
    public static Entity Create(
        World world,
        VirtualCamera camera,
        CameraTargets targets = default,
        LocalTransform? local = null
    ) {
        ArgumentNullException.ThrowIfNull(world);

        var entity = Hierarchy.CreateTransform(world, local ?? LocalTransform.Identity);

        world.Add(entity, camera);
        world.Add(entity, targets);
        world.Add(entity, default(CameraShot));

        return entity;
    }

    /// <summary>Whether a shot has at most one body stage and at most one aim stage.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The shot.</param>
    /// <returns><see langword="true" /> if the shot is well formed.</returns>
    /// <remarks>
    ///     For an inspector to grey a second body out with, and for a test to assert on. The frame
    ///     loop does not call it: see <see cref="VirtualCamera" />'s remarks for why a mistake made
    ///     once is not worth an archetype interrogation sixty times a second.
    /// </remarks>
    public static bool Validate(World world, Entity entity) => BodyCount(world, entity) <= 1
        && AimCount(world, entity) <= 1;

    /// <summary>How many body stages a shot carries. More than one is a configuration error.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The shot.</param>
    /// <returns>The count.</returns>
    public static int BodyCount(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return (world.Has<FollowBody>(entity) ? 1 : 0)
            + (world.Has<FramingBody>(entity) ? 1 : 0)
            + (world.Has<OrbitBody>(entity) ? 1 : 0)
            + (world.Has<HardLockBody>(entity) ? 1 : 0);
    }

    /// <summary>How many aim stages a shot carries. More than one is a configuration error.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The shot.</param>
    /// <returns>The count.</returns>
    public static int AimCount(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return (world.Has<ComposerAim>(entity) ? 1 : 0)
            + (world.Has<HardLookAim>(entity) ? 1 : 0)
            + (world.Has<PovAim>(entity) ? 1 : 0)
            + (world.Has<MatchTargetAim>(entity) ? 1 : 0);
    }
}
