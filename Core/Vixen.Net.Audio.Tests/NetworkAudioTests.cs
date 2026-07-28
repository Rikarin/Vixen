// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Audio.Tests;

/// <summary>Networked audio: whether a sound is playing, not the sound.</summary>
public sealed class NetworkAudioTests {
    static readonly PlayerId Receiving = new(4);

    /// <summary>The authority publishes what its sound is doing.</summary>
    [Fact]
    public void TheAuthorityPublishesWhatItsSoundIsDoing() {
        using var world = new World("audio-capture");
        var capture = new NetworkAudioCaptureSystem();

        var entity = world.Create(
            new NetworkId(1),
            AudioSource.Default with { Playback = AudioPlayback.Playing, Gain = 0.75f, Pitch = 1.5f },
            default(NetworkAudioSource)
        );

        capture.Publish(world);

        var value = world.Read<NetworkAudioSource>(entity);

        Assert.Equal((byte)AudioPlayback.Playing, value.Playback);
        Assert.Equal(0.75f, value.Gain, 3);
        Assert.Equal(1.5f, value.Pitch, 3);
        Assert.Equal(0, value.Trigger);
        Assert.Equal(1, capture.PublishedCount);
    }

    /// <summary>A trigger tag becomes a counter and is taken off again.</summary>
    /// <remarks>
    ///     The same shape as <c>NetworkTeleport</c>, and for the same reason: a game says "again" by
    ///     adding a tag, and nothing has to remember to clear it.
    /// </remarks>
    [Fact]
    public void ATriggerTagBecomesACounterAndIsConsumed() {
        using var world = new World("audio-trigger");
        var capture = new NetworkAudioCaptureSystem();

        var entity = world.Create(
            new NetworkId(1),
            AudioSource.Playing,
            default(NetworkAudioSource),
            default(NetworkAudioTrigger)
        );

        capture.Publish(world);

        Assert.Equal(1, world.Read<NetworkAudioSource>(entity).Trigger);
        Assert.False(world.Has<NetworkAudioTrigger>(entity));
        Assert.Equal(1, capture.TriggeredCount);

        // And a second pass without the tag leaves the counter alone, which is what makes it mean
        // "somebody asked again" rather than "time passed".
        capture.Publish(world);
        Assert.Equal(1, world.Read<NetworkAudioSource>(entity).Trigger);
    }

    /// <summary>A receiver's sound does what the wire says.</summary>
    [Fact]
    public void AReceiverDoesWhatTheWireSays() {
        using var world = new World("audio-apply");
        var apply = new NetworkAudioApplySystem { Local = Receiving };

        var entity = world.Create(
            new NetworkId(1),
            AudioSource.Default,
            new NetworkAudioSource { Playback = (byte)AudioPlayback.Playing, Gain = 0.5f, Pitch = 2f }
        );

        apply.Apply(world);

        var source = world.Read<AudioSource>(entity);

        Assert.Equal(AudioPlayback.Playing, source.Playback);
        Assert.Equal(0.5f, source.Gain, 3);
        Assert.Equal(2f, source.Pitch, 3);
        Assert.Equal(1, apply.AppliedCount);
        Assert.Equal(0, apply.RestartedCount);
    }

    /// <summary>A one-shot played twice is heard twice.</summary>
    /// <remarks>
    ///     <para>
    ///         The whole reason the counter exists. Playing a one-shot again sets <c>Playback</c> to
    ///         the value it already had, so a receiver comparing states sees nothing and the second
    ///         shot is silent — a bug that only shows up with two players in the room.
    ///     </para>
    ///     <para>
    ///         The restart is stop-now, start-next-pass, because the audio system starts a voice only
    ///         when one is not already alive. That is a frame, and this asserts both halves of it
    ///         rather than only the end state, because the intermediate <c>Stopped</c> is what makes it
    ///         work and would be easy to optimise away.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AOneShotPlayedTwiceIsHeardTwice() {
        using var world = new World("audio-restart");
        var apply = new NetworkAudioApplySystem { Local = Receiving };

        var entity = world.Create(
            new NetworkId(1),
            AudioSource.Default,
            new NetworkAudioSource { Playback = (byte)AudioPlayback.Playing, Trigger = 1 }
        );

        apply.Apply(world);
        Assert.Equal(AudioPlayback.Playing, world.Read<AudioSource>(entity).Playback);

        // Played again: same state, different trigger.
        world.Get<NetworkAudioSource>(entity).Trigger = 2;

        apply.Apply(world);
        Assert.Equal(AudioPlayback.Stopped, world.Read<AudioSource>(entity).Playback);
        Assert.Equal(1, apply.RestartedCount);

        apply.Apply(world);
        Assert.Equal(AudioPlayback.Playing, world.Read<AudioSource>(entity).Playback);

        // And it settles: no further restarts from a trigger that has stopped moving.
        apply.Apply(world);
        Assert.Equal(AudioPlayback.Playing, world.Read<AudioSource>(entity).Playback);
        Assert.Equal(1, apply.RestartedCount);
    }

    /// <summary>Authority comes from the rules, the same question everything else asks.</summary>
    [Fact]
    public void AuthorityComesFromTheRules() {
        var ownership = new NetworkOwnership();
        var rules = new NetworkRulesRegistry(ownership);
        var mine = new PlayerId(4);

        ownership.SetOwner(new(1), mine);
        rules.Set(new(1), NetworkRules.OwnerAuthoritative);

        Assert.True(new NetworkAudioCaptureSystem { Rules = rules, Local = mine }.IsAuthority(new(1)));
        Assert.False(new NetworkAudioCaptureSystem { Rules = rules, Local = new(5) }.IsAuthority(new(1)));
    }

    /// <summary>The state survives the wire.</summary>
    [Fact]
    public void TheStateRoundTrips() {
        using var world = new World("audio-wire");
        var replicator = new NetworkAudioSourceReplicator();
        var buffer = new byte[64];

        var entity = world.Create(
            new NetworkId(1),
            new NetworkAudioSource {
                Playback = (byte)AudioPlayback.Paused, Trigger = 200, Gain = 3.25f, Pitch = -1f
            }
        );

        var writer = new Messaging.BitWriter(buffer);
        replicator.Write(world, entity, ref writer);
        Assert.True(writer.TryFinish(out var bits));
        Assert.Equal(Messaging.DeltaCodec.TotalBits(replicator.Lanes), writer.BitsWritten);

        using var receiving = new World("audio-wire-client");
        var arrived = receiving.Create(new NetworkId(1));
        var reader = new Messaging.BitReader(bits);

        Assert.True(replicator.Apply(receiving, arrived, ref reader));

        var got = receiving.Read<NetworkAudioSource>(arrived);

        Assert.Equal((byte)AudioPlayback.Paused, got.Playback);
        Assert.Equal(200, got.Trigger);
        Assert.Equal(3.25f, got.Gain, 1);
        Assert.Equal(-1f, got.Pitch, 1);
    }
}
