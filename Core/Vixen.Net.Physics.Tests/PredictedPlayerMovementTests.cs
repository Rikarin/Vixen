// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Net.Engine.Players;
using Vixen.Net.Motion;
using Vixen.Net.Prediction;
using Vixen.Net.Replication;
using Vixen.Physics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Vixen.Testing;
using Xunit;
using EcsWorld = Vixen.Ecs.World;

namespace Vixen.Net.Physics.Tests;

/// <summary>
///     Prediction over a real character: two machines, the same inputs, and the number that says
///     whether they agree.
/// </summary>
public sealed class PredictedPlayerMovementTests : IDisposable {
    const float Step = 1f / 60f;

    /// <summary>One machine: a world, a physics scene, and a character in it.</summary>
    sealed class Machine : IDisposable {
        public EcsWorld Entities { get; } = new("predicted");

        public PhysicsScene Scene { get; }

        public PredictedPlayerMovement Movement { get; }

        public Entity Pawn { get; }

        public Machine() {
            Scene = new(Entities);

            var ground = Entities.Create(LocalTransform.At(new(0f, -1f, 0f)));
            Entities.Add(ground, Collider.Of(Scene.Shapes.Box(new Vector3(50f, 1f, 50f))));

            Pawn = Entities.Create(LocalTransform.At(new(0f, 0.1f, 0f)));
            Entities.Add(
                Pawn,
                CharacterMovement.Default with {
                    Shape = Scene.Shapes.Capsule(0.6f, 0.3f),
                    CrouchShape = Scene.Shapes.Capsule(0.3f, 0.3f)
                }
            );

            Entities.Add(Pawn, default(MoveIntent));
            Entities.Add(Pawn, new NetworkId(1));
            Entities.Add(Pawn, default(NetworkTransform));
            Entities.Add(Pawn, default(Predicted));

            Movement = new(Scene, Step) { Pawn = Pawn };
        }

        public Vector3 Position => Entities.Read<LocalTransform>(Pawn).Position;

        public NetworkTransform Networked => Entities.Read<NetworkTransform>(Pawn);

        public void Dispose() {
            Scene.Dispose();
            Entities.Dispose();
        }
    }

    readonly Machine client = new();
    readonly Machine server = new();
    readonly ReplicationRegistry registry = new();
    readonly InputLog<PlayerMoveInput> log = new();
    readonly ClientPrediction<PlayerMoveInput> prediction;

    public PredictedPlayerMovementTests() {
        registry.Register(new NetworkTransformReplicator());
        prediction = new(registry, log, client.Movement.AsStep());
    }

    public void Dispose() {
        client.Dispose();
        server.Dispose();
    }

    /// <summary>What a player is holding on a given tick — enough variety to exercise every rule.</summary>
    static PlayerMoveInput InputFor(int tick) {
        var buttons = MoveButtons.None;

        if (tick % 41 == 0) {
            buttons |= MoveButtons.Jump;
        }

        if (tick % 29 < 10) {
            buttons |= MoveButtons.Sprint;
        }

        if (tick % 67 < 12) {
            buttons |= MoveButtons.Crouch;
        }

        return PlayerMoveInput.From(
            new MoveIntent {
                Move = new(MathF.Sin(tick * 0.11f), MathF.Cos(tick * 0.07f)),
                Yaw = MathUtil.WrapAngle(tick * 0.019f),
                Buttons = buttons
            }
        ).Roundtrip();
    }

    /// <summary>Copies the server's answer into the client, which is what a snapshot does.</summary>
    static void Deliver(Machine from, Machine to) {
        var networked = from.Networked;

        to.Entities.Set(to.Pawn, networked);

        // NetworkTransformApplySystem's half: a snapshot carries the networked pose and the transform
        // is written from it. The replay reads the transform, so both have to arrive.
        ref var transform = ref to.Entities.Get<LocalTransform>(to.Pawn);
        transform.Position = networked.Position;
        transform.Rotation = networked.Rotation;
    }

    /// <summary>
    ///     The prerequisite for all of it: the same inputs from the same start produce the same
    ///     answer, on two independent physics worlds, to the last bit.
    /// </summary>
    [Fact]
    public void TwoMachinesRunningTheSameInputsAgreeExactly() {
        for (var tick = 1; tick <= 120; tick++) {
            var input = InputFor(tick);

            client.Movement.Step(client.Entities, new((uint)tick), input);
            server.Movement.Step(server.Entities, new((uint)tick), input);
        }

        Assert.Equal(server.Position.X, client.Position.X);
        Assert.Equal(server.Position.Y, client.Position.Y);
        Assert.Equal(server.Position.Z, client.Position.Z);
    }

    /// <summary>
    ///     <b>The number that says whether prediction works.</b> A client predicting each tick and a
    ///     server simulating the same decoded inputs must agree on every one of them, so a lossless
    ///     connection costs no rollback at all. A step that read a clock, an unseeded random source or
    ///     a field of its own would mispredict here on a connection with no loss — and in a game it
    ///     would look like jitter rather than like a bug.
    /// </summary>
    [Fact]
    public void MispredictionCountIsZeroOverALosslessRun() {
        for (var tick = 1; tick <= 180; tick++) {
            var input = InputFor(tick);
            var stamp = new Tick((uint)tick);

            prediction.Step(client.Entities, stamp, input);
            server.Movement.Step(server.Entities, stamp, input);

            Deliver(server, client);

            Assert.Equal(0, prediction.Reconcile(client.Entities, stamp));
        }

        Assert.Equal(0, prediction.MispredictionCount);
        Assert.Equal(0, prediction.ResimulatedTickCount);
        Assert.Equal(180, prediction.ConfirmedCount);
        Assert.Equal(180, prediction.PredictedTickCount);
    }

    /// <summary>
    ///     And the history is not comparing a constant. A test that published nothing would confirm
    ///     every tick while the two machines drifted apart, so this pins that the recorded bytes move.
    /// </summary>
    [Fact]
    public void ThePredictedStatePublishesWhatTheCharacterDid() {
        prediction.Step(client.Entities, new(1), InputFor(1));
        var first = client.Networked.Position;

        for (var tick = 2; tick <= 40; tick++) {
            prediction.Step(client.Entities, new((uint)tick), InputFor(tick));
        }

        Assert.NotEqual(first, client.Networked.Position);
        Assert.Equal(client.Position.X, client.Networked.Position.X, 5);
        Assert.Equal(client.Position.Z, client.Networked.Position.Z, 5);
    }

    /// <summary>
    ///     A server that disagrees costs a replay, and the replay lands on the server's state rather
    ///     than nudging towards it. This is also what proves the character controller is rolled back:
    ///     its position lives in Jolt, and without <c>PhysicsScene</c> adopting a written transform the
    ///     replay would start from the guess it was correcting and never converge.
    /// </summary>
    [Fact]
    public void ADisagreementReplaysOntoTheServersState() {
        var walking = PlayerMoveInput.From(new MoveIntent { Move = new(0f, 1f) }).Roundtrip();
        var atSeventeen = default(NetworkTransform);

        for (var tick = 1; tick <= 20; tick++) {
            var stamp = new Tick((uint)tick);

            prediction.Step(client.Entities, stamp, walking);
            server.Movement.Step(server.Entities, stamp, walking);

            if (tick == 17) {
                atSeventeen = server.Networked;
            }
        }

        var predicted = client.Position;

        // The server's word for tick 17, plus two metres sideways — a knockback the client never saw,
        // arriving three ticks late. Taken from the tick it describes rather than nudged onto the
        // present, because a snapshot always describes a tick that has already happened.
        var corrected = atSeventeen with { Position = atSeventeen.Position + new Vector3(2f, 0f, 0f) };

        client.Entities.Set(client.Pawn, corrected);

        ref var transform = ref client.Entities.Get<LocalTransform>(client.Pawn);
        transform.Position = corrected.Position;
        transform.Rotation = corrected.Rotation;

        var replayed = prediction.Reconcile(client.Entities, new(17));

        Assert.Equal(3, replayed);
        Assert.Equal(1, prediction.MispredictionCount);
        Assert.NotEmpty(prediction.Corrections);

        // Two metres across, and three ticks of walking carried on from there rather than snapped
        // back to where tick 17 was.
        Assert.Equal(predicted.X + 2f, client.Position.X, 2);
        Assert.Equal(predicted.Z, client.Position.Z, 2);

        // ⚠ And the controller went with it. Reading only the entity's transform would pass even if
        // the character's own position had stayed behind in Jolt, which is exactly the bug
        // PhysicsScene's adopt exists to close — and the replay would then diverge further every time.
        Assert.True(client.Scene.TryGetCharacter(client.Pawn, out var controller));
        Assert.Equal(
            client.Position.X + CharacterMovement.Default.ShapeOffset.X,
            controller!.Position.X,
            3
        );
    }

    /// <summary>
    ///     A replayed tick uses the input that was used the first time. Without the log it would
    ///     replay with nothing held, and a player who was mid-jump when a correction arrived would be
    ///     dropped by it.
    /// </summary>
    [Fact]
    public void AReplayUsesTheInputsThatWereUsedTheFirstTime() {
        var held = PlayerMoveInput.From(new MoveIntent { Move = new(0f, 1f), Buttons = MoveButtons.Sprint })
            .Roundtrip();

        for (var tick = 1; tick <= 12; tick++) {
            prediction.Step(client.Entities, new((uint)tick), held);
        }

        var sprinted = client.Position.Z;

        client.Entities.Get<LocalTransform>(client.Pawn).Position += new Vector3(0.5f, 0f, 0f);
        client.Movement.Transforms.Publish(client.Entities);

        Assert.Equal(4, prediction.Reconcile(client.Entities, new(8)));

        // Four ticks replayed at sprint speed, not at walk speed and not at a standstill.
        Assert.True(client.Position.Z < sprinted + 0.01f, $"replayed to {client.Position.Z} from {sprinted}");
        Assert.Equal(MoveButtons.Sprint, client.Entities.Read<MoveIntent>(client.Pawn).Buttons);
    }

    /// <summary>
    ///     The pawn is followed through the possession edge rather than set twice. Two properties that
    ///     had to be kept in agreement is the kind of wire a game forgets, and the failure is a client
    ///     that silently stops predicting after its first respawn.
    /// </summary>
    [Fact]
    public void ThePawnFollowsWhateverTheControllerPossesses() {
        var controller = Player.Create(client.Entities);

        client.Movement.Pawn = Entity.Null;
        client.Movement.Controller = controller;

        Assert.True(client.Movement.PawnIn(client.Entities).IsNull);

        Player.Possess(client.Entities, controller, client.Pawn);

        Assert.Equal(client.Pawn, client.Movement.PawnIn(client.Entities));

        // A death: the controller survives, and the step simply has nothing to write until the next
        // body arrives.
        Player.Unpossess(client.Entities, controller);

        Assert.True(client.Movement.PawnIn(client.Entities).IsNull);
    }

    [Fact]
    public void AnExplicitPawnStillWorksForAGameWithNoController() {
        client.Movement.Controller = Entity.Null;
        client.Movement.Pawn = client.Pawn;

        Assert.Equal(client.Pawn, client.Movement.PawnIn(client.Entities));
    }

    /// <summary>
    ///     A reconciliation replays this once per tick of round trip, so an allocation here is an
    ///     allocation multiplied by the connection's latency.
    /// </summary>
    /// <remarks>
    ///     The whole tick, native world step included — because that is what a replay actually costs
    ///     and a measurement that excluded it would not answer the question.
    /// </remarks>
    [Fact]
    public void APredictedTickAllocatesNothing() {
        var walking = PlayerMoveInput.From(new MoveIntent { Move = new(0f, 1f) }).Roundtrip();
        var tick = 0u;

        for (var warm = 0; warm < 10; warm++) {
            client.Movement.Step(client.Entities, new(++tick), walking);
        }

        Assert.Equal(
            0,
            Measured.Bytes(
                () => client.Movement.Step(client.Entities, new(++tick), walking),
                warmUp: 16,
                passes: 300
            )
        );
    }

    [Fact]
    public void APawnThatDoesNotExistIsNotAnError() {
        client.Movement.Pawn = Entity.Null;

        client.Movement.Step(client.Entities, new(1), InputFor(1));

        Assert.Equal(1, client.Movement.StepCount);
    }

    [Fact]
    public void ATickOfNoLengthIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PredictedPlayerMovement(client.Scene, 0f));
    }
}
