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
}
