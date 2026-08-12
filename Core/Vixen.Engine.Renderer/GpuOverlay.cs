// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Graphics;

namespace Vixen.Engine.Renderer;

/// <summary>`stat gpu`: where the frame's GPU time went, pass by pass, while you watch.</summary>
/// <remarks>
///     <para>
///         <b>The panel exists so that a cost distribution is <em>noticed</em> rather than
///         measured.</b> Every number here has been available from <c>AppGraphics.GpuFrame</c> since
///         the render graph started emitting a scope per pass, and <c>Samples/13</c> already prints
///         the same breakdown to its log — but a log line is read by somebody who already suspects
///         something. Three fixes this repository shipped in one week (a 17× screen march, a
///         page-marking pass at 11 % of the frame, a thickness shell that made a hit test
///         meaningless) were each found by reading a breakdown <em>after</em> forming a suspicion.
///         What a log cannot do is show the distribution move as the camera turns.
///     </para>
///     <para>
///         ⚠ <b>Which is why the rows are in the frame's own order and not in cost order.</b> A
///         panel that re-sorts every frame cannot be read while anything is moving: the eye tracks a
///         row's position, and a table whose rows swap places is a table nobody can watch. Cost
///         decides <em>which</em> passes get a row — the most expensive
///         <see cref="MaxRows" /> of them — and the graph's declaration order decides where each one
///         sits. <see cref="FrameGraphOverlay" /> makes the identical argument about its colours.
///     </para>
///     <para>
///         ⚠ <b>And why each bar carries a peak that decays rather than a bare instantaneous
///         value.</b> A GPU reading moves by more than ten percent between two frames of a still
///         camera, so an unsmoothed number is unreadable and a smoothed one hides exactly the spike
///         worth seeing. The bar is the smoothed cost and the tick is the worst this pass has been
///         in the last <see cref="PeakSeconds" /> seconds — so a pass that costs three milliseconds
///         once, while you walk past a wall, leaves a mark that is still there when you look down.
///     </para>
///     <para>
///         ⚠ <b>Two rows exist to stop the panel lying by omission.</b> <c>unattributed</c> is the
///         frame span minus the passes that fill it, which is non-zero when GPU work happens outside
///         any pass the graph ran — a breakdown that does not describe the whole frame. <c>dropped</c>
///         is <see cref="GpuProfiler.Dropped" />, and a non-zero value means the timeline simply
///         stops partway through the frame: the bars that are there look right and the expensive pass
///         somebody opened this for is absent. Neither is a detail; a panel that hid either would
///         make its own incompleteness authoritative.
///     </para>
///     <para>
///         <b>Here rather than in <c>Vixen.Engine</c></b> for the reason the assembly exists:
///         <see cref="GpuFrame" /> is <c>Vixen.Graphics</c>' and <see cref="IDiagnosticOverlay" /> is
///         <c>Vixen.Engine</c>'s, and neither may reference the other. This is the join.
///     </para>
/// </remarks>
public sealed class GpuOverlay : IDiagnosticOverlay {
    /// <summary>How many passes get a row before the rest are folded into one.</summary>
    public const int MaxRows = 12;

    /// <summary>How long a peak is held before it decays back toward the current cost.</summary>
    public const float PeakSeconds = 3f;

    // Keyed by the pass name, because a scope's index moves the moment the document changes and a
    // history attached to an index would silently follow a different pass. Names are the graph's and
    // are stable across a reload of the same document.
    readonly Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
    readonly List<Row> rows = [];
    readonly List<string> visible = [];

    float[] ranking = [];
    int lastFrameIndex = -1;

    /// <inheritdoc />
    public string Name => "gpu";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.BottomRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 300f;

    /// <summary>The last frame the GPU finished timing. The host sets this once a frame.</summary>
    public GpuFrame Frame { get; set; } = GpuFrame.Empty;

    /// <summary>How many frames back <see cref="Frame" /> was recorded.</summary>
    public int Latency { get; set; }

    /// <summary>Whether a profiler is attached at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Said out loud, because an empty panel because the profiler is off looks exactly like
    ///     an empty panel because the frame did no work</b> — which is
    ///     <see cref="FrameGraphOverlay" />'s "profiler is off" line, one layer down. A build run
    ///     without <c>--vixen-gpu-profile</c> is the ordinary case and the panel says so rather than
    ///     showing zeroes.
    /// </remarks>
    public bool Available { get; set; }

    /// <summary>How many scopes the frame being recorded could not fit. ⚠ Non-zero is a lie by omission.</summary>
    public int Dropped { get; set; }

    /// <summary>How many pass rows the last <see cref="Draw" /> put on screen.</summary>
    public int DrawnRows { get; private set; }

    /// <summary>The share of the frame no pass accounted for, from zero to one.</summary>
    public float UnattributedFraction { get; private set; }

    /// <summary>Which passes have a row, top to bottom, as of the last <see cref="Draw" />.</summary>
    /// <remarks>
    ///     Published because the two claims this panel makes about itself — that a row keeps its
    ///     place while the costs move, and that the rows are the expensive ones — are claims about
    ///     this list, and a panel drawn as line segments cannot be read back any other way. A host
    ///     with its own layout may also use it.
    /// </remarks>
    public IReadOnlyList<string> VisiblePasses => visible;

    /// <summary>The decaying peak cost of one pass, in milliseconds, or zero if it has no history.</summary>
    /// <param name="pass">The render-graph pass's name.</param>
    /// <returns>The peak.</returns>
    public double PeakOf(string? pass) =>
        pass is not null && tracks.TryGetValue(pass, out var track) ? track.Peak : 0d;

    /// <summary>Forgets every smoothed cost and every peak.</summary>
    public void Reset() {
        tracks.Clear();
        rows.Clear();
        visible.Clear();
        lastFrameIndex = -1;
        DrawnRows = 0;
        UnattributedFraction = 0f;
    }

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var theme = surface.Theme;
        var frame = Frame;

        if (!Available || frame.Scopes.Count == 0) {
            var empty = surface.Panel(Anchor, Width, 1, "GPU");

            empty.Text(
                0,
                Available ? "waiting for the first frame" : "profiler is off — --vixen-gpu-profile",
                theme.Muted
            );

            DrawnRows = 0;
            visible.Clear();

            return;
        }

        var total = frame.Milliseconds;

        // ⚠ Integrated once per resolved frame and not once per draw. `GpuProfiler.Resolve` returns
        // false for most frames — it never waits — so the same GpuFrame is handed to this panel
        // several times running, and averaging it each time would pull the average onto whichever
        // reading happened to survive rather than onto the frame's own history.
        if (frame.FrameIndex != lastFrameIndex) {
            lastFrameIndex = frame.FrameIndex;
            Integrate(frame, (float) time.UnscaledElapsed.TotalSeconds);
        } else {
            Decay((float) time.UnscaledElapsed.TotalSeconds);
        }

        var attributed = Select();

        // One row of header, the pass rows, then unattributed — plus dropped, only when there is
        // something to say. A row that always reads zero teaches people to stop reading it.
        var extra = Dropped > 0 ? 2 : 1;
        var region = surface.Panel(Anchor, Width, rows.Count + 1 + extra, "GPU");

        if (region.IsEmpty) {
            DrawnRows = 0;
            return;
        }

        Span<char> buffer = stackalloc char[64];

        if (buffer.TryWrite($"{frame.Scopes.Count} passes", out var length)) {
            region.Text(0, buffer[..length], theme.Muted);
        }

        // ⚠ The read-back lag beside the figure, on FrameStatsOverlay's terms: a GPU time presented
        // as this frame's will be paired with the wrong CPU frame by whoever is chasing a stutter.
        if (buffer.TryWrite($"{total,6:F2} ms -{Latency}", out length)) {
            region.TextRight(0, buffer[..length], Health(total, theme));
        }

        DrawnRows = 0;

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];
            var track = row.Track;
            var share = total > 0d ? (float) (track.Smoothed / total) : 0f;

            if (buffer.TryWrite($"{track.Smoothed,6:F2} {share * 100f,3:F0}%", out length)) {
                Bar(
                    region,
                    index + 1,
                    row.Label,
                    buffer[..length],
                    share,
                    total > 0d ? (float) (track.Peak / total) : 0f,
                    Share(share, theme),
                    theme
                );
            }

            DrawnRows++;
        }

        var unattributed = Math.Max(0d, total - attributed);
        UnattributedFraction = total > 0d ? (float) (unattributed / total) : 0f;

        // ⚠ Against the level-zero scopes alone. A nested scope's span lies inside its parent's — the
        // screen-probe gather times five dispatches one level down — so summing everything reports
        // more GPU time than the frame has and turns this row into noise.
        region.Text(rows.Count + 1, "unattributed", theme.Text);

        if (buffer.TryWrite($"{unattributed,6:F2} {UnattributedFraction * 100f,3:F0}%", out length)) {
            region.TextRight(
                rows.Count + 1,
                buffer[..length],
                UnattributedFraction > 0.25f ? theme.Warning : theme.Muted
            );
        }

        if (Dropped > 0) {
            region.Text(rows.Count + 2, "dropped", theme.Bad);

            if (buffer.TryWrite($"{Dropped,6} — raise capacity", out length)) {
                region.TextRight(rows.Count + 2, buffer[..length], theme.Bad);
            }
        }
    }

    /// <summary>One labelled bar with a decaying peak tick on it.</summary>
    /// <remarks>
    ///     <see cref="OverlayRegion.Meter" />'s geometry with one thing added, rather than a call to
    ///     it: the tick is the whole reason this panel is worth watching, and a meter that drew the
    ///     smoothed value alone would be a table of numbers with decoration.
    /// </remarks>
    static void Bar(
        in OverlayRegion region,
        int row,
        ReadOnlySpan<char> label,
        ReadOnlySpan<char> value,
        float fraction,
        float peak,
        Color4 colour,
        in OverlayTheme theme
    ) {
        region.Text(row, label, theme.Text);
        region.TextRight(row, value, colour);

        var top = ((row + 1) * region.RowHeight) - (region.RowHeight * 0.28f);
        var height = region.RowHeight * 0.16f;
        var full = region.ContentWidth;

        region.Fill(new(0f, top), new(full, height), OverlayTheme.Fade(theme.Muted, 0.25f));

        var filled = full * Math.Clamp(fraction, 0f, 1f);

        if (filled > 0f) {
            region.Fill(new(0f, top), new(filled, height), colour);
        }

        // Only when it is meaningfully worse than the bar under it: a tick sitting on the bar's own
        // end is two marks saying one thing, and reads as a rendering artefact.
        var mark = full * Math.Clamp(peak, 0f, 1f);

        if (mark > filled + 1f) {
            region.Line(new(mark, top - 1f), new(mark, top + height + 1f), theme.Warning);
        }
    }

    /// <summary>Folds this frame's readings into the per-pass history.</summary>
    void Integrate(GpuFrame frame, float seconds) {
        foreach (var (_, track) in tracks) {
            track.Seen = false;
        }

        for (var index = 0; index < frame.Scopes.Count; index++) {
            var scope = frame.Scopes[index];
            var cost = frame.MillisecondsOf(scope);

            if (!tracks.TryGetValue(scope.Name, out var track)) {
                track = new Track();
                tracks[scope.Name] = track;
            }

            // A quarter-weight average, FrameStatsOverlay's: settled enough to read, quick enough
            // that a stall is visible while it is still happening.
            track.Smoothed = track.Seen || track.Smoothed <= 0d
                ? cost
                : track.Smoothed + ((cost - track.Smoothed) * 0.25d);

            track.Order = index;
            track.Seen = true;

            // ⚠ The label is built when the level changes and not when the row is drawn. A panel
            // somebody leaves on draws sixty times a second, and a concatenation per nested row per
            // frame is a garbage collection caused by a diagnostic — which is the shape of bug that
            // makes people turn diagnostics off.
            if (track.Level != scope.Level || track.Label is null) {
                track.Level = scope.Level;
                track.Label = scope.Level == 0 ? scope.Name : "└ " + scope.Name;
            }

            Hold(track, cost, seconds);
        }

        // A pass the document stopped declaring keeps no row: its history would otherwise sit at its
        // last cost for ever and be read as a pass that is still running.
        foreach (var (name, track) in tracks) {
            if (!track.Seen) {
                tracks.Remove(name);
            }
        }
    }

    /// <summary>Lets every peak fall on a frame whose readings are the ones already folded in.</summary>
    void Decay(float seconds) {
        foreach (var (_, track) in tracks) {
            Hold(track, track.Smoothed, seconds);
        }
    }

    static void Hold(Track track, double cost, float seconds) {
        if (cost >= track.Peak) {
            track.Peak = cost;
            return;
        }

        // Linear back toward the current cost over PeakSeconds, so a mark is legible for about three
        // seconds and then stops competing with the bar. An exponential decay spends most of its life
        // just above the bar, which is the part that says nothing.
        var fall = (track.Peak - cost) * Math.Clamp(seconds / PeakSeconds, 0f, 1f);
        track.Peak = Math.Max(cost, track.Peak - fall);
    }

    /// <summary>Picks the rows and puts them back into the frame's order. Returns the level-zero total.</summary>
    double Select() {
        rows.Clear();

        if (ranking.Length < tracks.Count) {
            ranking = new float[Math.Max(tracks.Count, 32)];
        }

        var count = 0;
        var attributed = 0d;

        foreach (var (_, track) in tracks) {
            ranking[count++] = (float) track.Smoothed;

            if (track.Level == 0) {
                attributed += track.Smoothed;
            }
        }

        // The MaxRows-th largest smoothed cost, which is the cut. Sorting a float[] in place takes no
        // comparer and allocates nothing, which matters for a panel somebody leaves on.
        Array.Sort(ranking, 0, count);

        var cut = count > MaxRows ? ranking[count - MaxRows] : 0f;

        foreach (var (name, track) in tracks) {
            if (track.Smoothed >= cut) {
                rows.Add(new(name, track));
            }
        }

        // Frame order, which is what makes a row stay where the eye left it. Membership changes only
        // when a pass crosses the cut, and the smoothing is what makes that rare.
        rows.Sort(static (left, right) => left.Track.Order.CompareTo(right.Track.Order));

        // A cut that ties can admit more than MaxRows — a frame of identically cheap passes does —
        // and a panel taller than the screen is refused whole by OverlaySurface.Panel.
        if (rows.Count > MaxRows) {
            rows.RemoveRange(MaxRows, rows.Count - MaxRows);
        }

        visible.Clear();

        for (var index = 0; index < rows.Count; index++) {
            visible.Add(rows[index].Name);
        }

        return attributed;
    }

    /// <summary>A share of the frame, coloured by how much of it one pass is taking.</summary>
    /// <remarks>
    ///     A threshold rather than a stable hue per pass, because the question this panel is left on
    ///     to answer is "is anything taking more of the frame than it should", and that is a property
    ///     of the number rather than of which pass it belongs to.
    /// </remarks>
    static Color4 Share(float fraction, in OverlayTheme theme) =>
        fraction switch {
            >= 0.35f => theme.Bad,
            >= 0.15f => theme.Warning,
            _ => theme.Good
        };

    static Color4 Health(double milliseconds, in OverlayTheme theme) =>
        milliseconds <= 0d
            ? theme.Muted
            : milliseconds <= 1000d / 60d
                ? theme.Good
                : milliseconds <= 1000d / 30d
                    ? theme.Warning
                    : theme.Bad;

    sealed class Track {
        public double Smoothed;
        public double Peak;
        public int Order;
        public int Level = -1;
        public bool Seen;

        /// <summary>The name, indented when the scope is nested inside another.</summary>
        /// <remarks>
        ///     The same arrow <c>SampleLog</c>'s ranked breakdown uses, and for the same reason: a
        ///     stage inside a pass is a candidate answer to "what is the most expensive thing here",
        ///     so it is ranked beside its parent — and the arrow is what stops it being read as a
        ///     second pass whose cost should be added.
        /// </remarks>
        public string? Label;
    }

    readonly record struct Row(string Name, Track Track) {
        public ReadOnlySpan<char> Label => Track.Label ?? Name;
    }
}
