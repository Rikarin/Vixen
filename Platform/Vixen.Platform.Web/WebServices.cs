// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Web;

/// <summary>
///     The browser's clock, in the ticks the rest of the engine measures in.
/// </summary>
/// <remarks>
///     <para>
///         A DOM event's <c>timeStamp</c> is milliseconds on <c>performance.now()</c>'s clock, which
///         is monotonic and has a different origin from <see cref="Stopwatch" />'s. Stamping events
///         on arrival instead — which the Android and iOS platforms do — would collapse a frame's
///         worth of input onto one instant, and on the web that frame's input is drained in a single
///         call, so <em>every</em> event in it would share a timestamp. Input latency and
///         gesture timing both stop being measurable at that point.
///     </para>
///     <para>
///         So the two clocks are paired once, at boot, and everything after that is a conversion.
///         Both are monotonic, so the offset stays correct across a wall-clock adjustment — which is
///         the property <c>docs/plan/10 § Cross-platform discipline</c> bans <see cref="DateTime" />
///         in the loop to preserve.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal static class WebClock {
    static readonly long OriginTicks = Stopwatch.GetTimestamp();
    static readonly double OriginMilliseconds = WebInterop.Now();
    static readonly double TicksPerMillisecond = Stopwatch.Frequency / 1000.0;

    /// <summary>Now, for an event the platform raised itself.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>A browser timestamp, as <see cref="Stopwatch" /> ticks.</summary>
    /// <param name="milliseconds">A <c>performance.now()</c> reading.</param>
    public static long FromBrowser(double milliseconds) =>
        OriginTicks + (long)((milliseconds - OriginMilliseconds) * TicksPerMillisecond);

    /// <summary>Forces the pairing to happen now rather than on first use.</summary>
    /// <remarks>
    ///     Called from the platform's constructor so that the origin is taken before the first
    ///     frame, not in the middle of one. A static initialiser that runs on the first event would
    ///     make that event's timestamp its own origin.
    /// </remarks>
    public static void Prime() => _ = OriginTicks;
}

/// <summary>The tab's viewport, as the one display a page is allowed to know about.</summary>
/// <remarks>
///     <para>
///         <b>A page cannot enumerate monitors, and that is deliberate on the browser's part.</b>
///         Screen count, arrangement and per-monitor resolution are a fingerprinting surface; the
///         Window Management API exposes them behind a permission prompt, which is not something to
///         raise so that <see cref="IDisplayInfo" /> can return a longer list. One display is
///         reported, and <see cref="PlatformCapabilities.DisplayEnumeration" /> is absent so that
///         callers know the list is not the machine's.
///     </para>
///     <para>
///         The refresh rate is <em>measured</em>, from the intervals between
///         <c>requestAnimationFrame</c> callbacks, because there is no API for it and 120 Hz
///         displays are now common enough that assuming 60 makes a frame-pacing decision wrong on
///         half the hardware. It reads 60 until the loop has run long enough to know better.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebDisplays : IDisplayInfo {
    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Displays => [Screen];

    /// <inheritdoc />
    public DisplayInfo? Primary => Screen;

    static DisplayInfo Screen {
        get {
            var scale = (float)WebInterop.DpiScale(0);
            var width = WebInterop.ScreenWidth();
            var height = WebInterop.ScreenHeight();

            var bounds = new Rectangle(0, 0, width, height);

            // availWidth/Height is the screen minus whatever the OS keeps — a taskbar, a dock, a
            // menu bar — which is exactly what WorkArea means, and one of the few things a page is
            // told accurately.
            var work = new Rectangle(0, 0, WebInterop.ScreenAvailWidth(), WebInterop.ScreenAvailHeight());

            var measured = (float)WebInterop.RefreshRate();
            var mode = new DisplayMode(
                new((int)(width * scale), (int)(height * scale)),
                measured > 0 ? measured : 60f,
                WebInterop.IsHdr()
            );

            // No mode list: a page cannot switch a display's mode, so offering one would be
            // offering something every caller would then fail to use.
            return new(0, "Browser", bounds, work, scale, mode, [], IsPrimary: true);
        }
    }

    /// <inheritdoc />
    public bool TryGetForWindow(IWindow window, [NotNullWhen(true)] out DisplayInfo? display) {
        ArgumentNullException.ThrowIfNull(window);
        display = Screen;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetForPoint(Int2 point, [NotNullWhen(true)] out DisplayInfo? display) {
        display = Screen;
        return true;
    }
}

/// <summary>The clipboard, as much of it as a page is ever allowed to see.</summary>
/// <remarks>
///     <para>
///         <b>What is served is what the last paste delivered</b>, which is what
///         <see cref="IClipboard" />'s own documentation says the web implementation would have to
///         do — reading the clipboard is gated on a user gesture, and <c>navigator.clipboard.read</c>
///         called from anywhere but a paste handler resolves with nothing or rejects. So the
///         document-level <c>paste</c> listener latches text, custom formats and — decoded
///         asynchronously — an image, and this reads that latch.
///     </para>
///     <para>
///         <b>Writing is asynchronous and reports what can be known.</b>
///         <c>navigator.clipboard.writeText</c> returns a promise whose answer arrives long after a
///         synchronous method has had to return, so <see cref="SetText" /> reports whether the
///         browser has the API and was asked — the strongest true statement available — and not
///         whether the clipboard now holds the text.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebClipboard : IClipboard {
    /// <inheritdoc />
    public bool HasText => WebInterop.HasClipboardText();

    /// <inheritdoc />
    public bool HasImage => WebInterop.ClipboardImageWidth() > 0 && WebInterop.ClipboardImageHeight() > 0;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) {
        text = WebInterop.ClipboardText();
        return !string.IsNullOrEmpty(text);
    }

    /// <inheritdoc />
    public bool SetText(string text) => WebInterop.SetClipboardText(text ?? string.Empty);

    /// <inheritdoc />
    /// <remarks>
    ///     Straight RGBA8, which is what a <c>2d</c> context's <c>getImageData</c> produces and what
    ///     <see cref="ClipboardImage" /> asks for — no premultiplication step, and no conversion.
    /// </remarks>
    public bool TryGetImage(out ClipboardImage image) {
        var width = WebInterop.ClipboardImageWidth();
        var height = WebInterop.ClipboardImageHeight();

        if (width <= 0 || height <= 0) {
            image = default;
            return false;
        }

        var pixels = new byte[width * height * 4];

        if (!WebInterop.ReadClipboardImage(pixels)) {
            image = default;
            return false;
        }

        image = new(pixels, new(width, height));
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Refused. Putting an image on the clipboard needs <c>ClipboardItem</c> with an encoded
    ///     <c>image/png</c> blob, and encoding a PNG means a codec in a runtime assembly — which
    ///     ADR-015 keeps out of shipping builds. An application that has an encoder can write the
    ///     blob itself.
    /// </remarks>
    public bool SetImage(in ClipboardImage image) => false;

    /// <inheritdoc />
    /// <remarks>
    ///     The format is a MIME type here, which is the browser's own vocabulary for exactly this —
    ///     <c>text/html</c>, <c>application/json</c>, or an application's own. The bytes are the
    ///     UTF-8 of what <c>DataTransfer.getData</c> returned: a paste event carries strings, and
    ///     binary custom formats need the asynchronous <c>read()</c> path this interface cannot use.
    /// </remarks>
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        ArgumentException.ThrowIfNullOrEmpty(format);

        if (!WebInterop.HasClipboardData(format)) {
            data = default;
            return false;
        }

        data = Encoding.UTF8.GetBytes(WebInterop.ClipboardData(format));
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Refused. Writing a custom format needs <c>ClipboardItem</c>, and browsers restrict which
    ///     types it will accept to a short allow-list that an application's own format is not on.
    /// </remarks>
    public bool SetData(string format, ReadOnlySpan<byte> data) => false;

    /// <inheritdoc />
    public void Clear() => WebInterop.ClearClipboard();
}

/// <summary>Battery and thermal state, of which a browser reports one and a half.</summary>
/// <remarks>
///     <para>
///         The Battery Status API is gone from Firefox and Safari and is unlikely to come back: it
///         was removed as a fingerprinting surface, not deprecated for a replacement. Where it is
///         absent everything here reports the "will not say" answer that
///         <see cref="IPowerInfo" /> already models as <see langword="null" />, rather than a
///         plausible default that a quality-scaling policy would then act on.
///     </para>
///     <para>
///         There is no thermal API at all, and no low-power-mode signal. Both report the honest
///         nothing. A browser build that wants to scale quality has frame time and
///         <see cref="IDisplayInfo" />'s measured refresh rate, which is less than a phone gets and
///         is what there is.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebPower : IPowerInfo {
    /// <inheritdoc />
    public PowerSource Source {
        get {
            if (!WebInterop.HasBattery()) {
                return PowerSource.Unknown;
            }

            return WebInterop.BatteryCharging() ? PowerSource.Charging : PowerSource.Battery;
        }
    }

    /// <inheritdoc />
    public float? BatteryLevel {
        get {
            var level = WebInterop.BatteryLevel();
            return level is >= 0 and <= 1 ? (float)level : null;
        }
    }

    /// <inheritdoc />
    public TimeSpan? EstimatedTimeRemaining {
        get {
            var seconds = WebInterop.BatteryDischargingTime();
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
    }

    /// <inheritdoc />
    /// <remarks>Always <see cref="ThermalState.Nominal" />. No browser reports thermal state.</remarks>
    public ThermalState Thermal => ThermalState.Nominal;

    /// <inheritdoc />
    /// <remarks>
    ///     Always <see langword="false" />. There is no API. <c>navigator.connection.saveData</c> is
    ///     the nearest thing and is a <em>data</em>-saver preference — acting on it as though it
    ///     were a power one would drop a user's frame rate because they are on a metered
    ///     connection.
    /// </remarks>
    public bool IsLowPowerMode => false;
}

/// <summary>What the browser will admit about the processors.</summary>
/// <remarks>
///     <para>
///         <b><see cref="AvailableProcessors" /> is one unless the page is cross-origin isolated</b>,
///         and that is the number that matters rather than the hardware's. .NET threads on
///         <c>browser-wasm</c> need <c>SharedArrayBuffer</c>, which needs COOP and COEP headers on
///         every response. Without them the runtime has one thread whatever
///         <c>navigator.hardwareConcurrency</c> says, and a job system sized from the hardware count
///         would try to start workers that throw.
///     </para>
///     <para>
///         <c>hardwareConcurrency</c> is also rounded down by every browser for fingerprinting
///         reasons — Safari caps it, Firefox's resist-fingerprinting mode reports 2 — so even where
///         threads exist it is a hint. It is reported through <see cref="PhysicalCores" /> so that a
///         diagnostic can show it, and it is not what a pool is sized from.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal sealed class WebProcessors : IProcessorTopology {
    /// <inheritdoc />
    public int AvailableProcessors { get; } =
        WebInterop.IsCrossOriginIsolated() ? Math.Max(1, WebInterop.HardwareConcurrency()) : 1;

    /// <inheritdoc />
    /// <remarks>What the browser claims the machine has, which is a hint rather than a count. See
    /// the type's remarks.</remarks>
    public int PhysicalCores { get; } = Math.Max(1, WebInterop.HardwareConcurrency());

    /// <inheritdoc />
    /// <remarks>Zero. A page is not told which cores are performance cores, and on a phone that is
    /// exactly the split that would matter.</remarks>
    public int PerformanceCores => 0;

    /// <inheritdoc />
    public bool SupportsAffinity => false;

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) => ProcessorClass.Unknown;

    /// <inheritdoc />
    public bool TrySetAffinity(int processor) => false;

    /// <inheritdoc />
    public void ClearAffinity() { }

    /// <summary>Whether this page has the headers that make threads possible.</summary>
    /// <remarks>
    ///     Worth surfacing rather than inferring from <see cref="AvailableProcessors" />: an
    ///     isolated page on a single-core machine reports one available processor too, and the two
    ///     situations want different diagnostics.
    /// </remarks>
    public bool IsCrossOriginIsolated { get; } = WebInterop.IsCrossOriginIsolated();
}

/// <summary>Nothing, and for a reason worth stating.</summary>
/// <remarks>
///     <para>
///         A browser's file picker is <c>&lt;input type="file"&gt;</c> or
///         <c>showOpenFilePicker</c>, and both give back a <em>handle</em> to a file, never a path.
///         <see cref="INativeDialogs" /> returns <see cref="string" /> paths, and returning a made-up
///         one would produce something that looks like a file and cannot be opened — the same
///         conclusion the Android and iOS platforms reach about the Storage Access Framework and the
///         document picker.
///     </para>
///     <para>
///         A message box is <c>alert()</c>, which blocks the browser's main thread — and the main
///         thread is where the WebAssembly runtime lives. Showing one would freeze the frame loop
///         inside a call that cannot return until the user clicks, which is not a dialog, it is a
///         hang with a button on it.
///     </para>
///     <para>
///         Dropped files are the path that <em>does</em> work, and it is real: a
///         <see cref="PlatformEventKind.DropFile" /> names the file and
///         <see cref="WebPlatform.ReadDroppedFileAsync" /> reads its bytes.
///     </para>
/// </remarks>
internal sealed class WebDialogs : INativeDialogs {
    /// <inheritdoc />
    public ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(MessageBoxResult.None);
}
