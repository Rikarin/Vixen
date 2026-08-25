// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui;

/// <summary>Something that answers command ids without being an element.</summary>
/// <remarks>
///     <para>
///         <b>The part of the chain that is not the tree.</b> AppKit's action chain does not stop at
///         the view hierarchy: past the last view it consults the window, the window controller, the
///         window's delegate, the document, <c>NSApp</c> and the application's delegate — and the
///         guide is explicit that a delegate gets its chance "even though a delegate isn't formally
///         in the responder chain". None of those are views. This is that, with the ceremony
///         removed: an object that maps an id to a handler, consulted after the element walk has
///         run out of parents.
///     </para>
///     <para>
///         ⚠ <b>It changes no rule, only the length of the walk.</b> The first responder that
///         answers still wins, and its <see cref="CommandHandler.CanExecute" /> is still the only
///         one asked — across the whole extended chain, exactly as inside the element walk. A
///         responder further along that would also have answered is not consulted, and is not asked
///         whether it could have.
///     </para>
///     <para>
///         ⚠ <b>No selector dispatch and no reflection</b> (ADR-002). An implementation says which
///         ids it answers by answering them; there is no <c>respondsToSelector:</c> here and none is
///         possible under trimming.
///     </para>
///     <para>
///         <see cref="CommandResponder" /> is the implementation almost everything wants — a table a
///         view-model or a document object fills in, owning no element. Implement this directly only
///         when the lookup already exists somewhere else and mirroring it would be a second copy to
///         keep in step; the editor's <c>CommandRegistry</c> is that case.
///     </para>
/// </remarks>
public interface ICommandResponder {
    /// <summary>The handler this responder has for a command id, if it has one.</summary>
    /// <param name="id">The command id.</param>
    /// <param name="handler">Receives the handler.</param>
    /// <returns>Whether this responder answers that id.</returns>
    /// <remarks>
    ///     ⚠ <b>Answering is not the same as being able to run.</b> Return <c>true</c> with a
    ///     handler whose predicate says no, rather than <c>false</c>: the difference is a greyed
    ///     item and an item that falls through to somebody further along, and only the first is what
    ///     "this verb is mine and I cannot do it right now" means.
    /// </remarks>
    bool TryGetCommandHandler(string id, out CommandHandler handler);
}

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
///         It names what it was found on, which is what makes a diagnostic overlay — and a
///         test — able to say <i>which</i> of two views answered rather than only that something
///         did. Exactly one of <see cref="Element" /> and <see cref="Responder" /> is set: the walk
///         reaches elements first and non-element responders afterwards, and a handler knows which
///         leg it came off.
///     </para>
/// </remarks>
public readonly struct CommandHandler : IEquatable<CommandHandler> {
    readonly Action execute;
    readonly Func<bool>? canExecute;
    readonly Func<string?>? title;
    readonly Func<bool>? isChecked;

    internal CommandHandler(
        string id,
        UiElement? element,
        ICommandResponder? responder,
        Action execute,
        Func<bool>? canExecute,
        Func<string?>? title,
        Func<bool>? isChecked
    ) {
        Id = id;
        Element = element;
        Responder = responder;

        this.execute = execute;
        this.canExecute = canExecute;
        this.title = title;
        this.isChecked = isChecked;
    }

    /// <summary>Builds a handler for a responder that is not an element.</summary>
    /// <param name="id">The command id.</param>
    /// <param name="responder">What is answering.</param>
    /// <param name="execute">What it does.</param>
    /// <param name="canExecute">Whether it can, asked whenever anything shows the command. Always, if omitted.</param>
    /// <param name="title">What to call it right now, or omitted to leave every surface's own label alone.</param>
    /// <param name="isChecked">Whether it is on, or omitted for a command that is not a toggle.</param>
    /// <returns>The handler, to be returned from <see cref="ICommandResponder.TryGetCommandHandler" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Build it once and keep it, rather than per lookup.</b> The struct exists so that a
    ///     toolbar re-resolving twenty ids on the tick allocates nothing; a responder that closes
    ///     over its state afresh on every call gives that back two closures at a time. A table built
    ///     at registration is both cheaper and the shape the lookup already has.
    /// </remarks>
    public static CommandHandler For(
        string id,
        ICommandResponder responder,
        Action execute,
        Func<bool>? canExecute = null,
        Func<string?>? title = null,
        Func<bool>? isChecked = null
    ) {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(execute);

        return new CommandHandler(id, null, responder, execute, canExecute, title, isChecked);
    }

    /// <summary>The command id this answers to.</summary>
    public string Id { get; }

    /// <summary>The element that declared it, or <c>null</c> when a non-element responder did.</summary>
    public UiElement? Element { get; }

    /// <summary>The responder that declared it, or <c>null</c> when an element did.</summary>
    /// <remarks>
    ///     The other half of <see cref="Element" />, and the reason that one became nullable: past
    ///     the root the chain is objects rather than views, and a handler that claimed an element it
    ///     did not have would make a diagnostic overlay lie about where a verb lives.
    /// </remarks>
    public ICommandResponder? Responder { get; }

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
        && ReferenceEquals(Responder, other.Responder)
        && Equals(execute, other.execute);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CommandHandler other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Element, Responder, execute);

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
    public override string ToString() => Element is { } element
        ? $"{Id} on <{element.Tag}>"
        : $"{Id} on {Responder?.GetType().Name ?? "nothing"}";
}

/// <summary>A table of command handlers, owned by something that is not an element.</summary>
/// <remarks>
///     <para>
///         <b>What a view-model, a document object or an application delegate uses.</b> Before this,
///         a handler had to hang on a <see cref="UiElement" />, so an object that owned
///         <c>edit.copy</c> had to own a piece of the view tree in order to say so — which is
///         backwards for the one kind of object whose whole job is not to be a view.
///     </para>
///     <para>
///         It is deliberately the same five arguments as
///         <see cref="UiElement.AddCommandHandler" />, with the same rule about a repeated id, so
///         that moving a handler between an element and a responder is a change of receiver and
///         nothing else.
///     </para>
///     <para>
///         ⚠ <b>It does not invalidate anything, because it does not know a document.</b> An element
///         registering a handler can raise <see cref="UiDocument.CommandsInvalidated" /> because it
///         is in a document; a responder may be installed on several or on none. The owner calls
///         <see cref="UiDocument.InvalidateCommands" /> after changing the table — installing the
///         responder in the first place already does.
///     </para>
/// </remarks>
public sealed class CommandResponder : ICommandResponder {
    readonly Dictionary<string, CommandHandler> handlers = new(StringComparer.Ordinal);

    /// <summary>Declares that this responder handles a command.</summary>
    /// <param name="id">The command id, as the keymap and the menu spell it.</param>
    /// <param name="execute">What it does.</param>
    /// <param name="canExecute">Whether it can, asked whenever anything shows the command. Always, if omitted.</param>
    /// <param name="title">What to call it right now. Omitted leaves every surface's own label alone.</param>
    /// <param name="isChecked">Whether it is on, for a toggle. Omitted means it shows no tick.</param>
    /// <exception cref="ArgumentException">This responder already handles that id.</exception>
    /// <remarks>
    ///     ⚠ <b>Registering the same id twice throws</b>, for
    ///     <see cref="UiElement.AddCommandHandler" />'s reason: a silent replace lets one
    ///     registration quietly take over another's verb, and a silent ignore leaves the second one
    ///     dead. Two <i>different</i> responders answering the same id is not a collision — the
    ///     chain picks the earlier one.
    /// </remarks>
    public void Add(
        string id,
        Action execute,
        Func<bool>? canExecute = null,
        Func<string?>? title = null,
        Func<bool>? isChecked = null
    ) {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(execute);

        if (handlers.ContainsKey(id)) {
            throw new ArgumentException($"This responder already handles '{id}'.", nameof(id));
        }

        handlers.Add(id, CommandHandler.For(id, this, execute, canExecute, title, isChecked));
    }

    /// <summary>Stops handling a command.</summary>
    /// <param name="id">The command id.</param>
    /// <returns>Whether this responder was handling it.</returns>
    public bool Remove(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return handlers.Remove(id);
    }

    /// <summary>Drops every handler.</summary>
    /// <remarks>
    ///     What an owner calls when it is torn down. The handlers are closures and a closure reaches
    ///     everything it captured, so a responder installed on a long-lived application and never
    ///     emptied is how a view-model outlives the view it belonged to.
    /// </remarks>
    public void Clear() => handlers.Clear();

    /// <summary>The ids this responder handles.</summary>
    /// <remarks>For a diagnostic overlay and for tests. Unordered, as the table is.</remarks>
    public IEnumerable<string> Ids => handlers.Keys;

    /// <inheritdoc />
    public bool TryGetCommandHandler(string id, out CommandHandler handler) {
        ArgumentNullException.ThrowIfNull(id);
        return handlers.TryGetValue(id, out handler);
    }
}

/// <summary>Where a command id goes: the focus, its ancestors, the root, the document, the application.</summary>
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
///     <para>
///         ⚠ <b>And it continues past the tree.</b> AppKit's action chain runs first responder →
///         views → window → window controller → delegate → document → <c>NSApp</c> → app delegate,
///         and most of that tail is not views at all. <see cref="ICommandResponder" /> is that tail:
///         <see cref="UiDocument.CommandResponder" /> and then
///         <see cref="UiDocument.ApplicationCommandResponder" />, asked in that order once the last
///         parent is gone. Every rule above holds across the join — first to answer wins, only that
///         one is asked whether it can, silence means disabled.
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
    ///         root" one loop rather than two cases. It is also why a command with a
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
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The walk does not stop at the root.</b> Past the last element it asks
    ///         <see cref="UiDocument.CommandResponder" /> and then
    ///         <see cref="UiDocument.ApplicationCommandResponder" />, in that order — AppKit's chain
    ///         continuing through the document to <c>NSApp</c> and its delegate once the view
    ///         hierarchy has run out. Both are usually <c>null</c>, and then this is exactly the
    ///         element walk it was before.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is document then application, and it is not a preference.</b> The
    ///         nearer thing to the user wins, all the way out: a leaf beats its panel, a panel beats
    ///         the root, the root beats the document, the document beats the application. An
    ///         application-wide fallback that could outrank the open document would make a verb mean
    ///         something different depending on what else happened to be registered.
    ///     </para>
    /// </remarks>
    public static CommandHandler? Resolve(UiDocument document, string id) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(id);

        for (var element = Origin(document); element is not null; element = element.Parent) {
            if (element.TryGetCommandHandler(id, out var handler)) {
                return handler;
            }
        }

        // ⚠ Written out rather than looped over an array of two, because the array would be an
        // allocation on the path a toolbar takes twenty times a frame — and because the order is the
        // rule, and a rule reads better as two lines than as the contents of a collection.
        if (document.CommandResponder is { } owner && owner.TryGetCommandHandler(id, out var owned)) {
            return owned;
        }

        if (document.ApplicationCommandResponder is { } application
            && application.TryGetCommandHandler(id, out var applied)) {
            return applied;
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

public sealed partial class UiDocument {
    bool commandsDirty;

    ICommandResponder? commandResponder;
    ICommandResponder? applicationCommandResponder;

    /// <summary>What answers a command id once the element walk has run out of parents.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The document's own responder — <c>NSDocument</c>'s place in the chain.</b> The
    ///         object that owns what the window is showing, which in most applications is not a view
    ///         and should not have to become one in order to own <c>edit.save</c>. Set it to a
    ///         <see cref="CommandResponder" /> and fill that in, or implement
    ///         <see cref="ICommandResponder" /> on the model object itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked after the root and before
    ///         <see cref="ApplicationCommandResponder" />.</b> Anything in the tree outranks it,
    ///         including a handler on the root, so a view that wants to override the document's verb
    ///         does so by declaring it and nothing else.
    ///     </para>
    ///     <para>
    ///         <b>The document holds it and nothing holds the document.</b> That direction is the
    ///         whole lifetime story: a responder never learns which documents it is installed on, so
    ///         a long-lived one cannot pin a window that has closed. The reference the other way is
    ///         released by <see cref="Dispose" />.
    ///     </para>
    /// </remarks>
    public ICommandResponder? CommandResponder {
        get => commandResponder;
        set {
            if (ReferenceEquals(commandResponder, value)) {
                return;
            }

            commandResponder = value;

            // A level appearing or vanishing can turn a greyed item live or grey a live one, which
            // is the same kind of change as a handler being registered on an element.
            InvalidateCommands();
        }
    }

    /// <summary>What answers a command id last of all, after the document's own responder.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The application's responder — <c>NSApp</c> and its delegate.</b> The verbs that
    ///         are true everywhere in the application and belong to no view and no open document:
    ///         Preferences, About, Quit, and the fallbacks a shell registers so that a menu line is
    ///         never dead merely because nothing is focused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Last, and it is not consulted at all when anything nearer answered</b> — not
    ///         even to ask whether it could have. A document responder that answers and then refuses
    ///         leaves the item greyed; the chain does not carry on looking for somebody more
    ///         willing, because "which handler runs" must not depend on how many things happen to be
    ///         listening.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the reference most likely to leak, and it points the safe way.</b> The
    ///         application object is the long-lived one and the document is not, so the document
    ///         holding the application is a short life pointing at a long one — the direction that
    ///         cannot pin anything. It is cleared by <see cref="Dispose" /> regardless, and a host
    ///         that installs a responder built over a plugin's objects clears the responder's own
    ///         table when the plugin unloads.
    ///     </para>
    /// </remarks>
    public ICommandResponder? ApplicationCommandResponder {
        get => applicationCommandResponder;
        set {
            if (ReferenceEquals(applicationCommandResponder, value)) {
                return;
            }

            applicationCommandResponder = value;
            InvalidateCommands();
        }
    }

    /// <summary>Drops both responders and the invalidation subscribers, so a closed document holds nothing it was lent.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called from <see cref="Dispose" />, and the point is the graph rather than the
    ///         three fields.</b> A responder is a table of closures and a closure reaches everything
    ///         it captured — a view-model, its selection, and in this editor's case an assembly in a
    ///         collectible load context. A disposed document that still pointed at one would keep
    ///         all of that reachable for as long as anything held the document, which is the shape
    ///         of every leak this repository has paid for.
    ///     </para>
    ///     <para>
    ///         <see cref="CommandsInvalidated" /> goes with them for the same reason and against the
    ///         opposite direction: its subscribers are controls, a control reaches its subtree, and
    ///         a host that keeps a disposed document in a field would otherwise keep the whole tree
    ///         that was hung off it.
    ///     </para>
    /// </remarks>
    void ReleaseCommandResponders() {
        commandResponder = null;
        applicationCommandResponder = null;
        CommandsInvalidated = null;
    }

    /// <summary>Raised at most once a frame when anything a command surface shows may have changed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>For the surfaces that are visible all the time.</b> A menu is asked as it opens and
    ///         needs nothing else; a toolbar, and Trinix's global menu bar — which has to <i>push</i>
    ///         an update over a Wayland protocol at the moment an item greys — are on screen
    ///         continuously and have no such moment. The alternative they had was polling every
    ///         command on the tick, which is what <c>EditorShell.Tick</c> does today.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Coalesced to one raise per frame, and that is the entire reason it exists.</b> A
    ///         command's answer can be changed by a focus change, by a registration, and by anything
    ///         at all through <see cref="InvalidateCommands" /> — and a load that registers fifty
    ///         handlers would otherwise re-ask forty menu items fifty times for one answer. The flag
    ///         is set as many times as anybody likes and read once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Raised from <see cref="Tick" /> rather than from <see cref="Update" />, because
    ///         <c>Update</c> is allowed not to happen.</b> A frame in which nothing dirtied the
    ///         document returns early without running a pass, and a command becoming executable is
    ///         not a thing that dirties one — so a surface hung on the pass would go stale for
    ///         exactly as long as the interface was still. <c>Tick</c> is the call a host must make
    ///         every frame whether anything happened or not, which is the guarantee this needs.
    ///     </para>
    ///     <para>
    ///         Subscribe in <c>OnCreated</c> and unsubscribe in <c>OnRemoved</c>, as
    ///         <see cref="Ticked" />'s remarks say.
    ///     </para>
    /// </remarks>
    public event Action<UiDocument>? CommandsInvalidated;

    /// <summary>Says that a command's enablement, name or check state may have changed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The third source, and the one an application uses.</b> The focus moving and a
    ///         handler being registered are noticed here; a <i>selection</i> changing is not, and
    ///         cannot be — the predicate that reads it is an arbitrary closure and this framework has
    ///         no way to know what it looked at. So the view that changed the selection says so, in
    ///         one line, and every surface showing any of its commands follows.
    ///     </para>
    ///     <para>
    ///         Free to call as often as you like: it sets a flag. Calling it a hundred times in a
    ///         frame raises <see cref="CommandsInvalidated" /> once.
    ///     </para>
    /// </remarks>
    public void InvalidateCommands() => commandsDirty = true;

    /// <summary>Raises the coalesced invalidation, if anything asked for one since the last frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The flag is cleared before the handlers run, not after.</b> A handler is entitled to
    ///     invalidate again — a toolbar that renames a button can legitimately change what a
    ///     predicate reads — and clearing afterwards would swallow that, leaving the interface a
    ///     frame stale with no way to notice. Clearing first means the second ask is honoured on the
    ///     next frame, which is one raise per frame in either order.
    /// </remarks>
    void RaiseCommandsInvalidated() {
        if (!commandsDirty) {
            return;
        }

        commandsDirty = false;
        CommandsInvalidated?.Invoke(this);
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

        // A new responder can turn a greyed item live, and a view that declares eight handlers as it
        // is built asks for one raise between them.
        Document.InvalidateCommands();
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

                // The other half of registration: a responder going away can grey an item that was
                // live, and is the same kind of change for the same surfaces.
                Document.InvalidateCommands();

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
                        null,
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
