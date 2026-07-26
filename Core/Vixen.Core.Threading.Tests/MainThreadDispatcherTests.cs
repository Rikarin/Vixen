// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Threading.Tests;

public class MainThreadDispatcherTests {
    [Fact]
    public void PostedWorkRunsOnDrainAndInOrder() {
        var dispatcher = new MainThreadDispatcher();
        var order = new List<int>();

        dispatcher.Post(() => order.Add(1));
        dispatcher.Post(() => order.Add(2));
        dispatcher.Post(() => order.Add(3));

        Assert.Empty(order);
        Assert.Equal(3, dispatcher.Drain());
        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public void PostingFromTheMainThreadStillQueues() {
        var dispatcher = new MainThreadDispatcher();
        var ran = false;

        dispatcher.Post(() => ran = true);

        Assert.False(ran);
        dispatcher.Drain();
        Assert.True(ran);
    }

    [Fact]
    public void WorkPostedDuringADrainWaitsForTheNextOne() {
        var dispatcher = new MainThreadDispatcher();
        var order = new List<int>();

        dispatcher.Post(() => {
                order.Add(1);
                dispatcher.Post(() => order.Add(2));
            }
        );

        // A drain that ran what it queued would never end for a job that re-posts itself, so a
        // frame point that calls this could stall the frame indefinitely.
        Assert.Equal(1, dispatcher.Drain());
        Assert.Equal([1], order);

        Assert.Equal(1, dispatcher.Drain());
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void PostCarriesState() {
        var dispatcher = new MainThreadDispatcher();
        var seen = 0;

        dispatcher.Post(static state => ((int[])state)[0] = 42, new[] { 0 });
        dispatcher.Post(value => seen = value, 7);
        dispatcher.Drain();

        Assert.Equal(7, seen);
    }

    [Fact]
    public void SendFromAnotherThreadRunsOnTheBoundThread() {
        var dispatcher = new MainThreadDispatcher();
        var mainThreadId = Environment.CurrentManagedThreadId;
        var ranOn = 0;

        RunOffThread(() => dispatcher.Send(() => ranOn = Environment.CurrentManagedThreadId), dispatcher);

        Assert.Equal(mainThreadId, ranOn);
    }

    [Fact]
    public void SendRethrowsWhatTheWorkThrew() {
        var dispatcher = new MainThreadDispatcher();

        RunOffThread(
            () => Assert.Throws<InvalidOperationException>(
                () => dispatcher.Send(() => throw new InvalidOperationException("nope"))
            ),
            dispatcher
        );
    }

    [Fact]
    public void SendFromTheMainThreadIsRefusedRatherThanDeadlocked() {
        var dispatcher = new MainThreadDispatcher();
        Assert.Throws<InvalidOperationException>(() => dispatcher.Send(() => { }));
    }

    [Fact]
    public void DrainingFromTheWrongThreadIsRefused() {
        var dispatcher = new MainThreadDispatcher();

        RunOffThread(
            () => {
                Assert.False(dispatcher.IsMainThread);
                Assert.Throws<InvalidOperationException>(() => dispatcher.Drain());
                Assert.Throws<InvalidOperationException>(dispatcher.AssertMainThread);
            },
            dispatcher: null
        );
    }

    [Fact]
    public void PostRejectsNull() {
        var dispatcher = new MainThreadDispatcher();
        Assert.Throws<ArgumentNullException>(() => dispatcher.Post(null!));
        Assert.Throws<ArgumentNullException>(() => dispatcher.Post<int>(null!, 0));
    }

    [Fact]
    public void AnExceptionFromPostedWorkReachesTheFrameLoop() {
        var dispatcher = new MainThreadDispatcher();
        dispatcher.Post(() => throw new InvalidOperationException("nope"));

        // Nobody is waiting on a posted item, so swallowing it here would lose the only report.
        Assert.Throws<InvalidOperationException>(() => dispatcher.Drain());
    }

    /// <summary>
    ///     Runs <paramref name="body" /> on a thread that is definitely not this one, draining
    ///     <paramref name="dispatcher" /> until it finishes.
    /// </summary>
    /// <remarks>
    ///     A dedicated thread rather than <see cref="Task.Run(Action)" />. These assertions all turn
    ///     on "a different managed thread", and a pool thread is free to be the very thread the test
    ///     released when it awaited — which makes the test pass or fail on the pool's scheduling
    ///     rather than on the dispatcher's behaviour.
    /// </remarks>
    static void RunOffThread(Action body, MainThreadDispatcher? dispatcher) {
        Exception? failure = null;
        var thread = new Thread(() => {
                try {
                    body();
                } catch (Exception exception) {
                    failure = exception;
                }
            }
        ) { IsBackground = true };

        thread.Start();

        // Keep draining while it runs: a Send blocks until the bound thread picks the item up.
        while (!thread.Join(TimeSpan.FromMilliseconds(1))) {
            dispatcher?.Drain();
        }

        dispatcher?.Drain();

        if (failure is not null) {
            throw failure;
        }
    }
}
