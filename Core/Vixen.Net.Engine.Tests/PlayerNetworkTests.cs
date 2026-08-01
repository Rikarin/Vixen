// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Engine.Players;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Tests;

public sealed class PlayerMoveInputTests {
    [Fact]
    public void AnIntentSurvivesTheRoundTripToWithinTheStatedError() {
        var intent = new MoveIntent {
            Move = new(-0.5f, 0.75f),
            Yaw = 1.2f,
            Pitch = -0.4f,
            Buttons = MoveButtons.Jump | MoveButtons.Sprint
        };

        var decoded = PlayerMoveInput.Round(intent);

        Assert.Equal(intent.Move.X, decoded.Move.X, PlayerMoveInput.Axis.MaxError);
        Assert.Equal(intent.Move.Y, decoded.Move.Y, PlayerMoveInput.Axis.MaxError);
        Assert.Equal(intent.Yaw, decoded.Yaw, PlayerMoveInput.Yaw.MaxError);
        Assert.Equal(intent.Pitch, decoded.Pitch, PlayerMoveInput.Pitch.MaxError);

        // Buttons are bits and there is nothing to round.
        Assert.Equal(intent.Buttons, decoded.Buttons);
    }

    /// <summary>
    ///     Rounding is idempotent, which is the property prediction needs: a client that rounds its
    ///     intent, predicts with it and sends it must arrive at the same numbers the server decodes.
    /// </summary>
    [Fact]
    public void RoundingTwiceChangesNothing() {
        var intent = new MoveIntent { Move = new(0.31f, -0.62f), Yaw = -2.9f, Pitch = 0.83f };

        var once = PlayerMoveInput.Round(intent);
        var twice = PlayerMoveInput.Round(once);

        Assert.Equal(once.Move.X, twice.Move.X, 6);
        Assert.Equal(once.Move.Y, twice.Move.Y, 6);
        Assert.Equal(once.Yaw, twice.Yaw, 6);
        Assert.Equal(once.Pitch, twice.Pitch, 6);
    }

    /// <summary>The one payload sent more often than a snapshot, so its width is the point.</summary>
    [Fact]
    public void AnInputIsSevenBytes() {
        var buffer = new byte[32];
        var writer = new BitWriter(buffer);

        PlayerMoveInput.From(new() { Move = new(1f, 1f), Yaw = 3f, Buttons = MoveButtons.Jump })
            .Write(ref writer);

        Assert.True(writer.TryFinish(out var payload));

        // Fifty-two bits: two axes at eight, two angles at ten, sixteen of buttons.
        Assert.Equal(7, payload.Length);
    }

    [Fact]
    public void EveryButtonBitSurvivesIncludingTheGamesOwn() {
        var custom = (MoveButtons)(1 << 12);
        var intent = new MoveIntent { Buttons = MoveButtons.Reload | custom };

        var decoded = PlayerMoveInput.Round(intent);

        Assert.True(decoded.IsHeld(MoveButtons.Reload));
        Assert.True(decoded.IsHeld(custom));
    }

    [Fact]
    public void AtruncatedPayloadIsRefusedRatherThanDecodedAsRubbish() {
        var buffer = new byte[32];
        var writer = new BitWriter(buffer);

        PlayerMoveInput.From(new() { Yaw = 1f }).Write(ref writer);
        Assert.True(writer.TryFinish(out var payload));

        var reader = new BitReader(payload[..3]);

        Assert.False(PlayerMoveInput.TryRead(ref reader, out _));
    }
}

public sealed class PlayerSpawnerTests : IDisposable {
    const string Address = "gameplay/prefabs/avatar";

    static readonly PlayerId One = new(1);
    static readonly PlayerId Two = new(2);

    readonly World server = new("player-server");
    readonly World authoring = new("player-authoring");
    readonly NetworkIdAllocator ids = new();
    readonly NetworkPrefabRegistry prefabs = new();
    readonly NetworkSpawner spawner;
    readonly PlayerSpawner players;
    readonly Prefab prefab;

    public PlayerSpawnerTests() {
        prefab = BuildPawn();
        prefabs.Register(Address, prefab);
        spawner = new(prefabs, ids);
        players = new(spawner);
    }

    public void Dispose() {
        prefab.Dispose();
        authoring.Dispose();
        server.Dispose();
    }

    [Fact]
    public void JoiningGivesAPlayerAControllerAndAPossessedPawn() {
        var controller = players.Join(server, One, Address, LocalTransform.At(new(3f, 0f, 0f)));
        var pawn = Player.PawnOf(server, controller);

        Assert.Equal(controller, players.ControllerOf(One));
        Assert.False(pawn.IsNull);
        Assert.Equal(controller, Player.ControllerOf(server, pawn));
        Assert.True(server.Has<PlayerPawn>(pawn));
        Assert.Equal(1, players.Count);
    }

    /// <summary>
    ///     The pawn is spawned <i>owned</i>, and that one argument is what makes the client's copy an
    ///     autonomous proxy: it is what PredictedOwnershipSystem reads and what an owner-only RPC
    ///     checks.
    /// </summary>
    [Fact]
    public void ThePawnIsOwnedByThePlayerItWasSpawnedFor() {
        var controller = players.Join(server, One, Address);
        var pawn = Player.PawnOf(server, controller);

        Assert.Equal(One.Value, server.Read<NetworkSpawn>(pawn).Owner);
        Assert.Equal(One.Value, server.Read<PlayerController>(controller).Owner);
    }

    [Fact]
    public void TwoPlayersGetTwoControllersAndTwoPawns() {
        var first = players.Join(server, One, Address);
        var second = players.Join(server, Two, Address);

        Assert.NotEqual(first, second);
        Assert.NotEqual(Player.PawnOf(server, first), Player.PawnOf(server, second));
        Assert.Equal(2, players.Count);
    }

    [Fact]
    public void JoiningTwiceIsAnError() {
        players.Join(server, One, Address);

        Assert.Throws<ArgumentException>(() => players.Join(server, One, Address));
    }

    [Fact]
    public void APlayerWithNoRealConnectionCannotJoin() {
        Assert.Throws<ArgumentException>(() => players.Join(server, PlayerId.None, Address));
    }

    /// <summary>
    ///     The claim the whole subsystem is arranged around, tested at the level a game sees it:
    ///     respawning keeps the controller, so it keeps the aim, the slot and the camera.
    /// </summary>
    [Fact]
    public void RespawningKeepsTheControllerAndItsAim() {
        var controller = players.Join(server, One, Address);
        var first = Player.PawnOf(server, controller);

        server.Get<ControlRotation>(controller).Turn(1.1f, 0.2f);

        var second = players.Respawn(server, One, Address, LocalTransform.At(new(0f, 0f, -20f)));

        Assert.Equal(controller, players.ControllerOf(One));
        Assert.NotEqual(first, second);
        Assert.False(server.IsAlive(first));
        Assert.Equal(second, Player.PawnOf(server, controller));
        Assert.Equal(1.1f, server.Read<ControlRotation>(controller).Yaw, 5);
        Assert.Equal(0.2f, server.Read<ControlRotation>(controller).Pitch, 5);
    }

    [Fact]
    public void RespawningAPlayerThatNeverJoinedIsAnError() {
        Assert.Throws<ArgumentException>(() => players.Respawn(server, One, Address));
    }

    [Fact]
    public void LeavingTakesTheBodyAndTheController() {
        var controller = players.Join(server, One, Address);
        var pawn = Player.PawnOf(server, controller);

        Assert.True(players.Leave(server, One));

        Assert.False(server.IsAlive(controller));
        Assert.False(server.IsAlive(pawn));
        Assert.Equal(0, players.Count);
        Assert.True(players.ControllerOf(One).IsNull);
    }

    [Fact]
    public void LeavingTwiceIsNotAnError() {
        players.Join(server, One, Address);

        Assert.True(players.Leave(server, One));
        Assert.False(players.Leave(server, One));
    }

    /// <summary>A player whose pawn was destroyed can still leave, and still be respawned.</summary>
    [Fact]
    public void APlayerSurvivesLosingItsPawnOutsideTheSpawner() {
        var controller = players.Join(server, One, Address);
        var pawn = Player.PawnOf(server, controller);

        server.Destroy(pawn);

        var respawned = players.Respawn(server, One, Address);

        Assert.True(server.IsAlive(respawned));
        Assert.Equal(respawned, Player.PawnOf(server, controller));
    }

    Prefab BuildPawn() {
        var root = Hierarchy.CreateTransform(authoring, LocalTransform.Identity);

        authoring.Add(root, NetworkId.None);
        authoring.Add(root, default(PlayerPawn));
        authoring.Add(root, default(MoveIntent));

        return Prefab.CaptureFrom(authoring, root, "Avatar");
    }
}

public sealed class LocalPlayerSystemTests : IDisposable {
    static readonly PlayerId Mine = new(1);
    static readonly PlayerId Theirs = new(2);

    readonly World world = new("local-player");

    public void Dispose() => world.Dispose();

    Entity Pawn(PlayerId owner) {
        var entity = world.Create(LocalTransform.Identity);

        world.Add(entity, default(PlayerPawn));
        world.Add(entity, new NetworkSpawn { Owner = owner.Value });
        world.Add(entity, default(NetworkInstance));

        return entity;
    }

    [Fact]
    public void AClientTakesChargeOfThePawnItOwns() {
        var controller = Player.Create(world);
        var system = new LocalPlayerSystem { Local = Mine, Controller = controller };
        var mine = Pawn(Mine);
        var theirs = Pawn(Theirs);

        Assert.Equal(1, system.Adopt(world));

        Assert.Equal(mine, Player.PawnOf(world, controller));
        Assert.False(Player.IsPossessed(world, theirs));
        Assert.Equal(1, system.AdoptedCount);
    }

    /// <summary>Idempotent, because it runs every frame and almost every frame has nothing to do.</summary>
    [Fact]
    public void AdoptingTwiceTakesNothingTheSecondTime() {
        var controller = Player.Create(world);
        var system = new LocalPlayerSystem { Local = Mine, Controller = controller };

        Pawn(Mine);

        Assert.Equal(1, system.Adopt(world));
        Assert.Equal(0, system.Adopt(world));
    }

    [Fact]
    public void AClientThatHasNotConnectedAdoptsNothing() {
        var controller = Player.Create(world);
        var system = new LocalPlayerSystem { Controller = controller };

        Pawn(Mine);

        Assert.Equal(0, system.Adopt(world));
    }

    [Fact]
    public void AClientWithNoControllerAdoptsNothing() {
        var system = new LocalPlayerSystem { Local = Mine };

        Pawn(Mine);

        Assert.Equal(0, system.Adopt(world));
    }

    /// <summary>
    ///     A spawn whose instance has not been built yet is not a pawn. Without the
    ///     <c>NetworkInstance</c> requirement a client would possess the bare entity a snapshot
    ///     created before the prefab was stamped onto it.
    /// </summary>
    [Fact]
    public void AStandInThatHasNotBeenBuiltIsNotAdopted() {
        var controller = Player.Create(world);
        var system = new LocalPlayerSystem { Local = Mine, Controller = controller };

        var waiting = world.Create(LocalTransform.Identity);
        world.Add(waiting, default(PlayerPawn));
        world.Add(waiting, new NetworkSpawn { Owner = Mine.Value });

        Assert.Equal(0, system.Adopt(world));
    }

    /// <summary>A new body is taken the frame it arrives, which is what a respawn looks like here.</summary>
    [Fact]
    public void ANewBodyIsTakenAfterTheOldOneDies() {
        var controller = Player.Create(world);
        var system = new LocalPlayerSystem { Local = Mine, Controller = controller };
        var possession = new PossessionSystem();
        var first = Pawn(Mine);

        system.Adopt(world);
        world.Destroy(first);
        possession.Apply(world);

        var second = Pawn(Mine);

        Assert.Equal(1, system.Adopt(world));
        Assert.Equal(second, Player.PawnOf(world, controller));
    }
}
