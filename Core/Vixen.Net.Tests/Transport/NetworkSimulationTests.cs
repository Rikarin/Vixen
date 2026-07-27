// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Net.Transport;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Net.Tests.Transport;

/// <summary>
///     The simulation decorator: that it injects what it says it injects, that it never violates the
///     channel it is injecting into, and that the same seed replays exactly.
/// </summary>
public sealed class NetworkSimulationTests {
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void Latency_HoldsAPayloadUntilItsDelayHasPassed() {
        using var harness = new Harness(
            new() { Latency = TimeSpan.FromMilliseconds(100) },
            seed: 7
        );

        harness.Connect();
        harness.Client.SendToServer(Bytes("slow"), Channel.Reliable);

        harness.Pump(5); // 50 ms
        Assert.Empty(harness.ServerEvents.Payloads(TransportRole.Server));

        harness.Pump(6); // 110 ms
        Assert.Equal(["slow"], harness.ServerEvents.Texts(TransportRole.Server));
    }

    [Fact]
    public void AReliableChannel_IsNotDropped_EvenWhenEverythingIsLost() {
        using var harness = new Harness(new() { LossChance = 1.0 }, seed: 11);
        harness.Connect();

        for (var i = 0; i < 16; i++) {
            harness.Client.SendToServer(Bytes(i.ToString(CultureInfo.InvariantCulture)), Channel.Reliable);
            harness.Client.SendToServer(Bytes("u"), Channel.Unreliable);
        }

        harness.Pump();

        // The sixteen reliable ones all arrived; the sixteen unreliable ones are the ones dropped.
        Assert.Equal(16, harness.ServerEvents.Payloads(TransportRole.Server).Count);
        Assert.Equal(16, harness.Simulation.DroppedPayloadCount);
    }

    [Fact]
    public void AnUnreliableChannel_AtTotalLoss_DeliversNothing() {
        using var harness = new Harness(new() { LossChance = 1.0 }, seed: 13);
        harness.Connect();

        for (var i = 0; i < 16; i++) {
            harness.Client.SendToServer(Bytes("gone"), Channel.Unreliable);
        }

        harness.Pump();

        Assert.Empty(harness.ServerEvents.Payloads(TransportRole.Server));
        Assert.Equal(16, harness.Simulation.DroppedPayloadCount);
    }

    [Fact]
    public void AReliableChannel_KeepsItsOrder_HoweverTheJitterFalls() {
        var arrived = SendManyUnderJitter(Channel.Reliable, seed: 17);

        Assert.Equal(50, arrived.Count);
        Assert.Equal(Numbers(50), arrived);
    }

    [Fact]
    public void AnUnorderedChannel_UnderJitter_ArrivesOutOfOrder() {
        var arrived = SendManyUnderJitter(Channel.Unreliable, seed: 17);

        // Nothing is lost in this profile, so everything sent is here — in some other order.
        Assert.Equal(50, arrived.Count);
        Assert.NotEqual(Numbers(50), arrived);
    }

    [Fact]
    public void ASequencedChannel_MayLosePayloads_ButNeverDeliversAnOldOne() {
        using var harness = new Harness(
            new() {
                Latency = TimeSpan.FromMilliseconds(50),
                Jitter = TimeSpan.FromMilliseconds(45),
                LossChance = 0.3
            },
            seed: 19
        );

        harness.Connect();

        for (var i = 0; i < 200; i++) {
            harness.Client.SendToServer(Bytes(i.ToString(CultureInfo.InvariantCulture)), Channel.Sequenced);
        }

        harness.Pump(40);

        var arrived = harness.ServerEvents.Texts(TransportRole.Server);

        Assert.InRange(arrived.Count, 100, 199); // lossy, but not silent
        var previous = -1;

        foreach (var text in arrived) {
            var value = int.Parse(text, CultureInfo.InvariantCulture);
            Assert.True(value > previous, $"{value} arrived after {previous}.");
            previous = value;
        }
    }

    [Fact]
    public void Duplication_DeliversAnUnreliablePayloadTwice_AndAReliableOneOnce() {
        using var harness = new Harness(new() { DuplicateChance = 1.0 }, seed: 23);
        harness.Connect();

        harness.Client.SendToServer(Bytes("twice"), Channel.Unreliable);
        harness.Client.SendToServer(Bytes("once"), Channel.Reliable);
        harness.Pump();

        Assert.Equal(["twice", "twice", "once"], harness.ServerEvents.Texts(TransportRole.Server));
        Assert.Equal(1, harness.Simulation.DuplicatedPayloadCount);
    }

    [Fact]
    public void TheSameSeed_ReplaysTheSameRun_AndADifferentOneDoesNot() {
        var first = LossyRun(seed: 2024);
        var again = LossyRun(seed: 2024);
        var other = LossyRun(seed: 2025);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void APayloadStillInFlightWhenTheConnectionEnds_NeverArrives() {
        using var harness = new Harness(new() { Latency = TimeSpan.FromMilliseconds(100) }, seed: 29);
        harness.Connect();

        harness.Client.SendToServer(Bytes("in flight"), Channel.Reliable);
        harness.Client.StopClient();
        harness.Pump(20);

        Assert.Empty(harness.ServerEvents.Payloads(TransportRole.Server));
    }

    [Fact]
    public void ASimulationThatInjectsSomething_SaysItIsLossy() {
        using var perfect = new Harness(NetworkSimulationProfile.Perfect, seed: 31);
        using var lossy = new Harness(new() { LossChance = 0.1 }, seed: 31);

        Assert.False(perfect.Simulation.Capabilities.IsLossy);
        Assert.True(lossy.Simulation.Capabilities.IsLossy);
        Assert.True(perfect.Simulation.Capabilities.IsInProcess);
    }

    [Fact]
    public void TimeRunningBackwards_Throws() {
        using var harness = new Harness(NetworkSimulationProfile.Perfect, seed: 37);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => harness.Simulation.Poll(TimeSpan.FromSeconds(-1), harness.ClientEvents)
        );
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 0)]
    [InlineData(0, -0.5)]
    [InlineData(0, 1.5)]
    public void AChanceThatIsNotAChance_Throws(double loss, double duplicate) {
        var profile = new NetworkSimulationProfile { LossChance = loss, DuplicateChance = duplicate };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetworkSimulation(new LocalTransport(new()), profile, seed: 41)
        );
    }

    [Fact]
    public void ANegativeDelay_Throws() {
        var profile = new NetworkSimulationProfile { Latency = TimeSpan.FromMilliseconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetworkSimulation(new LocalTransport(new()), profile, seed: 43)
        );
    }

    [Fact]
    public void ASimulationThatDoesNotOwnItsTransport_LeavesItAlone() {
        using var transport = new LocalTransport(new());
        var simulation = new NetworkSimulation(transport, NetworkSimulationProfile.Perfect, seed: 47, ownsInner: false);

        transport.StartServer();
        simulation.Dispose();

        Assert.Equal(TransportState.Running, transport.ServerState);
    }

    static List<string> SendManyUnderJitter(Channel channel, ulong seed) {
        using var harness = new Harness(
            new() { Latency = TimeSpan.FromMilliseconds(50), Jitter = TimeSpan.FromMilliseconds(45) },
            seed
        );

        harness.Connect();

        for (var i = 0; i < 50; i++) {
            harness.Client.SendToServer(Bytes(i.ToString(CultureInfo.InvariantCulture)), channel);
        }

        harness.Pump(30);

        return harness.ServerEvents.Texts(TransportRole.Server);
    }

    static List<string> LossyRun(ulong seed) {
        using var harness = new Harness(
            new() {
                Latency = TimeSpan.FromMilliseconds(30),
                Jitter = TimeSpan.FromMilliseconds(20),
                LossChance = 0.5,
                DuplicateChance = 0.1
            },
            seed
        );

        harness.Connect();

        for (var i = 0; i < 100; i++) {
            harness.Client.SendToServer(Bytes(i.ToString(CultureInfo.InvariantCulture)), Channel.Unreliable);
        }

        harness.Pump(30);

        return harness.ServerEvents.Texts(TransportRole.Server);
    }

    static List<string> Numbers(int count) {
        var result = new List<string>(count);

        for (var i = 0; i < count; i++) {
            result.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A plain server and a simulated client on one in-process network.</summary>
    sealed class Harness : IDisposable {
        readonly LocalNetwork network = new();

        public LocalTransport Server { get; }
        public NetworkSimulation Simulation { get; }
        public ITransport Client => Simulation;
        public EventRecorder ServerEvents { get; } = new();
        public EventRecorder ClientEvents { get; } = new();

        public Harness(NetworkSimulationProfile profile, ulong seed) {
            Server = new(network);
            Simulation = new(new LocalTransport(network), profile, seed);
        }

        public void Connect() {
            Server.StartServer();
            Simulation.StartClient();
            Pump();

            Assert.Single(ClientEvents.Connects(TransportRole.Client));
            ServerEvents.Clear();
            ClientEvents.Clear();
        }

        public void Pump(int rounds = 4) {
            for (var round = 0; round < rounds; round++) {
                // The client first: it is the one holding payloads back, and what it releases this
                // round should be deliverable by the server in the same one.
                Simulation.Poll(Step, ClientEvents);
                Server.Poll(Step, ServerEvents);
            }
        }

        public void Dispose() {
            Simulation.Dispose();
            Server.Dispose();
        }
    }
}
