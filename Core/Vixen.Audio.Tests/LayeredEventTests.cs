// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Audio.Events;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
using Vixen.Audio.Sources;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>A gunshot is a mechanism, a report and a tail — not one sound.</summary>
public sealed class LayeredEventTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public LayeredEventTests() => (engine, device) = AudioTestData.Engine(voices: 32);

    public void Dispose() => engine.Dispose();

    AudioEvent Sound(int frames = 4_800, params AudioEventLayer[] layers) => new(engine, new AudioEventDescription {
        Variants = [new(AudioTestData.Constant(frames, 1f))],
        Layers = layers
    });

    AudioEvent Container(params AudioEventLayer[] layers) =>
        new(engine, new AudioEventDescription { Layers = layers });

    [Fact]
    public void ALayerWithNoDelayPlaysInTheSameCall() {
        var tail = Sound();
        var report = Sound(4_800, new AudioEventLayer(tail));

        Assert.True(report.Play().IsValid);
        Assert.Equal(1, tail.InstanceCount);
    }

    /// <summary>The twenty milliseconds is most of what makes two sounds read as one event.</summary>
    [Fact]
    public void ALayerWithADelayWaitsForIt() {
        var tail = Sound(48_000);
        var report = Sound(48_000, new AudioEventLayer(tail) { DelaySeconds = 0.05f });

        report.Play();
        Assert.Equal(0, tail.InstanceCount);
        Assert.Equal(1, engine.PendingLayers);

        engine.Update(0.02f);
        Assert.Equal(0, tail.InstanceCount);

        engine.Update(0.04f);
        Assert.Equal(1, tail.InstanceCount);
        Assert.Equal(0, engine.PendingLayers);
    }

    /// <summary>
    ///     The ordinary way to write a composite: the parent decides where the whole thing is and
    ///     every sound comes from a layer.
    /// </summary>
    [Fact]
    public void AnEventWithNoVariantsOfItsOwnIsStillAContainerOfLayers() {
        var mechanism = Sound();
        var report = Sound();
        var gunshot = Container(new AudioEventLayer(mechanism), new AudioEventLayer(report));

        // Nothing of its own to play, so no handle — and both layers went.
        Assert.False(gunshot.Play().IsValid);
        Assert.Equal(1, mechanism.InstanceCount);
        Assert.Equal(1, report.InstanceCount);
    }

    /// <summary>A layer holds an event that already exists, so A can only layer B if B was built first.</summary>
    [Fact]
    public void ALayerCanItselfHaveLayers() {
        var third = Sound();
        var second = Sound(4_800, new AudioEventLayer(third));
        var first = Sound(4_800, new AudioEventLayer(second));

        first.Play();

        Assert.Equal(1, second.InstanceCount);
        Assert.Equal(1, third.InstanceCount);
    }

    /// <summary>Which is what makes walking the pending table while it is appended to safe.</summary>
    [Fact]
    public void ADelayedLayerCanScheduleMoreDelayedLayers() {
        var third = Sound(48_000);
        var second = Sound(48_000, new AudioEventLayer(third) { DelaySeconds = 0.05f });
        var first = Sound(48_000, new AudioEventLayer(second) { DelaySeconds = 0.05f });

        first.Play();
        engine.Update(0.06f);

        Assert.Equal(1, second.InstanceCount);
        Assert.Equal(0, third.InstanceCount);
        Assert.Equal(1, engine.PendingLayers);

        engine.Update(0.06f);
        Assert.Equal(1, third.InstanceCount);
    }

    [Fact]
    public void ATrimOnTheLayerMultipliesIntoWhatItsOwnEventChose() {
        var tails = engine.CreateBus("Tails");

        var tail = new AudioEvent(engine, new AudioEventDescription {
            Bus = tails.Index,
            Variants = [new(AudioTestData.Constant(48_000, 1f))],
            GainDb = -6f
        });

        var report = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(48_000, 1f))],
            Layers = [new AudioEventLayer(tail) { GainDb = -6f }]
        });

        report.Play();

        // The layer's own −6 is what the event chose; the trim's −6 is on top of it and is the
        // caller's, so it is not in LastGain. What reached the bus is both.
        Assert.Equal(0.5012f, tail.LastGain, 1e-3f);

        AudioTestData.Render(device, 64);

        // A quarter of full scale, panned centre.
        Assert.Equal(0.1772f, tails.PeakLevel, 5e-3f);
    }

    /// <summary>The cheapest variety there is, and it costs one comparison.</summary>
    [Fact]
    public void ProbabilityDecidesWhetherALayerHappensAtAll() {
        var ricochet = Sound(480);
        var impact = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(480, 1f))],
            Seed = 21,
            Layers = [new AudioEventLayer(ricochet) { Probability = 0.25f }]
        });

        var fired = 0;

        for (var i = 0; i < 400; i++) {
            var before = ricochet.PlayCount;
            impact.Play();

            if (ricochet.PlayCount > before) {
                fired++;
            }

            engine.StopAll();
            AudioTestData.Render(device, 64);
            engine.Update(0f);
        }

        Assert.InRange(fired, 75, 125);
    }

    [Fact]
    public void ProbabilityOfZeroNeverFiresAndOfOneAlwaysDoes() {
        var never = Sound(480);
        var always = Sound(480);

        var impact = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(480, 1f))],
            Layers = [
                new AudioEventLayer(never) { Probability = 0f },
                new AudioEventLayer(always)
            ]
        });

        for (var i = 0; i < 20; i++) {
            impact.Play();
            engine.StopAll();
            AudioTestData.Render(device, 64);
            engine.Update(0f);
        }

        Assert.Equal(0, never.PlayCount);
        Assert.Equal(20, always.PlayCount);
    }

    /// <summary>A layer that kept going after its parent stopped is a sound nobody can find the source of.</summary>
    [Fact]
    public void StoppingCascadesAndCancelsWhatWasStillWaiting() {
        var tail = Sound(48_000);
        var immediate = Sound(48_000);

        var report = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(48_000, 1f))],
            Layers = [
                new AudioEventLayer(immediate),
                new AudioEventLayer(tail) { DelaySeconds = 0.5f }
            ]
        });

        report.Play();
        Assert.Equal(1, immediate.InstanceCount);
        Assert.Equal(1, engine.PendingLayers);

        report.StopAll();

        Assert.Equal(0, immediate.InstanceCount);
        Assert.Equal(0, engine.PendingLayers);

        engine.Update(1f);
        Assert.Equal(0, tail.InstanceCount);
    }

    /// <summary>
    ///     The instance limit is the event saying no, and a tail with no report in front of it is
    ///     worse than silence. Which is a different case from the pool being full — see below.
    /// </summary>
    [Fact]
    public void ALayerIsRefusedWithItsParent() {
        var tail = Sound(48_000);

        var report = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Constant(48_000, 1f))],
            MaxInstances = 1,
            Steal = EventStealMode.None,
            Layers = [new AudioEventLayer(tail)]
        });

        Assert.True(report.Play().IsValid);
        Assert.False(report.Play().IsValid);
        Assert.Equal(1, tail.InstanceCount);
    }

    /// <summary>
    ///     An empty pool is the engine saying "not right now" rather than the event saying no, so the
    ///     layers still try — and a tail is often the part that carries at a distance.
    /// </summary>
    [Fact]
    public void ALayerStillTriesWhenTheParentFoundNoVoice() {
        var (small, _) = AudioTestData.Engine(voices: 2);

        using (small) {
            // Worth more than what is already playing, so it can take a slot the report could not.
            var tail = new AudioEvent(small, new AudioEventDescription {
                Variants = [new(AudioTestData.Constant(48_000, 1f))],
                Priority = 200
            });

            var report = new AudioEvent(small, new AudioEventDescription {
                Variants = [new(AudioTestData.Constant(48_000, 1f))],
                Priority = -10,
                Layers = [new AudioEventLayer(tail)]
            });

            // Fill the pool with something nothing may displace.
            small.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Priority = 100 });
            small.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Priority = 100 });

            Assert.False(report.Play().IsValid);
            Assert.Equal(1, tail.PlayCount);
        }
    }

    [Fact]
    public void AFullPendingTableDropsRatherThanGrows() {
        var tail = Sound(48_000);
        var report = Sound(48_000, new AudioEventLayer(tail) { DelaySeconds = 10f });

        for (var i = 0; i < 200; i++) {
            report.Play();
        }

        Assert.Equal(128, engine.PendingLayers);
        Assert.True(engine.DroppedLayers > 0);
    }

    [Fact]
    public void AnAssetResolvesItsLayersThroughTheLibrary() {
        var tail = Sound();
        var library = new Library { ["Tail"] = tail };

        var report = engine.LoadEvent(new AudioEventAsset {
            Name = "Report",
            Variants = [new() { Clip = new ContentReference<AudioClip>(default, AudioTestData.Constant(480, 1f)) }],
            Layers = [new() { Event = "Tail", DelaySeconds = 0.02f, GainDb = -3f }]
        }, out var problems, library);

        Assert.Empty(problems);
        Assert.Single(report.Layers);
        Assert.Equal(0.02f, report.Layers[0].DelaySeconds);
        Assert.Same(tail, report.Layers[0].Sound);
    }

    /// <summary>A gunshot that is only its report is still a gunshot.</summary>
    [Fact]
    public void LayersThatDoNotResolveAreReportedAndTheEventStillBuilds() {
        var withoutLibrary = engine.LoadEvent(new AudioEventAsset {
            Name = "Report",
            Variants = [new() { Clip = new ContentReference<AudioClip>(default, AudioTestData.Constant(480, 1f)) }],
            Layers = [new() { Event = "Tail" }]
        }, out var noLibrary);

        Assert.Empty(withoutLibrary.Layers);
        Assert.Contains(noLibrary, p => p.Contains("no event library", StringComparison.Ordinal));

        var unknown = engine.LoadEvent(new AudioEventAsset {
            Name = "Report",
            Variants = [new() { Clip = new ContentReference<AudioClip>(default, AudioTestData.Constant(480, 1f)) }],
            Layers = [new() { Event = "Missing" }]
        }, out var problems, new Library());

        Assert.Empty(unknown.Layers);
        Assert.Contains(problems, p => p.Contains("Missing", StringComparison.Ordinal));
    }

    /// <summary>The bridge that lets voice chat be an event rather than a raw Play.</summary>
    [Fact]
    public void AnEventCanPlayASourceTheCallerSupplied() {
        var chat = engine.CreateBus("Chat");

        var voice = new AudioEvent(engine, new AudioEventDescription {
            Bus = chat.Index,
            GainDb = -6f,
            MaxInstances = 2,
            Steal = EventStealMode.None,
            Parameters = new([
                new AudioParameterDefinition {
                    Name = "submersion",
                    Automation = [new(AudioParameterTarget.LowPassHz, AudioCurve.Ramp(24_000f, 300f))]
                }
            ])
        });

        using var microphone = new NullAudioCaptureDevice(new AudioCaptureOptions());
        microphone.Start();

        var handle = voice.Play(new CaptureSampleProvider(microphone), new AudioEventPlayback());

        Assert.True(handle.IsValid);
        Assert.Equal(chat.Index, engine.Mixer.Buses[chat.Index].Index);
        Assert.Equal(0.5012f, engine.GainOf(handle), 1e-3f);
        Assert.NotNull(engine.ParametersOf(handle));
        Assert.True(voice.SetParameter(handle, "submersion", 1f));
        Assert.Equal(1, voice.InstanceCount);
    }

    /// <summary>Which is what the instance limit is for: a player cannot open two voices.</summary>
    [Fact]
    public void ASuppliedSourceIsSubjectToTheEventsInstanceLimit() {
        var voice = new AudioEvent(engine, new AudioEventDescription {
            MaxInstances = 1,
            Steal = EventStealMode.None
        });

        using var microphone = new NullAudioCaptureDevice(new AudioCaptureOptions());
        microphone.Start();

        Assert.True(voice.Play(new CaptureSampleProvider(microphone), new AudioEventPlayback()).IsValid);
        Assert.False(voice.Play(new CaptureSampleProvider(microphone), new AudioEventPlayback()).IsValid);
    }

    sealed class Library : IAudioEventLibrary {
        readonly Dictionary<string, AudioEvent> events = [];

        public AudioEvent this[string name] {
            set => events[name] = value;
        }

        public AudioEvent? Find(string name) => events.GetValueOrDefault(name);
    }
}
