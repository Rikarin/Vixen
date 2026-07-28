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
    /// <summary>A zone and where it actually is, which is not always where it says it is.</summary>
    readonly record struct Placed(AudioReverbZone Zone, Vector3 Position);

    // Two lists, because they are owned differently. `added` is a game's own, and lives until it says
    // otherwise. `synced` is rebuilt from the world every frame, because an entity that was destroyed
    // has to stop being a zone without anybody having to remember to say so.
    readonly List<Placed> added = [];
    readonly List<Placed> synced = [];

    // The parameters any zone has ever mentioned, registered when the zone is first seen rather than
    // discovered every frame — so the per-frame pass is walks over parallel lists and allocates
    // nothing, which matters because it runs inside the frame loop.
    readonly Dictionary<string, int> slots = [];
    readonly List<string> names = [];
    readonly List<float> values = [];
    readonly List<int> winners = [];

    /// <summary>How many zones there are, from both sources.</summary>
    public int Count => added.Count + synced.Count;

    /// <summary>Adds a zone that stays until it is removed.</summary>
    /// <param name="zone">It. Its own <see cref="AudioReverbZone.Position" /> is where it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="zone" /> is null.</exception>
    public void Add(AudioReverbZone zone) {
        ArgumentNullException.ThrowIfNull(zone);
        Register(zone);
        added.Add(new(zone, zone.Position));
    }

    /// <summary>Removes one.</summary>
    /// <param name="zone">It.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     Its parameter stays registered and is driven to zero from the next
    ///     <see cref="Apply" /> — which is the point. Forgetting the name instead would leave the
    ///     parameter wherever the removed zone had last pushed it.
    /// </remarks>
    public bool Remove(AudioReverbZone zone) {
        var index = added.FindIndex(placed => placed.Zone == zone);

        if (index < 0) {
            return false;
        }

        added.RemoveAt(index);
        return true;
    }

    /// <summary>Starts rebuilding the set that comes from the world.</summary>
    /// <remarks>
    ///     Clears only what was synced last frame, and none of the registered parameters: a zone
    ///     entity that has been destroyed still has to release whatever it was driving, and a
    ///     forgotten name cannot.
    /// </remarks>
    public void BeginSync() => synced.Clear();

    /// <summary>Adds a zone for this frame, at a position its own does not decide.</summary>
    /// <param name="zone">The shared description.</param>
    /// <param name="position">Where the entity carrying it is.</param>
    public void Sync(AudioReverbZone zone, in Vector3 position) {
        ArgumentNullException.ThrowIfNull(zone);
        Register(zone);
        synced.Add(new(zone, position));
    }

    /// <summary>Drops all of them, for a scene change.</summary>
    public void Clear() {
        added.Clear();
        synced.Clear();
        slots.Clear();
        names.Clear();
        values.Clear();
        winners.Clear();
    }

    void Register(AudioReverbZone zone) {
        if (!string.IsNullOrEmpty(zone.Parameter) && slots.TryAdd(zone.Parameter, names.Count)) {
            names.Add(zone.Parameter);
            values.Add(0f);
            winners.Add(int.MinValue);
        }
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

        Consider(added, listener);
        Consider(synced, listener);

        if (parameters is null) {
            return;
        }

        for (var i = 0; i < names.Count; i++) {
            parameters.Set(names[i], values[i]);
        }
    }

    /// <summary>Lets every zone in a list bid for its parameter, keeping the winner.</summary>
    void Consider(List<Placed> zones, in Vector3 listener) {
        foreach (var (zone, position) in zones) {
            if (string.IsNullOrEmpty(zone.Parameter) || !slots.TryGetValue(zone.Parameter, out var slot)) {
                continue;
            }

            var value = zone.Evaluate(listener, position);

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
    }

    /// <summary>What a parameter was last driven to, for a test or an overlay to read.</summary>
    /// <param name="parameter">Its name.</param>
    public float StrengthOf(string parameter) =>
        slots.TryGetValue(parameter, out var slot) ? values[slot] : 0f;
}
