// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Vixen.Platform.Web;

/// <summary>The calls across to <c>vixen-platform.js</c>.</summary>
/// <remarks>
///     <para>
///         Generated marshalling, not <c>eval</c>: <c>[JSImport]</c> emits a direct call through the
///         runtime's interop table, which is both faster and the only form that survives trimming and
///         ahead-of-time compilation — a browser build is published with both.
///     </para>
///     <para>
///         <b>Two shapes recur, and both are here for the same reason.</b> Bulk data crosses as a
///         <c>JSType.MemoryView</c> over a span that is only valid for the duration of the call, so
///         the other side copies immediately and keeps nothing. And an asynchronous read cannot
///         resolve <em>with</em> such a view — it would outlive the call it was valid for — so it
///         resolves with a <em>buffer handle</em> instead, and a second synchronous call copies the
///         bytes out. That is why <see cref="FetchRange" /> returns an <see cref="int" /> and is
///         always followed by <see cref="ReadBuffer" /> and <see cref="ReleaseBuffer" />.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class WebInterop {
    /// <summary>What the module is called once imported.</summary>
    public const string ModuleName = "vixen-platform";

    /// <summary>Where it is fetched from when the caller does not say.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>../</c>, and the two dots are the whole point.</b>
    ///         <see cref="System.Runtime.InteropServices.JavaScript.JSHost.ImportAsync" /> resolves a
    ///         relative URL against the <em>runtime's</em> module, which
    ///         <c>Microsoft.NET.Sdk.WebAssembly</c> publishes into <c>_framework/</c> — not against
    ///         the page. This file is a content file and lands at the site root. So <c>./</c>, which
    ///         this was, asked for <c>_framework/vixen-platform.js</c> and got a 404 dressed up as
    ///         <c>TypeError: Failed to fetch dynamically imported module</c> from inside
    ///         <c>WebPlatform.CreateAsync</c>, for the layout the SDK produces by default — which is
    ///         to say for every head that did not already pass
    ///         <see cref="WebPlatformOptions.ModuleUrl" />. Measured by publishing a head and
    ///         running it; there is no build-time diagnostic for it.
    ///     </para>
    ///     <para>
    ///         A page that arranges its assets differently still passes its own URL.
    ///     </para>
    /// </remarks>
    public const string DefaultModuleUrl = "../vixen-platform.js";

    /// <summary>How many <see cref="double" />s one event occupies in the drained ring.</summary>
    /// <remarks>
    ///     Fixed, and duplicated in <c>vixen-platform.js</c> as <c>RECORD</c>. The two have to agree
    ///     and there is no way to make one derive from the other across the language boundary, so
    ///     <see cref="WebEventRecord" /> reads the slots by name and a mismatch shows up as an
    ///     assertion there rather than as plausible nonsense in an event.
    /// </remarks>
    public const int EventStride = 12;

    /// <summary>Loads the module. Must complete before anything else here is called.</summary>
    /// <param name="url">Where the module is.</param>
    /// <returns>The task that completes when it has been fetched and evaluated.</returns>
    public static Task ImportAsync(string url) => JSHost.ImportAsync(ModuleName, url);

    // ── Boot ─────────────────────────────────────────────────────────────────────────────────

    [JSImport("initialise", ModuleName)]
    public static partial void Initialise();

    // ── The event ring ───────────────────────────────────────────────────────────────────────

    /// <summary>Copies whole event records out and returns how many were written.</summary>
    /// <remarks>
    ///     A caller that gets back its own capacity calls again: the ring keeps whatever did not
    ///     fit, in order, rather than dropping the tail of a frame's input.
    /// </remarks>
    [JSImport("drainEvents", ModuleName)]
    public static partial int DrainEvents([JSMarshalAs<JSType.MemoryView>] Span<double> destination);

    [JSImport("droppedEvents", ModuleName)]
    public static partial int DroppedEvents();

    /// <summary><c>performance.now()</c>, the clock every event timestamp is measured against.</summary>
    [JSImport("now", ModuleName)]
    public static partial double Now();

    [JSImport("takeString", ModuleName)]
    public static partial string TakeString(int handle);

    [JSImport("modifiers", ModuleName)]
    public static partial int Modifiers();

    // ── Canvases ─────────────────────────────────────────────────────────────────────────────

    [JSImport("createCanvas", ModuleName)]
    public static partial int CreateCanvas(string? selector);

    [JSImport("canvasSelector", ModuleName)]
    public static partial string CanvasSelector(int handle);

    [JSImport("destroyCanvas", ModuleName)]
    public static partial void DestroyCanvas(int handle);

    [JSImport("clientWidth", ModuleName)]
    public static partial int ClientWidth(int handle);

    [JSImport("clientHeight", ModuleName)]
    public static partial int ClientHeight(int handle);

    [JSImport("pixelWidth", ModuleName)]
    public static partial int PixelWidth(int handle);

    [JSImport("pixelHeight", ModuleName)]
    public static partial int PixelHeight(int handle);

    [JSImport("dpiScale", ModuleName)]
    public static partial double DpiScale(int handle);

    [JSImport("setClientSize", ModuleName)]
    public static partial void SetClientSize(int handle, int width, int height);

    [JSImport("setVisible", ModuleName)]
    public static partial void SetVisible(int handle, bool visible);

    [JSImport("isVisible", ModuleName)]
    public static partial bool IsVisible(int handle);

    [JSImport("focus", ModuleName)]
    public static partial void Focus(int handle);

    [JSImport("isFocused", ModuleName)]
    public static partial bool IsFocused(int handle);

    [JSImport("setTitle", ModuleName)]
    public static partial void SetTitle(string title);

    [JSImport("requestFullscreen", ModuleName)]
    public static partial void RequestFullscreen(int handle);

    [JSImport("exitFullscreen", ModuleName)]
    public static partial void ExitFullscreen();

    [JSImport("isFullscreen", ModuleName)]
    public static partial bool IsFullscreen(int handle);

    [JSImport("setCursor", ModuleName)]
    public static partial void SetCursor(int handle, string css);

    [JSImport("requestPointerLock", ModuleName)]
    public static partial void RequestPointerLock(int handle);

    [JSImport("exitPointerLock", ModuleName)]
    public static partial void ExitPointerLock();

    [JSImport("isPointerLocked", ModuleName)]
    public static partial bool IsPointerLocked(int handle);

    // ── The frame loop ───────────────────────────────────────────────────────────────────────

    [JSImport("startFrameLoop", ModuleName)]
    public static partial void StartFrameLoop(
        [JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> onFrame
    );

    [JSImport("stopFrameLoop", ModuleName)]
    public static partial void StopFrameLoop();

    [JSImport("refreshRate", ModuleName)]
    public static partial double RefreshRate();

    // ── Clipboard ────────────────────────────────────────────────────────────────────────────

    [JSImport("clipboardText", ModuleName)]
    public static partial string ClipboardText();

    [JSImport("hasClipboardText", ModuleName)]
    public static partial bool HasClipboardText();

    [JSImport("setClipboardText", ModuleName)]
    public static partial bool SetClipboardText(string text);

    [JSImport("clipboardImageWidth", ModuleName)]
    public static partial int ClipboardImageWidth();

    [JSImport("clipboardImageHeight", ModuleName)]
    public static partial int ClipboardImageHeight();

    [JSImport("readClipboardImage", ModuleName)]
    public static partial bool ReadClipboardImage([JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("clipboardData", ModuleName)]
    public static partial string ClipboardData(string format);

    [JSImport("hasClipboardData", ModuleName)]
    public static partial bool HasClipboardData(string format);

    [JSImport("clearClipboard", ModuleName)]
    public static partial void ClearClipboard();

    // ── Text input ───────────────────────────────────────────────────────────────────────────

    [JSImport("activateTextInput", ModuleName)]
    public static partial bool ActivateTextInput(int handle);

    [JSImport("deactivateTextInput", ModuleName)]
    public static partial void DeactivateTextInput();

    [JSImport("setCandidateArea", ModuleName)]
    public static partial void SetCandidateArea(int handle, double x, double y, double width, double height);

    [JSImport("hasOnScreenKeyboard", ModuleName)]
    public static partial bool HasOnScreenKeyboard();

    [JSImport("onScreenKeyboardArea", ModuleName)]
    public static partial bool OnScreenKeyboardArea([JSMarshalAs<JSType.MemoryView>] Span<double> destination);

    [JSImport("isTextInputActive", ModuleName)]
    public static partial bool IsTextInputActive();

    // ── Gamepads ─────────────────────────────────────────────────────────────────────────────

    [JSImport("gamepadStride", ModuleName)]
    public static partial int GamepadStride();

    [JSImport("pollGamepads", ModuleName)]
    public static partial int PollGamepads([JSMarshalAs<JSType.MemoryView>] Span<double> destination);

    [JSImport("gamepadName", ModuleName)]
    public static partial string GamepadName(int index);

    [JSImport("rumble", ModuleName)]
    public static partial bool Rumble(int index, double weak, double strong, double milliseconds);

    [JSImport("stopRumble", ModuleName)]
    public static partial void StopRumble(int index);

    [JSImport("hasRumble", ModuleName)]
    public static partial bool HasRumble(int index);

    // ── Screen, processors, power ────────────────────────────────────────────────────────────

    [JSImport("screenWidth", ModuleName)]
    public static partial int ScreenWidth();

    [JSImport("screenHeight", ModuleName)]
    public static partial int ScreenHeight();

    [JSImport("screenAvailWidth", ModuleName)]
    public static partial int ScreenAvailWidth();

    [JSImport("screenAvailHeight", ModuleName)]
    public static partial int ScreenAvailHeight();

    [JSImport("isHdr", ModuleName)]
    public static partial bool IsHdr();

    [JSImport("hardwareConcurrency", ModuleName)]
    public static partial int HardwareConcurrency();

    [JSImport("isCrossOriginIsolated", ModuleName)]
    public static partial bool IsCrossOriginIsolated();

    [JSImport("deviceMemory", ModuleName)]
    public static partial double DeviceMemory();

    [JSImport("hasBattery", ModuleName)]
    public static partial bool HasBattery();

    [JSImport("batteryLevel", ModuleName)]
    public static partial double BatteryLevel();

    [JSImport("batteryCharging", ModuleName)]
    public static partial bool BatteryCharging();

    [JSImport("batteryDischargingTime", ModuleName)]
    public static partial double BatteryDischargingTime();

    [JSImport("openUrl", ModuleName)]
    public static partial bool OpenUrl(string url);

    // ── Buffers ──────────────────────────────────────────────────────────────────────────────

    [JSImport("bufferLength", ModuleName)]
    public static partial int BufferLength(int handle);

    [JSImport("readBuffer", ModuleName)]
    public static partial bool ReadBuffer(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination);

    [JSImport("releaseBuffer", ModuleName)]
    public static partial void ReleaseBuffer(int handle);

    /// <summary>Parks a copy of the caller's bytes for an asynchronous call to use afterwards.</summary>
    /// <remarks>The other direction of the buffer-handle dance. See <see cref="WriteDatabase" />.</remarks>
    [JSImport("stageBuffer", ModuleName)]
    public static partial int StageBuffer([JSMarshalAs<JSType.MemoryView>] Span<byte> contents);

    // ── Dropped files ────────────────────────────────────────────────────────────────────────

    [JSImport("droppedFileCount", ModuleName)]
    public static partial int DroppedFileCount();

    [JSImport("droppedFileName", ModuleName)]
    public static partial string DroppedFileName(int index);

    [JSImport("droppedFileLength", ModuleName)]
    public static partial double DroppedFileLength(int index);

    [JSImport("readDroppedFile", ModuleName)]
    public static partial Task<int> ReadDroppedFile(int index);

    [JSImport("clearDroppedFiles", ModuleName)]
    public static partial void ClearDroppedFiles(int count);

    // ── fetch ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fetches a byte range, or the whole resource when <paramref name="length" /> is
    /// zero.</summary>
    /// <returns>A buffer handle. The task faults with a <see cref="JSException" /> carrying the HTTP
    /// status if the server refused.</returns>
    [JSImport("fetchRange", ModuleName)]
    public static partial Task<int> FetchRange(string url, double offset, double length);

    [JSImport("fetchAll", ModuleName)]
    public static partial Task<int> FetchAll(string url);

    /// <summary>A HEAD request. The buffer holds two doubles: length, then last-modified in
    /// milliseconds since the Unix epoch.</summary>
    [JSImport("fetchHead", ModuleName)]
    public static partial Task<int> FetchHead(string url);

    [JSImport("supportsRanges", ModuleName)]
    public static partial Task<bool> SupportsRanges(string url);

    // ── IndexedDB ────────────────────────────────────────────────────────────────────────────

    [JSImport("openDatabase", ModuleName)]
    public static partial Task<int> OpenDatabase(string name);

    [JSImport("closeDatabase", ModuleName)]
    public static partial void CloseDatabase(int handle);

    /// <summary>Reads every key with its length and write time. Returns how many there were.</summary>
    [JSImport("listDatabase", ModuleName)]
    public static partial Task<int> ListDatabase(int handle);

    [JSImport("listingName", ModuleName)]
    public static partial string ListingName(int index);

    [JSImport("listingLength", ModuleName)]
    public static partial double ListingLength(int index);

    [JSImport("listingTime", ModuleName)]
    public static partial double ListingTime(int index);

    /// <summary>Reads one value. Resolves with a buffer handle, or <c>0</c> if the key is absent.</summary>
    [JSImport("readDatabase", ModuleName)]
    public static partial Task<int> ReadDatabase(int handle, string path);

    /// <summary>Writes one value from a buffer staged by <see cref="StageBuffer" />.</summary>
    /// <remarks>
    ///     Two calls rather than one because the marshaller rejects a memory view on a method that
    ///     returns a <see cref="Task{TResult}" /> outright — <c>SYSLIB1072</c> — and it is right to:
    ///     the view is valid for the duration of the call, and an asynchronous call finishes after
    ///     it. Staging synchronously and putting asynchronously is the shape that survives, and the
    ///     copy it costs is one IndexedDB was going to make anyway.
    /// </remarks>
    [JSImport("writeDatabase", ModuleName)]
    public static partial Task<int> WriteDatabase(int handle, string path, int buffer, double modified);

    [JSImport("deleteDatabase", ModuleName)]
    public static partial Task<bool> DeleteDatabase(int handle, string path);

    /// <summary>Usage and quota, as two doubles in a buffer.</summary>
    [JSImport("storageEstimate", ModuleName)]
    public static partial Task<int> StorageEstimate();

    [JSImport("persistStorage", ModuleName)]
    public static partial Task<bool> PersistStorage();

    // ── Lazy assemblies ──────────────────────────────────────────────────────────────────────

    [JSImport("fetchAssembly", ModuleName)]
    public static partial Task<int> FetchAssembly(string url);
}
