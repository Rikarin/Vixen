// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Parameters;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Spatial;

/// <summary>The zones in a level, and which of them the listener is in.</summary>
/// <remarks>
///     <para>
///         <b>One winner per parameter, not a sum.</b> A cupboard inside a cathedral is inside both;
///         adding them gives a room that is neither, and averaging them gives a room that exists
///         nowhere. So for each parameter the highest-priority zone containing the listener takes it
///         outright, still faded across its own edge.
///     </para>
///     <para>
///         <b>Every parameter a zone mentions is written every frame, including to zero.</b> A zone
///         the listener has left has to actively push its parameter back down; leaving it alone would
///         mean the cathedral's reverb following the player out into the field, which is the bug this
///         shape exists to make impossible.
///     </para>
///     <para>
///         <b>The fade is the parameter's, not the zone's.</b> <see cref="AudioReverbZone.Blend" />
///         shapes the value across the boundary in space; a parameter's own <c>SeekSeconds</c> shapes
///         it across time. Both are wanted, and they are different tools — the first stops a doorway
///         being a step function, the second stops a teleport being a click.
///     </para>
/// </remarks>
public sealed class AudioReverbZones {
    readonly List<AudioReverbZone> zones = [];

    // The parameters any zone mentions, registered once when the zone is added rather than
    // discovered every frame — so the per-frame pass is two walks over parallel lists and allocates
    // nothing, which matters because it runs inside the frame loop.
    readonly Dictionary<string, int> slots = [];
    readonly List<string> names = [];
    readonly List<float> values = [];
    readonly List<int> winners = [];

    /// <summary>How many zones there are.</summary>
    public int Count => zones.Count;

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    public AudioReverbZone this[int index] => zones[index];

    /// <summary>Adds a zone.</summary>
    /// <param name="zone">It.</param>
    /// <exception cref="ArgumentNullException"><paramref name="zone" /> is null.</exception>
    public void Add(AudioReverbZone zone) {
        ArgumentNullException.ThrowIfNull(zone);
        zones.Add(zone);

        if (!string.IsNullOrEmpty(zone.Parameter) && slots.TryAdd(zone.Parameter, names.Count)) {
            names.Add(zone.Parameter);
            values.Add(0f);
            winners.Add(int.MinValue);
        }
    }

    /// <summary>Removes one.</summary>
    /// <param name="zone">It.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     Its parameter stays registered and is driven to zero from the next
    ///     <see cref="Apply" /> — which is the point. Forgetting the name instead would leave the
    ///     parameter wherever the removed zone had last pushed it.
    /// </remarks>
    public bool Remove(AudioReverbZone zone) => zones.Remove(zone);

    /// <summary>Drops all of them, for a scene change.</summary>
    public void Clear() {
        zones.Clear();
        slots.Clear();
        names.Clear();
        values.Clear();
        winners.Clear();
    }

    /// <summary>Works out what the listener is standing in, and writes it to the parameters.</summary>
    /// <param name="listener">Where the ear is.</param>
    /// <param name="parameters">What to write to. Null does the arithmetic and nothing else.</param>
    public void Apply(in Vector3 listener, MixerParameters? parameters) {
        // Not `zones.Count == 0`. Removing the last zone still leaves its parameter registered and
        // still driving whatever it was — so there has to be one more pass to push it back to zero,
        // or the room the player has left follows them out of it forever.
        if (names.Count == 0) {
            return;
        }

        // Reset rather than skip: a parameter that was being driven and no longer is must be actively
        // pushed back to zero, or the room the player has left follows them out of it.
        for (var i = 0; i < names.Count; i++) {
            values[i] = 0f;
            winners[i] = int.MinValue;
        }

        foreach (var zone in zones) {
            if (string.IsNullOrEmpty(zone.Parameter) || !slots.TryGetValue(zone.Parameter, out var slot)) {
                continue;
            }

            var value = zone.Evaluate(listener);

            if (value <= 0f) {
                continue;
            }

            // Priority first, and strength only to break a tie between equals — otherwise a large
            // weak zone would beat the small strong one deliberately placed inside it.
            if (zone.Priority > winners[slot] || (zone.Priority == winners[slot] && value > values[slot])) {
                winners[slot] = zone.Priority;
                values[slot] = value;
            }
        }

        if (parameters is null) {
            return;
        }

        for (var i = 0; i < names.Count; i++) {
            parameters.Set(names[i], values[i]);
        }
    }

    /// <summary>What a parameter was last driven to, for a test or an overlay to read.</summary>
    /// <param name="parameter">Its name.</param>
    public float StrengthOf(string parameter) =>
        slots.TryGetValue(parameter, out var slot) ? values[slot] : 0f;
}
