// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>A finger that drags an advanced control inside a scroll view drags the control and not the view (#1357).</summary>
/// <remarks>
///     <para>
///         <c>TouchActionCensusTests</c> proves every capture has a row and every row resolves; this
///         proves the rows reach through a real gesture, for the controls that can stand alone. Every
///         case is made of touch pointer events through the document, because <c>touch-action</c>
///         governs a finger and never a mouse — the same drag with a mouse is green whatever the theme
///         says.
///     </para>
///     <para>
///         ⚠ <b>Read during the drag, before the release.</b> A focus arriving inside a scroll view
///         reveals the focused element, and these controls take the focus on the press — so an offset
///         read after the gesture counts that reveal as a scroll. <c>TouchActionTests.DragAcross</c>
///         in the base suite records the same trap.
///     </para>
/// </remarks>
public class AdvancedTouchActionTests {
    const float Step = 20f;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>A 200×200 view over a 2000×2000 body whose first child — under the view's middle — is the control.</summary>
    static (AdvancedFixture Fixture, ScrollView View, UiElement Control) Themed(Func<UiDocument, UiElement, UiElement> create) {
        var fixture = new AdvancedFixture(
            400f,
            400f,
            """
            #view { width: 200px; height: 200px; }
            #body { width: 2000px; height: 2000px; display: flex; flex-direction: column; }
            #knob { width: 180px; height: 180px; flex-shrink: 0; flex-grow: 0; flex-basis: auto; }
            """
        );

        var view = fixture.Document.Create<ScrollView>(null, fixture.Document.Root, "view");
        var body = fixture.Document.Create("div", view.Content, "body");
        var control = create(fixture.Document, body);

        fixture.Update();
        fixture.Advance(Frame);

        return (fixture, view, control);
    }

    /// <summary>Drags a finger from a point and reports how far the view's offsets moved during the drag.</summary>
    static (float Top, float Left) Drag(AdvancedFixture fixture, ScrollView view, float x, float y, float dx, float dy) {
        fixture.Touch(PointerAction.Pressed, x, y);
        fixture.Advance(Frame);

        var top = view.ScrollTop;
        var left = view.ScrollLeft;

        for (var step = 1; step <= 3; step++) {
            fixture.Touch(PointerAction.Moved, x + (dx * step), y + (dy * step));
            fixture.Advance(Frame);
        }

        var moved = (view.ScrollTop - top, view.ScrollLeft - left);
        fixture.Touch(PointerAction.Released, x + (dx * 3f), y + (dy * 3f));

        return moved;
    }

    static (float Top, float Left) DragFromMiddle(AdvancedFixture fixture, ScrollView view, float dx, float dy) {
        var (x, y) = AdvancedFixture.Centre(view);
        return Drag(fixture, view, x, y, dx, dy);
    }

    /// <summary>The paired baseline: an element no theme says anything about scrolls the view, in both axes.</summary>
    /// <remarks>
    ///     Without it every refusal below is satisfied by a fixture whose view could not scroll — a body
    ///     that failed to overflow, a swipe that missed, a fixture sending mouse events.
    /// </remarks>
    [Theory]
    [InlineData(0f, -1f)]
    [InlineData(-1f, 0f)]
    public void A_finger_on_an_undeclared_element_scrolls_the_view(float dx, float dy) {
        var (fixture, view, _) = Themed(static (document, parent) => document.Create("div", parent, "knob"));
        using var _ = fixture;

        var (top, left) = DragFromMiddle(fixture, view, dx * Step, dy * Step);

        Assert.True(top > 0f || left > 0f, $"top {top}, left {left}");
    }

    public static TheoryData<string> Owned => ["node-canvas", "node-minimap", "viewport", "image-view", "curve-editor", "color-field"];

    /// <summary>
    ///     The controls that own a finger outright: a canvas pans or bands in both axes, a viewport
    ///     orbits, a field picks a point. A drag either way leaves the view where it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(Owned))]
    public void A_finger_dragging_a_control_that_owns_it_never_reaches_the_view(string tag) {
        var (fixture, view, control) = Themed((document, parent) => Create(document, parent, tag));
        using var _ = fixture;

        Assert.Equal(tag, control.Tag);

        var vertical = DragFromMiddle(fixture, view, 0f, -Step);
        var horizontal = DragFromMiddle(fixture, view, -Step, 0f);

        Assert.True(
            vertical == (0f, 0f) && horizontal == (0f, 0f),
            $"a finger on `{tag}` moved the view: vertical {vertical}, horizontal {horizontal}"
        );
    }

    /// <summary>
    ///     ⚠ <b><c>pan-y</c>, and both halves of it.</b> A hue band reads <c>args.X</c> and nothing
    ///     else, so the horizontal travel is the band's and the vertical travel is still the view's —
    ///     a <c>none</c> here would make an inspector unscrollable from anywhere a colour row sits.
    /// </summary>
    [Fact]
    public void A_colour_strip_keeps_the_horizontal_finger_and_gives_back_the_vertical() {
        var (fixture, view, _) = Themed(static (document, parent) => document.Create<ColorStrip>(null, parent, "knob"));
        using var _ = fixture;

        Assert.Equal(0f, DragFromMiddle(fixture, view, -Step, 0f).Left);
        Assert.True(DragFromMiddle(fixture, view, 0f, -Step).Top > 0f);
    }

    /// <summary>
    ///     ⚠ <b>A timeline is two rows, because it is two gestures.</b> Its ruler scrubs along time and
    ///     nothing else, so the ruler is <c>pan-y</c>; its lanes draw a marquee in both axes on any
    ///     press that grabbed nothing, so the lanes are <c>none</c>. A single row on <c>timeline</c>
    ///     would have got one of the two wrong whichever keyword it chose.
    /// </summary>
    [Fact]
    public void A_timeline_scrubs_its_ruler_along_one_axis_and_owns_its_lanes_in_both() {
        var (fixture, view, control) = Themed(static (document, parent) => document.Create<Timeline>(null, parent, "knob"));
        using var _ = fixture;

        var timeline = (Timeline) control;
        var (rulerX, rulerY) = AdvancedFixture.Centre(timeline.Ruler);
        var (lanesX, lanesY) = AdvancedFixture.Centre(timeline.Lanes);

        Assert.True(timeline.Ruler.Height > 0f && timeline.Lanes.Height > 0f, "the fixture's timeline has no ruler or no lanes to press");

        // ⚠ The one drag that scrolls goes last. Both points were read before any gesture, and a view
        // that scrolled — or is still flinging from a release — has moved them.
        Assert.Equal((0f, 0f), Drag(fixture, view, lanesX, lanesY, 0f, -Step));
        Assert.Equal((0f, 0f), Drag(fixture, view, lanesX, lanesY, -Step, 0f));
        Assert.Equal(0f, Drag(fixture, view, rulerX, rulerY, -Step, 0f).Left);
        Assert.True(Drag(fixture, view, rulerX, rulerY, 0f, -Step).Top > 0f, "a vertical finger on the ruler is the view's");
    }

    /// <summary>A code editor of four hundred lines filling the fixture, so its own scroller has somewhere to go.</summary>
    static CodeEditor Code(AdvancedFixture fixture) {
        var editor = fixture.Add<CodeEditor>();
        editor.Source = string.Join('\n', Enumerable.Range(0, 400).Select(static line => $"line {line} alpha bravo charlie"));

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        return editor;
    }

    /// <summary>Drags from the middle of the editor's scroller and reports how far that scroller moved during the drag.</summary>
    static float DragCode(AdvancedFixture fixture, CodeEditor editor, float dx, float dy, PointerType type) {
        var (x, y) = AdvancedFixture.Centre(editor.Scroller);

        Send(fixture, PointerAction.Pressed, x, y, type);
        fixture.Advance(Frame);

        var top = editor.Scroller.ScrollTop;

        for (var step = 1; step <= 3; step++) {
            Send(fixture, PointerAction.Moved, x + (dx * step), y + (dy * step), type);
            fixture.Advance(Frame);
        }

        var scrolled = editor.Scroller.ScrollTop - top;
        Send(fixture, PointerAction.Released, x + (dx * 3f), y + (dy * 3f), type);

        return scrolled;
    }

    static void Send(AdvancedFixture fixture, PointerAction action, float x, float y, PointerType type) {
        if (type == PointerType.Touch) {
            fixture.Touch(action, x, y);
        } else if (action == PointerAction.Pressed) {
            fixture.Press(x, y);
        } else if (action == PointerAction.Moved) {
            fixture.Move(x, y);
        } else {
            fixture.Release(x, y);
        }
    }

    /// <summary>
    ///     ⚠ <b>The code editor is the text-field shape, not the canvas shape.</b> #1357 listed it with
    ///     the controls whose fix is a mechanical <c>none</c>; its capture is a text selection, and the
    ///     view a finger drags is the editor's <i>own</i> <see cref="CodeEditor.Scroller" /> — which a
    ///     row on <c>code-editor</c> cannot reach at all, because <c>touch-action</c> is read between
    ///     the pressed element and the view and the editor is above its own view, not between. So one
    ///     finger selected <b>and</b> scrolled the code under the selection it was making: measured
    ///     at 40 pixels of scroll with the selection spanning the lines it crossed.
    /// </summary>
    /// <remarks>
    ///     Settled the way <c>TextField</c> is: a finger's drag is the scroller's and begins no
    ///     selection. Diagonal, so the drag crosses both lines and columns and the old behaviour's
    ///     selection cannot be empty by accident.
    /// </remarks>
    [Fact]
    public void A_finger_dragging_a_code_editor_scrolls_its_own_view_and_selects_nothing() {
        using var fixture = new AdvancedFixture();
        var editor = Code(fixture);

        var scrolled = DragCode(fixture, editor, -Step, -Step, PointerType.Touch);

        Assert.True(scrolled > 0f, $"a finger's drag no longer scrolls the editor's own view (scrolled {scrolled})");
        Assert.False(editor.HasSelection, $"a finger's drag selected `{editor.SelectedText}`");
    }

    /// <summary>The paired half: a mouse drag still selects, and does not scroll a view that only a finger drags.</summary>
    [Fact]
    public void A_mouse_dragging_a_code_editor_still_selects() {
        using var fixture = new AdvancedFixture();
        var editor = Code(fixture);

        DragCode(fixture, editor, -Step, -Step, PointerType.Mouse);

        Assert.True(editor.HasSelection, "a mouse drag across the code selected nothing");
    }

    /// <summary>A finger's caret arrives on the tap, and a finger held still selects the word under it.</summary>
    [Fact]
    public void A_finger_tap_places_the_caret_and_holding_still_selects_a_word() {
        using var fixture = new AdvancedFixture();
        var editor = Code(fixture);

        fixture.Document.Focus(null);
        fixture.Update();

        // Near the start of a line rather than the scroller's middle, which is past the end of every
        // line here — a word selected there is the line break, for a mouse's double click as well.
        var x = editor.Scroller.Bounds.X + 12f;
        var y = AdvancedFixture.Centre(editor.Scroller).Y;

        fixture.Touch(PointerAction.Pressed, x, y);
        Assert.False(editor.IsFocused, "a finger's press focused the editor before it could know the press was not a scroll");

        fixture.Touch(PointerAction.Released, x, y);
        Assert.True(editor.IsFocused);
        Assert.False(editor.HasSelection);
        Assert.True(editor.Caret.Line > 0, $"the caret is at {editor.Caret}, not where the tap landed");

        fixture.Rest();

        fixture.Touch(PointerAction.Pressed, x, y);
        fixture.Advance(TimeSpan.FromSeconds(2));
        fixture.Touch(PointerAction.Released, x, y);

        Assert.Matches("^[a-z0-9]+$", editor.SelectedText);
    }

    static UiElement Create(UiDocument document, UiElement parent, string tag) => tag switch {
        "node-canvas" => document.Create<NodeCanvas>(null, parent, "knob"),
        "node-minimap" => document.Create<NodeMinimap>(null, parent, "knob"),
        "viewport" => document.Create<Viewport>(null, parent, "knob"),
        "image-view" => document.Create<ImageView>(null, parent, "knob"),
        "curve-editor" => document.Create<CurveEditor>(null, parent, "knob"),
        "color-field" => document.Create<ColorField>(null, parent, "knob"),
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "no control for this tag")
    };
}
