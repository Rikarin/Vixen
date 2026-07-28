// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Net.Motion;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>This entity moved discontinuously, and nothing may interpolate across it.</summary>
/// <remarks>
///     <para>
///         A respawn, a portal, a cutscene cut. Without it the receiving client draws the entity
///         sliding across the level at whatever speed the gap implies, over the interpolation delay
///         — which reads as the netcode being broken rather than as a teleport.
///     </para>
///     <para>
///         A tag rather than a flag on <see cref="NetworkTransform" />, because the entity that
///         teleported is the one that knows, and it knows for one tick. The bridge turns it into the
///         counter the wire carries and takes it off again, so nothing has to remember to clear it.
///     </para>
/// </remarks>
public struct NetworkTeleport : ITagComponent;

/// <summary>Copies the engine's transform into the one the network sends.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 16 said Vixen.Engine and Vixen.Net would first have to meet at.</b>
///         Everything above the wire speaks <c>LocalTransform</c>; everything on it speaks
///         <see cref="NetworkTransform" />. Something has to copy between them, and until now that
///         something was every game that wanted a physics object networked.
///     </para>
///     <para>
///         <b>The direction depends on which peer this is, and getting that wrong is not subtle for
///         long.</b> A server owns the truth and publishes it, so it copies transform → network. A
///         client receives the truth and displays it, so it copies network → transform. A single
///         system that did both would have each end overwriting the other every tick; on a client
///         with physics it also means the solver and the network fighting over the same body, which
///         looks like the object vibrating. Hence two systems, and a peer registers one of them.
///     </para>
///     <para>
///         <b>It costs nothing when nothing moved.</b> The query is filtered on
///         <c>WithChanged&lt;LocalTransform&gt;</c> and the sweep starts from the version last seen,
///         so a scene of a thousand sleeping props visits none of them. That is the same mechanism
///         <c>TransformSystem</c> uses and the reason the ECS carries change versions at all.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
public sealed class NetworkTransformCaptureSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription moved = new QueryDescription()
        .WithAll<LocalTransform, NetworkTransform, NetworkId>()
        .WithChanged<LocalTransform>();

    readonly QueryDescription teleported = new QueryDescription()
        .WithAll<NetworkTransform, NetworkTeleport>();

    readonly List<Entity> arriving = [];

    uint lastSeen;

    /// <inheritdoc />
    /// <remarks>
    ///     Declared at construction rather than with attributes, for the reason
    ///     <c>TransformSystem</c> gives: naming a component type in a generic call is what assigns it
    ///     an id, and an attribute can only look one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<LocalTransform>()
        .Read<NetworkId>()
        .Read<NetworkTeleport>()
        .Write<NetworkTransform>()
        .Build();

    /// <summary>How many transforms have been published.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>How many teleports have been turned into a counter bump.</summary>
    public long TeleportCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Publish(context.World);

        return dependency;
    }

    /// <summary>Publishes every transform that moved, and no others.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     Public so a server loop that drives its own schedule — which is what
    ///     <c>Samples/08</c> does — can call it directly rather than standing up a runner.
    /// </remarks>
    public void Publish(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var since = lastSeen;
        lastSeen = world.Version;

        // Teleports first. The counter has to be on the value this tick publishes, not the next one,
        // or the receiver interpolates across the jump and then hears about it.
        arriving.Clear();

        foreach (var chunk in world.Chunks(teleported)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                arriving.Add(entities[index]);
            }
        }

        foreach (var entity in arriving) {
            // Wraps at 256, deliberately: the receiver compares it against what it last saw, so what
            // matters is that it changed. A counter that saturated would stop reporting teleports
            // after the two hundred and fifty-sixth respawn.
            world.Get<NetworkTransform>(entity).TeleportCount++;
            world.Remove<NetworkTeleport>(entity);
            TeleportCount++;
        }

        foreach (var chunk in world.Chunks(moved, since)) {
            var locals = chunk.ReadValues<LocalTransform>();
            var networked = chunk.Values<NetworkTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                networked[index].Position = locals[index].Position;
                networked[index].Rotation = locals[index].Rotation;
            }

            PublishedCount += chunk.Count;
        }
    }
}

/// <summary>Copies what the network received into the engine's transform.</summary>
/// <remarks>
///     <para>
///         The client half of <see cref="NetworkTransformCaptureSystem" />. Runs on a peer that
///         receives transforms rather than publishing them, and it is the <i>only</i> thing that
///         should be writing those entities' transforms — see that type's remarks for what happens
///         when both directions run at once.
///     </para>
///     <para>
///         <b>Scale is deliberately not touched.</b> <see cref="NetworkTransform" /> carries a
///         position and a rotation and no scale, because scale changes rarely and belongs in a
///         <c>[Replicated]</c> component of the game's own when it changes at all — putting it on
///         the wire for every object every tick to serve the few that resize is the trade doc 16
///         makes for the position too, in the other direction. A local scale set by a prefab
///         therefore survives, which is what anyone would expect.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
public sealed class NetworkTransformApplySystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription received = new QueryDescription()
        .WithAll<LocalTransform, NetworkTransform, NetworkId>()
        .WithChanged<NetworkTransform>();

    uint lastSeen;

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkTransform>()
        .Read<NetworkId>()
        .Write<LocalTransform>()
        .Build();

    /// <summary>How many transforms have been applied.</summary>
    public long AppliedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Apply(context.World);

        return dependency;
    }

    /// <summary>Applies every transform that arrived, and no others.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Apply(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var since = lastSeen;
        lastSeen = world.Version;

        foreach (var chunk in world.Chunks(received, since)) {
            var networked = chunk.ReadValues<NetworkTransform>();
            var locals = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                locals[index].Position = networked[index].Position;
                locals[index].Rotation = networked[index].Rotation;
            }

            AppliedCount += chunk.Count;
        }
    }
}
