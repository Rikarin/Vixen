// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Sessions;

namespace Vixen.Net.Replication;

/// <summary>Turns a world into a snapshot per connection, once a tick.</summary>
/// <remarks>
///     <para>
///         The shape of a tick is <see cref="Capture" /> then one <see cref="TryWriteSnapshot" /> per
///         connection, and the split is the point. Capture reads the world, quantizes and packs each
///         component that changed, and does it <b>once</b>; a snapshot is then a copy of those bits
///         for the connections that do not already have them. Fifty players cost fifty memcpys and
///         one encode, rather than fifty encodes.
///     </para>
///     <para>
///         <b>What to send is decided by comparison, not by a dirty flag.</b> The ECS's per-chunk
///         change versions say which chunks are worth looking at — the cheap filter, and the main
///         structural reason for having built an ECS with them — and a hash of the encoded value says
///         which entities within those chunks actually differ from what this connection has
///         acknowledged. The first is O(chunks) and skips almost everything; the second is exact.
///     </para>
///     <para>
///         <b>The budget sheds rather than truncates.</b> Records are written in priority order and
///         the writer is rewound if one would take the snapshot over the budget, so a snapshot is
///         always a whole number of complete records. What was shed was never acknowledged, so it is
///         simply included in the next one — the loss and the shed take the same path out.
///     </para>
/// </remarks>
public sealed class ReplicationServer {
    /// <summary>
    ///     How many bits name which capture a difference was measured from, counted back from the
    ///     one it produces.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A fixed field rather than a variable-length integer, because a varint spends a whole
    ///         byte to say "the tick before this one" and that is the usual answer.
    ///     </para>
    ///     <para>
    ///         <b>It is counted in ticks, not in captures</b>, and that distinction is a bug that was
    ///         written before it was thought about. The history advances when a value <i>changes</i>,
    ///         so two neighbouring captures of something that changes rarely can be a hundred ticks
    ///         apart even though they are next to each other in the ring — and the receiver has its
    ///         own ring with its own contents, so counting positions instead is not open either.
    ///         Ticks it is, and a difference reaching further back than this field can say is simply
    ///         not offered: sixteen ticks is half a second, which covers any round trip a game is
    ///         playable over, and a value that has not changed in half a second is not the one the
    ///         bandwidth is going on.
    ///     </para>
    /// </remarks>
    public const int BaselineAgeBits = 4;

    /// <summary>The furthest back, in ticks, a difference may be measured from.</summary>
    public const int MaxBaselineAge = 1 << BaselineAgeBits;

    readonly ReplicationRegistry registry;
    readonly IInterestResolver interest;
    readonly Dictionary<BaselineKey, CaptureRing> current = [];
    readonly Dictionary<DeltaKey, Cached> memo = [];
    readonly Dictionary<uint, Connection> connections = [];
    readonly List<IComponentReplicator> byPriority = [];
    readonly Dictionary<uint, uint> wireIndex = [];
    readonly List<Entity> observed = [];
    readonly HashSet<uint> observedIds = [];
    readonly List<NetworkId> finished = [];
    readonly List<uint> stamping = [];
    readonly byte[] encodeScratch = new byte[4096];
    readonly byte[] deltaScratch = new byte[4096];

    uint capturedVersion;

    /// <summary>How much each snapshot may cost.</summary>
    public BandwidthBudget Budget { get; set; } = new();

    /// <summary>How many component values are being tracked across every entity.</summary>
    public int TrackedValueCount => current.Count;

    /// <summary>What was captured at the last <see cref="Capture" />.</summary>
    public int LastCapturedCount { get; private set; }

    /// <summary>Records sent as a difference from the value the connection already held.</summary>
    public long DeltaRecordCount { get; private set; }

    /// <summary>
    ///     Records sent whole, because the connection did not hold the value the difference was
    ///     measured from — or because the component does not declare a layout to difference over.
    /// </summary>
    public long WholeRecordCount { get; private set; }

    /// <summary>Creates a server-side replicator.</summary>
    /// <param name="registry">The component types that may be replicated.</param>
    /// <param name="interest">Who is told about what. Everything, if null.</param>
    public ReplicationServer(ReplicationRegistry registry, IInterestResolver? interest = null) {
        ArgumentNullException.ThrowIfNull(registry);

        this.registry = registry;
        this.interest = interest ?? new ReplicateEverythingResolver();

        byPriority.AddRange(registry.Replicators);

        // Highest priority first, so that when the budget runs out it is the tail of this list that
        // goes. Stable within a priority, so two runs of the same tick shed the same things.
        byPriority.Sort((left, right) => right.Priority.CompareTo(left.Priority));

        foreach (var replicator in byPriority) {
            wireIndex[replicator.TypeId] = (uint)registry.IndexOf(replicator.TypeId);
        }
    }

    /// <summary>
    ///     Reads everything that changed since the last call, and encodes it once.
    /// </summary>
    /// <param name="world">The server's world.</param>
    /// <param name="capturedAt">
    ///     The tick this reading of the world belongs to. Recorded against each value so a
    ///     difference can name the capture it was measured from, and so a receiver files what
    ///     arrives under the tick it describes rather than the tick it turned up in.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Call once a tick, after the simulation has run and before the snapshots go out.
    ///     </para>
    ///     <para>
    ///         <b>The world's version must not advance between a write and the capture that should
    ///         see it.</b> A capture takes everything written since the previous capture's version,
    ///         so the order within a tick has to be advance–write–capture (what the scheduler does)
    ///         or write–capture–advance. Advancing in between puts the write on the far side of the
    ///         comparison and it is never sent: the client simply never learns about that change,
    ///         and nothing reports an error. It is an off-by-one that looks like a rare desync, so
    ///         it is stated here and asserted by the tests rather than left to be discovered.
    ///     </para>
    /// </remarks>
    public void Capture(World world, Tick capturedAt = default) {
        ArgumentNullException.ThrowIfNull(world);

        LastCapturedCount = 0;

        // A difference is only ever sent within the round of snapshots that follows the capture
        // it was measured against, so nothing here outlives a tick.
        memo.Clear();

        foreach (var replicator in byPriority) {
            foreach (var chunk in world.Chunks(replicator.ChangedQuery, capturedVersion)) {
                foreach (var entity in chunk.Entities) {
                    if (!world.Has<NetworkId>(entity)) {
                        continue;
                    }

                    var id = world.Read<NetworkId>(entity);

                    if (!id.IsValid) {
                        continue;
                    }

                    var writer = new BitWriter(encodeScratch);
                    replicator.Write(world, entity, ref writer);

                    if (!writer.TryFinish(out var bits)) {
                        // A component larger than the encode scratch is a bug in the replicator, not
                        // a bandwidth problem. Skipping it keeps the tick running; it will be caught
                        // by the state never converging, which is what the convergence test asserts.
                        continue;
                    }

                    Store(new(id, replicator.TypeId), bits, writer.BitsWritten, capturedAt);
                    LastCapturedCount++;
                }
            }
        }

        capturedVersion = world.Version;
    }

    /// <summary>Writes one connection's snapshot.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="player">Who it is for.</param>
    /// <param name="tick">The tick it describes.</param>
    /// <param name="buffer">Where to write it.</param>
    /// <param name="snapshot">The snapshot, if there was anything to say.</param>
    /// <returns>
    ///     Whether anything was written. False means this connection is already up to date, and a
    ///     tick that says nothing is a tick that costs nothing.
    /// </returns>
    public bool TryWriteSnapshot(
        World world,
        PlayerId player,
        Tick tick,
        Span<byte> buffer,
        out ReadOnlySpan<byte> snapshot
    ) {
        ArgumentNullException.ThrowIfNull(world);
        snapshot = default;

        var connection = ConnectionFor(player);
        observed.Clear();
        observedIds.Clear();
        interest.Resolve(world, player, observed);

        foreach (var entity in observed) {
            if (world.Has<NetworkId>(entity)) {
                observedIds.Add(world.Read<NetworkId>(entity).Value);
            }
        }

        MarkWhatLeft(connection, tick);

        var budget = Math.Min(buffer.Length, Budget.BytesPerSnapshot);
        var writer = new BitWriter(buffer);
        writer.WriteUInt32(tick.Value);

        var wrote = WriteRemovals(connection, ref writer, budget);
        wrote |= WriteRecords(connection, tick, ref writer, budget);

        writer.WriteBool(false); // no more records

        if (!writer.TryFinish(out var packet) || !wrote) {
            return false;
        }

        snapshot = packet;

        return true;
    }

    /// <summary>Takes a client's acknowledgement of a snapshot it applied.</summary>
    /// <param name="player">Who acknowledged.</param>
    /// <param name="tick">The newest tick they applied cleanly.</param>
    public void Acknowledge(PlayerId player, Tick tick) => ConnectionFor(player).Baseline.Acknowledge(tick);

    /// <summary>Stops tracking an entity, and tells everybody holding it to drop it.</summary>
    /// <param name="id">The entity.</param>
    /// <remarks>
    ///     Called when a networked entity is destroyed, to drop what was captured for it. Telling the
    ///     clients is not this method's job and does not need to be: an entity that is gone is an
    ///     entity the interest resolver stops returning, and leaving interest already means "drop
    ///     it". Destruction and walking over the horizon are then the same mechanism, which is the
    ///     only way they stay consistent with each other.
    /// </remarks>
    public void Despawn(NetworkId id) {
        foreach (var replicator in byPriority) {
            current.Remove(new(id, replicator.TypeId));
        }
    }

    /// <summary>Forgets a connection entirely.</summary>
    /// <param name="player">Who left.</param>
    public void Forget(PlayerId player) => connections.Remove(player.Value);

    /// <summary>What a connection is known to hold, for diagnostics and for tests.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their baseline, created if this is the first time they have been asked about.</returns>
    public ConnectionBaseline BaselineOf(PlayerId player) => ConnectionFor(player).Baseline;

    void Store(in BaselineKey key, ReadOnlySpan<byte> bits, int bitCount, Tick capturedAt) {
        if (!current.TryGetValue(key, out var ring)) {
            ring = new();
            current[key] = ring;
        }

        var slot = ring.Advance(bits.Length);
        bits.CopyTo(slot.Bits);
        slot.BitCount = bitCount;
        slot.Hash = Hash(bits);
        slot.At = capturedAt;
    }

    /// <summary>Encodes the difference between a connection's baseline and the newest capture.</summary>
    /// <param name="replicator">Which component.</param>
    /// <param name="key">Which entity's which component, so the scratch can be keyed by it.</param>
    /// <param name="ring">Its recent captures.</param>
    /// <param name="baseline">The capture the connection has acknowledged.</param>
    /// <param name="bitCount">How long the difference is.</param>
    /// <returns>The bits, or empty if a difference is not worth sending or not possible.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Encoded once per distinct baseline, not once per connection.</b> Connections that
    ///         have acknowledged the same capture — which is most of them, most of the time, because
    ///         they are all about the same distance behind — share the encoding. The scratch is keyed
    ///         by the baseline's tick so a second connection asking for the same one gets the answer
    ///         the first one paid for.
    ///     </para>
    ///     <para>
    ///         Kept only if it is smaller than the value itself. A value that jumped encodes as a
    ///         difference a few bits longer than sending it whole, and there is no reason to prefer
    ///         the longer one.
    ///     </para>
    /// </remarks>
    ReadOnlySpan<byte> Difference(
        IComponentReplicator replicator,
        BaselineKey key,
        CaptureRing ring,
        Capture baseline,
        out int bitCount
    ) {
        bitCount = 0;

        var newest = ring.Newest;
        var lanes = replicator.Lanes;

        // Lanes that do not add up to what Write produced describe a different encoding from the one
        // that was written, and a difference measured through them would be wrong in a way nothing
        // would report. The check is two integers, and it is what makes a mistaken layout cost
        // bandwidth instead of correctness.
        if (lanes.IsEmpty
            || baseline.BitCount != newest.BitCount
            || DeltaCodec.TotalBits(lanes) != newest.BitCount) {
            return default;
        }

        var memoKey = new DeltaKey(key, baseline.At.Value);

        if (memo.TryGetValue(memoKey, out var cached)) {
            bitCount = cached.BitCount;

            return cached.Bits.AsSpan(0, (cached.BitCount + 7) / 8);
        }

        var previous = new BitReader(baseline.Bits);
        var now = new BitReader(newest.Bits);
        var delta = new BitWriter(deltaScratch);

        if (!DeltaCodec.TryEncode(lanes, ref previous, ref now, ref delta)
            || !delta.TryFinish(out var bits)
            || delta.BitsWritten >= newest.BitCount) {
            return default;
        }

        bitCount = delta.BitsWritten;
        Remember(memoKey, bits, bitCount);

        return bits;
    }

    void Remember(in DeltaKey key, ReadOnlySpan<byte> bits, int bitCount) {
        if (!memo.TryGetValue(key, out var cached)) {
            cached = new();
            memo[key] = cached;
        }

        if (cached.Bits.Length < bits.Length) {
            cached.Bits = new byte[bits.Length];
        }

        bits.CopyTo(cached.Bits);
        cached.BitCount = bitCount;
    }

    void MarkWhatLeft(Connection connection, Tick tick) {
        finished.Clear();
        stamping.Clear();

        foreach (var held in connection.Holding) {
            if (!observedIds.Contains(held) && !connection.Removing.ContainsKey(held)) {
                stamping.Add(held);
            }
        }

        foreach (var id in stamping) {
            connection.Removing[id] = tick;
        }

        foreach (var (id, removedAt) in connection.Removing) {
            // A removal is only done with once the client has acknowledged a tick at or after the one
            // it was first sent in. Until then it goes in every snapshot: an entity that stayed
            // behind on one client because one packet was lost is a ghost only that player can see.
            if (connection.Baseline.HasAcknowledged && !removedAt.IsAfter(connection.Baseline.AcknowledgedTick)) {
                finished.Add(new(id));
            }
        }

        foreach (var id in finished) {
            connection.Removing.Remove(id.Value);
            connection.Holding.Remove(id.Value);
            connection.Baseline.Forget(id);
        }
    }

    static bool WriteRemovals(Connection connection, ref BitWriter writer, int budget) {
        var wrote = false;

        foreach (var (id, _) in connection.Removing) {
            var mark = writer.BitsWritten;
            writer.WriteBool(true);
            writer.WriteVariable(id);

            if (writer.Overflowed || writer.BytesWritten > budget) {
                writer.Rewind(mark);

                break;
            }

            wrote = true;
        }

        writer.WriteBool(false);

        return wrote;
    }

    bool WriteRecords(Connection connection, Tick tick, ref BitWriter writer, int budget) {
        var wrote = false;

        foreach (var replicator in byPriority) {
            foreach (var id in observedIds) {
                var key = new BaselineKey(new(id), replicator.TypeId);

                if (!current.TryGetValue(key, out var ring) || !ring.HasAny) {
                    continue;
                }

                var newest = ring.Newest;

                if (connection.Baseline.IsCurrent(key, newest.Hash)) {
                    continue;
                }

                // A difference is measured from the capture this connection acknowledged, which on a
                // connection with any latency is several captures back rather than the previous one.
                // Finding it is what the ring is for; if it has fallen out, the value goes whole.
                var difference = ReadOnlySpan<byte>.Empty;
                var differenceBits = 0;
                Capture? baseline = null;

                if (connection.Baseline.TryGetBaseline(key, out var baselineAt, out var baselineHash)
                    && ring.TryFind(baselineAt, out var found)
                    && found is not null
                    && found.Hash == baselineHash
                    && newest.At.Subtract(found.At) is > 0 and <= MaxBaselineAge) {
                    baseline = found;
                    difference = Difference(replicator, key, ring, found, out differenceBits);
                }

                var asDelta = !difference.IsEmpty;

                var mark = writer.BitsWritten;
                writer.WriteBool(true);
                writer.WriteVariable(id);
                writer.WriteVariable(wireIndex[replicator.TypeId]);

                // How old the value is, so the receiver files it under the tick it was captured
                // at rather than the tick it arrived in. One bit for the usual answer — it changed
                // this tick — because a variable-length integer costs a whole byte to say zero.
                var age = (uint)tick.Subtract(newest.At);
                writer.WriteBool(age != 0);

                if (age != 0) {
                    writer.WriteVariable(age);
                }

                writer.WriteBool(asDelta);

                if (asDelta) {
                    // Bounded by the ring, so it is a fixed field rather than a variable one.
                    writer.Write((uint)(newest.At.Subtract(baseline!.At) - 1), BaselineAgeBits);
                    writer.WriteBitsFrom(difference, differenceBits);
                } else {
                    writer.WriteBitsFrom(newest.Bits, newest.BitCount);
                }

                if (writer.Overflowed || writer.BytesWritten > budget) {
                    // Over budget: take the whole record back, so the snapshot is always a whole
                    // number of complete records. It was not acknowledged, so it goes out next tick.
                    writer.Rewind(mark);

                    return wrote;
                }

                if (asDelta) {
                    DeltaRecordCount++;
                } else {
                    WholeRecordCount++;
                }

                connection.Baseline.RecordSent(tick, key, newest.Hash, newest.At);
                connection.Holding.Add(id);
                wrote = true;
            }
        }

        return wrote;
    }

    Connection ConnectionFor(PlayerId player) {
        if (!connections.TryGetValue(player.Value, out var connection)) {
            connection = new();
            connections[player.Value] = connection;
        }

        return connection;
    }

    static uint Hash(ReadOnlySpan<byte> bytes) {
        var hash = 2166136261u;

        foreach (var value in bytes) {
            hash ^= value;
            hash *= 16777619u;
        }

        return hash;
    }

    /// <summary>One value, and the baseline a difference for it was measured from.</summary>
    readonly record struct DeltaKey(BaselineKey Value, uint BaselineTick);

    /// <summary>A difference already encoded this tick, for whoever asks for it next.</summary>
    sealed class Cached {
        public byte[] Bits { get; set; } = [];
        public int BitCount { get; set; }
    }

    sealed class Connection {
        public ConnectionBaseline Baseline { get; } = new();
        public HashSet<uint> Holding { get; } = [];
        public Dictionary<uint, Tick> Removing { get; } = [];
    }
}

/// <summary>How much a connection's snapshot may cost.</summary>
/// <remarks>
///     A budget rather than a limit: going over it does not fail, it sheds — the lowest-priority
///     records are left for the next tick. A game whose bandwidth is spiky is then a game that gets
///     slower updates rather than dropped packets, which is the failure mode players do not notice.
/// </remarks>
public sealed record BandwidthBudget {
    /// <summary>
    ///     The most one snapshot may be, in bytes. The default sits inside a typical path MTU, so a
    ///     snapshot is one datagram and a lost snapshot loses one tick rather than a fragment set.
    /// </summary>
    public int BytesPerSnapshot { get; init; } = 1200;
}
