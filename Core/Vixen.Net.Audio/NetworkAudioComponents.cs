// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;

namespace Vixen.Net.Audio;

/// <summary>Whether a sound is playing, and how — not the sound.</summary>
/// <remarks>
///     <para>
///         <b>No clip on the wire.</b> Not a simplification: the entity carrying this was spawned
///         from a prefab, and the prefab carries its <c>AudioClipRef</c>, so both peers already agree
///         about which sound this is by the same mechanism they agree about which mesh it has.
///         Sending a clip id would be re-stating a fact the spawn already established, and it would
///         mean a second asset registry to keep in step with the first.
///     </para>
///     <para>
///         <b>What this is <i>not</i> for is a one-shot at a world position</b> — an explosion, a
///         footstep, a UI click. Those are events, and modelling an event as replicated state means a
///         player who joins five minutes later hears the explosion. They belong on a broadcast or an
///         RPC, both of which happen once and reach who was there. This is for sounds with a state
///         worth agreeing on: an engine that is running, an alarm that is sounding, a machine that is
///         humming until somebody switches it off.
///     </para>
///     <para>
///         <b><see cref="Trigger" /> is what makes "again" visible</b>, and it is the same trick
///         <c>NetworkTransform.TeleportCount</c> uses for the same reason. Playing a one-shot twice
///         sets <c>Playback</c> to the value it already had, so a receiver comparing states sees
///         nothing and the second shot is silent. A counter that moves is a change even when the state
///         it accompanies has not.
///     </para>
/// </remarks>
[DataContract]
public struct NetworkAudioSource {
    /// <summary>What it should be doing, as <c>AudioPlayback</c>.</summary>
    public byte Playback;

    /// <summary>Bumped to say "start it again". Wraps, and only inequality is ever asked of it.</summary>
    public byte Trigger;

    /// <summary>Its linear gain.</summary>
    public float Gain;

    /// <summary>Its playback rate multiplier.</summary>
    public float Pitch;
}

/// <summary>Say "play it again" by adding one of these.</summary>
/// <remarks>
///     A tag the capture system turns into a bump of <see cref="NetworkAudioSource.Trigger" /> and
///     takes off again, so nothing has to remember to clear it — exactly as <c>NetworkTeleport</c>
///     works for the transform bridge. Adding it on a peer that is not the authority does nothing,
///     which is the right answer rather than a silent half-effect: the sound is the authority's to
///     start.
/// </remarks>
public struct NetworkAudioTrigger : ITagComponent;

/// <summary>Puts a sound's state on the wire.</summary>
public sealed class NetworkAudioSourceReplicator : IComponentReplicator {
    /// <summary>The range a replicated gain lives in.</summary>
    /// <remarks>
    ///     Zero to four rather than zero to one: a gain above unity is an ordinary thing for a mixer
    ///     to be asked for, and clamping it on the wire would make a loud sound quietly wrong. Eight
    ///     bits is about one and a half percent, which is far below the smallest change in loudness
    ///     anybody can hear.
    /// </remarks>
    public static QuantizeRange GainRange { get; } = new(0f, 4f, 8);

    /// <summary>The range a replicated pitch lives in.</summary>
    /// <remarks>
    ///     Negative for a sound played backwards, and capped at four because past two octaves up
    ///     nothing is recognisable anyway.
    /// </remarks>
    public static QuantizeRange PitchRange { get; } = new(-4f, 4f, 10);

    static readonly WireLane[] Layout = [
        new("Playback", 2, false),
        new("Trigger", 8, false),
        new("Gain", 8, true),
        new("Pitch", 10, true)
    ];

    /// <inheritdoc />
    public ComponentTypeId ComponentType => ComponentType<NetworkAudioSource>.Id;

    /// <inheritdoc />
    public uint TypeId { get; } = ReplicationRegistry.HashTypeName("Vixen.Net.Audio.NetworkAudioSource");

    /// <inheritdoc />
    public string TypeName => "Vixen.Net.Audio.NetworkAudioSource";

    /// <summary>Reliable, because starting and stopping do not repeat.</summary>
    /// <remarks>
    ///     The same argument the animator's parameters make. A lost position is superseded a
    ///     thirtieth of a second later; a lost "the alarm stopped" is an alarm that sounds for ever on
    ///     one client. The state is re-sent until acknowledged either way — the channel is what makes
    ///     it arrive the first time.
    /// </remarks>
    public Channel Channel => Channel.ReliableUnordered;

    /// <summary>Below motion. A sound that is a tick late is a sound nobody notices is late.</summary>
    public int Priority => 5;

    /// <inheritdoc />
    public QueryDescription ChangedQuery { get; } =
        new QueryDescription().RequireChanged([ComponentType<NetworkAudioSource>.Id]);

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => Layout;

    /// <inheritdoc />
    public bool Has(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.Has<NetworkAudioSource>(entity);
    }

    /// <inheritdoc />
    public void Write(World world, Entity entity, ref BitWriter writer) {
        ArgumentNullException.ThrowIfNull(world);

        ref readonly var value = ref world.Read<NetworkAudioSource>(entity);

        writer.Write(Math.Min(value.Playback, (byte)2), 2);
        writer.Write(value.Trigger, 8);
        writer.WriteQuantized(value.Gain, GainRange);
        writer.WriteQuantized(value.Pitch, PitchRange);
    }

    /// <inheritdoc />
    public bool Apply(World world, Entity entity, ref BitReader reader) {
        ArgumentNullException.ThrowIfNull(world);

        if (!reader.TryRead(2, out var playback)
            || !reader.TryRead(8, out var trigger)
            || !reader.TryReadQuantized(GainRange, out var gain)
            || !reader.TryReadQuantized(PitchRange, out var pitch)) {
            return false;
        }

        var value = new NetworkAudioSource {
            Playback = (byte)playback, Trigger = (byte)trigger, Gain = gain, Pitch = pitch
        };

        if (world.Has<NetworkAudioSource>(entity)) {
            world.Set(entity, value);
        } else {
            world.Add(entity, value);
        }

        return true;
    }
}
