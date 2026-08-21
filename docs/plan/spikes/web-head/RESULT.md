# Spike: can a Vixen application head run in a browser?

**Answer: the platform layer can, and does — 400 frames at a measured 120 Hz in headless Chromium.
Nothing above the platform layer can, because no sample is portable and no build verb produces a
page.**

`web-webgl2` retired the "will Silk.NET's GLES binding work on `browser-wasm`" unknown. This one
retires a different one: *is there a path from this repository to a running browser page at all?*
It is a probe, not a sample — it draws nothing, and says so.

> **The head has been promoted; this directory is a document again.** The spike's head became
> [`Tools/Vixen.WebProbe`](../../../../Tools/Vixen.WebProbe/README.md), which is what `nuke
> PublishWeb` publishes and asserts about. It was load-bearing from here for a while — a build
> target depending on a spike — and that is what the move ends. Everything below is the record of
> what running it established; the commands under *Running it* are the promoted paths.

## What it establishes

| | |
|---|---|
| `wasm-tools` workload | present; `net10.0-browser` evaluates |
| A `net10.0-browser` head with `Sdk="Microsoft.NET.Sdk.WebAssembly"` | builds, and emcc relinks with `-lGL -sMAX_WEBGL_VERSION=2 -sMIN_WEBGL_VERSION=2` |
| `WebPlatform.CreateAsync` | returns a live platform |
| `platform.CreateWindow` | gives a `SurfaceKind.Web` surface |
| `WebCanvas.TryGetSelector` | `[data-vixen-canvas="1"]` — the documented contract holds |
| `WebFrameLoop` | ran 400+ frames; `RefreshRate` settled to **120.5 Hz** against an independent 120/s `rAF` probe |
| `IProcessorTopology.AvailableProcessors` | **10** under COOP/COEP, **1** without — the README's claim, confirmed both ways |
| `BrowserWebGpuBinding.CreateAsync` | module imports, reaches `navigator.gpu.requestAdapter` |
| A WebGPU **device** | ✗ not obtained — no adapter in headless Chromium on macOS, at any flag combination tried |

## Three things it found by running that no build catches

**1. The default module URL could never resolve.** `JSHost.ImportAsync` resolves a relative URL
against the *runtime's* module in `_framework/`, not against the page; the three `vixen-*.js` files
are content files and land at the site root. `"./vixen-platform.js"` therefore asked for
`_framework/vixen-platform.js` and surfaced as a `TypeError` about a dynamically imported module,
thrown from inside `WebPlatform.CreateAsync`. Fixed to `"../"` in all three bindings. There is no
build-time diagnostic; this is only visible by publishing and loading the page.

**2. `dotnet.run()` exits the runtime when `Main` returns, which kills the frame loop.** The
`Vixen.Platform.Web` README's model — *"Main returns here. The browser keeps calling back"* — is
correct about the design and silent about the one line that makes it true. `dotnet.run()` tears the
runtime down the moment `Main` completes, and every `requestAnimationFrame` callback
`WebFrameLoop` registered dies with it; the page reports
`Assert failed: .NET runtime already exited with 0`. A head must use `runtime.runMain()`, as
`wwwroot/main.js` here does. Nothing in `build/Vixen.Platform.Web.props` enforces or documents this.

**3. `WasmMainJSPath` is not a static web asset.** A `main.js` at the project root is *not*
published to `wwwroot`, so the page 404s on its own entry point and nothing at all happens. It has
to live under `wwwroot/`.

## Running it

```bash
dotnet publish Tools/Vixen.WebProbe/Vixen.WebProbe.csproj -c Release

node Tools/Vixen.WebProbe/serve.mjs \
    Tools/Vixen.WebProbe/bin/Release/net10.0-browser/publish/wwwroot 8099
```

The server sets `Cross-Origin-Opener-Policy`/`Cross-Origin-Embedder-Policy`; drop them to see
`AvailableProcessors` fall to 1.

Then, in another shell — **the full browser over CDP, not `--dump-dom`**:

```bash
npm install --prefix Tools/Vixen.WebProbe playwright-core

chrome-headless-shell --headless --no-sandbox --enable-unsafe-swiftshader \
    --remote-debugging-port=9223 --user-data-dir=/tmp/vixen-cdp about:blank &

node Tools/Vixen.WebProbe/drive.mjs http://localhost:8099/index.html 6000
```

⚠ **`chrome-headless-shell --dump-dom` never fires `requestAnimationFrame`** — with or without
`--virtual-time-budget`, `--screenshot`, or SwiftShader. A pure-JS control page counted **zero**
callbacks in three seconds. Every frame-loop measurement here is therefore taken over CDP, where the
same control page counts 120/s. A Playwright CI leg must not be built on `--dump-dom`: it would
report a dead frame loop as a dead engine, or a broken one as fine.

## What it does not establish

**No pixel was drawn.** No WebGPU adapter was obtainable in headless Chromium on this machine, so
`WebGpuDevice` was never constructed and no swapchain, pass or draw was exercised. The binding is
proven to load and to reach `requestAdapter`; everything past that is untested on the web. A CI leg
on Linux with `--enable-features=Vulkan` and a software Vulkan ICD is the way to close this.

**No sample runs.** `01-HelloTriangle` is the wrong first candidate: it is deliberately
backend-specific — `using Vixen.Graphics.Vulkan`, `VulkanDevice.Create`, and embedded `.spv` — which
is exactly what makes it a platform smoke test and exactly what makes it unportable. A first web
sample needs a backend-agnostic game plus WGSL, not a head.

**Nothing produces `content/manifest.json`.** `MountContent = false` here for that reason.
