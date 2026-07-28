// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;

namespace Vixen.Audio.Tests;

/// <summary>Clips and engines with known contents, so an assertion can be about arithmetic.</summary>
static class AudioTestData {
    /// <summary>How far apart two frames of a <see cref="Ramp" /> are.</summary>
    /// <remarks>
    ///     Small enough that a ramp thousands of frames long stays inside ±1 and is not touched by
    ///     the master's clamp — which is a real behaviour and not one a resampling test wants to be
    ///     testing at the same time.
    /// </remarks>
    public const float RampStep = 1f / 1024f;

    /// <summary>A clip whose frame <c>n</c> holds <c>n × <see cref="RampStep" /></c> in every channel.</summary>
    /// <remarks>
    ///     A ramp rather than a sine, because a ramp makes an off-by-one visible: a resampler that
    ///     started half a frame early produces the midpoint, where a sine would produce something
    ///     that still looks like a sine.
    /// </remarks>
    public static AudioClip Ramp(int frames, int sampleRate = 48_000, int channels = 1) {
        var samples = new float[frames * channels];

        for (var frame = 0; frame < frames; frame++) {
            for (var channel = 0; channel < channels; channel++) {
                samples[(frame * channels) + channel] = frame * RampStep;
            }
        }

        return FromFloats(samples, sampleRate, channels);
    }

    /// <summary>A clip that is the same value all the way through.</summary>
    public static AudioClip Constant(int frames, float value, int sampleRate = 48_000, int channels = 1) {
        var samples = new float[frames * channels];
        Array.Fill(samples, value);
        return FromFloats(samples, sampleRate, channels);
    }

    /// <summary>A sine at a frequency, for anything that is about what a filter did.</summary>
    public static AudioClip Tone(float frequency, int frames, float amplitude = 1f, int sampleRate = 48_000) {
        var samples = new float[frames];

        for (var i = 0; i < frames; i++) {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * frequency * i / sampleRate);
        }

        return FromFloats(samples, sampleRate, 1);
    }

    /// <summary>A clip that is one full-scale sample and then nothing.</summary>
    public static AudioClip Impulse(int frames, int sampleRate = 48_000, int channels = 1) {
        var samples = new float[frames * channels];

        for (var channel = 0; channel < channels; channel++) {
            samples[channel] = 1f;
        }

        return FromFloats(samples, sampleRate, channels);
    }

    /// <summary>Wraps interleaved floats as a clip without a copy anybody can see.</summary>
    public static AudioClip FromFloats(float[] samples, int sampleRate, int channels) {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        return new AudioClip {
            SampleRate = sampleRate,
            Channels = channels,
            Format = AudioSampleFormat.Float32,
            Samples = bytes
        };
    }

    /// <summary>An engine on a device nobody hears, which a test renders by hand.</summary>
    /// <remarks>
    ///     <b>The master limiter is off unless a test asks for it.</b> It is on by default in a real
    ///     engine, and it delays the signal by its look-ahead and pulls the gain down whenever the mix
    ///     is loud — both correct, and both noise in a test whose subject is what a pan law or a
    ///     resampler produced. A test about the limiter turns it back on.
    /// </remarks>
    public static (AudioEngine Engine, NullAudioDevice Device) Engine(
        int channels = 2,
        int sampleRate = 48_000,
        int bufferFrames = 64,
        int voices = 8,
        bool limiter = false
    ) {
        var backend = new NullAudioBackend();

        var device = (NullAudioDevice)backend.OpenDevice(new AudioDeviceOptions {
            Format = new AudioFormat(sampleRate, channels),
            BufferFrames = bufferFrames
        });

        var engine = new AudioEngine(device, new AudioEngineOptions {
            VoiceCapacity = voices,
            StreamOnOwnThread = false,
            MasterLimiter = limiter
        });

        return (engine, device);
    }

    /// <summary>Renders a number of frames and hands back the interleaved result.</summary>
    public static float[] Render(NullAudioDevice device, int frames) {
        var buffer = new float[frames * device.Format.Channels];
        device.Render(buffer);
        return buffer;
    }

    /// <summary>The loudest absolute value in a buffer.</summary>
    public static float Peak(ReadOnlySpan<float> buffer) {
        var loudest = 0f;

        foreach (var value in buffer) {
            loudest = MathF.Max(loudest, MathF.Abs(value));
        }

        return loudest;
    }
}
