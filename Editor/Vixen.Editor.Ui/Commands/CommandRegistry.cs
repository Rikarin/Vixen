// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Ui;

/// <summary>Every command the editor knows, by id.</summary>
/// <remarks>
///     <para>
///         <b>One table, and everything that shows a command reads it.</b> That is what makes a new
///         action appear in the menu, the palette and the keymap editor at once, and it is why a
///         plugin adding a command is one <see cref="Add" /> call rather than an entry in four
///         places.
///     </para>
///     <para>
///         ⚠ <b>Registering an id twice throws.</b> A silent replace would let a plugin take over
///         <c>file.save</c> by naming it, and a silent ignore would leave the plugin's own command
///         quietly dead — both of which are found weeks later. The loader catches this and reports
///         which plugin collided with what.
///     </para>
///     <para>
///         Insertion order is kept, because it is the order a palette shows commands in when the
///         query is empty and there is nothing better to sort by. A ranked query reorders it.
///     </para>
/// </remarks>
public sealed class CommandRegistry {
    readonly Dictionary<string, EditorCommand> byId = new(StringComparer.Ordinal);
    readonly List<EditorCommand> ordered = [];

    /// <summary>The commands, in the order they were registered.</summary>
    public IReadOnlyList<EditorCommand> Commands => ordered;

    /// <summary>Which context has the focus, asked whenever a scoped command is looked at.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The shell sets this and nothing else does.</b> A context is a place the user can
    ///         be — a panel, a mode, a field — and only the thing that owns the focus knows which one
    ///         that is. Left unset, every command is in scope, which is the right answer for a
    ///         registry with no shell around it and for the majority of commands, which declare no
    ///         context at all.
    ///     </para>
    ///     <para>
    ///         Asked on demand rather than pushed, for the same reason
    ///         <see cref="EditorCommand.Enablement" /> is: a value pushed on focus change is one that
    ///         is right only if every path that moves the focus remembered to push it.
    ///     </para>
    /// </remarks>
    public Func<string?>? FocusedContext { get; set; }

    /// <summary>Whether a command belongs to the context the user is in.</summary>
    /// <param name="command">The command.</param>
    /// <returns>Whether it does. A command with no context always does.</returns>
    public bool IsInScope(EditorCommand command) {
        ArgumentNullException.ThrowIfNull(command);

        return command.Context is not { } context
            || string.Equals(context, FocusedContext?.Invoke(), StringComparison.Ordinal);
    }

    /// <summary>Raised when a command is added or removed.</summary>
    /// <remarks>What a palette listens to in order to forget a cached list, and what a menu built
    ///     from a model listens to in order to rebuild.</remarks>
    public event Action<CommandRegistry>? Changed;

    /// <summary>Raised after a command runs, whatever ran it.</summary>
    /// <remarks>
    ///     The palette's "recently used" list hangs off this, and so does the log line that makes a
    ///     bug report say what the user actually did.
    /// </remarks>
    public event Action<EditorCommand>? Executed;

    /// <summary>Adds a command.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The command, so a caller can keep hold of it.</returns>
    /// <exception cref="ArgumentException">Something is already registered under that id.</exception>
    public EditorCommand Add(EditorCommand command) {
        ArgumentNullException.ThrowIfNull(command);

        if (!byId.TryAdd(command.Id, command)) {
            throw new ArgumentException($"A command is already registered as '{command.Id}'.", nameof(command));
        }

        ordered.Add(command);
        Changed?.Invoke(this);

        return command;
    }

    /// <summary>Adds a command built from its parts.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">What it is called.</param>
    /// <param name="run">What it does.</param>
    /// <returns>The command.</returns>
    public EditorCommand Add(string id, StringId title, Action run) => Add(new EditorCommand(id, title, run));

    /// <summary>Takes a command out.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>What unloading a plugin does. The keymap keeps the binding — see
    ///     <see cref="KeyMap" /> — so reloading the plugin restores it.</remarks>
    public bool Remove(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (!byId.Remove(id, out var command)) {
            return false;
        }

        ordered.Remove(command);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>The command with an id, or <c>null</c>.</summary>
    /// <param name="id">The id.</param>
    public EditorCommand? this[string id] => byId.GetValueOrDefault(id);

    /// <summary>Looks a command up.</summary>
    /// <param name="id">The id.</param>
    /// <param name="command">The command, if there is one.</param>
    /// <returns>Whether there is.</returns>
    public bool TryGet(string id, [NotNullWhen(true)] out EditorCommand? command) {
        ArgumentNullException.ThrowIfNull(id);
        return byId.TryGetValue(id, out command);
    }

    /// <summary>Whether a command exists and can run right now.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it can.</returns>
    /// <remarks>An id nothing registered is <c>false</c> rather than a throw: a menu model outlives
    ///     the plugin whose commands it names, exactly as a saved layout outlives its panels.</remarks>
    public bool CanExecute(string id) => TryGet(id, out var command) && CanExecute(command);

    /// <summary>Whether a command is in scope and enabled.</summary>
    /// <param name="command">The command.</param>
    /// <returns>Whether it can run.</returns>
    /// <remarks>
    ///     The pair every view asks, together, because a menu that greyed a line out for enablement
    ///     and not for scope would offer the content browser's Delete while the outliner has the
    ///     focus — which is the confusion <see cref="EditorCommand.Context" /> exists to end.
    /// </remarks>
    public bool CanExecute(EditorCommand command) => IsInScope(command) && command.CanExecute;

    /// <summary>Runs a command.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it ran.</returns>
    /// <remarks>
    ///     ⚠ <b>A disabled command does not run, whoever asked.</b> The check is here rather than in
    ///     each view because a keybinding, a palette entry and a plugin calling <c>Execute</c> are
    ///     three ways to reach the same command and only two of them go past a greyed-out control.
    /// </remarks>
    public bool Execute(string id) {
        if (!TryGet(id, out var command) || !CanExecute(command)) {
            return false;
        }

        command.Run();
        Executed?.Invoke(command);

        return true;
    }
}
