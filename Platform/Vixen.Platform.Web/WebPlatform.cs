// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Vixen.Core.IO;

namespace Vixen.Platform.Web;

/// <summary>A browser tab, behind <see cref="IPlatform" />.</summary>
/// <remarks>
///     <para>
///         <b>Structurally like the mobile platforms: the loop is driven from outside.</b> A
///         WebAssembly <c>Main</c> that never returned would starve the browser's event loop, so the
///         frame is a <c>requestAnimationFrame</c> callback — see <see cref="WebFrameLoop" /> — and
///         <see cref="PumpEvents" /> drains what the DOM listeners already queued, exactly as the
///         Android and iOS platforms drain what their callbacks queued.
///     </para>
///     <para>
///         <b>What is genuinely different is that everything is asynchronous.</b> A browser has no
///         synchronous file read, no synchronous network, and no way to block the one thread the
///         runtime lives on — <c>.GetAwaiter().GetResult()</c> on the main thread does not wait, it
///         deadlocks the tab. That is why construction is <see cref="CreateAsync" /> rather than a
///         constructor: the JavaScript module has to be fetched, the storage opened and the content
///         manifest read before any of the synchronous <see cref="IFileProvider" /> members can
///         answer, and the alternative is an <see cref="IPlatform" /> that lies until it is warm.
///     </para>
///     <para>
///         <b>Input arrives as one buffer per frame.</b> The DOM listeners write fixed-width records
///         into a JavaScript ring and <see cref="PumpEvents" /> copies the whole thing across in a
///         single interop call, rather than paying a marshalled delegate invocation per mousemove.
///         Translation into <see cref="PlatformEvent" /> — including the finger bookkeeping that
///         <see cref="TouchTracker" /> exists for — happens here, in C#, where it is testable.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class WebPlatform : IPlatform {
    /// <summary>How many events one drain asks for. A frame's worth of input, with headroom.</summary>
    /// <remarks>
    ///     A drain that comes back full is repeated, so this is a batch size and not a limit —
    ///     nothing is lost by it being too small, only an extra interop call made. 512 records is
    ///     48 KB, allocated once.
    /// </remarks>
    const int DrainBatch = 512;

    readonly PlatformEventBuffer events = new();
    readonly List<IWindow> windows = [];
    readonly WebInput input = new();
    readonly TouchTracker touches = new();
    readonly double[] drained = new double[DrainBatch * WebInterop.EventStride];
    readonly WebFileSystemHost fileSystem;

    WebWindow? window;
    uint nextWindowId = 1;
    bool disposed;

    WebPlatform(WebFileSystemHost fileSystem, string? canvasSelector) {
        this.fileSystem = fileSystem;
        CanvasSelector = canvasSelector;

        // One buffer, shared with the lifecycle. Constructed here rather than in a field
        // initialiser because a field initialiser cannot see `events`, and a lifecycle with its own
        // buffer is one whose Suspending and Resumed never reach PumpEvents.
        MobileLifecycle = new(events);

        WebClock.Prime();
        WebInterop.Initialise();
        MobileLifecycle.EnterForeground();

        // After `Initialise`, because the module has to be there to be asked. Seeded rather than
        // left to the first pump so that a host choosing a palette before its first frame chooses
        // the right one.
        ColorScheme = (SystemColorScheme)WebInterop.ColorScheme();
    }

    /// <summary>Creates the platform, once the browser has given us everything it has to give.</summary>
    /// <param name="options">What to mount and where from.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The platform.</returns>
    /// <remarks>
    ///     Asynchronous because the module has to be fetched and evaluated, IndexedDB has to be
    ///     opened, and the content manifest has to be read — three round trips that must all have
    ///     finished before <see cref="FileSystem" /> can answer an <see cref="IFileProvider" />
    ///     query, which the interface requires to be synchronous. See
    ///     <see cref="WebFileSystemHost" /> for why that is not negotiable on this platform.
    /// </remarks>
    public static async Task<WebPlatform> CreateAsync(
        WebPlatformOptions options = default,
        CancellationToken cancellationToken = default
    ) {
        await WebInterop.ImportAsync(options.ModuleUrl ?? WebInterop.DefaultModuleUrl).ConfigureAwait(false);

        var fileSystem = await WebFileSystemHost
            .CreateAsync(options, cancellationToken)
            .ConfigureAwait(false);

        return new(fileSystem, options.CanvasSelector);
    }

    /// <inheritdoc />
    public string Name => "Web (browser)";

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         No <see cref="PlatformCapabilities.MultiWindow" />: a tab has one canvas as far as
    ///         this platform is concerned, and a second browser window is the user opening one
    ///         rather than the application doing it.
    ///     </para>
    ///     <para>
    ///         No <see cref="PlatformCapabilities.WindowPositioning" />, because a page is not told
    ///         where it is; no <see cref="PlatformCapabilities.DisplayEnumeration" />, because a
    ///         page is not told what monitors exist; no
    ///         <see cref="PlatformCapabilities.NativeDialogs" />, because a browser's pickers return
    ///         handles rather than paths and its message box freezes the runtime's thread. Each of
    ///         those is a capability query with a documented fallback rather than a
    ///         <c>#if BROWSER</c> somewhere above.
    ///     </para>
    /// </remarks>
    public PlatformCapabilities Capabilities =>
        PlatformCapabilities.Windowing
        | PlatformCapabilities.Cursor
        | PlatformCapabilities.Clipboard
        | PlatformCapabilities.TextInput
        | PlatformCapabilities.GameControllers
        | PlatformCapabilities.Haptics
        | PlatformCapabilities.PowerInfo
        | PlatformCapabilities.Suspension
        | PlatformCapabilities.DragAndDrop;

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => windows;

    /// <inheritdoc />
    public IDisplayInfo Displays { get; } = new WebDisplays();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Polled once a pump rather than driven by the media query's <c>change</c> listener.</b>
    ///     A listener would be the browser's own idiom and would mean one more entry in the event
    ///     ring for a fact that is two <c>matchMedia</c> calls to read — and <c>matchMedia</c> results
    ///     are cached by the browser, so asking is a property read rather than a re-evaluation.
    /// </remarks>
    public SystemColorScheme ColorScheme { get; private set; }

    /// <inheritdoc />
    public IFileSystemHost FileSystem => fileSystem;

    /// <inheritdoc />
    public IClipboard Clipboard { get; } = new WebClipboard();

    /// <inheritdoc />
    public INativeDialogs Dialogs { get; } = new WebDialogs();

    /// <inheritdoc />
    public ILifecycle Lifecycle => MobileLifecycle;

    /// <inheritdoc />
    public IInputSource Input => input;

    /// <inheritdoc />
    public ITextInput TextInput { get; } = new WebTextInput();

    /// <inheritdoc />
    public IPowerInfo Power { get; } = new WebPower();

    /// <inheritdoc />
    public IProcessorTopology Processors { get; } = new WebProcessors();

    /// <summary>The lifecycle, as the thing the page's visibility drives.</summary>
    internal MobileLifecycle MobileLifecycle { get; }

    /// <summary>
    ///     How many events the browser produced that did not fit in the ring, over the session.
    /// </summary>
    /// <remarks>
    ///     Non-zero means input was lost, which is worth saying once: a frame that took eight
    ///     seconds — a synchronous shader compile, a debugger pause — is the only way to reach it,
    ///     and the events that were dropped are releases whose presses were not.
    /// </remarks>
    public static int DroppedEventCount => WebInterop.DroppedEvents();

    /// <summary>The files dropped since the last pump, in the order their events arrived.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="PlatformEventKind.DropFile" />'s <see cref="PlatformEvent.Text" /> is the
    ///         file's <em>name</em> here and not a path, because a browser does not give one: a drop
    ///         hands over a <c>File</c> object and no location on disk, and there is nothing honest
    ///         to put in a field documented as a native path.
    ///     </para>
    ///     <para>
    ///         The bytes are read through <see cref="ReadDroppedFileAsync" />, indexed the same way
    ///         as this list, which is the same order as the events. The list is emptied by the next
    ///         <see cref="PumpEvents" />, so a frame that wants a dropped file reads it in that
    ///         frame.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> DroppedFiles { get; private set; } = [];

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">A second window was asked for.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <see cref="WebPlatformOptions.CanvasSelector" /> matched nothing, or matched something
    ///     that is not a <c>&lt;canvas&gt;</c>.
    /// </exception>
    public IWindow CreateWindow(in WindowOptions options) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (window is { IsClosed: false }) {
            throw new PlatformNotSupportedException(
                "A browser tab has one canvas. This platform does not report "
                + "PlatformCapabilities.MultiWindow, which is the runtime question to ask first."
            );
        }

        var canvas = WebInterop.CreateCanvas(CanvasSelector);

        if (canvas == 0) {
            throw new InvalidOperationException(
                CanvasSelector is null
                    ? "The page has no <body> to append a canvas to."
                    : $"'{CanvasSelector}' matched no <canvas> element. WebPlatformOptions."
                    + "CanvasSelector must name one that is in the document by the time the window "
                    + "is created."
            );
        }

        window = new(nextWindowId++, canvas, events, options);

        windows.Clear();
        windows.Add(window);

        return window;
    }

    /// <summary>Which canvas <see cref="CreateWindow" /> adopts, or <see langword="null" /> to make
    /// one.</summary>
    internal string? CanvasSelector { get; }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        window = this.window is { IsClosed: false } candidate && candidate.Id == id ? candidate : null;
        return window is not null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Drains the JavaScript ring, translates it, folds the result into the polled input state,
    ///     and polls the gamepads — which have no events of their own and must be diffed.
    /// </remarks>
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (window is { IsClosed: true }) {
            windows.Clear();
            window = null;
        }

        // Last frame's drops, released before this frame's are collected. A File held past the
        // frame that was told about it is a File nobody is going to read.
        WebInterop.ClearDroppedFiles(DroppedFiles.Count);
        DroppedFiles = [];

        int taken;

        do {
            taken = WebInterop.DrainEvents(drained);

            for (var index = 0; index < taken; index++) {
                Translate(new(drained.AsSpan(index * WebInterop.EventStride, WebInterop.EventStride)));
            }

            // A full batch means the ring had at least this much, and may have more. Repeating is
            // what keeps a frame's input whole rather than spilling the tail into the next frame.
        } while (taken == DrainBatch);

        TakeDroppedFiles();
        input.PollGamepads(events, WebClock.Now);

        // ⚠ Before `Drain`, so the change travels in the batch it happened in — see the same note on
        // `DesktopPlatform.PumpEvents`, which is the other half of this wire.
        var scheme = (SystemColorScheme)WebInterop.ColorScheme();

        if (scheme != ColorScheme) {
            ColorScheme = scheme;
            events.Post(PlatformEvent.Application(PlatformEventKind.SystemColorSchemeChanged, WebClock.Now));
        }

        var frame = events.Drain();

        foreach (ref readonly var platformEvent in frame) {
            input.Observe(in platformEvent);
        }

        return frame;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>window.open</c> with <c>noopener</c>, always: without it the opened page gets a handle
    ///     to this one through <c>window.opener</c> and can navigate it somewhere else. Returns
    ///     <see langword="false" /> when the pop-up blocker refused, which is the normal answer
    ///     outside a user gesture and is not an error.
    /// </remarks>
    public bool TryOpenUrl(string url) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(url);

        return WebInterop.OpenUrl(url);
    }

    /// <summary>Reads a dropped file's bytes.</summary>
    /// <param name="index">Its position in <see cref="DroppedFiles" />.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>The file's contents.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such file in this frame's drop.</exception>
    /// <remarks>
    ///     Asynchronous because reading a <c>File</c> is, and there is no synchronous form of it in
    ///     any browser. Valid until the next <see cref="PumpEvents" />, which releases the parked
    ///     objects — a frame that wants a dropped file reads it in that frame.
    /// </remarks>
    public async Task<byte[]> ReadDroppedFileAsync(int index, CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, DroppedFiles.Count);

        var buffer = await WebInterop.ReadDroppedFile(index).WaitAsync(cancellationToken).ConfigureAwait(false);
        return WebBuffer.Take(buffer);
    }

    /// <summary>Posts an event as though the browser had raised it.</summary>
    /// <param name="platformEvent">The event.</param>
    public void Post(in PlatformEvent platformEvent) => events.Post(platformEvent);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        MobileLifecycle.Stopping();
        input.Clear();
        window?.Dispose();
        windows.Clear();
        fileSystem.Dispose();
    }

    void TakeDroppedFiles() {
        var count = WebInterop.DroppedFileCount();

        if (count == 0) {
            return;
        }

        var names = new string[count];

        for (var index = 0; index < count; index++) {
            names[index] = WebInterop.DroppedFileName(index);
        }

        DroppedFiles = names;
    }

    void Translate(in WebEventRecord record) {
        var timestamp = WebClock.FromBrowser(record.TimeStampMilliseconds);

        switch (record.Kind) {
            case (int)WebEventKind.PageHidden:
                // Background *and* suspended. A hidden tab stops being composited, its
                // requestAnimationFrame stops firing, and the browser is free to discard it
                // without another word — which is what Suspending means everywhere else.
                MobileLifecycle.EnterBackground();
                MobileLifecycle.Suspend();
                return;

            case (int)WebEventKind.PageVisible:
                MobileLifecycle.EnterForeground();
                return;

            case (int)WebEventKind.PageUnloading:
                MobileLifecycle.Stopping();
                return;

            case (int)WebEventKind.MemoryPressure:
                MobileLifecycle.ReportMemoryPressure(
                    record.Value >= 2 ? MemoryPressure.Critical : MemoryPressure.Warning
                );

                return;

            case (int)PlatformEventKind.KeyDown:
            case (int)PlatformEventKind.KeyUp:
                // A key this browser reported and the table does not name. Dropped rather than
                // posted as Key.Unknown: an unknown key that arrives as a press with no release,
                // or the reverse, is worse for a key-state tracker than one that never arrived.
                if (record.Key == Key.Unknown) {
                    return;
                }

                events.Post(
                    PlatformEvent.Keyboard(
                        (PlatformEventKind)record.Kind,
                        record.WindowId,
                        timestamp,
                        record.Key,
                        record.Modifiers,
                        record.IsRepeat
                    )
                );

                return;

            case (int)PlatformEventKind.TextInput:
                events.Post(
                    PlatformEvent.TextInput(record.WindowId, timestamp, WebInterop.TakeString(record.StringHandle))
                );

                return;

            case (int)PlatformEventKind.TextEditing:
                events.Post(
                    PlatformEvent.TextEditing(
                        record.WindowId,
                        timestamp,
                        WebInterop.TakeString(record.StringHandle),
                        record.Code,
                        record.Device
                    )
                );

                return;

            case (int)PlatformEventKind.MouseMoved:
                events.Post(
                    PlatformEvent.MouseMoved(
                        record.WindowId,
                        timestamp,
                        record.First,
                        record.Second,
                        record.Modifiers
                    )
                );

                return;

            case (int)PlatformEventKind.MouseButtonDown:
            case (int)PlatformEventKind.MouseButtonUp:
                if (record.MouseButton == MouseButton.None) {
                    return;
                }

                events.Post(
                    PlatformEvent.MouseButtonChanged(
                        (PlatformEventKind)record.Kind,
                        record.WindowId,
                        timestamp,
                        record.MouseButton,
                        record.First,
                        record.ClickCount,
                        record.Modifiers
                    )
                );

                return;

            case (int)PlatformEventKind.MouseWheel:
                events.Post(
                    PlatformEvent.MouseWheel(
                        record.WindowId,
                        timestamp,
                        record.First,
                        record.Second,
                        record.Modifiers
                    )
                );

                return;

            case (int)PlatformEventKind.TouchDown:
                if (touches.TryBegin(record.TouchIdentifier, record.First, out var began)) {
                    events.Post(
                        PlatformEvent.Touch(
                            PlatformEventKind.TouchDown,
                            record.WindowId,
                            timestamp,
                            began,
                            record.First,
                            pressure: record.Value
                        )
                    );
                }

                return;

            case (int)PlatformEventKind.TouchMoved:
                if (touches.TryMove(record.TouchIdentifier, record.First, out var moved, out var delta)) {
                    events.Post(
                        PlatformEvent.Touch(
                            PlatformEventKind.TouchMoved,
                            record.WindowId,
                            timestamp,
                            moved,
                            record.First,
                            delta,
                            record.Value
                        )
                    );
                }

                return;

            case (int)PlatformEventKind.TouchUp:
                if (touches.TryEnd(record.TouchIdentifier, out var ended)) {
                    events.Post(
                        PlatformEvent.Touch(
                            PlatformEventKind.TouchUp,
                            record.WindowId,
                            timestamp,
                            ended,
                            record.First,
                            pressure: record.Value
                        )
                    );
                }

                return;

            case (int)PlatformEventKind.WindowResized:
                events.Post(
                    PlatformEvent.WindowResized(
                        record.WindowId,
                        timestamp,
                        new((int)record.First.X, (int)record.First.Y),
                        new((int)record.Second.X, (int)record.Second.Y)
                    )
                );

                return;

            case (int)PlatformEventKind.WindowDpiChanged:
                events.Post(PlatformEvent.WindowDpiChanged(record.WindowId, timestamp, record.Value));
                return;

            case (int)PlatformEventKind.WindowFocusLost:
                // Every finger, before the focus event itself. A touch sequence interrupted by the
                // page losing focus never delivers its touchend, and an application never told a
                // finger lifted keeps dragging what it was dragging.
                foreach (var finger in touches.Clear()) {
                    events.Post(
                        PlatformEvent.Touch(
                            PlatformEventKind.TouchUp,
                            record.WindowId,
                            timestamp,
                            finger,
                            default
                        )
                    );
                }

                events.Post(PlatformEvent.Window(PlatformEventKind.WindowFocusLost, record.WindowId, timestamp));
                return;

            case (int)PlatformEventKind.DropFile:
            case (int)PlatformEventKind.DropText:
                events.Post(
                    PlatformEvent.Drop(
                        (PlatformEventKind)record.Kind,
                        record.WindowId,
                        timestamp,
                        WebInterop.TakeString(record.StringHandle),
                        record.First
                    )
                );

                return;

            case (int)PlatformEventKind.DisplaysChanged:
                events.Post(PlatformEvent.Application(PlatformEventKind.DisplaysChanged, timestamp));
                return;

            default:
                // Window shown/hidden/focus-gained/mouse-entered/mouse-left: identity and nothing
                // else, which is what PlatformEvent.Window is for.
                events.Post(PlatformEvent.Window((PlatformEventKind)record.Kind, record.WindowId, timestamp));
                return;
        }
    }
}
