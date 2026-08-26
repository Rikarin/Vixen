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

/// <summary>What a track holds.</summary>
/// <remarks>
///     ⚠ <b>A track is one or the other, and that is what makes a double-tap unambiguous.</b> On a
///     key track an empty double-tap adds a key; on a span track it adds a span. A track that held
///     both would have to guess which was meant from the pointer's height, and the answer would be
///     wrong half the time.
/// </remarks>
public enum TimelineTrackKind : byte {
    /// <summary>Instants. A diamond each.</summary>
    Keys,

    /// <summary>Stretches of time. A bar each, with its ramps drawn on it.</summary>
    Spans
}

/// <summary>A stretch of time on a track, with a ramp at each end.</summary>
/// <remarks>
///     <para>
///         <b>What a key cannot say is "for how long".</b> An event happens at a moment; a constraint,
///         a sub-clip or an audio region occupies an interval and fades at its edges, and representing
///         one as a pair of keys loses the association between them the moment either is dragged.
///     </para>
///     <para>
///         ⚠ <b><see cref="End" /> before <see cref="Begin" /> means it wraps the end of the
///         timeline</b> and is drawn as two bars. That is the ordinary case for anything authored
///         against a looping clip — a foot that plants near the end of a cycle and lifts near the
///         start of the next — and refusing it would make the loop point a place authors cannot mark.
///     </para>
/// </remarks>
public sealed class TimelineSpan {
    /// <summary>Creates a span.</summary>
    /// <param name="begin">When it starts, in seconds.</param>
    /// <param name="end">When it stops, in seconds. Before <paramref name="begin" /> to wrap.</param>
    /// <param name="tag">Whatever the application wants to hang off it.</param>
    public TimelineSpan(float begin, float end, object? tag = null) {
        Begin = begin;
        End = end;
        Tag = tag;
    }

    /// <summary>When it starts, in seconds.</summary>
    public float Begin { get; set; }

    /// <summary>When it stops, in seconds.</summary>
    public float End { get; set; }

    /// <summary>How long it takes to fade in, in seconds.</summary>
    public float EaseIn { get; set; }

    /// <summary>How long it takes to fade out, in seconds.</summary>
    public float EaseOut { get; set; }

    /// <summary>The most of it that ever applies, in <c>[0, 1]</c>. What the bar's height shows.</summary>
    public float Peak { get; set; } = 1f;

    /// <summary>Whatever the application wants to hang off it.</summary>
    public object? Tag { get; set; }

    /// <summary>Whether it runs past the end of the timeline and resumes at the start.</summary>
    public bool Wraps => End < Begin;

    /// <summary>How long it lasts, in seconds.</summary>
    /// <param name="duration">How long the timeline is, for a span that wraps.</param>
    /// <returns>The length.</returns>
    public float Length(float duration) => Wraps ? (End + duration) - Begin : End - Begin;

    /// <summary>How much of it is live at a moment, ramps included.</summary>
    /// <param name="time">When, in seconds.</param>
    /// <param name="duration">How long the timeline is, for a span that wraps.</param>
    /// <returns>The activation, in <c>[0, 1]</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The same arithmetic the runtime uses, so the drawn shape is the applied shape.</b> A
    ///     bar drawn from an approximation of the ramp is a bar that lies about the one thing it is
    ///     there to show.
    /// </remarks>
    public float Activation(float time, float duration) {
        var live = Wraps ? time >= Begin || time <= End : time >= Begin && time <= End;

        if (!live) {
            return 0f;
        }

        var into = time >= Begin ? time - Begin : (time + duration) - Begin;
        var span = Length(duration);
        var ramp = 1f;

        if (EaseIn > 0f) {
            ramp = MathF.Min(ramp, Math.Clamp(into / EaseIn, 0f, 1f));
        }

        if (EaseOut > 0f) {
            ramp = MathF.Min(ramp, Math.Clamp((span - into) / EaseOut, 0f, 1f));
        }

        return ramp * Math.Clamp(Peak, 0f, 1f);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Begin:0.###}–{End:0.###}");
}

/// <summary>Which part of a span a pointer is over.</summary>
public enum SpanGrip : byte {
    /// <summary>None of it.</summary>
    None,

    /// <summary>The left edge, which moves <see cref="TimelineSpan.Begin" />.</summary>
    Begin,

    /// <summary>The right edge, which moves <see cref="TimelineSpan.End" />.</summary>
    End,

    /// <summary>The middle, which moves both.</summary>
    Body
}

/// <summary>One row: a name, some keys or some spans, and optionally the curve they describe.</summary>
public sealed class TimelineTrack {
    readonly List<TimelineKey> keys = [];
    readonly List<TimelineSpan> spans = [];

    /// <summary>Creates a track.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="kind">Whether it holds instants or intervals.</param>
    public TimelineTrack(string name, TimelineTrackKind kind = TimelineTrackKind.Keys) {
        Name = name;
        Kind = kind;
    }

    /// <summary>What it is called.</summary>
    public string Name { get; set; }

    /// <summary>Whether it holds instants or intervals.</summary>
    public TimelineTrackKind Kind { get; }

    /// <summary>Its keys, in time order.</summary>
    public IReadOnlyList<TimelineKey> Keys => keys;

    /// <summary>Its spans, in start order.</summary>
    public IReadOnlyList<TimelineSpan> Spans => spans;

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

    /// <summary>Adds a span.</summary>
    /// <param name="begin">When it starts.</param>
    /// <param name="end">When it stops. Before <paramref name="begin" /> to wrap.</param>
    /// <param name="tag">Its tag.</param>
    /// <returns>The span.</returns>
    public TimelineSpan AddSpan(float begin, float end, object? tag = null) {
        var span = new TimelineSpan(begin, end, tag);

        spans.Add(span);
        SortSpans();

        return span;
    }

    /// <summary>Removes a span.</summary>
    /// <param name="span">The span.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(TimelineSpan span) => spans.Remove(span);

    /// <summary>Moves a span's ends and puts the list back in start order.</summary>
    /// <param name="span">The span.</param>
    /// <param name="begin">Where it starts now.</param>
    /// <param name="end">Where it stops now.</param>
    public void Move(TimelineSpan span, float begin, float end) {
        ArgumentNullException.ThrowIfNull(span);

        span.Begin = begin;
        span.End = end;

        SortSpans();
    }

    void Sort() => keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));

    void SortSpans() => spans.Sort(static (left, right) => left.Begin.CompareTo(right.Begin));
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

            foreach (var span in track.Spans) {
                Bar(context, timeline, bounds, span, top, height);
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

    /// <summary>Draws one span: its extent faintly, and its activation solid on top of it.</summary>
    /// <remarks>
    ///     ⚠ <b>Two shapes, because the extent and the activation are different facts.</b> The ramps
    ///     mean the bar is at full height for less of its length than it occupies, and a single
    ///     trapezoid would leave an author unable to see where the span actually ends. A span that
    ///     wraps is drawn as its two visible pieces, each with the part of the ramp that falls in it.
    /// </remarks>
    void Bar(DrawContext context, Timeline timeline, Rectangle bounds, TimelineSpan span, float top, float height) {
        var duration = MathF.Max(1e-4f, timeline.Duration);
        var selected = timeline.SpanSelection.Contains(span);
        var inset = height * 0.18f;
        var lane = height - (inset * 2f);

        if (lane <= 0f) {
            return;
        }

        // One piece for an ordinary span, two for one that straddles the end. Both pieces read the
        // activation at their own real time, so the ramp lands where it would when the clip plays.
        Span<Vector2> pieces = stackalloc Vector2[2];
        var count = 1;

        if (span.Wraps) {
            pieces[0] = new(span.Begin, duration);
            pieces[1] = new(0f, span.End);
            count = 2;
        } else {
            pieces[0] = new(span.Begin, span.End);
        }

        foreach (var piece in pieces[..count]) {
            var left = timeline.ToScreen(piece.X);
            var right = timeline.ToScreen(piece.Y);

            if (right < bounds.X - 2f || left > bounds.Right + 2f) {
                continue;
            }

            var clippedLeft = MathF.Max(left, bounds.X);
            var clippedRight = MathF.Min(right, bounds.Right);
            var width = MathF.Max(1f, clippedRight - clippedLeft);

            context.FillRectangle(
                new Rectangle(clippedLeft, top + inset, width, lane),
                selected ? timeline.SpanActiveColor : timeline.SpanColor,
                2f
            );

            // The activation, sampled a column at a time. A ramp is linear, so this is more samples
            // than the shape needs — but an eased ramp would not be, and the cost is a few dozen
            // points on a shape that is already being filled.
            path.Clear().MoveTo(new Vector2(clippedLeft, top + height - inset));

            var columns = (int) MathF.Ceiling(width);

            for (var column = 0; column <= columns; column++) {
                var x = MathF.Min(clippedLeft + column, clippedRight);
                var weight = span.Activation(timeline.ToTime(x), duration);

                path.LineTo(new Vector2(x, top + height - inset - (weight * lane)));
            }

            path.LineTo(new Vector2(clippedRight, top + height - inset)).Close();
            context.Fill(path, selected ? timeline.SpanActiveColor : timeline.SpanColor);

            // The grips, drawn only where the real end is on screen — a clipped edge is not one
            // somebody can take hold of, and drawing a handle there would say it is.
            foreach (var edge in (ReadOnlySpan<float>) [left, right]) {
                if (edge >= bounds.X && edge <= bounds.Right) {
                    context.FillRectangle(
                        new Rectangle(edge - 1f, top + inset, 2f, lane),
                        timeline.SpanGripColor
                    );
                }
            }
        }
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
    Scrub,
    Span
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
///     <para>
///         ⚠ <b>A track holds instants or intervals, never both</b>, and the two are drawn and hit
///         tested differently — see <see cref="TimelineTrackKind" />. A span is a bar with its ramps
///         drawn on it rather than a pair of keys, because a pair of keys loses the association
///         between the two the moment either is dragged, and because the <em>shape</em> of a fade is
///         the thing an author is trying to see.
///     </para>
/// </remarks>
public sealed partial class Timeline : Control {
    readonly List<TimelineTrack> tracks = [];
    readonly List<TimelineHeader> headers = [];
    readonly HashSet<TimelineKey> selection = [];
    readonly HashSet<TimelineSpan> spanSelection = [];
    readonly List<TimelineKey> moving = [];

    TimelineDrag drag;
    float dragTime;
    Vector2 bandOrigin;

    TimelineTrack? grabbedTrack;
    TimelineSpan? grabbed;
    SpanGrip grip;

    int gridColor;
    int stripeColor;
    int keyColor;
    int keyActiveColor;
    int playheadColor;
    int marqueeColor;
    int curveColor;
    int spanColor;
    int spanActiveColor;
    int spanGripColor;
    int trackHeightId;

    /// <inheritdoc />
    protected override string TagName => "timeline";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>ARIA <c>application</c>, and it is a role with a cost that is worth paying
    ///     here.</b> It tells assistive technology to stop intercepting the keyboard and pass every
    ///     key through, because this element has a keyboard model of its own that no generic widget
    ///     vocabulary describes. That is exactly true of a direct-manipulation surface — keys and spans dragged along a set of tracks — and it
    ///     is exactly false of a text field, which is why <c>CodeEditor</c> is a <c>textbox</c>
    ///     instead. Unnamed by default: what this one is a view of is the application's sentence,
    ///     and it is usually the panel title above it.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Application;

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

    /// <summary>Which spans are selected.</summary>
    /// <remarks>
    ///     Separate from <see cref="Selection" /> rather than one set of objects, because what an
    ///     application does with a selected span — read its ends, show its ramps — has nothing in
    ///     common with what it does with a key, and a single set would be cast at every use.
    /// </remarks>
    public IReadOnlyCollection<TimelineSpan> SpanSelection => spanSelection;

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

    /// <summary>How near a span's end a pointer has to be to take hold of it, in pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>A short span is all grip and no body.</b> Below twice this, the two ends would overlap
    ///     and the middle would be unreachable, so the reach shrinks with the bar rather than the bar
    ///     becoming undraggable — a two-frame contact is exactly the one somebody needs to nudge.
    /// </remarks>
    [UiProperty(Default = 5f)]
    public partial float GripSize { get; set; }

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

    /// <summary>Raised after a drag has moved a span's ends.</summary>
    public event Action<Timeline, TimelineSpan>? SpanMoved;

    /// <summary>Raised after a double-tap has taken a span off its track.</summary>
    /// <remarks>
    ///     The span is already gone when this arrives — the same shape as <see cref="KeysMoved" />,
    ///     which reports a gesture the timeline has already performed. An application backed by a
    ///     document uses it to make the removal an undoable edit rather than a divergence.
    /// </remarks>
    public event Action<Timeline, TimelineTrack, TimelineSpan>? SpanRemoved;

    /// <summary>Raised when a double-tap asks for a span on an empty stretch of a span track.</summary>
    /// <remarks>
    ///     A request rather than a fact: a span is added by whatever owns the document, because what
    ///     the new span <em>is</em> — which constraint, which sub-clip — is not something a timeline
    ///     can invent. Nothing is added if nobody is listening.
    /// </remarks>
    public event Action<Timeline, TimelineTrack, float>? SpanRequested;

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
        spanColor = Document.PropertyId("--span-color");
        spanActiveColor = Document.PropertyId("--span-active-color");
        spanGripColor = Document.PropertyId("--span-grip-color");
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
    /// <param name="kind">Whether it holds instants or intervals.</param>
    /// <returns>The track.</returns>
    public TimelineTrack AddTrack(string name, TimelineTrackKind kind = TimelineTrackKind.Keys) {
        var track = new TimelineTrack(name, kind);
        tracks.Add(track);

        Refresh();
        return track;
    }

    /// <summary>Removes a track, and takes its keys and spans out of the selection.</summary>
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

        foreach (var span in track.Spans) {
            spanSelection.Remove(span);
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

    /// <summary>The span under a document-space point, and which part of it.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="grip">Which part of it, or <see cref="SpanGrip.None" />.</param>
    /// <returns>The span, or <see langword="null" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The ends win over the body, and the last span drawn wins over the first.</b> Overlap
    ///     is ordinary on a span track and the one on top is the one somebody can see the edge of.
    /// </remarks>
    public TimelineSpan? SpanAt(float x, float y, out SpanGrip grip) {
        grip = SpanGrip.None;

        var index = TrackAt(y);

        if (index < 0) {
            return null;
        }

        var track = tracks[index];
        TimelineSpan? found = null;

        for (var at = track.Spans.Count - 1; at >= 0; at--) {
            var span = track.Spans[at];
            var hit = Grip(span, x);

            if (hit == SpanGrip.None) {
                continue;
            }

            found = span;
            grip = hit;

            // An edge is a smaller target than a body and the one somebody aimed at, so it stops the
            // search; a body keeps looking in case a span on top of it offers an edge here.
            if (hit != SpanGrip.Body) {
                break;
            }
        }

        return found;
    }

    SpanGrip Grip(TimelineSpan span, float x) {
        var duration = MathF.Max(1e-4f, Duration);
        var reach = MathF.Min(GripSize, MathF.Max(1f, span.Length(duration) * PixelsPerSecond * 0.25f));

        var begin = ToScreen(span.Begin);
        var end = ToScreen(span.End);

        if (MathF.Abs(x - begin) <= reach) {
            return SpanGrip.Begin;
        }

        if (MathF.Abs(x - end) <= reach) {
            return SpanGrip.End;
        }

        var time = ToTime(x);
        var live = span.Wraps ? time >= span.Begin || time <= span.End : time >= span.Begin && time <= span.End;

        return live ? SpanGrip.Body : SpanGrip.None;
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

    /// <summary>A span's colour.</summary>
    public Color4 SpanColor => Document.ColorOf(Style, spanColor) ?? new Color4(0.35f, 0.45f, 0.62f, 0.45f);

    /// <summary>A selected span's colour.</summary>
    public Color4 SpanActiveColor => Document.ColorOf(Style, spanActiveColor) ?? new Color4(0.23f, 0.42f, 0.94f, 0.55f);

    /// <summary>The colour of the handles at a span's ends.</summary>
    public Color4 SpanGripColor => Document.ColorOf(Style, spanGripColor) ?? new Color4(0.9f, 0.92f, 0.95f, 0.9f);

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Selects a key, or adds it to or removes it from the selection.</summary>
    /// <param name="key">The key, or <c>null</c> to select nothing.</param>
    /// <param name="modifiers">What was held.</param>
    public void Select(TimelineKey? key, ModifierKeys modifiers = ModifierKeys.None) {
        if (key is null) {
            SelectNone();
            return;
        }

        spanSelection.Clear();

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

    /// <summary>Selects nothing at all, keys and spans alike.</summary>
    public void SelectNone() {
        if (selection.Count == 0 && spanSelection.Count == 0) {
            return;
        }

        selection.Clear();
        spanSelection.Clear();

        Restate();
    }

    /// <summary>Selects a span, or adds it to or removes it from the selection.</summary>
    /// <param name="span">The span, or <c>null</c> to select nothing.</param>
    /// <param name="modifiers">What was held.</param>
    /// <remarks>
    ///     Selecting a span clears the key selection and the other way round: one thing is selected at
    ///     a time, because the panel beside a timeline shows one thing.
    /// </remarks>
    public void Select(TimelineSpan? span, ModifierKeys modifiers = ModifierKeys.None) {
        if (span is null) {
            if (spanSelection.Count == 0) {
                return;
            }

            spanSelection.Clear();
            Restate();

            return;
        }

        selection.Clear();

        if (modifiers.HasFlag(ModifierKeys.Control)) {
            if (!spanSelection.Remove(span)) {
                spanSelection.Add(span);
            }

            Restate();
            return;
        }

        if (!modifiers.HasFlag(ModifierKeys.Shift)) {
            spanSelection.Clear();
        }

        spanSelection.Add(span);
        Restate();
    }

    /// <summary>Selects every key and every span on every track.</summary>
    public void SelectAll() {
        selection.Clear();
        spanSelection.Clear();

        foreach (var track in tracks) {
            foreach (var key in track.Keys) {
                selection.Add(key);
            }

            foreach (var span in track.Spans) {
                spanSelection.Add(span);
            }
        }

        Restate();
    }

    /// <summary>Removes every selected key and span.</summary>
    public void DeleteSelection() {
        if (selection.Count == 0 && spanSelection.Count == 0) {
            return;
        }

        foreach (var track in tracks) {
            foreach (var key in track.Keys.ToArray()) {
                if (selection.Contains(key)) {
                    track.Remove(key);
                }
            }

            foreach (var span in track.Spans.ToArray()) {
                if (spanSelection.Contains(span)) {
                    track.Remove(span);
                }
            }
        }

        selection.Clear();
        spanSelection.Clear();

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

        var over = TrackAt(args.Y);

        if (over >= 0 && tracks[over].Kind == TimelineTrackKind.Spans) {
            if (SpanAt(args.X, args.Y, out var hit) is { } span) {
                Select(span, args.Modifiers);

                grabbed = span;
                grabbedTrack = tracks[over];
                grip = hit;
                dragTime = ToTime(args.X);
                drag = TimelineDrag.Span;

                Document.CapturePointer(this);
                return;
            }
        } else if (KeyAt(args.X, args.Y) is { } key) {
            Select(key, args.Modifiers);

            moving.Clear();
            moving.AddRange(selection);

            dragTime = ToTime(args.X);
            drag = TimelineDrag.Keys;

            Document.CapturePointer(this);
            return;
        }

        if (!args.Modifiers.HasFlag(ModifierKeys.Shift) && !args.Modifiers.HasFlag(ModifierKeys.Control)) {
            SelectNone();
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

            case TimelineDrag.Span:
                Stretch(ToTime(args.X));
                break;

            case TimelineDrag.Marquee:
                Marquee = Rectangle.FromCorners(bandOrigin, new Vector2(args.X, args.Y));
                Document.Invalidate();

                break;

            default:
                break;
        }
    }

    /// <summary>Moves whichever part of the grabbed span the pointer took hold of.</summary>
    /// <remarks>
    ///     ⚠ <b>Dragging an end past the other makes the span wrap rather than refusing.</b> That is
    ///     the only gesture that produces a span across the loop point, and a timeline that clamped
    ///     instead would make the one span an author most often needs unauthorable.
    /// </remarks>
    void Stretch(float now) {
        if (grabbed is not { } span || grabbedTrack is not { } track) {
            return;
        }

        var duration = MathF.Max(1e-4f, Duration);
        var delta = now - dragTime;

        switch (grip) {
            case SpanGrip.Begin:
                track.Move(span, Math.Clamp(Snap(span.Begin + delta), 0f, duration), span.End);
                break;

            case SpanGrip.End:
                track.Move(span, span.Begin, Math.Clamp(Snap(span.End + delta), 0f, duration));
                break;

            case SpanGrip.Body when span.Wraps:
                // Already across the loop, so both ends stay across it: a wrapping span slides round
                // rather than piling up against an edge it is not on.
                track.Move(span, Around(Snap(span.Begin + delta), duration), Around(Snap(span.End + delta), duration));
                break;

            case SpanGrip.Body:
                var shift = Math.Clamp(delta, -span.Begin, duration - span.End);
                track.Move(span, Snap(span.Begin + shift), Snap(span.End + shift));

                break;

            default:
                return;
        }

        dragTime = now;
        Document.Invalidate();
    }

    static float Around(float time, float duration) {
        var wrapped = time % duration;
        return wrapped < 0f ? wrapped + duration : wrapped;
    }

    void Finish() {
        if (drag == TimelineDrag.Keys) {
            KeysMoved?.Invoke(this);
        } else if (drag == TimelineDrag.Span && grabbed is { } span) {
            SpanMoved?.Invoke(this, span);
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

                // A span is caught by overlapping the band, not by being inside it: a band drawn over
                // the middle of a long bar is a band somebody drew over that bar.
                foreach (var entry in tracks[i].Spans) {
                    if (ToScreen(entry.Begin) <= band.Right && ToScreen(entry.End) >= band.Left) {
                        spanSelection.Add(entry);
                    }
                }
            }

            Restate();
        }

        drag = TimelineDrag.None;
        Marquee = null;

        grabbed = null;
        grabbedTrack = null;
        grip = SpanGrip.None;

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

        if (tracks[index].Kind == TimelineTrackKind.Spans) {
            if (SpanAt(args.X, args.Y, out _) is { } span) {
                tracks[index].Remove(span);
                spanSelection.Remove(span);

                SpanRemoved?.Invoke(this, tracks[index], span);
                Restate();
            } else {
                SpanRequested?.Invoke(this, tracks[index], Snap(ToTime(args.X)));
            }

            args.Handled = true;
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
