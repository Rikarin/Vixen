// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Devices;

/// <summary>One output a backend could play through.</summary>
/// <param name="Id">What to pass back as <see cref="AudioDeviceOptions.DeviceId" /> to choose it.</param>
/// <param name="Name">What to show a human.</param>
/// <param name="IsDefault">Whether the operating system considers this the one to use.</param>
/// <param name="PreferredFormat">
///     The format it would rather be given. Opening it with something else is legal and costs a
///     conversion somewhere below.
/// </param>
public readonly record struct AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    AudioFormat PreferredFormat
);

/// <summary>What to open a device with.</summary>
/// <remarks>
///     Every field is a request. <see cref="IAudioDevice.Format" /> and
///     <see cref="IAudioDevice.BufferFrames" /> are what was granted, and a caller that cares must
///     read them back rather than assume — a browser, in particular, decides the sample rate itself
///     and will not be argued with.
/// </remarks>
public readonly record struct AudioDeviceOptions() {
    /// <summary>Which device, or <see langword="null" /> for whichever one is default.</summary>
    public string? DeviceId { get; init; }

    /// <summary>The format to render in.</summary>
    public AudioFormat Format { get; init; } = AudioFormat.Stereo48k;

    /// <summary>How many frames the device asks for at a time.</summary>
    /// <remarks>
    ///     <para>
    ///         480 frames is 10 ms at 48 kHz. That is the number this defaults to because it is the
    ///         one that is defensible in both directions: small enough that a gunshot triggered on
    ///         the frame the trigger was pulled is not audibly late, and large enough that the mixer
    ///         gets a hundred wake-ups a second rather than a thousand.
    ///     </para>
    ///     <para>
    ///         Latency is this times <see cref="BufferCount" />, and the buffer count is what
    ///         actually protects against a scheduling hiccup. Lowering this without raising that
    ///         buys latency with dropouts.
    ///     </para>
    /// </remarks>
    public int BufferFrames { get; init; } = 480;

    /// <summary>How many buffers are queued ahead of the device.</summary>
    /// <remarks>
    ///     Four, so a render that misses its slot has three more to catch up in. Two is the point at
    ///     which any hitch anywhere in the process is audible.
    /// </remarks>
    public int BufferCount { get; init; } = 4;
}

/// <summary>Where a device gets the samples it is about to play.</summary>
/// <remarks>
///     <para>
///         <b>Pull, not push.</b> The device asks when it needs frames, on whatever thread it does
///         its work on, and the source produces exactly that many. A push model would need a queue
///         between the game and the device with a policy for what to do when it filled, and every
///         audio API in existence is already a pull model underneath — a push API would be a queue
///         wrapped around a queue.
///     </para>
///     <para>
///         <b><see cref="Render" /> runs on the audio thread and must not block.</b> No locks a game
///         thread can hold, no allocation, no I/O, no logging. That constraint is what shapes
///         <c>AudioEngine</c>: control arrives as commands in a lock-free queue that
///         <see cref="Render" /> drains, and results leave as counters it publishes.
///     </para>
/// </remarks>
public interface IAudioRenderSource {
    /// <summary>Told what the device settled on, before the first <see cref="Render" />.</summary>
    /// <param name="format">The device's format.</param>
    /// <param name="maxFrames">The most frames one <see cref="Render" /> will ever ask for.</param>
    /// <remarks>
    ///     Every buffer the mixer needs is allocated here, which is what lets <see cref="Render" />
    ///     allocate nothing.
    /// </remarks>
    void Prepare(in AudioFormat format, int maxFrames);

    /// <summary>Fills a buffer with the next frames.</summary>
    /// <param name="destination">
    ///     Interleaved, <c>frameCount × channels</c> floats long. Its previous contents mean
    ///     nothing; the source overwrites all of it, including with silence.
    /// </param>
    /// <param name="frameCount">How many frames to produce. Never more than <c>maxFrames</c>.</param>
    void Render(Span<float> destination, int frameCount);
}

/// <summary>An open output.</summary>
public interface IAudioDevice : IDisposable {
    /// <summary>Which device this is.</summary>
    AudioDeviceInfo Info { get; }

    /// <summary>The format it was actually opened in.</summary>
    AudioFormat Format { get; }

    /// <summary>How many frames it asks for at a time.</summary>
    int BufferFrames { get; }

    /// <summary>Whether it is currently pulling.</summary>
    bool IsRunning { get; }

    /// <summary>How many times it wanted frames and did not get them in time.</summary>
    /// <remarks>
    ///     The one number that says whether the audio budget is being met. It is written from the
    ///     audio thread and read from anywhere, so it is a <see cref="long" /> updated with
    ///     <see cref="System.Threading.Interlocked" /> rather than a property with a setter.
    /// </remarks>
    long Underruns { get; }

    /// <summary>Starts pulling from a source.</summary>
    /// <param name="source">Where the frames come from.</param>
    /// <exception cref="InvalidOperationException">It is already running.</exception>
    void Start(IAudioRenderSource source);

    /// <summary>Stops pulling. Starting again is legal.</summary>
    void Stop();
}

/// <summary>A way of getting at the machine's audio outputs.</summary>
public interface IAudioBackend : IDisposable {
    /// <summary>What to call it in a log — <c>OpenAL</c>, <c>WebAudio</c>, <c>Null</c>.</summary>
    string Name { get; }

    /// <summary>Whether this process could actually open a device through it.</summary>
    /// <remarks>
    ///     False rather than throwing, because "no audio" is an ordinary state — a CI runner, a
    ///     dedicated server, a container, a machine whose only sound card is in use by something
    ///     exclusive. Backend selection asks this and moves on to the next candidate.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>Every output it can see, default first if it knows which one that is.</summary>
    /// <returns>The devices. Empty if there are none, rather than <see langword="null" />.</returns>
    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();

    /// <summary>Opens one.</summary>
    /// <param name="options">What to ask for.</param>
    /// <returns>The device, stopped.</returns>
    /// <exception cref="AudioDeviceException">It could not be opened.</exception>
    IAudioDevice OpenDevice(in AudioDeviceOptions options);
}

/// <summary>A device would not open, or stopped being one.</summary>
public sealed class AudioDeviceException : Exception {
    /// <summary>A new exception.</summary>
    public AudioDeviceException() { }

    /// <summary>A new exception.</summary>
    /// <param name="message">What went wrong.</param>
    public AudioDeviceException(string message) : base(message) { }

    /// <summary>A new exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What the backend said.</param>
    public AudioDeviceException(string message, Exception innerException) : base(message, innerException) { }
}
