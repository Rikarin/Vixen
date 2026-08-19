# Vixen.Platform.Web

A browser tab behind `IPlatform`: a canvas as the surface, `requestAnimationFrame` driving the frame
loop, pointer/keyboard/touch/gamepad input drained from one ring per frame, IndexedDB behind `/data`
and `/cache`, and `fetch` with HTTP range requests behind `/app`.

Spec: [docs/plan/10](../../docs/plan/10-platforms.md) § Web, whose measurements this is built
against, and [`spikes/web-webgl2/RESULT.md`](../../docs/plan/spikes/web-webgl2/RESULT.md), which
retired the WebGL2 unknown.

```csharp
var platform = await WebPlatform.CreateAsync(new() { CanvasSelector = "#view" });
var vfs = new VirtualFileSystem();
platform.FileSystem.MountStandardLocations(vfs);

var window = platform.CreateWindow(new() { Title = "Vixen", IsVisible = true });
using var loop = new WebFrameLoop();

loop.Start(timestamp => {
    foreach (var platformEvent in platform.PumpEvents()) {
        // ...
    }

    // ...update, render.
});

// Main returns here. The browser keeps calling back.
```

## Not in `Vixen.slnx`

This project targets `net10.0-browser`, which needs the `wasm-tools` workload even to *evaluate* —
and a solution that will not restore on a machine without it is a solution nobody can open. So it
sits on disk and out of the solution, exactly as `Vixen.Platform.Android`, `Vixen.Platform.iOS` and
`Vixen.Audio.Backend.WebAudio` do.

```bash
dotnet build Platform/Vixen.Platform.Web
```

`Vixen.Platform.Web.Tests` *is* in the solution, and covers three things without a browser:

- **The manifest reader**, in C#. The project targets `net10.0` and links the source files that touch
  no interop rather than referencing this one, which is the only way round the TFM.
- **The module URL**, in C#, for all three browser bindings at once — `BrowserModuleUrlTests`. Each
  binding's `DefaultModuleUrl` is resolved the way `JSHost.ImportAsync` resolves it, against the
  runtime's module in `_framework/`, and has to land on a `vixen-*.js` the project actually ships to
  the site root. All three were `./` and therefore unresolvable; the fix had no test until the
  constants were moved into `*Interop.Module.cs` files that hold nothing a browser is needed for.
- **`vixen-platform.js`**, in JavaScript, under Node against a DOM stub — the record layout, the HID
  key table, the wheel-unit conversion, the button-role mapping. Each of those is a translation that
  is wrong in a way no C# test can see. It runs as part of that project's build and fails it; no
  `package.json`, no install step.

  ```bash
  node Platform/Vixen.Platform.Web.Tests/js/vixen-platform.test.mjs
  ```

What genuinely needs a browser — IndexedDB, `fetch`, pointer lock, the IME — is for the Playwright
smoke test in doc 10's CI matrix, which does not exist yet. What needs only the *toolchain* is
covered: `nuke CompileWeb` builds all three browser projects and `nuke PublishWeb` publishes a head
and checks the page it produced has its entry point and its bindings where they are fetched from.
Both run on the `web` leg of `ci.yml`.

## The application head is `net10.0-browser` too

Not `net10.0` with `Sdk="Microsoft.NET.Sdk.WebAssembly"`, which is what the spike used and what an
earlier draft of this README said. A `net10.0` project cannot reference a `net10.0-browser` one —
`NU1201`, at restore — so the head takes the browser TFM and keeps the WebAssembly SDK:

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0-browser</TargetFramework>
    <OutputType>Exe</OutputType>
    <WasmMainJSPath>main.js</WasmMainJSPath>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="…/Vixen.Platform.Web/Vixen.Platform.Web.csproj" />
  </ItemGroup>
</Project>
```

Referencing this project by **package** brings `build/Vixen.Platform.Web.props` and `.targets` in
automatically. A head that references it by *path*, as the repository's samples do, imports them
itself — the two `<Import>`s go either side of the rest of the file, props first.

## The frame loop is the browser's

A WebAssembly `Main` that ran `while (running) { … }` would never return to the browser's event loop:
no DOM event would be delivered, no `fetch` would complete, and the tab would be reported unresponsive
within seconds. So the frame is a callback and `WebFrameLoop` owns it.

`requestAnimationFrame`, not a timer. It runs at the display's rate whatever that is, it is the point
at which the compositor will take a frame, and it stops entirely in a hidden tab. A `setInterval` loop
renders frames nobody sees and keeps a backgrounded tab's GPU busy, which is how a page gets throttled.

There is no API for the refresh rate — 120 Hz hardware is now common and assuming 60 is wrong on a lot
of machines — so `WebFrameLoop.RefreshRate` **measures** it, as the median interval over the last two
seconds. `IDisplayInfo` reports the same number.

## The canvas is addressed by a number

`SurfaceHandle` is two `nint`s and a discriminant, and a graphics backend gets nothing else: it does
not reference `Vixen.Platform`, by the layer rule in [docs/plan/00](../../docs/plan/00-vision-and-principles.md).
A canvas has no pointer to put in one, so the handle is a small integer and the selector is *derived*
from it:

```
[data-vixen-canvas="7"]
```

which is what `emscripten_webgl_create_context` and `canvas.getContext("webgpu")` both take.
`WebCanvas.SelectorFor` builds it for anything that happens to reference this assembly; a backend that
does not can concatenate the string. **That format is the contract between this project and every
browser backend** — `Vixen.Graphics.WebGPU` and `Vixen.Graphics.OpenGL`'s WebGL2 profile both depend
on it, which is why it is stated here rather than left as an implementation detail of the JavaScript.

## One drain per frame, not one call per event

Every DOM listener writes a fixed-width record of twelve doubles into a JavaScript ring;
`PumpEvents` copies the whole ring across in a single `JSType.MemoryView` call. The alternative — a
marshalled .NET delegate per DOM event — pays that cost for every `mousemove`, and a trackpad
produces those at the display's refresh rate whether or not anything is listening.

Strings cannot travel in a `Float64Array`, so a text-carrying event stores a *handle* and `PumpEvents`
pulls the string with a second call. Text events are rare; the extra call is paid where it does not
matter.

Translation into `PlatformEvent` happens in C#, including the finger bookkeeping `TouchTracker` exists
for and the gamepad diff — so both are testable without a browser and without a physical pad.

## Input, and what the browser will not give

**Keyboard.** `KeyboardEvent.code` and the USB HID keyboard page both name the *physical position*,
and `code` is specified in terms of the same US-QWERTY legends HID uses — so the map is a table, not
a guess, and `KeyQ` is `Key.Q` on an AZERTY keyboard even though it is labelled `A`.

**Wheel.** `deltaMode` differs between browsers for the same gesture: pixels in Chrome, lines in
Firefox, pages after a page-scroll key. All three are converted to notches here, so a caller never
sees the difference.

**Pointer lock and fullscreen** are only granted inside a user gesture. `CursorMode.Relative` and
`WindowMode.BorderlessFullscreen` are requests; reading them back tells you what the browser did.
`CursorMode.Confined` without a lock is not something a page may do — it would let a page trap the
pointer — so it is served as an ordinary cursor rather than silently locked.

**Text and the IME** need a real editable element; a canvas cannot host one. Text input puts an
invisible `<input>` over the caret, focuses it, and reads the composition events off it —
`SetCandidateArea` is what stops the candidate window opening in the corner of the screen. Keys are
forwarded from that element while it has focus, so `Escape` still closes the chat box.

**Gamepads** are polled, because the Gamepad API is. Rumble is `dual-rumble` where the pad and browser
have it; trigger motors are not exposed to a page at all.

**Clipboard** serves what the last `paste` event delivered, which is the only thing a browser will
ever let it have — `IClipboard`'s own documentation says so, and is synchronous for that reason.
Writing is asynchronous and `SetText` reports "this browser has the API and we asked it", which is the
strongest true statement a synchronous call can make.

**Dropped files** are named, not pathed: a drop hands over a `File` and no location on disk. The event
carries the name; `WebPlatform.ReadDroppedFileAsync` reads the bytes, indexed the same way as
`WebPlatform.DroppedFiles` and valid until the next pump.

## Everything is asynchronous, which is why there is a factory

`WebPlatform.CreateAsync`, not a constructor. `IFileProvider`'s `Exists`, `TryGetEntry` and
`Enumerate` are synchronous — deliberately, because every other provider answers them from a
directory, a dictionary or a bundle catalog. A browser has none of those and **cannot block to go and
look**: the WebAssembly runtime shares its thread with the event loop, so `.GetAwaiter().GetResult()`
does not wait for the fetch, it stops the fetch from ever completing.

So the JavaScript module is imported, IndexedDB is opened and the content manifest is read *before*
the platform exists, and every synchronous query afterwards is answered from memory. The synchronous
`OpenRead` is refused with a message naming `OpenReadAsync`, which is the behaviour that leads a
caller to the fix rather than to a hung tab.

### `/app` — `fetch`, with range requests

`FetchFileProvider` needs a manifest, and that is a precondition rather than an optimisation: HTTP has
no directory listing and no synchronous `HEAD`.

```json
[
  { "path": "/textures/atlas.ktx2", "length": 4194304, "modified": 1730000000000 },
  { "path": "/bundles/level1.vxb", "length": 83886080, "url": "level1.4f2c9e.vxb" }
]
```

`url` is for content-addressed builds — a CDN wants `level1.4f2c9e.vxb` with a far-future cache header
and the engine wants to keep asking for `/app/bundles/level1.vxb`.

Files over 256 KB come back as a `FetchStream`: seekable, fetching `Range: bytes=…` a megabyte at a
time as it is read. That is what makes streaming possible at all here — a bundle whose header says
where its entries are costs one request for the header and one for the entry, not eighty megabytes
before the first frame. Below the threshold a file is fetched whole, because a request per chunk loses
to its own round-trip cost.

A server that ignores `Range` answers `200` with the whole body instead of `206` with the slice, which
is legal; the JavaScript takes the slice, so the caller gets what it asked for either way and pays
only in bandwidth.

`FetchStream.Read` works only where the bytes are already resident, and says so rather than blocking.
`PrefetchAsync` is how code that must read synchronously arranges to be able to.

### `/data` and `/cache` — IndexedDB

Not `localStorage`, which is five megabytes of strings and blocks the compositor. Not Cache Storage,
which is the first thing evicted when an origin is over quota — precisely wrong for saves. The Origin
Private File System is the better answer, has a synchronous access handle, and is not in Safari on
iOS, which is where the storage limits bite hardest.

The **directory** — every key with its length and write time — is read once at mount and kept in
memory, so the synchronous queries can be answered. Values are read and written on demand.

Writes are visible immediately and durable shortly afterwards: closing a write stream updates the
in-memory directory synchronously, so `Exists` is true the instant you closed it, and starts the
IndexedDB put. **`await using` waits for the put; `using` only starts it.** `FlushAsync` waits for
everything outstanding and is what the platform calls when the tab is being hidden — which on the web
is the last moment there is, since a hidden tab may be discarded without another word.

`IndexedDbFileProvider.GetStorageEstimateAsync` is the number a bundle cache's eviction policy should
be written against. A cache that writes until it is refused is a cache that gets the whole origin
evicted, saves included.

### `/temp` — memory

Exactly right here. Temporary means "need not survive the session", a page's session ends when the tab
closes, and writing scratch data to storage the browser then has to evict is work for nobody.

## Threads: there are none, unless the page is cross-origin isolated

.NET threads on `browser-wasm` need `SharedArrayBuffer`, which needs COOP and COEP headers on every
response — a deployment fact the engine can read and never arrange. `IProcessorTopology.AvailableProcessors`
reports **1** unless `crossOriginIsolated` is true, whatever `navigator.hardwareConcurrency` says,
because a pool sized from the hardware count would try to start workers that throw.

`new JobScheduler()` picks zero workers on `browser-wasm` for the same reason. See
[Vixen.Core.Threading](../../Core/Vixen.Core.Threading/README.md) § "Zero workers is a supported
count": the graph, the slot ring and the batching are unchanged, and work runs when a thread reaches
`Complete`. The one behavioural difference is that scheduled-and-never-completed work never runs —
code that relies on it happening anyway has a bug on the web.

## Size

Measured on a head that creates the platform, mounts the file system and runs the loop, published
`Release`, Brotli, `_framework` only:

| | Brotli |
|---|---|
| Mono runtime — `dotnet.native.wasm`, `dotnet.native.js`, `dotnet.runtime.js`, `dotnet.js` | 554 KB |
| `System.Private.CoreLib` | 330 KB |
| the rest of the BCL the platform pulls in — `System.Runtime.InteropServices.JavaScript`, `System.Linq`, `System.Collections.Concurrent`, `System.Console`, `System.IO.Hashing` | 40 KB |
| `Vixen.Platform.Web` | 20 KB |
| `Vixen.Platform` + `Vixen.Core`, `.IO`, `.Mathematics`, `.Serialization` | 30 KB |
| the head itself | 4 KB |
| **total** | **978 KB** |

So the whole platform layer — this project and everything of Vixen's it needs — is **50 KB Brotli** on
top of a runtime that is 884 KB of it. The 930 KB doc 10 measured was a bare runtime plus a triangle,
so the two are not the same build and should not be subtracted from each other; what they agree on is
that the floor is the runtime and the engine's own IL is small against it.

`build/Vixen.Platform.Web.props` ships the settings that get there — `InvariantGlobalization`,
`PublishTrimmed` with `TrimMode=full`, the feature switches, `WasmEnableSIMD`, and Brotli at
`SmallestSize`. Two of those are worth calling out:

**`BrotliCompressionLevel` takes a `CompressionLevel` name, not a number.** Setting it to `11` — the
value Brotli's own documentation uses — does not fail the build. It writes a **zero-byte** `.br` for
every asset, and a server doing content negotiation then serves empty files to every browser that asks
for Brotli. There is no diagnostic; this was found by publishing and looking.

**`System.Text.Json` is not used, on purpose.** A source-generated context is trim-clean and is what
`Vixen.Shaders` uses. On a browser build it costs **59 KB Brotli** — six per cent of the payload — to
read one array of four-field objects once at start-up, so the manifest reader is hand-written. What it
does not implement is listed on `ManifestReader` and asserted in the tests.

## Lazy assemblies

Doc 10: *"930 KB is the runtime baseline; the engine's own IL adds to it. Lazy assembly loading and
splitting so a 2D/UI app does not download the 3D renderer remain worthwhile."*

```xml
<ItemGroup>
  <VixenWebLazyAssembly Include="Vixen.Rendering" />
</ItemGroup>
```

```csharp
if (settings.EnableThreeD) {
    await WebLazyAssemblies.LoadAsync("Vixen.Rendering");
}
```

The publish step takes each named assembly out of the boot manifest and republishes it under `_lazy/`.
Nothing downloads it until `LoadAsync` does.

Three things to know before deferring something:

- **A deferred assembly must still be referenced.** The trimmer runs first and removes what nothing
  reaches; an assembly reached only through `Assembly.Load` by name is one the trimmer deletes, and
  the publish then *fails saying so* rather than shipping a page that 404s on first use. Reach it
  through an interface in a referenced contracts assembly, or root it with `TrimmerRootAssembly`.
- **It runs interpreted**, even in an AOT build, exactly as Blazor's lazy loading does. Defer
  subsystems that are large and cold.
- **It ships as IL, not WebCIL**, because `AssemblyLoadContext.LoadFromStream` reads IL and the
  runtime's own loader is what understands WebCIL. A few per cent larger over the wire than the rest
  of the payload, against not downloading it at all.

The boot manifest is inlined into `_framework/dotnet.js` between `/*json-start*/` and `/*json-end*/`
markers, which is an SDK implementation detail. The publish task **errors** rather than guessing if
they are not there, because the alternative failure — a build that silently downloads everything and
still works — is one nobody would notice until a size budget did.
`VixenWebLazyAssembliesEnabled=false` publishes without the split.

## `vixen-platform.js`

The browser half. It has to be fetchable by URL at run time — `JSHost.ImportAsync` takes a path, not a
stream — so it is copied beside the assembly on build and packed as a content file. A page that
arranges its assets differently passes `WebPlatformOptions.ModuleUrl`.

## The WebGL2 flags

`build/Vixen.Platform.Web.targets` appends `-lGL -sMAX_WEBGL_VERSION=2 -sMIN_WEBGL_VERSION=2` to
`EmccExtraLDFlags`. This is the reason that file exists.

Omitting them does not error. `emscripten_webgl_create_context` silently returns a **WebGL 1** context,
the ES 3.00 shaders then fail to compile, and Silk.NET's `GetShaderInfoLog` throws
`ArgumentOutOfRangeException` instead of reporting the compile error — so the symptom is an exception
inside a diagnostic, several layers from the missing flag. Doc 10 measured this. A consumer must not be
able to get it wrong by forgetting a line.

## What is honestly absent

**Native dialogs.** A browser's file picker returns a *handle*, never a path, and `INativeDialogs`
returns `string` paths — returning a made-up one would produce something that looks like a file and
cannot be opened. A message box is `alert()`, which blocks the thread the WebAssembly runtime lives on:
that is not a dialog, it is a hang with a button on it. Drag-and-drop is the path that does work.

**Display enumeration.** A page cannot list monitors; the Window Management API exposes them behind a
permission prompt, which is not something to raise so a list can be longer. One display is reported and
`PlatformCapabilities.DisplayEnumeration` is absent so callers know.

**Window position, minimise, icons, attention.** A page is not told where its window is and cannot move
it. The tab's icon is the page's favicon and its HTML owns it. The browser's attention mechanism is a
notification, which needs a permission and reaches the desktop rather than the tab — an application's
decision, not something a window method should do behind one.

**Thermal state and low-power mode.** No API. The Battery Status API is gone from Firefox and Safari as
a fingerprinting surface, and where it is absent `IPowerInfo` reports the `null` it already models for
"will not say" rather than a plausible default a quality-scaling policy would then act on.

**Clipboard images out, and custom formats out.** Writing an image needs an encoded PNG blob, and a PNG
encoder in a runtime assembly is what ADR-015 keeps out of shipping builds. Custom formats need
`ClipboardItem`, whose accepted types are a short allow-list an application's own format is not on.

## Still to come

**A `WebTransport`/WebSocket transport** for `Vixen.Net` — the browser cannot open a UDP socket, so the
web build needs its own, and it is a `Vixen.Net.Transport.*` project rather than anything here.

**An `AudioWorklet` path** in `Vixen.Audio.Backend.WebAudio`, taken when the page *is* cross-origin
isolated. It would cut that backend's 40 ms queue to a couple of milliseconds.

**The Playwright smoke test**, which is what would cover the `[JSImport]` calls themselves. Doc 10's
CI matrix has the leg; it does not exist yet. ⚠ When it is written it must drive a real browser over
CDP: `chrome-headless-shell --dump-dom` never fires `requestAnimationFrame` — measured, with and
without `--virtual-time-budget`, `--screenshot` and SwiftShader — so a leg built on it would report a
live frame loop as dead. `docs/plan/spikes/web-head/drive.mjs` is the shape that works.

Licensed under Apache-2.0.
