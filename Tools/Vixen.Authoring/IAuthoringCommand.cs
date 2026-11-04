// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Authoring;

/// <summary>One reversible change, in a form that needs no editor to run.</summary>
/// <remarks>
///     The editor's <c>IEditorCommand</c> is this with a richer context. A command that only ever
///     touches a document implements this directly, which is what lets a headless host run the same
///     graph and layer edits the editor's undo stack runs.
/// </remarks>
public interface IAuthoringCommand {
    /// <summary>What this is called in the undo history, as a person would say it.</summary>
    string Name { get; }

    /// <summary>Applies the change. Called on execute and again on every redo.</summary>
    /// <param name="context">Where the command records what it touched.</param>
    void Do(ICommandContext context);

    /// <summary>Puts back exactly what <see cref="Do" /> changed.</summary>
    /// <param name="context">Where the command records what it touched.</param>
    void Undo(ICommandContext context);

    /// <summary>Absorbs the entry below this one, so that the two become one undo step.</summary>
    /// <param name="previous">The newest entry already on the stack.</param>
    /// <param name="merged">The single command that replaces both.</param>
    /// <returns>Whether the two merged.</returns>
    bool TryMergeWith(IAuthoringCommand previous, [NotNullWhen(true)] out IAuthoringCommand? merged) {
        merged = null;
        return false;
    }
}
