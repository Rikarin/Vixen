// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Alt and a letter, and which element that reaches.</summary>
/// <remarks>
///     <para>
///         The framework's half: finding the element and raising an event on it. What an access key
///         <i>does</i> is the control's business — <c>Vixen.Ui.Controls</c> makes a button press —
///         and this assembly deliberately has no controls in it, so the tests below listen for
///         <see cref="AccessKeyEvent" /> directly.
///     </para>
///     <para>
///         Verified by sabotage, eight of eight landing: searching the whole document instead of the
///         focus scope fails 1, re-firing instead of cycling fails 1, searching hidden subtrees fails
///         1, letting <c>:disabled</c> elements answer fails 1, accepting Alt-and-anything fails 1,
///         running before the route instead of after it fails 1, mapping the number row
///         arithmetically past 9 fails 1, and treating a doubled marker as a key fails 1.
///     </para>
///     <para>
///         ⚠ <b>A ninth failed to fail and was deleted rather than defended.</b> The document
///         guarded <c>Focus(target)</c> with <c>if (target.Focusable)</c>, which reads as a rule and
///         is insurance: <c>Focus</c> already refuses an element that cannot hold the focus. Removing
///         the guard broke nothing because there was nothing there to break.
///     </para>
/// </remarks>
public class AccessKeyTests {
    static UiDocument Documented() {
        var document = new UiDocument(400f, 200f);
        document.Load("root { width: 400px; height: 200px; } div { width: 60px; height: 20px; }");

        return document;
    }

    static UiElement Keyed(UiElement parent, char key, bool focusable = true) {
        var element = parent.Add("div");
        element.AccessKey = key;
        element.Focusable = focusable;

        return element;
    }

    static List<UiElement> Listening(params UiElement[] elements) {
        var reached = new List<UiElement>();

        foreach (var element in elements) {
            element.AddHandler<AccessKeyEvent>((source, _) => reached.Add(source));
        }

        return reached;
    }

    static void Press(UiDocument document, InputKey key, ModifierKeys modifiers = ModifierKeys.Alt) =>
        document.Dispatch(new KeyEvent { Key = key, Action = KeyAction.Pressed, Modifiers = modifiers });

    [Fact]
    public void Alt_and_a_letter_reach_the_element_that_claims_it() {
        using var document = Documented();

        var save = Keyed(document.Root, 'S');
        var cancel = Keyed(document.Root, 'C');
        var reached = Listening(save, cancel);

        document.Update();
        Press(document, InputKey.S);

        Assert.Same(save, Assert.Single(reached));

        // And it takes the focus, which is half of what an access key is for: the keyboard user who
        // pressed it is now *on* the control rather than having poked it from a distance.
        Assert.Same(save, document.Focused);
    }

    [Fact]
    public void The_case_of_the_key_does_not_matter() {
        using var document = Documented();

        var save = Keyed(document.Root, 's');
        var reached = Listening(save);

        document.Update();
        Press(document, InputKey.S);

        Assert.Single(reached);
    }

    [Fact]
    public void Alt_and_something_else_is_somebody_elses_shortcut() {
        using var document = Documented();

        var save = Keyed(document.Root, 'S');
        var reached = Listening(save);

        document.Update();

        // ⚠ Exact modifiers, not "Alt is among them". Ctrl-Alt-S is a shortcut an application may
        // well have bound, and an access key that also answered to it would take the key away from
        // whoever bound it — silently, and only on the machines where that combination is used.
        Press(document, InputKey.S, ModifierKeys.Alt | ModifierKeys.Control);
        Press(document, InputKey.S, ModifierKeys.Alt | ModifierKeys.Shift);
        Press(document, InputKey.S, ModifierKeys.None);

        Assert.Empty(reached);
    }

    [Fact]
    public void A_control_that_wanted_the_key_keeps_it() {
        using var document = Documented();

        var save = Keyed(document.Root, 'S');
        var field = document.Root.Add("div");

        field.Focusable = true;
        field.AddHandler<KeyEvent>((_, args) => args.Handled = true);

        var reached = Listening(save);
        document.Update();
        document.Focus(field);

        Press(document, InputKey.S);

        // ⚠ The access key runs *after* the route and only if nothing took the event, exactly like
        // Tab. A text field that uses Alt-Left for word movement must not lose it to a button that
        // happens to be called "_Left".
        Assert.Empty(reached);
    }

    [Fact]
    public void A_second_press_cycles_rather_than_pressing_the_same_one_again() {
        using var document = Documented();

        var first = Keyed(document.Root, 'N');
        var second = Keyed(document.Root, 'N');
        var reached = Listening(first, second);

        document.Update();

        Press(document, InputKey.N);
        Press(document, InputKey.N);
        Press(document, InputKey.N);

        // ⚠ Two elements sharing a key is ordinary — two "_Name" fields in different groups — and
        // there is no rule that makes one of them right. Cycling makes the collision an annoyance;
        // re-firing makes one of the two unreachable from the keyboard for ever.
        Assert.Equal([first, second, first], reached);
    }

    [Fact]
    public void A_hidden_element_does_not_answer() {
        using var document = Documented();

        var panel = document.Root.Add("div");
        panel.SetStyle("display", "none");

        var hidden = Keyed(panel, 'H');
        var reached = Listening(hidden);

        document.Update();
        Press(document, InputKey.H);

        // ⚠ Its *parent* is collapsed, not the element itself — which is the case a check that read
        // `display` off the element alone would let through. Flexbox reports a collapsed subtree as
        // zero boxes all the way down, so asking the layout answers the whole question.
        Assert.Empty(reached);
    }

    [Fact]
    public void A_disabled_element_does_not_answer() {
        using var document = Documented();

        var button = Keyed(document.Root, 'D');
        button.State |= ElementState.Disabled;

        var reached = Listening(button);
        document.Update();
        Press(document, InputKey.D);

        // Read off the style state rather than off a control property, because this assembly has no
        // controls — and `:disabled` is the same fact the cascade uses to grey the thing out.
        Assert.Empty(reached);
    }

    [Fact]
    public void A_focus_scope_is_the_boundary() {
        using var document = Documented();

        var behind = Keyed(document.Root, 'S');

        var dialog = document.Root.Add("div");
        dialog.IsFocusScope = true;

        var inside = Keyed(dialog, 'S');
        var reached = Listening(behind, inside);

        document.Update();
        document.Focus(inside);

        Press(document, InputKey.S);

        // ⚠ A dialog whose "_Save" could be answered by a toolbar button in the window behind it is
        // not modal. This is the same scope tab traversal uses, for the same reason.
        Assert.Same(inside, Assert.Single(reached));
    }

    [Fact]
    public void A_number_row_key_works_as_well_as_a_letter() {
        using var document = Documented();

        var first = Keyed(document.Root, '1');
        var zero = Keyed(document.Root, '0');
        var reached = Listening(first, zero);

        document.Update();

        Press(document, InputKey.Number1);
        Press(document, InputKey.Number0);

        // ⚠ Zero is at the *end* of the number row in the HID table, past 9, so mapping the range
        // arithmetically and forgetting it gives Alt-0 to whatever key code happens to follow.
        Assert.Equal([first, zero], reached);
    }

    [Fact]
    public void An_element_that_cannot_take_the_focus_still_hears_it() {
        using var document = Documented();

        var element = Keyed(document.Root, 'X', focusable: false);
        var reached = Listening(element);

        document.Update();
        Press(document, InputKey.X);

        // The event and the focus are separate: a label with an access key that moves the focus to
        // the field beside it is the classic use, and it can only work if the label hears the key
        // without being focusable itself.
        Assert.Single(reached);
        Assert.Null(document.Focused);
    }

    [Fact]
    public void A_label_can_carry_its_access_key_in_a_marker() {
        Assert.Equal("Save", AccessKey.Parse("_Save", out var save));
        Assert.Equal('S', save);

        Assert.Equal("Save As", AccessKey.Parse("Save _As", out var asKey));
        Assert.Equal('A', asKey);

        // ⚠ A doubled marker is a literal one and claims nothing. Without that rule `snake__case`
        // takes `c` as an access key, and the collision only appears when somebody holds Alt.
        Assert.Equal("snake_case", AccessKey.Parse("snake__case", out var none));
        Assert.Equal('\0', none);

        // The first marked character wins; a second is a mistake and taking the first is at least
        // predictable.
        Assert.Equal("Save As", AccessKey.Parse("_Save _As", out var first));
        Assert.Equal('S', first);

        Assert.Equal("plain", AccessKey.Parse("plain", out var absent));
        Assert.Equal('\0', absent);
    }
}
