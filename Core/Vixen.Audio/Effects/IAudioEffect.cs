// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Something that changes a bus's signal on its way to the parent.</summary>
/// <remarks>
///     <para>
///         <b>In place, on the bus, and never on a voice.</b> A reverb on a bus is computed once for
///         everything routed into it; a reverb per voice is the same computation forty times over
///         and does not even sound right — two sounds in one room share the room. Anything that
///         genuinely is per-voice is a property of the voice: gain, pitch, the distance filter the
///         spatialiser applies.
///     </para>
///     <para>
///         <b><see cref="Process" /> runs on the audio thread.</b> No allocation, no locks. All
///         state is sized in <see cref="Prepare" />, which the bus calls when the effect is added
///         and the format is already known.
///     </para>
///     <para>
///         <b>Parameters are plain properties, written from the game thread while the audio thread
///         reads them.</b> That is a deliberate, documented race: every parameter is a
///         <see cref="float" /> or an <see cref="int" />, both of which are written atomically on
///         every platform .NET supports, so the worst outcome is one block rendered with a mix of
///         old and new values. The alternative — routing every knob turn through the command queue —
///         costs more than the artefact it prevents, which is inaudible.
///     </para>
/// </remarks>
public interface IAudioEffect {
    /// <summary>Whether it does anything. A bypassed effect costs one branch a block.</summary>
    bool Enabled { get; set; }

    /// <summary>Sizes and clears every buffer the effect needs.</summary>
    /// <param name="format">What it will be processing.</param>
    /// <param name="maxFrames">The most frames one <see cref="Process" /> can be given.</param>
    void Prepare(in AudioFormat format, int maxFrames);

    /// <summary>Processes a block in place.</summary>
    /// <param name="buffer">Interleaved, <c>frameCount × channels</c> floats.</param>
    /// <param name="frameCount">How many frames.</param>
    /// <param name="channels">How many channels are interleaved.</param>
    void Process(Span<float> buffer, int frameCount, int channels);

    /// <summary>Throws away everything the effect remembers about what came before.</summary>
    /// <remarks>
    ///     What a scene change calls, so the reverb tail of the previous level does not arrive in
    ///     the next one.
    /// </remarks>
    void Reset();

    /// <summary>Sets one of the effect's knobs by name, for automation.</summary>
    /// <param name="name">The property's own name, matched exactly — <c>Wet</c>, <c>Frequency</c>.</param>
    /// <param name="value">What to set it to.</param>
    /// <returns>Whether the effect has such a property.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A switch each effect writes, and not reflection.</b> Looking a property up by name
    ///         at run time is what <c>ADR-002</c> forbids and what does not survive trimming, so every
    ///         effect that wants to be automatable declares which of its knobs are. The default is
    ///         "none", so an effect that says nothing is simply not automatable rather than broken.
    ///     </para>
    ///     <para>
    ///         <b>Case-sensitive, deliberately.</b> Matching loosely would mean lowering the name on
    ///         every call — a string allocation per driven property per frame, in the frame loop. The
    ///         cost of exactness is a typo, and a typo is caught where the automation is resolved
    ///         rather than being silently ignored for the life of the project.
    ///     </para>
    ///     <para>
    ///         Called from the game thread while <see cref="Process" /> may be running, which is the
    ///         same documented race as writing the property directly.
    ///     </para>
    /// </remarks>
    bool TrySetProperty(string name, float value) => false;

    /// <summary>Reads one of the effect's knobs by name.</summary>
    /// <param name="name">The property's own name, matched exactly.</param>
    /// <param name="value">What it is worth.</param>
    /// <returns>Whether the effect has such a property.</returns>
    /// <remarks>
    ///     The pair of <see cref="TrySetProperty" />, and needed for the same reason a fader can be
    ///     read as well as moved: anything showing a mix — a live-update session, an overlay — has to
    ///     start from what the values already are rather than from zero.
    /// </remarks>
    bool TryGetProperty(string name, out float value) {
        value = 0f;
        return false;
    }

    /// <summary>The knobs this effect will answer to, by name.</summary>
    /// <remarks>
    ///     <b>Declared beside the two accessors, deliberately.</b> The three have to agree, and the
    ///     only thing that keeps hand-written switches in step is that they sit together where a
    ///     change to one makes the others obviously wrong. A test walks this list through both
    ///     accessors, so drift is a failure rather than a surprise.
    /// </remarks>
    IReadOnlyList<string> Properties => [];
}

/// <summary>An effect that listens to one signal while processing another.</summary>
/// <remarks>
///     <para>
///         Ducking, and everything shaped like it. A compressor on the music bus keyed by the
///         dialogue bus turns the music down whenever anybody speaks — which is the single most
///         asked-for behaviour in game audio and cannot be expressed by an effect that can only see
///         what it is processing.
///     </para>
///     <para>
///         The key arrives as a span the bus owns, valid only for the call. An effect that wants
///         history keeps an envelope, not the samples.
///     </para>
///     <para>
///         <see cref="IAudioEffect.Process" /> is still implemented and is what runs when the bus
///         has no <see cref="Vixen.Audio.Mixing.AudioBus.SidechainSource" />, so a keyed effect on
///         an unkeyed bus behaves as an ordinary one rather than failing.
///     </para>
/// </remarks>
public interface ISidechainEffect : IAudioEffect {
    /// <summary>Processes a block against a key signal.</summary>
    /// <param name="buffer">Interleaved, <c>frameCount × channels</c> floats. Processed in place.</param>
    /// <param name="key">The signal to listen to. The same length and layout as the buffer.</param>
    /// <param name="frameCount">How many frames.</param>
    /// <param name="channels">How many channels are interleaved.</param>
    void Process(Span<float> buffer, ReadOnlySpan<float> key, int frameCount, int channels);
}
