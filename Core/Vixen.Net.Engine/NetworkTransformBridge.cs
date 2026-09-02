// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
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

    // Unfiltered on purpose, and the two halves of one question. Reparenting writes Parent and does
    // not touch LocalTransform, so a change-filtered query would publish a rider's seat offset and
    // never say which vehicle it is an offset from. What keeps this cheap is that the archetypes are
    // narrow: a networked entity that is a child of anything at all is rare, and one that carries a
    // frame while having no parent is rarer.
    readonly QueryDescription framed = new QueryDescription().WithAll<NetworkTransform, NetworkId, Parent>();

    readonly QueryDescription unframed = new QueryDescription().WithAll<NetworkTransform, NetworkId, NetworkParent>()
        .WithNone<Parent>();

    readonly List<Entity> arriving = [];
    readonly List<(Entity Entity, uint Frame)> reframed = [];

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
        .Read<Parent>()
        .Write<NetworkTransform>()
        .Write<NetworkParent>()
        .Build();

    /// <summary>How many transforms have been published.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>How many teleports have been turned into a counter bump.</summary>
    public long TeleportCount { get; private set; }

    /// <summary>How many times an entity has been said to be in a different frame.</summary>
    /// <remarks>
    ///     Two per mount: the rider entering the vehicle's frame and leaving it again. A number that
    ///     climbs every tick is a hierarchy being rebuilt every tick, which costs a reliable record
    ///     per entity per tick and is worth finding.
    /// </remarks>
    public long ReframedCount { get; private set; }

    /// <summary>
    ///     How many entities are parented to something the wire cannot name, and are therefore
    ///     published in world space.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The case that used to be silently wrong.</b> <c>LocalTransform</c> is relative to
    ///         the parent, and this system published it verbatim — so a networked entity hanging off
    ///         a purely local parent sent an offset and the receiver read it as a world position.
    ///         Nothing said so; the object was simply somewhere else, by however far the parent was
    ///         from the origin.
    ///     </para>
    ///     <para>
    ///         There is no honest way to quote a frame the other end cannot name, so those are
    ///         resolved to world coordinates instead, which is correct and costs a matrix walk per
    ///         level of depth. This counter is what makes the cost visible: a game seeing a large
    ///         number here should give the parent a <see cref="NetworkId" /> and pay nothing.
    ///     </para>
    /// </remarks>
    public long UnnameableFrameCount { get; private set; }

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

        PublishFrames(world);
    }

    /// <summary>Says which frame each parented entity's transform is quoted in.</summary>
    /// <remarks>
    ///     <para>
    ///         Last, so it has the last word: an entity whose frame cannot be named has the
    ///         <c>LocalTransform</c> the pass above published replaced with world coordinates, which
    ///         is the only thing that is true on both ends.
    ///     </para>
    ///     <para>
    ///         <b>Structural changes are collected and applied afterwards</b>, for the reason the
    ///         teleport pass gives: adding a component to an entity while its chunk is being walked
    ///         moves it into another chunk.
    ///     </para>
    /// </remarks>
    void PublishFrames(World world) {
        reframed.Clear();

        foreach (var chunk in world.Chunks(framed)) {
            var entities = chunk.Entities;
            var parents = chunk.ReadValues<Parent>();

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];
                var frame = world.TryGet<NetworkId>(parents[index].Value, out var id) ? id.Value : 0u;

                if (frame == 0) {
                    Flatten(world, entity);
                }

                var had = world.TryGet<NetworkParent>(entity, out var current) ? current.Value : 0u;

                if (had != frame && (frame != 0 || world.Has<NetworkParent>(entity))) {
                    reframed.Add((entity, frame));
                }
            }
        }

        // The other half: an entity that was in a frame and is no longer parented to anything. The
        // query above cannot see it, because it no longer has a Parent — and without this the rider
        // who got off is still quoted in the vehicle's frame for ever.
        foreach (var chunk in world.Chunks(unframed)) {
            var entities = chunk.Entities;
            var frames = chunk.ReadValues<NetworkParent>();

            for (var index = 0; index < chunk.Count; index++) {
                if (frames[index].Value != 0) {
                    reframed.Add((entities[index], 0u));
                }
            }
        }

        foreach (var (entity, frame) in reframed) {
            if (world.Has<NetworkParent>(entity)) {
                world.Set(entity, new NetworkParent { Value = frame });
            } else {
                world.Add(entity, new NetworkParent { Value = frame });
            }

            ReframedCount++;
        }
    }

    /// <summary>Publishes an entity in world space, for a frame the wire has no name for.</summary>
    void Flatten(World world, Entity entity) {
        UnnameableFrameCount++;

        // Resolved by walking the parent chain rather than read from WorldTransform, because this
        // runs in FixedUpdate and TransformSystem resolves that column in PreRender — so the column
        // holds where the entity was at the end of the previous frame, which for a rider on a moving
        // vehicle is exactly one frame of lag added to whatever the network already costs.
        if (!Matrix4x4.Decompose(Hierarchy.ResolveWorldMatrix(world, entity), out _, out var rotation, out var position)) {
            // A parent scaled to zero on some axis. There is no rotation to recover, so the position
            // is taken and the rotation left as it was rather than replaced with a wrong one.
            world.Get<NetworkTransform>(entity).Position = position;

            return;
        }

        ref var networked = ref world.Get<NetworkTransform>(entity);
        networked.Position = position;
        networked.Rotation = rotation;
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
        .WithChanged<NetworkTransform>()
        .WithNone<NetworkParent>();

    // Unfiltered, and it has to be. An entity waiting for its frame is one whose NetworkTransform
    // stopped changing — the value arrived, it simply could not be used — so a change-filtered query
    // would look at it exactly once, on the tick it could not be placed, and never again.
    readonly QueryDescription anchored = new QueryDescription()
        .WithAll<LocalTransform, NetworkTransform, NetworkId, NetworkParent>();

    readonly List<(Entity Entity, uint Frame)> pending = [];

    uint lastSeen;

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkTransform>()
        .Read<NetworkId>()
        .Read<NetworkParent>()
        .Write<LocalTransform>()
        .Build();

    /// <summary>The client whose id-to-entity map names the frames. Optional; without it there are none.</summary>
    /// <remarks>
    ///     <para>
    ///         The same seam <c>NetworkSpawnSystem.Client</c> is, and for the same reason: a
    ///         <see cref="NetworkParent" /> is a <c>NetworkId</c>, and only the client holds the map
    ///         from one of those to a local entity.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Without it every frame is unresolved</b>, which means every parented entity is
    ///         held still rather than being put somewhere wrong. That is the right way round — a
    ///         peer that has not been given a client cannot place a rider and should not guess — but
    ///         it is also why <see cref="UnresolvedFrameCount" /> climbing steadily is the first
    ///         thing to look at when parented objects do not move.
    ///     </para>
    /// </remarks>
    public ReplicationClient? Client { get; set; }

    /// <summary>How many transforms have been applied.</summary>
    public long AppliedCount { get; private set; }

    /// <summary>How many times a transform was held because the frame it is quoted in is not here yet.</summary>
    /// <remarks>
    ///     A handful per mount is ordinary: the frame and the transform travel as separate records
    ///     and the vehicle may not have been spawned locally yet. A number that keeps climbing is a
    ///     frame that will never arrive — an interest rule that sends the rider and not the vehicle,
    ///     which is the failure this counter exists to name.
    /// </remarks>
    public long UnresolvedFrameCount { get; private set; }

    /// <summary>How many times an entity has been moved into or out of a replicated frame.</summary>
    public long ReparentedCount { get; private set; }

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

        ApplyFrames(world);
    }

    /// <summary>Places the entities whose transform is quoted in another entity's frame.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An entity whose frame has not arrived is not placed at all.</b> The numbers are a
    ///         seat offset — a metre and a half up and half a metre back — and read as world
    ///         coordinates they put the rider at the middle of the map, near the ground, for however
    ///         many ticks the vehicle takes to arrive. Holding it where it was costs a rider who is
    ///         briefly stale and gains one who is never in the wrong place, and staleness is what the
    ///         snapshot buffer is for.
    ///     </para>
    ///     <para>
    ///         <b>The hierarchy is made to match the frame rather than assumed to.</b> The receiving
    ///         peer has no other instruction to parent anything: a rider that mounted after it was
    ///         spawned is a root here whatever it is there, and writing a seat offset into the local
    ///         transform of a root is the same wrong answer by another route.
    ///     </para>
    /// </remarks>
    void ApplyFrames(World world) {
        pending.Clear();

        foreach (var chunk in world.Chunks(anchored)) {
            var entities = chunk.Entities;
            var frames = chunk.ReadValues<NetworkParent>();

            for (var index = 0; index < chunk.Count; index++) {
                pending.Add((entities[index], frames[index].Value));
            }
        }

        foreach (var (entity, frame) in pending) {
            var parent = Entity.Null;

            if (frame != 0) {
                if (Client is not { } client
                    || !client.TryGetEntity(new(frame), out parent)
                    || !world.IsAlive(parent)
                    || parent == entity) {
                    UnresolvedFrameCount++;

                    continue;
                }
            }

            if (Hierarchy.ParentOf(world, entity) != parent) {
                // SetParent and not SetParentKeepingWorldPosition. The local transform about to be
                // written is the whole point: keeping the world position would rewrite it to hold
                // the entity still, and the next line would overwrite that with the value off the
                // wire anyway — one frame of the entity being in two places for no benefit.
                Hierarchy.SetParent(world, entity, parent);
                ReparentedCount++;
            }

            ref readonly var networked = ref world.Read<NetworkTransform>(entity);
            ref var local = ref world.Get<LocalTransform>(entity);
            local.Position = networked.Position;
            local.Rotation = networked.Rotation;
            AppliedCount++;
        }
    }
}
