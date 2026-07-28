// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Mixing;

/// <summary>A copy of one bus's signal, added into another at some level.</summary>
/// <remarks>
///     <para>
///         The second of the two ways a bus can reach another one, and the one that makes a mixer a
///         graph rather than a tree. A bus's <em>parent</em> is where its signal goes; a
///         <em>send</em> is where a copy of it also goes, and the copy does not stop it reaching the
///         parent as well.
///     </para>
///     <para>
///         <see cref="Level" /> is an ordinary <see cref="float" /> written from the game thread
///         while the audio thread reads it, on the same terms as <see cref="AudioBus.Gain" />: the
///         CLR writes one atomically, so the worst outcome is a change landing one block late. That
///         is what lets a send level be driven per frame — an emitter's reverb amount tracking how
///         far into a room it is, say — without a queue.
///     </para>
/// </remarks>
public sealed class AudioSend {
    internal AudioSend(AudioBus target, bool preFader) {
        Target = target;
        PreFader = preFader;
    }

    /// <summary>Where the copy goes.</summary>
    public AudioBus Target { get; }

    /// <summary>Whether the copy is taken before the source bus's own gain and mute.</summary>
    /// <remarks>
    ///     Fixed when the send is made. A send that could change sides at run time would step its
    ///     own level by the fader's value the moment it did.
    /// </remarks>
    public bool PreFader { get; }

    /// <summary>How much of the signal to send, as a linear gain. Zero costs one branch a block.</summary>
    public float Level { get; set; } = 1f;

    /// <summary>What parameter automation last worked out for this send, as a linear multiplier.</summary>
    /// <remarks>Kept apart from <see cref="Level" /> for the reason <c>AudioBus.ParameterGain</c> gives.</remarks>
    public float ParameterLevel { get; set; } = 1f;
}
