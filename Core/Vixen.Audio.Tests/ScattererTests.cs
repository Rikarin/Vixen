// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Events;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The ambience tool: a sound thrown about at irregular intervals from irregular directions.</summary>
public sealed class ScattererTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public ScattererTests() => (engine, device) = AudioTestData.Engine(voices: 16);

    public void Dispose() => engine.Dispose();

    AudioEvent Birds(int frames = 4_800) => new(engine, new AudioEventDescription {
        Variants = [new(AudioTestData.Constant(frames, 1f))],
        IsSpatial = true,
        Spatial = new SpatialSettings { MinDistance = 1f, MaxDistance = 1_000f, DopplerFactor = 0f }
    });

    AudioScatterer Scatterer(AudioScattererSettings settings, AudioEvent? sound = null) =>
        new(engine, sound ?? Birds(), settings);

    /// <summary>Ten scatterers started on the load frame must not all fire on it.</summary>
    [Fact]
    public void StartingDoesNotSpawnImmediately() {
        var scatterer = Scatterer(new() { MinimumInterval = 1f, MaximumInterval = 2f });
        scatterer.Start();

        Assert.True(scatterer.IsRunning);
        Assert.Equal(0, scatterer.SpawnCount);
        Assert.True(scatterer.NextSpawnSeconds >= 1f);
        Assert.False(scatterer.Update(0.5f).IsValid);
    }

    [Fact]
    public void ItSpawnsOnceTheIntervalHasRunOut() {
        var scatterer = Scatterer(new() { MinimumInterval = 1f, MaximumInterval = 1f });
        scatterer.Start();

        Assert.False(scatterer.Update(0.9f).IsValid);
        Assert.True(scatterer.Update(0.2f).IsValid);
        Assert.Equal(1, scatterer.SpawnCount);
    }

    /// <summary>
    ///     A frame that took a second would otherwise release everything that should have happened
    ///     during it at the same instant — and those spawns were meant to be spread over a second the
    ///     player did not experience.
    /// </summary>
    [Fact]
    public void AVeryLongFrameSpawnsOnceRatherThanCatchingUp() {
        var scatterer = Scatterer(new() { MinimumInterval = 0.1f, MaximumInterval = 0.1f });
        scatterer.Start();

        scatterer.Update(10f);

        Assert.Equal(1, scatterer.SpawnCount);
    }

    /// <summary>The ear finds a period of a second or two within about four repetitions.</summary>
    [Fact]
    public void TheIntervalVariesRatherThanBeingAMetronome() {
        var scatterer = Scatterer(new() { MinimumInterval = 1f, MaximumInterval = 4f, Seed = 7 });
        scatterer.Start();

        var intervals = new List<float>();

        for (var i = 0; i < 30; i++) {
            intervals.Add(scatterer.NextSpawnSeconds);
            scatterer.Update(scatterer.NextSpawnSeconds + 0.001f);
        }

        Assert.All(intervals, interval => Assert.InRange(interval, 1f, 4f));
        Assert.True(intervals.Max() - intervals.Min() > 1.5f, "they were all much the same");
    }

    /// <summary>The failure mode of every scatterer ever written is a bird landing on your head.</summary>
    [Fact]
    public void NothingLandsInsideTheHoleInTheMiddle() {
        var scatterer = Scatterer(new() { MinimumDistance = 8f, MaximumDistance = 30f, Seed = 3 });

        for (var i = 0; i < 500; i++) {
            var distance = scatterer.Scatter().Length();
            Assert.InRange(distance, 8f - 1e-3f, 30f + 1e-3f);
        }
    }

    /// <summary>
    ///     Drawing the distance evenly puts far too many spawns near the middle, because the area at a
    ///     radius grows with the radius. The audible version of that is "everything is happening right
    ///     next to me".
    /// </summary>
    [Fact]
    public void SpawnsAreSpreadThroughTheVolumeRatherThanAlongTheRadius() {
        var scatterer = Scatterer(new() { MinimumDistance = 0f, MaximumDistance = 30f, Seed = 11 });
        var inner = 0;

        // Half the radius is an eighth of the volume, so about an eighth of the draws — not a half,
        // which is what an even draw along the radius would give.
        for (var i = 0; i < 4_000; i++) {
            if (scatterer.Scatter().Length() < 15f) {
                inner++;
            }
        }

        Assert.InRange(inner / 4_000f, 0.09f, 0.16f);
    }

    [Fact]
    public void TheVerticalSpreadIsWhatDecidesHowFlatTheRingIs() {
        var flat = Scatterer(new() { VerticalSpread = 0f, MinimumDistance = 10f, MaximumDistance = 10f, Seed = 5 });
        var round = Scatterer(new() { VerticalSpread = 1f, MinimumDistance = 10f, MaximumDistance = 10f, Seed = 5 });

        var (flattest, roundest) = (0f, 0f);

        for (var i = 0; i < 200; i++) {
            flattest = MathF.Max(flattest, MathF.Abs(flat.Scatter().Y));
            roundest = MathF.Max(roundest, MathF.Abs(round.Scatter().Y));
        }

        Assert.Equal(0f, flattest, 1e-4f);
        Assert.True(roundest > 8f, $"a full sphere should reach overhead, got {roundest:F2}");
    }

    /// <summary>Ambience that cannot be walked away from, against a place that must be.</summary>
    [Fact]
    public void FollowingTheListenerIsWhatSeparatesAmbienceFromAPlace() {
        engine.SetListener(new AudioListener { Position = new Vector3(100f, 0f, 0f) });

        var following = Scatterer(new() { FollowListener = true, MinimumDistance = 5f, MaximumDistance = 6f });
        var fixedPlace = Scatterer(new() { FollowListener = false, MinimumDistance = 5f, MaximumDistance = 6f });
        fixedPlace.Origin = new Vector3(-100f, 0f, 0f);

        Assert.InRange((following.Scatter() - new Vector3(100f, 0f, 0f)).Length(), 5f, 6f);
        Assert.InRange((fixedPlace.Scatter() - new Vector3(-100f, 0f, 0f)).Length(), 5f, 6f);
    }

    /// <summary>What the event's own instance limit is for, seen from the thing that will hit it.</summary>
    [Fact]
    public void TheEventsInstanceLimitIsWhatCapsAFastScatterer() {
        var sound = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(48_000, 1f))],
            MaxInstances = 3,
            Steal = EventStealMode.None
        });

        var scatterer = Scatterer(new() { MinimumInterval = 0.01f, MaximumInterval = 0.01f }, sound);
        scatterer.Start();

        for (var i = 0; i < 20; i++) {
            scatterer.Update(0.02f);
        }

        Assert.Equal(3, sound.InstanceCount);
    }

    [Fact]
    public void StoppingLeavesWhatIsPlayingAndStopAllDoesNot() {
        var sound = Birds(48_000);
        var scatterer = Scatterer(new() { MinimumInterval = 0.01f, MaximumInterval = 0.01f }, sound);
        scatterer.Start();

        scatterer.Update(0.02f);
        Assert.Equal(1, sound.InstanceCount);

        scatterer.Stop();
        Assert.False(scatterer.IsRunning);
        Assert.Equal(1, sound.InstanceCount);

        scatterer.StopAll();
        Assert.Equal(0, sound.InstanceCount);
    }

    [Fact]
    public void ASpawnActuallyReachesTheMixer() {
        engine.SetListener(new AudioListener());
        var scatterer = Scatterer(new() { MinimumDistance = 2f, MaximumDistance = 3f });

        Assert.True(scatterer.SpawnNow().IsValid);
        Assert.Equal(1, scatterer.SpawnCount);
        Assert.True(AudioTestData.Peak(AudioTestData.Render(device, 64)) > 0f);
    }

    [Fact]
    public void AStoppedScattererTicksToNothing() {
        var scatterer = Scatterer(new() { MinimumInterval = 0.01f, MaximumInterval = 0.01f });

        Assert.False(scatterer.Update(10f).IsValid);
        Assert.Equal(0, scatterer.SpawnCount);
    }
}
