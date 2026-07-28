// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Enumeration;
using Vixen.Audio.Devices;

// Silk.NET has an AudioDeviceException of its own, thrown by its convenience wrappers. This file
// throws ours, which is the one an IAudioBackend contract says callers may catch.
using AudioDeviceException = Vixen.Audio.Devices.AudioDeviceException;

namespace Vixen.Audio.Backend.OpenAL;

/// <summary>OpenAL, used as a sink and nothing else.</summary>
/// <remarks>
///     <para>
///         <b>What this backend does not do is the interesting part.</b> OpenAL will spatialise,
///         attenuate, apply a distance model and — with EFX — reverberate. None of that is used.
///         Vixen mixes in software (see <c>Core/Vixen.Audio/Mixing/AudioMixer.cs</c> for why), so
///         what arrives here is a finished interleaved stereo signal, and the whole backend is one
///         source with a queue of buffers and a thread that keeps it full.
///     </para>
///     <para>
///         That is not a waste of OpenAL. It is what makes OpenAL replaceable: the browser's audio
///         API cannot be driven the way OpenAL's can, and a backend that had leaned on either one's
///         mixer would have had to be written twice and would still have sounded different.
///     </para>
///     <para>
///         <b>OpenAL Soft travels with the game.</b> Unlike Vulkan, where the loader and the driver
///         come from the platform, there is no OpenAL on a stock Windows or Linux install and macOS's
///         is a deprecated 1.1 shim. The <c>Silk.NET.OpenAL.Soft.Native</c> package puts the
///         implementation in <c>runtimes/&lt;rid&gt;/native</c>, which is where the .NET host looks
///         first.
///     </para>
/// </remarks>
public sealed unsafe class OpenALBackend : IAudioBackend {
    readonly ILogger logger;
    readonly ALContext? alc;
    readonly AL? al;

    /// <summary>A backend, loading the OpenAL library.</summary>
    /// <param name="logger">Where to report. Nothing is logged from the audio thread.</param>
    /// <remarks>
    ///     Loading is attempted here and its failure is recorded rather than thrown. A machine with
    ///     no OpenAL — a container, a CI runner, a locked-down build agent — should run the game, and
    ///     <see cref="IsAvailable" /> is how backend selection is told to try the next candidate.
    /// </remarks>
    public OpenALBackend(ILogger? logger = null) {
        this.logger = logger ?? NullLogger.Instance;

        if (OpenALLoader.TryLoad(out var api, out var context, out var reason)) {
            al = api;
            alc = context;
            return;
        }

        OpenALLog.LibraryMissing(this.logger, reason);
    }

    /// <summary>Which file OpenAL was loaded from, for a log line at boot and for a bug report.</summary>
    /// <remarks>Static because the library is loaded once for the process, like the Vulkan loader's.</remarks>
    public static string? LibraryPath => OpenALLoader.ResolvedPath;

    /// <inheritdoc />
    public string Name => "OpenAL";

    /// <inheritdoc />
    public bool IsAvailable => alc is not null && al is not null;

    /// <inheritdoc />
    /// <remarks>
    ///     Through <c>ALC_ENUMERATE_ALL_EXT</c> where it is present, which is everywhere OpenAL Soft
    ///     is. Without it there is exactly one device — whatever <c>alcOpenDevice(null)</c> gives —
    ///     and that is reported as the one entry rather than as an empty list, because "no devices"
    ///     and "no way to list them" are different states and only one of them means silence.
    /// </remarks>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() {
        if (alc is null) {
            return [];
        }

        if (!alc.TryGetExtension<Enumeration>(null, out var enumeration)) {
            return [new AudioDeviceInfo(string.Empty, "Default", true, AudioFormat.Stereo48k)];
        }

        var names = enumeration.GetStringList(GetEnumerationContextStringList.DeviceSpecifiers);
        var preferred = enumeration.GetString(null, GetEnumerationContextString.DefaultDeviceSpecifier);
        var devices = new List<AudioDeviceInfo>();

        foreach (var name in names) {
            devices.Add(new AudioDeviceInfo(name, name, name == preferred, AudioFormat.Stereo48k));
        }

        if (devices.Count == 0) {
            devices.Add(new AudioDeviceInfo(string.Empty, "Default", true, AudioFormat.Stereo48k));
        }

        return devices;
    }

    /// <inheritdoc />
    public IAudioDevice OpenDevice(in AudioDeviceOptions options) {
        if (alc is null || al is null) {
            throw new AudioDeviceException("The OpenAL library is not loaded.");
        }

        var requested = options.Format.IsValid ? options.Format : AudioFormat.Stereo48k;

        // Mono or stereo, and nothing else. Wider layouts need AL_EXT_MCFORMATS, which is not
        // present everywhere OpenAL is, and a mixer asked for 5.1 that quietly got stereo would be
        // worse than one that said what it did.
        var channels = Math.Clamp(requested.Channels, 1, 2);
        var format = new AudioFormat(requested.SampleRate, channels);

        var handle = alc.OpenDevice(options.DeviceId ?? string.Empty);

        if (handle is null) {
            throw new AudioDeviceException(
                options.DeviceId is null
                    ? "alcOpenDevice returned nothing for the default device."
                    : $"alcOpenDevice returned nothing for '{options.DeviceId}'."
            );
        }

        var context = alc.CreateContext(handle, null);

        if (context is null) {
            alc.CloseDevice(handle);
            throw new AudioDeviceException($"alcCreateContext failed: {alc.GetError(handle)}.");
        }

        var info = new AudioDeviceInfo(
            options.DeviceId ?? string.Empty,
            string.IsNullOrEmpty(options.DeviceId) ? "Default" : options.DeviceId,
            string.IsNullOrEmpty(options.DeviceId),
            format
        );

        try {
            return new OpenALDevice(alc, al, handle, context, info, format, options, logger);
        } catch {
            alc.DestroyContext(context);
            alc.CloseDevice(handle);
            throw;
        }
    }

    /// <summary>Nothing. The library is loaded once and shared, like the Vulkan loader.</summary>
    /// <remarks>
    ///     Disposing an <c>AL</c> unloads the native library out from under any other backend
    ///     instance holding it, and backend selection constructs every candidate. Devices own
    ///     everything that genuinely has to be released.
    /// </remarks>
    public void Dispose() { }
}
