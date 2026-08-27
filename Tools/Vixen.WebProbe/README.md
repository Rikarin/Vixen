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
./build.sh CompileWeb PublishWeb BrowserSmoke --configuration Release
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
   behaviour — which `nuke BrowserSmoke` now does, by reading the frame count out of the page twice
   a second apart and requiring it to have **moved**.
3. **`WasmMainJSPath` at the project root is not a static web asset**, so the page 404s on its own
   entry point and nothing at all happens, with no build error.

## It is a probe: it draws nothing, and says so

`Program.cs` stands the platform up, asks for a window, and then **calls `[JSImport]`s** — which is
the whole point of it and the thing nothing else in this repository does. It prints one line per
check:

```
VIXENPROBE check canvas-selector pass #view
VIXENPROBE check indexeddb-round-trip pass 8 bytes back of 8
VIXENPROBE done checks=23 failed=0
```

`browser-smoke.mjs` parses those, adds twelve of its own, and fails the build if any of the
thirty-five did not pass — or if fewer than thirty-five ran at all. What each check reaches is set
out in `Program.cs`; between them they execute every marshalling shape the boundary has: a `void`
call, primitives, strings in both directions, `[JSMarshalAs<JSType.MemoryView>]` in both directions,
a `JSType.Function` callback, and `Task<T>` with the buffer-handle dance on either side of it.

**No pixel is drawn**: no WebGPU adapter was obtainable in headless Chromium on macOS at any flag
combination tried, so `WebGpuDevice` has never been constructed on the web. That is reported as
`VIXENPROBE observe gpu-unavailable …` and is deliberately **not** a failing check — making it one
would make the leg red on every machine anyone has, which is how a gate gets ignored. Closing it
needs a Linux job with `--enable-features=Vulkan` over a software Vulkan ICD.

`MountContent` is `false` because nothing in this repository produces the `content/manifest.json`
that `FetchFileProvider` requires — the fetch check builds a one-entry manifest by hand instead and
reads the page's own `index.html` back through `FetchFileProvider`.

## The gate: `nuke BrowserSmoke`

```bash
./build.sh BrowserSmoke --configuration Release
```

It publishes the head, serves it on an ephemeral loopback port with `Cross-Origin-Opener-Policy`
and `Cross-Origin-Embedder-Policy`, launches the Chrome it can find, drives it over CDP, and asserts
thirty-five things. `build/Build.BrowserSmoke.cs` carries the reasoning; the short version:

- **No Playwright, no npm install, no vendored binary.** `playwright-core` would be a third-party
  dependency that `nuke CheckAttribution` **cannot see** — it reads `Directory.Packages.props` and
  `native-dependencies.json`, neither of which knows what npm is — so adding one would put an
  unattributed dependency in the one place the attribution gate cannot look. The driver is ~200
  lines of CDP over Node's built-in `WebSocket` (Node 22+). `Vixen.Platform.Web.Tests`'
  `js/vixen-platform.test.mjs` already makes the same call in its own header.
- **The browser is not vendored either.** It is whatever Chrome is on the machine: `VIXEN_CHROME`,
  then `CHROME_PATH`/`CHROME_BIN`, then the usual paths. GitHub's `ubuntu-latest` image ships one.
  ⚠ `chrome-headless-shell` is deliberately **not** in that list — see below.
- ⚠ **A missing browser is a FAILURE, not a skip**, and so is a missing Node, a page that 404'd, a
  transcript that arrived incomplete, and a run that executed fewer checks than it was told to
  expect. On the day this gate does not run, it says so.

## ⚠ It drives a browser; it does not dump the DOM

`chrome-headless-shell --dump-dom` **never fires `requestAnimationFrame`** — with or without
`--virtual-time-budget`, `--screenshot` or SwiftShader; a pure-JS control page counted **zero**
callbacks in three seconds, while the same page over CDP counts 120/s. A leg built on `--dump-dom`
would report a live frame loop as dead.

So `browser-smoke.mjs` **verifies its own instrument before it believes the subject**: it counts
`requestAnimationFrame` from the driver's side, in a page with none of our code in it, and reports a
zero there as `INSTRUMENT FAILURE` in those words rather than as a broken engine.

**A first web *sample* is a different, larger thing** and is still owed. `Samples/01-HelloTriangle`
is deliberately backend-specific — `using Vixen.Graphics.Vulkan`, `VulkanDevice.Create`, embedded
`.spv` — which is what makes it a platform smoke test and what makes it unportable. A web sample
needs a backend-agnostic game plus WGSL, not a head.

## Loading it by hand

The gate does all of this for you; this is for when you want to sit and look at the page.

```bash
dotnet publish Tools/Vixen.WebProbe/Vixen.WebProbe.csproj -c Release

node Tools/Vixen.WebProbe/serve.mjs \
    Tools/Vixen.WebProbe/bin/Release/net10.0-browser/publish/wwwroot 8099
```

`serve.mjs` sets `Cross-Origin-Opener-Policy`/`Cross-Origin-Embedder-Policy`; drop them to watch
`IProcessorTopology.AvailableProcessors` fall from 10 to 1, and to watch the gate's
`processors-see-isolation` check go red for it.

Then, in another shell — **the full browser over CDP, not `--dump-dom`**. `browser-smoke.mjs` takes
a site root and does its own serving and launching, so it is also the one-liner:

```bash
node Tools/Vixen.WebProbe/browser-smoke.mjs artifacts/web/wwwroot
```

`drive.mjs` is the older, chattier developer tool that attaches to a browser you started yourself.
⚠ It needs `npm install --prefix Tools/Vixen.WebProbe playwright-core` — which is exactly why the
**gate** does not use it, and why nothing that runs in CI depends on it.

```bash
chrome-headless-shell --headless --no-sandbox --enable-unsafe-swiftshader \
    --remote-debugging-port=9223 --user-data-dir=/tmp/vixen-cdp about:blank &

node Tools/Vixen.WebProbe/drive.mjs http://localhost:8099/index.html 6000
```

## Why it is not in `Vixen.slnx`

`net10.0-browser` cannot be *evaluated* without the `wasm-tools` workload — not built, evaluated — so
a solution containing this project would not restore for anyone who has not installed it. That is the
same reason `Vixen.Platform.Web` and its two siblings are absent, and the same reason
`../Vixen.AotProbe.iOS` is.

The cost is stated rather than hidden: `Test`, `CheckFormat`, `CheckApi` and `Pack` do not see this
project. `CheckArchitecture` **does**, because it globs `Tools/**/*.csproj` rather than reading the
solution. `BrowserSmoke` sees it too, and is the only gate that *runs* it.

⚠ **A measurement that does not fit the sentence above, recorded rather than acted on.** On a Mac
with **no `wasm-tools` workload installed at all**, `nuke CompileWeb` — which is `dotnet build` on
each of the three browser *libraries* — **succeeded, in four seconds**, while `nuke PublishWeb`
failed on the next line with `NETSDK1147: the following workloads must be installed: wasm-tools`.
So the workload is what the **head** needs, for the emcc relink; the three libraries built without
it. That is not the whole question — the claim above is about *restoring a solution* that contains
them, which is a different operation from building a csproj by path, and it was not measured — but
it is enough that "the libraries cannot be evaluated without the workload" should be re-measured
before it is relied on again, rather than repeated. It is repeated in `build/Build.cs`
(`CompileWeb`'s remarks), in `Vixen.Platform.Web.Tests.csproj` and in `ci.yml`.

Licensed under Apache-2.0.
