// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vixen.Audio.Devices;

namespace Vixen.Audio.Backend.WebAudio;

/// <summary>An <c>AudioContext</c> with a queue of scheduled blocks in front of it.</summary>
/// <remarks>
///     <para>
///         <b>Everything here runs on the browser's one thread.</b> There is no audio thread to keep
///         off, no lock to avoid and no memory barrier to place: the JavaScript timer, the render and
///         the game loop are the same thread. That makes this the simplest of the backends and the
///         one with the least room to be subtly wrong — and it is why <c>AudioEngine</c>'s lock-free
///         machinery, which exists for the desktop, costs nothing here.
///     </para>
///     <para>
///         <b>It also means the render has a hard deadline it shares with the frame.</b> A frame that
///         takes 40 ms is 40 ms in which the timer does not fire, and the queue is what covers it —
///         which is why the block count matters more on the web than anywhere else.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class WebAudioDevice : IAudioDevice {
    readonly int handle;
    readonly float[] block;
    readonly Action<int> pump;

    IAudioRenderSource? source;
    bool started;
    bool disposed;

    internal WebAudioDevice(int handle, AudioDeviceInfo info, AudioFormat format, int bufferFrames) {
        this.handle = handle;
        Info = info;
        Format = format;
        BufferFrames = bufferFrames;
        block = new float[bufferFrames * format.Channels];

        // Held in a field rather than created per call: the delegate is marshalled into a JavaScript
        // function once, and a new one every tick would leak an interop registration a tick.
        pump = Pump;
    }

    /// <inheritdoc />
    public AudioDeviceInfo Info { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public int BufferFrames { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     False until the user has interacted with the page, however many times <see cref="Start" />
    ///     has been called. That is the browser's autoplay policy and not a bug here.
    /// </remarks>
    public bool IsRunning => !disposed && started && WebAudioInterop.IsRunning(handle);

    /// <inheritdoc />
    public long Underruns => disposed ? 0 : WebAudioInterop.Underruns(handle);

    /// <inheritdoc />
    public void Start(IAudioRenderSource source) {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (started) {
            throw new InvalidOperationException("The device is already running.");
        }

        this.source = source;
        source.Prepare(Format, BufferFrames);
        started = true;
        WebAudioInterop.Start(handle, pump);
    }

    /// <inheritdoc />
    public void Stop() {
        if (disposed || !started) {
            return;
        }

        started = false;
        WebAudioInterop.Stop(handle);
    }

    /// <summary>Asks the browser to let the page make a sound.</summary>
    /// <remarks>
    ///     <b>Call this from a click, a key press or a touch.</b> Every browser suspends an
    ///     <c>AudioContext</c> created without a user gesture, and calling <c>resume</c> from anywhere
    ///     else is ignored. An application that wants sound at the title screen puts this in the
    ///     handler for the button that leaves it.
    /// </remarks>
    public void Resume() {
        if (!disposed) {
            WebAudioInterop.Resume(handle);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        Stop();
        disposed = true;
        WebAudioInterop.Close(handle);
    }

    void Pump(int blocks) {
        var renderSource = source;

        if (renderSource is null || !started) {
            return;
        }

        for (var i = 0; i < blocks; i++) {
            renderSource.Render(block, BufferFrames);
            WebAudioInterop.Enqueue(handle, MemoryMarshal.AsBytes(block.AsSpan()), BufferFrames);
        }
    }
}
