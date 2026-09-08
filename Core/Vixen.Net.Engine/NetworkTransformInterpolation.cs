// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Time;

namespace Vixen.Net.Engine;

/// <summary>Draws received objects where they were, not where the last packet said they are.</summary>
/// <remarks>
///     <para>
///         <b>The engine's answer to "who owns interpolation", and it had not been given one.</b>
///         <c>Vixen.Net</c> shipped <see cref="SnapshotBuffer" /> — the ring, the interpolation, the
///         clamped extrapolation, the teleport-aware snap — and nothing in <c>Core/</c> ever built
///         one: the only two construction sites in the repository were a unit test and
///         <c>Samples/08-Multiplayer</c>, which drives the whole thing by hand. Meanwhile
///         <see cref="NetworkTransformApplySystem" />, the system a game reaches for, wrote the
///         received pose straight into <see cref="LocalTransform" /> with no buffering and no
///         interpolation delay, so the engine's own bridge was the *un*smoothed path and the smoothed
///         one had no system. See <see href="https://github.com/Rikarin/Vixen/issues/467" />.
///     </para>
///     <para>
///         <b>It runs after the apply system rather than instead of it</b>, and the split is the whole
///         design. The apply system resolves <see cref="NetworkParent" /> frames, reparents, and
///         places anything this one has nothing to say about; this overwrites the placement for the
///         entities it holds a buffer for. A game that wants the raw path adds only the apply system,
///         which is what it always had; a game that wants smoothing adds both. Nothing has two
///         policies for one question.
///     </para>
///     <para>
///         <b>One sample per applied tick, for every networked object, whether or not it moved.</b>
///         "It did not change" is as much a fact about where something is at that tick as a new
///         position would be, and the buffer drops anything not newer than what it holds, so
///         re-adding costs a comparison. Sampling only what the snapshot mentioned would leave a
///         still object with one sample and a buffer that reports itself starved for ever.
///     </para>
///     <para>
///         ⚠ <b>An entity whose <see cref="NetworkParent" /> has not arrived is left alone.</b> The
///         buffer holds what <see cref="NetworkTransform" /> said, which for a parented entity is a
///         seat offset — and a seat offset written as a world position puts the rider at the middle
///         of the map. The apply system already holds those still and counts them; this must not
///         undo that by interpolating between two offsets and placing the result in world space.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(NetworkTransformApplySystem))]
public sealed class NetworkTransformInterpolateSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription received = new QueryDescription().WithAll<LocalTransform, NetworkTransform, NetworkId>();

    readonly ReplicationClient client;
    readonly TickManager clock;
    readonly SnapshotBufferOptions? options;
    readonly int capacity;

    readonly Dictionary<uint, SnapshotBuffer> buffers = [];
    readonly HashSet<uint> present = [];
    readonly List<uint> departed = [];

    Tick fed;
    bool hasFed;

    /// <summary>Creates the system.</summary>
    /// <param name="client">
    ///     What has been applied. Its <see cref="ReplicationClient.AppliedTick" /> is what a sample is
    ///     keyed by, and its id-to-entity map is what resolves a replicated frame.
    /// </param>
    /// <param name="clock">
    ///     The session's clock. <see cref="TickManager.InterpolationTick" /> and
    ///     <see cref="TickManager.Alpha" /> are the moment being drawn.
    /// </param>
    /// <param name="options">How a buffer behaves when it runs out. Defaults if null.</param>
    /// <param name="capacity">How many samples each object keeps.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client" /> or <paramref name="clock" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Both are constructor arguments rather than the settable properties the neighbouring
    ///     systems use.</b> Either one missing leaves this with nothing it can do at all — no tick to
    ///     key a sample by, no moment to sample at — and a system that silently does nothing is
    ///     precisely the defect this class was written to close. An optional seam is right where the
    ///     absence degrades behaviour; here it would erase it.
    /// </remarks>
    public NetworkTransformInterpolateSystem(
        ReplicationClient client,
        TickManager clock,
        SnapshotBufferOptions? options = null,
        int capacity = 32
    ) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);

        this.client = client;
        this.clock = clock;
        this.options = options;
        this.capacity = capacity;
    }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkTransform>()
        .Read<NetworkId>()
        .Read<NetworkParent>()
        .Write<LocalTransform>()
        .Build();

    /// <summary>How many objects have a buffer.</summary>
    public int BufferedCount => buffers.Count;

    /// <summary>How many transforms have been placed from a buffer.</summary>
    public long SampledCount { get; private set; }

    /// <summary>How many objects have been forgotten because they left this peer's world.</summary>
    /// <remarks>
    ///     The pair to <see cref="BufferedCount" />. Leaving interest and being destroyed are the same
    ///     thing to a client, so an object that walked over the horizon is evicted here — and a
    ///     buffered count that only ever grows is a session-long leak of thirty-two poses per object
    ///     that has ever been seen.
    /// </remarks>
    public long EvictedCount { get; private set; }

    /// <summary>
    ///     How many samples had nothing to sit between and were held at the newest pose instead.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Worth watching, and its healthy value is not zero.</b> An object is starved for the
    ///     first few ticks after it arrives, which is one buffer's worth per spawn. A number that
    ///     climbs with the match is an interpolation delay shorter than the connection's jitter, and
    ///     what it looks like on screen is motion that stutters rather than stops —
    ///     <see cref="TickManager.InterpolationDelayTicks" /> is derived from measured jitter and is
    ///     the thing to read beside it.
    /// </remarks>
    public long StarvedCount { get; private set; }

    /// <summary>How many were left where the apply system put them, their frame not having arrived.</summary>
    public long UnresolvedFrameCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Advance(context.World);

        return dependency;
    }

    /// <summary>Takes a sample of everything, then draws everything at the interpolation tick.</summary>
    /// <param name="world">The client's world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Advance(World world) {
        ArgumentNullException.ThrowIfNull(world);

        // ⚠ Once per *applied* tick, not once per frame. A frame runs at whatever rate the display
        // does and a snapshot arrives at the session's; feeding per frame would fill the ring with
        // duplicates of one tick under the buffer's own "no older than what I hold" rule — which
        // costs nothing and hides the fact that only one of them was ever a new fact.
        var sampling = client.HasApplied && (!hasFed || client.AppliedTick.IsAfter(fed));

        if (sampling) {
            fed = client.AppliedTick;
            hasFed = true;
        }

        present.Clear();

        var tick = clock.InterpolationTick;
        var alpha = clock.Alpha;

        foreach (var chunk in world.Chunks(received)) {
            var entities = chunk.Entities;
            var ids = chunk.ReadValues<NetworkId>();
            var networked = chunk.ReadValues<NetworkTransform>();
            var locals = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                var id = ids[index];

                if (!id.IsValid) {
                    continue;
                }

                present.Add(id.Value);

                if (!buffers.TryGetValue(id.Value, out var buffer)) {
                    buffer = new(capacity, options);
                    buffers[id.Value] = buffer;
                }

                if (sampling) {
                    buffer.Add(
                        new(fed, networked[index].Position, networked[index].Rotation, networked[index].TeleportCount)
                    );
                }

                if (!CanPlace(world, entities[index])) {
                    UnresolvedFrameCount++;

                    continue;
                }

                if (!buffer.TrySample(tick, alpha, out var drawn)) {
                    // Nothing has arrived at all. The apply system's placement, or the prefab's own
                    // transform, is a better answer than the origin.
                    StarvedCount++;

                    continue;
                }

                locals[index].Position = drawn.Position;
                locals[index].Rotation = drawn.Rotation;
                SampledCount++;
            }
        }

        Forget();
    }

    /// <summary>The buffer an object's poses are kept in, for a diagnostic that wants to draw it.</summary>
    /// <param name="id">The object.</param>
    /// <param name="buffer">Its buffer, if it has one.</param>
    /// <returns>Whether it does.</returns>
    public bool TryGetBuffer(NetworkId id, out SnapshotBuffer? buffer) => buffers.TryGetValue(id.Value, out buffer);

    /// <summary>Forgets everything, for a peer that has reconnected into a different world.</summary>
    public void Clear() {
        buffers.Clear();
        hasFed = false;
    }

    bool CanPlace(World world, Entity entity) {
        if (!world.TryGet<NetworkParent>(entity, out var frame) || frame.Value == 0) {
            return true;
        }

        return client.TryGetEntity(new(frame.Value), out var parent) && world.IsAlive(parent) && parent != entity;
    }

    void Forget() {
        departed.Clear();

        foreach (var id in buffers.Keys) {
            if (!present.Contains(id)) {
                departed.Add(id);
            }
        }

        foreach (var id in departed) {
            buffers.Remove(id);
            EvictedCount++;
        }
    }
}
