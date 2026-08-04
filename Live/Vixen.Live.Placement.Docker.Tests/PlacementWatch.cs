// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>Reads a placement backend's event stream, one event at a time, with a deadline.</summary>
/// <remarks>
///     ⚠ <b>Subscribing happens in the constructor, not on the first read, and it has to.</b>
///     <c>WatchAsync</c> registers its channel synchronously — an async iterator's body runs up to
///     its first <c>await</c> when <c>MoveNextAsync</c> is first called — so the pending read is
///     started here, before the test starts anything. A watcher that subscribed later would miss the
///     <c>Started</c> event it was written to observe, occasionally, on a loaded machine.
/// </remarks>
sealed class PlacementWatch : IAsyncDisposable {
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    readonly CancellationTokenSource cancellation = new();
    readonly IAsyncEnumerator<PlacementEvent> events;

    Task<bool> pending;

    public PlacementWatch(IRealmPlacement placement) {
        events = placement.WatchAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        pending = events.MoveNextAsync().AsTask();
    }

    /// <summary>Whether an event is already waiting, which is how "nothing happened" is asserted.</summary>
    public bool HasPending => pending.IsCompleted;

    /// <summary>The next event, or a failed test.</summary>
    public async Task<PlacementEvent> NextAsync() {
        Assert.True(await pending.WaitAsync(Patience), "The event stream ended.");

        var placement = events.Current;

        pending = events.MoveNextAsync().AsTask();

        return placement;
    }

    public async ValueTask DisposeAsync() {
        await cancellation.CancelAsync();

        try {
            await pending;
        } catch (OperationCanceledException) {
            // What cancelling the stream does. Observed rather than left on the task, so a finaliser
            // does not report it as an unobserved exception in an unrelated test.
        }

        cancellation.Dispose();
    }
}
