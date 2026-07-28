// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Keys and typed text, from the document to the focus and back out.</summary>
public class KeyboardTests {
    static UiDocument Documented() {
        var document = new UiDocument(400f, 300f);
        document.Load("root { width: 400px; height: 300px; } .box { width: 40px; height: 20px; }");

        return document;
    }

    static KeyEvent Key(InputKey key, KeyAction action = KeyAction.Pressed, ModifierKeys modifiers = ModifierKeys.None) =>
        new() { Key = key, Action = action, Modifiers = modifiers };

    [Fact]
    public void A_key_goes_to_the_focus() {
        using var document = Documented();

        var field = document.Root.Add("div", classNames: "box");
        field.Focusable = true;
        document.Focus(field);

        UiElement? reached = null;
        field.AddHandler<KeyEvent>((element, _) => reached = element);

        document.Dispatch(Key(InputKey.A));

        Assert.Same(field, reached);
    }

    [Fact]
    public void With_nothing_focused_it_goes_to_the_root() {
        using var document = Documented();

        var seen = 0;
        document.Root.AddHandler<KeyEvent>((_, _) => seen++);

        document.Dispatch(Key(InputKey.A));

        // ⚠ The root is a real target rather than a fallback nobody listens on: an application-wide
        // shortcut has to work before anything has been clicked, which is the state every
        // application starts in.
        Assert.Equal(1, seen);
    }

    [Fact]
    public void It_bubbles_from_the_focus_to_the_root() {
        using var document = Documented();

        var panel = document.Root.Add("div");
        var field = panel.Add("div", classNames: "box");
        field.Focusable = true;
        document.Focus(field);

        var route = new List<UiElement>();
        panel.AddHandler<KeyEvent>((element, _) => route.Add(element));
        document.Root.AddHandler<KeyEvent>((element, _) => route.Add(element));

        document.Dispatch(Key(InputKey.Escape));

        Assert.Equal([panel, document.Root], route);
    }

    [Fact]
    public void Tab_moves_the_focus_and_shift_tab_moves_it_back() {
        using var document = Documented();

        var first = document.Root.Add("div", classNames: "box");
        var second = document.Root.Add("div", classNames: "box");
        first.Focusable = true;
        second.Focusable = true;

        document.Focus(first);
        document.Dispatch(Key(InputKey.Tab));
        Assert.Same(second, document.Focused);

        document.Dispatch(Key(InputKey.Tab, modifiers: ModifierKeys.Shift));
        Assert.Same(first, document.Focused);
    }

    [Fact]
    public void A_control_that_wants_tab_can_have_it() {
        using var document = Documented();

        var first = document.Root.Add("div", classNames: "box");
        var second = document.Root.Add("div", classNames: "box");
        first.Focusable = true;
        second.Focusable = true;

        first.AddHandler<KeyEvent>((_, args) => args.Handled = true);

        document.Focus(first);
        document.Dispatch(Key(InputKey.Tab));

        // The default runs after the route and only if nothing wanted it — which is what lets a code
        // editor insert an indent without the document stealing the key first.
        Assert.Same(first, document.Focused);
    }

    [Fact]
    public void Ctrl_tab_is_left_alone() {
        using var document = Documented();

        var first = document.Root.Add("div", classNames: "box");
        var second = document.Root.Add("div", classNames: "box");
        first.Focusable = true;
        second.Focusable = true;

        document.Focus(first);
        document.Dispatch(Key(InputKey.Tab, modifiers: ModifierKeys.Control));

        // A document switcher in every application that has documents. Consuming it here would mean
        // a tab strip could never be given it.
        Assert.Same(first, document.Focused);
    }

    [Fact]
    public void Typed_text_goes_to_the_focus() {
        using var document = Documented();

        var field = document.Root.Add("div", classNames: "box");
        field.Focusable = true;
        document.Focus(field);

        string? typed = null;
        field.AddHandler<TextInputEvent>((_, args) => typed = args.Text);

        document.Dispatch(new TextInputEvent { Text = "é" });

        Assert.Equal("é", typed);
    }

    [Fact]
    public void The_focus_ring_follows_how_the_focus_arrived() {
        using var document = Documented();

        var first = document.Root.Add("div", classNames: "box");
        var second = document.Root.Add("div", classNames: "box");
        first.Focusable = true;
        second.Focusable = true;

        document.Update();

        // A press is what puts the document back into pointer mode, so the focus it causes is quiet.
        document.Dispatch(
            new PointerEvent { X = 10f, Y = 5f, Action = PointerAction.Pressed, Button = PointerButton.Primary }
        );

        document.Focus(first);
        Assert.True((first.State & ElementState.Focus) != 0);
        Assert.Equal(ElementState.None, first.State & ElementState.FocusVisible);

        document.Dispatch(Key(InputKey.Tab));

        Assert.Same(second, document.Focused);
        Assert.True((second.State & ElementState.FocusVisible) != 0);
        Assert.Equal(ElementState.None, first.State & ElementState.FocusVisible);
    }

    [Fact]
    public void A_pointer_move_does_not_take_the_ring_away() {
        using var document = Documented();

        var field = document.Root.Add("div", classNames: "box");
        field.Focusable = true;
        document.Update();

        document.Dispatch(Key(InputKey.Tab));
        Assert.True((field.State & ElementState.FocusVisible) != 0);

        // A mouse crossing the screen while somebody is tabbing through a form has not taken over
        // the interaction. Clearing on movement would put the ring out mid-keystroke.
        document.Dispatch(new PointerEvent { X = 200f, Y = 200f, Action = PointerAction.Moved });

        Assert.True(document.KeyboardMode);
        Assert.True((field.State & ElementState.FocusVisible) != 0);
    }
}
