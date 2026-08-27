// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Graphics;
using Vixen.Graphics.WebGPU;
using Vixen.Graphics.WebGPU.Browser;
using Vixen.Platform;
using Vixen.Platform.Web;

// The browser head `nuke PublishWeb` publishes, and the subject `nuke BrowserSmoke` drives.
//
// ── What this file is for ───────────────────────────────────────────────────────────────────
//
// It is the only place in the repository where a [JSImport] is actually CALLED. Everything else
// covers the boundary from one side: `CompileWeb` compiles the declarations, `BrowserModuleUrlTests`
// knows the module-URL constants, `PublishWeb` knows where the files landed, and
// js/vixen-platform.test.mjs tests the JavaScript half against a DOM stub. None of them executes a
// marshalled call, and a marshalled call is where the interesting failures are: a MemoryView whose
// two sides disagree about a record's width, an async import that resolves to a 404, a callback
// signature the runtime rejects at first invocation.
//
// So every check below goes through the public Vixen.Platform.Web API and out across the boundary,
// and each one prints a line the driver parses:
//
//     VIXENPROBE check <name> pass|fail <detail>
//
// ⚠ The count is printed on the terminal line and asserted by the driver against what it actually
// saw. A probe that quietly stopped running half its checks would otherwise report a shorter,
// greener run — which is the failure this repository has been bitten by twice (a comparator that
// called three empty manifests identical, and eighteen goldens that passed without a device).
//
// It still draws nothing. No WebGPU adapter has ever been obtainable in headless Chromium here, so
// `WebGpuDevice` has never been constructed on the web; that is reported as an observation and is
// deliberately not a failing check. See README.md.

var checks = 0;
var failures = 0;
var reportedChecks = new HashSet<string>(StringComparer.Ordinal);

void Check(string name, bool ok, string detail) {
    checks++;
    reportedChecks.Add(name);

    if (!ok) {
        failures++;
    }

    Console.WriteLine($"VIXENPROBE check {name} {(ok ? "pass" : "fail")} {detail}");
}

void Failed(string name, Exception exception) =>
    Check(name, false, $"threw {exception.GetType().Name}: {exception.Message}");

// ── The platform, which is the module import and `initialise` ────────────────────────────────
//
// CreateAsync is JSHost.ImportAsync against WebInterop.DefaultModuleUrl followed by
// WebInterop.Initialise(). If the URL is wrong this throws here and the page never prints another
// line — which is defect 1 (docs/plan/spikes/web-head/RESULT.md), the one that shipped for months
// with no gate able to see it.
var platform = await WebPlatform.CreateAsync(new() { CanvasSelector = "#view", MountContent = false });

Check("platform-created", platform.Name == "Web (browser)", platform.Name);

// ── The canvas: createCanvas, canvasSelector, and the geometry readers ───────────────────────

var window = platform.CreateWindow(new() { Title = "Vixen smoke", IsVisible = true });

Check(
    "surface-is-a-canvas",
    window.Surface.Handle.Kind == SurfaceKind.Web,
    window.Surface.Handle.Kind.ToString()
);

var haveSelector = WebCanvas.TryGetSelector(window.Surface.Handle, out var canvasSelector);

// ⚠ NOT `== "#view"`, and finding that out was the first thing this leg said about itself.
// WebCanvas.SelectorFor answers with the attribute selector it stamps on the element —
// `[data-vixen-canvas="1"]` — because a canvas the platform created has no id to speak of and a
// handle-derived selector is the only one that is true for both cases. Asserting the page's own id
// here was an assertion about the probe's markup dressed up as one about the engine.
//
// So: the page checks that a selector came back at all, and the DRIVER resolves it in the DOM and
// requires it to be the same element as #view. That is the claim worth making — the selector
// managed code was handed actually addresses the canvas managed code asked for — and only
// something outside the page can make it.
Check("canvas-selector", haveSelector && canvasSelector.Length > 0, canvasSelector);
Console.WriteLine($"VIXENPROBE observe canvas-selector {canvasSelector}");

var clientSize = window.ClientSize;

// The size index.html gives the canvas, read back through clientWidth/clientHeight. A number that
// crossed the boundary and is checked against something a person wrote in the HTML.
Check("canvas-client-size", clientSize is { X: 320, Y: 240 }, $"{clientSize.X}x{clientSize.Y}");

var framebuffer = window.FramebufferSize;

Check(
    "canvas-pixel-size",
    framebuffer.X > 0 && framebuffer.Y > 0,
    $"{framebuffer.X}x{framebuffer.Y}"
);

Check("canvas-dpi-scale", window.DpiScale > 0, window.DpiScale.ToString("0.###"));

window.Show();
Check("canvas-visible", window.IsVisible, window.IsVisible.ToString());

window.Focus();
Check("canvas-focused", window.IsFocused, window.IsFocused.ToString());

// setTitle sets document.title, which the driver reads back over CDP — the one check whose two
// ends are in different processes.
window.Title = "vixen-smoke-title";
Check("window-title-set", window.Title == "vixen-smoke-title", window.Title);

// ── Navigator and screen ─────────────────────────────────────────────────────────────────────

Check(
    "processors",
    platform.Processors.AvailableProcessors >= 1,
    platform.Processors.AvailableProcessors.ToString()
);

// ⚠ This one is about the SERVER, not the browser. WebProcessors.AvailableProcessors is hard 1
// unless crossOriginIsolated, which needs COOP and COEP on the response — so this fails if
// browser-smoke.mjs ever stops sending those headers, which is the difference between the engine
// seeing every core and seeing one.
//
// ⚠ And it is an inference, because WebProcessors is internal and IProcessorTopology has no
// IsCrossOriginIsolated. A genuinely single-core machine would report 1 while isolated and fail
// this. The driver therefore asserts globalThis.crossOriginIsolated directly as well, and that
// check is the authority; this one is here because it is the value the engine will actually use.
Check(
    "processors-see-isolation",
    platform.Processors.AvailableProcessors > 1,
    $"{platform.Processors.AvailableProcessors} available of "
    + $"{platform.Processors.PhysicalCores} reported by the browser"
);

var display = platform.Displays.Primary;

Check(
    "display-bounds",
    display is { Bounds.Width: > 0, Bounds.Height: > 0 },
    display is null ? "no primary display" : $"{display.Bounds.Width}x{display.Bounds.Height}"
);

// ── Strings across the boundary, both ways ───────────────────────────────────────────────────
//
// The module keeps its own clipboard mirror, so this is a round trip through setClipboardText and
// clipboardText without needing a permission prompt a headless run cannot answer.
try {
    const string sentinel = "vixen — smoke ✓";
    platform.Clipboard.SetText(sentinel);
    var readBack = platform.Clipboard.TryGetText(out var text) ? text : null;

    Check("clipboard-round-trip", readBack == sentinel, readBack ?? "(nothing)");
} catch (Exception exception) {
    Failed("clipboard-round-trip", exception);
}

// ── Text input ───────────────────────────────────────────────────────────────────────────────

try {
    platform.TextInput.Activate(window);
    var active = platform.TextInput.IsActive;
    platform.TextInput.Deactivate();

    Check("text-input-activates", active && !platform.TextInput.IsActive, $"active={active}");
} catch (Exception exception) {
    Failed("text-input-activates", exception);
}

// ── fetch: Task<int>, a buffer handle, and a MemoryView read ─────────────────────────────────
//
// FetchFileProvider under 256 KB is fetchAll → bufferLength → readBuffer(MemoryView) →
// releaseBuffer, which is the whole asynchronous buffer-handle dance WebInterop's remarks describe
// and which nothing else in the repository executes. The subject is the page's own index.html,
// because it is the one file this head is guaranteed to be served next to.
try {
    var manifest = WebContentManifest.Parse(
        "[{\"path\":\"/index.html\",\"length\":4096}]"u8
    );

    var content = new FetchFileProvider(string.Empty, manifest);

    await using var stream = await content.OpenReadAsync(new VirtualPath("/index.html"));
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var html = await reader.ReadToEndAsync();

    Check(
        "fetch-reads-bytes",
        html.Contains("<canvas", StringComparison.Ordinal),
        $"{html.Length} chars, canvas present: {html.Contains("<canvas", StringComparison.Ordinal)}"
    );
} catch (Exception exception) {
    Failed("fetch-reads-bytes", exception);
}

// ── IndexedDB: stageBuffer, an async put, an async get, a delete ─────────────────────────────
//
// The other direction of the buffer-handle dance — a Span<byte> staged synchronously because the
// marshaller rejects a MemoryView on a Task-returning method outright (SYSLIB1072), then put
// asynchronously. Written, read back byte for byte, then deleted so a rerun in the same profile
// starts clean.
try {
    var store = await IndexedDbFileProvider.OpenAsync("vixen-smoke");
    var path = new VirtualPath("/smoke.bin");
    var written = new byte[] { 0x56, 0x49, 0x58, 0x45, 0x4E, 0x00, 0xFF, 0x7F };

    await using (var output = await store.OpenWriteAsync(path)) {
        await output.WriteAsync(written);
    }

    await store.FlushAsync();
    await store.RefreshAsync();

    byte[] readBack;

    await using (var input = await store.OpenReadAsync(path)) {
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        readBack = buffer.ToArray();
    }

    Check(
        "indexeddb-round-trip",
        readBack.AsSpan().SequenceEqual(written),
        $"{readBack.Length} bytes back of {written.Length}"
    );

    var deleted = store.Delete(path);
    await store.FlushAsync();

    Check("indexeddb-delete", deleted, deleted.ToString());

    var (usage, quota) = await IndexedDbFileProvider.GetStorageEstimateAsync();
    Check("storage-estimate", quota > 0, $"usage {usage}, quota {quota}");

    store.Dispose();
} catch (Exception exception) {
    // ⚠ Whatever is left, not just the first. The driver asserts the number of checks it saw
    // against a fixed floor, and a catch block that collapses three checks into one turns a failure
    // into a SHORTER run — which is the shape that has to be impossible here. Every path through
    // this block leaves exactly three lines behind it.
    foreach (var name in new[] { "indexeddb-round-trip", "indexeddb-delete", "storage-estimate" }) {
        if (!reportedChecks.Contains(name)) {
            Failed(name, exception);
        }
    }
}

// ── Graphics, which is an observation and not a check ────────────────────────────────────────
//
// ⚠ Deliberately NOT a check. No WebGPU adapter has been obtainable in headless Chromium at any
// flag combination tried, so making this a check would make the leg red on every machine and teach
// everyone to ignore it. Closing it needs a Linux job with --enable-features=Vulkan over a software
// Vulkan ICD, which nobody has watched come up. Reported so the transcript says what happened.
try {
    var binding = await BrowserWebGpuBinding.CreateAsync(new() { CanvasSelector = canvasSelector });
    IGraphicsDevice device = new WebGpuDevice(binding);

    Console.WriteLine(
        $"VIXENPROBE observe gpu adapter={binding.AdapterInfo.Name} device={device.GetType().Name}"
    );
} catch (Exception exception) {
    Console.WriteLine($"VIXENPROBE observe gpu-unavailable {exception.GetType().Name}: {exception.Message}");
}

// ── The loop, and the events that arrive through it ──────────────────────────────────────────
//
// Everything above ran before Main returned. Everything below runs on the browser's own callbacks,
// which is the half `dotnet.run()` used to kill (defect 2) and which `nuke PublishWeb` can only
// guess at by reading the boot script's shape.
//
// The driver waits for the line below before it synthesises any input: a signal the page emits,
// rather than a sleep, because a probe that is still loading a 40 MB runtime when the driver stops
// looking is the flakiest shape this leg could have.

var kindsSeen = new HashSet<PlatformEventKind>();
var frames = 0;
var reported = false;
var resizeRequested = false;
var resizeObserved = false;

var loop = new WebFrameLoop();

loop.Start(_ => {
    frames++;

    // ⚠ Reported rather than rethrown. WebFrameLoop catches an exception out of the callback,
    // STOPS the loop, and rethrows it on the browser's task queue — where main.js's
    // unhandledrejection handler writes it into #result and never calls console.log. That is how
    // the first real run of this leg presented: eighteen checks, then silence, no frame lines and
    // no exception anywhere in the console. The cause was `view.fill is not a function` inside
    // pollGamepads, thrown on frame one. Naming it in the transcript costs nothing and saves the
    // next person that hour.
    try {
        foreach (ref readonly var platformEvent in platform.PumpEvents()) {
            kindsSeen.Add(platformEvent.Kind);
        }
    } catch (Exception exception) {
        Console.WriteLine($"VIXENPROBE observe pump-threw {exception.GetType().Name}: {exception.Message}");
        throw;
    }

    // A weak request that the page's layout answers asynchronously through a ResizeObserver, so it
    // can only be checked over frames — which is exactly why it is checked here and not above.
    if (frames == 5 && !resizeRequested) {
        resizeRequested = true;
        window.ClientSize = new(400, 300);
    }

    if (resizeRequested && window.ClientSize is { X: 400, Y: 300 }) {
        resizeObserved = true;
    }

    // ⚠ Frame 1 as well as every 10th, and BOTH numbers were bought with a red leg.
    //
    // Frame 1, because the driver takes a baseline from these lines: the Linux CI runner reaches
    // its input and prints `done` at frame TWELVE where this Mac takes until frame 52, so with a
    // 30-frame cadence alone the baseline did not exist yet on the slower machine and the driver
    // read a sentinel. Nothing was wrong with the engine.
    //
    // Every 10th rather than every 30th, because the driver has to see the count MOVE, and how
    // long that takes is the cadence divided by the frame rate. At 30 frames a run below about
    // 30 fps could go a whole second without crossing a boundary and look stopped — and the rAF
    // the driver measured for itself on that Linux runner was 38/s. The driver no longer waits a
    // fixed second either, but a cadence that is small next to the rate is what keeps the two
    // independent.
    if (frames == 1 || frames % 10 == 0) {
        // The driver reads this twice, a second apart, and requires it to have moved. A loop that
        // ran once and stopped reports a number and then reports it forever.
        //
        // `rate` is WebFrameLoop.RefreshRate, which is refreshRate() across the boundary: the
        // median of the last two seconds' intervals, and 0 until ten of them have gone by. So it
        // is legitimately 0 on the frame-1 line, and the driver reads it off the LAST line rather
        // than the first.
        Console.WriteLine($"VIXENPROBE frames={frames} rate={loop.RefreshRate:0.#}");
    }

    if (reported) {
        return;
    }

    var sawInput = kindsSeen.Contains(PlatformEventKind.MouseMoved)
        && kindsSeen.Contains(PlatformEventKind.MouseButtonDown)
        && kindsSeen.Contains(PlatformEventKind.KeyDown);

    // ⚠ A frame budget rather than an indefinite wait, so that input which never arrives is a
    // legible failing check instead of the driver's timeout. 300 frames is about five seconds at
    // 60 Hz and about twelve at the 24 Hz a throttled tab drops to.
    if (!sawInput && frames < 300) {
        return;
    }

    reported = true;

    Check(
        "events-drained",
        sawInput,
        sawInput
            ? string.Join(",", kindsSeen.Order())
            : $"after {frames} frames saw only: {string.Join(",", kindsSeen.Order())}"
    );

    // ⚠ Inside the canvas, NOT at the viewport point the driver dispatched. The driver aims at
    // (42, 24) in viewport coordinates and the engine reports (34, 16), because a pointer position
    // is relative to the canvas and the canvas sits 8 px in — the body's default margin. Asserting
    // the dispatched number here would have been asserting that the translation does NOT happen.
    // The page checks the answer is on the canvas; the driver checks the arithmetic, because only
    // it can read the element's rectangle.
    var pointer = platform.Input.PointerPosition;

    Check(
        "pointer-position-in-canvas",
        pointer.X >= 0 && pointer.Y >= 0
        && pointer.X <= window.ClientSize.X && pointer.Y <= window.ClientSize.Y,
        $"{pointer.X},{pointer.Y} within {window.ClientSize.X}x{window.ClientSize.Y}"
    );

    Console.WriteLine($"VIXENPROBE observe pointer-position {pointer.X},{pointer.Y}");

    Check("canvas-resize-observed", resizeObserved, $"{window.ClientSize.X}x{window.ClientSize.Y}");

    Check("frame-loop-is-running", loop.IsRunning && frames > 0, $"{frames} frames");

    Check("frame-count-agrees", loop.FrameCount == frames, $"{loop.FrameCount} vs {frames}");

    Console.WriteLine($"VIXENPROBE done checks={checks} failed={failures}");
});

// The line the driver waits for before it synthesises input. It is printed after Start, so a loop
// that refused to start never gets here and the driver reports a timeout with the transcript.
Console.WriteLine("VIXENPROBE ready-for-input");
