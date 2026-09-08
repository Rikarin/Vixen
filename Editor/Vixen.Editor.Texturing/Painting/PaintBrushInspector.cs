// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Terrain;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>The brush's settings, as a column of rows beside the layer stack.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § M9's brush surface, less the viewport.</b> The tool mode and the seven
///         settings a stroke reads are here; turning a pointer into texels, a screen radius into
///         texels and a mirror plane into a second hit is § D13's two front ends and is not.
///         <see cref="PaintSession" />'s remarks say precisely what those owe.
///     </para>
///     <para>
///         ⚠ <b>Its own type rather than more of <c>LayerStackView</c>, and the reason is the
///         panel.</b> That file is a list of rows and a preview pane; a brush inspector is neither,
///         and folding it in would make one class that grows on two axes. It also keeps this whole
///         surface in <c>Painting/</c>, beside the model it drives.
///     </para>
///     <para>
///         ⚠ <b>Built in C# rather than <c>.vxml</c>, and that is <c>LayerStackView</c>'s debt
///         rather than a new one.</b> Doc 36 § P4 makes markup the authoring path and
///         <c>TerrainBrushInspector.vxml</c> is the worked example — <c>PropertyField</c> against an
///         <c>[Inspector]</c>-annotated settings object, with the reset button and the undo arriving
///         free. ⚠ The obstacle here is specific and is written down in this project's csproj: a
///         plugin's own entry assembly must not declare a <c>[DataContract]</c>, because
///         <c>PluginLoadContext</c> loads it twice and the second registration is refused with
///         "Both 'X' and 'X' claim the name". The settings object that path wants is exactly such a
///         declaration. <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>.
///     </para>
///     <para>
///         ⚠ <b>Every control writes through <see cref="PaintTool" /> and none of them writes a
///         <c>PaintBrush</c> field.</b> That is where the clamping is, so a slider whose range
///         somebody widens cannot produce a brush the stroke would have to defend itself against —
///         and a NaN out of a half-typed field cannot become a radius that makes every weight a NaN.
///     </para>
/// </remarks>
sealed class PaintBrushInspector {
    /// <summary>What a row says when it is showing a number nothing downstream reads.</summary>
    const string Inert = " — a round brush ignores it";

    readonly PaintTool tool;
    readonly UiElement root;
    readonly UiElement summary;
    readonly SegmentedControl mode;
    readonly List<Readout> readouts = [];
    readonly List<(SegmentedControl Picker, Func<string> Value)> pickers = [];

    /// <summary>
    ///     ⚠ <b>Set while <see cref="Refresh" /> is writing the controls, and read by every handler
    ///     that writes the tool.</b> A control and a model that follow each other are a loop: a
    ///     refresh that sets a slider raises its changed event, which sets the tool, which refreshes.
    ///     It settles by itself where the value round-trips exactly and does not where a clamp moves
    ///     it — a radius pushed past the maximum is the case — so the flag is what makes "the panel
    ///     is writing" distinguishable from "a person is".
    /// </summary>
    bool syncing;

    /// <summary>One captioned row: what it says, what it is worth, and the controls showing it.</summary>
    /// <param name="Caption">The words above the control.</param>
    /// <param name="Label">What the row is called, without its value.</param>
    /// <param name="Value">How the value reads.</param>
    /// <param name="Current">What the value is, for the controls that have to follow it.</param>
    /// <param name="Slider">The slider.</param>
    /// <param name="Box">The number beside it, where the range is too wide for a slider alone.</param>
    readonly record struct Readout(
        UiElement Caption,
        string Label,
        Func<string> Value,
        Func<float> Current,
        Slider Slider,
        NumericInput? Box
    );

    /// <summary>Builds the inspector into a host element.</summary>
    /// <param name="host">Where the column goes.</param>
    /// <param name="tool">The brush it edits. Held, not copied.</param>
    /// <exception cref="ArgumentNullException">Either is null.</exception>
    public PaintBrushInspector(UiElement host, PaintTool tool) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tool);

        this.tool = tool;

        root = host.Add("paint-brush");

        root.SetStyle("display", "flex");
        root.SetStyle("flex-direction", "column");
        root.SetStyle("width", "220px");

        var title = root.Add("world-title");

        title.Text = "Brush";

        // ⚠ The mode first and as a segmented control rather than a checkbox, because "off" is a
        // real tool rather than the absence of one: with the pointer not painting, a drag selects
        // rows and pans the preview, and a control that read "Paint ☐" says nothing about what the
        // other state does.
        mode = root.Add<SegmentedControl>();

        mode.AddSegment(nameof(PaintToolMode.Select), "Select");
        mode.AddSegment(nameof(PaintToolMode.Paint), "Paint");
        mode.Value = tool.Mode.ToString();
        mode.ValueChanged += (_, value) => {
            if (syncing) {
                return;
            }

            tool.Mode = string.Equals(value, nameof(PaintToolMode.Paint), StringComparison.Ordinal)
                ? PaintToolMode.Paint
                : PaintToolMode.Select;

            Refresh();
        };

        summary = root.Add("paint-brush-summary");

        // ⚠ The one row with a number beside its slider, and the range is why. Half a texel to five
        // hundred and twelve over a 220-pixel column is better than two texels a pixel: an artist
        // who wants 32 cannot hit it, and 32 is the brush they reach for most. Every other setting
        // here is a fraction over a unit interval, where a slider is the better control and a field
        // would be four keystrokes for something worth dragging.
        Row("Radius", PaintTool.MinimumRadius, PaintTool.MaximumRadius, () => tool.Brush.Radius,
            tool.SetRadius, () => tool.Brush.Radius.ToString("0.#", CultureInfo.InvariantCulture) + " px",
            typed: true);

        Picker("Curve", Enum.GetValues<BrushFalloffKind>().Select(curve => curve.ToString()),
            () => tool.Brush.Curve.ToString(),
            value => {
                if (Enum.TryParse<BrushFalloffKind>(value, out var chosen)) {
                    tool.SetCurve(chosen);
                }
            });

        Row("Falloff", 0f, 1f, () => tool.Brush.Falloff, tool.SetFalloff, () => Percent(tool.Brush.Falloff));
        Row("Flow", 0f, 1f, () => tool.Brush.Flow, tool.SetFlow, () => Percent(tool.Brush.Flow));
        Row("Opacity", 0f, 1f, () => tool.Brush.Opacity, tool.SetOpacity, () => Percent(tool.Brush.Opacity));
        Row("Spacing", 0.01f, 2f, () => tool.Brush.Spacing, tool.SetSpacing, () => Percent(tool.Brush.Spacing));
        Row("Smoothing", 0f, 0.999f, () => tool.Smoothing, tool.SetSmoothing, () => Percent(tool.Smoothing));

        Stamp();

        var jitter = root.Add("world-title");

        jitter.Text = "Jitter";

        Row("Position", 0f, 1f, () => tool.Brush.PositionJitter, tool.SetPositionJitter,
            () => Percent(tool.Brush.PositionJitter));

        Row("Angle jitter", 0f, 180f, () => tool.AngleJitterDegrees, tool.SetAngleJitter,
            () => tool.AngleJitterDegrees.ToString("0", CultureInfo.InvariantCulture) + "°");

        Row("Size", 0f, 1f, () => tool.Brush.SizeJitter, tool.SetSizeJitter, () => Percent(tool.Brush.SizeJitter));

        Refresh();
    }

    /// <summary>Everything this built, for a caller that has to hide or show the column.</summary>
    public UiElement Root => root;

    /// <summary>The brush this edits.</summary>
    public PaintTool Tool => tool;

    /// <summary>What the heading under the mode reads.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>Every row's caption, as a test reads them instead of walking the tree.</summary>
    public IReadOnlyList<string> Captions {
        get {
            var lines = new List<string>(readouts.Count);

            foreach (var readout in readouts) {
                lines.Add(readout.Caption.Text ?? string.Empty);
            }

            return lines;
        }
    }

    /// <summary>Re-reads the tool, after a change made anywhere but a slider.</summary>
    /// <remarks>
    ///     ⚠ <b>A command binding and a slider are two writers of one model, and this is the pull the
    ///     second needs.</b> <c>TexturingModule</c>'s verb toggles the mode from a menu or a keymap;
    ///     without this the segmented control would keep showing what the artist last clicked, which
    ///     is the state the panel is in exactly when they used the shortcut instead.
    /// </remarks>
    public void Refresh() {
        if (syncing) {
            return;
        }

        syncing = true;

        try {
            mode.Value = tool.Mode.ToString();
            summary.Text = tool.IsPainting
                ? tool.Describe()
                : "Not painting — a drag selects and pans. " + tool.Describe();

            foreach (var (caption, label, value, current, slider, box) in readouts) {
                caption.Text = label + " — " + value();

                // ⚠ The controls follow the model and not only the other way round, which is the
                // half a push-only wiring gets wrong — the same defect the segmented control above
                // was fixed for. A radius set by the verb, by a preset, or by this tool's own clamp
                // left the slider showing what the artist last dragged it to.
                slider.Value = Math.Clamp(current(), slider.Minimum, slider.Maximum);

                if (box is not null) {
                    box.Number = current();
                }
            }

            foreach (var (picker, value) in pickers) {
                picker.Value = value();
            }
        } finally {
            syncing = false;
        }
    }

    static string Percent(float value) =>
        (value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The alpha and the two rotation settings that only mean anything with one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One section rather than three settings, because they are one feature —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1083">#1083</a>.</b>
    ///         <c>PaintBrush.KernelFor</c> picks <c>BrushShape.Circle</c> for a null alpha and
    ///         <c>TerrainBrush.WeightAt</c> turns nothing about a disc, so an angle shipped without a
    ///         mask to choose would be a control that drags and changes no texel — which reads as a
    ///         working setting and is worse than an absent one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the two rotation rows say when they are inert rather than being hidden.</b>
    ///         <c>TerrainBrushSettings</c> hides its angle behind a <c>[ShowIf]</c>; a row that
    ///         vanishes leaves an artist looking for a setting they remember, and the reason it went
    ///         is exactly the thing worth telling them. <see cref="PaintTool.IsMasked" /> and
    ///         <see cref="PaintTool.IsAngled" /> are the predicates, on the model where a test can
    ///         reach them.
    ///     </para>
    /// </remarks>
    void Stamp() {
        var title = root.Add("world-title");

        title.Text = "Stamp";

        // The names from the shelf rather than a second list here, for the curve picker's reason: a
        // fifth alpha appears without an edit to this file.
        Picker("Alpha", PaintAlphas.Names, () => tool.AlphaName, tool.SetAlpha);

        Picker("Rotation", Enum.GetValues<BrushRotation>().Select(rotation => rotation.ToString()),
            () => tool.Brush.Rotation.ToString(),
            value => {
                if (Enum.TryParse<BrushRotation>(value, out var chosen)) {
                    tool.SetRotation(chosen);
                }
            });

        Row("Angle", 0f, 360f, () => tool.AngleDegrees, tool.SetAngle,
            () => tool.AngleDegrees.ToString("0", CultureInfo.InvariantCulture) + "°"
                + (tool.IsAngled ? "" : Inert));
    }

    /// <summary>One captioned row of buttons, writing through the tool.</summary>
    /// <param name="label">The caption.</param>
    /// <param name="segments">What may be chosen.</param>
    /// <param name="current">Which is chosen, re-read on every <see cref="Refresh" />.</param>
    /// <param name="set">What choosing one does.</param>
    void Picker(string label, IEnumerable<string> segments, Func<string> current, Action<string> set) {
        var caption = root.Add("paint-brush-caption");

        caption.Text = label;

        var picker = root.Add<SegmentedControl>();

        foreach (var segment in segments) {
            picker.AddSegment(segment);
        }

        picker.Value = current();
        picker.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);

        picker.ValueChanged += (_, value) => {
            if (syncing || value is null) {
                return;
            }

            set(value);
            Refresh();
        };

        pickers.Add((picker, current));
    }

    /// <summary>One captioned slider, writing through the tool.</summary>
    /// <param name="label">The caption, without its value.</param>
    /// <param name="minimum">The slider's low end.</param>
    /// <param name="maximum">Its high end.</param>
    /// <param name="current">What the setting is worth, re-read on every <see cref="Refresh" />.</param>
    /// <param name="set">What moving the control does.</param>
    /// <param name="read">How the value reads in the caption.</param>
    /// <param name="typed">Whether a number goes beside the slider, for a range a slider cannot hit.</param>
    void Row(
        string label,
        float minimum,
        float maximum,
        Func<float> current,
        Action<float> set,
        Func<string> read,
        bool typed = false
    ) {
        var caption = root.Add("paint-brush-caption");
        var slider = root.Add<Slider>();

        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.Value = Math.Clamp(current(), minimum, maximum);

        // ⚠ The caption is the slider's accessible name and the relation is what says so.
        // `ColorPicker`'s own remark: a slider beside words it is not related to announces nothing.
        slider.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);

        slider.ValueChanged += (_, changed) => {
            if (syncing) {
                return;
            }

            set(changed);
            Refresh();
        };

        NumericInput? box = null;

        if (typed) {
            box = root.Add<NumericInput>();

            box.Minimum = minimum;
            box.Maximum = maximum;
            box.Decimals = 1;
            box.Number = current();
            box.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);

            box.NumberChanged += (_, changed) => {
                if (syncing) {
                    return;
                }

                set((float)changed);
                Refresh();
            };
        }

        readouts.Add(new(caption, label, read, current, slider, box));
    }
}
