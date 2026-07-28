// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Parameters the engine fills in from what the spatialiser already worked out.</summary>
public sealed class BuiltinParameterTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public BuiltinParameterTests() => (engine, device) = AudioTestData.Engine(voices: 8);

    public void Dispose() => engine.Dispose();

    static AudioParameterSheet Sheet(AudioBuiltinParameter builtin, float minimum, float maximum) => new([
        new AudioParameterDefinition {
            Name = builtin.ToString(),
            Builtin = builtin,
            Minimum = minimum,
            Maximum = maximum
        }
    ]);

    /// <summary>Plays a sound at a place, renders a block so the spatialiser has run, and updates.</summary>
    VoiceHandle Emit(AudioParameterSheet sheet, Vector3 position, Vector3 velocity = default) {
        var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
            IsSpatial = true,
            Spatial = new SpatialSettings {
                Position = position,
                Velocity = velocity,
                MinDistance = 1f,
                MaxDistance = 1_000f,
                DopplerFactor = 0f
            }
        });

        engine.AttachParameters(handle, sheet);
        AudioTestData.Render(device, 64);
        engine.Update(0f);
        return handle;
    }

    [Fact]
    public void DistanceIsFilledInWithoutGameplayTouchingIt() {
        engine.SetListener(new AudioListener());
        var handle = Emit(Sheet(AudioBuiltinParameter.Distance, 0f, 500f), new Vector3(0f, 0f, 30f));

        Assert.Equal(30f, engine.ParameterOf(handle, 0), 0.01f);
    }

    /// <summary>Zero ahead, +90 to the right, ±180 behind — the axis somebody would draw a curve on.</summary>
    [Fact]
    public void DirectionIsSignedDegreesInTheListenersOwnFrame() {
        engine.SetListener(new AudioListener());
        var sheet = Sheet(AudioBuiltinParameter.Direction, -180f, 180f);

        Assert.Equal(0f, engine.ParameterOf(Emit(sheet, new Vector3(0f, 0f, -10f)), 0), 0.5f);
        Assert.Equal(90f, MathF.Abs(engine.ParameterOf(Emit(sheet, new Vector3(10f, 0f, 0f)), 0)), 0.5f);
        Assert.Equal(180f, MathF.Abs(engine.ParameterOf(Emit(sheet, new Vector3(0f, 0f, 10f)), 0)), 0.5f);
    }

    /// <summary>And the two sides have opposite signs, which is what makes it an axis rather than an angle.</summary>
    [Fact]
    public void DirectionTellsLeftFromRight() {
        engine.SetListener(new AudioListener());
        var sheet = Sheet(AudioBuiltinParameter.Direction, -180f, 180f);

        var right = engine.ParameterOf(Emit(sheet, new Vector3(10f, 0f, 0f)), 0);
        var left = engine.ParameterOf(Emit(sheet, new Vector3(-10f, 0f, 0f)), 0);

        Assert.Equal(-right, left, 0.5f);
        Assert.NotEqual(0f, right);
    }

    [Fact]
    public void ElevationIsPositiveOverheadAndNegativeUnderfoot() {
        engine.SetListener(new AudioListener());
        var sheet = Sheet(AudioBuiltinParameter.Elevation, -90f, 90f);

        Assert.Equal(90f, engine.ParameterOf(Emit(sheet, new Vector3(0f, 10f, 0f)), 0), 0.5f);
        Assert.Equal(-90f, engine.ParameterOf(Emit(sheet, new Vector3(0f, -10f, 0f)), 0), 0.5f);
        Assert.Equal(0f, engine.ParameterOf(Emit(sheet, new Vector3(0f, 0f, -10f)), 0), 0.5f);
    }

    [Fact]
    public void SpeedIsHowFastTheSourceIsMovingRegardlessOfDirection() {
        engine.SetListener(new AudioListener());
        var sheet = Sheet(AudioBuiltinParameter.Speed, 0f, 100f);

        var away = Emit(sheet, new Vector3(0f, 0f, 20f), new Vector3(0f, 0f, 30f));
        var across = Emit(sheet, new Vector3(0f, 0f, 20f), new Vector3(30f, 0f, 0f));

        Assert.Equal(30f, engine.ParameterOf(away, 0), 0.01f);
        Assert.Equal(30f, engine.ParameterOf(across, 0), 0.01f);
    }

    /// <summary>
    ///     Refused rather than ignored: a caller setting one would watch it revert every frame with
    ///     nothing to go on.
    /// </summary>
    [Fact]
    public void GameplayCannotSetABuiltIn() {
        engine.SetListener(new AudioListener());
        var handle = Emit(Sheet(AudioBuiltinParameter.Distance, 0f, 500f), new Vector3(0f, 0f, 30f));

        Assert.False(engine.SetParameter(handle, "Distance", 5f));
        engine.Update(0f);
        Assert.Equal(30f, engine.ParameterOf(handle, 0), 0.01f);
    }

    [Fact]
    public void AValueOutsideTheRangeIsClampedIntoIt() {
        engine.SetListener(new AudioListener());
        var handle = Emit(Sheet(AudioBuiltinParameter.Distance, 0f, 10f), new Vector3(0f, 0f, 300f));

        Assert.Equal(10f, engine.ParameterOf(handle, 0));
    }

    /// <summary>A sound in the room has no geometry, and must not inherit the last one that had some.</summary>
    [Fact]
    public void ANonSpatialVoiceReadsZeroRatherThanWhatWasThereBefore() {
        engine.SetListener(new AudioListener());
        var sheet = Sheet(AudioBuiltinParameter.Distance, 0f, 500f);

        Emit(sheet, new Vector3(0f, 0f, 200f));

        var flat = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.AttachParameters(flat, sheet);
        AudioTestData.Render(device, 64);
        engine.Update(0f);

        Assert.Equal(0f, engine.ParameterOf(flat, 0));
    }

    /// <summary>Built-in and gameplay-driven parameters in one sheet, which is the ordinary case.</summary>
    [Fact]
    public void ASheetCanMixTheTwoKinds() {
        engine.SetListener(new AudioListener());

        var sheet = new AudioParameterSheet([
            new AudioParameterDefinition {
                Name = "distance",
                Builtin = AudioBuiltinParameter.Distance,
                Maximum = 100f,
                Automation = [new(AudioParameterTarget.LowPassHz, AudioCurve.Ramp(24_000f, 400f))]
            },
            new AudioParameterDefinition {
                Name = "submersion",
                Automation = [new(AudioParameterTarget.GainDb, AudioCurve.Ramp(0f, -20f))]
            }
        ]);

        var handle = Emit(sheet, new Vector3(0f, 0f, 50f));

        Assert.True(engine.SetParameter(handle, "submersion", 1f));
        engine.Update(0f);

        Assert.Equal(50f, engine.ParameterOf(handle, 0), 0.01f);
        Assert.Equal(1f, engine.ParameterOf(handle, 1));
    }

    /// <summary>All the way to what came out: a curve drawn against distance, with nothing plumbing it.</summary>
    [Fact]
    public void ADistanceCurveActuallyMufflesTheDistantSound() {
        engine.SetListener(new AudioListener());
        var near = engine.CreateBus("Near");
        var far = engine.CreateBus("Far");

        var sheet = new AudioParameterSheet([
            new AudioParameterDefinition {
                Name = "distance",
                Builtin = AudioBuiltinParameter.Distance,
                Maximum = 100f,
                Automation = [new(AudioParameterTarget.LowPassHz, AudioCurve.Ramp(24_000f, 300f))]
            }
        ]);

        foreach (var (bus, z) in new[] { (near, 1f), (far, 100f) }) {
            var handle = engine.Play(AudioTestData.Tone(6_000f, 48_000), new PlaybackSettings {
                Bus = bus.Index,
                Gain = z > 50f ? 40f : 1f,   // the far one is boosted so this is about the filter, not the rolloff
                IsSpatial = true,
                Spatial = new SpatialSettings {
                    Position = new Vector3(0f, 0f, z),
                    MinDistance = 1f,
                    MaxDistance = 1_000f,
                    DopplerFactor = 0f
                }
            });

            engine.AttachParameters(handle, sheet);
        }

        // The first blocks are rendered before the spatialiser has told anybody how far away
        // anything is, so they are unfiltered by construction — and PeakLevel is a per-block figure,
        // so a maximum taken across them would be the maximum of the unfiltered ones.
        for (var i = 0; i < 8; i++) {
            AudioTestData.Render(device, 64);
            engine.Update(0f);
        }

        var (nearPeak, farPeak) = (0f, 0f);

        for (var i = 0; i < 16; i++) {
            AudioTestData.Render(device, 64);
            engine.Update(0f);
            nearPeak = MathF.Max(nearPeak, near.PeakLevel);
            farPeak = MathF.Max(farPeak, far.PeakLevel);
        }

        Assert.True(nearPeak > 0.3f, $"the near one should be open, was {nearPeak:F3}");
        Assert.True(farPeak < nearPeak * 0.2f, $"near {nearPeak:F3}, far {farPeak:F3}");
    }
}
