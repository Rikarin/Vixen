// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Core;

/// <summary>One reversible change to something the editor edits.</summary>
/// <remarks>
///     <para>
///         <b>Undo is a command stack, not a snapshot diff.</b> Snapshots are simpler and they cannot
///         represent "renamed this asset, which updated four hundred references" as one step, because
///         the four hundred documents were never opened. A command can: it is the operation, so it
///         knows what it touched.
///     </para>
///     <para>
///         <b>Everything the editor edits goes through this one vocabulary</b>, which is what makes
///         undo/redo, dirty tracking and the remote inspector work uniformly rather than once per
///         feature. A drawer that mutates a value behind the stack's back is the bug this interface
///         exists to make obvious.
///     </para>
/// </remarks>
public interface IEditorCommand {
    /// <summary>What this is called in the undo history, as a person would say it.</summary>
    /// <remarks>
    ///     Shown in a menu item — "Undo Set Roughness" — so it reads best as a verb phrase in the
    ///     imperative, and it is not localised here: the shell's string table is what turns an id
    ///     into a language.
    /// </remarks>
    string Name { get; }

    /// <summary>Applies the change.</summary>
    /// <param name="context">The project, the selection, and the document this is running in.</param>
    /// <remarks>
    ///     Called once when the command is executed and again on every redo, so it has to be
    ///     repeatable rather than merely correct the first time.
    /// </remarks>
    void Do(EditorContext context);

    /// <summary>Puts back exactly what <see cref="Do" /> changed.</summary>
    /// <param name="context">The project, the selection, and the document this is running in.</param>
    void Undo(EditorContext context);

    /// <summary>Absorbs the entry below this one, so that the two become one undo step.</summary>
    /// <param name="previous">The newest entry already on the stack.</param>
    /// <param name="merged">The single command that replaces both.</param>
    /// <returns>Whether the two merged.</returns>
    /// <remarks>
    ///     <para>
    ///         This is what makes a slider drag one undo step rather than three hundred. The receiver
    ///         is the <i>new</i> command and it is being asked whether it can swallow the old one, so
    ///         the merged command must undo to what <paramref name="previous" /> would have undone to
    ///         — the value before the drag started, not the value one mouse-move ago.
    ///     </para>
    ///     <para>
    ///         The default is not to merge. Merging is a claim about two operations being one edit,
    ///         and a command type that has not thought about it should not be making that claim.
    ///     </para>
    /// </remarks>
    bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }
}
