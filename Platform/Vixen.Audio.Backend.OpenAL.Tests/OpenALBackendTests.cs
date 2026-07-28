// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Backend.OpenAL.Tests;

/// <summary>
///     What can be asserted about a backend that talks to a sound card. The mixing is tested in
///     <c>Vixen.Audio.Tests</c> against a buffer; this is about whether the device opens, whether it
///     pulls, and whether it lets go — the three things a backend can get wrong that the mixer
///     cannot.
/// </summary>
/// <remarks>
///     Every test that needs real hardware skips itself when there is none, rather than failing. A CI
///     runner with no sound card is the ordinary case, and a suite that goes red on it is a suite
///     people learn to ignore.
/// </remarks>
public sealed class OpenALBackendTests(ITestOutputHelper output) {
    static AudioClip Tone(int frames = 48_000) {
        var samples = new float[frames];

        for (var i = 0; i < frames; i++) {
            samples[i] = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / 48_000f);
        }

        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        return new AudioClip {
            SampleRate = 48_000,
            Channels = 1,
            Format = AudioSampleFormat.Float32,
            Samples = bytes
        };
    }

    [Fact]
    public void TheBackendLoadsOrSaysItDidNot() {
        using var backend = new OpenALBackend();

        // No assertion about which: a machine may genuinely have no OpenAL. What must hold is that
        // constructing it is safe either way, because backend selection constructs every candidate.
        Assert.Equal("OpenAL", backend.Name);
    }

    [Fact]
    public void EnumeratingDevicesNeverThrows() {
        using var backend = new OpenALBackend();

        var devices = backend.EnumerateDevices();

        Assert.NotNull(devices);

        foreach (var device in devices) {
            Assert.False(string.IsNullOrEmpty(device.Name));
        }
    }

    /// <summary>
    ///     "No way to list them" and "none" are different states, and only one of them means silence.
    /// </summary>
    [Fact]
    public void ThereIsAlwaysAtLeastOneDeviceWhenTheLibraryLoaded() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        Assert.NotEmpty(backend.EnumerateDevices());
    }

    [Fact]
    public void ADeviceOpensAndReportsWhatItGranted() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        using var device = backend.OpenDevice(new AudioDeviceOptions { BufferFrames = 256 });

        Assert.Equal(48_000, device.Format.SampleRate);
        Assert.Equal(2, device.Format.Channels);
        Assert.Equal(256, device.BufferFrames);
        Assert.False(device.IsRunning);
    }

    /// <summary>
    ///     Wider than stereo needs AL_EXT_MCFORMATS, which is not present everywhere OpenAL is. A
    ///     mixer that asked for 5.1 and quietly got stereo would be worse than one told what it got.
    /// </summary>
    [Fact]
    public void MoreThanTwoChannelsIsClampedAndSaidSo() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        using var device = backend.OpenDevice(new AudioDeviceOptions {
            Format = new AudioFormat(48_000, 6)
        });

        Assert.Equal(2, device.Format.Channels);
    }

    [Fact]
    public void AnUnknownDeviceNameIsRefusedRatherThanIgnored() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        Assert.Throws<AudioDeviceException>(
            () => backend.OpenDevice(new AudioDeviceOptions { DeviceId = "not a sound card" })
        );
    }

    [Fact]
    public void AStartedDevicePullsFromTheMixer() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        using var engine = AudioEngine.Create(backend, new AudioEngineOptions {
            Device = new AudioDeviceOptions { BufferFrames = 256 }
        });

        Assert.True(engine.Device.IsRunning);

        var handle = engine.Play(Tone(), new PlaybackSettings { Gain = 0.2f, Pitch = 1f });
        Assert.True(handle.IsValid);

        // The pump runs on its own thread, so this waits for evidence rather than assuming a
        // duration. The ceiling is generous because it is a deadline for "did this ever happen", not
        // a measurement: one buffer at 256 frames is five milliseconds, and anything that needs ten
        // seconds to produce it did not produce it.
        var watch = Stopwatch.StartNew();

        while (engine.Statistics.RenderedFrames == 0 && watch.ElapsedMilliseconds < 10_000) {
            Thread.Sleep(10);
            engine.Update();
        }

        Assert.True(engine.Statistics.RenderedFrames > 0, "the device never asked the mixer for a block");

        // Reported, not asserted. Load is mixing time over the real time the buffer covers, so it
        // measures how much of this machine was free while the test ran — a number several test
        // processes at once, or a laptop on battery, can push over one without anything in the mixer
        // having changed. It was an assertion, and it failed for exactly that reason. What the mixer
        // costs belongs in Benchmarks/, where the run is controlled; what this test can honestly say
        // is that the device asked and the mixer answered.
        output.WriteLine($"the mixer used {engine.Statistics.Load:P0} of its budget");
    }

    [Fact]
    public void StoppingAndStartingAgainIsLegal() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        using var engine = AudioEngine.Create(backend);

        engine.Suspend();
        Assert.False(engine.Device.IsRunning);

        engine.Start();
        Assert.True(engine.Device.IsRunning);
    }

    [Fact]
    public void ADeviceCanBeDisposedWhileItIsPlaying() {
        using var backend = new OpenALBackend();
        Assert.SkipUnless(backend.IsAvailable, "OpenAL is not installed on this machine.");

        var engine = AudioEngine.Create(backend);
        engine.Play(Tone(), new PlaybackSettings { Gain = 0.2f, Pitch = 1f });
        Thread.Sleep(20);

        // The join in Stop is what makes this safe: disposing native handles while a thread is still
        // calling into them is the classic way a backend crashes on exit.
        engine.Dispose();

        Assert.False(engine.Device.IsRunning);
    }
}
