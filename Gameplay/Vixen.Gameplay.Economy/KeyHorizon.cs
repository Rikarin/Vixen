// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay.Economy;

/// <summary>How long an applied idempotency key goes on being remembered.</summary>
/// <remarks>
///     <para>
///         <b>A ledger's key set is the only unbounded thing in a realm that cannot simply be
///         cleared.</b> It is what makes a retried trade write nothing the second time, so every key
///         it drops is a retry it will silently apply twice — and a shard that runs for a week
///         otherwise keeps every key of that week. <c>Samples/14-Mmo</c>'s soak measured it as roughly
///         a megabyte a minute, for ever.
///     </para>
///     <para>
///         ⚠ <b>The two failure modes are not comparable, and that asymmetry is the whole design.</b>
///         A horizon that is too long costs memory, which is visible in a graph and recoverable by
///         restarting. A horizon that is too short duplicates an item, which is invisible, permanent,
///         and indistinguishable from a duplication exploit when a player reports it. So there is no
///         default horizon and <see cref="Never" /> is what a ledger is built with: leaking is the
///         safe wrong answer, and a number nobody chose is not.
///     </para>
///     <para>
///         ⚠ <b>Which is why the only number a caller gives is the retry window, not the horizon.</b>
///         A retry window is a real quantity somebody knows — how long a client goes on resending an
///         unacknowledged claim, how long an operator's replay tool reaches back. A horizon is that
///         number times a margin, and letting it be written directly is letting somebody write one
///         <em>shorter</em> than the window it has to outlive, which is the one mistake this type
///         exists to make unrepresentable.
///     </para>
///     <para>
///         ⚠ <b><see cref="Guaranteed" /> is the number that matters, and it is not
///         <see cref="Length" />.</b> Forgetting happens a bucket at a time, so a key added just after
///         a rotation is dropped nearly a full <see cref="Length" /> later and one added just before
///         it is dropped one <see cref="Interval" /> sooner. The <em>worst</em> case is what a safety
///         argument is made of, and it is the one asserted against the retry window.
///     </para>
/// </remarks>
public readonly record struct KeyHorizon {
    /// <summary>How many generations a bounded horizon is swept in.</summary>
    /// <remarks>
    ///     The granularity of forgetting, and the reason <see cref="Guaranteed" /> is less than
    ///     <see cref="Length" />. More buckets is a tighter bound and a cheaper sweep — each one
    ///     drops a smaller share of the keys — at the cost of a longer list to walk when nothing is
    ///     due. Eight is the point where the worst case is already within an eighth of the nominal.
    /// </remarks>
    public const int Buckets = 8;

    /// <summary>How many retry windows the nominal horizon is.</summary>
    /// <remarks>
    ///     ⚠ <b>A margin over a stated window rather than a guess at one.</b> A realm's clock can
    ///     jump, a process can be paused by its host for a minute, and a client's "thirty seconds"
    ///     is thirty seconds of its own clock. Four windows means every one of those has to happen
    ///     at once before a key is dropped early, and the cost of the margin is memory.
    /// </remarks>
    public const int Windows = 4;

    KeyHorizon(TimeSpan retryWindow) => RetryWindow = retryWindow;

    /// <summary>Forget nothing. What a ledger has until somebody says otherwise.</summary>
    public static KeyHorizon Never => default;

    /// <summary>The longest retry this horizon undertakes to survive.</summary>
    public TimeSpan RetryWindow { get; }

    /// <summary>Whether anything is ever forgotten.</summary>
    public bool IsBounded => RetryWindow > TimeSpan.Zero;

    /// <summary>The nominal age at which a key stops being remembered.</summary>
    public TimeSpan Length => RetryWindow * Windows;

    /// <summary>How much of the horizon one generation covers.</summary>
    public TimeSpan Interval => Length / Buckets;

    /// <summary>The shortest time any key is remembered for. The number the safety argument is about.</summary>
    /// <remarks>
    ///     Always strictly greater than <see cref="RetryWindow" /> for a bounded horizon, which is
    ///     what makes "a retry within the window is free" a property rather than a hope.
    /// </remarks>
    public TimeSpan Guaranteed => Length - Interval;

    /// <summary>A horizon that outlives a stated retry window.</summary>
    /// <param name="retryWindow">The longest a caller may go on retrying one operation.</param>
    /// <returns>The horizon.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The window is not positive, or is so large that <see cref="Length" /> would not fit in a
    ///     <see cref="TimeSpan" />.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>Pass the window the <em>slowest</em> caller uses, not the common one.</b> The set
    ///     guards against every retry that reaches it, and the one that matters is the operator
    ///     re-running yesterday's failed settlements — not the client that gives up after five
    ///     seconds.
    /// </remarks>
    public static KeyHorizon Outliving(TimeSpan retryWindow) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retryWindow.Ticks, TimeSpan.MaxValue.Ticks / Windows);

        return new(retryWindow);
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsBounded
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"keys kept at least {Guaranteed} (retries up to {RetryWindow})"
            )
            : "keys kept for ever";
}
