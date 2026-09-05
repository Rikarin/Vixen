// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Focus, the focus scope tree, and the tab order.</summary>
public class FocusTests {
    const float Tolerance = 0.001f;

    static UiElement Stop(UiElement parent, int tabIndex = 0) {
        var element = parent.Add("div");
        element.Focusable = true;
        element.TabIndex = tabIndex;
        return element;
    }

    [Fact]
    public void Only_a_focusable_element_takes_the_focus() {
        using var document = new UiDocument(100f, 100f);

        var plain = document.Root.Add("div");
        var stop = Stop(document.Root);

        Assert.False(document.Focus(plain));
        Assert.Null(document.Focused);

        Assert.True(document.Focus(stop));
        Assert.Same(stop, document.Focused);
        Assert.True(stop.IsFocused);
    }

    [Fact]
    public void Tab_goes_round_the_stops_and_wraps() {
        using var document = new UiDocument(100f, 100f);

        var first = Stop(document.Root);
        var second = Stop(document.Root);
        var third = Stop(document.Root);

        Assert.True(document.MoveFocus(FocusDirection.Next));
        Assert.Same(first, document.Focused);

        document.MoveFocus(FocusDirection.Next);
        document.MoveFocus(FocusDirection.Next);
        Assert.Same(third, document.Focused);

        document.MoveFocus(FocusDirection.Next);
        Assert.Same(first, document.Focused);

        document.MoveFocus(FocusDirection.Previous);
        Assert.Same(third, document.Focused);

        Assert.Same(second, document.Root.Children[1]);
    }

    [Fact]
    public void With_nothing_focused_shift_tab_starts_at_the_end() {
        using var document = new UiDocument(100f, 100f);

        Stop(document.Root);
        var last = Stop(document.Root);

        Assert.True(document.MoveFocus(FocusDirection.Previous));
        Assert.Same(last, document.Focused);
    }

    [Fact]
    public void A_positive_tab_index_jumps_the_queue() {
        using var document = new UiDocument(100f, 100f);

        var natural = Stop(document.Root);
        var alsoNatural = Stop(document.Root);
        var promoted = Stop(document.Root, tabIndex: 1);

        // ⚠ HTML's rule, and it is stranger than it looks: a positive index comes before *every*
        // zero, so one element written at the bottom of a form jumps to the front of it. Implemented
        // faithfully rather than sanely — a framework that quietly reinterprets this produces a tab
        // order nobody can predict from the markup.
        Assert.Equal([promoted, natural, alsoNatural], UiDocument.TabOrder(document.Root));
    }

    [Fact]
    public void Two_elements_with_the_same_index_stay_in_document_order() {
        using var document = new UiDocument(100f, 100f);

        var first = Stop(document.Root, tabIndex: 2);
        var second = Stop(document.Root, tabIndex: 2);
        var third = Stop(document.Root, tabIndex: 1);

        // The sort has to be stable. An unstable one gives a tab order that changes with how many
        // elements are on the page, which is the kind of bug nobody can reproduce.
        Assert.Equal([third, first, second], UiDocument.TabOrder(document.Root));
    }

    [Fact]
    public void A_negative_tab_index_can_be_focused_but_is_not_a_stop() {
        using var document = new UiDocument(100f, 100f);

        var skipped = Stop(document.Root, tabIndex: -1);
        var stop = Stop(document.Root);

        Assert.DoesNotContain(skipped, UiDocument.TabOrder(document.Root));

        // Still focusable by a click or by code — the escape hatch for a pane that can hold focus
        // without being a stop on the way round.
        Assert.True(document.Focus(skipped));
        Assert.Same(skipped, document.Focused);

        document.MoveFocus(FocusDirection.Next);
        Assert.Same(stop, document.Focused);
    }

    [Fact]
    public void Tab_stays_inside_a_focus_scope() {
        using var document = new UiDocument(100f, 100f);

        var outside = Stop(document.Root);
        var dialog = document.Root.Add("div");
        dialog.IsFocusScope = true;

        var first = Stop(dialog);
        var second = Stop(dialog);

        document.Focus(first);
        document.MoveFocus(FocusDirection.Next);
        Assert.Same(second, document.Focused);

        // ⚠ Wraps within the dialog rather than escaping into the window behind it, which is what
        // makes a dialog modal to the keyboard.
        document.MoveFocus(FocusDirection.Next);
        Assert.Same(first, document.Focused);
        Assert.NotSame(outside, document.Focused);
    }

    [Fact]
    public void A_press_on_something_that_cannot_hold_the_focus_takes_it_away() {
        using var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; flex-direction: row; }
            div { width: 100px; height: 200px; }
        """);

        var stop = Stop(document.Root);
        var background = document.Root.Add("div");
        document.Update();

        document.Focus(stop);
        Assert.Same(background, document.HitTest(150f, 100f));

        document.Dispatch(new PointerEvent { X = 150f, Y = 100f, Action = PointerAction.Pressed, Button = PointerButton.Primary });

        Assert.Null(document.Focused);
        Assert.False(stop.IsFocused);
    }

    [Fact]
    public void A_press_inside_the_focused_element_leaves_the_focus_where_it_is() {
        using var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            div { width: 100px; height: 100px; }
        """);

        var stop = Stop(document.Root);

        // The part a control draws itself out of. It is what a press actually lands on, and it holds
        // no focus of its own — blurring on that would take the focus off every control the moment
        // it was clicked.
        var part = stop.Add("div");
        document.Update();

        document.Focus(stop);
        Assert.Same(part, document.HitTest(50f, 50f));

        document.Dispatch(new PointerEvent { X = 50f, Y = 50f, Action = PointerAction.Pressed, Button = PointerButton.Primary });

        Assert.Same(stop, document.Focused);
    }

    [Fact]
    public void Focus_and_focus_within_reach_the_cascade() {
        using var document = new UiDocument(200f, 200f);

        document.Load("""
            root { width: 200px; height: 200px; }
            div { height: 10px; }
            div:focus { height: 40px; }
            div:focus-within { width: 90px; }
        """);

        var panel = document.Root.Add("div");
        var field = Stop(panel);

        document.Focus(field);
        document.Update();

        // A focus ring is a stylesheet's business, so the states have to be answered by the cascade
        // rather than by a special case somewhere in the renderer.
        Assert.Equal(40f, field.Height, Tolerance);
        Assert.Equal(90f, panel.Width, Tolerance);
        Assert.Equal(90f, field.Width, Tolerance);
    }

    [Fact]
    public void Focus_leaving_clears_the_state_it_set() {
        using var document = new UiDocument(100f, 100f);

        var panel = document.Root.Add("div");
        var field = Stop(panel);
        var elsewhere = Stop(document.Root);

        document.Focus(field);
        Assert.True(panel.State.HasFlag(ElementState.FocusWithin));

        document.Focus(elsewhere);

        Assert.False(field.State.HasFlag(ElementState.Focus));
        Assert.False(panel.State.HasFlag(ElementState.FocusWithin));
        Assert.True(elsewhere.State.HasFlag(ElementState.Focus));
    }

    [Fact]
    public void A_common_ancestor_does_not_lose_focus_within_on_the_way_past() {
        using var document = new UiDocument(100f, 100f);

        var form = document.Root.Add("div");
        var first = Stop(form);
        var second = Stop(form);

        document.Focus(first);

        var flickers = 0;
        form.AddHandler<FocusEvent>((_, _) => flickers++, handledEventsToo: true);

        document.Focus(second);

        // ⚠ The form contains both, so its `:focus-within` never stopped being true. Clearing the
        // old chain before setting the new one — rather than interleaving — is what keeps it from
        // switching off and on again, and a transition on it from restarting for no visible reason.
        Assert.True(form.State.HasFlag(ElementState.FocusWithin));

        // It still hears about both, because the events bubble through it.
        Assert.Equal(2, flickers);
    }

    [Fact]
    public void Focus_events_say_where_the_focus_went() {
        using var document = new UiDocument(100f, 100f);

        var first = Stop(document.Root);
        var second = Stop(document.Root);

        document.Focus(first);

        var log = new List<string>();
        first.AddHandler<FocusEvent>((_, args) => log.Add(args.Gained ? "first gained" : "first lost"));
        second.AddHandler<FocusEvent>((_, args) => log.Add(args.Gained ? "second gained" : "second lost"));

        document.Focus(second);

        Assert.Equal(["first lost", "second gained"], log);
    }

    [Fact]
    public void Focusing_what_is_already_focused_changes_nothing() {
        using var document = new UiDocument(100f, 100f);

        var stop = Stop(document.Root);
        document.Focus(stop);

        var events = 0;
        stop.AddHandler<FocusEvent>((_, _) => events++);

        Assert.True(document.Focus(stop));
        Assert.Equal(0, events);
    }

    [Fact]
    public void Focusing_nothing_clears_the_focus() {
        using var document = new UiDocument(100f, 100f);

        var stop = Stop(document.Root);
        document.Focus(stop);

        Assert.False(document.Focus(null));
        Assert.Null(document.Focused);
        Assert.False(stop.State.HasFlag(ElementState.Focus));
    }

    [Fact]
    public void A_display_none_subtree_contributes_no_tab_stops() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            div { width: 20px; height: 20px; }
            parked { display: none; }
        """);

        var shown = Stop(document.Root);

        // The shape every pool in this framework has: a box kept alive for later and hidden with a
        // class. The three controls inside it are bound to nothing and cannot be seen, and until now
        // Tab visited all three.
        var parked = document.Root.Add("parked");
        var buried = Stop(parked);
        var alsoBuried = Stop(parked.Add("div"));

        document.Update();

        Assert.Equal([shown], UiDocument.TabOrder(document.Root));
        Assert.DoesNotContain(buried, UiDocument.TabOrder(document.Root));
        Assert.DoesNotContain(alsoBuried, UiDocument.TabOrder(document.Root));

        // Which is the property that matters: Tab wraps round the one stop rather than landing on
        // something invisible.
        Assert.True(document.MoveFocus(FocusDirection.Next));
        Assert.Same(shown, document.Focused);

        document.MoveFocus(FocusDirection.Next);
        Assert.Same(shown, document.Focused);
    }

    [Fact]
    public void A_focusable_element_that_is_itself_display_none_is_not_a_stop() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            div { width: 20px; height: 20px; }
            div.parked { display: none; }
        """);

        var shown = Stop(document.Root);
        var parked = Stop(document.Root);
        parked.AddClass("parked");

        document.Update();

        Assert.Equal([shown], UiDocument.TabOrder(document.Root));
    }

    [Fact]
    public void A_visibility_hidden_element_is_not_a_stop_and_a_visible_island_inside_one_is() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            div { width: 20px; height: 20px; }
            div.ghost { visibility: hidden; }
            div.seen { visibility: visible; }
        """);

        var shown = Stop(document.Root);

        var ghost = Stop(document.Root);
        ghost.AddClass("ghost");

        // ⚠ Inherited, and that is the whole reason this is asked per element rather than by
        // refusing the subtree: the draw list and the hit test both bring a declared `visible`
        // descendant back, so the tab order has to as well or a painted, clickable control would be
        // unreachable by keyboard.
        var inherited = Stop(ghost);
        var island = Stop(ghost);
        island.AddClass("seen");

        document.Update();

        Assert.Equal([shown, island], UiDocument.TabOrder(document.Root));
        Assert.DoesNotContain(ghost, UiDocument.TabOrder(document.Root));
        Assert.DoesNotContain(inherited, UiDocument.TabOrder(document.Root));
    }

    [Fact]
    public void An_unstyled_tree_is_all_stops_because_nothing_declared_otherwise() {
        using var document = new UiDocument(100f, 100f);

        var first = Stop(document.Root);
        var second = Stop(document.Root);

        // ⚠ The reason the hiding test reads the declared style and not the zero box layout gives an
        // element. Nothing here has been through a layout pass, so every one of these measures zero
        // — and a box test would answer that a document nobody has laid out yet has no tab order at
        // all, which is what every other test in this file would then be asserting about.
        Assert.Equal([first, second], UiDocument.TabOrder(document.Root));
    }
}
