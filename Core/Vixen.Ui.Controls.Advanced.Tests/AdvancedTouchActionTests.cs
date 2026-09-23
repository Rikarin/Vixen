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

    /// <summary>
    ///     ⚠ <b>The code editor is the text-field shape, not the canvas shape, and it is measured here
    ///     rather than declared.</b> #1357 listed it with the controls whose fix is a mechanical
    ///     <c>none</c>; its capture is a text selection, and the view a finger drags is the editor's
    ///     <i>own</i> <see cref="CodeEditor.Scroller" /> — which a row on <c>code-editor</c> cannot
    ///     reach at all, because <c>touch-action</c> is read between the pressed element and the view
    ///     and the editor is above its own view, not between. So one finger selects <b>and</b> scrolls
    ///     the code under the selection it is making.
    /// </summary>
    /// <remarks>
    ///     This asserts the defect, as <c>TouchActionTests</c> does for <c>textbox</c> and
    ///     <c>textarea</c>, because the remedy is the same undecided change: a plain finger drag that
    ///     does not begin a selection at all (#225). The day that is decided this goes red, and the
    ///     census row for <c>CodeEditor.cs</c> is what to update.
    /// </remarks>
    [Fact]
    public void A_finger_dragging_a_code_editor_selects_and_scrolls_its_own_view_and_that_is_not_yet_decided() {
        using var fixture = new AdvancedFixture();

        var editor = fixture.Add<CodeEditor>();
        editor.Source = string.Join('\n', Enumerable.Range(0, 400).Select(static line => $"line {line}"));

        fixture.Update();
        editor.Refresh();
        fixture.Update();

        var (x, y) = AdvancedFixture.Centre(editor.Scroller);

        fixture.Touch(PointerAction.Pressed, x, y);
        fixture.Advance(Frame);

        var top = editor.Scroller.ScrollTop;

        for (var step = 1; step <= 3; step++) {
            fixture.Touch(PointerAction.Moved, x, y - (Step * step));
            fixture.Advance(Frame);
        }

        var scrolled = editor.Scroller.ScrollTop - top;
        var selected = editor.HasSelection;

        fixture.Touch(PointerAction.Released, x, y - (Step * 3f));

        Assert.True(
            scrolled > 0f && selected,
            $"""
             a finger dragging a code editor no longer both selects and scrolls (scrolled {scrolled}, selected {selected}).

             Either the editor stopped taking a finger's drag as a selection, or its own view stopped taking
             the drag: that is the decision this theory was waiting for. Record it in
             `TouchActionCensus.txt`'s `CodeEditor.cs` row and in `AdvancedTheme.vcss`'s touch block, and
             turn this into the assertion its siblings are.
             """
        );
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
