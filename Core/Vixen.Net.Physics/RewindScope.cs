// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Physics;

/// <summary>A world that is in the past, and the promise that it will not stay there.</summary>
/// <remarks>
///     <para>
///         <b>The restore is the important half, and it is the half that is easy to lose.</b> A
///         rewound query is a handful of lines — move the bodies, cast, read the answer — and every
///         one of those lines can throw, return early, or grow a branch six months later that forgets
///         to put the world back. A world left in the past does not fail: it simulates, replicates
///         and looks entirely normal, with every player standing where they were a fifth of a second
///         ago, for ever. Making the restore a <c>using</c> is what turns that from a thing you must
///         remember into a thing the compiler does.
///     </para>
///     <para>
///         <c>using var rewind = compensator.RewindFor(claim.Tick, player.RoundTrip.RoundTrip);</c>
///     </para>
///     <para>
///         Disposing twice is harmless, and disposing a scope whose compensator has already been
///         restored some other way is harmless too. Both are what a <c>finally</c> does on a path
///         that has already unwound.
///     </para>
/// </remarks>
public readonly struct RewindScope : IDisposable, IEquatable<RewindScope> {
    readonly LagCompensator? compensator;

    internal RewindScope(LagCompensator compensator, Tick at, int bodyCount) {
        this.compensator = compensator;
        At = at;
        BodyCount = bodyCount;
    }

    /// <summary>The tick the world was moved to.</summary>
    /// <remarks>
    ///     Worth logging beside a hit. It is what the claim was allowed to mean rather than what it
    ///     asked for, so a disputed kill is answered by comparing this against the claim.
    /// </remarks>
    public Tick At { get; }

    /// <summary>How many bodies were actually moved.</summary>
    /// <remarks>
    ///     Fewer than the tracked count when a body has no history reaching that far back — one that
    ///     joined a moment ago, most often. Zero means the rewind changed nothing and the query is
    ///     running against the present, which is worth noticing rather than reading as a clean miss.
    /// </remarks>
    public int BodyCount { get; }

    /// <summary>Puts every body back where the simulation left it.</summary>
    public void Dispose() => compensator?.Restore();

    /// <inheritdoc />
    public bool Equals(RewindScope other) =>
        ReferenceEquals(compensator, other.compensator) && At == other.At && BodyCount == other.BodyCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RewindScope other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(compensator, At, BodyCount);

    /// <summary>Whether two scopes are the same one.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are.</returns>
    public static bool operator ==(RewindScope left, RewindScope right) => left.Equals(right);

    /// <summary>Whether two scopes are different.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are.</returns>
    public static bool operator !=(RewindScope left, RewindScope right) => !left.Equals(right);
}
