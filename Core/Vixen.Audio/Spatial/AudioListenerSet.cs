// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Audio.Spatial;

[InlineArray(AudioListenerSet.MaxListeners)]
struct ListenerBuffer {
    AudioListener element;
}

[InlineArray(AudioListenerSet.MaxListeners)]
struct WeightBuffer {
    float element;
}

/// <summary>Everywhere the game is listening from at once.</summary>
/// <remarks>
///     <para>
///         <b>One set of speakers, several pairs of ears.</b> Split-screen is the case: four players
///         at four places in the world, and one stereo output that has to represent all of them. There
///         is no correct answer to that — the room only has two speakers — so what matters is picking
///         a wrong answer that nobody notices.
///     </para>
///     <para>
///         <b>Four, because the fifth player does not exist.</b> Local multiplayer stops at four on
///         every console that supports it, and a fixed cap is what lets the whole set live in a struct
///         and go through the same sequence lock a single listener did — no allocation, no list, and
///         one copy per block rather than one per voice.
///     </para>
///     <para>
///         A set with one listener in it behaves exactly as the single listener did, and that is the
///         path almost every game takes.
///     </para>
/// </remarks>
public struct AudioListenerSet {
    /// <summary>How many pairs of ears there can be.</summary>
    public const int MaxListeners = 4;

    ListenerBuffer listeners;
    WeightBuffer weights;

    /// <summary>How many there are.</summary>
    public int Count { get; private set; }

    /// <summary>A set with one listener in it, at full weight.</summary>
    /// <param name="listener">The listener.</param>
    /// <returns>The set.</returns>
    public static AudioListenerSet Single(in AudioListener listener) {
        var set = default(AudioListenerSet);
        set.TryAdd(listener);
        return set;
    }

    /// <summary>The default listener, alone.</summary>
    public static AudioListenerSet Default => Single(AudioListener.Default);

    /// <summary>Adds a listener.</summary>
    /// <param name="listener">Where those ears are.</param>
    /// <param name="weight">
    ///     How much of the mix is theirs. Equal weights are the split-screen case; an unequal one is
    ///     for a spectator or a security camera that should be present but not dominant.
    /// </param>
    /// <returns><see langword="false" /> if the set was already full.</returns>
    public bool TryAdd(in AudioListener listener, float weight = 1f) {
        if (Count >= MaxListeners) {
            return false;
        }

        listeners[Count] = listener;
        weights[Count] = MathF.Max(weight, 0f);
        Count++;
        return true;
    }

    /// <summary>Empties it.</summary>
    public void Clear() => Count = 0;

    /// <summary>One of the listeners.</summary>
    /// <param name="index">Which.</param>
    /// <returns>It.</returns>
    public readonly AudioListener Get(int index) => listeners[index];

    /// <summary>How much of the mix one listener gets.</summary>
    /// <param name="index">Which.</param>
    /// <returns>Its weight.</returns>
    public readonly float WeightOf(int index) => weights[index];
}
