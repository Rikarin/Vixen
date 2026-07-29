// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>
///     A live mini flame chart of the last frame: one bar per profiler scope, nested by depth.
/// </summary>
/// <remarks>
///     <para>
///         [13](../../../../docs/plan/13-diagnostics.md) § Diagnostic overlays asks for this beside
///         the frame stats, and the two answer different questions: the stats say <i>that</i> a frame
///         was slow, and this says <i>what</i> in it was. Neither is much use without the other,
///         which is why the profiler is compiled in and left on.
///     </para>
///     <para>
///         ⚠ <b>Enabled, this drains the profiler's rings.</b> <see cref="Profiler.Collect" /> is
///         destructive — it is how the rings are emptied — so this overlay and a trace export cannot
///         both be reading at once. Set <see cref="Collects" /> to <see langword="false" /> and feed
///         it with <see cref="Capture" /> when something else owns the collection; that is what a
///         host exporting Perfetto traces has to do.
///     </para>
///     <para>
///         ⚠ <b>Only the deepest thread is charted, and it says which.</b> A flame chart of every
///         worker stacked in one panel is unreadable at overlay size, and picking one arbitrarily
///         would show a different thread each frame. The one with the most samples is the one doing
///         the work, and the header names it so the choice is not silent.
///     </para>
/// </remarks>
public sealed class FrameGraphOverlay : IDiagnosticOverlay {
    /// <summary>How many nested levels are drawn before the rest are folded away.</summary>
    public const int MaxDepth = 8;

    /// <summary>How short a scope can be, as a fraction of the frame, before it is not worth a bar.</summary>
    const float MinimumFraction = 0.002f;

    ProfilerThreadSamples[] samples = [];

    /// <inheritdoc />
    public string Name => "framegraph";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.BottomLeft;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 420f;

    /// <summary>Whether drawing collects from <see cref="Profiler" /> itself.</summary>
    public bool Collects { get; set; } = true;

    /// <summary>How tall one nesting level's bar is, in pixels.</summary>
    public float BarHeight { get; set; } = 11f;

    /// <summary>How long the charted frame took, in milliseconds.</summary>
    public double FrameMilliseconds { get; private set; }

    /// <summary>How many scopes the last chart drew.</summary>
    public int ScopeCount { get; private set; }

    /// <summary>Hands the overlay samples somebody else collected.</summary>
    /// <param name="threads">The samples, as <see cref="Profiler.Collect" /> returns them.</param>
    /// <remarks>Use with <see cref="Collects" /> off, so the rings are drained exactly once.</remarks>
    public void Capture(ProfilerThreadSamples[] threads) => samples = threads ?? [];

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        if (Collects && Profiler.IsEnabled) {
            samples = Profiler.Collect();
        }

        var theme = surface.Theme;
        var thread = Deepest();

        if (thread is null) {
            var empty = surface.Panel(Anchor, Width, 1, "FRAME GRAPH");

            // Said out loud, because a chart that is blank because the profiler is off looks exactly
            // like a chart that is blank because the frame did nothing.
            empty.Text(0, Profiler.IsEnabled ? "no samples this frame" : "profiler is off", theme.Muted);
            return;
        }

        var frame = LastFrame(thread.Samples);

        if (!Bounds(thread.Samples, frame, out var begin, out var end)) {
            return;
        }

        var span = end - begin;
        FrameMilliseconds = span * 1000.0 / Stopwatch.Frequency;

        var levels = Math.Min(MaxDepth, Depth(thread.Samples, frame) + 1);
        var chart = levels * BarHeight;
        var region = surface.Panel(Anchor, Width, surface.HeightForRows(1) + chart, "FRAME GRAPH");

        if (region.IsEmpty) {
            return;
        }

        Span<char> buffer = stackalloc char[64];

        if (buffer.TryWrite($"{thread.ThreadName} — {FrameMilliseconds,0:F2} ms", out var length)) {
            region.Text(0, buffer[..length], theme.Muted);
        }

        ScopeCount = 0;

        var width = region.ContentWidth;
        var top = region.RowHeight;

        foreach (var sample in thread.Samples) {
            if (sample.FrameIndex != frame || sample.Depth >= levels) {
                continue;
            }

            var fraction = sample.DurationTicks / (float) span;

            // A scope thinner than a pixel is a bar nobody can see and a name nobody can read; the
            // total it belongs to is still visible as its parent's width.
            if (fraction < MinimumFraction) {
                continue;
            }

            var x = (sample.BeginTicks - begin) / (float) span * width;
            var bar = MathF.Max(fraction * width, 1f);
            var y = top + (sample.Depth * BarHeight);
            var colour = Hue(sample.Key);

            region.Fill(new(x, y), new(bar, BarHeight - 1f), OverlayTheme.Fade(colour, 0.55f));
            region.Rect(new(x, y), new(bar, BarHeight - 1f), colour);

            var name = sample.Key.Name;

            // Drawn only when the whole name fits: a truncated scope name in a flame chart is worse
            // than none, because two scopes that share a prefix become indistinguishable.
            if (surface.MeasureWidth(name) + 4f <= bar) {
                region.Text(new Vector2(x + 2f, y + 1f), name, theme.Text);
            }

            ScopeCount++;
        }
    }

    /// <summary>The thread that recorded the most, which is the one doing the work.</summary>
    ProfilerThreadSamples? Deepest() {
        ProfilerThreadSamples? best = null;

        foreach (var thread in samples) {
            if (thread.Samples.Length > 0 && (best is null || thread.Samples.Length > best.Samples.Length)) {
                best = thread;
            }
        }

        return best;
    }

    static int LastFrame(ProfilerSample[] entries) {
        var frame = 0;

        foreach (var sample in entries) {
            frame = Math.Max(frame, sample.FrameIndex);
        }

        return frame;
    }

    static bool Bounds(ProfilerSample[] entries, int frame, out long begin, out long end) {
        begin = long.MaxValue;
        end = long.MinValue;

        foreach (var sample in entries) {
            if (sample.FrameIndex == frame) {
                begin = Math.Min(begin, sample.BeginTicks);
                end = Math.Max(end, sample.BeginTicks + sample.DurationTicks);
            }
        }

        // A frame whose scopes all began and ended on the same tick would divide by zero below, which
        // is a real possibility on a coarse timer and an empty frame.
        return end > begin;
    }

    static int Depth(ProfilerSample[] entries, int frame) {
        var deepest = 0;

        foreach (var sample in entries) {
            if (sample.FrameIndex == frame) {
                deepest = Math.Max(deepest, sample.Depth);
            }
        }

        return deepest;
    }

    /// <summary>A stable colour per scope, so the same work is the same colour every frame.</summary>
    /// <remarks>
    ///     Hashed from the key rather than assigned in order of appearance: a chart whose colours
    ///     shuffle between frames cannot be read at all, and the order scopes are first seen in is
    ///     exactly what changes when the thing being investigated happens.
    /// </remarks>
    static Color4 Hue(ProfilingKey key) {
        var hash = (uint) key.Id * 2_654_435_761u;
        var hue = hash % 360u / 360f * 6f;
        var sector = (int) hue;
        var fraction = hue - sector;

        var (r, g, b) = sector switch {
            0 => (1f, fraction, 0f),
            1 => (1f - fraction, 1f, 0f),
            2 => (0f, 1f, fraction),
            3 => (0f, 1f - fraction, 1f),
            4 => (fraction, 0f, 1f),
            _ => (1f, 0f, 1f - fraction)
        };

        // Pulled toward grey, so eight adjacent bars read as eight bars rather than as a rainbow.
        return new((r * 0.55f) + 0.30f, (g * 0.55f) + 0.30f, (b * 0.55f) + 0.30f, 1f);
    }
}
