// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;

namespace Vixen.Net.Replication;

/// <summary>Applies snapshots into a client's world.</summary>
/// <remarks>
///     <para>
///         The client keeps its own map from <see cref="NetworkId" /> to its own <c>Entity</c>,
///         because the two worlds have no reason to agree about handles. An id it has never seen is
///         an entity it creates; an id in the removal list is one it destroys.
///     </para>
///     <para>
///         <b>A snapshot that does not decode is not acknowledged.</b> Records are applied as they
///         are read, so a malformed one leaves the world part-way through — and that is fine, because
///         the tick is never acknowledged, so the server's baseline does not advance and everything
///         in it comes again. The alternative, buffering the whole snapshot to apply it atomically,
///         costs an allocation per tick to protect against a case that corrects itself.
///     </para>
/// </remarks>
public sealed class ReplicationClient {
    readonly ReplicationRegistry registry;
    readonly Dictionary<uint, Entity> entities = [];

    /// <summary>The newest tick that was applied without anything going wrong.</summary>
    public Tick AppliedTick { get; private set; }

    /// <summary>Whether anything has been applied at all.</summary>
    public bool HasApplied { get; private set; }

    /// <summary>How many networked entities this client is holding.</summary>
    public int EntityCount => entities.Count;

    /// <summary>Snapshots that failed to decode since this client was made.</summary>
    public int RejectedSnapshotCount { get; private set; }

    /// <summary>Creates a client-side applier.</summary>
    /// <param name="registry">The component types that may arrive. Anything else is refused.</param>
    public ReplicationClient(ReplicationRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    /// <summary>Finds the local entity a networked id names.</summary>
    /// <param name="id">The id.</param>
    /// <param name="entity">The local entity, if this client has one for it.</param>
    /// <returns>Whether it does.</returns>
    public bool TryGetEntity(NetworkId id, out Entity entity) => entities.TryGetValue(id.Value, out entity);

    /// <summary>Applies a snapshot.</summary>
    /// <param name="world">The client's world.</param>
    /// <param name="snapshot">The bytes as they arrived.</param>
    /// <returns>
    ///     Whether the snapshot decoded cleanly, and therefore whether it may be acknowledged. A
    ///     false must not be acknowledged: that is what makes the server send it all again.
    /// </returns>
    public bool TryApply(World world, ReadOnlySpan<byte> snapshot) {
        ArgumentNullException.ThrowIfNull(world);

        var reader = new BitReader(snapshot);

        if (!reader.TryReadUInt32(out var rawTick)) {
            RejectedSnapshotCount++;

            return false;
        }

        var tick = new Tick(rawTick);

        while (true) {
            if (!reader.TryReadBool(out var more)) {
                return Reject();
            }

            if (!more) {
                break;
            }

            if (!reader.TryReadVariable(out var removed)) {
                return Reject();
            }

            if (entities.Remove(removed, out var gone) && world.IsAlive(gone)) {
                world.Destroy(gone);
            }
        }

        while (true) {
            if (!reader.TryReadBool(out var more)) {
                return Reject();
            }

            if (!more) {
                break;
            }

            if (!reader.TryReadVariable(out var id)
                || !reader.TryReadVariable(out var typeIndex)
                || !registry.TryGetByIndex(typeIndex, out var replicator)
                || replicator is null) {
                // An index outside the manifest is not a type we can be talked into constructing.
                // The handshake's content hash, which the manifest hash folds into, is what stops
                // this from being a normal event.
                return Reject();
            }

            if (!replicator.Apply(world, EntityFor(world, id), ref reader)) {
                return Reject();
            }
        }

        if (!HasApplied || tick.IsAfter(AppliedTick)) {
            AppliedTick = tick;
            HasApplied = true;
        }

        return true;
    }

    /// <summary>Forgets everything, for a client that is reconnecting into a fresh world.</summary>
    public void Clear() {
        entities.Clear();
        HasApplied = false;
        AppliedTick = default;
    }

    Entity EntityFor(World world, uint id) {
        if (entities.TryGetValue(id, out var entity) && world.IsAlive(entity)) {
            return entity;
        }

        entity = world.Create(new NetworkId(id));
        entities[id] = entity;

        return entity;
    }

    bool Reject() {
        RejectedSnapshotCount++;

        return false;
    }
}
