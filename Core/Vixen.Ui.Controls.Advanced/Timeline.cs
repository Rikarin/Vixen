// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One keyframe on a track.</summary>
public sealed class TimelineKey {
    /// <summary>Creates a key.</summary>
    /// <param name="time">When, in seconds.</param>
    /// <param name="tag">Whatever the application wants to hang off it.</param>
    public TimelineKey(float time, object? tag = null) {
        Time = time;
        Tag = tag;
    }

    /// <summary>When, in seconds.</summary>
    public float Time { get; set; }

    /// <summary>Whatever the application wants to hang off it.</summary>
    public object? Tag { get; set; }

    /// <inheritdoc />
    public override string ToString() => Time.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One row: a name, some keys, and optionally the curve they describe.</summary>
public sealed class TimelineTrack {
    readonly List<TimelineKey> keys = [];

    /// <summary>Creates a track.</summary>
    /// <param name="name">What it is called.</param>
    public TimelineTrack(string name) => Name = name;

    /// <summary>What it is called.</summary>
    public string Name { get; set; }

    /// <summary>Its keys, in time order.</summary>
    public IReadOnlyList<TimelineKey> Keys => keys;

    /// <summary>Whether it is switched off.</summary>
    public bool Muted { get; set; }

    /// <summary>The curve these keys are of, if the application has one.</summary>
    /// <remarks>
    ///     Optional, and read only for drawing. A timeline shows <i>when</i> things happen; a
    ///     <see cref="CurveEditor" /> shows what they are worth. Carrying the curve lets the lane
    ///     draw a faint trace behind the keys, which is what makes a timeline readable without
    ///     turning it into a second curve editor.
    /// </remarks>
    public AnimationCurve? Curve { get; set; }

    /// <summary>Whatever the application wants to hang off it.</summary>
    public object? Tag { get; set; }

    /// <summary>Adds a key.</summary>
    /// <param name="time">When.</param>
    /// <param name="tag">Its tag.</param>
    /// <returns>The key.</returns>
    public TimelineKey Add(float time, object? tag = null) {
        var key = new TimelineKey(time, tag);

        keys.Add(key);
        Sort();

        return key;
    }

    /// <summary>Removes a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(TimelineKey key) => keys.Remove(key);

    /// <summary>Moves a key and puts the list back in time order.</summary>
    /// <param name="key">The key.</param>
    /// <param name="time">When to.</param>
    public void Move(TimelineKey key, float time) {
        ArgumentNullException.ThrowIfNull(key);

        key.Time = time;
        Sort();
    }

    void Sort() => keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
}

/// <summary>One track's name and its mute button.</summary>
public sealed partial class TimelineHeader : Control {
    /// <inheritdoc />
    protected override string TagName => "timeline-header";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which track, or <c>null</c> if parked.</summary>
    public TimelineTrack? Track { get; internal set; }

    /// <summary>Its name.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>The button that switches it off.</summary>
    public ToggleButton Mute { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Label = Part("timeline-name");

        Mute = Part<ToggleButton>();
        Mute.Label = "M";
        Mute.Size = ControlSize.Small;
        Mute.Variant = ControlVariant.Subtle;
    }
}

/// <summary>The lanes: every key, every curve trace, the marquee and the playhead.</summary>
/// <remarks>
///     ⚠ <b>One element for every track rather than one element per key.</b> A timeline of forty
///     tracks with two hundred keys each is eight thousand diamonds four pixels across, and as
///     elements that would be eight thousand style nodes and layout boxes for a picture whose whole
///     content is arithmetic. Everything below the ruler is drawn, and the hit tests are the same
///     arithmetic read backwards.
/// </remarks>
public sealed class TimelineLanes : UiElement {
    readonly PathBuilder path = new();

    /// <inheritdoc />
    protected override string TagName => "timeline-lanes";

    /// <summary>The timeline it draws for.</summary>
    public Timeline? Owner { get; internal set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 0f || bounds.Height <= 0f || Owner is not { } timeline) {
            return;
        }

        DrawGrid(context, timeline, bounds);

        var height = timeline.TrackHeight;
        var accent = timeline.KeyActiveColor;
        var key = timeline.KeyColor;

        for (var i = timeline.FirstVisibleTrack; i < timeline.Tracks.Count; i++) {
            var top = bounds.Y + (i * height) - timeline.ScrollTop;

            if (top > bounds.Bottom) {
                break;
            }

            var track = timeline.Tracks[i];

            if (i % 2 == 1) {
                context.FillRectangle(new Rectangle(bounds.X, top, bounds.Width, height), timeline.StripeColor);
            }

            if (track.Curve is { } curve) {
                Trace(context, timeline, bounds, curve, top, height);
            }

            foreach (var entry in track.Keys) {
                var x = timeline.ToScreen(entry.Time);

                if (x < bounds.X - 8f || x > bounds.Right + 8f) {
                    continue;
                }

                var size = timeline.KeySize;
                var centre = top + (height * 0.5f);
                var selected = timeline.Selection.Contains(entry);

                // A diamond rather than a square, because that is what a keyframe has looked like
                // since the first animation tool and because it is unmistakable at four pixels.
                path.Clear()
                    .MoveTo(new Vector2(x, centre - size))
                    .LineTo(new Vector2(x + size, centre))
                    .LineTo(new Vector2(x, centre + size))
                    .LineTo(new Vector2(x - size, centre))
                    .Close();

                context.Fill(path, selected ? accent : key);
            }
        }

        if (timeline.Marquee is { } band) {
            context.FillRectangle(band, timeline.MarqueeColor);
            context.StrokeRectangle(band, accent, 1f);
        }

        DrawPlayhead(context, timeline, bounds);
    }

    void DrawGrid(DrawContext context, Timeline timeline, Rectangle bounds) {
        var step = timeline.TickStep;

        if (step <= 0f) {
            return;
        }

        path.Clear();

        for (var time = MathF.Ceiling(timeline.TimeStart / step) * step; ; time += step) {
            var x = timeline.ToScreen(time);

            if (x > bounds.Right) {
                break;
            }

            path.MoveTo(new Vector2(x, bounds.Top)).LineTo(new Vector2(x, bounds.Bottom));
        }

        context.Stroke(path, timeline.GridColor, 1f);
    }

    /// <summary>Draws a track's curve behind its keys, scaled to the lane.</summary>
    void Trace(DrawContext context, Timeline timeline, Rectangle bounds, AnimationCurve curve, float top, float height) {
        if (curve.Keys.Count == 0) {
            return;
        }

        var minimum = float.MaxValue;
        var maximum = float.MinValue;

        foreach (var key in curve.Keys) {
            minimum = MathF.Min(minimum, key.Value);
            maximum = MathF.Max(maximum, key.Value);
        }

        var span = MathF.Max(1e-4f, maximum - minimum);
        var inset = height * 0.15f;

        path.Clear();

        var columns = (int) MathF.Ceiling(bounds.Width);

        for (var i = 0; i <= columns; i++) {
            var x = bounds.X + i;
            var value = curve.Evaluate(timeline.ToTime(x));
            var y = top + height - inset - ((value - minimum) / span * (height - (inset * 2f)));

            if (i == 0) {
                path.MoveTo(new Vector2(x, y));
            } else {
                path.LineTo(new Vector2(x, y));
            }
        }

        context.Stroke(path, timeline.CurveColor, 1f);
    }

    void DrawPlayhead(DrawContext context, Timeline timeline, Rectangle bounds) {
        var x = timeline.ToScreen(timeline.Time);

        if (x < bounds.X || x > bounds.Right) {
            return;
        }

        path.Clear().MoveTo(new Vector2(x, bounds.Top)).LineTo(new Vector2(x, bounds.Bottom));
        context.Stroke(path, timeline.PlayheadColor, 1.5f);
    }
}

/// <summary>The strip of times along the top, and where the playhead is scrubbed.</summary>
public sealed partial class TimelineRuler : Control {
    readonly List<UiElement> labels = [];
    readonly PathBuilder path = new();

    /// <inheritdoc />
    protected override string TagName => "timeline-ruler";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The timeline it belongs to.</summary>
    public Timeline? Owner { get; internal set; }

    /// <summary>Puts a label under every tick that is on screen.</summary>
    /// <remarks>
    ///     ⚠ <b>The only elements below the ruler's line.</b> Text belongs to an element in this
    ///     framework — there is no way to draw a string from <see cref="UiElement.OnDraw" /> — so the
    ///     numbers are a pool of labels, positioned absolutely and parked when the zoom needs fewer.
    /// </remarks>
    internal void Realise() {
        if (Owner is not { } timeline) {
            return;
        }

        var bounds = Bounds;
        var step = timeline.TickStep;
        var slot = 0;

        if (step > 0f && bounds.Width > 0f) {
            for (var time = MathF.Ceiling(timeline.TimeStart / step) * step; ; time += step) {
                var x = timeline.ToScreen(time);

                if (x > bounds.Right) {
                    break;
                }

                while (labels.Count <= slot) {
                    labels.Add(Add("timeline-tick"));
                }

                var label = labels[slot++];

                label.RemoveClass("parked");
                label.Text = time.ToString("0.##", CultureInfo.InvariantCulture);
                label.SetStyle("left", Inline.Px(x - bounds.X + 2f));
            }
        }

        for (var i = slot; i < labels.Count; i++) {
            labels[i].AddClass("parked");
        }
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Owner is not { } timeline) {
            return;
        }

        var bounds = context.Bounds;
        var step = timeline.TickStep;

        if (step <= 0f || bounds.Width <= 0f) {
            return;
        }

        path.Clear();

        for (var time = MathF.Ceiling(timeline.TimeStart / step) * step; ; time += step) {
            var x = timeline.ToScreen(time);

            if (x > bounds.Right) {
                break;
            }

            path.MoveTo(new Vector2(x, bounds.Bottom - 5f)).LineTo(new Vector2(x, bounds.Bottom));
        }

        context.Stroke(path, timeline.GridColor, 1f);

        var head = timeline.ToScreen(timeline.Time);

        if (head >= bounds.X && head <= bounds.Right) {
            context.FillRectangle(new Rectangle(head - 5f, bounds.Y + 2f, 10f, bounds.Height - 4f), timeline.PlayheadColor, 2f);
        }
    }
}

/// <summary>What a drag on the lanes is doing.</summary>
enum TimelineDrag : byte {
    None,
    Keys,
    Marquee,
    Scrub
}

/// <summary>Tracks against time: keys, a playhead, a zoom and a snap.</summary>
/// <remarks>
///     <para>
///         <b>Three parts, and only one of them is elements.</b> The headers down the left are
///         controls, because a track name is text and a mute button is a button. Everything to the
///         right of them is drawn, because it is eight thousand diamonds whose positions are two
///         multiplications each. That split is the same one <c>TreeView</c> makes between its rows
///         and its scroll range, one level coarser.
///     </para>
///     <para>
///         ⚠ <b>Time and pixels are related by one number.</b> <see cref="PixelsPerSecond" /> is the
///         zoom and <see cref="TimeStart" /> is the pan, and everything — the ruler's tick spacing,
///         a key's x, the playhead, the hit radius — goes through the two. A timeline that kept a
///         separate visible range would have three numbers that could disagree.
///     </para>
///     <para>
///         ⚠ <b>Snapping is to frames, not to a grid.</b> An animation is played back at a frame
///         rate and a key between two frames is a key that plays on one of them anyway — so the snap
///         that matters is <see cref="FrameRate" />, and a pixel grid would put keys where no frame
///         is.
///     </para>
/// </remarks>
public sealed partial class Timeline : Control {
    readonly List<TimelineTrack> tracks = [];
    readonly List<TimelineHeader> headers = [];
    readonly HashSet<TimelineKey> selection = [];
    readonly List<TimelineKey> moving = [];

    TimelineDrag drag;
    float dragTime;
    Vector2 bandOrigin;

    int gridColor;
    int stripeColor;
    int keyColor;
    int keyActiveColor;
    int playheadColor;
    int marqueeColor;
    int curveColor;
    int trackHeightId;

    /// <inheritdoc />
    protected override string TagName => "timeline";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>The strip of times along the top.</summary>
    public TimelineRuler Ruler { get; private set; } = null!;

    /// <summary>The column of names down the left.</summary>
    public UiElement Headers { get; private set; } = null!;

    /// <summary>Everything to the right of the names.</summary>
    public TimelineLanes Lanes { get; private set; } = null!;

    /// <summary>The tracks, top to bottom.</summary>
    public IReadOnlyList<TimelineTrack> Tracks => tracks;

    /// <summary>The header elements that exist, including the parked ones.</summary>
    public IReadOnlyList<TimelineHeader> HeaderRows => headers;

    /// <summary>Which keys are selected.</summary>
    public IReadOnlyCollection<TimelineKey> Selection => selection;

    /// <summary>Where the playhead is, in seconds.</summary>
    [UiProperty(Coerce = nameof(ClampTime), Changed = nameof(OnTimeChanged))]
    public partial float Time { get; set; }

    /// <summary>How long the timeline is, in seconds.</summary>
    [UiProperty(Default = 5f, Changed = nameof(OnRangeChanged))]
    public partial float Duration { get; set; }

    /// <summary>The time at the left edge of the lanes.</summary>
    [UiProperty(Changed = nameof(OnRangeChanged))]
    public partial float TimeStart { get; set; }

    /// <summary>The zoom.</summary>
    [UiProperty(Default = 100f, Coerce = nameof(ClampScale), Changed = nameof(OnRangeChanged))]
    public partial float PixelsPerSecond { get; set; }

    /// <summary>How many frames a second, which is what a drag snaps to.</summary>
    [UiProperty(Default = 30f)]
    public partial float FrameRate { get; set; }

    /// <summary>Whether a dragged key lands on a frame.</summary>
    [UiProperty(Default = true)]
    public partial bool SnapToFrames { get; set; }

    /// <summary>How far down the tracks have been scrolled.</summary>
    [UiProperty(Coerce = nameof(ClampScroll), Changed = nameof(OnScrolled))]
    public partial float ScrollTop { get; set; }

    /// <summary>How tall a track is, from <c>--track-height</c>.</summary>
    public float TrackHeight => Document.LengthOf(Style, trackHeightId) ?? 24f;

    /// <summary>How big a keyframe diamond is, from its centre to a point.</summary>
    [UiProperty(Default = 5f)]
    public partial float KeySize { get; set; }

    /// <summary>The first track with any of it on screen.</summary>
    public int FirstVisibleTrack =>
        TrackHeight <= 0f ? 0 : Math.Clamp((int) MathF.Floor(ScrollTop / TrackHeight), 0, Math.Max(0, tracks.Count - 1));

    /// <summary>The rubber band, if one is being dragged.</summary>
    public Rectangle? Marquee { get; private set; }

    /// <summary>How far apart the ruler's ticks are, in seconds.</summary>
    /// <remarks>
    ///     ⚠ <b>Chosen from a 1-2-5 ladder rather than by dividing.</b> A tick every 0.037 seconds is
    ///     arithmetically fine and unreadable; every ruler anybody has ever used steps through one,
    ///     two and five times a power of ten, so the labels are numbers a person can hold.
    /// </remarks>
    public float TickStep {
        get {
            if (PixelsPerSecond <= 0f) {
                return 0f;
            }

            var target = 80f / PixelsPerSecond;
            var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(MathF.Max(1e-6f, target))));

            foreach (var multiple in (ReadOnlySpan<float>) [1f, 2f, 5f, 10f]) {
                if (magnitude * multiple >= target) {
                    return magnitude * multiple;
                }
            }

            return magnitude * 10f;
        }
    }

    /// <summary>Raised when the playhead moves.</summary>
    public event Action<Timeline, float>? TimeChanged;

    /// <summary>Raised when the selection changes.</summary>
    public event Action<Timeline>? SelectionChanged;

    /// <summary>Raised after a drag has moved some keys.</summary>
    public event Action<Timeline>? KeysMoved;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        gridColor = Document.PropertyId("--grid-color");
        stripeColor = Document.PropertyId("--stripe-color");
        keyColor = Document.PropertyId("--key-color");
        keyActiveColor = Document.PropertyId("--key-active-color");
        playheadColor = Document.PropertyId("--playhead-color");
        marqueeColor = Document.PropertyId("--marquee-color");
        curveColor = Document.PropertyId("--curve-color");
        trackHeightId = Document.PropertyId("--track-height");

        Ruler = Part<TimelineRuler>();
        Ruler.Owner = this;

        var body = Part("timeline-body");

        Headers = body.Add("timeline-headers");

        Lanes = body.Add<TimelineLanes>();
        Lanes.Owner = this;

        AddHandler<PointerEvent>(static (element, args) => ((Timeline) element).Pointed(args));
        AddHandler<WheelEvent>(static (element, args) => ((Timeline) element).Wheeled(args));
        AddHandler<KeyEvent>(static (element, args) => ((Timeline) element).Keyed(args));
        AddHandler<TapEvent>(static (element, args) => ((Timeline) element).Tapped(args));
    }

    // ── Contents ─────────────────────────────────────────────────────────────

    /// <summary>Adds a track at the bottom.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The track.</returns>
    public TimelineTrack AddTrack(string name) {
        var track = new TimelineTrack(name);
        tracks.Add(track);

        Refresh();
        return track;
    }

    /// <summary>Removes a track, and takes its keys out of the selection.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(TimelineTrack track) {
        ArgumentNullException.ThrowIfNull(track);

        if (!tracks.Remove(track)) {
            return false;
        }

        foreach (var key in track.Keys) {
            selection.Remove(key);
        }

        Refresh();
        return true;
    }

    /// <summary>Brings the headers back into agreement with the tracks.</summary>
    public void Refresh() {
        var height = TrackHeight;

        if (height <= 0f) {
            return;
        }

        var capacity = Math.Min(
            tracks.Count,
            (int) MathF.Ceiling(MathF.Max(0f, Lanes.Height) / height) + 2
        );

        while (headers.Count < capacity) {
            headers.Add(Headers.Add<TimelineHeader>());
        }

        var first = FirstVisibleTrack;

        for (var i = 0; i < headers.Count; i++) {
            var header = headers[i];
            var index = first + i;

            if (i >= capacity || index >= tracks.Count) {
                header.Track = null;
                header.AddClass("parked");

                continue;
            }

            var track = tracks[index];

            header.RemoveClass("parked");
            header.Track = track;
            header.Label.Text = track.Name;
            header.Mute.IsChecked = track.Muted;

            header.SetStyle("top", Inline.Px((index * height) - ScrollTop));
            header.SetStyle("height", Inline.Px(height));
        }

        Ruler.Realise();
        Document.Invalidate();
    }

    /// <summary>Zooms and pans until the whole duration fits.</summary>
    public void ZoomToFit() {
        if (Lanes.Width <= 0f || Duration <= 0f) {
            return;
        }

        PixelsPerSecond = Lanes.Width / Duration;
        TimeStart = 0f;
    }

    // ── Coordinates ──────────────────────────────────────────────────────────

    /// <summary>Where a time is, in document space.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The x.</returns>
    public float ToScreen(float time) => Lanes.AbsoluteLeft + ((time - TimeStart) * PixelsPerSecond);

    /// <summary>Which time a document-space x is.</summary>
    /// <param name="x">The x.</param>
    /// <returns>The time.</returns>
    public float ToTime(float x) =>
        PixelsPerSecond <= 0f ? TimeStart : TimeStart + ((x - Lanes.AbsoluteLeft) / PixelsPerSecond);

    /// <summary>Which track a document-space y is over, or -1.</summary>
    /// <param name="y">The y.</param>
    /// <returns>The index.</returns>
    public int TrackAt(float y) {
        var height = TrackHeight;

        if (height <= 0f) {
            return -1;
        }

        var index = (int) MathF.Floor((y - Lanes.AbsoluteTop + ScrollTop) / height);
        return index >= 0 && index < tracks.Count ? index : -1;
    }

    /// <summary>The key nearest a document-space point, if one is within reach.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    public TimelineKey? KeyAt(float x, float y) {
        var index = TrackAt(y);

        if (index < 0) {
            return null;
        }

        TimelineKey? found = null;
        var best = KeySize + 3f;

        foreach (var key in tracks[index].Keys) {
            var distance = MathF.Abs(ToScreen(key.Time) - x);

            if (distance <= best) {
                best = distance;
                found = key;
            }
        }

        return found;
    }

    /// <summary>Brings a time onto a frame, if snapping is on.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The snapped time.</returns>
    public float Snap(float time) =>
        SnapToFrames && FrameRate > 0f ? MathF.Round(time * FrameRate) / FrameRate : time;

    // ── Colours ──────────────────────────────────────────────────────────────

    /// <summary>The tick and grid colour.</summary>
    public Color4 GridColor => Document.ColorOf(Style, gridColor) ?? new Color4(0f, 0f, 0f, 0.10f);

    /// <summary>The colour of every other lane.</summary>
    public Color4 StripeColor => Document.ColorOf(Style, stripeColor) ?? new Color4(0f, 0f, 0f, 0.03f);

    /// <summary>A keyframe's colour.</summary>
    public Color4 KeyColor => Document.ColorOf(Style, keyColor) ?? new Color4(0.55f, 0.58f, 0.63f, 1f);

    /// <summary>A selected keyframe's colour.</summary>
    public Color4 KeyActiveColor => Document.ColorOf(Style, keyActiveColor) ?? Document.ForegroundOf(this);

    /// <summary>The playhead's colour.</summary>
    public Color4 PlayheadColor => Document.ColorOf(Style, playheadColor) ?? new Color4(0.87f, 0.29f, 0.33f, 1f);

    /// <summary>The rubber band's colour.</summary>
    public Color4 MarqueeColor => Document.ColorOf(Style, marqueeColor) ?? new Color4(0.23f, 0.42f, 0.94f, 0.2f);

    /// <summary>The colour of the trace behind a track's keys.</summary>
    public Color4 CurveColor => Document.ColorOf(Style, curveColor) ?? new Color4(0.55f, 0.58f, 0.63f, 0.6f);

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Selects a key, or adds it to or removes it from the selection.</summary>
    /// <param name="key">The key, or <c>null</c> to select nothing.</param>
    /// <param name="modifiers">What was held.</param>
    public void Select(TimelineKey? key, ModifierKeys modifiers = ModifierKeys.None) {
        if (key is null) {
            if (selection.Count == 0) {
                return;
            }

            selection.Clear();
            Restate();

            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control)) {
            if (!selection.Remove(key)) {
                selection.Add(key);
            }

            Restate();
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift) || selection.Contains(key)) {
            selection.Add(key);
            Restate();

            return;
        }

        selection.Clear();
        selection.Add(key);

        Restate();
    }

    /// <summary>Selects every key on every track.</summary>
    public void SelectAll() {
        selection.Clear();

        foreach (var track in tracks) {
            foreach (var key in track.Keys) {
                selection.Add(key);
            }
        }

        Restate();
    }

    /// <summary>Removes every selected key.</summary>
    public void DeleteSelection() {
        if (selection.Count == 0) {
            return;
        }

        foreach (var track in tracks) {
            foreach (var key in track.Keys.ToArray()) {
                if (selection.Contains(key)) {
                    track.Remove(key);
                }
            }
        }

        selection.Clear();
        Restate();
    }

    void Restate() {
        SelectionChanged?.Invoke(this);
        Document.Invalidate();
    }

    // ── Input ────────────────────────────────────────────────────────────────

    float ClampTime(float time) => Math.Clamp(time, 0f, MathF.Max(0f, Duration));

    static float ClampScale(float scale) => MathF.Max(1f, scale);

    float ClampScroll(float top) =>
        Math.Clamp(top, 0f, MathF.Max(0f, (tracks.Count * TrackHeight) - MathF.Max(0f, Lanes.Height)));

    void OnTimeChanged(float previous, float current) {
        TimeChanged?.Invoke(this, current);
        Document.Invalidate();
    }

    void OnRangeChanged(float previous, float current) {
        Ruler.Realise();
        Document.Invalidate();
    }

    void OnScrolled(float previous, float current) => Refresh();

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                Begin(args);
                break;

            case PointerAction.Moved when drag != TimelineDrag.None:
                Track(args);
                break;

            case PointerAction.Released when drag != TimelineDrag.None:
                Finish();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Begin(PointerEvent args) {
        Document.Focus(this);

        // A press anywhere on the ruler scrubs, which is what a ruler is for — and it is why the
        // ruler is a sibling of the lanes rather than a strip the lanes draw themselves.
        if (Ruler.Bounds.Contains(new Vector2(args.X, args.Y))) {
            drag = TimelineDrag.Scrub;
            Time = Snap(ToTime(args.X));

            Document.CapturePointer(this);
            return;
        }

        if (!Lanes.Bounds.Contains(new Vector2(args.X, args.Y))) {
            return;
        }

        if (KeyAt(args.X, args.Y) is { } key) {
            Select(key, args.Modifiers);

            moving.Clear();
            moving.AddRange(selection);

            dragTime = ToTime(args.X);
            drag = TimelineDrag.Keys;

            Document.CapturePointer(this);
            return;
        }

        if (!args.Modifiers.HasFlag(ModifierKeys.Shift) && !args.Modifiers.HasFlag(ModifierKeys.Control)) {
            Select(null);
        }

        bandOrigin = new Vector2(args.X, args.Y);
        Marquee = new Rectangle(args.X, args.Y, 0f, 0f);
        drag = TimelineDrag.Marquee;

        Document.CapturePointer(this);
    }

    void Track(PointerEvent args) {
        switch (drag) {
            case TimelineDrag.Scrub:
                Time = Snap(ToTime(args.X));
                break;

            case TimelineDrag.Keys:
                var now = ToTime(args.X);
                var delta = now - dragTime;

                foreach (var track in tracks) {
                    foreach (var key in track.Keys.ToArray()) {
                        if (moving.Contains(key)) {
                            track.Move(key, MathF.Max(0f, Snap(key.Time + delta)));
                        }
                    }
                }

                dragTime = now;
                Document.Invalidate();

                break;

            case TimelineDrag.Marquee:
                Marquee = Rectangle.FromCorners(bandOrigin, new Vector2(args.X, args.Y));
                Document.Invalidate();

                break;

            default:
                break;
        }
    }

    void Finish() {
        if (drag == TimelineDrag.Keys) {
            KeysMoved?.Invoke(this);
        } else if (drag == TimelineDrag.Marquee && Marquee is { } band) {
            var height = TrackHeight;

            for (var i = 0; i < tracks.Count; i++) {
                var top = Lanes.AbsoluteTop + (i * height) - ScrollTop;

                if (top + height < band.Top || top > band.Bottom) {
                    continue;
                }

                foreach (var key in tracks[i].Keys) {
                    var x = ToScreen(key.Time);

                    if (x >= band.Left && x <= band.Right) {
                        selection.Add(key);
                    }
                }
            }

            Restate();
        }

        drag = TimelineDrag.None;
        Marquee = null;

        moving.Clear();
        Document.ReleasePointer();
        Document.Invalidate();
    }

    void Tapped(TapEvent args) {
        if (args.Count != 2 || !Lanes.Bounds.Contains(new Vector2(args.X, args.Y))) {
            return;
        }

        var index = TrackAt(args.Y);

        if (index < 0) {
            return;
        }

        if (KeyAt(args.X, args.Y) is { } key) {
            tracks[index].Remove(key);
            selection.Remove(key);

            Restate();
        } else {
            Select(tracks[index].Add(Snap(ToTime(args.X))));
        }

        args.Handled = true;
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        switch (args.Key) {
            case InputKey.Delete or InputKey.Backspace:
                DeleteSelection();
                break;

            case InputKey.A when args.Modifiers.HasFlag(ModifierKeys.Control):
                SelectAll();
                break;

            case InputKey.Home:
                Time = 0f;
                break;

            case InputKey.End:
                Time = Duration;
                break;

            case InputKey.Left:
                Time -= FrameRate > 0f ? 1f / FrameRate : 0.1f;
                break;

            case InputKey.Right:
                Time += FrameRate > 0f ? 1f / FrameRate : 0.1f;
                break;

            case InputKey.F:
                ZoomToFit();
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// <remarks>
    ///     ⚠ <b>The wheel zooms and Shift-wheel scrolls the tracks</b>, rather than the other way
    ///     round. A timeline is nearly always too long and rarely too tall, so the gesture without a
    ///     modifier is the one that is wanted a hundred times an hour.
    /// </remarks>
    void Wheeled(WheelEvent args) {
        if (args.Modifiers.HasFlag(ModifierKeys.Shift)) {
            ScrollTop += args.DeltaY;
            args.Handled = true;

            return;
        }

        var before = ToTime(args.X);

        PixelsPerSecond *= MathF.Exp(-args.DeltaY * 0.0015f);
        TimeStart += before - ToTime(args.X);

        args.Handled = true;
    }
}
