// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Web;

/// <summary>The frame loop, driven by <c>requestAnimationFrame</c>.</summary>
/// <remarks>
///     <para>
///         <b>A browser application does not own its main loop, and this is where that becomes
///         visible.</b> Every other platform runs <c>while (running) { Pump(); Update(); Render(); }</c>
///         from <c>Main</c>. A WebAssembly <c>Main</c> that did the same would never return to the
///         browser's event loop, so no DOM event would ever be delivered, no <c>fetch</c> would ever
///         complete, and the tab would be reported as unresponsive within a few seconds. The frame
///         is a callback, and the loop is the browser's.
///     </para>
///     <para>
///         <b><c>requestAnimationFrame</c> and not a timer.</b> It runs at the display's rate
///         whatever that is — 60 Hz, 120 Hz, or the 24 Hz a browser drops to on battery — it is the
///         point at which the compositor will actually take a frame, and it stops entirely in a
///         hidden tab. A <c>setInterval</c> loop renders frames nobody sees and keeps a backgrounded
///         tab's GPU busy, which is what gets a page throttled.
///     </para>
///     <para>
///         The callback is handed the browser's own timestamp, converted to the
///         <see cref="System.Diagnostics.Stopwatch" /> ticks the rest of the engine measures in by
///         <see cref="WebClock" />, so frame timing and event timestamps are on one clock.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     var platform = await WebPlatform.CreateAsync();
///     var window = platform.CreateWindow(new() { Title = "Vixen", IsVisible = true });
///     using var loop = new WebFrameLoop();
///
///     loop.Start(_ =&gt; {
///         foreach (var platformEvent in platform.PumpEvents()) {
///             // ...
///         }
///
///         // ...update, render.
///     });
///
///     // Main returns here. The browser keeps calling back.
///     </code>
/// </example>
[SupportedOSPlatform("browser")]
public sealed class WebFrameLoop : IDisposable {
    Action<long>? onFrame;
    bool running;
    bool disposed;

    /// <summary>Whether the loop is running.</summary>
    public bool IsRunning => running;

    /// <summary>How many frames have been delivered since <see cref="Start" />.</summary>
    public long FrameCount { get; private set; }

    /// <summary>When the last frame started, in <see cref="System.Diagnostics.Stopwatch" /> ticks.</summary>
    public long LastFrameTimestamp { get; private set; }

    /// <summary>
    ///     The display's refresh rate as measured from the intervals between callbacks, or <c>0</c>
    ///     until enough frames have gone by to be sure.
    /// </summary>
    /// <remarks>
    ///     There is no API for this — a page is not told what its display runs at — and 120 Hz
    ///     hardware is common enough that assuming 60 makes a frame-pacing decision wrong on a lot
    ///     of machines. The median of the last two seconds' intervals, so a single garbage
    ///     collection or shader compile does not drag the answer.
    /// </remarks>
    public double RefreshRate => running ? WebInterop.RefreshRate() : 0;

    /// <summary>Starts calling <paramref name="frame" /> once per displayed frame.</summary>
    /// <param name="frame">
    ///     The frame, given the browser's timestamp as <see cref="System.Diagnostics.Stopwatch" />
    ///     ticks.
    /// </param>
    /// <exception cref="InvalidOperationException">The loop is already running.</exception>
    /// <remarks>
    ///     An exception thrown out of <paramref name="frame" /> would cross into JavaScript, where
    ///     it becomes an unhandled rejection and the next frame is scheduled anyway — so a broken
    ///     frame would repeat sixty times a second, forever, filling the console. It is caught, the
    ///     loop is stopped, and the exception is rethrown on the browser's task queue so a
    ///     debugger and <c>window.onerror</c> both see it once.
    /// </remarks>
    public void Start(Action<long> frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (running) {
            throw new InvalidOperationException("The frame loop is already running.");
        }

        onFrame = frame;
        running = true;
        FrameCount = 0;

        WebInterop.StartFrameLoop(Tick);
    }

    /// <summary>Stops the loop. The browser stops calling back after the current frame.</summary>
    public void Stop() {
        if (!running) {
            return;
        }

        running = false;
        onFrame = null;
        WebInterop.StopFrameLoop();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Stop();
    }

    void Tick(double milliseconds) {
        if (!running) {
            return;
        }

        FrameCount++;
        LastFrameTimestamp = WebClock.FromBrowser(milliseconds);

        try {
            onFrame?.Invoke(LastFrameTimestamp);
        } catch (Exception exception) {
            Stop();

            // Rethrown from the browser's queue rather than from here, so it does not unwind
            // through the interop boundary — where it would arrive as an opaque rejection with no
            // managed stack on it.
            _ = Task.Run(() => throw new InvalidOperationException(
                "The frame callback threw, so the loop has been stopped. See the inner exception.",
                exception
            ));
        }
    }
}
