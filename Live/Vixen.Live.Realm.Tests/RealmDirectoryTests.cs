// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Realms.Tests;

/// <summary>ADR-016's rule, and the threading property that is the whole point of it.</summary>
public sealed class RealmDirectoryTests {
    [Fact]
    public void AnAnswerIsNotAppliedUntilSomebodyDrains() {
        using var directory = new RealmDirectory();
        var applied = 0;

        directory.Ask(_ => Task.FromResult(7), answer => applied = answer);

        // The realm keeps simulating. Nothing has touched anything it owns, whatever the task did.
        Assert.Equal(0, applied);

        Eventually(directory, () => directory.AnsweredCount == 1);

        Assert.Equal(7, applied);
    }

    [Fact]
    public void TheAnswerRunsOnTheThreadThatDrained() {
        // The entire value of the class: everything the callback touches — the world, the session,
        // the admission list — is single-threaded and stays that way.
        using var directory = new RealmDirectory();

        var expected = Environment.CurrentManagedThreadId;
        var actual = 0;

        directory.Ask(
            async cancellation => {
                await Task.Yield();
                await Task.Delay(5, cancellation);

                return 1;
            },
            _ => actual = Environment.CurrentManagedThreadId
        );

        Eventually(directory, () => actual != 0);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PendingCountsWhatIsOutstanding() {
        using var directory = new RealmDirectory();
        var release = new TaskCompletionSource();

        directory.Ask(_ => release.Task.ContinueWith(_ => 1, TaskScheduler.Default), _ => { });

        Assert.Equal(1, directory.Pending);

        release.SetResult();

        Eventually(directory, () => directory.AnsweredCount == 1);

        Assert.Equal(0, directory.Pending);
    }

    [Fact]
    public void AFaultIsCountedAndOfferedRatherThanThrownAtTheFrame() {
        using var directory = new RealmDirectory();
        Exception? seen = null;

        directory.Ask<int>(
            _ => throw new InvalidOperationException("the orchestrator is not answering"),
            _ => Assert.Fail("A faulted call must not be applied."),
            failure => seen = failure
        );

        Eventually(directory, () => directory.FaultedCount == 1);

        Assert.IsType<InvalidOperationException>(seen);
        Assert.Equal(0, directory.AnsweredCount);
    }

    [Fact]
    public void AFaultWithNoHandlerIsSurvived() {
        using var directory = new RealmDirectory();

        directory.Ask<int>(_ => throw new InvalidOperationException("nobody is listening"), _ => { });

        Eventually(directory, () => directory.FaultedCount == 1);
    }

    [Fact]
    public void ACallbackThatThrowsDoesNotTakeTheRestOfTheQueueWithIt() {
        // Losing one answer is survivable. Losing the tick is not.
        using var directory = new RealmDirectory();
        var second = 0;

        directory.Ask(_ => Task.FromResult(1), _ => throw new InvalidOperationException("oops"));
        directory.Ask(_ => Task.FromResult(2), answer => second = answer);

        Eventually(directory, () => second == 2);

        Assert.Equal(1, directory.FaultedCount);
        Assert.Equal(1, directory.AnsweredCount);
    }

    [Fact]
    public void ADisposedDirectoryAsksNothingMore() {
        var directory = new RealmDirectory();

        directory.Dispose();
        directory.Dispose();

        directory.Ask(_ => Task.FromResult(1), _ => Assert.Fail("A disposed directory must not ask."));

        Assert.Equal(0, directory.Pending);
        Assert.Equal(0, directory.Drain());
    }

    static void Eventually(RealmDirectory directory, Func<bool> condition) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline) {
            directory.Drain();

            if (condition()) {
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("The condition was still false after five seconds of draining.");
    }
}
