// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

public sealed class SpatializerTests {
    static readonly AudioListener AtOrigin = AudioListener.Default;

    static float[] Gains(in SpatialSettings source, int channels = 2, AudioListener? listener = null) {
        var gains = new float[AudioFormat.MaxChannels];
        Spatializer.Evaluate(listener ?? AtOrigin, source, channels, gains);
        return gains;
    }

    /// <summary>
    ///     Right-handed, Y-up, −Z forward — Conventions.md, which this file has to agree with and
    ///     which is why a sign flip here is a settled argument rather than an open one.
    /// </summary>
    [Fact]
    public void ASourceOnThePositiveXAxisIsToTheListenersRight() {
        var gains = Gains(new SpatialSettings {
            Position = new Vector3(10f, 0f, 0f),
            Attenuation = AttenuationModel.None
        });

        Assert.True(gains[1] > gains[0]);
        Assert.Equal(0f, gains[0], 0.001f);
        Assert.Equal(1f, gains[1], 0.001f);
    }

    [Fact]
    public void ASourceStraightAheadIsCentred() {
        var gains = Gains(new SpatialSettings {
            Position = new Vector3(0f, 0f, -10f),
            Attenuation = AttenuationModel.None
        });

        Assert.Equal(0.7071f, gains[0], 0.001f);
        Assert.Equal(0.7071f, gains[1], 0.001f);
    }

    /// <summary>
    ///     Panning has a left and a right and no front and back. Something behind you sounds like
    ///     something in front of you, which is what amplitude panning is and what an HRTF panner
    ///     would fix.
    /// </summary>
    [Fact]
    public void ASourceBehindTheListenerIsAlsoCentred() {
        var gains = Gains(new SpatialSettings {
            Position = new Vector3(0f, 0f, 10f),
            Attenuation = AttenuationModel.None
        });

        Assert.Equal(0.7071f, gains[0], 0.001f);
        Assert.Equal(0.7071f, gains[1], 0.001f);
    }

    [Fact]
    public void ASourceOverheadIsCentredBecauseItHasNoSideways() {
        var gains = Gains(new SpatialSettings {
            Position = new Vector3(0f, 10f, 0f),
            Attenuation = AttenuationModel.None
        });

        Assert.Equal(0.7071f, gains[0], 0.001f);
        Assert.Equal(0.7071f, gains[1], 0.001f);
    }

    [Fact]
    public void TurningTheListenerMovesTheSoundTheOtherWay() {
        // Facing +X: a source on +X is now straight ahead rather than to the right.
        var facingRight = AudioListener.Default with { Forward = Vector3.Right };

        var gains = Gains(
            new SpatialSettings { Position = new Vector3(10f, 0f, 0f), Attenuation = AttenuationModel.None },
            listener: facingRight
        );

        Assert.Equal(0.7071f, gains[0], 0.001f);
        Assert.Equal(0.7071f, gains[1], 0.001f);
    }

    [Fact]
    public void InverseAttenuationHalvesEveryTimeTheDistanceDoubles() {
        var settings = new SpatialSettings { MinDistance = 1f, MaxDistance = 1_000f };

        var near = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(1f, 0f, 0f) }, 2, new float[2]);
        var far = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(2f, 0f, 0f) }, 2, new float[2]);
        var further = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(4f, 0f, 0f) }, 2, new float[2]);

        Assert.Equal(1f, near.Attenuation, 0.001f);
        Assert.Equal(0.5f, far.Attenuation, 0.001f);
        Assert.Equal(0.25f, further.Attenuation, 0.001f);
    }

    /// <summary>
    ///     The only model that reaches zero, which is why a designer reaches for it: it guarantees a
    ///     sound stops being audible at a distance they can point at on a map.
    /// </summary>
    [Fact]
    public void LinearAttenuationReachesSilenceAtTheMaximumDistance() {
        var settings = new SpatialSettings {
            MinDistance = 1f,
            MaxDistance = 11f,
            Attenuation = AttenuationModel.Linear
        };

        var half = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(6f, 0f, 0f) }, 2, new float[2]);
        var edge = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(11f, 0f, 0f) }, 2, new float[2]);
        var past = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(50f, 0f, 0f) }, 2, new float[2]);

        Assert.Equal(0.5f, half.Attenuation, 0.001f);
        Assert.Equal(0f, edge.Attenuation, 0.001f);
        Assert.Equal(0f, past.Attenuation, 0.001f);
    }

    [Fact]
    public void NothingGetsQuieterInsideTheReferenceDistance() {
        var settings = new SpatialSettings { MinDistance = 5f };

        var inside = Spatializer.Evaluate(AtOrigin, settings with { Position = new Vector3(1f, 0f, 0f) }, 2, new float[2]);

        Assert.Equal(1f, inside.Attenuation, 0.001f);
    }

    /// <summary>
    ///     Inside the reference distance the listener is effectively inside the sound, so the pan
    ///     dissolves rather than swinging through 180° as they walk past it.
    /// </summary>
    [Fact]
    public void ASourceTheListenerIsStandingInsideStopsBeingLocalised() {
        var gains = Gains(new SpatialSettings {
            Position = new Vector3(0.01f, 0f, 0f),
            MinDistance = 5f,
            Attenuation = AttenuationModel.None
        });

        Assert.Equal(0.7071f, gains[0], 0.01f);
        Assert.Equal(0.7071f, gains[1], 0.01f);
    }

    [Fact]
    public void AConeIsFullVolumeInsideItAndTheOuterGainOutside() {
        // Pointing along −Z, with the listener at the origin and the source in front of it, so the
        // listener is directly behind the cone.
        var settings = new SpatialSettings {
            Position = new Vector3(0f, 0f, -10f),
            ConeDirection = Vector3.Forward,
            ConeInnerAngle = 60f,
            ConeOuterAngle = 120f,
            ConeOuterGain = 0.1f,
            Attenuation = AttenuationModel.None
        };

        var behind = Spatializer.Evaluate(AtOrigin, settings, 2, new float[2]);
        var facing = Spatializer.Evaluate(AtOrigin, settings with { ConeDirection = Vector3.Backward }, 2, new float[2]);

        Assert.Equal(0.1f, behind.ConeGain, 0.001f);
        Assert.Equal(1f, facing.ConeGain, 0.001f);
    }

    /// <summary>
    ///     The authored angles are the full width of the cone, and comparing against half of each is
    ///     what OpenAL, FMOD and Wwise all do. Getting it wrong makes every cone in a project twice
    ///     as wide as it was drawn.
    /// </summary>
    [Fact]
    public void TheConeAnglesAreFullWidthsAndNotHalfAngles() {
        var settings = new SpatialSettings {
            // 45° off the cone axis: inside a 90° cone, outside a 60° one.
            Position = new Vector3(0f, 0f, 0f),
            ConeDirection = new Vector3(1f, 1f, 0f),
            ConeOuterGain = 0f,
            Attenuation = AttenuationModel.None
        };

        var listener = AudioListener.Default with { Position = new Vector3(10f, 0f, 0f) };

        var wide = Spatializer.Evaluate(
            listener,
            settings with { ConeInnerAngle = 90f, ConeOuterAngle = 90f },
            2,
            new float[2]
        );

        var narrow = Spatializer.Evaluate(
            listener,
            settings with { ConeInnerAngle = 60f, ConeOuterAngle = 60f },
            2,
            new float[2]
        );

        Assert.Equal(1f, wide.ConeGain, 0.001f);
        Assert.Equal(0f, narrow.ConeGain, 0.001f);
    }

    [Fact]
    public void ASourceApproachingTheListenerIsPitchedUp() {
        var settings = new SpatialSettings {
            Position = new Vector3(0f, 0f, -100f),
            Velocity = new Vector3(0f, 0f, 34.3f)
        };

        var result = Spatializer.Evaluate(AtOrigin, settings, 2, new float[2]);

        // 10 % of the speed of sound closing: 343 / (343 − 34.3) ≈ 1.111.
        Assert.Equal(1.111f, result.DopplerRatio, 0.001f);
    }

    [Fact]
    public void ASourceRecedingIsPitchedDown() {
        var settings = new SpatialSettings {
            Position = new Vector3(0f, 0f, -100f),
            Velocity = new Vector3(0f, 0f, -34.3f)
        };

        var result = Spatializer.Evaluate(AtOrigin, settings, 2, new float[2]);

        Assert.Equal(343f / 377.3f, result.DopplerRatio, 0.001f);
    }

    [Fact]
    public void AListenerChasingASourceHearsItPitchedUp() {
        var listener = AudioListener.Default with { Velocity = new Vector3(0f, 0f, -34.3f) };
        var settings = new SpatialSettings { Position = new Vector3(0f, 0f, -100f) };

        var result = Spatializer.Evaluate(listener, settings, 2, new float[2]);

        Assert.True(result.DopplerRatio > 1f);
    }

    /// <summary>
    ///     What a supersonic source should sound like is not a question a game mixer has to answer,
    ///     and the unclamped formula divides by zero at exactly that point.
    /// </summary>
    [Fact]
    public void ASourceMovingFasterThanSoundDoesNotProduceInfinity() {
        var settings = new SpatialSettings {
            Position = new Vector3(0f, 0f, -100f),
            Velocity = new Vector3(0f, 0f, 5_000f)
        };

        var result = Spatializer.Evaluate(AtOrigin, settings, 2, new float[2]);

        Assert.True(float.IsFinite(result.DopplerRatio));
        Assert.True(result.DopplerRatio > 1f);
    }

    [Fact]
    public void DopplerCanBeTurnedOff() {
        var settings = new SpatialSettings {
            Position = new Vector3(0f, 0f, -100f),
            Velocity = new Vector3(0f, 0f, 200f),
            DopplerFactor = 0f
        };

        var result = Spatializer.Evaluate(AtOrigin, settings, 2, new float[2]);

        Assert.Equal(1f, result.DopplerRatio, 0.0001f);
    }

    [Fact]
    public void AMonoDeviceGetsOneGainAndNoPanning() {
        var gains = Gains(
            new SpatialSettings { Position = new Vector3(10f, 0f, 0f), Attenuation = AttenuationModel.None },
            channels: 1
        );

        Assert.Equal(1f, gains[0], 0.001f);
    }

    [Fact]
    public void SpreadDissolvesTheDirection() {
        var point = Gains(new SpatialSettings {
            Position = new Vector3(10f, 0f, 0f),
            Attenuation = AttenuationModel.None
        });

        var spread = Gains(new SpatialSettings {
            Position = new Vector3(10f, 0f, 0f),
            Attenuation = AttenuationModel.None,
            Spread = 1f
        });

        Assert.Equal(0f, point[0], 0.001f);
        Assert.Equal(0.7071f, spread[0], 0.001f);
        Assert.Equal(0.7071f, spread[1], 0.001f);
    }

    [Fact]
    public void TheListenerGainScalesEveryPositionedVoice() {
        var quiet = AudioListener.Default with { Gain = 0.25f };

        var gains = Gains(
            new SpatialSettings { Position = new Vector3(0f, 0f, -10f), Attenuation = AttenuationModel.None },
            listener: quiet
        );

        Assert.Equal(0.7071f * 0.25f, gains[0], 0.001f);
    }

    [Fact]
    public void ADistantSpatialVoiceIsQuieterThanANearOneAllTheWayToTheBuffer() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        engine.SetListener(AudioListener.Default);

        engine.Play(AudioTestData.Constant(4_800, 1f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            IsSpatial = true,
            Spatial = new SpatialSettings { Position = new Vector3(0f, 0f, -1f) }
        });

        var near = AudioTestData.Peak(AudioTestData.Render(device, 32));

        engine.StopAll();
        AudioTestData.Render(device, 64);
        engine.Update();

        engine.Play(AudioTestData.Constant(4_800, 1f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            IsSpatial = true,
            Spatial = new SpatialSettings { Position = new Vector3(0f, 0f, -16f) }
        });

        var far = AudioTestData.Peak(AudioTestData.Render(device, 32));

        Assert.True(near > far);
        Assert.Equal(near / 16f, far, 0.01f);
    }
}
