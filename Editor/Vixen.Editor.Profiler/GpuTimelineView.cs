// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Profiler;

/// <summary>A frame's GPU passes, as a bar each.</summary>
/// <remarks>
///     <para>
///         <b>Deliberately not a second flame chart.</b> GPU work is a sequence of passes rather than
///         a call tree — a pass has no caller and nothing runs "inside" another pass in any sense a
///         timestamp can see — so a control that drew rows of nesting would be inventing structure
///         that is not there. What a debug group gives is one level of grouping, which is what
///         <see cref="GpuScope.Level" /> carries.
///     </para>
///     <para>
///         ⚠ <b>It says when the device cannot be timed rather than drawing nothing.</b> Doc 20's B4
///         names timestamp queries as this panel's dependency and several real configurations do not
///         have them — MoltenVK's statistics, GLES, WebGPU without the feature — so an empty chart
///         would be indistinguishable from a frame with no passes in it.
///     </para>
///     <para>
///         ⚠ <b>The frame it shows is several frames old, and the label says which.</b> Reading a
///         query pool the GPU has not finished writing would mean stalling the frame thread on the
///         device once per frame; <see cref="GpuProfiler" /> refuses to, so what is on screen is the
///         oldest submission that has retired.
///     </para>
/// </remarks>
public sealed partial class GpuTimelineView : Control {
    /// <summary>How tall one bar is.</summary>
    public const float RowHeight = 20f;

    /// <summary>How narrow a bar may be before it is not worth an element.</summary>
    public const float MinimumBarWidth = 1.5f;

    readonly List<UiElement> pool = [];

    GpuFrame frame = GpuFrame.Empty;
    Action<UiDocument>? settle;

    /// <summary>What the last realise was for, so an unchanged frame does no work.</summary>
    /// <remarks>
    ///     The same guard <see cref="FlameChartView" /> keeps, and for the same reason — except that
    ///     here the identity is the frame object itself, because a resolved frame is replaced rather
    ///     than edited and a new one is always a different instance.
    /// </remarks>
    (float Width, GpuFrame Frame) realised = (-1f, GpuFrame.Empty);

    /// <inheritdoc />
    protected override string TagName => "gpu-timeline";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The line saying what the frame cost, or why there is nothing to show.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>Where the bars go.</summary>
    public UiElement Lanes { get; private set; } = null!;

    /// <summary>Why the device cannot be timed, or <see langword="null" /> when it can.</summary>
    /// <remarks>
    ///     Set by whoever built the panel, because "this device reports no timestamp queries" is a
    ///     sentence only something holding the device can write.
    /// </remarks>
    public string? Unavailable { get; set; }

    /// <summary>Where the newest resolved frame comes from, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Pulled rather than pushed, and the difference is a class of bug rather than a
    ///     preference.</b> A host that pushed frames into this view would be holding a reference to
    ///     a panel whose factory runs again on every reopen — so the frame after the tab is closed
    ///     goes to an element that has been removed from the document, and reading its bounds
    ///     throws. Pulling means the view is driven only while it is in the tree, because the only
    ///     thing that drives it is its own layout hook.
    /// </remarks>
    public Func<GpuFrame>? Source { get; set; }

    /// <summary>What it is showing.</summary>
    public GpuFrame Frame => frame;

    /// <summary>Puts a frame on screen.</summary>
    /// <param name="resolved">The frame.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolved" /> is null.</exception>
    public void Show(GpuFrame resolved) {
        ArgumentNullException.ThrowIfNull(resolved);

        frame = resolved;
        Realise();
    }

    /// <summary>Rebuilds the bars for the current width.</summary>
    public void Realise() {
        if (Source?.Invoke() is { } latest) {
            frame = latest;
        }

        var width = Lanes.Bounds.Width;

        if (realised.Width == width && ReferenceEquals(realised.Frame, frame)) {
            return;
        }

        realised = (width, frame);

        var slot = 0;
        var deepest = 0;

        if (Unavailable is null && width > 0f) {
            foreach (var scope in frame.Scopes) {
                var left = frame.Fraction(scope.BeginTicks) * width;
                var right = frame.Fraction(scope.EndTicks) * width;
                var span = right - left;

                if (span < MinimumBarWidth) {
                    continue;
                }

                while (pool.Count <= slot) {
                    pool.Add(Lanes.Add<UiElement>("gpu-bar"));
                }

                var bar = pool[slot++];

                for (var hue = 0; hue < FlameChartView.HueCount; hue++) {
                    bar.RemoveClass("flame-hue-" + hue.ToString(CultureInfo.InvariantCulture));
                }

                bar.RemoveClass("parked");
                bar.AddClass("flame-hue-" + FlameChartView.HueOf(scope.Name).ToString(CultureInfo.InvariantCulture));

                bar.SetStyle("left", Length(left));
                bar.SetStyle("top", Length(scope.Level * RowHeight));
                bar.SetStyle("width", Length(span));
                bar.SetStyle("height", Length(RowHeight - 2f));

                bar.Text = span >= 48f
                    ? string.Create(CultureInfo.InvariantCulture, $"{scope.Name} {frame.MillisecondsOf(scope):0.###} ms")
                    : null;

                deepest = Math.Max(deepest, scope.Level);
            }
        }

        for (var index = slot; index < pool.Count; index++) {
            pool[index].AddClass("parked");
        }

        Lanes.SetStyle("height", Length((deepest + 1) * RowHeight));
        Status.Text = Sentence();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Status = Part("gpu-status");
        Lanes = Part("gpu-lanes");

        settle = _ => Realise();
        Document.LayoutFinished += settle;

        Realise();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    string Sentence() {
        if (Unavailable is { } reason) {
            return reason;
        }

        if (frame.Scopes.Count == 0) {
            return "Waiting for the GPU to retire a frame.";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Frame {frame.FrameIndex} — {frame.Milliseconds:0.###} ms across {frame.Scopes.Count} pass(es)"
        );
    }

    static string Length(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";
}
