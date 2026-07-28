// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Several commands that undo and redo as one.</summary>
/// <remarks>
///     Undone in reverse, which is not a stylistic choice: the later commands may depend on what the
///     earlier ones did — "create the entity" then "parent it" — and unwinding them in the order they
///     ran would try to unparent something that no longer exists.
/// </remarks>
public sealed class CompositeCommand : IEditorCommand {
    readonly IEditorCommand[] commands;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>What this is made of, in the order it runs.</summary>
    public IReadOnlyList<IEditorCommand> Commands => commands;

    /// <summary>Groups commands into one entry.</summary>
    /// <param name="name">What the entry is called.</param>
    /// <param name="commands">The commands, in the order they should run.</param>
    public CompositeCommand(string name, params IEditorCommand[] commands) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(commands);

        Name = name;
        this.commands = commands;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        for (var index = 0; index < commands.Length; index++) {
            commands[index].Do(context);
        }
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        for (var index = commands.Length - 1; index >= 0; index--) {
            commands[index].Undo(context);
        }
    }
}
