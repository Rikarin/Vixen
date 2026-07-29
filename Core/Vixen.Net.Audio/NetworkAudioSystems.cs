// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Net.Replication;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;

namespace Vixen.Net.Audio;

/// <summary>Reads what a sound is doing into the component the wire carries.</summary>
/// <remarks>
///     Before the audio system, which runs in <see cref="SystemPhase.PostRender" /> and is what
///     writes <c>Playback</c> back to <c>Stopped</c> when a sound runs out on its own. Reading in
///     <see cref="SystemPhase.PreRender" /> therefore publishes the state the game asked for in this
///     frame rather than last frame's, and the natural end of a sound reaches the wire on the tick
///     after it happens — which is a frame, and is the right side of the trade against publishing a
///     start the game has not made yet.
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class NetworkAudioCaptureSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription sounding = new QueryDescription().WithAll<AudioSource, NetworkAudioSource, NetworkId>();
    readonly List<Entity> triggered = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AudioSource>()
        .Read<NetworkId>()
        .Write<NetworkAudioSource>()
        .Build();

    /// <summary>Who decides what, and who this peer is. Null is server-authoritative.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is, or <see cref="PlayerId.None" /> for a server.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many sounds have been published.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>How many re-triggers have gone out.</summary>
    public long TriggeredCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Publish(context.World);

        return dependency;
    }

    /// <summary>Reads every networked sound this peer decides.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Publish(World world) {
        ArgumentNullException.ThrowIfNull(world);

        triggered.Clear();

        foreach (var chunk in world.Chunks(sounding)) {
            var sources = chunk.ReadValues<AudioSource>();
            var networked = chunk.Values<NetworkAudioSource>();
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!IsAuthority(ids[index])) {
                    continue;
                }

                ref var value = ref networked[index];
                value.Playback = (byte)sources[index].Playback;
                value.Gain = sources[index].Gain;
                value.Pitch = sources[index].Pitch;

                if (world.Has<NetworkAudioTrigger>(entities[index])) {
                    // Wraps, deliberately. Only inequality is ever asked of it, so a byte that comes
                    // round after two hundred and fifty-six plays is a byte that has done its job.
                    value.Trigger++;
                    triggered.Add(entities[index]);
                }

                PublishedCount++;
            }
        }

        // Outside the sweep, because taking a tag off is a structural change and the chunks are being
        // walked. The tag is consumed rather than left, so nothing has to remember to clear it.
        foreach (var entity in triggered) {
            world.Remove<NetworkAudioTrigger>(entity);
            TriggeredCount++;
        }
    }

    /// <summary>Whether this peer decides what a sound is doing.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it does.</returns>
    public bool IsAuthority(NetworkId id) =>
        Rules is { } rules
            ? rules.MayWrite(id, Local)
            : NetworkRules.Allows(RuleAudience.ServerOnly, Local, isOwner: false);
}

/// <summary>Makes a local sound do what the wire says.</summary>
/// <remarks>
///     <b>A re-trigger restarts the sound even when the state did not change.</b> That is the whole
///     reason the counter exists: a one-shot played twice is <c>Playing</c> both times, so a receiver
///     comparing states would see nothing and the second shot would be silent. The receiver keeps the
///     last trigger it acted on rather than reading it back off the component, because the component
///     is what arrived and not what was done with it.
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class NetworkAudioApplySystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription sounding = new QueryDescription().WithAll<AudioSource, NetworkAudioSource, NetworkId>();
    readonly Dictionary<uint, byte> acted = [];
    readonly HashSet<uint> restarting = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkAudioSource>()
        .Read<NetworkId>()
        .Write<AudioSource>()
        .Build();

    /// <summary>Who decides what, and who this peer is. Null is server-authoritative.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is, or <see cref="PlayerId.None" /> for a server.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many sounds have been driven from the wire.</summary>
    public long AppliedCount { get; private set; }

    /// <summary>How many were restarted because the trigger moved.</summary>
    public long RestartedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Apply(context.World);

        return dependency;
    }

    /// <summary>Drives every networked sound this peer does not decide.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Apply(World world) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(sounding)) {
            var sources = chunk.Values<AudioSource>();
            var networked = chunk.ReadValues<NetworkAudioSource>();
            var ids = chunk.ReadValues<NetworkId>();

            for (var index = 0; index < chunk.Count; index++) {
                if (IsAuthority(ids[index])) {
                    continue;
                }

                ref readonly var value = ref networked[index];
                ref var source = ref sources[index];

                source.Gain = value.Gain;
                source.Pitch = value.Pitch;

                var id = ids[index].Value;
                var moved = acted.TryGetValue(id, out var last) && last != value.Trigger;
                acted[id] = value.Trigger;

                if (moved && (AudioPlayback)value.Playback is AudioPlayback.Playing) {
                    // Stopped now, started on the next pass — because the audio system starts a voice
                    // only when one is not already alive, so telling a playing sound to play is a
                    // no-op and the second shot would be silent. A frame at sixty is sixteen
                    // milliseconds and inaudible; reaching past the component into the mixer to do it
                    // in one would mean this system needing an AudioEngine, which is the dependency
                    // the whole declarative design exists to avoid.
                    source.Playback = AudioPlayback.Stopped;
                    restarting.Add(id);
                    RestartedCount++;
                } else if (restarting.Remove(id)) {
                    source.Playback = AudioPlayback.Playing;
                } else {
                    source.Playback = (AudioPlayback)value.Playback;
                }

                AppliedCount++;
            }
        }
    }

    /// <summary>Forgets what it knew about an object, because it is gone.</summary>
    /// <param name="id">The object.</param>
    /// <remarks>
    ///     The trigger a receiver last acted on is the one piece of state this keeps per object, so it
    ///     is the one piece that leaks if a despawn is never mentioned. Reusing an id would then make
    ///     a new object inherit an old one's trigger and either restart once for nothing or miss its
    ///     first play — and ids are not reused within a session precisely so that mistakes like this
    ///     one are not silent, but a long-running server would still grow this table for ever.
    /// </remarks>
    public void Forget(NetworkId id) {
        acted.Remove(id.Value);
        restarting.Remove(id.Value);
    }

    /// <summary>Whether this peer decides what a sound is doing.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it does.</returns>
    public bool IsAuthority(NetworkId id) =>
        Rules is { } rules
            ? rules.MayWrite(id, Local)
            : NetworkRules.Allows(RuleAudience.ServerOnly, Local, isOwner: false);
}
