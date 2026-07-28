// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>A command written as two lambdas.</summary>
/// <remarks>
///     For the one-off operation that does not deserve a type — and it never merges, because two
///     closures have no way to know whether they are the same edit twice.
/// </remarks>
public sealed class DelegateCommand : IEditorCommand {
    readonly Action<EditorContext> apply;
    readonly Action<EditorContext> revert;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Makes a command out of what it does and how to take it back.</summary>
    /// <param name="name">What the undo history calls it.</param>
    /// <param name="apply">What it does.</param>
    /// <param name="revert">How to take it back.</param>
    public DelegateCommand(string name, Action<EditorContext> apply, Action<EditorContext> revert) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(revert);

        Name = name;
        this.apply = apply;
        this.revert = revert;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) => apply(context);

    /// <inheritdoc />
    public void Undo(EditorContext context) => revert(context);
}
