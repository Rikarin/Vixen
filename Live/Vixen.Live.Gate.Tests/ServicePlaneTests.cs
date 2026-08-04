// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Gate.Tests;

/// <summary>Who hears what, and what happens when one listener is broken.</summary>
public class ServicePlaneTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_account_hears_its_own_messages_and_nobody_elses() {
        var plane = new ServicePlane();
        var alice = new Listener(Guid.NewGuid());
        var bob = new Listener(Guid.NewGuid());

        plane.Join(alice);
        plane.Join(bob);

        await plane.TellAsync(alice.Account, new("chat", "hello", Noon));

        Assert.Equal("hello", Assert.Single(alice.Heard).Detail);
        Assert.Empty(bob.Heard);
    }

    /// <summary>
    ///     Two clients on one account is an ordinary state of affairs — a launcher and a game, or two
    ///     machines — and both have to be told. It is also why the socket is keyed by account rather
    ///     than by character.
    /// </summary>
    [Fact]
    public async Task Two_sockets_on_one_account_are_both_told() {
        var plane = new ServicePlane();
        var account = Guid.NewGuid();
        var first = new Listener(account);
        var second = new Listener(account);

        plane.Join(first);
        plane.Join(second);

        await plane.TellAsync(account, new("draining", "your shard is going away", Noon));

        Assert.Equal(2, plane.Count);
        Assert.Single(first.Heard);
        Assert.Single(second.Heard);
    }

    [Fact]
    public async Task A_broadcast_reaches_everybody_and_is_what_a_catalog_publication_is() {
        var plane = new ServicePlane();
        var listeners = Enumerable.Range(0, 5).Select(_ => new Listener(Guid.NewGuid())).ToList();

        foreach (var listener in listeners) {
            plane.Join(listener);
        }

        await plane.TellEveryoneAsync(new("catalog", "0.1.1+deadbeef", Noon));

        Assert.All(listeners, listener => Assert.Single(listener.Heard));
    }

    [Fact]
    public async Task Leaving_stops_the_messages_and_forgets_the_account() {
        var plane = new ServicePlane();
        var alice = new Listener(Guid.NewGuid());

        plane.Join(alice);
        plane.Leave(alice);

        await plane.TellAsync(alice.Account, new("chat", "hello", Noon));

        Assert.Empty(alice.Heard);
        Assert.Equal(0, plane.Count);
    }

    [Fact]
    public async Task Telling_an_account_that_is_not_listening_is_not_an_error() {
        var plane = new ServicePlane();

        await plane.TellAsync(Guid.NewGuid(), new("chat", "into the void", Noon));

        Assert.Equal(0, plane.Count);
    }

    /// <summary>
    ///     One client's dead socket must not be able to fail a broadcast to everybody else — and a
    ///     broadcast is exactly where that is most expensive, because which listeners get skipped
    ///     depends on dictionary order.
    /// </summary>
    [Fact]
    public async Task A_broken_listener_is_dropped_rather_than_taking_the_broadcast_with_it() {
        var plane = new ServicePlane();
        var broken = new Listener(Guid.NewGuid()) { Broken = true };
        var fine = new Listener(Guid.NewGuid());

        plane.Join(broken);
        plane.Join(fine);

        await plane.TellEveryoneAsync(new("catalog", "0.1.1+deadbeef", Noon));

        Assert.Single(fine.Heard);
        Assert.Empty(broken.Heard);
        Assert.Equal(1, plane.Count);
    }

    sealed class Listener(Guid account) : IGateSubscriber {
        public Guid Account => account;

        public List<GateEvent> Heard { get; } = [];

        public bool Broken { get; init; }

        public ValueTask PostAsync(GateEvent message) {
            if (Broken) {
                throw new InvalidOperationException("this socket is gone");
            }

            Heard.Add(message);

            return ValueTask.CompletedTask;
        }
    }
}
