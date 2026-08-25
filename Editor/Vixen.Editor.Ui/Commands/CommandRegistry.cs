// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ui;

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
///     <para>
///         ⚠ <b>It is also the editor's <see cref="ICommandResponder" /></b> — the last link of
///         <see cref="CommandRoute" />'s chain, installed by <see cref="EditorShell" /> as its
///         document's <see cref="UiDocument.ApplicationCommandResponder" />. That is what makes a
///         <c>Vixen.Ui</c> control bound to <c>edit.rename</c> resolve, enable and run the editor's
///         command with no editor-specific wiring in the control.
///     </para>
///     <para>
///         <b>The interface rather than a mirror.</b> A <see cref="CommandResponder" /> filled in
///         alongside this table would be a second copy of the same map, wrong the first time a
///         plugin registered into one and not the other. The lookup already exists here; the
///         interface is three lines over it.
///     </para>
/// </remarks>
public sealed class CommandRegistry : ICommandResponder {
    readonly Dictionary<string, EditorCommand> byId = new(StringComparer.Ordinal);
    readonly List<EditorCommand> ordered = [];

    // ⚠ Built once per command at registration, not per lookup. `CommandHandler` is a struct so that
    // a persistently visible surface re-resolving twenty ids on the tick allocates nothing, and a
    // responder that closed over the command afresh on every call would hand that back two closures
    // at a time. Keyed the same way and removed together, because a handler for a command that is
    // gone is a menu line that runs a plugin which has unloaded.
    readonly Dictionary<string, CommandHandler> responders = new(StringComparer.Ordinal);

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

    /// <summary>How many things are listening to <see cref="Changed" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Internal, and it exists so that "the shell unsubscribed" is an assertion rather than
    ///     a comment.</b> An unsubscription is invisible from outside — the symptom of a missing one
    ///     is a subscriber that keeps working for a while and then acts on a disposed object — so
    ///     there is nothing else a test can look at. Not public: a count of listeners is not a fact
    ///     any caller should branch on.
    /// </remarks>
    internal int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;

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

        // ⚠ `Run(command)` and not `command.Run`, so that a caller reaching this through
        // `CommandRoute` goes past the same scope-and-enablement gate and raises the same `Executed`
        // as the palette, the menu and the keymap. One `Execute` that all the entry points go
        // through is the property this table is worth keeping for, and a fourth entry point that
        // bypassed it would be the one that broke it.
        responders.Add(
            command.Id,
            CommandHandler.For(command.Id, this, () => Run(command), () => CanExecute(command), isChecked: command.Checked)
        );

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
        responders.Remove(id);
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>The handler the command route gets for an id, if the editor knows it.</summary>
    /// <param name="id">The command id.</param>
    /// <param name="handler">Receives the handler.</param>
    /// <returns>Whether a command is registered under that id.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A command out of scope, or disabled, still answers here.</b> It returns
    ///         <c>true</c> with a handler whose predicate says no, which is what greys the item —
    ///         returning <c>false</c> would let the id fall out of the chain entirely, and there is
    ///         nothing after this to catch it.
    ///     </para>
    ///     <para>
    ///         <b>No title.</b> <see cref="EditorCommand.CurrentTitle" /> is a <c>StringId</c> and
    ///         the route's title is a string, so resolving one here would need a catalogue this
    ///         table does not have; <c>null</c> means "leave the surface's own label alone", which
    ///         is right for every editor command whose menu line is already written.
    ///         <c>MenuPresenter</c> is where a caption is resolved, and stays so.
    ///     </para>
    /// </remarks>
    public bool TryGetCommandHandler(string id, out CommandHandler handler) {
        ArgumentNullException.ThrowIfNull(id);
        return responders.TryGetValue(id, out handler);
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

        Run(command);
        return true;
    }

    /// <summary>Runs a command that has already been found able to, and announces it.</summary>
    /// <remarks>
    ///     The bottom of the single path <see cref="Execute" />'s remarks describe, factored out so
    ///     that the fourth entry point — <see cref="CommandRoute" />, through
    ///     <see cref="TryGetCommandHandler" /> — lands on it too rather than calling
    ///     <see cref="EditorCommand.Run" /> behind <see cref="Executed" />'s back.
    /// </remarks>
    void Run(EditorCommand command) {
        command.Run();
        Executed?.Invoke(command);
    }
}
