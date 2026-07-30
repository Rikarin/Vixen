// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Profiler;

/// <summary>The CPU profiler panel: a source, a capture, a flame chart and a table.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E4 in one panel, and its exit criterion is the first thing the strip
///         says.</b> "A frame of the editor and a frame of a running game are both profilable in the
///         same panel" is a dropdown — the panel has no idea which process it is looking at, which is
///         what <see cref="IProfileSource" /> is for.
///     </para>
///     <para>
///         ⚠ <b>The chart and the table answer different questions and both are here.</b> A flame
///         chart shows one frame's <i>shape</i>; a table shows which scope costs the most across the
///         whole capture. Shipping only the chart is the mistake that makes a profiler pretty and
///         useless at any capture longer than a frame.
///     </para>
///     <para>
///         ⚠ <b>The table is rebuilt on change rather than every frame.</b> A capture's summary is
///         fixed the moment the capture is — nothing about it moves while it is on screen — so the
///         only thing that ticks here is the model's drain of the rings.
///     </para>
/// </remarks>
public sealed partial class ProfilerView : Control {
    readonly Dictionary<string, IProfileSource> byName = new(StringComparer.Ordinal);

    Action<ProfilerModel>? onChanged;
    ProfilerModel? model;

    /// <inheritdoc />
    protected override string TagName => "profiler-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What it is showing.</summary>
    public ProfilerModel? Model => model;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>Which process is being profiled.</summary>
    public Select Sources { get; private set; } = null!;

    /// <summary>Starts and stops a capture.</summary>
    public Button Record { get; private set; } = null!;

    /// <summary>Makes the current capture the one later ones are compared with.</summary>
    public Button Baseline { get; private set; } = null!;

    /// <summary>Which thread's chart is on screen.</summary>
    public Select Threads { get; private set; } = null!;

    /// <summary>Which frame, or all of them.</summary>
    public Select Frames { get; private set; } = null!;

    /// <summary>The line under the strip saying what the capture holds.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>The chart.</summary>
    public FlameChartView Chart { get; private set; } = null!;

    /// <summary>The table under it.</summary>
    public DataGrid Table { get; private set; } = null!;

    /// <summary>The table that replaces it once there is a baseline to compare against.</summary>
    /// <remarks>
    ///     ⚠ <b>A second grid rather than the first one's columns rebuilt, because a
    ///     <see cref="DataGrid" /> has no way to take a column back off.</b> That is not a workaround
    ///     for a gap: a comparison is a different table — different columns, different sort, a
    ///     different question — and one grid that mutated between the two would lose the column
    ///     widths somebody had dragged every time they pressed the button.
    /// </remarks>
    public DataGrid Comparison { get; private set; } = null!;

    /// <summary>What the selected bar says about itself.</summary>
    public UiElement Detail { get; private set; } = null!;

    /// <summary>Points the panel at a model.</summary>
    /// <param name="profiler">What to show.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profiler" /> is null.</exception>
    public void Show(ProfilerModel profiler) {
        ArgumentNullException.ThrowIfNull(profiler);

        if (model is not null && onChanged is not null) {
            model.Changed -= onChanged;
        }

        model = profiler;
        onChanged ??= _ => Restate();

        profiler.Changed += onChanged;
        Restate();
    }

    /// <summary>Drains the rings, if a capture is running.</summary>
    /// <param name="now">Unused; taken so the shell can drive this like everything else it ticks.</param>
    public void Tick(TimeSpan now = default) {
        _ = now;
        model?.Tick();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("profiler-toolbar");

        Sources = Toolbar.Add<Select>();
        Sources.Size = ControlSize.Small;
        Sources.Placeholder = "Source";

        Sources.SelectionChanged += (_, value) => {
            if (model is not null && value is not null && byName.TryGetValue(value, out var source)) {
                model.Selected = source;
            }
        };

        Record = Toolbar.Add<Button>();
        Record.Size = ControlSize.Small;
        Record.Label = "Record";
        Record.Clicked += _ => Toggle();

        Baseline = Toolbar.Add<Button>();
        Baseline.Size = ControlSize.Small;
        Baseline.Variant = ControlVariant.Subtle;
        Baseline.Label = "Set Baseline";

        Baseline.Clicked += _ => {
            if (model is null) {
                return;
            }

            if (model.Baseline is null) {
                model.MarkBaseline();
            } else {
                model.ClearBaseline();
            }
        };

        Threads = Toolbar.Add<Select>();
        Threads.Size = ControlSize.Small;
        Threads.Placeholder = "Thread";

        Threads.SelectionChanged += (_, value) => {
            if (model is not null && int.TryParse(value, CultureInfo.InvariantCulture, out var index)) {
                model.Thread = index;
                ShowChart();
            }
        };

        Frames = Toolbar.Add<Select>();
        Frames.Size = ControlSize.Small;
        Frames.Placeholder = "Frame";

        Frames.SelectionChanged += (_, value) => {
            if (model is null) {
                return;
            }

            model.Frame = int.TryParse(value, CultureInfo.InvariantCulture, out var frame) ? frame : null;
            ShowChart();
        };

        Status = Part("profiler-status");

        var body = Part("profiler-body");

        var scroller = body.Add<ScrollView>();
        Chart = scroller.Content.Add<FlameChartView>();
        Chart.Chosen += (_, node) => Describe(node);

        Detail = Part("profiler-detail");

        Table = Part<DataGrid>();
        Table.AddColumn("Scope", item => ((ProfileEntry)item).Name).Width = 260f;
        Table.AddColumn("Calls", item => Number(((ProfileEntry)item).Calls));
        Table.AddColumn("Total ms", item => Number(((ProfileEntry)item).TotalMilliseconds));
        Table.AddColumn("Self ms", item => Number(((ProfileEntry)item).SelfMilliseconds));
        Table.AddColumn("Mean ms", item => Number(((ProfileEntry)item).MeanMilliseconds));
        Table.AddColumn("Max ms", item => Number(((ProfileEntry)item).MaximumMilliseconds));

        Comparison = Part<DataGrid>();
        Comparison.AddColumn("Scope", item => ((ProfileDelta)item).Name).Width = 260f;
        Comparison.AddColumn("Δ ms/frame", item => Number(((ProfileDelta)item).TotalDelta, sign: true));
        Comparison.AddColumn("Δ %", item => Percentage(((ProfileDelta)item).Ratio));
        Comparison.AddColumn("Δ calls", item => Number(((ProfileDelta)item).CallsDelta, sign: true));

        Restate();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (model is not null && onChanged is not null) {
            model.Changed -= onChanged;
        }

        base.OnRemoved();
    }

    void Toggle() {
        if (model is null) {
            return;
        }

        if (model.State == ProfilerState.Recording) {
            model.Stop();
        } else {
            model.Start();
        }
    }

    void Restate() {
        if (model is not { } profiler) {
            return;
        }

        byName.Clear();

        for (var index = Sources.Options.Count; index < profiler.Sources.Count; index++) {
            Sources.AddOption(profiler.Sources[index].Name, profiler.Sources[index].Name);
        }

        foreach (var source in profiler.Sources) {
            byName[source.Name] = source;
        }

        if (profiler.Selected is { } selected) {
            Sources.Value = selected.Name;
        }

        var recording = profiler.State == ProfilerState.Recording;

        Record.Label = recording ? "Stop" : "Record";
        Record.Variant = recording ? ControlVariant.Danger : ControlVariant.Primary;

        Baseline.Label = profiler.Baseline is null ? "Set Baseline" : "Clear Baseline";
        Baseline.Disabled = profiler.Baseline is null && profiler.Capture.IsEmpty;

        Status.Text = Describe(profiler);

        Options();

        var comparing = profiler.Baseline is not null;

        Table.SetStyle("display", comparing ? "none" : "flex");
        Comparison.SetStyle("display", comparing ? "flex" : "none");

        if (comparing) {
            Comparison.SetItems(profiler.Deltas.Cast<object>());
        } else {
            Table.SetItems(profiler.Capture.Summary.Cast<object>());
        }

        ShowChart();
    }

    /// <summary>Refills the thread and frame pickers from the capture.</summary>
    /// <remarks>
    ///     Cleared and rebuilt rather than compared, unlike the console's category list: a capture
    ///     replaces the whole set of threads and frames at once, so there is no choice to preserve —
    ///     the frames of the previous capture are not the frames of this one even where the numbers
    ///     coincide.
    /// </remarks>
    void Options() {
        if (model is not { } profiler) {
            return;
        }

        Threads.ClearOptions();

        for (var index = 0; index < profiler.Capture.Threads.Count; index++) {
            var thread = profiler.Capture.Threads[index];

            Threads.AddOption(
                index.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{thread.ThreadName} ({thread.Samples.Length})")
            );
        }

        Threads.Value = profiler.Capture.Threads.Count > 0
            ? Math.Clamp(profiler.Thread, 0, profiler.Capture.Threads.Count - 1).ToString(CultureInfo.InvariantCulture)
            : null;

        Frames.ClearOptions();
        Frames.AddOption(string.Empty, "All frames");

        // ⚠ Capped. A capture of ten thousand frames is a legitimate thing to take, and a dropdown
        // with ten thousand entries in it is not a control — the chart's zoom is how somebody reaches
        // a frame past this, and the table does not care.
        var last = Math.Min(profiler.Capture.LastFrame, profiler.Capture.FirstFrame + MaximumFrameOptions - 1);

        for (var frame = profiler.Capture.FirstFrame; frame <= last; frame++) {
            var text = frame.ToString(CultureInfo.InvariantCulture);
            Frames.AddOption(text, "Frame " + text);
        }

        Frames.Value = profiler.Frame?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    void ShowChart() {
        if (model is { } profiler) {
            Chart.Show(profiler.Roots);
        }

        Detail.Text = Chart.Selected is { } node ? Sentence(node) : "Select a bar to see its numbers.";
    }

    void Describe(FlameNode node) => Detail.Text = Sentence(node);

    static string Sentence(FlameNode node) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{node.Name} — {node.Milliseconds:0.###} ms total, {node.SelfMilliseconds:0.###} ms self, "
            + $"{node.Children.Count} nested scope(s), frame {node.Sample.FrameIndex}"
        );

    static string Describe(ProfilerModel profiler) {
        if (profiler.State == ProfilerState.Recording) {
            return "Recording…";
        }

        if (profiler.Capture.IsEmpty) {
            return profiler.Selected is null
                ? "No profileable source."
                : "Press Record, then Stop. Nothing captured yet.";
        }

        var capture = profiler.Capture;

        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{capture.SampleCount:N0} samples over {capture.FrameCount:N0} frame(s), "
            + $"{capture.DurationMilliseconds:0.#} ms, {capture.Threads.Count} thread(s) — {capture.Source}"
        );

        // Said out loud for the reason the console says it: a capture missing its beginning and a
        // capture where nothing happened look identical and have opposite fixes.
        return capture.Dropped > 0
            ? text + string.Create(CultureInfo.InvariantCulture, $" · {capture.Dropped:N0} sample(s) dropped")
            : text;
    }

    /// <summary>How many frames the picker will list.</summary>
    const int MaximumFrameOptions = 512;

    static string Number(double value, bool sign = false) =>
        value.ToString(sign ? "+0.###;-0.###;0" : "0.###", CultureInfo.InvariantCulture);

    static string Number(int value, bool sign = false) =>
        value.ToString(sign ? "+#,##0;-#,##0;0" : "#,##0", CultureInfo.InvariantCulture);

    static string Percentage(double? ratio) =>
        ratio is { } value ? (value * 100d).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + "%" : "new";
}
