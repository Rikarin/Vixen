// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui;

/// <summary>The handler a command id resolved to, and the two things a caller can do with it.</summary>
/// <remarks>
///     <para>
///         <b>A struct, because a persistently visible surface asks per item per frame.</b> A
///         toolbar of twenty buttons re-resolving on the tick is twenty of these; as a class that
///         would be twenty allocations a frame for a question whose answer is usually "the same
///         one as last time". <see cref="CommandRoute.Resolve" /> hands one back by value and
///         <c>null</c> costs nothing either.
///     </para>
///     <para>
///         It names the element it was found on, which is what makes a diagnostic overlay — and a
///         test — able to say <i>which</i> of two views answered rather than only that something
///         did.
///     </para>
/// </remarks>
public readonly struct CommandHandler : IEquatable<CommandHandler> {
    readonly Action execute;
    readonly Func<bool>? canExecute;
    readonly Func<string?>? title;
    readonly Func<bool>? isChecked;

    internal CommandHandler(
        string id,
        UiElement element,
        Action execute,
        Func<bool>? canExecute,
        Func<string?>? title,
        Func<bool>? isChecked
    ) {
        Id = id;
        Element = element;

        this.execute = execute;
        this.canExecute = canExecute;
        this.title = title;
        this.isChecked = isChecked;
    }

    /// <summary>The command id this answers to.</summary>
    public string Id { get; }

    /// <summary>The element that declared it.</summary>
    public UiElement Element { get; }

    /// <summary>Whether it can run right now.</summary>
    /// <remarks>
    ///     A handler registered without a predicate is always able to run, which is what makes
    ///     "declare a handler" the whole of the common case. The predicate is asked every time
    ///     rather than cached, for the reason <c>CommandRegistry.FocusedContext</c>'s remarks give:
    ///     a value pushed when it changes is right only if every path that changes it remembered.
    /// </remarks>
    public bool CanExecute => canExecute?.Invoke() ?? true;

    /// <summary>What it should be called right now, or <c>null</c> if it does not rename itself.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The handler supplies it, not the command</b>, and that is forced by the same
    ///         thing that makes the route worth having: there is no command object here to hang a
    ///         caption on, only whichever element answered. "Undo Move" is a fact about the view
    ///         that owns the undo stack, and the view that answers <c>edit.undo</c> is that view.
    ///     </para>
    ///     <para>
    ///         <c>null</c> — which is what a handler that passed no title reports — means "leave the
    ///         label alone". A surface must not read it as "no name": the overwhelming majority of
    ///         commands are named once where the menu is written, and a binding that blanked them
    ///         would empty every line in the menu.
    ///     </para>
    /// </remarks>
    public string? Title => title?.Invoke();

    /// <summary>Whether this command is a toggle at all, without asking what it is set to.</summary>
    /// <remarks>
    ///     ⚠ <b>The question <see cref="IsChecked" /> cannot answer</b>, and a surface needs it before
    ///     it needs the state: a tick shown as "off" and a command that has no tick look identical
    ///     from a <c>bool</c> and are drawn differently — the first reserves the gutter, the second
    ///     must not, or every ordinary menu is indented by a column of nothing.
    /// </remarks>
    public bool IsCheckable => isChecked is not null;

    /// <summary>Whether a checkable command is currently on. <c>false</c> for one that is not checkable.</summary>
    public bool IsChecked => isChecked?.Invoke() ?? false;

    /// <summary>Runs it, whether or not it said it could.</summary>
    /// <remarks>
    ///     ⚠ <b>No enablement check here, and that is deliberate.</b> Every caller that reaches a
    ///     command through the route goes through <see cref="CommandRoute.Execute" />, which asks
    ///     first; this is the raw call underneath, for a host that has already asked and for a test
    ///     that wants to prove the predicate is what refused rather than the handler being absent.
    /// </remarks>
    public void Run() => execute();

    /// <inheritdoc />
    public bool Equals(CommandHandler other) =>
        string.Equals(Id, other.Id, StringComparison.Ordinal)
        && ReferenceEquals(Element, other.Element)
        && execute.Equals(other.execute);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CommandHandler other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Element, execute);

    /// <summary>Whether two handlers are the same registration.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are.</returns>
    public static bool operator ==(CommandHandler left, CommandHandler right) => left.Equals(right);

    /// <summary>Whether two handlers are different registrations.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are.</returns>
    public static bool operator !=(CommandHandler left, CommandHandler right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => $"{Id} on <{Element.Tag}>";
}

/// <summary>Where a command id goes: the focused element, then its ancestors, then the root.</summary>
/// <remarks>
///     <para>
///         <b>The responder chain, and it is derived rather than pushed.</b> A menu declares
///         <i>what</i> — an id — and the focus decides <i>who</i>. Two views can each declare a
///         handler for <c>edit.copy</c> without either knowing the other exists, and focusing one
///         or the other is the whole of the wiring.
///     </para>
///     <para>
///         ⚠ <b>The first handler that responds wins, and its <c>canExecute</c> is the only one
///         asked.</b> A second responder further up that would also have handled the id is not
///         consulted — not even to break a tie when the first one says no. Consulting it would make
///         "which handler runs" depend on how many things happen to be listening, which is a
///         behaviour that changes when an unrelated panel is added.
///     </para>
///     <para>
///         ⚠ <b>Nobody responds ⇒ not executable.</b> That is the affordance a hand-written
///         enablement rule cannot express: an application declares a menu of ids and the items grey
///         themselves out wherever nothing in the chain answers, with no rule written anywhere.
///     </para>
///     <para>
///         The walk is the same one <c>CommandDispatcher.Available</c> already does looking for a
///         <c>TextField</c> — <see cref="UiDocument.Focused" /> to <see cref="UiElement.Parent" />
///         — so the mechanism is the tree Vixen already owns and there is no selector dispatch and
///         no reflection anywhere in it (ADR-002).
///     </para>
/// </remarks>
public static class CommandRoute {
    /// <summary>Where the walk starts.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The focus commands resolve from, or the root when there is none.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The root rather than nothing, so a document-wide handler still answers while the
    ///         focus is nowhere.</b> With something focused the root is on the walk anyway, being
    ///         every element's last ancestor; falling back to it is what makes "focused → parents →
    ///         document" one loop rather than two cases. It is also why a command with a
    ///         registration-time implementation — a handler on the root — always responds, and
    ///         nothing changes for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="UiDocument.CommandFocus" /> and not <see cref="UiDocument.Focused" />,
    ///         because the surfaces that <i>display</i> commands take the focus in order to be
    ///         operable.</b> An open menu focuses its first item so the arrow keys work, and a menu
    ///         bar's name takes the focus when it is pressed — so a menu item resolving from
    ///         <c>Focused</c> resolves from <i>itself</i>, and the view whose verb it was showing is
    ///         no longer on the walk at all. That is AppKit's rule stated as data rather than as an
    ///         event loop: a menu is not in the responder chain. See
    ///         <see cref="UiElement.IsCommandTransparent" />.
    ///     </para>
    /// </remarks>
    public static UiElement Origin(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return document.CommandFocus ?? document.Root;
    }

    /// <summary>The scope the focus is in.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The nearest declared scope at or above the focus, or <c>null</c> if none declares one.</returns>
    /// <remarks>
    ///     <b>Derived, which is what makes forgetting to push it impossible.</b> A panel declares
    ///     <see cref="UiElement.CommandScope" /> once on its own root and every control inside it
    ///     is in that scope, including ones written later and ones written by a plugin.
    /// </remarks>
    public static string? ScopeOf(UiDocument document) => Origin(document).EffectiveCommandScope;

    /// <summary>Finds the handler a command id resolves to.</summary>
    /// <param name="document">The document whose focus decides.</param>
    /// <param name="id">The command id.</param>
    /// <returns>The first handler along the route, or <c>null</c> if nothing responds.</returns>
    public static CommandHandler? Resolve(UiDocument document, string id) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(id);

        for (var element = Origin(document); element is not null; element = element.Parent) {
            if (element.TryGetCommandHandler(id, out var handler)) {
                return handler;
            }
        }

        return null;
    }

    /// <summary>Whether a command can run right now.</summary>
    /// <param name="document">The document.</param>
    /// <param name="id">The command id.</param>
    /// <returns>Whether something responds and says it can.</returns>
    public static bool CanExecute(UiDocument document, string id) =>
        Resolve(document, id) is { } handler && handler.CanExecute;

    /// <summary>Runs a command through the route.</summary>
    /// <param name="document">The document.</param>
    /// <param name="id">The command id.</param>
    /// <returns>Whether it ran.</returns>
    /// <remarks>
    ///     ⚠ <b>A handler that says it cannot run does not run, whoever asked.</b> The check is
    ///     here rather than in each surface because a keystroke, a menu item and a plugin calling
    ///     this are three ways to reach the same handler and only one of them goes past a greyed
    ///     control.
    /// </remarks>
    public static bool Execute(UiDocument document, string id) {
        if (Resolve(document, id) is not { } handler || !handler.CanExecute) {
            return false;
        }

        handler.Run();
        return true;
    }
}

public partial class UiElement {
    // ⚠ One nullable reference, and both command features live behind it. A UI tree is 10⁴
    // elements and almost none of them are command responders, so the cost of the feature on an
    // element that never uses it is eight bytes and no allocation at all — the same bargain
    // `handlers` above makes. Splitting it into a `string? scope` plus a `List<…>? handlers` would
    // have been two fields for the same reach.
    CommandBindings? commands;

    /// <summary>The command scope this element declares, or <c>null</c> if it declares none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Declared once at a panel's root, and inherited by everything inside it.</b> Read
    ///         <see cref="EffectiveCommandScope" /> for the answer including inheritance; this
    ///         property is only what this element itself says, so that clearing it back to
    ///         <c>null</c> means "take my parent's" rather than "no scope".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a <c>[UiProperty]</c>, and not in the cascade.</b> An inheriting UI property
    ///         would cost every element in the document a value field <i>and</i> an is-set flag, and
    ///         would put the scope where a stylesheet could set it — which would make "which panel
    ///         am I in" a thing a theme could change.
    ///     </para>
    /// </remarks>
    public string? CommandScope {
        get => commands?.Scope;
        set {
            if (value is null && commands is null) {
                return;
            }

            (commands ??= new CommandBindings()).Scope = value;
        }
    }

    /// <summary>Whether the focus landing here should leave the command route pointing where it was.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>"This is not a place."</b> A menu, a menu bar and a command palette are surfaces
    ///         that <i>show</i> commands, and every one of them has to take the focus to be usable —
    ///         a menu focuses its first item so the arrows work, a bar's name focuses when pressed.
    ///         Without this, a menu item asking the route which view handles <c>edit.copy</c> gets
    ///         the answer "the menu item", because by the time the menu is on screen the view it was
    ///         opened over is no longer focused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does not make anything unfocusable, and that is the whole reason it is a
    ///         second flag rather than <see cref="Focusable" />.</b> Tab still reaches it, the ring
    ///         still shows, arrow keys still move between menu items. The only thing that changes is
    ///         what <see cref="CommandRoute.Origin" /> reads afterwards.
    ///     </para>
    ///     <para>
    ///         <b>Inherited downwards</b>, on <see cref="CommandScope" />'s terms and for its
    ///         reasons: a menu declares it once and every item, label and icon inside it is covered,
    ///         including ones a plugin adds later.
    ///     </para>
    /// </remarks>
    public bool IsCommandTransparent {
        get => commands?.Transparent ?? false;
        set {
            if (!value && commands is null) {
                return;
            }

            (commands ??= new CommandBindings()).Transparent = value;
        }
    }

    /// <summary>Whether this element is inside anything that declares itself command-transparent.</summary>
    /// <remarks>Asked once per focus change, which is what makes a walk the right shape for it.</remarks>
    public bool IsInCommandTransparentSubtree {
        get {
            for (var element = this; element is not null; element = element.Parent) {
                if (element.commands?.Transparent == true) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The scope in force here: this element's, or the nearest ancestor's that declares one.</summary>
    public string? EffectiveCommandScope {
        get {
            for (var element = this; element is not null; element = element.Parent) {
                if (element.commands?.Scope is { } scope) {
                    return scope;
                }
            }

            return null;
        }
    }

    /// <summary>Declares that this element handles a command.</summary>
    /// <param name="id">The command id, as the keymap and the menu spell it.</param>
    /// <param name="execute">What it does.</param>
    /// <param name="canExecute">Whether it can, asked whenever anything shows the command. Always, if omitted.</param>
    /// <param name="title">What to call it right now, for the handful of commands whose name is their state. Omitted leaves every surface's own label alone.</param>
    /// <param name="isChecked">Whether it is on, for a command that is a toggle. Omitted means it is not a toggle and shows no tick.</param>
    /// <exception cref="ArgumentException">This element already handles that id.</exception>
    /// <remarks>
    ///     ⚠ <b>Registering the same id twice on one element throws</b>, for
    ///     <c>CommandRegistry.Add</c>'s reason: a silent replace lets one control quietly take over
    ///     another's verb, and a silent ignore leaves the second registration dead. Two
    ///     <i>different</i> elements handling the same id is the whole point and is not a collision
    ///     — the route picks the nearer one.
    /// </remarks>
    public void AddCommandHandler(
        string id,
        Action execute,
        Func<bool>? canExecute = null,
        Func<string?>? title = null,
        Func<bool>? isChecked = null
    ) {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(execute);

        var bindings = commands ??= new CommandBindings();

        foreach (var existing in bindings.Handlers) {
            if (string.Equals(existing.Id, id, StringComparison.Ordinal)) {
                throw new ArgumentException($"This element already handles '{id}'.", nameof(id));
            }
        }

        bindings.Handlers.Add(new CommandRegistration(id, execute, canExecute, title, isChecked));
    }

    /// <summary>Stops handling a command.</summary>
    /// <param name="id">The command id.</param>
    /// <returns>Whether this element was handling it.</returns>
    public bool RemoveCommandHandler(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (commands is null) {
            return false;
        }

        var handlers = commands.Handlers;

        for (var i = 0; i < handlers.Count; i++) {
            if (string.Equals(handlers[i].Id, id, StringComparison.Ordinal)) {
                handlers.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>The handler this element declared for a command id, if it declared one.</summary>
    /// <param name="id">The command id.</param>
    /// <param name="handler">Receives the handler.</param>
    /// <returns>Whether there is one.</returns>
    /// <remarks>This element only, and no walk. <see cref="CommandRoute.Resolve" /> is the walk.</remarks>
    public bool TryGetCommandHandler(string id, out CommandHandler handler) {
        ArgumentNullException.ThrowIfNull(id);

        if (commands is not null) {
            foreach (var registration in commands.Handlers) {
                if (string.Equals(registration.Id, id, StringComparison.Ordinal)) {
                    handler = new CommandHandler(
                        id,
                        this,
                        registration.Execute,
                        registration.CanExecute,
                        registration.Title,
                        registration.IsChecked
                    );
                    return true;
                }
            }
        }

        handler = default;
        return false;
    }

    /// <summary>The ids this element handles, in the order they were declared.</summary>
    /// <remarks>For a diagnostic overlay and for tests. Empty for the overwhelming majority of elements.</remarks>
    public IEnumerable<string> CommandHandlerIds {
        get {
            if (commands is null) {
                yield break;
            }

            foreach (var registration in commands.Handlers) {
                yield return registration.Id;
            }
        }
    }

    readonly record struct CommandRegistration(
        string Id,
        Action Execute,
        Func<bool>? CanExecute,
        Func<string?>? Title,
        Func<bool>? IsChecked
    );

    // A list rather than a dictionary: an element declares a handful of ids at most, and a linear
    // scan over four strings beats hashing one. Allocated only for elements that take part.
    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated through UiElement.commands.")]
    sealed class CommandBindings {
        public string? Scope { get; set; }

        public bool Transparent { get; set; }

        public List<CommandRegistration> Handlers { get; } = [];
    }
}
