// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Several commands being built into one undo entry.</summary>
/// <remarks>
///     <para>
///         Opened by <see cref="CommandStack.BeginTransaction" /> and committed by disposing it, so
///         the shape at the call site is a <see langword="using" /> block and forgetting to close one
///         is not a failure mode.
///     </para>
///     <para>
///         Distinct from merging. Merging is two commands discovering afterwards that they were one
///         edit; a transaction is a caller saying up front that everything inside it is.
///     </para>
/// </remarks>
public sealed class CommandTransaction : IDisposable {
    readonly CommandStack stack;
    bool closed;

    internal CommandTransaction(CommandStack stack) => this.stack = stack;

    /// <summary>Records everything collected as one entry.</summary>
    /// <remarks>
    ///     A transaction that collected nothing records nothing, so a "delete selection" that ran
    ///     against an empty selection leaves no entry to undo.
    /// </remarks>
    public void Commit() {
        if (closed) {
            return;
        }

        closed = true;
        stack.CommitTransaction();
    }

    /// <summary>Rolls back everything collected and records nothing.</summary>
    /// <remarks>
    ///     For the drag that ends on Escape rather than on a mouse-up. The rollback happens here
    ///     rather than at commit, so the model is back to where it started before this returns and
    ///     the viewport can be redrawn from it immediately.
    /// </remarks>
    public void Cancel() {
        if (closed) {
            return;
        }

        stack.CancelTransaction();
    }

    /// <inheritdoc />
    public void Dispose() => Commit();
}
