// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Vixen.Audio.Backend.WebAudio;

/// <summary>The calls across to <c>vixen-audio.js</c>.</summary>
/// <remarks>
///     <para>
///         Generated marshalling, not <c>eval</c>: <c>[JSImport]</c> emits a direct call through the
///         runtime's interop table, which is both faster and the only form that survives trimming and
///         ahead-of-time compilation — a browser build is published with both.
///     </para>
///     <para>
///         <b><see cref="Enqueue" /> takes bytes and not floats.</b> <c>JSType.MemoryView</c> is
///         defined for <c>byte</c>, <c>int</c> and <c>double</c> and not for <c>float</c>, so the
///         block crosses as its own bytes and JavaScript puts a <c>Float32Array</c> over them. The
///         alternative the marshaller does offer — a <c>double[]</c> as
///         <c>JSType.Array&lt;JSType.Number&gt;</c> — would double the size of every block and
///         convert every sample twice, to reach an API that wants floats at the other end.
///     </para>
///     <para>
///         The view is only valid for the duration of the call, which is why the JavaScript side
///         copies out of it into an <c>AudioBuffer</c> immediately and keeps nothing.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class WebAudioInterop {
    /// <summary>What the module is called once imported.</summary>
    public const string ModuleName = "vixen-audio";

    /// <summary>Where it is fetched from when the caller does not say.</summary>
    /// <remarks>
    ///     ⚠ <c>../</c>, for the reason set out on <c>WebInterop.DefaultModuleUrl</c>:
    ///     <see cref="JSHost.ImportAsync" /> resolves against the runtime's module in
    ///     <c>_framework/</c>, and this file is a content file at the site root. A page that
    ///     arranges its assets differently passes its own URL.
    /// </remarks>
    public const string DefaultModuleUrl = "../vixen-audio.js";

    /// <summary>Loads the module. Must complete before anything else here is called.</summary>
    /// <param name="url">Where the module is.</param>
    /// <returns>The task that completes when it has been fetched and evaluated.</returns>
    public static Task ImportAsync(string url) => JSHost.ImportAsync(ModuleName, url);

    [JSImport("create", ModuleName)]
    public static partial int Create(int sampleRate, int channels, int blockFrames, int blockCount);

    [JSImport("sampleRate", ModuleName)]
    public static partial int SampleRate(int handle);

    [JSImport("isRunning", ModuleName)]
    public static partial bool IsRunning(int handle);

    [JSImport("underruns", ModuleName)]
    public static partial int Underruns(int handle);

    [JSImport("resume", ModuleName)]
    public static partial void Resume(int handle);

    [JSImport("start", ModuleName)]
    public static partial void Start(
        int handle,
        [JSMarshalAs<JSType.Function<JSType.Number>>] Action<int> pump
    );

    [JSImport("stop", ModuleName)]
    public static partial void Stop(int handle);

    [JSImport("enqueue", ModuleName)]
    public static partial void Enqueue(
        int handle,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> samples,
        int frames
    );

    [JSImport("close", ModuleName)]
    public static partial void Close(int handle);

    [JSImport("captureCreate", ModuleName)]
    public static partial int CaptureCreate(int sampleRate, int channels, int bufferedFrames);

    [JSImport("captureSampleRate", ModuleName)]
    public static partial int CaptureSampleRate(int handle);

    [JSImport("captureIsRunning", ModuleName)]
    public static partial bool CaptureIsRunning(int handle);

    [JSImport("captureAvailable", ModuleName)]
    public static partial int CaptureAvailable(int handle);

    [JSImport("captureOverruns", ModuleName)]
    public static partial int CaptureOverruns(int handle);

    [JSImport("captureStart", ModuleName)]
    public static partial void CaptureStart(int handle);

    /// <summary>Copies what has been captured into the caller's buffer.</summary>
    /// <remarks>
    ///     Bytes again, for the reason <see cref="Enqueue" /> gives — and in this direction the view
    ///     is written rather than read, which the marshaller supports and which is why the capture
    ///     path needs no buffer of its own on the JavaScript side beyond its ring.
    /// </remarks>
    [JSImport("captureRead", ModuleName)]
    public static partial int CaptureRead(
        int handle,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> samples,
        int frames
    );

    [JSImport("captureStop", ModuleName)]
    public static partial void CaptureStop(int handle);

    [JSImport("captureClose", ModuleName)]
    public static partial void CaptureClose(int handle);
}
