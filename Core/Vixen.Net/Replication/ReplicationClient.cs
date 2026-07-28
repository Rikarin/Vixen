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
    readonly Dictionary<BaselineKey, CaptureRing> held = [];
    readonly byte[] rebuildScratch = new byte[4096];

    /// <summary>The newest tick that was applied without anything going wrong.</summary>
    public Tick AppliedTick { get; private set; }

    /// <summary>Whether anything has been applied at all.</summary>
    public bool HasApplied { get; private set; }

    /// <summary>How many networked entities this client is holding.</summary>
    public int EntityCount => entities.Count;

    /// <summary>Snapshots that failed to decode since this client was made.</summary>
    public int RejectedSnapshotCount { get; private set; }

    /// <summary>Snapshots that arrived after a newer one had already been applied, and were dropped.</summary>
    public int StaleSnapshotCount { get; private set; }

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

        if (HasApplied && !tick.IsAfter(AppliedTick)) {
            // Snapshots travel unreliably and therefore arrive out of order. An older one describes
            // the world before the one already applied, and applying it would put stale values on
            // top of fresh ones — invisibly, because the server would then be told a tick it does
            // not correspond to and would never send those values again.
            //
            // Dropping it loses nothing. A snapshot carries everything this connection has not
            // acknowledged, not just what changed on its own tick, so the newer one already
            // contained whatever this one had to say.
            StaleSnapshotCount++;

            return true;
        }

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

            Forget(new(removed));
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

            if (!reader.TryReadBool(out var aged)) {
                return Reject();
            }

            var age = 0u;

            if (aged && !reader.TryReadVariable(out age)) {
                return Reject();
            }

            if (!reader.TryReadBool(out var isDelta)) {
                return Reject();
            }

            var capturedAt = tick.Subtract((int)age);

            var applied = isDelta
                ? ApplyDelta(world, replicator, id, capturedAt, ref reader)
                : ApplyWhole(world, replicator, id, capturedAt, ref reader);

            if (!applied) {
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
        held.Clear();
        HasApplied = false;
        AppliedTick = default;
    }

    /// <summary>Takes a whole value, and keeps the bits it arrived as.</summary>
    /// <remarks>
    ///     <b>The bits, not the decoded value.</b> A later difference is measured from exactly what
    ///     the server sent, so what is kept has to be exactly that; re-encoding the component after
    ///     decoding it would put a quantized level through a float and back, and a level that came
    ///     home one different would corrupt every difference after it and nothing would say so.
    ///     Hence <see cref="BitReader.Rewind" />: decode, then go back and take the bits.
    /// </remarks>
    bool ApplyWhole(World world, IComponentReplicator replicator, uint id, Tick capturedAt, ref BitReader reader) {
        var key = new BaselineKey(new(id), replicator.TypeId);
        var start = reader.BitsRead;

        if (!replicator.Apply(world, EntityFor(world, id), ref reader)) {
            return false;
        }

        var length = reader.BitsRead - start;
        reader.Rewind(start);

        var slot = Reserve(key, length, capturedAt);
        var keeping = new BitWriter(slot.Bits);

        return reader.TryCopyTo(ref keeping, length);
    }

    /// <summary>Rebuilds a value from the capture the difference names, then takes it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Applied to the named capture, not to whatever is currently held.</b> The two are
    ///         often different: this client may have applied values it has not managed to acknowledge
    ///         yet, so the server is still measuring from an older one. Keeping a short history is
    ///         what makes that case correct rather than a silent corruption.
    ///     </para>
    ///     <para>
    ///         A difference naming a capture that has fallen out of the history cannot be applied, so
    ///         the snapshot is refused and therefore never acknowledged. The server's ring rolls past
    ///         that baseline within a few changes and sends the value whole, which is how this heals
    ///         without anything having to negotiate.
    ///     </para>
    /// </remarks>
    bool ApplyDelta(World world, IComponentReplicator replicator, uint id, Tick capturedAt, ref BitReader reader) {
        var key = new BaselineKey(new(id), replicator.TypeId);
        var lanes = replicator.Lanes;

        if (lanes.IsEmpty
            || !reader.TryRead(ReplicationServer.BaselineAgeBits, out var age)
            || !held.TryGetValue(key, out var ring)) {
            return false;
        }

        if (!ring.TryFind(capturedAt.Subtract((int)age + 1), out var previous)
            || previous is null
            || previous.BitCount != DeltaCodec.TotalBits(lanes)) {
            return false;
        }

        var baseline = new BitReader(previous.Bits);
        var rebuilt = new BitWriter(rebuildScratch);

        if (!DeltaCodec.TryDecode(lanes, ref baseline, ref reader, ref rebuilt)
            || !rebuilt.TryFinish(out var bits)) {
            return false;
        }

        var value = new BitReader(bits);

        if (!replicator.Apply(world, EntityFor(world, id), ref value)) {
            return false;
        }

        bits.CopyTo(Reserve(key, rebuilt.BitsWritten, capturedAt).Bits);

        return true;
    }

    Capture Reserve(in BaselineKey key, int bitCount, Tick capturedAt) {
        if (!held.TryGetValue(key, out var ring)) {
            ring = new();
            held[key] = ring;
        }

        var slot = ring.Advance((bitCount + 7) / 8);
        slot.BitCount = bitCount;
        slot.At = capturedAt;

        return slot;
    }

    void Forget(NetworkId id) {
        foreach (var replicator in registry.Replicators) {
            held.Remove(new(id, replicator.TypeId));
        }
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
