// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;
using Vixen.Net.Tests.Replication;
using Xunit;

namespace Vixen.Net.Tests.Prediction;

/// <summary>Prediction: guessing the local player's future, and taking it back when told.</summary>
public sealed class ClientPredictionTests : IDisposable {
    static readonly NetworkId Id = new(1);

    readonly World world = new("prediction");
    readonly ReplicationRegistry registry = new();
    readonly InputLog<Move> log = new();
    readonly ClientPrediction<Move> prediction;
    readonly Entity player;

    long steps;

    public ClientPredictionTests() {
        registry.Register(new PositionReplicator());
        prediction = new(registry, log, Simulate);
        player = world.Create(Id, default(Predicted), default(ReplicatedPosition));
    }

    public void Dispose() => world.Dispose();

    /// <summary>A prediction the server agrees with costs nothing to reconcile.</summary>
    /// <remarks>
    ///     <para>
    ///         The property the design has to have to be worth having. Agreement is the common case —
    ///         on a connection that is behaving, it is <i>every</i> case — so if reconciling cost a
    ///         replay each time, prediction would be a constant multiple on the simulation budget
    ///         rather than an occasional one.
    ///     </para>
    ///     <para>
    ///         It also checks the part that is easy to get wrong: the world must end up holding the
    ///         predicted <i>present</i>, not the tick the server described. The snapshot moved the
    ///         player back five ticks, and something has to move it forward again.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APredictionTheServerAgreesWithCostsNothing() {
        for (var tick = 1u; tick <= 6; tick++) {
            prediction.Step(world, new(tick), new Move { X = 1 });
        }

        Assert.Equal(6f, X, 3);

        // The server's word for tick 3, which is what this client predicted for tick 3.
        ApplyServerState(3f);

        Assert.Equal(0, prediction.Reconcile(world, new(3)));
        Assert.Equal(1, prediction.ConfirmedCount);
        Assert.Equal(0, prediction.MispredictionCount);
        Assert.Equal(0, prediction.ResimulatedTickCount);

        // Back at the predicted present rather than left at the tick the snapshot described — and on
        // the wire's lattice, because a restore comes back through the codec that recorded it. The
        // error is one quantization step, it does not accumulate, and it is the same lattice the
        // server's own values live on.
        Assert.Equal(Wire(6f), X, 4);
        Assert.Equal(6, steps);
    }

    /// <summary>A prediction the server disagrees with is replayed from the server's state.</summary>
    /// <remarks>
    ///     Replaying from the server's tick is what makes the correction converge. Nudging the present
    ///     toward the server's value is the tempting alternative and it does not: the error being
    ///     corrected was produced by ticks that are not being redone, so it comes back.
    /// </remarks>
    [Fact]
    public void APredictionTheServerDisagreesWithIsReplayed() {
        for (var tick = 1u; tick <= 6; tick++) {
            prediction.Step(world, new(tick), new Move { X = 1 });
        }

        // The server says the player was somewhere else at tick 3 — a wall, a shove, a shot.
        var landed = ApplyServerState(30f);

        Assert.Equal(3, prediction.Reconcile(world, new(3)));
        Assert.Equal(1, prediction.MispredictionCount);
        Assert.Equal(3, prediction.ResimulatedTickCount);

        // Three ticks of the recorded input, on top of what the server said — where "what the server
        // said" is what arrived rather than what was sent, because the wire quantizes.
        Assert.Equal(landed + 3f, X, 4);
        Assert.Equal(9, steps);
    }

    /// <summary>The replay uses the inputs that were used the first time.</summary>
    /// <remarks>
    ///     Not the newest input repeated. A replay driven by the current input would reproduce the
    ///     last tick four times, which is a correction that is confidently wrong rather than obviously
    ///     wrong — and the difference only shows when the inputs actually varied.
    /// </remarks>
    [Fact]
    public void TheReplayUsesTheInputsThatWereUsedTheFirstTime() {
        prediction.Step(world, new(1), new Move { X = 1 });
        prediction.Step(world, new(2), new Move { X = 2 });
        prediction.Step(world, new(3), new Move { X = 4 });
        prediction.Step(world, new(4), new Move { X = 8 });

        Assert.Equal(15f, X, 3);

        var landed = ApplyServerState(100f);
        Assert.Equal(3, prediction.Reconcile(world, new(1)));

        // 100 + 2 + 4 + 8, not 100 + 8 + 8 + 8.
        Assert.Equal(landed + 14f, X, 4);
    }

    /// <summary>A difference the wire cannot express is not a misprediction.</summary>
    /// <remarks>
    ///     <para>
    ///         The reason the comparison is over encoded bytes rather than over component values. The
    ///         server's state arrives quantized, and a prediction that differs from it by less than
    ///         one quantization step encodes to the same bits — so there is nothing to correct and no
    ///         replay happens.
    ///     </para>
    ///     <para>
    ///         Comparing floats instead would roll back on very nearly every snapshot, and the cost
    ///         would look exactly like the feature working.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ADifferenceBelowTheWiresResolutionIsNotAMisprediction() {
        for (var tick = 1u; tick <= 4; tick++) {
            prediction.Step(world, new(tick), new Move { X = 1 });
        }

        // One 16-bit level over a 2000-unit range is about 3 cm. A tenth of that is a real difference
        // between two floats and no difference at all to anything that has been on the wire.
        ApplyServerState(2f + 0.003f);

        Assert.Equal(0, prediction.Reconcile(world, new(2)));
        Assert.Equal(1, prediction.ConfirmedCount);
        Assert.Equal(0, prediction.MispredictionCount);
    }

    /// <summary>A snapshot older than the history is taken as it is, and counted.</summary>
    /// <remarks>
    ///     A round trip longer than the history is a connection prediction cannot help with. Replaying
    ///     from it would mean simulating forward from a state whose successors have been forgotten,
    ///     which produces a confident wrong answer where taking the state produces an obvious jump.
    /// </remarks>
    [Fact]
    public void ASnapshotOlderThanTheHistoryIsTakenAsItIs() {
        var shallow = new ClientPrediction<Move>(registry, log, Simulate, depth: 4);

        for (var tick = 1u; tick <= 12; tick++) {
            shallow.Step(world, new(tick), new Move { X = 1 });
        }

        var landed = ApplyServerState(50f);

        Assert.Equal(0, shallow.Reconcile(world, new(2)));
        Assert.Equal(1, shallow.LostHistoryCount);
        Assert.Equal(0, shallow.MispredictionCount);
        Assert.Equal(landed, X, 4);
    }

    /// <summary>A snapshot ahead of anything guessed at simply stands.</summary>
    [Fact]
    public void ASnapshotAheadOfWhatWasGuessedStands() {
        prediction.Step(world, new(1), new Move { X = 1 });

        var landed = ApplyServerState(9f);

        Assert.Equal(0, prediction.Reconcile(world, new(5)));
        Assert.Equal(landed, X, 4);
        Assert.Equal(new Tick(5), prediction.Current);

        // And prediction carries on from there rather than from where it left off.
        prediction.Step(world, new(6), new Move { X = 1 });
        Assert.Equal(landed + 1f, X, 4);
    }

    /// <summary>An entity nobody predicts is left entirely alone.</summary>
    /// <remarks>
    ///     A rollback restores what it recorded, and it records what carries <see cref="Predicted" />.
    ///     Another player's avatar is interpolated rather than guessed at, so a replay that moved it
    ///     would be undoing the interpolation with a simulation of inputs that are not theirs.
    /// </remarks>
    [Fact]
    public void AnEntityNobodyPredictsIsLeftAlone() {
        var other = world.Create(new NetworkId(2), new ReplicatedPosition { X = 500f });

        for (var tick = 1u; tick <= 4; tick++) {
            prediction.Step(world, new(tick), new Move { X = 1 });
        }

        ApplyServerState(40f);
        world.Get<ReplicatedPosition>(other).X = 501f;

        Assert.True(prediction.Reconcile(world, new(2)) > 0);
        Assert.Equal(501f, world.Read<ReplicatedPosition>(other).X, 3);
    }

    float X => world.Read<ReplicatedPosition>(player).X;

    /// <summary>The value a float comes back as after a trip through the wire.</summary>
    static float Wire(float value) {
        var range = new QuantizeRange(-1000f, 1000f, 16);

        return range.Decode(range.Encode(value));
    }

    /// <summary>What a snapshot arriving does: the server's value, through the wire's quantization.</summary>
    /// <returns>What actually landed, which is not what was sent — 16 bits over 2000 units is 3 cm.</returns>
    float ApplyServerState(float x) {
        var buffer = new byte[64];
        var replicator = new PositionReplicator();

        using var authority = new World("prediction-server");
        var mirrored = authority.Create(Id, new ReplicatedPosition { X = x });

        var writer = new BitWriter(buffer);
        replicator.Write(authority, mirrored, ref writer);
        Assert.True(writer.TryFinish(out var bits));

        var reader = new BitReader(bits);
        Assert.True(replicator.Apply(world, player, ref reader));

        return X;
    }

    void Simulate(World simulated, Tick tick, in Move input) {
        simulated.Get<ReplicatedPosition>(player).X += input.X;
        steps++;
    }

    /// <summary>A movement input: how far along X, which is all these need.</summary>
    readonly record struct Move : IPredictedInput<Move> {
        public float X { get; init; }

        public void Write(ref BitWriter writer) => writer.WriteSingle(X);

        public static bool TryRead(ref BitReader reader, out Move value) {
            value = default;

            if (!reader.TryReadSingle(out var x)) {
                return false;
            }

            value = new() { X = x };

            return true;
        }
    }
}
