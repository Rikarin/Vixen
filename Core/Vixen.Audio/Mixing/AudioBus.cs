// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;

namespace Vixen.Audio.Mixing;

/// <summary>A place voices sum into, on their way to the master.</summary>
/// <remarks>
///     <para>
///         The reason a game has a music slider. Every voice names a bus, every bus names a parent,
///         and the tree is summed from the leaves up — so <c>Music.Gain = 0.2f</c> is one write that
///         reaches every track playing and every track that will play, and an effect on a bus is
///         computed once for everything routed into it.
///     </para>
///     <para>
///         <b>Gain and mute are written directly from the game thread and read from the audio
///         thread, with no command and no lock.</b> Both are single values the CLR guarantees are
///         written atomically — a <see cref="float" /> and a <see cref="bool" /> — so the worst a
///         race can do is apply a new gain one block later than it was set. Buying atomicity for
///         that with a queue would make a volume slider feel worse, not better.
///     </para>
///     <para>
///         <b>The effect chain is a snapshot, swapped whole.</b> Adding an effect builds a new array
///         and publishes it with one write; the audio thread reads the reference once per block and
///         works from what it got. So the chain is never observed half-modified, and neither side
///         waits for the other.
///     </para>
/// </remarks>
public sealed class AudioBus {
    static readonly IAudioEffect[] NoEffects = [];
    static readonly AudioSend[] NoSends = [];

    readonly Lock gate = new();
    readonly List<AudioBus> children = [];

    IAudioEffect[] effects = NoEffects;
    AudioSend[] sends = NoSends;
    AudioMixer? owner;
    FadeState fade;
    float[] buffer = [];
    AudioFormat format;
    int maxFrames;
    float peak;

    internal AudioBus(int index, string name, AudioBus? parent) {
        Index = index;
        Name = name;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        parent?.children.Add(this);
    }

    /// <summary>Its index, which is what <see cref="PlaybackSettings.Bus" /> holds. The master is zero.</summary>
    public int Index { get; }

    /// <summary>What it is called.</summary>
    public string Name { get; }

    /// <summary>What it sums into, or <see langword="null" /> for the master.</summary>
    public AudioBus? Parent { get; }

    /// <summary>How far it is from the master. The master is zero.</summary>
    public int Depth { get; }

    /// <summary>Buses that sum into this one.</summary>
    public IReadOnlyList<AudioBus> Children => children;

    /// <summary>Its linear gain.</summary>
    public float Gain { get; set; } = 1f;

    /// <summary>Whether it contributes nothing to its parent.</summary>
    /// <remarks>
    ///     Voices routed into a muted bus still play, still advance, and still finish. Muting is not
    ///     pausing, and a game that muted the music bus during a cutscene expects the track to be
    ///     where it should be when the cutscene ends.
    /// </remarks>
    public bool Muted { get; set; }

    /// <summary>The effects on it, in the order they run.</summary>
    public IReadOnlyList<IAudioEffect> Effects => effects;

    /// <summary>
    ///     The loudest sample this bus produced in the last block, after its effects and its gain.
    /// </summary>
    /// <remarks>What the mixer-levels part of the audio debug overlay reads. Anything above 1 is clipping.</remarks>
    public float PeakLevel => peak;

    /// <summary>Takes this bus's gain somewhere else over time.</summary>
    /// <param name="gain">Where it is going.</param>
    /// <param name="duration">How long to take. Zero or less arrives at once.</param>
    /// <param name="curve">Which way. Decibels by default, because that is what sounds like a fade.</param>
    /// <remarks>
    ///     <para>
    ///         What a cutscene, a pause menu and a level transition all want, and the thing everybody
    ///         writes by hand against <see cref="Gain" /> otherwise — badly, because doing it linearly
    ///         is the obvious way and the wrong one.
    ///     </para>
    ///     <para>
    ///         Stepped by <c>AudioEngine.Update</c>, so it runs on game time: it stops when the game
    ///         is paused and slows down in slow motion. A second call replaces the fade in progress
    ///         from wherever it had got to, so fading out and changing your mind does not jump.
    ///     </para>
    /// </remarks>
    public void FadeTo(float gain, TimeSpan duration, AudioFadeCurve curve = AudioFadeCurve.Decibel) {
        fade = new FadeState {
            Active = true,
            From = Gain,
            To = gain,
            Duration = (float)duration.TotalSeconds,
            Curve = curve
        };

        if (fade.Duration <= 0f) {
            Gain = gain;
            fade.Active = false;
        }
    }

    /// <summary>Whether a fade is running on this bus.</summary>
    public bool IsFading => fade.Active;

    /// <summary>Stops a fade where it is, leaving the gain at whatever it had reached.</summary>
    public void CancelFade() => fade.Active = false;

    /// <summary>Adds an effect to the end of the chain.</summary>
    /// <param name="effect">The effect. Prepared here, so it is ready before the next block.</param>
    /// <exception cref="ArgumentNullException"><paramref name="effect" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The mixer has not been prepared yet.</exception>
    public void AddEffect(IAudioEffect effect) {
        ArgumentNullException.ThrowIfNull(effect);

        if (!format.IsValid) {
            throw new InvalidOperationException(
                "The bus does not know its format yet, so an effect added to it could not be sized. "
                + "Open a device — or call AudioMixer.Prepare — before building the effect chain."
            );
        }

        effect.Prepare(format, maxFrames);

        lock (gate) {
            effects = [.. effects, effect];
        }
    }

    /// <summary>Takes an effect off the chain.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveEffect(IAudioEffect effect) {
        lock (gate) {
            var index = Array.IndexOf(effects, effect);

            if (index < 0) {
                return false;
            }

            var replacement = new IAudioEffect[effects.Length - 1];
            Array.Copy(effects, replacement, index);
            Array.Copy(effects, index + 1, replacement, index, effects.Length - index - 1);
            effects = replacement;
            return true;
        }
    }

    /// <summary>Clears every effect's memory of what came before.</summary>
    public void ResetEffects() {
        foreach (var effect in effects) {
            effect.Reset();
        }
    }

    /// <summary>Sends a copy of this bus's signal to another one.</summary>
    /// <param name="target">Where the copy goes. Usually an aux bus carrying a reverb.</param>
    /// <param name="level">How much of it, as a linear gain.</param>
    /// <param name="preFader">
    ///     Whether the copy is taken before this bus's own gain and mute are applied.
    /// </param>
    /// <returns>The send, so its level can be changed later.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The target already reaches this bus, so the send would be a loop.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>This is what makes one reverb serve a whole level.</b> An effect added with
    ///         <see cref="AddEffect" /> is an <em>insert</em>: it processes the bus it is on, and
    ///         everything routed there gets all of it. A reverb wanted by six buses at six different
    ///         amounts would be six reverbs. A send is the other arrangement — one reverb on an aux
    ///         bus, and six sends into it — and it is how every mixer that has ever existed does this.
    ///     </para>
    ///     <para>
    ///         <b>Post-fader by default.</b> Pulling a bus's fader down should take its reverb with
    ///         it, or the tail of a muted bus keeps playing. Pre-fader is for the cases where the
    ///         signal is wanted regardless of what the listener hears — a compressor key being the
    ///         one that matters here.
    ///     </para>
    /// </remarks>
    public AudioSend AddSend(AudioBus target, float level = 1f, bool preFader = false) {
        ArgumentNullException.ThrowIfNull(target);

        if (ReferenceEquals(target, this) || Reaches(target, this)) {
            throw new ArgumentException(
                $"A send from '{Name}' to '{target.Name}' would be a loop: '{target.Name}' already "
                + "reaches it through a parent or another send. A cycle in the graph has no order to "
                + "be rendered in, and it would feed back.",
                nameof(target)
            );
        }

        var send = new AudioSend(target, preFader) { Level = level };

        lock (gate) {
            sends = [.. sends, send];
        }

        owner?.Invalidate();
        return send;
    }

    /// <summary>Takes a send off.</summary>
    /// <param name="send">The send.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveSend(AudioSend send) {
        lock (gate) {
            var index = Array.IndexOf(sends, send);

            if (index < 0) {
                return false;
            }

            var replacement = new AudioSend[sends.Length - 1];
            Array.Copy(sends, replacement, index);
            Array.Copy(sends, index + 1, replacement, index, sends.Length - index - 1);
            sends = replacement;
        }

        owner?.Invalidate();
        return true;
    }

    /// <summary>Makes another bus's signal available to this bus's keyed effects.</summary>
    /// <param name="source">The bus to listen to, or <see langword="null" /> to stop.</param>
    /// <exception cref="ArgumentException">
    ///     The source is downstream of this bus, so its signal would be a block old.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         What ducking is made of. Set the music bus's sidechain to the dialogue bus, put a
    ///         <c>CompressorEffect</c> on the music bus, and the music drops whenever anybody speaks.
    ///     </para>
    ///     <para>
    ///         The source must be rendered before this bus, which is a constraint on the render
    ///         order rather than a suggestion — reading a bus that has not been filled yet would key
    ///         the compressor off last block's signal, which is both wrong and impossible to debug by
    ///         ear.
    ///     </para>
    /// </remarks>
    public void SetSidechain(AudioBus? source) {
        if (source is not null && (ReferenceEquals(source, this) || Reaches(this, source))) {
            throw new ArgumentException(
                $"'{source.Name}' cannot key '{Name}': it is downstream, so it has not been rendered "
                + "when '{Name}' runs. Key from a bus that feeds this one, or from an unrelated one.",
                nameof(source)
            );
        }

        SidechainSource = source;
        owner?.Invalidate();
    }

    /// <summary>Which bus keys this one's sidechained effects, if any.</summary>
    public AudioBus? SidechainSource { get; private set; }

    /// <summary>The sends off this bus.</summary>
    public IReadOnlyList<AudioSend> Sends => sends;

    internal Span<float> Buffer => buffer;

    internal void StepFade(float deltaSeconds) {
        if (!fade.Active) {
            return;
        }

        if (fade.Step(deltaSeconds, out var gain)) {
            fade.Active = false;
        }

        Gain = gain;
    }

    internal void Attach(AudioMixer mixer) => owner = mixer;

    internal void Prepare(in AudioFormat deviceFormat, int frames) {
        format = deviceFormat;
        maxFrames = frames;
        buffer = new float[frames * deviceFormat.Channels];
        peak = 0f;

        foreach (var effect in effects) {
            effect.Prepare(deviceFormat, frames);
        }
    }

    internal void Clear(int frames) => buffer.AsSpan(0, frames * format.Channels).Clear();

    /// <summary>
    ///     Runs the effects, feeds the sends, applies the fader, and leaves the result in the buffer.
    /// </summary>
    /// <param name="frames">How many frames.</param>
    /// <remarks>
    ///     <b>The gain is applied in place, and that is load-bearing.</b> It used to be handed back
    ///     for the caller to apply while summing into the parent, which meant the buffer held a
    ///     pre-fader signal — so a send reading it, or a compressor keying off it, would have ignored
    ///     the fader entirely. Doing it here means the buffer always holds what this bus actually
    ///     contributes, and everything downstream reads the same thing the listener hears.
    /// </remarks>
    internal void Finish(int frames) {
        var samples = frames * format.Channels;
        var span = buffer.AsSpan(0, samples);
        var key = SidechainSource;

        foreach (var effect in effects) {
            if (effect is ISidechainEffect keyed && key is not null) {
                keyed.Process(span, key.buffer.AsSpan(0, samples), frames, format.Channels);
                continue;
            }

            effect.Process(span, frames, format.Channels);
        }

        var gain = Muted ? 0f : Gain;

        // Pre-fader sends are taken here, before the gain lands; post-fader ones are scaled by it.
        // Both have to happen before the buffer is faded, because after that the pre-fader signal is
        // gone.
        foreach (var send in sends) {
            var level = send.Level * (send.PreFader ? 1f : gain);

            if (level == 0f) {
                continue;
            }

            var target = send.Target.buffer;

            for (var i = 0; i < samples; i++) {
                target[i] += span[i] * level;
            }
        }

        var loudest = 0f;

        for (var i = 0; i < samples; i++) {
            var value = span[i] * gain;
            span[i] = value;
            var magnitude = MathF.Abs(value);

            if (magnitude > loudest) {
                loudest = magnitude;
            }
        }

        peak = loudest;
    }

    /// <summary>Whether <paramref name="from" /> can reach <paramref name="to" />, parents and sends alike.</summary>
    /// <remarks>
    ///     The cycle check both <see cref="AddSend" /> and <see cref="SetSidechain" /> run before
    ///     they change anything. Depth-first over a graph that is a handful of nodes; it is called
    ///     when a mixer is built and never in a frame.
    /// </remarks>
    static bool Reaches(AudioBus from, AudioBus to) {
        if (ReferenceEquals(from, to)) {
            return true;
        }

        if (from.Parent is { } parent && Reaches(parent, to)) {
            return true;
        }

        foreach (var send in from.sends) {
            if (Reaches(send.Target, to)) {
                return true;
            }
        }

        return false;
    }
}
