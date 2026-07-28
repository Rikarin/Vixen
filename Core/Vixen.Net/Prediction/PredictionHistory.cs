// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Prediction;

/// <summary>Marks an entity whose future this client guesses at rather than waits for.</summary>
/// <remarks>
///     <para>
///         <b>Local, and never replicated.</b> Which entities a peer predicts is a property of that
///         peer — a client predicts itself, and every other client is somebody it interpolates. A
///         replicated version of this would be a server telling clients what to guess, which is not a
///         thing a server knows.
///     </para>
///     <para>
///         <b>Predicting more is not free and not obviously better.</b> Every predicted entity is
///         state to record per tick and to compare per snapshot, and — more to the point — every
///         predicted entity is a thing that can be predicted <i>wrong</i> in front of the player.
///         Doc 16's warning is worth repeating here: a game that predicts movement but not the
///         interactions movement causes feels less consistent than one that predicts nothing.
///     </para>
/// </remarks>
public struct Predicted : ITagComponent;

/// <summary>What this client thought the world looked like on each of the last few ticks.</summary>
/// <remarks>
///     <para>
///         <b>Recorded through the replication codecs, which is the whole trick.</b> A predicted
///         entity's state is written with the same <c>IComponentReplicator</c> the server would have
///         used, so a frame of history and a snapshot are the same bytes describing the same thing —
///         and comparing them is a span comparison rather than a per-component equality nobody wrote.
///     </para>
///     <para>
///         <b>It also settles what "predicted state" means: exactly what is replicated.</b> That is
///         not a limitation to be lifted later; it is the definition. A field the server never sends
///         is a field no snapshot can contradict, so there is nothing to reconcile it against and
///         nothing that could tell you the prediction was wrong.
///     </para>
///     <para>
///         <b>Comparing in the encoded domain gets the tolerance right for free.</b> A prediction that
///         differs from the server in the last bit of a float is a difference below what the wire can
///         express — the server's own value arrived quantized — so the two encode identically and no
///         rollback happens. A naive float comparison would roll back on almost every snapshot and
///         the cost would look like the feature working.
///     </para>
/// </remarks>
public sealed class PredictionHistory {
    static readonly QueryDescription PredictedQuery = new QueryDescription()
        .RequireAll([ComponentType<NetworkId>.Id, ComponentType<Predicted>.Id]);

    readonly ReplicationRegistry registry;
    readonly Frame[] frames;
    readonly Frame scratch = new();
    readonly Dictionary<uint, Entity> located = [];

    /// <summary>How many ticks of history are kept.</summary>
    /// <remarks>
    ///     A snapshot describing a tick older than this cannot be reconciled against, because there is
    ///     nothing to compare it with — see <c>ClientPrediction.LostHistoryCount</c>. Thirty-two ticks
    ///     is half a second at sixty, which is longer than the round trip of any connection a
    ///     predicting game is playable over.
    /// </remarks>
    public int Depth => frames.Length;

    /// <summary>How many frames are filled.</summary>
    public int Count {
        get {
            var total = 0;

            foreach (var frame in frames) {
                if (frame.Filled) {
                    total++;
                }
            }

            return total;
        }
    }

    /// <summary>Creates a history.</summary>
    /// <param name="registry">The component types that may be predicted, which are the replicated ones.</param>
    /// <param name="depth">How many ticks to keep.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth" /> is less than two.</exception>
    public PredictionHistory(ReplicationRegistry registry, int depth = 32) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 2);

        this.registry = registry;
        frames = new Frame[depth];

        for (var index = 0; index < depth; index++) {
            frames[index] = new();
        }
    }

    /// <summary>Records what the world looks like now, as this tick's prediction.</summary>
    /// <param name="world">The client's world.</param>
    /// <param name="tick">The tick just simulated.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Record(World world, Tick tick) {
        ArgumentNullException.ThrowIfNull(world);
        Encode(world, tick, Slot(tick));
    }

    /// <summary>Whether a tick's prediction is still held.</summary>
    /// <param name="tick">The tick.</param>
    /// <returns>Whether it is.</returns>
    public bool Has(Tick tick) {
        var frame = Slot(tick);

        return frame.Filled && frame.At == tick;
    }

    /// <summary>Whether the world now matches what was predicted for a tick.</summary>
    /// <param name="world">The client's world, holding whatever the server just said.</param>
    /// <param name="tick">The tick the server described.</param>
    /// <returns>
    ///     Whether they agree. False when the tick is not held, because a prediction that cannot be
    ///     checked has to be treated as wrong.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public bool Matches(World world, Tick tick) {
        ArgumentNullException.ThrowIfNull(world);

        if (!Has(tick)) {
            return false;
        }

        Encode(world, tick, scratch);

        return scratch.SameAs(Slot(tick));
    }

    /// <summary>Puts a recorded prediction back into the world.</summary>
    /// <param name="world">The client's world.</param>
    /// <param name="tick">The tick to restore.</param>
    /// <returns>Whether that tick was still held.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>
    ///     <b>What comes back is the encoded form, so a restore snaps the world onto the wire's
    ///     lattice.</b> A predicted position of exactly 6 comes back as whatever 6 quantizes to and
    ///     decodes from — a few millimetres out, for a range of two kilometres in sixteen bits. That
    ///     is a consequence worth stating rather than discovering, and it is benign in both the ways
    ///     that matter: the error is bounded by one quantization step and does not accumulate, because
    ///     encoding an already-decoded value gives back the same level; and it is the <i>same</i>
    ///     lattice the server's own snapshots arrive on, so being on it makes the next comparison more
    ///     likely to agree rather than less.
    /// </remarks>
    public bool TryRestore(World world, Tick tick) {
        ArgumentNullException.ThrowIfNull(world);

        if (!Has(tick)) {
            return false;
        }

        var frame = Slot(tick);
        Locate(world);

        foreach (var entry in frame.Entries) {
            if (!located.TryGetValue(entry.Id, out var entity)
                || !registry.TryGetByIndex((uint)entry.Replicator, out var replicator)
                || replicator is null) {
                // An entity that has gone since it was recorded. Nothing to put the state back into,
                // and a despawn is the server's word rather than something to argue with.
                continue;
            }

            var reader = new BitReader(frame.Bytes.AsSpan(entry.Offset, entry.Length));
            replicator.Apply(world, entity, ref reader);
        }

        return true;
    }

    /// <summary>Forgets everything, for a client that has been put somewhere.</summary>
    public void Clear() {
        foreach (var frame in frames) {
            frame.Filled = false;
        }
    }

    Frame Slot(Tick tick) => frames[tick.Value % (uint)frames.Length];

    void Encode(World world, Tick tick, Frame frame) {
        frame.Begin(tick);

        // The registry's order, entity by entity within it, so two encodings of the same state list
        // the same things in the same order and comparing them is a walk rather than a lookup per
        // entry. The chunk order a query returns is not stable across an archetype change; the
        // registry's is fixed at construction.
        for (var index = 0; index < registry.Replicators.Count; index++) {
            var replicator = registry.Replicators[index];

            foreach (var chunk in world.Chunks(PredictedQuery)) {
                var ids = chunk.ReadValues<NetworkId>();
                var entities = chunk.Entities;

                for (var row = 0; row < chunk.Count; row++) {
                    if (!ids[row].IsValid || !replicator.Has(world, entities[row])) {
                        continue;
                    }

                    frame.Write(world, entities[row], ids[row], index, replicator);
                }
            }
        }

        frame.Sort();
    }

    void Locate(World world) {
        located.Clear();

        foreach (var chunk in world.Chunks(PredictedQuery)) {
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var row = 0; row < chunk.Count; row++) {
                located[ids[row].Value] = entities[row];
            }
        }
    }

    /// <summary>One tick's worth of encoded predicted state.</summary>
    /// <remarks>
    ///     Every record starts on a byte boundary. It wastes up to seven bits each, and it makes a
    ///     record a span — which is what lets a comparison be <c>SequenceEqual</c> and a restore be a
    ///     <c>BitReader</c> over a slice, with no bit-offset arithmetic anywhere. For a handful of
    ///     predicted entities that is the right side of the trade by a distance.
    /// </remarks>
    sealed class Frame {
        readonly List<Entry> entries = [];

        byte[] bytes = new byte[1024];
        int used;

        public Tick At { get; private set; }
        public bool Filled { get; set; }
        public IReadOnlyList<Entry> Entries => entries;
        public byte[] Bytes => bytes;

        public void Begin(Tick tick) {
            At = tick;
            Filled = true;
            used = 0;
            entries.Clear();
        }

        public void Write(
            World world,
            Entity entity,
            NetworkId id,
            int replicator,
            IComponentReplicator codec
        ) {
            while (true) {
                var writer = new BitWriter(bytes.AsSpan(used));
                codec.Write(world, entity, ref writer);

                if (writer.TryFinish(out var record)) {
                    entries.Add(new(id.Value, replicator, used, record.Length));
                    used += record.Length;

                    return;
                }

                // The record did not fit in what is left. Growing and starting the record again is
                // simpler than reserving per record, and it happens once per session rather than per
                // tick — the buffer is kept between frames precisely so that it stops happening.
                Array.Resize(ref bytes, Math.Max(bytes.Length * 2, used + 512));
            }
        }

        public void Sort() =>
            entries.Sort(
                static (left, right) => left.Id != right.Id
                    ? left.Id.CompareTo(right.Id)
                    : left.Replicator.CompareTo(right.Replicator)
            );

        public bool SameAs(Frame other) {
            if (entries.Count != other.entries.Count) {
                return false;
            }

            for (var index = 0; index < entries.Count; index++) {
                var mine = entries[index];
                var theirs = other.entries[index];

                if (mine.Id != theirs.Id || mine.Replicator != theirs.Replicator || mine.Length != theirs.Length) {
                    return false;
                }

                if (!bytes.AsSpan(mine.Offset, mine.Length)
                    .SequenceEqual(other.bytes.AsSpan(theirs.Offset, theirs.Length))) {
                    return false;
                }
            }

            return true;
        }
    }

    readonly record struct Entry(uint Id, int Replicator, int Offset, int Length);
}
