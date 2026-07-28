// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;

namespace Vixen.Audio.Music;

/// <summary>Music that reacts, without gameplay knowing what a bar is.</summary>
/// <remarks>
///     <para>
///         <b>The problem this solves is timing, not sequencing.</b> Playing one piece of music after
///         another is a queue and needs nothing; the hard part is that gameplay decides to change the
///         music at an arbitrary instant and music cannot change at an arbitrary instant. Everything
///         here is arranged around separating "a fight started" from "the music changes at the top of
///         the next bar".
///     </para>
///     <para>
///         <b>The incoming segment is scheduled to the sample.</b> Its start is an absolute frame on
///         the device's own clock, so it lands where it was asked to whatever the frame rate is doing.
///         The outgoing one is faded over <see cref="CrossfadeSeconds" /> rather than cut, because a
///         hard cut between two unrelated pieces of music is a click at worst and a seam at best,
///         while a fortieth of a second of overlap is inaudible and standard. The join that matters
///         musically is where the <em>new</em> material begins, and that one is exact.
///     </para>
///     <para>
///         <b>Looping is the provider's, not a rescheduling.</b> A looping segment is one voice with
///         <c>Loop</c> set, so the wrap is seamless by construction — restarting the clip each time
///         round would put a block boundary at every loop point, which is the seam every naive music
///         system has.
///     </para>
///     <para>
///         <b>Game thread only, and <see cref="Update" /> must be called each frame.</b> Nothing here
///         runs on the audio thread; what reaches it is a <see cref="PlaybackSettings.StartFrame" />
///         and the ordinary voice fields.
///     </para>
/// </remarks>
public sealed class MusicPlayer {
    readonly AudioEngine engine;
    readonly List<MusicSegment> segments = [];
    readonly List<MusicTransition> transitions = [];

    VoiceHandle voice = VoiceHandle.None;
    long currentEnd = long.MaxValue;
    long lastBeat = long.MinValue;
    long lastBar = long.MinValue;
    long lastFrame;
    int lastMarker = -1;

    /// <summary>A player on an engine.</summary>
    /// <param name="engine">Where the music goes.</param>
    /// <param name="bus">Which bus. Zero is the master; a real game gives music its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine" /> is null.</exception>
    public MusicPlayer(AudioEngine engine, int bus = 0) {
        ArgumentNullException.ThrowIfNull(engine);
        this.engine = engine;
        Bus = bus;
        Transport = new(engine.Format.SampleRate);
    }

    /// <summary>Where the music is, in samples the device actually produced.</summary>
    public MusicTransport Transport { get; }

    /// <summary>Which bus the music plays on.</summary>
    public int Bus { get; }

    /// <summary>How long the outgoing segment takes to get out of the way.</summary>
    /// <remarks>
    ///     Forty milliseconds. Long enough that no join clicks, short enough that nobody hears two
    ///     pieces of music at once. Set it to zero for a hard cut, which is right when the segments
    ///     were composed to butt together and wrong otherwise.
    /// </remarks>
    public float CrossfadeSeconds { get; set; } = 0.04f;

    /// <summary>What is playing.</summary>
    public MusicSegment? Current { get; private set; }

    /// <summary>What is scheduled to play next, if anything.</summary>
    public MusicSegment? Queued { get; private set; }

    /// <summary>The device frame <see cref="Queued" /> begins at.</summary>
    public long QueuedAtFrame { get; private set; }

    /// <summary>Whether anything is playing or scheduled.</summary>
    public bool IsPlaying => Current is not null || Queued is not null;

    /// <summary>Whether the music is holding at a sustain point, waiting to be let go.</summary>
    public bool IsSustaining => Current?.Sustains == true && !released;

    /// <summary>Lets a sustaining segment move on.</summary>
    /// <returns>Whether it was sustaining.</returns>
    /// <remarks>
    ///     <b>It does not cut anything short.</b> Releasing says the music may proceed, and the
    ///     proceeding still happens where it is allowed to — at the end of the pass for a segment
    ///     following its <c>Next</c>, or at the bar line a queued transition asked for. A release
    ///     that took effect immediately would be a jump cut, which is the thing a sustain point exists
    ///     to avoid.
    /// </remarks>
    public bool Release() {
        if (!IsSustaining) {
            return false;
        }

        released = true;

        // Whatever was asked for while it was held is now allowed to land, from here rather than
        // from when it was asked for — a transition queued four bars ago wants the next bar line,
        // not one four bars in the past.
        if (Queued is { } waiting) {
            var quantize = queuedQuantize;
            CancelQueued();
            Schedule(waiting, quantize);
        } else if (Current is { } current && current.LoopCount < 0) {
            // A sustaining loop has no natural end, so releasing gives it one.
            currentEnd = Transport.NextBoundary(engine.RenderedFrames, MusicQuantize.Segment, SegmentFrames(current));
        }

        return true;
    }

    /// <summary>Raised as the playhead crosses a beat, with its number from the segment's start.</summary>
    /// <remarks>
    ///     <b>Late by up to a frame, unavoidably.</b> It is raised from <see cref="Update" /> on the
    ///     game thread, because that is the only thread a game may do anything on. Fine for a light,
    ///     a camera shake or an animation; not what a <em>musical</em> change is scheduled with, which
    ///     is what <see cref="TransitionTo" /> is for.
    /// </remarks>
    public event Action<long>? BeatPassed;

    /// <summary>Raised as the playhead crosses a bar line.</summary>
    public event Action<long>? BarPassed;

    /// <summary>Raised as the playhead crosses a named marker.</summary>
    public event Action<string>? MarkerPassed;

    /// <summary>Raised when a segment actually begins, rather than when it was scheduled.</summary>
    public event Action<MusicSegment>? SegmentStarted;

    /// <summary>Adds a segment it can play.</summary>
    /// <param name="segment">The segment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segment" /> is null.</exception>
    public void Add(MusicSegment segment) {
        ArgumentNullException.ThrowIfNull(segment);
        segments.Add(segment);
    }

    /// <summary>Adds a rule about when the music may change on its own.</summary>
    /// <param name="transition">The rule.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transition" /> is null.</exception>
    public void AddTransition(MusicTransition transition) {
        ArgumentNullException.ThrowIfNull(transition);
        transitions.Add(transition);
    }

    /// <summary>Finds a segment by name.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>It, or null.</returns>
    public MusicSegment? Find(string name) {
        foreach (var segment in segments) {
            if (string.Equals(segment.Name, name, StringComparison.Ordinal)) {
                return segment;
            }
        }

        return null;
    }

    /// <summary>Starts a segment now, cutting off whatever was playing.</summary>
    /// <param name="name">Which segment.</param>
    /// <returns>Whether there was such a segment.</returns>
    public bool Play(string name) {
        if (Find(name) is not { } segment) {
            return false;
        }

        LetGo();
        Queued = null;
        Begin(segment, engine.RenderedFrames);
        return true;
    }

    /// <summary>Asks for a segment, to land where it is allowed to.</summary>
    /// <param name="name">Which segment.</param>
    /// <param name="quantize">Where it is allowed to land.</param>
    /// <returns>Whether there was such a segment.</returns>
    /// <remarks>
    ///     <b>Scheduled the instant it is asked for, not when its moment arrives.</b> The voice is
    ///     started immediately with a start frame in the future and waits there, so nothing between
    ///     the request and the boundary — a long frame, a level load, a breakpoint — can make it late.
    ///     It costs one voice for the length of the wait, which for music is a trade nobody would
    ///     hesitate over.
    /// </remarks>
    public bool TransitionTo(string name, MusicQuantize quantize = MusicQuantize.Bar) {
        if (Find(name) is not { } segment) {
            return false;
        }

        return Schedule(segment, quantize);
    }

    /// <summary>Fires a one-shot over the top of whatever is playing.</summary>
    /// <param name="clip">The hit.</param>
    /// <param name="quantize">Where it is allowed to land.</param>
    /// <param name="gainDb">How loud, against the music.</param>
    /// <returns>Its handle, or <see cref="VoiceHandle.None" /> if the pool was full.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A stinger is not a transition.</b> It plays alongside the music rather than instead
    ///         of it, and it changes nothing about where the music is going — which is what makes it
    ///         the right answer to "something happened" and the wrong answer to "the situation
    ///         changed".
    ///     </para>
    ///     <para>
    ///         Quantised like everything else here, because a hit landing off the beat is the same
    ///         mistake a cut landing off the beat is, and rather more obvious.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="clip" /> is null.</exception>
    public VoiceHandle PlayStinger(AudioClip clip, MusicQuantize quantize = MusicQuantize.Beat, float gainDb = 0f) {
        ArgumentNullException.ThrowIfNull(clip);
        var now = engine.RenderedFrames;

        return engine.Play(clip, new PlaybackSettings {
            Bus = Bus,
            Gain = gainDb == 0f ? 1f : Effects.Decibels.ToLinear(gainDb),

            // Below the music, which is the one thing that must never be displaced, and above
            // everything else — a stinger that lost its slot to a footstep would be worse than none.
            Priority = int.MaxValue - 1,
            StartFrame = Current is null ? now : Transport.NextBoundary(now, quantize, SegmentFrames(Current))
        });
    }

    /// <summary>Stops the music.</summary>
    /// <param name="quantize">Where the stop is allowed to land.</param>
    public void Stop(MusicQuantize quantize = MusicQuantize.Immediate) {
        CancelQueued();

        if (quantize is MusicQuantize.Immediate) {
            LetGo();
            Current = null;
            currentEnd = long.MaxValue;
            return;
        }

        // A stop is a transition to nothing, so it goes through the same boundary arithmetic — and
        // ends up in the same place a transition would have, which is what makes the two consistent.
        currentEnd = Transport.NextBoundary(engine.RenderedFrames, quantize, SegmentFrames(Current));
        stopping = true;
    }

    /// <summary>Advances the music. Once a frame, on the game thread.</summary>
    /// <remarks>
    ///     It reads the device's clock rather than being given a delta: a frame time is what the game
    ///     thread thinks happened, and the samples the hardware produced are what actually did.
    /// </remarks>
    public void Update() {
        var now = engine.RenderedFrames;

        if (Queued is { } queued && now >= QueuedAtFrame && !IsSustaining) {
            LetGo();
            var start = QueuedAtFrame;
            Queued = null;
            QueuedAtFrame = 0;
            Begin(queued, start, alreadyStarted: true);
        }

        Notify(now);

        if (Current is not null && now >= currentEnd && !IsSustaining) {
            Advance(now);
        }

        if (Queued is null && !stopping) {
            Consider(now);
        }

        lastFrame = now;
    }

    bool stopping;
    bool released;
    MusicQuantize queuedQuantize = MusicQuantize.Bar;

    /// <summary>Raises everything the playhead has crossed since the last call.</summary>
    /// <remarks>
    ///     From where it was to where it is, so nothing is missed by a frame that ran long — a
    ///     twenty-beat gap raises twenty beats. Which is occasionally a lot of callbacks at once, and
    ///     is still better than a game that quietly skipped nineteen of them.
    /// </remarks>
    void Notify(long now) {
        if (Current is null || now <= lastFrame) {
            return;
        }

        var beat = Transport.BeatAt(now);

        if (beat > lastBeat) {
            for (var i = Math.Max(lastBeat + 1, 0); i <= beat; i++) {
                BeatPassed?.Invoke(i);
            }

            lastBeat = beat;
        }

        var bar = Transport.BarAt(now);

        if (bar > lastBar) {
            for (var i = Math.Max(lastBar + 1, 0); i <= bar; i++) {
                BarPassed?.Invoke(i);
            }

            lastBar = bar;
        }

        var perBeat = Current.Tempo.FramesPerBeat(Transport.SampleRate);

        if (perBeat <= 0) {
            return;
        }

        // Markers are in order of the array rather than of time, so the cursor is over the array and
        // every one not yet raised is tested. A segment has a handful.
        var position = Transport.PositionAt(now);

        for (var i = lastMarker + 1; i < Current.Markers.Length; i++) {
            if ((long)(Current.Markers[i].Beat * perBeat) > position) {
                break;
            }

            lastMarker = i;
            MarkerPassed?.Invoke(Current.Markers[i].Name);
        }
    }

    /// <summary>Decides what happens when a segment runs out.</summary>
    void Advance(long now) {
        var finished = Current;
        LetGo();
        Current = null;
        currentEnd = long.MaxValue;

        if (stopping) {
            stopping = false;
            return;
        }

        if (finished is not null && !string.IsNullOrEmpty(finished.Next) && Find(finished.Next) is { } next) {
            Begin(next, now);
        }
    }

    /// <summary>Takes the first declared transition that applies, if any does.</summary>
    void Consider(long now) {
        if (engine.Parameters is not { } parameters) {
            return;
        }

        foreach (var transition in transitions) {
            if (string.IsNullOrEmpty(transition.Parameter)) {
                continue;
            }

            if (!string.IsNullOrEmpty(transition.From)
                && !string.Equals(transition.From, Current?.Name, StringComparison.Ordinal)) {
                continue;
            }

            if (string.Equals(transition.To, Current?.Name, StringComparison.Ordinal)) {
                continue;
            }

            var index = parameters.IndexOf(transition.Parameter);

            if (index < 0) {
                continue;
            }

            var value = parameters.ValueOf(index);

            if (value < transition.Minimum || value > transition.Maximum) {
                continue;
            }

            if (Find(transition.To) is { } target) {
                Schedule(target, transition.Quantize);
            }

            return;
        }

        _ = now;
    }

    bool Schedule(MusicSegment segment, MusicQuantize quantize) {
        var now = engine.RenderedFrames;

        if (Current is null || quantize is MusicQuantize.Immediate) {
            LetGo();
            CancelQueued();
            Begin(segment, now);
            return true;
        }

        var at = Transport.NextBoundary(now, quantize, SegmentFrames(Current));
        CancelQueued();

        Queued = segment;
        QueuedAtFrame = at;
        queuedQuantize = quantize;

        // A sustaining segment has no idea when it will be let go, so there is nothing to schedule
        // against yet — the voice is started when Release works out where the boundary actually is.
        queuedVoice = IsSustaining ? VoiceHandle.None : Start(segment, at);
        return true;
    }

    VoiceHandle queuedVoice = VoiceHandle.None;

    /// <summary>Makes a segment the current one, from a frame.</summary>
    void Begin(MusicSegment segment, long start, bool alreadyStarted = false) {
        Current = segment;
        Transport.Start(start, new MusicTempoMap(segment.Tempo, segment.TempoChanges, Transport.SampleRate));
        lastBeat = long.MinValue;
        lastBar = long.MinValue;
        lastMarker = -1;
        lastFrame = start;
        released = false;

        var frames = SegmentFrames(segment);

        currentEnd = segment.LoopCount < 0 || frames <= 0
            ? long.MaxValue
            : start + (frames * (segment.LoopCount + 1));

        if (alreadyStarted) {
            voice = queuedVoice;
            queuedVoice = VoiceHandle.None;
        } else {
            voice = Start(segment, start);
        }

        SegmentStarted?.Invoke(segment);
    }

    VoiceHandle Start(MusicSegment segment, long at) =>
        segment.Clip is { } clip
            ? engine.Play(clip, new PlaybackSettings {
                Bus = Bus,
                Loop = segment.LoopCount != 0,

                // Music is the last thing that should ever be displaced, and it is one voice.
                Priority = int.MaxValue,
                StartFrame = at
            })
            : VoiceHandle.None;

    /// <summary>Lets the current voice go, over the crossfade.</summary>
    void LetGo() {
        if (!voice.IsValid) {
            return;
        }

        if (CrossfadeSeconds > 0f) {
            engine.FadeOutAndStop(voice, TimeSpan.FromSeconds(CrossfadeSeconds));
        } else {
            engine.Stop(voice);
        }

        voice = VoiceHandle.None;
    }

    void CancelQueued() {
        if (queuedVoice.IsValid) {
            // Stopped rather than faded: it has not been heard yet, so there is nothing to fade.
            engine.Stop(queuedVoice);
            queuedVoice = VoiceHandle.None;
        }

        Queued = null;
        QueuedAtFrame = 0;
    }

    /// <summary>How long one pass of a segment is, in device frames.</summary>
    long SegmentFrames(MusicSegment? segment) {
        if (segment?.Clip is not { } clip || clip.SampleRate <= 0) {
            return 0;
        }

        // In the device's frames and not the clip's, because everything else here is — a clip at
        // 44 100 played on a 48 000 device lasts longer than its own frame count says.
        return (long)((double)clip.FrameCount * Transport.SampleRate / clip.SampleRate);
    }
}
