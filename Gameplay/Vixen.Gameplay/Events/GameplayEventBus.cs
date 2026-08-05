// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>What a subscriber is called with.</summary>
/// <param name="gameplayEvent">What happened.</param>
/// <remarks>
///     By <c>in</c> reference rather than by value, because a bus that copied a six-field struct once
///     per subscriber would do it for every subscriber that then rejected the event on its verb.
/// </remarks>
public delegate void GameplayEventCallback(in GameplayEvent gameplayEvent);

/// <summary>One subscription, and the handle that cancels it.</summary>
public sealed class GameplayEventSubscription {
    internal GameplayEventSubscription(GameplayEventBus bus, GameplayEventFilter filter, GameplayEventCallback handler) {
        Bus = bus;
        Filter = filter;
        Handler = handler;
    }

    /// <summary>Which bus it is on.</summary>
    public GameplayEventBus Bus { get; }

    /// <summary>What it wanted.</summary>
    public GameplayEventFilter Filter { get; }

    /// <summary>Whether it is still listening.</summary>
    public bool IsActive { get; internal set; } = true;

    internal GameplayEventCallback Handler { get; }

    /// <summary>Stops listening.</summary>
    /// <returns>Whether it was still listening.</returns>
    public bool Cancel() => Bus.Unsubscribe(this);
}

/// <summary>Where gameplay events are posted and where filters wait for them.</summary>
/// <remarks>
///     <para>
///         <b>In the kernel because both ends of every meeting are above it.</b> Combat posts kills,
///         crafting posts crafts, the economy posts purchases; quests, dynamic events and — when they
///         are built — achievements and collections listen. Putting the bus in any one of those makes
///         every other one depend on it, which is the horizontal edge doc 28's spine forbids.
///     </para>
///     <para>
///         ⚠ <b>Subscribing or cancelling during a dispatch is ordinary and is handled.</b> It is not
///         an edge case: the last kill of an objective completes a stage, which cancels that stage's
///         subscriptions and takes out the next stage's — all inside the handler the bus is currently
///         calling. So a cancellation during dispatch only clears a flag and the list is compacted
///         afterwards, and a subscription made during dispatch is held aside until the dispatch ends.
///     </para>
///     <para>
///         ⚠ <b>A subscription made during a dispatch does not see the event being dispatched.</b> The
///         alternative is a stage that begins mid-event seeing the kill that ended the previous stage,
///         which is one kill counted twice — and doc 28's quest property test is that no objective
///         completes twice.
///     </para>
///     <para>
///         <b>Delivery is in subscription order</b>, so a replay of the same posts against the same
///         subscriptions does the same things in the same order. Nothing here is randomised and nothing
///         depends on a dictionary's enumeration.
///     </para>
/// </remarks>
public sealed class GameplayEventBus {
    readonly List<GameplayEventSubscription> subscriptions = [];
    readonly List<GameplayEventSubscription> pending = [];

    int dispatching;
    int cancelled;

    /// <summary>How many subscriptions are listening.</summary>
    public int Count => subscriptions.Count - cancelled + pending.Count;

    /// <summary>How many events have ever been posted. What an event id is seeded from.</summary>
    public ulong Posted { get; private set; }

    /// <summary>Starts listening.</summary>
    /// <param name="filter">Which events.</param>
    /// <param name="handler">What to do with them.</param>
    /// <returns>The subscription, which is also how to stop.</returns>
    /// <remarks>
    ///     A filter that can never match — <see cref="GameplayEventFilter.IsSome" /> is false — is
    ///     accepted rather than refused. It is what an objective naming a verb this build does not have
    ///     compiles to, and the report of that belongs to whatever compiled it, which can say which
    ///     definition and which line.
    /// </remarks>
    public GameplayEventSubscription Subscribe(GameplayEventFilter filter, GameplayEventCallback handler) {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new GameplayEventSubscription(this, filter, handler);

        if (dispatching > 0) {
            pending.Add(subscription);
        } else {
            subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Stops a subscription listening.</summary>
    /// <param name="subscription">The one to stop.</param>
    /// <returns>Whether it was listening.</returns>
    public bool Unsubscribe(GameplayEventSubscription? subscription) {
        if (subscription is null || !subscription.IsActive || subscription.Bus != this) {
            return false;
        }

        subscription.IsActive = false;

        if (pending.Remove(subscription)) {
            return true;
        }

        cancelled++;

        if (dispatching == 0) {
            Compact();
        }

        return true;
    }

    /// <summary>Forgets every subscription.</summary>
    /// <remarks>What a realm does when it drops a scene, so nothing is left holding a dead handler.</remarks>
    public void Clear() {
        foreach (var subscription in subscriptions) {
            subscription.IsActive = false;
        }

        foreach (var subscription in pending) {
            subscription.IsActive = false;
        }

        subscriptions.Clear();
        pending.Clear();
        cancelled = 0;
    }

    /// <summary>Tells everybody who wanted to know.</summary>
    /// <param name="gameplayEvent">What happened.</param>
    /// <returns>How many subscribers it reached.</returns>
    public int Post(in GameplayEvent gameplayEvent) {
        Posted++;

        var delivered = 0;

        dispatching++;

        try {
            // By index over the live list rather than over a copy: a handler can only append (to
            // `pending`) or clear a flag, so the list cannot be reordered underneath this walk and the
            // common case — a post nobody wanted — allocates nothing.
            for (var index = 0; index < subscriptions.Count; index++) {
                var subscription = subscriptions[index];

                if (!subscription.IsActive || !subscription.Filter.Matches(gameplayEvent)) {
                    continue;
                }

                subscription.Handler(gameplayEvent);
                delivered++;
            }
        } finally {
            dispatching--;

            if (dispatching == 0) {
                Flush();
            }
        }

        return delivered;
    }

    void Flush() {
        if (pending.Count > 0) {
            subscriptions.AddRange(pending);
            pending.Clear();
        }

        if (cancelled > 0) {
            Compact();
        }
    }

    void Compact() {
        subscriptions.RemoveAll(static subscription => !subscription.IsActive);
        cancelled = 0;
    }
}
