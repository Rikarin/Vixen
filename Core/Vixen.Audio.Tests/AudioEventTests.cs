// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Audio.Events;
using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The variant selector on its own — no mixer, no device, no clip.</summary>
public sealed class VariantSelectionTests {
    static VariantSelector Selector(VariantSelection mode, int count, uint seed = 12_345) {
        var weights = new float[count];
        Array.Fill(weights, 1f);
        return new(weights, mode, seed);
    }

    static int[] Draw(VariantSelector selector, int count) {
        var drawn = new int[count];

        for (var i = 0; i < count; i++) {
            drawn[i] = selector.Next();
        }

        return drawn;
    }

    /// <summary>The property that makes a bag worth having over a die.</summary>
    [Fact]
    public void ShuffleVisitsEveryVariantBeforeAnyPlaysTwice() {
        var selector = Selector(VariantSelection.Shuffle, 5);

        for (var round = 0; round < 20; round++) {
            var drawn = Draw(selector, 5);
            Assert.Equal([0, 1, 2, 3, 4], drawn.Order());
        }
    }

    /// <summary>
    ///     The bag boundary is the one place shuffling can still repeat: the last of one round and the
    ///     first of the next are independent, so about one round in five would open with the sound
    ///     that just played.
    /// </summary>
    [Fact]
    public void ShuffleDoesNotRepeatAcrossABagBoundary() {
        var selector = Selector(VariantSelection.Shuffle, 5);
        var drawn = Draw(selector, 500);

        for (var i = 1; i < drawn.Length; i++) {
            Assert.NotEqual(drawn[i - 1], drawn[i]);
        }
    }

    [Fact]
    public void NoRepeatNeverPlaysTheSameOneTwiceRunning() {
        var selector = Selector(VariantSelection.RandomNoRepeat, 3);
        var drawn = Draw(selector, 500);

        for (var i = 1; i < drawn.Length; i++) {
            Assert.NotEqual(drawn[i - 1], drawn[i]);
        }
    }

    /// <summary>Plain random is the mode that <em>is</em> allowed to repeat, and does.</summary>
    [Fact]
    public void RandomRepeats() {
        var selector = Selector(VariantSelection.Random, 3);
        var drawn = Draw(selector, 500);
        var runs = 0;

        for (var i = 1; i < drawn.Length; i++) {
            if (drawn[i - 1] == drawn[i]) {
                runs++;
            }
        }

        // A third of 499 is about 166. Anything in three figures is the distribution working.
        Assert.InRange(runs, 100, 240);
    }

    [Fact]
    public void WeightsDecideHowOftenAVariantIsHeard() {
        var selector = new VariantSelector([1f, 3f], VariantSelection.Random, 777);
        var drawn = Draw(selector, 4_000);
        var second = drawn.Count(index => index == 1);

        // Three thousand of four thousand, and the tolerance is about four standard deviations.
        Assert.InRange(second, 2_880, 3_120);
    }

    /// <summary>How a variant is auditioned out without being deleted.</summary>
    [Fact]
    public void AZeroWeightNeverPlays() {
        var selector = new VariantSelector([1f, 0f, 1f], VariantSelection.Random, 42);
        Assert.DoesNotContain(1, Draw(selector, 1_000));
    }

    /// <summary>A bag visits every entry once a round, so a weight there could only mean something else.</summary>
    [Fact]
    public void ShuffleIgnoresWeights() {
        var selector = new VariantSelector([1f, 0f, 1f], VariantSelection.Shuffle, 42);
        Assert.Contains(1, Draw(selector, 30));
    }

    [Fact]
    public void SequentialGoesInTheOrderItWasWritten() {
        var selector = Selector(VariantSelection.Sequential, 3);
        Assert.Equal([0, 1, 2, 0, 1, 2, 0], Draw(selector, 7));
    }

    [Fact]
    public void OneVariantIsAlwaysTheAnswerAndNoneIsMinusOne() {
        Assert.Equal(0, Selector(VariantSelection.Shuffle, 1).Next());
        Assert.Equal(0, Selector(VariantSelection.RandomNoRepeat, 1).Next());
        Assert.Equal(-1, Selector(VariantSelection.Shuffle, 0).Next());
    }

    /// <summary>Without this every assertion above would be a coin toss rather than a test.</summary>
    [Fact]
    public void TheSameSeedGivesTheSameSequence() {
        Assert.Equal(
            Draw(Selector(VariantSelection.Shuffle, 6, 99), 60),
            Draw(Selector(VariantSelection.Shuffle, 6, 99), 60)
        );

        Assert.NotEqual(
            Draw(Selector(VariantSelection.Shuffle, 6, 99), 60),
            Draw(Selector(VariantSelection.Shuffle, 6, 100), 60)
        );
    }
}

/// <summary>The event itself: variation, instance limits, and what a file builds into.</summary>
public sealed class AudioEventTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public AudioEventTests() => (engine, device) = AudioTestData.Engine(voices: 16);

    public void Dispose() => engine.Dispose();

    AudioEvent Event(AudioEventDescription description) => new(engine, description);

    static AudioEventVariant[] Variants(int count, int frames = 4_800) {
        var variants = new AudioEventVariant[count];

        for (var i = 0; i < count; i++) {
            variants[i] = new(AudioTestData.Constant(frames, 1f));
        }

        return variants;
    }

    [Fact]
    public void AnEventPlaysThroughTheOrdinaryMixer() {
        var sound = Event(new() { Variants = Variants(3) });
        var handle = sound.Play();

        Assert.True(handle.IsValid);
        Assert.True(engine.IsPlaying(handle));
        Assert.True(AudioTestData.Peak(AudioTestData.Render(device, 16)) > 0f);
    }

    [Fact]
    public void AnEventWithNoVariantsIsQuietRatherThanBroken() {
        var sound = Event(new());

        Assert.Equal(VoiceHandle.None, sound.Play());
        Assert.Equal(0, sound.VariantCount);
    }

    /// <summary>A variant with no clip would otherwise be a draw that plays nothing — a sound misfiring at random.</summary>
    [Fact]
    public void AVariantWithNoClipIsDroppedRatherThanKeptAsAHole() {
        var sound = Event(new() {
            Variants = [new(AudioTestData.Constant(480, 1f)), new(null!), new(AudioTestData.Constant(480, 1f))]
        });

        Assert.Equal(2, sound.VariantCount);
    }

    [Fact]
    public void VariationMovesTheLevelAndThePitchWithinWhatWasAsked() {
        var sound = Event(new() {
            Variants = Variants(1),
            GainVarianceDb = 3f,
            PitchVarianceSemitones = 2f,
            Seed = 5
        });

        var gains = new List<float>();
        var pitches = new List<float>();

        for (var i = 0; i < 32; i++) {
            sound.Play();
            gains.Add(sound.LastGain);
            pitches.Add(sound.LastPitch);
            engine.StopAll();
            engine.Update(0f);
        }

        // Inside the declared range: ±3 dB is 0.708 to 1.413, and ±2 semitones is 0.891 to 1.122.
        Assert.All(gains, gain => Assert.InRange(gain, 0.7f, 1.42f));
        Assert.All(pitches, pitch => Assert.InRange(pitch, 0.89f, 1.13f));

        // And actually varying, which is the point. A generator returning a constant would pass
        // every bound above.
        Assert.True(gains.Max() - gains.Min() > 0.3f, $"gain spread was only {gains.Max() - gains.Min():F3}");
        Assert.True(pitches.Max() - pitches.Min() > 0.1f);
    }

    [Fact]
    public void NoVarianceMeansEveryPlayIsIdentical() {
        var sound = Event(new() { Variants = Variants(1), GainDb = -6f });

        for (var i = 0; i < 8; i++) {
            sound.Play();
            Assert.Equal(0.5012f, sound.LastGain, 1e-3f);
            Assert.Equal(1f, sound.LastPitch);
        }
    }

    /// <summary>A take recorded hot is corrected in the asset, not in the wav.</summary>
    [Fact]
    public void AVariantCarriesItsOwnCorrections() {
        var sound = Event(new() {
            Variants = [new(AudioTestData.Constant(480, 1f)) { GainDb = -12f, PitchSemitones = 12f }],
            Selection = VariantSelection.Sequential
        });

        sound.Play();

        Assert.Equal(0.2512f, sound.LastGain, 1e-3f);
        Assert.Equal(2f, sound.LastPitch, 1e-4f);
    }

    [Fact]
    public void TheInstanceLimitRefusesWhenItIsToldTo() {
        var sound = Event(new() {
            Variants = Variants(1, 48_000),
            MaxInstances = 2,
            Steal = EventStealMode.None
        });

        Assert.True(sound.Play().IsValid);
        Assert.True(sound.Play().IsValid);
        Assert.False(sound.Play().IsValid);
        Assert.Equal(2, sound.InstanceCount);
    }

    [Fact]
    public void StealingTheOldestMakesRoomForTheNewOne() {
        var sound = Event(new() {
            Variants = Variants(1, 48_000),
            MaxInstances = 2,
            Steal = EventStealMode.Oldest
        });

        var first = sound.Play();
        var second = sound.Play();
        var third = sound.Play();

        Assert.True(third.IsValid);
        Assert.Equal(VoiceState.Stopping, engine.StateOf(first));
        Assert.Equal(VoiceState.Playing, engine.StateOf(second));
        Assert.Equal(2, sound.InstanceCount);
    }

    /// <summary>For a long sound the newcomer is the interloper, not the one a minute in.</summary>
    [Fact]
    public void StealingTheNewestKeepsTheOneThatHasBeenGoingLongest() {
        var sound = Event(new() {
            Variants = Variants(1, 48_000),
            MaxInstances = 2,
            Steal = EventStealMode.Newest
        });

        var first = sound.Play();
        var second = sound.Play();

        Assert.True(sound.Play().IsValid);
        Assert.Equal(VoiceState.Playing, engine.StateOf(first));
        Assert.Equal(VoiceState.Stopping, engine.StateOf(second));
    }

    /// <summary>Distance and attenuation count, not just the fader — which is why it is not "quietest gain".</summary>
    [Fact]
    public void TheQuietestGivesWayFirst() {
        var sound = Event(new() {
            Variants = Variants(1, 48_000),
            MaxInstances = 2,
            Steal = EventStealMode.Quietest,
            IsSpatial = true,
            Spatial = new() { MinDistance = 1f, MaxDistance = 400f }
        });

        engine.SetListener(new());
        var near = sound.Play(new Vector3(0f, 0f, 2f));
        var far = sound.Play(new Vector3(0f, 0f, 300f));

        // Audibility is what the spatialiser worked out last block, so there has to have been one.
        AudioTestData.Render(device, 64);

        Assert.True(engine.AudibilityOf(far) < engine.AudibilityOf(near));
        Assert.True(sound.Play(new Vector3(0f, 0f, 2f)).IsValid);
        Assert.Equal(VoiceState.Stopping, engine.StateOf(far));
        Assert.Equal(VoiceState.Playing, engine.StateOf(near));
    }

    [Fact]
    public void AnInstanceThatEndedOnItsOwnStopsCountingAgainstTheLimit() {
        var sound = Event(new() {
            Variants = Variants(1, 32),
            MaxInstances = 1,
            Steal = EventStealMode.None
        });

        Assert.True(sound.Play().IsValid);
        Assert.False(sound.Play().IsValid);

        // Long enough for a thirty-two frame clip to run out several times over.
        AudioTestData.Render(device, 512);
        engine.Update(0f);

        Assert.Equal(0, sound.InstanceCount);
        Assert.True(sound.Play().IsValid);
    }

    /// <summary>
    ///     The reason the room check comes before the draw. Otherwise a busy event silently skips
    ///     variants, and the guarantee that every one is heard is not one.
    /// </summary>
    [Fact]
    public void ARefusedPlayDoesNotAdvanceTheShuffleBag() {
        var sound = Event(new() {
            Variants = Variants(4, 48_000),
            MaxInstances = 1,
            Steal = EventStealMode.None
        });

        sound.Play();
        var chosen = sound.Variants.Last;

        for (var i = 0; i < 8; i++) {
            Assert.False(sound.Play().IsValid);
        }

        Assert.Equal(chosen, sound.Variants.Last);
    }

    [Fact]
    public void StopAllStopsEveryCopy() {
        var sound = Event(new() { Variants = Variants(1, 48_000) });
        var handles = new[] { sound.Play(), sound.Play(), sound.Play() };

        sound.StopAll();

        Assert.Equal(0, sound.InstanceCount);
        Assert.All(handles, handle => Assert.Equal(VoiceState.Stopping, engine.StateOf(handle)));
    }

    /// <summary>A fade is what a level ending wants; StopAll takes one block.</summary>
    [Fact]
    public void FadeOutAllLetsThemGoWithoutHoldingTheLimit() {
        var sound = Event(new() { Variants = Variants(1, 48_000), MaxInstances = 2 });

        var first = sound.Play();
        sound.Play();
        sound.FadeOutAll(TimeSpan.FromSeconds(2));

        Assert.Equal(VoiceState.Playing, engine.StateOf(first));
        Assert.True(engine.IsFading(first));
        Assert.Equal(0, sound.InstanceCount);
    }

    /// <summary>Where a sound is belongs to the caller; how it attenuates belongs to the event.</summary>
    [Fact]
    public void PlacingASoundKeepsEverythingTheEventDecided() {
        var sound = Event(new() {
            Variants = Variants(1),
            IsSpatial = true,
            Spatial = new() { MinDistance = 3f, MaxDistance = 40f, Attenuation = AttenuationModel.Linear }
        });

        var placed = sound.Place(new Vector3(1f, 2f, 3f), Vector3.Zero, Vector3.Zero);

        Assert.Equal(new Vector3(1f, 2f, 3f), placed.Position);
        Assert.Equal(3f, placed.MinDistance);
        Assert.Equal(40f, placed.MaxDistance);
        Assert.Equal(AttenuationModel.Linear, placed.Attenuation);
    }

    [Fact]
    public void APlayTrimsTheEventRatherThanReplacingIt() {
        var sound = Event(new() { Variants = Variants(1), GainDb = -6f });
        var handle = sound.Play(new AudioEventPlayback { Gain = 0.5f });

        // −6 dB is about a half, and the caller's half on top of it is about a quarter.
        Assert.Equal(0.2506f, engine.GainOf(handle), 1e-3f);
        Assert.Equal(0.5012f, sound.LastGain, 1e-3f);
    }
}

/// <summary>What a file builds into, and what it says when it cannot.</summary>
public sealed class AudioEventAssetTests : IDisposable {
    readonly AudioEngine engine;

    public AudioEventAssetTests() => (engine, _) = AudioTestData.Engine();

    public void Dispose() => engine.Dispose();

    static ContentReference<AudioClip> Held(AudioClip clip) => new(default, clip);

    [Fact]
    public void TheBusIsResolvedByName() {
        var music = engine.CreateBus("Music");

        var sound = engine.LoadEvent(new AudioEventAsset {
            Name = "Theme",
            Bus = "Music",
            Variants = [new() { Clip = Held(AudioTestData.Constant(480, 1f)) }]
        }, out var problems);

        Assert.Empty(problems);
        Assert.Equal(music.Index, sound.Bus);
    }

    /// <summary>A footstep on the wrong bus is a mix problem; a missing footstep is an afternoon.</summary>
    [Fact]
    public void AnUnknownBusIsReportedAndPlaysOnTheMaster() {
        var sound = engine.LoadEvent(new AudioEventAsset {
            Name = "Theme",
            Bus = "Nowhere",
            Variants = [new() { Clip = Held(AudioTestData.Constant(480, 1f)) }]
        }, out var problems);

        Assert.Equal(0, sound.Bus);
        Assert.Contains(problems, problem => problem.Contains("Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnresolvedClipIsDroppedAndSaidSo() {
        var sound = engine.LoadEvent(new AudioEventAsset {
            Name = "Footsteps",
            Variants = [
                new() { Clip = Held(AudioTestData.Constant(480, 1f)) },
                new() { Clip = new ContentReference<AudioClip>(new(7, 9)) },
                new()
            ]
        }, out var problems);

        Assert.Equal(1, sound.VariantCount);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("unresolved clip", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("no clip", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEventWithNothingPlayableSaysSoAndStillLoads() {
        var sound = engine.LoadEvent(new AudioEventAsset { Name = "Empty" }, out var problems);

        Assert.Equal(0, sound.VariantCount);
        Assert.Contains(problems, problem => problem.Contains("silent", StringComparison.Ordinal));
    }

    /// <summary>The presence of the block is the switch, as AudioSpatial's is in the ECS.</summary>
    [Fact]
    public void TheSpatialBlockIsWhatMakesAnEventPositional() {
        var flat = engine.LoadEvent(new AudioEventAsset {
            Variants = [new() { Clip = Held(AudioTestData.Constant(480, 1f)) }]
        }, out _);

        var placed = engine.LoadEvent(new AudioEventAsset {
            Variants = [new() { Clip = Held(AudioTestData.Constant(480, 1f)) }],
            Spatial = new() { MinDistance = 4f, MaxDistance = 90f }
        }, out _);

        Assert.False(flat.IsSpatial);
        Assert.True(placed.IsSpatial);
        Assert.Equal(4f, placed.Spatial.MinDistance);
        Assert.Equal(90f, placed.Spatial.MaxDistance);
    }

    [Fact]
    public void EverythingElseCopiesStraightAcross() {
        var sound = engine.LoadEvent(new AudioEventAsset {
            Name = "Impacts",
            Variants = [new() { Clip = Held(AudioTestData.Constant(480, 1f)) }],
            Selection = VariantSelection.Sequential,
            MaxInstances = 4,
            Steal = EventStealMode.Quietest
        }, out _);

        Assert.Equal("Impacts", sound.Name);
        Assert.Equal(VariantSelection.Sequential, sound.Variants.Mode);
        Assert.Equal(4, sound.MaxInstances);
        Assert.Equal(EventStealMode.Quietest, sound.Steal);
    }
}
