// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using Vixen.Audio.Devices;

// Silk.NET has an AudioDeviceException of its own, thrown by its convenience wrappers. This file
// throws ours, which is the one an IAudioBackend contract says callers may catch.
using AudioDeviceException = Vixen.Audio.Devices.AudioDeviceException;

namespace Vixen.Audio.Backend.OpenAL;

/// <summary>One source, a ring of buffers, and a thread that keeps it fed.</summary>
/// <remarks>
///     <para>
///         <b>Why a thread and not a callback.</b> OpenAL has no callback: it is a pull API driven by
///         asking how many of the buffers queued on a source have been played and refilling those.
///         Somebody has to do the asking, and it has to be somebody who is not the game thread — a
///         frame that took 40 ms would otherwise be 40 ms of silence.
///     </para>
///     <para>
///         <b>Why the buffers are small and several.</b> Four buffers of 480 frames is 40 ms of audio
///         queued ahead at 48 kHz. The queue is the whole safety margin: the thread has four blocks'
///         worth of time to be scheduled in before the source runs dry, and running dry is the one
///         failure a listener notices immediately. Making the blocks larger trades latency for
///         margin, and making them fewer trades margin for nothing.
///     </para>
///     <para>
///         <b>Float where the driver takes it.</b> <c>AL_EXT_FLOAT32</c> is present on every OpenAL
///         Soft build; where it is, the mixer's floats go straight across. Where it is not, they are
///         converted to signed 16-bit here — the mixer has already clamped to ±1, so the conversion
///         is exact and one multiply.
///     </para>
/// </remarks>
sealed unsafe class OpenALDevice : IAudioDevice {
    readonly ALContext alc;
    readonly AL al;
    readonly Device* handle;
    readonly Context* context;
    readonly ILogger logger;
    readonly FloatFormat? floatFormat;
    readonly uint[] buffers;
    readonly uint source;
    readonly float[] mix;
    readonly short[] quantised;
    readonly Lock gate = new();

    IAudioRenderSource? renderSource;
    Thread? thread;
    long underruns;
    volatile bool running;
    bool disposed;

    internal OpenALDevice(
        ALContext alc,
        AL al,
        Device* handle,
        Context* context,
        AudioDeviceInfo info,
        AudioFormat format,
        in AudioDeviceOptions options,
        ILogger logger
    ) {
        this.alc = alc;
        this.al = al;
        this.handle = handle;
        this.context = context;
        this.logger = logger;

        Info = info;
        Format = format;
        BufferFrames = options.BufferFrames > 0 ? options.BufferFrames : 480;

        alc.MakeContextCurrent(context);

        // Everything OpenAL would do to the signal is turned off. The mixer has already placed the
        // sound, attenuated it and summed the buses; a distance model applied on top of that would
        // attenuate it a second time.
        al.DistanceModel(DistanceModel.None);
        al.SetSourceProperty(source = al.GenSource(), SourceBoolean.SourceRelative, true);
        al.SetSourceProperty(source, SourceVector3.Position, 0f, 0f, 0f);
        al.SetSourceProperty(source, SourceFloat.Gain, 1f);

        buffers = al.GenBuffers(Math.Max(2, options.BufferCount));
        floatFormat = al.TryGetExtension<FloatFormat>(out var extension) ? extension : null;

        mix = new float[BufferFrames * format.Channels];
        quantised = floatFormat is null ? new short[mix.Length] : [];

        Check("opening the device");
    }

    /// <inheritdoc />
    public AudioDeviceInfo Info { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public int BufferFrames { get; }

    /// <inheritdoc />
    public bool IsRunning => running;

    /// <inheritdoc />
    public long Underruns => Interlocked.Read(ref underruns);

    /// <summary>Whether the mixer's floats are handed over untouched.</summary>
    public bool IsFloatOutput => floatFormat is not null;

    /// <inheritdoc />
    public void Start(IAudioRenderSource source) {
        ArgumentNullException.ThrowIfNull(source);

        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (running) {
                throw new InvalidOperationException("The device is already running.");
            }

            renderSource = source;
            source.Prepare(Format, BufferFrames);
            running = true;

            thread = new Thread(Pump) {
                IsBackground = true,
                Name = "Vixen OpenAL",

                // Above normal, not highest. The mixer must beat the game thread to the CPU or it
                // will drop out under load; taking priority over the operating system's own work is
                // how an audio thread makes a machine unresponsive rather than making it sound good.
                Priority = ThreadPriority.AboveNormal
            };

            thread.Start();
        }
    }

    /// <inheritdoc />
    public void Stop() {
        Thread? joining;

        lock (gate) {
            if (!running) {
                return;
            }

            running = false;
            joining = thread;
            thread = null;
        }

        joining?.Join();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        Stop();
        disposed = true;

        alc.MakeContextCurrent(context);
        al.SourceStop(source);
        al.SetSourceProperty(source, SourceInteger.Buffer, 0);
        al.DeleteSource(source);
        al.DeleteBuffers(buffers);
        alc.MakeContextCurrent(null);
        alc.DestroyContext(context);
        alc.CloseDevice(handle);
    }

    void Pump() {
        // The context is per-thread current, and this is the only thread that ever touches AL after
        // construction.
        alc.MakeContextCurrent(context);

        try {
            foreach (var buffer in buffers) {
                Fill(buffer);
            }

            fixed (uint* queued = buffers) {
                al.SourceQueueBuffers(source, buffers.Length, queued);
            }

            al.SourcePlay(source);

            while (running) {
                al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out var processed);

                if (processed == 0) {
                    // A block is milliseconds long and this wakes several times inside one, so the
                    // queue is never allowed to drain while the thread sleeps through its refill.
                    Thread.Sleep(1);
                    continue;
                }

                while (processed-- > 0) {
                    uint buffer;
                    al.SourceUnqueueBuffers(source, 1, &buffer);
                    Fill(buffer);
                    al.SourceQueueBuffers(source, 1, &buffer);
                }

                al.GetSourceProperty(source, GetSourceInteger.SourceState, out var state);

                if ((SourceState)state is not SourceState.Playing) {
                    // The source ran out of queued buffers before the refill got there. Every one of
                    // these is an audible gap; the count is what the diagnostics overlay reads.
                    Interlocked.Increment(ref underruns);
                    al.SourcePlay(source);
                }
            }

            al.SourceStop(source);
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            // The same rule as the mixer's: an exception here would take down a background thread
            // and, with it, all sound, silently. Stop, record, and let Update report it.
            running = false;
            OpenALLog.PumpFailed(logger, exception);
        }
    }

    void Fill(uint buffer) {
        var frames = BufferFrames;
        renderSource?.Render(mix, frames);

        if (floatFormat is not null) {
            fixed (float* data = mix) {
                floatFormat.BufferData(
                    buffer,
                    Format.Channels == 1 ? FloatBufferFormat.Mono : FloatBufferFormat.Stereo,
                    data,
                    mix.Length * sizeof(float),
                    Format.SampleRate
                );
            }

            return;
        }

        // The mixer clamps at the master, so this cannot wrap. 32 767 rather than 32 768 so that a
        // sample of exactly 1 lands on the rail instead of one past it.
        for (var i = 0; i < mix.Length; i++) {
            quantised[i] = (short)(mix[i] * 32_767f);
        }

        fixed (short* data = quantised) {
            al.BufferData(
                buffer,
                Format.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16,
                data,
                quantised.Length * sizeof(short),
                Format.SampleRate
            );
        }
    }

    void Check(string what) {
        var error = al.GetError();

        if (error is not AudioError.NoError) {
            throw new AudioDeviceException($"OpenAL reported {error} while {what}.");
        }
    }
}
