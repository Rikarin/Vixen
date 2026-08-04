// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>
///     A blackboard several agents read, writable only inside a scope on one thread.
/// </summary>
/// <remarks>
///     <para>
///         <b>A distinct type rather than a flag, because sharing is the parallelism hazard and it
///         should be a decision somebody made.</b> A tree step is safe to run over chunks precisely
///         because it touches one agent's memory and one agent's board; a service writing a key that
///         another agent's decorator observes is a cross-agent edge, and an edge that arrived by
///         accident is a race nobody can see in a diff.
///     </para>
///     <para>
///         So the shared board is written in a single-threaded phase and read freely for the rest of
///         the frame. Opening a scope is what says "this is that phase", and a write outside one —
///         or from a worker while one is open elsewhere — throws rather than corrupting a value half
///         the population is about to read.
///     </para>
///     <para>
///         ⚠ <b>Reads are not gated, and that is the bargain.</b> Nothing here makes a read that
///         races an open scope safe; what it does is make the write phase a place in the frame
///         rather than a convention. Squad coordination, a shared threat list and a group's current
///         objective are what this is for, and doc 37 § Where it stops is explicit that the policy
///         over them is a game's.
///     </para>
/// </remarks>
public sealed class SharedBlackboard {
    /// <summary>Creates one over a layout.</summary>
    /// <param name="layout">Its shape.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    public SharedBlackboard(BlackboardLayout layout) {
        Values = new(layout) { WritesGated = true };
    }

    /// <summary>The board. Reads are free; writes need an open <see cref="Scope" />.</summary>
    public Blackboard Values { get; }

    /// <summary>Its shape.</summary>
    public BlackboardLayout Layout => Values.Layout;

    /// <summary>Whether a write scope is open.</summary>
    public bool IsWriting => Values.WritesOpen;

    /// <summary>Opens the write phase.</summary>
    /// <returns>A scope to dispose when the phase ends.</returns>
    /// <exception cref="InvalidOperationException">A scope is already open.</exception>
    public Scope BeginWrite() {
        if (Values.WritesOpen) {
            throw new InvalidOperationException(
                "A shared blackboard's write phase is already open. Two of them is the race this type exists to prevent."
            );
        }

        Values.WriterThread = Environment.CurrentManagedThreadId;
        Values.WritesOpen = true;

        return new(this);
    }

    /// <summary>The open write phase.</summary>
    /// <remarks>
    ///     A struct, so that opening a phase every frame costs nothing. It is only ever used as the
    ///     subject of a <c>using</c>, which is why it does not try to be safe against being copied.
    /// </remarks>
    /// <param name="board">The board it is open on.</param>
    public readonly struct Scope(SharedBlackboard board) : IDisposable {
        /// <summary>Closes the write phase.</summary>
        public void Dispose() {
            board.Values.WritesOpen = false;
            board.Values.WriterThread = -1;
        }
    }
}
