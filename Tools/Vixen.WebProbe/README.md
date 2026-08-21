# Vixen.WebProbe

The browser head this repository owns, and the subject of `nuke PublishWeb`. Not a sample and not a
tool anybody runs for its own sake — an application head that exists so that the browser gate has a
real page to publish and assert about, and so that a person debugging the web path has something to
load.

Spec: [docs/plan/10](../../docs/plan/10-platforms.md) § Web,
[docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 10, and
[docs/plan/17](../../docs/plan/17-app-heads-and-shipping.md) § Project templates.

Findings: [docs/plan/spikes/web-head/RESULT.md](../../docs/plan/spikes/web-head/RESULT.md), which is
the spike this head was promoted out of.

```bash
./build.sh CompileWeb PublishWeb --configuration Release
```

## Why a head exists at all, when three libraries already compile

`nuke CompileWeb` builds `Vixen.Platform.Web`, `Vixen.Graphics.WebGPU.Browser` and
`Vixen.Audio.Backend.WebAudio`, and **a library never evaluates
`Platform/Vixen.Platform.Web/build/Vixen.Platform.Web.props` or `.targets`**. Those apply to the head
that consumes them, so the emcc relink, the WebGL2 minimum, the trimming profile and the
static-web-asset layout are all untouched by a compile. Every one of the three defects the spike
found lives in exactly that gap:

1. **The default module URL could never resolve.** `JSHost.ImportAsync` resolves a relative URL
   against the *runtime's* module in `_framework/`, not against the page, while the three
   `vixen-*.js` content files land at the site root. Fixed to `"../"` in all three bindings, and
   asserted from both sides: `BrowserModuleUrlTests` knows the constants, `PublishWeb` knows where
   the SDK actually put the files.
2. **`dotnet.run()` exits the runtime when `Main` returns**, killing every `requestAnimationFrame`
   callback `WebFrameLoop` registered. `wwwroot/main.js` uses `runtime.runMain()`; `PublishWeb`
   checks the *shape* of that in the published file, and only a loaded page can check the
   behaviour — see below.
3. **`WasmMainJSPath` at the project root is not a static web asset**, so the page 404s on its own
   entry point and nothing at all happens, with no build error.

## It is a probe: it draws nothing, and says so

`Program.cs` stands the platform up, asks for a window, reads the processor count, tries to reach
`navigator.gpu`, and runs a frame loop that counts. It prints `VIXENPROBE …` lines and paints them
into the page. **No pixel is drawn**: no WebGPU adapter was obtainable in headless Chromium on
macOS at any flag combination tried, so `WebGpuDevice` has never been constructed on the web. Closing
that needs a Linux job with `--enable-features=Vulkan` over a software Vulkan ICD.

`MountContent` is `false` because nothing in this repository produces the `content/manifest.json`
that `FetchFileProvider` requires.

**A first web *sample* is a different, larger thing** and is still owed. `Samples/01-HelloTriangle`
is deliberately backend-specific — `using Vixen.Graphics.Vulkan`, `VulkanDevice.Create`, embedded
`.spv` — which is what makes it a platform smoke test and what makes it unportable. A web sample
needs a backend-agnostic game plus WGSL, not a head.

## Loading it by hand

```bash
dotnet publish Tools/Vixen.WebProbe/Vixen.WebProbe.csproj -c Release

node Tools/Vixen.WebProbe/serve.mjs \
    Tools/Vixen.WebProbe/bin/Release/net10.0-browser/publish/wwwroot 8099
```

`serve.mjs` sets `Cross-Origin-Opener-Policy`/`Cross-Origin-Embedder-Policy`; drop them to watch
`IProcessorTopology.AvailableProcessors` fall from 10 to 1.

Then, in another shell — **the full browser over CDP, not `--dump-dom`**:

```bash
npm install --prefix Tools/Vixen.WebProbe playwright-core

chrome-headless-shell --headless --no-sandbox --enable-unsafe-swiftshader \
    --remote-debugging-port=9223 --user-data-dir=/tmp/vixen-cdp about:blank &

node Tools/Vixen.WebProbe/drive.mjs http://localhost:8099/index.html 6000
```

⚠ **`chrome-headless-shell --dump-dom` never fires `requestAnimationFrame`** — with or without
`--virtual-time-budget`, `--screenshot` or SwiftShader; a pure-JS control page counted **zero**
callbacks in three seconds, while the same page over CDP counts 120/s. The Playwright CI leg
[doc 10](../../docs/plan/10-platforms.md) asks for is **owed**, and must not be built on `--dump-dom`:
it would report a live frame loop as dead.

## Why it is not in `Vixen.slnx`

`net10.0-browser` cannot be *evaluated* without the `wasm-tools` workload — not built, evaluated — so
a solution containing this project would not restore for anyone who has not installed it. That is the
same reason `Vixen.Platform.Web` and its two siblings are absent, and the same reason
`../Vixen.AotProbe.iOS` is.

The cost is stated rather than hidden: `Test`, `CheckFormat`, `CheckApi` and `Pack` do not see this
project. `CheckArchitecture` **does**, because it globs `Tools/**/*.csproj` rather than reading the
solution.

Licensed under Apache-2.0.
