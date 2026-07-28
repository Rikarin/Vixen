// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace Vixen.Audio.Devices;

/// <summary>The backend with no sound card.</summary>
/// <remarks>
///     <para>
///         Two jobs, and they are the same job. A dedicated server, a batch tool and a CI run have
///         no audio device and still run the mixer — a sound that was started has to finish, or the
///         voice never comes back to the pool and the eight-hundredth footstep is silent on the
///         server and audible on the client. And <c>docs/plan/12</c> says audio correctness is
///         tested at buffer level: a test that asserts on what the mixer produced needs somewhere to
///         produce it that is not a speaker.
///     </para>
///     <para>
///         <b>Why this is in <c>Core</c> and <c>Vixen.Graphics.Null</c> is in <c>Platform</c>.</b>
///         That one is a backend: it implements an RHI whose other implementations talk to drivers,
///         and it lives beside them. This is the absence of a backend, and it is what
///         <c>Vixen.Audio</c>'s own tests render through — putting it in <c>Platform</c> would make
///         the assembly that owns the mixer depend on a platform assembly to test the mixer.
///     </para>
/// </remarks>
public sealed class NullAudioBackend : IAudioBackend {
    static readonly AudioDeviceInfo[] Devices = [
        new("null", "Vixen Null Audio Device", true, AudioFormat.Stereo48k)
    ];

    static readonly AudioDeviceInfo[] CaptureDevices = [
        new("null", "No microphone", true, AudioFormat.Mono48k)
    ];

    /// <summary>Whether an opened device paces itself against a real clock.</summary>
    /// <remarks>
    ///     <b>Off by default.</b> A test wants to render exactly the frames it asks for, in the
    ///     order it asks for them, with no thread involved — the whole value of this device is that
    ///     it makes audio a deterministic function. A server head that runs gameplay logic keyed off
    ///     "has this sound finished" turns it on, and pays a thread for it.
    /// </remarks>
    public bool Paced { get; init; }

    /// <inheritdoc />
    public string Name => "Null";

    /// <summary>Always. There is nothing here to be unavailable.</summary>
    public bool IsAvailable => true;

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => Devices;

    /// <inheritdoc />
    public IAudioDevice OpenDevice(in AudioDeviceOptions options) {
        var format = options.Format.IsValid ? options.Format : AudioFormat.Stereo48k;
        var frames = options.BufferFrames > 0 ? options.BufferFrames : 480;

        return new NullAudioDevice(Devices[0], format, frames, Paced);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    ///     True, and the microphone hears whatever <c>NullAudioCaptureDevice.Push</c> is given. A
    ///     server has no input and still has to run the code that reads one; a test has no input and
    ///     still has to assert about what a reader got.
    /// </remarks>
    public bool SupportsCapture => true;

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> EnumerateCaptureDevices() => CaptureDevices;

    /// <inheritdoc />
    public IAudioCaptureDevice OpenCaptureDevice(in AudioCaptureOptions options) => new NullAudioCaptureDevice(options);

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>A device that renders and throws the result away — or hands it to a test.</summary>
public sealed class NullAudioDevice : IAudioDevice {
    readonly bool paced;
    readonly Lock gate = new();

    float[] scratch = [];
    IAudioRenderSource? source;
    Thread? clock;
    volatile bool running;

    internal NullAudioDevice(AudioDeviceInfo info, AudioFormat format, int bufferFrames, bool paced) {
        Info = info;
        Format = format;
        BufferFrames = bufferFrames;
        this.paced = paced;
    }

    /// <summary>How many frames this device has rendered since it was opened.</summary>
    /// <remarks>
    ///     The clock a test asserts against: "after 48 000 frames the one-second clip has finished"
    ///     is a statement about this counter and not about how long the test took to run.
    /// </remarks>
    public long RenderedFrames { get; private set; }

    /// <inheritdoc />
    public AudioDeviceInfo Info { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public int BufferFrames { get; }

    /// <inheritdoc />
    public bool IsRunning => running;

    /// <summary>Zero, always. Nothing is waiting on this device, so nothing can be late for it.</summary>
    public long Underruns => 0;

    /// <inheritdoc />
    public void Start(IAudioRenderSource source) {
        lock (gate) {
            if (running) {
                throw new InvalidOperationException("The device is already running.");
            }

            this.source = source;
            source.Prepare(Format, BufferFrames);
            scratch = new float[BufferFrames * Format.Channels];
            running = true;

            if (paced) {
                clock = new Thread(PaceLoop) { IsBackground = true, Name = "Vixen Null Audio" };
                clock.Start();
            }
        }
    }

    /// <inheritdoc />
    public void Stop() {
        Thread? joining;

        lock (gate) {
            running = false;
            joining = clock;
            clock = null;
        }

        joining?.Join();
    }

    /// <summary>Renders the next block into a caller's buffer.</summary>
    /// <param name="destination">
    ///     Interleaved, a multiple of the channel count long. Every frame it can hold is rendered.
    /// </param>
    /// <returns>How many frames were written.</returns>
    /// <remarks>
    ///     The manual pump. A test calls this; a paced device's own thread calls it; nothing else
    ///     needs to exist for audio to be observable.
    /// </remarks>
    public int Render(Span<float> destination) {
        var current = source;

        if (current is null || !running) {
            destination.Clear();
            return 0;
        }

        var frames = destination.Length / Format.Channels;

        if (frames <= 0) {
            return 0;
        }

        var written = 0;

        while (written < frames) {
            var block = Math.Min(BufferFrames, frames - written);
            var slice = destination.Slice(written * Format.Channels, block * Format.Channels);
            current.Render(slice, block);
            written += block;
        }

        RenderedFrames += written;
        return written;
    }

    /// <summary>Renders a number of frames and discards them.</summary>
    /// <param name="frames">How many.</param>
    /// <remarks>How a server advances the mixer without wanting the samples.</remarks>
    public void Advance(int frames) {
        for (var done = 0; done < frames;) {
            var block = Math.Min(BufferFrames, frames - done);
            Render(scratch.AsSpan(0, block * Format.Channels));
            done += block;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    void PaceLoop() {
        var watch = Stopwatch.StartNew();
        var rendered = 0L;

        while (running) {
            var due = (long)(watch.Elapsed.TotalSeconds * Format.SampleRate);

            if (due - rendered < BufferFrames) {
                // A millisecond is a fifth of a 480-frame block at 48 kHz, so this wakes several
                // times per block and never oversleeps one.
                Thread.Sleep(1);
                continue;
            }

            Render(scratch);
            rendered += BufferFrames;
        }
    }
}
