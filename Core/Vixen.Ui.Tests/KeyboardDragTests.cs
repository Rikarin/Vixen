// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A drag driven by the focus and finished by Enter, with no pointer anywhere in it.</summary>
/// <remarks>
///     ⚠ <b>Not one of these tests sends a <see cref="PointerEvent" />.</b> That is the assertion:
///     the in-app drag could be started and abandoned from the keyboard and not <i>completed</i> from
///     it, so a drag was a gesture only a mouse could finish. A test that moved the pointer to set
///     the target up would pass with every line of this feature deleted.
/// </remarks>
public class KeyboardDragTests {
    static UiDocument Laid() {
        var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; flex-direction: row; }
            .pane { width: 100px; height: 100px; flex-shrink: 0; }
        """);

        return document;
    }

    static KeyEvent Pressed(InputKey key, ModifierKeys modifiers = ModifierKeys.None) =>
        new() { Action = KeyAction.Pressed, Key = key, Modifiers = modifiers };

    static DataObject Row(string name) {
        var data = new DataObject();
        data.SetText(name);

        return data;
    }

    /// <summary>A pane that takes drops, focusable so that the keyboard can reach it.</summary>
    static UiElement Pane(UiDocument document) {
        var pane = document.Root.Add("div", classNames: "pane");
        pane.AllowDrop = true;
        pane.Focusable = true;

        return pane;
    }

    [Fact]
    public void Moving_the_focus_moves_the_drag_and_enter_drops_it() {
        using var document = Laid();

        var source = document.Root.Add("div", classNames: "pane");
        source.Focusable = true;

        var first = Pane(document);
        var second = Pane(document);
        document.Update();

        var stages = new List<(UiElement Element, DragOverStage Stage)>();
        first.AddHandler<DragOverEvent>((element, args) => stages.Add((element, args.Stage)));
        second.AddHandler<DragOverEvent>((element, args) => stages.Add((element, args.Stage)));

        var dropped = new List<string?>();
        second.AddHandler<DropEvent>((_, args) => dropped.Add(args.Data.Text));

        document.Focus(source);
        document.BeginDrag(source, Row("alpha"), DropEffect.Move);

        document.Focus(first);
        Assert.Same(first, document.CurrentDrag!.Target);

        document.Focus(second);
        Assert.Same(second, document.CurrentDrag!.Target);

        // Entered the first, left it when the focus moved on, entered the second — the same
        // sequence a pointer crossing the two would have produced.
        Assert.Equal(
            [(first, DragOverStage.Entered), (first, DragOverStage.Left), (second, DragOverStage.Entered)],
            stages
        );

        var key = Pressed(InputKey.Enter);
        document.Dispatch(key);

        Assert.True(key.Handled);
        Assert.Equal(["alpha"], dropped);
        Assert.Null(document.CurrentDrag);
    }

    /// <summary>The drop lands at the target's middle, which is what a three-zone target reads.</summary>
    [Fact]
    public void A_keyboard_drop_arrives_at_the_centre_and_not_the_corner() {
        using var document = Laid();

        var source = document.Root.Add("div", classNames: "pane");
        source.Focusable = true;

        var pane = Pane(document);
        document.Update();

        DropEvent? seen = null;
        pane.AddHandler<DropEvent>((_, args) => seen = args);

        document.Focus(source);
        document.BeginDrag(source, Row("alpha"));
        document.Focus(pane);
        document.Dispatch(Pressed(InputKey.Enter));

        Assert.NotNull(seen);
        Assert.Equal(pane.AbsoluteLeft + (pane.Width / 2f), seen!.X, 3);
        Assert.Equal(pane.AbsoluteTop + (pane.Height / 2f), seen.Y, 3);
    }

    /// <summary>Tabbing off the last target and pressing Enter drops nowhere, and the drag survives.</summary>
    /// <remarks>
    ///     ⚠ Both halves matter. A session that kept pointing at the target two stops back would drop
    ///     there instead, silently, and a session that treated the miss as a release would end the
    ///     drag on the key the user pressed to complete it.
    /// </remarks>
    [Fact]
    public void The_focus_leaving_every_target_empties_the_drag_without_ending_it() {
        using var document = Laid();

        var source = document.Root.Add("div", classNames: "pane");
        source.Focusable = true;

        var pane = Pane(document);

        var elsewhere = document.Root.Add("div", classNames: "pane");
        elsewhere.Focusable = true;
        document.Update();

        var dropped = 0;
        pane.AddHandler<DropEvent>((_, _) => dropped++);

        var left = 0;
        pane.AddHandler<DragOverEvent>((_, args) => left += args.Stage == DragOverStage.Left ? 1 : 0);

        document.Focus(source);
        document.BeginDrag(source, Row("alpha"));

        document.Focus(pane);
        document.Focus(elsewhere);

        Assert.Equal(1, left);
        Assert.Null(document.CurrentDrag!.Target);

        var key = Pressed(InputKey.Enter);
        document.Dispatch(key);

        Assert.Equal(0, dropped);
        Assert.False(key.Handled);
        Assert.NotNull(document.CurrentDrag);

        // And Escape is still what ends it, which is the reason Enter is allowed to do nothing.
        Assert.True(document.CancelDrag());
    }

    /// <summary>A target that refuses the effect is not dropped on, whichever driver arrived at it.</summary>
    [Fact]
    public void A_target_that_narrows_to_nothing_refuses_a_keyboard_drop_too() {
        using var document = Laid();

        var source = document.Root.Add("div", classNames: "pane");
        source.Focusable = true;

        var pane = Pane(document);
        pane.AddHandler<DragOverEvent>((_, args) => args.Effect = DropEffect.None);
        document.Update();

        var dropped = 0;
        pane.AddHandler<DropEvent>((_, _) => dropped++);

        document.Focus(source);
        document.BeginDrag(source, Row("alpha"));
        document.Focus(pane);

        var key = Pressed(InputKey.Enter);
        document.Dispatch(key);

        Assert.Equal(0, dropped);
        Assert.False(key.Handled);
    }

    /// <summary>With no drag running, Enter is the focused element's own and nothing intercepts it.</summary>
    /// <remarks>
    ///     ⚠ The half that could not otherwise fail. Taking Enter before the route unconditionally
    ///     would break every button, every dialog's default and every text field that submits, and
    ///     an assertion only about the drag would never have noticed.
    /// </remarks>
    [Fact]
    public void Enter_reaches_the_focus_when_no_drag_is_running() {
        using var document = Laid();

        var pane = Pane(document);
        document.Update();

        var reached = 0;
        pane.AddHandler<KeyEvent>((_, args) => {
            reached++;
            args.Handled = true;
        });

        document.Focus(pane);
        document.Dispatch(Pressed(InputKey.Enter));

        Assert.Equal(1, reached);

        // And a modified Enter is not a drop either: ⌘Enter and Shift-Enter are somebody's verb.
        document.BeginDrag(pane, Row("alpha"));
        document.Focus(pane);
        document.Dispatch(Pressed(InputKey.Enter, ModifierKeys.Meta));

        Assert.Equal(2, reached);
        Assert.NotNull(document.CurrentDrag);
    }
}
