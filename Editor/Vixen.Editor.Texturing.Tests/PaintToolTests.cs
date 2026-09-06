// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.Texturing.Painting;
using Vixen.Terrain;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The brush an artist dials in, and the verb that picks it up.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § M9's brush surface, less the viewport — and saying which half this is matters
///         more here than usual.</b> What is asserted is that the settings exist, that they are
///         clamped where a stroke assumes they are, that they survive the panel being closed, and
///         that the mode has a verb. What is <em>not</em> asserted, because nothing in this tree can
///         do it yet, is that a drag paints: that needs a pointer position in texels, and § D13's two
///         front ends are what produce one.
///     </para>
///     <para>
///         ⚠ <b>The clamping tests are written against the values that break a stroke rather than
///         against the range.</b> A radius of zero, a spacing of zero and a NaN out of a half-typed
///         field each have a specific consequence — no texel covered, a divisor of zero in
///         <c>BrushStroke</c>, and a weight function that returns NaN and paints transparency — so
///         each is its own case rather than a sweep over the interval.
///     </para>
/// </remarks>
public class PaintToolTests {
    /// <summary>A brush arrives clamped however it was set.</summary>
    /// <remarks>
    ///     ⚠ The three inputs a control can produce that a stroke cannot survive. A NaN is the one a
    ///     clamp alone does not catch — <c>Math.Clamp</c> of a NaN is a NaN — and a NaN radius makes
    ///     every weight NaN, so the stroke erases instead of painting.
    /// </remarks>
    [Fact]
    public void The_values_that_break_a_stroke_cannot_be_set() {
        PaintTool tool = new();

        tool.SetRadius(0f);
        tool.SetSpacing(0f);
        tool.SetFlow(float.NaN);
        tool.SetOpacity(4f);
        tool.SetSmoothing(1f);

        Assert.Equal(PaintTool.MinimumRadius, tool.Brush.Radius);
        Assert.True(tool.Brush.Spacing > 0f, "a spacing of zero is a stamp every zero texels.");
        Assert.False(float.IsNaN(tool.Brush.Flow), "a NaN reached the brush, and a NaN weight paints nothing.");
        Assert.Equal(1f, tool.Brush.Opacity);
        Assert.True(tool.Smoothing < 1f, "a smoothing of one is a path that never reaches the pointer.");

        // The instrument: an ordinary value is not clamped, so the assertions above are about the
        // edges rather than about a setter that ignores what it is given.
        tool.SetRadius(24f);
        tool.SetFlow(0.4f);

        Assert.Equal(24f, tool.Brush.Radius);
        Assert.Equal(0.4f, tool.Brush.Flow);
    }

    /// <summary>Angle jitter is set in degrees and held in radians.</summary>
    [Fact]
    public void Angle_jitter_is_typed_in_degrees_and_kept_in_radians() {
        PaintTool tool = new();

        tool.SetAngleJitter(90f);

        Assert.Equal(MathF.PI / 2f, tool.Brush.AngleJitter, 5);
        Assert.Equal(90f, tool.AngleJitterDegrees, 3);
    }

    /// <summary>The mode swaps, and it starts off.</summary>
    /// <remarks>
    ///     ⚠ Off first, because a panel that opened in paint mode would make the first drag on a
    ///     newly opened stack a stroke rather than a selection.
    /// </remarks>
    [Fact]
    public void The_tool_starts_out_of_paint_mode_and_toggles() {
        PaintTool tool = new();

        Assert.False(tool.IsPainting);
        Assert.Equal(PaintToolMode.Paint, tool.Toggle());
        Assert.True(tool.IsPainting);
        Assert.Equal(PaintToolMode.Select, tool.Toggle());
    }

    /// <summary>The inspector's sliders write through the tool, so they are clamped too.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect this is against is a slider whose range somebody widens.</b> Every control
    ///     here calls a <c>PaintTool</c> setter rather than assigning a <c>PaintBrush</c> field, so a
    ///     maximum raised past what a stroke can survive still cannot produce such a brush. Driving a
    ///     slider past its own maximum is how that is shown without editing the inspector.
    /// </remarks>
    [Fact]
    public void A_slider_driven_past_its_range_still_lands_on_a_brush_a_stroke_can_use() {
        PaintTool tool = new();

        using UiDocument document = new(1280f, 800f);

        PaintBrushInspector inspector = new(document.Root, tool);

        var sliders = Every<Slider>(inspector.Root);

        Assert.NotEmpty(sliders);

        foreach (var slider in sliders) {
            slider.Value = float.NaN;
        }

        Assert.False(float.IsNaN(tool.Brush.Radius));
        Assert.False(float.IsNaN(tool.Brush.Flow));
        Assert.False(float.IsNaN(tool.Brush.Opacity));
        Assert.False(float.IsNaN(tool.Brush.Falloff));
        Assert.False(float.IsNaN(tool.Brush.Spacing));
        Assert.False(float.IsNaN(tool.Smoothing));
        Assert.True(tool.Brush.Radius >= PaintTool.MinimumRadius);
    }

    /// <summary>Every setting doc 48 § M9 names has a row, and the rows read the tool.</summary>
    [Fact]
    public void The_inspector_has_a_row_for_every_setting_the_scope_line_names() {
        PaintTool tool = new();

        using UiDocument document = new(1280f, 800f);

        PaintBrushInspector inspector = new(document.Root, tool);

        Assert.All(
            new[] { "Radius", "Flow", "Opacity", "Falloff", "Spacing", "Position", "Angle", "Size" },
            setting => Assert.Contains(
                inspector.Captions,
                caption => caption.StartsWith(setting, StringComparison.Ordinal)
            )
        );

        // ⚠ And a caption carries the value, not only the name. A row that read "Radius" alone would
        // leave an artist unable to see what a slider is at without dragging it.
        tool.SetRadius(17f);
        inspector.Refresh();

        Assert.Contains(inspector.Captions, caption => caption.Contains("17", StringComparison.Ordinal));
    }

    /// <summary>Four curves, from the enum, so a fifth appears without an edit here.</summary>
    [Fact]
    public void The_curve_picker_offers_every_falloff_the_terrain_brush_declares() {
        PaintTool tool = new();

        using UiDocument document = new(1280f, 800f);

        PaintBrushInspector inspector = new(document.Root, tool);

        var pickers = Every<SegmentedControl>(inspector.Root);
        var curve = pickers.Single(picker =>
            picker.Segments.Any(segment =>
                string.Equals(segment.Value, nameof(BrushFalloffKind.Spherical), StringComparison.Ordinal)));

        Assert.Equal(
            Enum.GetValues<BrushFalloffKind>().Select(kind => kind.ToString()),
            curve.Segments.Select(segment => segment.Value)
        );

        curve.Value = nameof(BrushFalloffKind.Tip);

        Assert.Equal(BrushFalloffKind.Tip, tool.Brush.Curve);
    }

    /// <summary>The verb is registered, toggles the mode, and goes when the module does.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own roll call rather than a line added to <c>TexturingModuleTests</c>'.</b> That
    ///     file's register-and-unregister pair is another slice's this batch; naming this command
    ///     here keeps the wiring gated without two agents editing one list. It should be folded in.
    /// </remarks>
    [Fact]
    public void The_paint_verb_is_registered_toggles_the_mode_and_is_taken_out_on_unload() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.NotNull(fixture.Shell.Commands[TexturingModule.PaintCommand]);

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        var mode = Every<SegmentedControl>(panel).First(control =>
            control.Segments.Any(segment =>
                string.Equals(segment.Value, nameof(PaintToolMode.Paint), StringComparison.Ordinal)));

        Assert.Equal(nameof(PaintToolMode.Select), mode.Value);
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        // ⚠ The control follows the verb, which is the half a push-only wiring gets wrong: the menu
        // and the keymap write the model, and a control that only ever wrote it keeps showing what
        // was last clicked.
        Assert.Equal(nameof(PaintToolMode.Paint), mode.Value);

        // And the artist is told, because a mode nothing drives yet is a drag that does nothing.
        Assert.NotEmpty(fixture.Shell.Notifications.History);

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));
        Assert.Null(fixture.Shell.Commands[TexturingModule.PaintCommand]);
    }

    /// <summary>A host that publishes no brush gets a panel with no brush column.</summary>
    /// <remarks>
    ///     The instrument for the test above: the column is there because the module handed one over,
    ///     not because <c>LayerStackView</c> always builds it.
    /// </remarks>
    [Fact]
    public void A_view_built_with_no_tool_has_no_brush_column() {
        using UiDocument document = new(1280f, 800f);

        LayerStackView view = new(document.Root);

        Assert.Null(view.Brush);
    }

    static List<T> Every<T>(UiElement element) where T : UiElement {
        List<T> found = [];

        Walk(element);

        return found;

        void Walk(UiElement node) {
            if (node is T match) {
                found.Add(match);
            }

            foreach (var child in node.Children) {
                Walk(child);
            }
        }
    }
}
