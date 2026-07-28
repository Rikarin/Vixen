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

    readonly Lock gate = new();
    readonly List<AudioBus> children = [];

    IAudioEffect[] effects = NoEffects;
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

    internal Span<float> Buffer => buffer;

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

    /// <summary>Runs the effects, measures the result, and hands it up.</summary>
    /// <param name="frames">How many frames.</param>
    /// <returns>The gain to apply on the way out, zero if muted.</returns>
    internal float Finish(int frames) {
        var samples = frames * format.Channels;
        var span = buffer.AsSpan(0, samples);
        var chain = effects;

        foreach (var effect in chain) {
            effect.Process(span, frames, format.Channels);
        }

        var gain = Muted ? 0f : Gain;
        var loudest = 0f;

        for (var i = 0; i < samples; i++) {
            var value = MathF.Abs(span[i] * gain);

            if (value > loudest) {
                loudest = value;
            }
        }

        peak = loudest;
        return gain;
    }
}
