// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Net.Engine;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>A module a couple of behaviours want, so nesting is exercised by a real shape.</summary>
public sealed class VitalsModule : NetworkModule {
    public SyncVar<byte> Health { get; }

    public SyncVar<bool> Alive { get; }

    public VitalsModule() {
        Health = Declare(new SyncVar<byte>(100), nameof(Health));
        Alive = Declare(new SyncVar<bool>(true), nameof(Alive));
    }
}

/// <summary>A behaviour's own state: two fields of its own and a module it holds one of.</summary>
public sealed class PlayerState : NetworkModule {
    public SyncVar<int> Score { get; }

    public SyncVar<Vector3> Spawn { get; }

    public VitalsModule Vitals { get; }

    public PlayerState() {
        Score = Declare(new SyncVar<int>(0), nameof(Score));
        Spawn = Declare(new SyncVar<Vector3>(Vector3.Zero), nameof(Spawn));
        Vitals = Nest(new VitalsModule(), nameof(Vitals));
    }
}

/// <summary>The behaviour under test.</summary>
public sealed class PlayerBehaviour : NetworkBehaviour {
    public PlayerState Sync { get; } = new();

    /// <inheritdoc />
    protected override NetworkModule Build() => Sync;
}

/// <summary>SyncVar and NetworkModule: that they replicate, and through the mechanism that exists.</summary>
public sealed class SyncStateTests : IDisposable {
    static readonly PlayerId Player = new(1);

    readonly World server = new("server");
    readonly World client = new("client");
    readonly BehaviorStore serverStore;
    readonly BehaviorStore clientStore;
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly ReplicationServer sender;
    readonly ReplicationClient receiver;
    readonly byte[] buffer = new byte[8192];

    uint tick = 1;
    NetworkId spawned;

    public SyncStateTests() {
        serverStore = new(server);
        clientStore = new(client);
        registry.Register(new SyncStateReplicator<PlayerBehaviour>(serverStore));
        sender = new(registry);

        var clientRegistry = new ReplicationRegistry();
        clientRegistry.Register(new SyncStateReplicator<PlayerBehaviour>(clientStore));
        receiver = new(clientRegistry);
    }

    public void Dispose() {
        server.Dispose();
        client.Dispose();
    }

    /// <summary>A nested module's fields are the outer one's fields, named by their path.</summary>
    /// <remarks>
    ///     What makes the bandwidth report say <c>PlayerState.Vitals.Health</c> rather than
    ///     <c>Health</c>, and what makes a module reusable without two of them colliding.
    /// </remarks>
    [Fact]
    public void ANestedModulesFieldsAreFlattenedAndNamedByTheirPath() {
        var state = new PlayerState();
        state.Seal(nameof(PlayerState));

        var names = state.Fields.Select(field => field.Name).ToArray();

        // Own fields in declaration order, then nested modules' — not source order, because fields
        // and modules are declared through different calls. Deterministic is what matters: both ends
        // build the layout the same way from the same constructor and never exchange it.
        Assert.Equal(
            ["PlayerState.Score", "PlayerState.Spawn", "PlayerState.Vitals.Health", "PlayerState.Vitals.Alive"],
            names
        );
    }

    /// <summary>The layout is the module's fields' lanes, end to end.</summary>
    /// <remarks>
    ///     The whole reason this authoring style gets delta encoding for nothing: a lane layout is
    ///     exactly what <c>DeltaCodec</c> needs, and a module is a thing that has one.
    /// </remarks>
    [Fact]
    public void AModulesLanesAddUpToWhatItWrites() {
        var state = new PlayerState();
        state.Seal();

        var writer = new BitWriter(buffer);
        state.Write(ref writer);

        Assert.Equal(writer.BitsWritten, DeltaCodec.TotalBits(state.Lanes));
    }

    [Fact]
    public void AServersSyncVarReachesTheClient() {
        var behaviour = Spawn();

        behaviour.Sync.Score.Value = 42;
        behaviour.Sync.Vitals.Health.Value = 70;
        behaviour.MarkChanged();

        Assert.True(Replicate());

        var mirror = Mirror();

        Assert.Equal(42, mirror.Sync.Score.Value);
        Assert.Equal((byte)70, mirror.Sync.Vitals.Health.Value);
        Assert.True(mirror.Sync.Vitals.Alive.Value);
    }

    [Fact]
    public void AValueArrivingThatDiffers_RaisesChanged() {
        var behaviour = Spawn();
        behaviour.Sync.Score.Value = 1;
        behaviour.MarkChanged();
        Replicate();

        var mirror = Mirror();
        var seen = new List<(int From, int To)>();
        mirror.Sync.Score.Changed += (from, to) => seen.Add((from, to));

        behaviour.Sync.Score.Value = 9;
        behaviour.MarkChanged();
        Replicate();

        Assert.Equal([(1, 9)], seen);

        // Arriving again with the same value is not a change, and a handler that fired anyway would
        // make every snapshot look like an event.
        behaviour.Sync.Spawn.Value = new(1f, 2f, 3f);
        behaviour.MarkChanged();
        Replicate();

        Assert.Equal([(1, 9)], seen);
    }

    /// <summary>Behaviour state goes through the delta packer like anything else.</summary>
    /// <remarks>
    ///     The claim the whole package is built to make: "two authoring styles, one mechanism
    ///     underneath". If this were a parallel path it would be a second encoder to keep in step.
    /// </remarks>
    [Fact]
    public void ChangingOneFieldSendsADifference_NotTheWholeModule() {
        var behaviour = Spawn();
        behaviour.Sync.Score.Value = 1;
        behaviour.MarkChanged();
        Replicate();

        behaviour.Sync.Score.Value = 2;
        behaviour.MarkChanged();
        Replicate();

        Assert.True(sender.DeltaRecordCount > 0, "nothing went as a difference");
        Assert.Equal(2, Mirror().Sync.Score.Value);
    }

    [Fact]
    public void AModuleThatHasBeenSealed_RefusesMoreFields() {
        var state = new PlayerState();
        state.Seal();

        Assert.Throws<InvalidOperationException>(() => new SealedTooLate(state));
    }

    [Fact]
    public void ATypeTheWireDoesNotKnow_IsRefusedAtConstruction() =>
        Assert.Throws<NotSupportedException>(() => new SyncVar<DateTime>());

    PlayerBehaviour Spawn() {
        spawned = ids.Next();

        var entity = server.Create(spawned, new SyncStateVersion());
        var behaviour = serverStore.Add<PlayerBehaviour>(entity);
        behaviour.State.Seal();
        behaviour.IsServer = true;

        return behaviour;
    }

    PlayerBehaviour Mirror() {
        Assert.True(receiver.TryGetEntity(spawned, out var entity));

        var behaviour = clientStore.Get<PlayerBehaviour>(entity);
        Assert.NotNull(behaviour);

        return behaviour;
    }

    bool Replicate() {
        var at = new Tick(tick);
        sender.Capture(server, at);

        var wrote = sender.TryWriteSnapshot(server, Player, at, buffer, out var snapshot);

        if (wrote) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(Player, at);
        }

        server.AdvanceVersion();
        tick++;

        return wrote;
    }

    /// <summary>A module that tries to declare a field after the layout was fixed.</summary>
    sealed class SealedTooLate : NetworkModule {
        public SealedTooLate(NetworkModule already) {
            ArgumentNullException.ThrowIfNull(already);
            already.Seal();

            // Declaring into a module whose layout is already fixed is the mistake being asserted.
            Seal();
            Declare(new SyncVar<int>(0), "late");
        }
    }
}
