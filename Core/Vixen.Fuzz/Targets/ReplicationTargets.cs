// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Diagnostics;
using Vixen.Net.Messaging;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Fuzz.Targets;

/// <summary>Builds real snapshots, so the fuzzer has something shaped like one to break.</summary>
/// <remarks>
///     A snapshot is four bytes of tick, a removal list, then a record per value — and a record
///     names a type by its position in the manifest, says whether it is a difference, and if it is,
///     how far back the capture it was measured from lies. Random bytes reach none of that: the tick
///     alone is a 32-bit gate. These are produced by <see cref="ReplicationServer" /> itself so that
///     a change to the record format changes the seeds with it.
/// </remarks>
internal static class SnapshotSeeds {
    public static List<byte[]> Build() {
        using var world = new World("fuzz-seeds");
        var registry = new ReplicationRegistry();
        registry.Register(new NetworkTransformReplicator());

        var server = new ReplicationServer(registry);
        var ids = new NetworkIdAllocator();
        var player = new PlayerId(1);
        var buffer = new byte[2048];
        var seeds = new List<byte[]>();
        var entities = new List<Entity>();

        for (var i = 0; i < 6; i++) {
            entities.Add(
                world.Create(
                    ids.Next(),
                    new NetworkTransform { Position = new(i, 0f, -i), Rotation = Quaternion.Identity }
                )
            );
        }

        for (var tick = 1u; tick <= 8; tick++) {
            world.AdvanceVersion();

            foreach (var entity in entities) {
                ref var transform = ref world.Get<NetworkTransform>(entity);
                transform.Position += new Vector3(0.05f, 0f, 0.01f);
            }

            server.Capture(world, new(tick));

            if (server.TryWriteSnapshot(world, player, new(tick), buffer, out var snapshot)) {
                seeds.Add(snapshot.ToArray());
            }

            // Acknowledged a tick late, so the later snapshots are differences rather than whole
            // records — which is the half of the format a client-side bug would live in.
            if (tick > 1) {
                server.Acknowledge(player, new(tick - 1));
            }
        }

        // A removal, which is its own list at the front of the record stream.
        world.AdvanceVersion();
        server.Despawn(new(1));
        server.Capture(world, new(9));

        if (server.TryWriteSnapshot(world, player, new(9), buffer, out var removal)) {
            seeds.Add(removal.ToArray());
        }

        return seeds;
    }
}

/// <summary>The client's snapshot applier.</summary>
/// <remarks>
///     <para>
///         <b>What a snapshot can talk a client into doing is the question.</b> It names entities to
///         create, entities to destroy, component types by index, and — for a difference — a capture
///         to measure from that the client may or may not still hold. Each of those is a number from
///         the wire that decides what happens to a live world.
///     </para>
///     <para>
///         The record that arrives whole and the record that arrives as a difference are different
///         code paths and the second one is the one with arithmetic in it: an age is subtracted from
///         a tick, a capture is looked up by the result, and its bit count is compared against what
///         the layout says it should be. Getting that wrong is a silent corruption rather than a
///         crash, which is why the seeds above deliberately produce differences.
///     </para>
/// </remarks>
public sealed class SnapshotTarget : IFuzzTarget, IDisposable {
    readonly ReplicationRegistry registry = new();
    readonly ReplicationClient client;

    World world = new("fuzz-snapshot");
    long rejectedBefore;
    long staleBefore;
    int entitiesBefore;

    /// <summary>Creates the target, with a registry holding the one hand-written replicator.</summary>
    public SnapshotTarget() {
        registry.Register(new NetworkTransformReplicator());
        client = new(registry);
    }

    /// <inheritdoc />
    public string Name => "snapshot";

    /// <inheritdoc />
    public string What => "ReplicationClient.TryApply: what a snapshot can do to a client's world";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <para>
    ///         The loosest allowance of the set, and the reason is worth stating rather than
    ///         hiding: a well-formed record legitimately makes the client create an entity and start
    ///         a capture ring for it, and both allocate. A record is a few bytes, so a full snapshot
    ///         of new objects is genuinely allocation per byte — that is what receiving a world is.
    ///     </para>
    ///     <para>
    ///         What the number still catches is the case that matters: a <i>short</i> input that
    ///         allocates a lot. Amplification is the property, and a client that can be made to
    ///         allocate a megabyte from forty bytes would fail this by three orders of magnitude.
    ///     </para>
    /// </remarks>
    public long AllowanceFor(int inputLength) => 8192 + (256L * inputLength);

    /// <summary>How many networked entities this client is holding.</summary>
    public long Held => client.EntityCount;

    /// <summary>
    ///     How many it may hold. Above where <see cref="Maintain" /> resets, so what this asserts is
    ///     that the reset works — a client legitimately holds as much of a world as it is sent, and
    ///     bounding that would be bounding the feature rather than a defect.
    /// </summary>
    public long HeldCap => 8192;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        foreach (var snapshot in SnapshotSeeds.Build()) {
            corpus.Add(snapshot);
        }
    }

    /// <inheritdoc />
    public void Maintain() {
        if (client.EntityCount < 4096) {
            return;
        }

        // A fresh world rather than a cleared one: what accumulates is entities, and the point of
        // the reset is that a run's hundredth million case is fuzzing the same shape of client the
        // first one was. Out here, so the cost is the harness's rather than some input's.
        client.Clear();
        world.Dispose();
        world = new("fuzz-snapshot");
        entitiesBefore = 0;
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var applied = client.TryApply(world, input);

        // Deltas, and deliberately not AppliedTick: it is four bytes taken straight off the wire,
        // so a signature containing it makes every accepted snapshot novel and the corpus keeps
        // every one of them. That is what this harness was doing before it was measured.
        var signature = (applied ? 1_000_003L : 0)
            + ((client.RejectedSnapshotCount - rejectedBefore) * 7919L)
            + ((client.StaleSnapshotCount - staleBefore) * 131L)
            + ((client.EntityCount - entitiesBefore) * 31L);

        rejectedBefore = client.RejectedSnapshotCount;
        staleBefore = client.StaleSnapshotCount;
        entitiesBefore = client.EntityCount;

        return signature;
    }

    /// <summary>Lets go of the world.</summary>
    public void Dispose() => world.Dispose();
}

/// <summary>The snapshot inspector, which walks a packet it cannot decode.</summary>
/// <remarks>
///     Deliberately a separate target from <see cref="SnapshotTarget" /> even though it reads the
///     same bytes. It is the tool a developer points at traffic they are suspicious of — a recorded
///     capture, a peer's stream, a snapshot the client refused — which is to say it is aimed at
///     exactly the inputs most likely to be malformed, by somebody who is already having a bad day.
///     An inspector that throws on the packet you are inspecting is worse than no inspector.
/// </remarks>
public sealed class SnapshotInspectorTarget : IFuzzTarget {
    readonly ReplicationRegistry registry = new();

    /// <summary>Creates the target.</summary>
    public SnapshotInspectorTarget() => registry.Register(new NetworkTransformReplicator());

    /// <inheritdoc />
    public string Name => "inspect";

    /// <inheritdoc />
    public string What => "SnapshotInspector.Inspect: taking a packet apart without applying it";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     It builds two lists and a record per entry, which is the job. A record is around fifty
    ///     bytes and the smallest one a packet can claim is a couple of bytes, so the ratio is the
    ///     bound and the constant covers the lists' first growth.
    /// </remarks>
    public long AllowanceFor(int inputLength) => 4096 + (128L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        foreach (var snapshot in SnapshotSeeds.Build()) {
            corpus.Add(snapshot);
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var contents = SnapshotInspector.Inspect(registry, input);

        // No tick here either. What the inspector found is the shape of the packet — how many
        // records, how many removals, and whether it read to the end — and the tick is a number the
        // packet chose.
        return (contents.Complete ? 1_000_003L : 0)
            + (contents.Records.Count * 7919L)
            + (contents.Removals.Count * 131L);
    }
}

/// <summary>The delta codec's decoder, on its own.</summary>
/// <remarks>
///     <para>
///         Underneath the snapshot applier and worth reaching directly, because
///         <see cref="ReplicationClient" /> only lets a difference through once it has found a
///         baseline of exactly the right length — so fuzzing through it tests the lookup far more
///         than the arithmetic. Here the baseline is always there and always right, and every byte
///         of the input is the difference.
///     </para>
///     <para>
///         What the codec has to survive is a selector naming a width, an offset that overflows the
///         lane it belongs to, and a difference that claims more bits than were sent. It writes into
///         a caller's buffer, so the failure mode if it got that wrong is not an exception — it is
///         writing past a lane and corrupting the value next to it, which nothing would report. The
///         signature includes the rebuilt bit count for that reason.
///     </para>
/// </remarks>
public sealed class DeltaCodecTarget : IFuzzTarget {
    static readonly NetworkTransformReplicator Replicator = new();

    readonly byte[] baseline = new byte[64];
    readonly byte[] rebuilt = new byte[256];
    readonly int baselineBits;

    /// <summary>Creates the target and encodes the capture every difference is measured from.</summary>
    public DeltaCodecTarget() {
        var writer = new BitWriter(baseline);

        foreach (var lane in Replicator.Lanes) {
            // A middling value in each lane, so an offset can go either way without saturating on
            // the first bit — a baseline of all zeroes would make half the codec unreachable.
            writer.Write(lane.Bits >= 32 ? 0x5555_5555u : (1u << (lane.Bits - 1)) - 1, lane.Bits);
        }

        baselineBits = writer.BitsWritten;
    }

    /// <inheritdoc />
    public string Name => "delta";

    /// <inheritdoc />
    public string What => "DeltaCodec.TryDecode: rebuilding a value from a difference";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>Nothing on this path allocates; the buffers are the target's and are reused.</remarks>
    public long AllowanceFor(int inputLength) => 1024;

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        var lanes = Replicator.Lanes;
        var current = new byte[64];
        var difference = new byte[64];

        // Encode a real difference for a handful of value shapes, so the mutator starts from
        // selectors and offsets that mean something rather than from noise.
        for (var shift = 0; shift < 4; shift++) {
            var writer = new BitWriter(current);

            foreach (var lane in lanes) {
                var middle = lane.Bits >= 32 ? 0x5555_5555u : (1u << (lane.Bits - 1)) - 1;
                writer.Write(middle + (uint)(1 << shift), lane.Bits);
            }

            if (!writer.TryFinish(out var currentBits)) {
                continue;
            }

            var previous = new BitReader(baseline);
            var value = new BitReader(currentBits);
            var delta = new BitWriter(difference);

            if (DeltaCodec.TryEncode(lanes, ref previous, ref value, ref delta) && delta.TryFinish(out var encoded)) {
                corpus.Add(encoded.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var lanes = Replicator.Lanes;
        var previous = new BitReader(baseline.AsSpan(0, (baselineBits + 7) / 8));
        var delta = new BitReader(input);
        var writer = new BitWriter(rebuilt);

        var decoded = DeltaCodec.TryDecode(lanes, ref previous, ref delta, ref writer);

        return (decoded ? 1_000_003L : 0)
            + (writer.BitsWritten * 31L)
            + (delta.BitsRead * 7L)
            + (writer.Overflowed ? 131L : 0);
    }
}
