// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>What the renderer did last frame, as numbers only it can know.</summary>
/// <remarks>
///     <para>
///         Pushed in by the host rather than read out of a renderer, because <c>Vixen.Engine</c> does
///         not reference one — deliberately, so that a game's simulation layer never drags a graphics
///         API behind it. The host sets this once a frame; the overlay reads it and formats it.
///     </para>
///     <para>
///         ⚠ <b><see cref="GpuMilliseconds" /> is several frames old and is presented as such.</b> A
///         timestamp query is read back with its frame's fence, so a GPU figure that claims to be
///         this frame's is lying — see [13](../../../../docs/plan/13-diagnostics.md) § GPU. Zero
///         means "nobody measured", which the overlay draws as a dash rather than as a very fast
///         frame.
///     </para>
/// </remarks>
public readonly record struct FrameStatistics {
    /// <summary>How long the GPU took, or zero if nothing measured it.</summary>
    public double GpuMilliseconds { get; init; }

    /// <summary>How many frames back <see cref="GpuMilliseconds" /> was actually measured.</summary>
    public int GpuLatency { get; init; }

    /// <summary>How many draws were recorded.</summary>
    public int DrawCalls { get; init; }

    /// <summary>How many triangles those draws asked for.</summary>
    public long Triangles { get; init; }

    /// <summary>How many render objects survived culling.</summary>
    public int Visible { get; init; }

    /// <summary>How many did not.</summary>
    public int Culled { get; init; }

    /// <summary>How much GPU memory is in use, or zero if the backend cannot say.</summary>
    public long GpuBytes { get; init; }
}

/// <summary>
///     The frame-stats overlay: rate, the CPU/GPU split, what was drawn, and what memory is doing.
/// </summary>
/// <remarks>
///     <para>
///         The one overlay worth leaving on, and therefore the one that has to be cheap enough to
///         leave on. It allocates nothing per frame: every number is formatted into a stack buffer
///         and the history behind the graph is a fixed array written round.
///     </para>
///     <para>
///         <b>Frame time is graphed, not only printed.</b> A mean of 16.7 ms and a mean of 16.7 ms
///         with a 60 ms spike every second are the same number and completely different games, and
///         the spike is the one somebody is complaining about. The graph is the last
///         <see cref="HistoryLength" /> frames, unsmoothed, with the budget drawn across it.
///     </para>
/// </remarks>
public sealed class FrameStatsOverlay : IDiagnosticOverlay {
    /// <summary>How many frames the graph remembers.</summary>
    public const int HistoryLength = 120;

    const int Rows = 9;

    readonly float[] history = new float[HistoryLength];

    int written;
    int next;

    /// <inheritdoc />
    public string Name => "stats";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 190f;

    /// <summary>What the renderer reported. The host sets this once a frame.</summary>
    public FrameStatistics Statistics { get; set; }

    /// <summary>The smoothed frame time, in milliseconds.</summary>
    /// <remarks>
    ///     Smoothed because an unsmoothed readout at sixty hertz is a number nobody can read. The
    ///     graph underneath is not smoothed, so the spikes the average hides are still visible.
    /// </remarks>
    public float SmoothedMilliseconds { get; private set; }

    /// <summary>The worst frame in the graph's window, in milliseconds.</summary>
    public float PeakMilliseconds { get; private set; }

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        Record(time);

        var theme = surface.Theme;
        var graphHeight = surface.RowHeight * 2.6f;

        // ⚠ The loose-content stamp used to take a row here and does not any more. It is a standing
        // notice on `DiagnosticOverlays` now, because a statement about the build that lives on a
        // panel is erased by every one of the five ways a panel can be taken off the screen — see
        // `DiagnosticOverlays.Notice`.
        var region = surface.Panel(Anchor, Width, surface.HeightForRows(Rows) + graphHeight, "FRAME");

        if (region.IsEmpty) {
            return;
        }

        Span<char> buffer = stackalloc char[48];
        var statistics = Statistics;

        var fps = SmoothedMilliseconds > 0f ? 1000f / SmoothedMilliseconds : 0f;
        var health = Health(SmoothedMilliseconds, theme);

        region.Text(0, "fps", theme.Text);

        if (buffer.TryWrite($"{fps,6:F1}", out var length)) {
            region.TextRight(0, buffer[..length], health);
        }

        region.Text(1, "cpu", theme.Text);

        if (buffer.TryWrite($"{SmoothedMilliseconds,6:F2} ms", out length)) {
            region.TextRight(1, buffer[..length], health);
        }

        region.Text(2, "gpu", theme.Text);

        // ⚠ The read-back lag is part of the reading rather than a footnote. A GPU time labelled as
        // this frame's is wrong, and somebody chasing a stutter will pair it with the wrong CPU
        // frame. A backend that measured nothing gets a dash, which is not the same as a fast frame.
        if (statistics.GpuMilliseconds <= 0d) {
            region.TextRight(2, "-", theme.Muted);
        } else if (buffer.TryWrite($"{statistics.GpuMilliseconds,6:F2} ms -{statistics.GpuLatency}", out length)) {
            region.TextRight(2, buffer[..length], Health((float) statistics.GpuMilliseconds, theme));
        }

        region.Text(3, "peak", theme.Text);

        if (buffer.TryWrite($"{PeakMilliseconds,6:F2} ms", out length)) {
            region.TextRight(3, buffer[..length], Health(PeakMilliseconds, theme));
        }

        region.Text(4, "draws", theme.Text);

        if (buffer.TryWrite($"{statistics.DrawCalls,6}", out length)) {
            region.TextRight(4, buffer[..length], theme.Text);
        }

        region.Text(5, "tris", theme.Text);
        region.TextRight(5, Metric(statistics.Triangles, buffer), theme.Text);

        // ⚠ Visible against the total submitted, not against the culled count on its own. "4 000
        // culled" is meaningless without knowing whether four thousand or four hundred thousand were
        // considered, and the ratio is the thing anybody looking at this is actually asking about.
        region.Text(6, "visible", theme.Text);

        if (buffer.TryWrite($"{statistics.Visible,6}/{statistics.Visible + statistics.Culled}", out length)) {
            region.TextRight(6, buffer[..length], theme.Text);
        }

        region.Text(7, "managed", theme.Text);

        if (buffer.TryWrite($"{GC.GetTotalMemory(false) / (1024d * 1024d),6:F1} MB", out length)) {
            region.TextRight(7, buffer[..length], theme.Text);
        }

        region.Text(8, "gpu mem", theme.Text);

        // A dash rather than a zero, for the same reason the GPU time gets one: several backends
        // cannot report residency at all, and "0 MB" reads as an answer.
        if (statistics.GpuBytes <= 0) {
            region.TextRight(8, "-", theme.Muted);
        } else if (buffer.TryWrite($"{statistics.GpuBytes / (1024d * 1024d),6:F1} MB", out length)) {
            region.TextRight(8, buffer[..length], theme.Text);
        }

        Graph(region, graphHeight, theme, Rows);
    }

    /// <summary>Forgets the graph's history and the running average.</summary>
    public void Reset() {
        Array.Clear(history);

        written = 0;
        next = 0;
        SmoothedMilliseconds = 0f;
        PeakMilliseconds = 0f;
    }

    void Record(in GameTime time) {
        // The unscaled delta, because a paused or slowed game still takes real milliseconds to draw:
        // the frame-time readout is about the machine, not about the simulation.
        var milliseconds = (float) time.UnscaledElapsed.TotalMilliseconds;

        history[next] = milliseconds;
        next = (next + 1) % HistoryLength;
        written = Math.Min(written + 1, HistoryLength);

        // A quarter-weight exponential average: settled enough to read, quick enough that a stall
        // shows up while it is still happening.
        SmoothedMilliseconds = SmoothedMilliseconds <= 0f
            ? milliseconds
            : SmoothedMilliseconds + ((milliseconds - SmoothedMilliseconds) * 0.25f);

        var peak = 0f;

        for (var index = 0; index < written; index++) {
            peak = MathF.Max(peak, history[index]);
        }

        PeakMilliseconds = peak;
    }

    void Graph(in OverlayRegion region, float height, in OverlayTheme theme, int rows) {
        var top = rows * region.RowHeight;
        var width = region.ContentWidth;

        region.Rect(new(0f, top), new(width, height), OverlayTheme.Fade(theme.Border, 0.4f));

        if (written == 0) {
            return;
        }

        // ⚠ Scaled to twice the budget, or to the peak if that is worse — not to the peak alone. A
        // graph that always rescales to its own maximum looks identical whether the frame time is
        // 2 ms or 200; pinning the budget to the middle is what makes the shape mean something.
        var budget = 1000f / 60f;
        var scale = MathF.Max(budget * 2f, PeakMilliseconds);

        if (scale <= 0f) {
            return;
        }

        var budgetY = top + height - (budget / scale * height);
        region.Line(new(0f, budgetY), new(width, budgetY), OverlayTheme.Fade(theme.Warning, 0.45f));

        var step = width / HistoryLength;

        for (var index = 0; index < written; index++) {
            // Oldest on the left. `next` is where the following sample goes, which is also the oldest
            // entry once the ring has wrapped.
            var slot = written < HistoryLength ? index : (next + index) % HistoryLength;
            var value = history[slot];
            var bar = MathF.Min(value / scale, 1f) * height;

            if (bar > 0f) {
                var x = index * step;
                region.Line(new(x, top + height), new(x, top + height - bar), Health(value, theme));
            }
        }
    }

    static Color4 Health(float milliseconds, in OverlayTheme theme) =>
        milliseconds <= 0f
            ? theme.Muted
            : milliseconds <= 1000f / 60f
                ? theme.Good
                : milliseconds <= 1000f / 30f
                    ? theme.Warning
                    : theme.Bad;

    /// <summary>Formats a count with a metric suffix, so a million triangles is not seven digits.</summary>
    static ReadOnlySpan<char> Metric(long value, Span<char> buffer) {
        var length = 0;

        var ok = value switch {
            >= 1_000_000_000 => buffer.TryWrite($"{value / 1_000_000_000d,6:F2} G", out length),
            >= 1_000_000 => buffer.TryWrite($"{value / 1_000_000d,6:F2} M", out length),
            >= 1_000 => buffer.TryWrite($"{value / 1_000d,6:F2} k", out length),
            _ => buffer.TryWrite($"{value,6}", out length)
        };

        return ok ? buffer[..length] : default;
    }
}
