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
///         declaration. <a href="https://github.com/Rikarin/Vixen/issues/874">#874</a>.
///     </para>
///     <para>
///         ⚠ <b>Every control writes through <see cref="PaintTool" /> and none of them writes a
///         <c>PaintBrush</c> field.</b> That is where the clamping is, so a slider whose range
///         somebody widens cannot produce a brush the stroke would have to defend itself against —
///         and a NaN out of a half-typed field cannot become a radius that makes every weight a NaN.
///     </para>
/// </remarks>
sealed class PaintBrushInspector {
    readonly PaintTool tool;
    readonly UiElement root;
    readonly UiElement summary;
    readonly SegmentedControl mode;
    readonly List<(UiElement Caption, string Label, Func<string> Value)> readouts = [];

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
            tool.Mode = string.Equals(value, nameof(PaintToolMode.Paint), StringComparison.Ordinal)
                ? PaintToolMode.Paint
                : PaintToolMode.Select;

            Refresh();
        };

        summary = root.Add("paint-brush-summary");

        Row("Radius", PaintTool.MinimumRadius, PaintTool.MaximumRadius, tool.Brush.Radius, tool.SetRadius,
            () => tool.Brush.Radius.ToString("0.#", CultureInfo.InvariantCulture) + " px");

        Curve();

        Row("Falloff", 0f, 1f, tool.Brush.Falloff, tool.SetFalloff, () => Percent(tool.Brush.Falloff));
        Row("Flow", 0f, 1f, tool.Brush.Flow, tool.SetFlow, () => Percent(tool.Brush.Flow));
        Row("Opacity", 0f, 1f, tool.Brush.Opacity, tool.SetOpacity, () => Percent(tool.Brush.Opacity));
        Row("Spacing", 0.01f, 2f, tool.Brush.Spacing, tool.SetSpacing, () => Percent(tool.Brush.Spacing));
        Row("Smoothing", 0f, 0.999f, tool.Smoothing, tool.SetSmoothing, () => Percent(tool.Smoothing));

        var jitter = root.Add("world-title");

        jitter.Text = "Jitter";

        Row("Position", 0f, 1f, tool.Brush.PositionJitter, tool.SetPositionJitter,
            () => Percent(tool.Brush.PositionJitter));

        Row("Angle", 0f, 180f, tool.AngleJitterDegrees, tool.SetAngleJitter,
            () => tool.AngleJitterDegrees.ToString("0", CultureInfo.InvariantCulture) + "°");

        Row("Size", 0f, 1f, tool.Brush.SizeJitter, tool.SetSizeJitter, () => Percent(tool.Brush.SizeJitter));

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

            foreach (var (caption, _, _) in readouts) {
                lines.Add(caption.Text ?? string.Empty);
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
        mode.Value = tool.Mode.ToString();
        summary.Text = tool.IsPainting
            ? tool.Describe()
            : "Not painting — a drag selects and pans. " + tool.Describe();

        foreach (var (caption, label, value) in readouts) {
            caption.Text = label + " — " + value();
        }
    }

    static string Percent(float value) =>
        (value * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The four falloff curves, as a row of buttons.</summary>
    /// <remarks>
    ///     Named from the enum rather than from a list here, so a fifth curve in
    ///     <c>Vixen.Terrain</c> appears without an edit — the same rule the mask sources follow, and
    ///     the reason <c>LayerStackGraph</c> refuses to keep a second list of anything the terrain
    ///     assembly already declares.
    /// </remarks>
    void Curve() {
        var caption = root.Add("paint-brush-caption");

        caption.Text = "Curve";

        var picker = root.Add<SegmentedControl>();

        foreach (var curve in Enum.GetValues<BrushFalloffKind>()) {
            picker.AddSegment(curve.ToString());
        }

        picker.Value = tool.Brush.Curve.ToString();
        picker.ValueChanged += (_, value) => {
            if (Enum.TryParse<BrushFalloffKind>(value, out var chosen)) {
                tool.SetCurve(chosen);
                Refresh();
            }
        };
    }

    /// <summary>One captioned slider, writing through the tool.</summary>
    void Row(string label, float minimum, float maximum, float value, Action<float> set, Func<string> read) {
        var caption = root.Add("paint-brush-caption");
        var slider = root.Add<Slider>();

        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.Value = Math.Clamp(value, minimum, maximum);

        // ⚠ The caption is the slider's accessible name and the relation is what says so.
        // `ColorPicker`'s own remark: a slider beside words it is not related to announces nothing.
        slider.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);

        slider.ValueChanged += (_, changed) => {
            set(changed);
            Refresh();
        };

        readouts.Add((caption, label, read));
    }
}
