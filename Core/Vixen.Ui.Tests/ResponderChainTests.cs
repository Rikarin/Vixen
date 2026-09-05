// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The middle of the walk, and the two legs that could not reach a responder at all.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Vixen had three chains where AppKit has one.</b> Routed events walked elements,
///         commands walked elements plus two document slots, and the editor's keymap did not walk at
///         all. The two <see cref="IResponder" /> slots on <see cref="UiDocument" /> are the command
///         chain's two <i>ends</i>, so the three objects AppKit puts in the middle — a view
///         controller, a window controller, a document — had nowhere to sit, and a non-element
///         responder could never see a <see cref="KeyEvent" /> at all because
///         <c>EventRouter.Raise</c> is <see cref="UiElement" />-typed end to end.
///     </para>
///     <para>
///         ⚠ <b>Every rule here is a rule the element walk already had.</b> That is what these tests
///         are for: appending a link must not have bought a second set of rules for the new
///         position. Nearest answers first, only that one is asked whether it can, and the walk
///         stops there.
///     </para>
/// </remarks>
public class ResponderChainTests {
    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;

        return element;
    }

    /// <summary>A responder that answers one id, one key, and optionally owns an undo manager.</summary>
    sealed class Controller : IResponder {
        readonly string id;
        readonly Action execute;
        readonly InputKey? key;

        public Controller(string id, Action execute, InputKey? key = null) {
            this.id = id;
            this.execute = execute;
            this.key = key;
        }

        public int Lookups { get; private set; }

        public int Keys { get; private set; }

        public IUndoManager? UndoManager { get; init; }

        public bool TryGetCommandHandler(string commandId, out CommandHandler handler) {
            Lookups++;

            if (!string.Equals(commandId, id, StringComparison.Ordinal)) {
                handler = default;

                return false;
            }

            handler = CommandHandler.For(commandId, this, execute);

            return true;
        }

        public bool OnKey(KeyEvent args) {
            Keys++;

            return key is { } wanted && args.Key == wanted;
        }
    }

    static KeyEvent Pressed(InputKey key) => new() { Key = key, Action = KeyAction.Pressed };

    // ── The command leg ─────────────────────────────────────────────────────

    [Fact]
    public void A_responder_appended_to_an_element_answers_from_that_element_s_position() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var panelController = new Controller("edit.copy", () => ran = "panel-controller");
        var applicationResponder = new Controller("edit.copy", () => ran = "application");

        var panel = View(document.Root);
        var leaf = View(panel);

        panel.AddResponder(panelController);
        document.ApplicationCommandResponder = applicationResponder;
        document.Focus(leaf);

        // ⚠ The claim the two document slots could not make: the controller answers from *inside*
        // the walk, not from the end of it. Before `Responders` the only place to put this object
        // was `UiDocument.CommandResponder`, where it would have outranked nothing and been
        // outranked by everything in the tree.
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("panel-controller", ran);
        Assert.Equal(0, applicationResponder.Lookups);
    }

    [Fact]
    public void An_element_s_own_handler_outranks_the_responder_it_appended() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var controller = new Controller("edit.copy", () => ran = "controller");

        var panel = View(document.Root);
        panel.AddCommandHandler("edit.copy", () => ran = "panel");
        panel.AddResponder(controller);

        document.Focus(panel);

        // A responder is a link appended *behind* the element, not one it delegates to. The element
        // is nearer to the user than anything it appended, which is the same rule that makes a leaf
        // beat its panel.
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("panel", ran);
        Assert.Equal(0, controller.Lookups);
    }

    [Fact]
    public void A_nearer_element_s_responder_beats_a_further_element_s() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        var outer = new Controller("edit.copy", () => ran = "outer");
        var inner = new Controller("edit.copy", () => ran = "inner");

        var panel = View(document.Root);
        var leaf = View(panel);

        panel.AddResponder(outer);
        leaf.AddResponder(inner);
        document.Focus(leaf);

        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("inner", ran);

        // Counted rather than observed, for `CommandResponderTests`' reason: "the inner one won" is
        // also true of a walk that asked the outer one and preferred the inner anyway, and nought
        // lookups is the claim the rule actually makes.
        Assert.Equal(0, outer.Lookups);
    }

    [Fact]
    public void Appending_the_same_responder_twice_throws_rather_than_being_asked_twice() {
        using var document = new UiDocument(100f, 100f);

        var panel = View(document.Root);
        var controller = new Controller("edit.copy", () => { });

        panel.AddResponder(controller);
        Assert.Throws<ArgumentException>(() => panel.AddResponder(controller));

        Assert.Single(panel.Responders);
        Assert.True(panel.RemoveResponder(controller));
        Assert.Empty(panel.Responders);
        Assert.False(panel.RemoveResponder(controller));
    }

    [Fact]
    public void An_element_with_no_responders_allocates_none() {
        using var document = new UiDocument(100f, 100f);

        // The empty case is the overwhelming majority — a UI tree is 10⁴ elements and almost none
        // take part — so it must be a shared empty list rather than one per element.
        Assert.Empty(document.Root.Responders);
        Assert.Same(document.Root.Responders, View(document.Root).Responders);
    }

    // ── The keyboard leg ────────────────────────────────────────────────────

    [Fact]
    public void A_key_reaches_a_responder_that_is_not_an_element() {
        using var document = new UiDocument(100f, 100f);

        var controller = new Controller("edit.copy", () => { }, InputKey.F5);

        var panel = View(document.Root);
        var leaf = View(panel);

        panel.AddResponder(controller);
        document.Focus(leaf);

        // ⚠ Structurally impossible before this. `EventRouter.Raise` builds a `List<UiElement>` and
        // hands the event down and up it, so there was no point on the route where an object that
        // is not an element could be offered a key — a view controller could answer `edit.copy` and
        // still never see the F5 that means it.
        var args = Pressed(InputKey.F5);
        document.Dispatch(args);

        Assert.True(args.Handled);
        Assert.Equal(1, controller.Keys);
    }

    [Fact]
    public void A_focused_element_still_takes_the_key_first() {
        using var document = new UiDocument(100f, 100f);

        var controller = new Controller("edit.copy", () => { }, InputKey.F5);

        var panel = View(document.Root);
        var leaf = View(panel);

        panel.AddResponder(controller);
        leaf.AddHandler<KeyEvent>((_, args) => args.Handled = true);
        document.Focus(leaf);

        document.Dispatch(Pressed(InputKey.F5));

        // ⚠ The one deliberate divergence from AppKit, asserted rather than left to a comment: the
        // responder walk runs after the bubble leg, so a focused control beats an appended
        // responder. `performKeyEquivalent:` goes the other way and Vixen does not copy it — a
        // global chord must not take a key out of the box somebody is typing in.
        Assert.Equal(0, controller.Keys);
    }

    [Fact]
    public void A_responder_that_declines_the_key_leaves_the_fallbacks_their_turn() {
        using var document = new UiDocument(100f, 100f);

        // Answers no key at all, so every offer comes back false.
        var controller = new Controller("edit.copy", () => { });

        var first = View(document.Root);
        var second = View(document.Root);

        document.Root.AddResponder(controller);
        document.Focus(first);

        var args = Pressed(InputKey.Tab);
        document.Dispatch(args);

        // It was asked — once, on the walk from the focus to the root — and Tab still moved the
        // focus, because declining must not consume the key. A responder that returned `true` from
        // the default `OnKey` would have made Tab stop working document-wide.
        Assert.Equal(1, controller.Keys);
        Assert.True(args.Handled);
        Assert.Same(second, document.Focused);
    }

    // ── The undo leg ────────────────────────────────────────────────────────

    [Fact]
    public void A_control_finds_the_undo_manager_a_responder_supplies() {
        using var document = new UiDocument(100f, 100f);

        var manager = new UndoManager();
        var controller = new Controller("edit.copy", () => { }) { UndoManager = manager };

        var panel = View(document.Root);
        var leaf = View(panel);

        panel.AddResponder(controller);

        // ⚠ `NSResponder.undoManager`'s leg. The object that owns a document's stack is normally a
        // controller rather than a view, so a search that only looked at elements would find
        // nothing in exactly the arrangement this exists for.
        Assert.Same(manager, leaf.FindUndoManager());

        // And the element's own still outranks it, on the same "nearer wins" rule.
        var owned = new UndoManager();
        leaf.UndoManager = owned;

        Assert.Same(owned, leaf.FindUndoManager());
    }

    [Fact]
    public void A_responder_that_owns_no_manager_keeps_the_walk_going() {
        using var document = new UiDocument(100f, 100f);

        var manager = new UndoManager();
        var controller = new Controller("edit.copy", () => { });

        var panel = View(document.Root);
        var leaf = View(panel);

        panel.AddResponder(controller);
        document.UndoManager = manager;

        // `null` means "not mine, keep walking" and not "there is none" — the default every
        // responder that is a table of verbs gets.
        Assert.Same(manager, leaf.FindUndoManager());
    }
}
