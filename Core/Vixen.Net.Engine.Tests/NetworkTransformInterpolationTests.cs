// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Time;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>The first engine system to sample a <c>SnapshotBuffer</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing in <c>Core/</c> ever built a <c>SnapshotBuffer</c></b>
///         (<see href="https://github.com/Rikarin/Vixen/issues/467" />): the ring, the interpolation,
///         the clamped extrapolation and the teleport-aware snap were all shipped and all driven from
///         a sample or a unit test. So the engine's own transform bridge was the *unsmoothed* path
///         and the smoothed one had no system at all.
///     </para>
///     <para>
///         These drive a real <see cref="ReplicationServer" /> into a real
///         <see cref="ReplicationClient" /> rather than hand-writing snapshot bytes, because the
///         property under test is about ticks — one sample per <i>applied</i> tick, sampled at the
///         clock's <i>interpolation</i> tick — and a fake that advanced those by itself would be a
///         test of the fake.
///     </para>
/// </remarks>
public sealed class NetworkTransformInterpolationTests : IDisposable {
    static readonly PlayerId Player = new(1);
    static readonly TickRate Rate = TickRate.Default;

    readonly World server = new("interpolate-server");
    readonly World client = new("interpolate-client");
    readonly ReplicationRegistry registry = new();
    readonly ReplicationServer sender;
    readonly ReplicationClient receiver;
    readonly TickManager clock = new(Rate);
    readonly byte[] buffer = new byte[4096];

    readonly Entity source;

    uint at;

    public NetworkTransformInterpolationTests() {
        registry.Register(new NetworkTransformReplicator());

        sender = new(registry);
        receiver = new(registry);

        source = server.Create(
            new NetworkId(1),
            new NetworkTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity }
        );
    }

    public void Dispose() {
        server.Dispose();
        client.Dispose();
    }

    /// <summary>The pose drawn is the one at the interpolation tick, which is behind the newest.</summary>
    /// <remarks>
    ///     A closed-form oracle rather than an eyeball: the object's x is its tick in metres, so the
    ///     answer has to be the interpolation tick plus the alpha. And the test asserts that tick is
    ///     genuinely behind what has been applied — without that half it would pass just as well
    ///     against a system that wrote the newest snapshot straight through, which is exactly what
    ///     <see cref="NetworkTransformApplySystem" /> does and what this exists to stop being the
    ///     only option.
    /// </remarks>
    [Fact]
    public void ThePoseDrawnIsTheOneAtTheInterpolationTick() {
        var system = Build();

        for (var tick = 1; tick <= 8; tick++) {
            Step(system, new Vector3(tick, 0f, 0f));
        }

        clock.Synchronize(receiver.AppliedTick, TimeSpan.Zero);
        system.Advance(client);

        var drawn = clock.InterpolationTick;

        Assert.True(drawn.IsBefore(receiver.AppliedTick), "the interpolation tick has to be behind the applied one");

        // The tolerance is the quantizer's, not a fudge: a position crosses the wire as three
        // quantized axes and comes back within half a level of where it started, which is the error
        // NetworkTransformReplicator spends on purpose. Anything larger is not rounding.
        Assert.Equal(
            drawn.Value + clock.Alpha,
            Mirror().Position.X,
            NetworkTransformReplicator.PositionRange.MaxError * 2f
        );

        Assert.True(system.SampledCount > 0);
        Assert.Equal(1, system.BufferedCount);
    }

    /// <summary>An object that did not move still gets a sample, so its buffer never runs dry.</summary>
    /// <remarks>
    ///     ⚠ The failure this pins is invisible. Sampling only what a snapshot <i>mentioned</i> leaves
    ///     a still object holding one sample for ever, and <c>SnapshotBuffer.TrySample</c> answers a
    ///     single sample by reporting itself starved — so the object is in the right place and the
    ///     counter says the buffer is broken, which is the worst of both.
    /// </remarks>
    [Fact]
    public void AStillObjectIsSampledAnyway() {
        var system = Build();

        Step(system, new Vector3(3f, 0f, 0f));

        // Nothing moves after this, so the only records crossing the wire belong to the second
        // entity — and the still one still has to be sampled on every one of those ticks.
        var mover = server.Create(
            new NetworkId(2),
            new NetworkTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity }
        );

        for (var tick = 1; tick <= 4; tick++) {
            server.Get<NetworkTransform>(mover).Position = new(tick, 0f, 0f);
            Step(system, null);
        }

        Assert.True(system.TryGetBuffer(new(1), out var still));
        Assert.Equal(5, still!.Count);
        Assert.Equal(0, still.StaleCount);
    }

    /// <summary>One sample per applied tick, however many frames run inside it.</summary>
    /// <remarks>
    ///     A frame runs at the display's rate and a snapshot arrives at the session's. Feeding per
    ///     frame would re-offer one tick over and over — which the buffer drops, so it costs nothing
    ///     and hides that only one of them was ever a new fact. The stale counter is what would say
    ///     so, and it is asserted here rather than the count alone.
    /// </remarks>
    [Fact]
    public void ManyFramesInsideOneAppliedTickAddOneSample() {
        var system = Build();

        Step(system, new Vector3(1f, 0f, 0f));

        system.Advance(client);
        system.Advance(client);
        system.Advance(client);

        Assert.True(system.TryGetBuffer(new(1), out var held));
        Assert.Equal(1, held!.Count);
        Assert.Equal(0, held.StaleCount);
    }

    /// <summary>An object that left is forgotten, rather than kept for the rest of the session.</summary>
    /// <remarks>
    ///     Leaving interest and being destroyed are the same thing to a client, so this is the
    ///     ordinary case rather than the exceptional one — and a buffered count that only ever grows
    ///     is a session-long leak of thirty-two poses per object that has ever been seen.
    /// </remarks>
    [Fact]
    public void AnObjectThatLeftIsEvicted() {
        var system = Build();

        Step(system, new Vector3(1f, 0f, 0f));
        Assert.Equal(1, system.BufferedCount);

        foreach (var entity in Mirrors()) {
            client.Destroy(entity);
        }

        system.Advance(client);

        Assert.Equal(0, system.BufferedCount);
        Assert.Equal(1, system.EvictedCount);
        Assert.False(system.TryGetBuffer(new(1), out _));
    }

    /// <summary>A rider whose vehicle has not arrived is left where it was, not put in world space.</summary>
    /// <remarks>
    ///     ⚠ The buffer holds what <c>NetworkTransform</c> said, and for a parented entity that is a
    ///     seat offset. Interpolating two offsets and writing the result as a world position puts the
    ///     rider near the origin for however many ticks the vehicle takes to arrive — the exact
    ///     failure <c>NetworkTransformApplySystem.ApplyFrames</c> refuses, and one this system could
    ///     reintroduce from behind because it runs after it.
    /// </remarks>
    [Fact]
    public void ARiderWithNoFrameIsNotPlaced() {
        var system = Build();

        Step(system, new Vector3(0f, 1.5f, -0.5f));

        var rider = Mirrors()[0];
        client.Add(rider, new NetworkParent { Value = 99 });
        client.Get<LocalTransform>(rider).Position = new(50f, 0f, 0f);

        // Everything placed before the entity became a rider stays placed; what must not happen is
        // one more placement after it. Counted rather than asserted at zero, because the first tick
        // of the fixture legitimately drew it as a root.
        var placed = system.SampledCount;

        Step(system, new Vector3(0f, 1.5f, -0.5f));

        clock.Synchronize(receiver.AppliedTick, TimeSpan.Zero);
        system.Advance(client);

        Assert.Equal(50f, client.Read<LocalTransform>(rider).Position.X, 3);
        Assert.True(system.UnresolvedFrameCount > 0);
        Assert.Equal(placed, system.SampledCount);
    }

    /// <summary>Neither seam is optional, because either one missing leaves nothing to do at all.</summary>
    [Fact]
    public void TheClockAndTheClientAreRequired() {
        Assert.Throws<ArgumentNullException>(() => new NetworkTransformInterpolateSystem(null!, clock));
        Assert.Throws<ArgumentNullException>(() => new NetworkTransformInterpolateSystem(receiver, null!));
    }

    NetworkTransformInterpolateSystem Build() => new(receiver, clock);

    /// <summary>One server tick, delivered, applied, and then one client frame.</summary>
    void Step(NetworkTransformInterpolateSystem system, Vector3? move) {
        at++;

        if (move is { } to) {
            server.Get<NetworkTransform>(source).Position = to;
        }

        sender.Capture(server, new(at));

        if (sender.TryWriteSnapshot(server, Player, new(at), buffer, out var snapshot)) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(Player, receiver.AppliedTick);
        }

        server.AdvanceVersion();

        // A receiving peer's entities carry a LocalTransform because a prefab gave them one; the
        // replicator only ever writes the networked component, so the test has to stand in for the
        // spawn system here.
        foreach (var entity in Mirrors()) {
            if (!client.Has<LocalTransform>(entity)) {
                client.Add(entity, new LocalTransform { Rotation = Quaternion.Identity, Scale = Vector3.One });
            }
        }

        system.Advance(client);
    }

    LocalTransform Mirror() => client.Read<LocalTransform>(Mirrors()[0]);

    List<Entity> Mirrors() {
        var found = new List<Entity>();

        foreach (var chunk in client.Chunks(new QueryDescription().WithAll<NetworkId>())) {
            found.AddRange(chunk.Entities);
        }

        return found;
    }
}
