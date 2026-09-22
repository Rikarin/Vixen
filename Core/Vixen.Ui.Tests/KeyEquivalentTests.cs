// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A bare Return or Escape, and which element that reaches.</summary>
/// <remarks>
///     <para>
///         <see cref="AccessKeyTests" /> with the Alt taken off: the framework finds the element and
///         raises a <see cref="KeyEquivalentEvent" /> on it, and what the element does is the
///         control's business — <c>Vixen.Ui.Controls</c> makes a button press. This assembly has no
///         controls, so the tests listen for the event directly. Filed under #666's "present with a
///         named gap": <c>Button</c> had no default or cancel key equivalent, so a form's Return did
///         nothing unless a field happened to take it.
///     </para>
///     <para>
///         Sabotages, each reddening the fact it names and no other: searching the whole document
///         rather than the focus scope; running before the route rather than after it; accepting
///         Ctrl-Return; letting <c>:disabled</c> elements answer; taking the focus.
///     </para>
/// </remarks>
public class KeyEquivalentTests {
    static UiDocument Documented() {
        var document = new UiDocument(400f, 200f);
        document.Load("root { width: 400px; height: 200px; } div { width: 60px; height: 20px; }");

        return document;
    }

    static UiElement Equivalent(UiElement parent, InputKey key, bool focusable = true) {
        var element = parent.Add("div");
        element.KeyEquivalent = key;
        element.Focusable = focusable;

        return element;
    }

    static List<UiElement> Listening(params UiElement[] elements) {
        var reached = new List<UiElement>();

        foreach (var element in elements) {
            element.AddHandler<KeyEquivalentEvent>((source, args) => { reached.Add(source); args.Handled = true; });
        }

        return reached;
    }

    static bool Press(UiDocument document, InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        var args = new KeyEvent { Key = key, Action = KeyAction.Pressed, Modifiers = modifiers };
        document.Dispatch(args);

        return args.Handled;
    }

    [Fact]
    public void Return_reaches_the_element_that_declares_it_without_moving_the_focus() {
        using var document = Documented();

        var field = document.Root.Add("div");
        field.Focusable = true;

        var ok = Equivalent(document.Root, InputKey.Enter);
        var cancel = Equivalent(document.Root, InputKey.Escape);
        var reached = Listening(ok, cancel);

        document.Update();
        document.Focus(field);

        Assert.True(Press(document, InputKey.Enter));
        Assert.Same(ok, Assert.Single(reached));

        // Unlike an access key: Return in a field commits the form the way a click on the button
        // would, and a click does not take the focus from the field the user was typing in either.
        Assert.Same(field, document.Focused);

        reached.Clear();

        Assert.True(Press(document, InputKey.Escape));
        Assert.Same(cancel, Assert.Single(reached));
    }

    [Fact]
    public void The_keypad_return_is_the_same_request() {
        using var document = Documented();

        var ok = Equivalent(document.Root, InputKey.Enter);
        var reached = Listening(ok);

        document.Update();

        Assert.True(Press(document, InputKey.KeypadEnter));
        Assert.Same(ok, Assert.Single(reached));
    }

    /// <summary>A key the route took never reaches the default: a multi-line field keeps its newline.</summary>
    [Fact]
    public void A_press_something_on_the_route_handled_does_not_reach_it() {
        using var document = Documented();

        var field = document.Root.Add("div");
        field.Focusable = true;
        field.AddHandler<KeyEvent>((_, args) => args.Handled = args.Key == InputKey.Enter);

        var ok = Equivalent(document.Root, InputKey.Enter);
        var reached = Listening(ok);

        document.Update();
        document.Focus(field);
        Press(document, InputKey.Enter);

        Assert.Empty(reached);
    }

    [Fact]
    public void A_modified_press_is_somebody_else_s_shortcut() {
        using var document = Documented();

        var ok = Equivalent(document.Root, InputKey.Enter);
        var reached = Listening(ok);

        document.Update();

        Assert.False(Press(document, InputKey.Enter, ModifierKeys.Control));
        Assert.False(Press(document, InputKey.Enter, ModifierKeys.Shift));
        Assert.Empty(reached);
    }

    /// <summary>A dialog's default cannot be pressed from the window behind it.</summary>
    [Fact]
    public void Only_the_focus_scope_is_searched() {
        using var document = Documented();

        var behind = Equivalent(document.Root, InputKey.Enter);

        var dialog = document.Root.Add("div");
        dialog.IsFocusScope = true;

        var field = dialog.Add("div");
        field.Focusable = true;

        var reached = Listening(behind);

        document.Update();
        document.Focus(field);

        Assert.False(Press(document, InputKey.Enter));
        Assert.Empty(reached);

        // And with one inside the scope, that is the one.
        var inside = Equivalent(dialog, InputKey.Enter);
        var reachedInside = Listening(inside);

        document.Update();

        Assert.True(Press(document, InputKey.Enter));
        Assert.Same(inside, Assert.Single(reachedInside));
        Assert.Empty(reached);
    }

    [Fact]
    public void A_disabled_element_does_not_answer_and_neither_does_a_collapsed_one() {
        using var document = Documented();

        var disabled = Equivalent(document.Root, InputKey.Enter);
        disabled.State |= ElementState.Disabled;

        var hidden = document.Root.Add("div");
        hidden.SetStyle("display", "none");
        var collapsed = Equivalent(hidden, InputKey.Enter);

        var reached = Listening(disabled, collapsed);

        document.Update();

        Assert.False(Press(document, InputKey.Enter));
        Assert.Empty(reached);
    }

    /// <summary>The answer is the element's, not "found one": a declined event leaves the key alone.</summary>
    [Fact]
    public void An_element_that_declines_the_event_leaves_the_key_unhandled() {
        using var document = Documented();

        var ok = Equivalent(document.Root, InputKey.Enter);
        var seen = 0;
        ok.AddHandler<KeyEquivalentEvent>((_, _) => seen++);

        document.Update();

        Assert.False(Press(document, InputKey.Enter));
        Assert.Equal(1, seen);
    }
}
