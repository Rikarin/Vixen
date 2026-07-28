// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;
using Vixen.Audio.Devices;

namespace Vixen.Audio.Backend.WebAudio;

/// <summary>The audio backend for a browser tab.</summary>
/// <remarks>
///     <para>
///         Like the OpenAL backend, and for the same reason, this uses none of the platform's audio
///         graph. No <c>PannerNode</c>, no <c>ConvolverNode</c>, no gain automation. Vixen has already
///         mixed, spatialised and reverberated in software (see
///         <c>Core/Vixen.Audio/Mixing/AudioMixer.cs</c>), and what a browser gets is the same finished
///         interleaved signal a sound card gets. That is what makes a game sound the same on the web
///         as it does on a desktop.
///     </para>
///     <para>
///         <b>Loading is asynchronous and opening is not.</b> The JavaScript module has to be fetched
///         before any call can be made, and <see cref="IAudioBackend" /> is a synchronous contract —
///         so the fetch happens in <see cref="CreateAsync" /> and everything after it is ordinary.
///         An application head awaits this once during start-up, beside the rest of its asset
///         loading.
///     </para>
///     <para>
///         <b>A browser will not make a sound until the user has clicked something.</b> That is the
///         autoplay policy and there is no way around it. <see cref="WebAudioDevice.Resume" /> is
///         what an application calls from its first input handler; until then the mixer runs, voices
///         start and finish, and the speakers are silent — which is the same shape as
///         <c>NullAudioBackend</c> and means no code path is special-cased for it.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class WebAudioBackend : IAudioBackend {
    static readonly AudioDeviceInfo[] Devices = [
        new("default", "Browser output", true, AudioFormat.Stereo48k)
    ];

    WebAudioBackend(bool available) => IsAvailable = available;

    /// <summary>Fetches the JavaScript module and reports whether this tab has WebAudio at all.</summary>
    /// <param name="moduleUrl">
    ///     Where <c>vixen-audio.js</c> is. Defaults to beside the application, which is where the
    ///     package's copy lands for a build that copied its output to the site root.
    /// </param>
    /// <returns>The backend. Check <see cref="IsAvailable" /> before opening a device.</returns>
    public static async Task<WebAudioBackend> CreateAsync(string? moduleUrl = null) {
        try {
            await WebAudioInterop.ImportAsync(moduleUrl ?? WebAudioInterop.DefaultModuleUrl);
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            // A module that will not load is not a crash. Backend selection tries the next
            // candidate, which on the web is silence — and a game with no sound still runs.
            return new WebAudioBackend(available: false);
        }

        return new WebAudioBackend(available: true);
    }

    /// <inheritdoc />
    public string Name => "WebAudio";

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     One entry, always. A browser does not let a page enumerate the machine's outputs — that is
    ///     a fingerprinting surface — and picking one is the operating system's job and the user's,
    ///     not the page's.
    /// </remarks>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => IsAvailable ? Devices : [];

    /// <inheritdoc />
    public IAudioDevice OpenDevice(in AudioDeviceOptions options) {
        if (!IsAvailable) {
            throw new AudioDeviceException("WebAudio is not available in this browser.");
        }

        var requested = options.Format.IsValid ? options.Format : AudioFormat.Stereo48k;

        // Mono or stereo. A browser will happily make an AudioBuffer with six channels and then
        // downmix it to whatever the output is, which is a mix Vixen has no say in — better to
        // render the layout that will actually be played.
        var channels = Math.Clamp(requested.Channels, 1, 2);
        var frames = options.BufferFrames > 0 ? options.BufferFrames : 480;
        var count = Math.Max(2, options.BufferCount);

        var handle = WebAudioInterop.Create(requested.SampleRate, channels, frames, count);

        if (handle == 0) {
            throw new AudioDeviceException("This browser has no AudioContext.");
        }

        // The browser decides the rate. Asking is a request; this is the answer, and the mixer is
        // prepared against it rather than against what was asked for.
        var granted = new AudioFormat(WebAudioInterop.SampleRate(handle), channels);
        return new WebAudioDevice(handle, Devices[0], granted, frames);
    }

    /// <inheritdoc />
    public void Dispose() { }
}
