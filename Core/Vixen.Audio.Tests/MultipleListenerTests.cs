// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Ecs;
using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Several pairs of ears and one set of speakers, which is split-screen.</summary>
public sealed class MultipleListenerTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public MultipleListenerTests() => (engine, device) = AudioTestData.Engine(channels: 2, voices: 8);

    public void Dispose() => engine.Dispose();

    static SpatialSettings At(float x, float z) => new() {
        Position = new Vector3(x, 0f, z),
        MinDistance = 1f,
        MaxDistance = 1_000f,
        DopplerFactor = 0f
    };

    static AudioListener Ears(float x, float z) => AudioListener.Default with {
        Position = new Vector3(x, 0f, z)
    };

    static float[] Gains(in AudioListenerSet listeners, in SpatialSettings source) {
        var gains = new float[2];
        Spatializer.Evaluate(listeners, source, 2, gains, new float[2]);
        return gains;
    }

    [Fact]
    public void ASetOfOneIsExactlyWhatASingleListenerAlwaysWas() {
        var one = Gains(AudioListenerSet.Single(Ears(0f, 0f)), At(3f, 4f));

        var direct = new float[2];
        Spatializer.Evaluate(Ears(0f, 0f), At(3f, 4f), 2, direct);

        Assert.Equal(direct, one);
    }

    [Fact]
    public void TheSetHoldsFourAndRefusesTheFifth() {
        var set = default(AudioListenerSet);

        for (var i = 0; i < AudioListenerSet.MaxListeners; i++) {
            Assert.True(set.TryAdd(Ears(i, 0f)));
        }

        Assert.False(set.TryAdd(Ears(99f, 0f)));
        Assert.Equal(AudioListenerSet.MaxListeners, set.Count);
        Assert.Equal(new Vector3(2f, 0f, 0f), set.Get(2).Position);
    }

    /// <summary>
    ///     The rule that makes summing wrong. Two players standing together beside a generator must not
    ///     hear it twice as loud as one player standing there — otherwise every sound in the level
    ///     gets louder as the party gathers.
    /// </summary>
    [Fact]
    public void TwoListenersInTheSamePlaceHearItAtTheSameLevelAsOne() {
        var alone = Gains(AudioListenerSet.Single(Ears(0f, 0f)), At(0f, 10f));

        var together = default(AudioListenerSet);
        together.TryAdd(Ears(0f, 0f));
        together.TryAdd(Ears(0f, 0f));

        var pair = Gains(together, At(0f, 10f));

        Assert.Equal(alone[0], pair[0], 1e-4f);
        Assert.Equal(alone[1], pair[1], 1e-4f);
    }

    /// <summary>The level belongs to whoever hears it best, so a distant second player changes nothing much.</summary>
    [Fact]
    public void TheLevelComesFromTheListenerWhoHearsItBest() {
        var near = AudioListenerSet.Single(Ears(0f, 0f));

        var both = default(AudioListenerSet);
        both.TryAdd(Ears(0f, 0f));
        both.TryAdd(Ears(0f, 500f));

        var source = At(0f, 5f);
        var alone = Gains(near, source);
        var pair = Gains(both, source);

        var aloneLevel = MathF.Sqrt((alone[0] * alone[0]) + (alone[1] * alone[1]));
        var pairLevel = MathF.Sqrt((pair[0] * pair[0]) + (pair[1] * pair[1]));

        Assert.Equal(aloneLevel, pairLevel, aloneLevel * 0.05f);
    }

    /// <summary>
    ///     Why nearest-wins was rejected. With it the pan flips the instant the sound crosses the
    ///     midpoint between two players — from hard one way to hard the other in one frame — because
    ///     the two listeners face the sound from opposite sides. Blending is continuous across that
    ///     line, which is the property worth having.
    /// </summary>
    [Fact]
    public void TheDirectionIsContinuousAcrossTheMidpointBetweenTwoListeners() {
        var set = default(AudioListenerSet);
        set.TryAdd(Ears(-10f, 0f));
        set.TryAdd(Ears(10f, 0f));

        static float Balance(float[] gains) => gains[1] - gains[0];

        var left = Balance(Gains(set, At(-0.5f, 10f)));
        var middle = Balance(Gains(set, At(0f, 10f)));
        var right = Balance(Gains(set, At(0.5f, 10f)));

        // What nearest-wins would have produced at the same two points: whoever is closer hears the
        // sound off to one side, and which of them that is changes at the midpoint.
        var nearest = new float[2];
        Spatializer.Evaluate(Ears(-10f, 0f), At(-0.5f, 10f), 2, nearest);
        var jump = MathF.Abs(Balance(nearest)) * 2f;

        Assert.Equal(0f, middle, 1e-3f);
        Assert.True(MathF.Abs(right - left) < jump * 0.25f,
            $"a metre across the midpoint moved the pan by {MathF.Abs(right - left):F3}, "
            + $"where nearest-wins would have moved it by about {jump:F3}");
    }

    [Fact]
    public void AWeightOfZeroIsAListenerThatIsNotThere() {
        var alone = Gains(AudioListenerSet.Single(Ears(0f, 0f)), At(0f, 10f));

        var set = default(AudioListenerSet);
        set.TryAdd(Ears(0f, 0f));
        set.TryAdd(Ears(0f, -10f), 0f);

        var weighted = Gains(set, At(0f, 10f));

        Assert.Equal(alone[0], weighted[0], 1e-4f);
        Assert.Equal(alone[1], weighted[1], 1e-4f);
    }

    /// <summary>Because a scene that forgot its listener should be audible and wrong, not silent.</summary>
    [Fact]
    public void AnEmptySetFallsBackToTheDefaultListener() {
        engine.SetListeners(default);

        Assert.Equal(1, engine.ListenerCount);
        Assert.Equal(AudioListener.Default.Position, engine.Listener.Position);
    }

    [Fact]
    public void SettingOneListenerStillWorksAndIsASetOfOne() {
        engine.SetListener(Ears(5f, 0f));

        Assert.Equal(1, engine.ListenerCount);
        Assert.Equal(new Vector3(5f, 0f, 0f), engine.Listener.Position);
        Assert.Equal(new Vector3(5f, 0f, 0f), engine.Listeners.Get(0).Position);
    }

    /// <summary>All the way through the mixer, which is where it has to be right.</summary>
    [Fact]
    public void ASoundBesideTheSecondPlayerIsHeardEvenThoughTheFirstIsMilesAway() {
        var set = default(AudioListenerSet);
        set.TryAdd(Ears(0f, 0f));
        set.TryAdd(Ears(0f, 800f));
        engine.SetListeners(set);

        engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
            IsSpatial = true,
            Spatial = At(0f, 802f)
        });

        var peak = 0f;

        for (var i = 0; i < 4; i++) {
            peak = MathF.Max(peak, AudioTestData.Peak(AudioTestData.Render(device, 64)));
        }

        Assert.True(peak > 0.2f, $"the second player is standing next to it, peak was {peak:F3}");
    }

    [Fact]
    public void TheEcsPassCollectsEveryListenerUpToTheCap() {
        using var world = new World("Listeners");
        using var system = new AudioSystem(engine);

        for (var i = 0; i < 6; i++) {
            var entity = world.Create();
            world.Add(entity, AudioListenerComponent.Default);
            world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(new Vector3(i * 10f, 0f, 0f)) });
        }

        system.Synchronize(world, 1f / 60f);

        // Six in the scene, four in the mix, and the count says so — which is what makes a scene with
        // five listeners a reportable mistake rather than a mystery.
        Assert.Equal(6, system.ListenerCount);
        Assert.Equal(AudioListenerSet.MaxListeners, engine.ListenerCount);
    }

    [Fact]
    public void AListenersWeightComesFromItsComponent() {
        using var world = new World("Weights");
        using var system = new AudioSystem(engine);

        var first = world.Create();
        world.Add(first, AudioListenerComponent.Default);
        world.Add(first, new WorldTransform { Value = Matrix4x4.Identity });

        var second = world.Create();
        world.Add(second, AudioListenerComponent.Default with { Weight = 0.25f });
        world.Add(second, new WorldTransform { Value = Matrix4x4.FromTranslation(new Vector3(50f, 0f, 0f)) });

        system.Synchronize(world, 1f / 60f);

        Assert.Equal(2, engine.ListenerCount);
        Assert.Equal(1f, engine.Listeners.WeightOf(0));
        Assert.Equal(0.25f, engine.Listeners.WeightOf(1));
    }
}
