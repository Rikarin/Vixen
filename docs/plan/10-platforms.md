# 10 — Platforms

Six targets. They are not equally hard, and pretending they are is how multi-platform plans fail. This
document gives each one an honest assessment, a concrete implementation path, and its own gate.

## Summary

| Target | RID | Runtime | Graphics | Difficulty | Risk |
|---|---|---|---|---|---|
| Windows | `win-x64`, `win-arm64` | CoreCLR (JIT) | Vulkan (D3D12 post-1.0) | Low | Low |
| Linux | `linux-x64`, `linux-arm64` | CoreCLR (JIT) | Vulkan | Low | Low |
| macOS | `osx-x64`, `osx-arm64` | CoreCLR (JIT) | Vulkan/MoltenVK | Medium | Medium |
| Android | `android-arm64`, `android-x64` | CoreCLR or Mono (SDK's choice) | Vulkan 1.1+, GLES 3.2 | Medium-High | Medium |
| iOS | `ios-arm64` | **NativeAOT only** (no JIT permitted) | Vulkan/MoltenVK | High | Medium |
| Web | `browser-wasm` | Mono-based WASM runtime | WebGL2 ✅ *verified*, WebGPU | High (labour) | Low-Medium |

## Shared foundation

`Vixen.Platform` defines the contracts; each platform assembly implements them. Nothing above
`Vixen.Platform` has a `#if ANDROID`.

```csharp
IWindow            create/destroy/resize/title/icon/fullscreen/cursor/DPI/events, multi-window
ISurface           native handle for swapchain creation (HWND, Wayland surface, CAMetalLayer, ANativeWindow, canvas)
IDisplayInfo       monitors, resolutions, refresh rates, HDR capability, per-monitor scale
IFileSystemHost    platform paths (app/data/cache/temp), sandbox rules, permission requests
IClipboard         text, image, custom formats
INativeDialogs     open/save/folder pickers, message boxes — must be native, users notice
ILifecycle         suspend/resume/low-memory/focus-lost/orientation/back-button
IInputSource       raw device enumeration and events (feeds Vixen.Input — via the host, see 11)
ITextInput         IME composition, on-screen keyboard, candidate window positioning
IHaptics           rumble, taptic
IPowerInfo         battery, thermal state, power mode — mobile quality scaling depends on this
```

✅ **Built, and four things came out differently from that list.** Each is written up in
`Platform/Vixen.Platform/README.md`; in summary:

- **`IInputSource` is not an event source.** Events — all of them, window, keyboard, pointer, touch,
  gamepad, lifecycle, drag-and-drop — arrive as one `PlatformEvent` stream drained once per frame by
  `IPlatform.PumpEvents()`. The OS delivers them interleaved, so several typed streams would mean
  buffering and re-ordering them and losing the ordering *between* them. `IInputSource` is what is
  left: device enumeration, and the held-key state only the platform knows after focus is lost.
- **Keys are physical positions and there is no layout-dependent enum.** WASD must be the same shape
  under the player's left hand on AZERTY; typed characters arrive as `TextInput` carrying a string,
  because a character is not a key.
- **`IHaptics` hangs off `IGamepad`** rather than standing alone, since force feedback is a property
  of a device and there is nothing to say about it without one.
- **`IProcessorTopology` was added**, which doc 03 did not anticipate: it is the contract half of the
  thread pinning deferred out of `Vixen.Core.Threading`, and `AvailableProcessors` is the number a
  worker pool should be sized from, since a container's quota and `Environment.ProcessorCount`
  differ.

✅ **`Vixen.Platform.Headless`** implements the same contracts with no window, no GPU, no audio device, and
no display server — what a dedicated server and batch-tooling head run on
([17](17-app-heads-and-shipping.md)). Every subsystem must tolerate the absence of a window rather than
assuming one exists; a headless CI leg enforces it.

Two decisions there worth recording. Headless **windows are real windows without a picture** — an id, a
size, a framebuffer size, a scale factor, focus, an event stream, and a surface reporting
`SurfaceKind.None` — so the server runs the desktop's frame loop rather than a second one written for
it. And the **lifecycle is driveable**: `Suspend`, `Resume` and `ReportMemoryPressure` are public on
the concrete type, which is where the suspend/resume fault-injection loop this document asks for below
actually runs. On a phone it needs a phone; there a hundred cycles cost milliseconds and run on every
pull request.

**`Vixen.Platform.Desktop`** implements most of this once, via `Silk.NET.SDL` 2.23.0 (SDL3): windowing,
input, gamepads with haptics, clipboard, display enumeration, IME. The three desktop assemblies then
add only what SDL does not cover well: native file dialogs, OS-specific window chrome, per-OS path
conventions, and platform-specific graphics-loader details.

**App heads.** Each platform gets a project template producing the platform's native entry point
(`Program.cs` + SDL loop on desktop, `Activity` on Android, `UIApplicationDelegate` on iOS, a JS
bootstrap on Web). Game/app code lives in a platform-neutral library that all heads reference — no
`#if` in user code.

## Windows

**Easiest target; it is the development baseline alongside Linux.**

- **Vulkan only at 1.0. D3D12 is postponed** (Q4) but designed for, with a stub project reserved from
  Phase 1 so it lands additively — see ADR-001 for the five measures that keep the RHI mappable, chiefly
  specifying barriers against Vulkan `synchronization2` so D3D12 *Enhanced Barriers* map directly.
  The eventual motivation is Windows GPU tooling (PIX, GPU crash dumps), IHV driver reliability, and
  `DirectStorage`/HDR interop. Until then, Windows Vulkan driver quality is good enough on all three IHVs.
- ~~`net10.0-windows` for `Vixen.Platform.Windows` only, for WinRT file pickers~~ — **corrected when it
  was built.** `Vixen.Platform.Windows` targets plain `net10.0`: the Windows-versioned framework is
  only needed for WinRT and WPF/WinForms projections, it would spread from that project to every
  consumer that references it, and it would take the assembly out of `nuke CheckApi`, which covers
  `net10.0`. WinRT's picker in a desktop application is a wrapper over `IFileDialog`, which is what is
  used instead — through `[LibraryImport]` behind `[SupportedOSPlatform("windows")]`, so the project
  builds and its pure tests run on all three desktops. Jump lists, taskbar progress and `DXGI` output
  enumeration for HDR are still owed and would not change this.
- Publish: `PublishSingleFile` + `PublishReadyToRun`; NativeAOT for the editor as an opt-in build
  variant (measured startup win, but rules out editor plugin loading — hence opt-in).
- Gates: `Samples/01` renders; editor runs; all tests green on `windows-latest` CI.

## Linux

- Vulkan only. GL exists as a documented fallback but is not gated on.
- Wayland primary, X11 fallback — SDL3 handles the selection; the engine must not assume either
  (a `IDisplayInfo` implementation that assumes X11 breaks on modern GNOME).
- XDG base directories for paths; XDG desktop portal for file dialogs when sandboxed (Flatpak).
- **CI's most valuable target** because of **lavapipe** (Mesa's software Vulkan): a real, conformant
  Vulkan 1.3 driver with no GPU, so the full Vulkan backend, validation layers, and golden-image
  rendering tests run on a standard GitHub runner. This is how graphics testing becomes routine instead
  of aspirational.
- Publish: framework-dependent + self-contained tarballs, AppImage for the editor, Flatpak manifest
  as a P2 nice-to-have.
- Gates: `Samples/01` renders on lavapipe *and* on a real GPU (a self-hosted runner or a manual
  pre-release check); all tests green on `ubuntu-latest`.

## macOS

**Medium difficulty, entirely because of MoltenVK and Apple's packaging rules.**

- **MoltenVK** (ADR-011), verified at **v1.4.2** — a layered implementation of **Vulkan 1.4**, minimum
  macOS 12.0 / iOS 15, shipped as a universal `XCFramework` or `libMoltenVK.dylib`.
- **Two build flavours, because MoltenVK does not load Vulkan layers itself.** This corrects an earlier
  draft of this plan, which recommended linking MoltenVK directly and skipping the Vulkan Loader —
  that would have silently cost us validation layers on macOS, and validation-clean-in-debug is a
  stated non-negotiable ([00](00-vision-and-principles.md)).
  | Flavour | Assembly |
  |---|---|
  | **Shipping** | Link MoltenVK directly (`XCFramework` in the bundle). No Loader, no layers, one fewer moving part, no dependency on a user-installed Vulkan SDK. |
  | **Development** | Bundle the **Vulkan Loader + validation layers** from the Vulkan SDK alongside MoltenVK as an ICD. Requires `VK_ICD_FILENAMES`/`VK_DRIVER_FILES` set before `vkCreateInstance`, **and** `VK_KHR_portability_enumeration` + the `VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR` flag — without that bit the Loader will not return MoltenVK's `VkPhysicalDevice` at all, which presents as "no Vulkan devices found" on a machine that works fine. |
  The instance-creation code paths differ only by that flag and the layer list, so this is a
  configuration switch in `Vixen.Graphics.Vulkan`, not two code paths.
- **Measured when the development flavour first met a real Homebrew install** (2026-07-26, Apple
  silicon, `vulkan-loader` + `molten-vk` + `vulkan-validationlayers`). Three separate failures, none
  of which were Vulkan problems, all of which present as Vulkan problems:
  | Symptom | Cause | Where it is handled |
  |---|---|---|
  | `DllNotFoundException` from `Vk.GetApi()` on a machine where `vulkaninfo` works | `/opt/homebrew/lib` is not on dyld's default search path (`/usr/local/lib`, `/usr/lib`) | `VulkanLoader` probes `VULKAN_SDK` and the known prefixes explicitly |
  | `vkCreateInstance` → `ERROR_LAYER_NOT_PRESENT` for a layer `vkEnumerateInstanceLayerProperties` had just listed | Homebrew's layer manifest names its library by bare filename, which the Loader resolves through `dlopen` — and that has the same search path | `.runsettings` sets `DYLD_LIBRARY_PATH` for test runs; the backend also retries without the layer and logs event 2002 rather than refusing to start |
  | Second `VulkanInstance` in a process segfaults | `Dispose` also disposed the shared `Vk`, unloading `libvulkan` under every cached entry point | `VulkanInstance.Dispose` no longer disposes what it does not own; asserted by `AnInstanceCanBeCreatedAfterOneIsDisposed` |
  Homebrew's own caveat suggests `VK_LAYER_PATH`; that was measured and **does not help**, because
  `VK_LAYER_PATH` locates the *manifest* and the manifest was never missing. `DYLD_LIBRARY_PATH` is
  the only lever, and dyld reads it once at process start — so it has to be set by whatever launches
  the process, never from managed code. The LunarG SDK writes absolute paths and has none of this.
- Constraints to design around, all capability-gated in the RHI (full list in ADR-011): descriptor
  indexing requires Metal argument buffers enabled and is Tier-1-limited; buffer-device-address needs
  Tier 2; **primitive restart cannot be disabled**; **pipeline-statistics queries are unsupported**;
  PVRTC uploads must be host-mapped rather than staged. None affect the P1 feature set.
- **The iOS and tvOS Simulators are supported targets** for MoltenVK, which is what makes the
  simulator smoke tests in CI meaningful rather than theatre.
- Surface: `VK_EXT_metal_surface` over a `CAMetalLayer` attached to the SDL window's `NSView`.
- ObjC interop in `Vixen.Platform.MacOS` via `[LibraryImport]` against `objc_msgSend` for the handful
  of calls needed (`NSOpenPanel`, `NSPasteboard`, `NSProcessInfo`; window chrome and accessibility are
  still owed). No Xamarin.Mac bindings. **Built, and it moved the main-thread rule**: AppKit's
  `0xbad4007` abort is not limited to windows — `TIFFRepresentation` on an `NSBitmapImageRep` does it
  too, from a thread that never went near one. Everything in that assembly which touches AppKit checks
  `NSThread.isMainThread` and refuses rather than aborting; the pasteboard's own reads and writes and
  `NSProcessInfo` are thread-safe and are exercised from a test runner on every run.
- **Packaging is the real work**: `.app` bundle layout, `Info.plist`, universal binary (`osx-x64` +
  `osx-arm64` via `lipo`), hardened runtime entitlements, codesigning with a Developer ID, and
  notarisation. All scripted in Nuke (`Build.Release.cs`) and run in CI on `macos-14`.
- **Presentation is verified by `Samples/01` and by nothing else, and that is a deliberate gap rather
  than an oversight.** AppKit aborts the process when a window is created off the main thread, so a
  test runner — which is never on it — cannot open one; the desktop tests force SDL's dummy video
  driver for the same reason. The swapchain's pure choices are unit-tested, the acquire and present
  path is not. Running the sample with `--vixen-frames N` is what stands in for it, and with the
  validation layers installed a validation error is a non-zero exit.
- Gates: `Samples/01` renders via MoltenVK; the editor runs notarised from a signed `.dmg`; the
  golden-image suite passes within tolerance (MoltenVK's output will differ slightly from lavapipe's —
  hence perceptual comparison per [05](05-graphics-rhi.md)).

## Android

- `net10.0-android`, API level 26 minimum (Vulkan 1.0 guaranteed, 1.1 on the overwhelming majority of
  API 28+ devices).
- **Vulkan primary with a GLES 3.2 fallback that is genuinely maintained**, not aspirational. Android
  driver fragmentation is real: some devices report Vulkan support and then fail on specific
  extensions. The engine must degrade, and the device-capability database (a curated deny-list keyed on
  GPU/driver version, shipped as content and updatable) is how commercial engines handle this. Build it
  small but build it.
  - ✅ **The graphics half of that fallback exists.** `Vixen.Graphics.OpenGL` now has
    `Silk.NET.OpenGLES` behind `SilkGlesApi` and an EGL context of its own — `EglContext`, over
    entry points loaded from the platform's `libEGL`, because there is no `Silk.NET.EGL` for
    Silk.NET 2. It asks a device for GLES 3.2 and falls back to 3.0, which is the same shape as the
    deny-list's own decision one level up. ⚠ What is still owed is the Android head choosing it:
    nothing here creates a GL device instead of a Vulkan one yet, and the deny-list that would say
    when to does not exist.
- `VK_KHR_dynamic_rendering` is not available on all Vulkan 1.1 drivers → the real-render-pass fallback
  path in the Vulkan backend ([05](05-graphics-rhi.md)) is mandatory here, not optional.
- **Lifecycle is the biggest source of bugs**: `onPause`/`onResume` destroys and recreates the surface;
  the engine must handle swapchain loss, and on some devices device loss, at arbitrary points. A
  fault-injection test (`ILifecycle` simulated suspend/resume in a loop) belongs in CI.
- Asset access via `AAssetManager` through a `IFileProvider` — assets inside the APK are not seekable
  files, so the VFS abstraction earns its keep here.
- Input: touch (multi-touch, pressure, stylus), sensors (accelerometer/gyro/gravity/compass), gamepad,
  soft keyboard with IME. Stride's input model covers all of these and is a good reference for the
  device abstraction.
- Runtime: .NET 10 on Android uses CoreCLR or Mono depending on SDK configuration. Vixen does not care
  — no IL weaving means either works (see [15](15-risks-and-open-questions.md) on the "no Mono"
  constraint). Prefer CoreCLR where it is stable for better JIT throughput; the choice is a publish
  property, not an engine dependency.
- Packaging: AAB with per-ABI splits; **Play Asset Delivery** is the natural pairing with addressable
  remote groups and should be a supported `loadPath` provider.
- Gates: `Samples/01` and `Samples/05` run on a physical mid-range device and on an emulator; suspend/
  resume 100 times without leaking or crashing; APK size budget for the sample.

## iOS

**High difficulty, but well-understood difficulty.**

- `net10.0-ios`, **NativeAOT is mandatory** — Apple forbids JIT. This is the reason the entire plan is
  built on source generators (ADR-002). If any subsystem needs runtime code generation, iOS is where it
  dies, so iOS must be brought up **early** (Phase 3, not Phase 10) as a forcing function.
- MoltenVK **statically linked** (dynamic frameworks are permitted but static avoids codesigning and
  load-time friction), surface from `CAMetalLayer` on a `UIView`.
- Constraints to design for: no background threads for GPU submission during suspension; strict memory
  limits with `didReceiveMemoryWarning` → the streaming manager must actually respond; thermal
  throttling → quality scaling from `IPowerInfo`; no file writes outside the sandbox.
- ObjC interop via `[LibraryImport]`, same approach as macOS.
- Packaging: `.ipa`, provisioning profiles, entitlements, App Store Connect upload — scripted in Nuke,
  run on `macos-14` CI with certificates from GitHub secrets.
- **Trimming is not optional** — a NativeAOT iOS binary with the full engine must fit a reasonable size
  budget, so `IsTrimmable` correctness across every runtime assembly is load-bearing. CI publishes an
  AOT+trimmed iOS binary on **every PR** and fails on any IL2xxx/IL3xxx warning, from Phase 3 onward.
- Gates: `Samples/01` and `Samples/05` run on a physical device; zero trim/AOT warnings; binary size
  under budget; memory-warning handling verified.

## Web

**Was the highest-risk target. The core unknown has been retired by a working spike** —
[`spikes/web-webgl2/RESULT.md`](spikes/web-webgl2/RESULT.md). Still the most *labour-intensive* target,
but no longer the most uncertain one.

### Verified facts (measured, not assumed)

Spike run on .NET SDK 10.0.302 + `wasm-tools`/`wasm-experimental` 10.0.110, Emscripten 3.1.56,
Silk.NET 2.23.0, Chromium.

- ✅ **`Silk.NET.OpenGLES` drives real WebGL2 from `browser-wasm`.** Verified end to end with a rendered
  triangle: context creation, shader compile/link, VAO/VBO, `BufferData(void*)`, `DrawArrays`,
  `glGetString` → `OpenGL ES 3.0 (WebGL 2.0 (OpenGL ES 3.0 Chromium))`. No Silk.NET fork needed.
- ✅ **The bridge is ~40 lines.** `[DllImport("*", EntryPoint = "emscripten_GetProcAddress")]` plus
  Silk.NET.Core's `LamdaNativeContext`, which adapts any `Func<string, nint>` into the `INativeContext`
  every Silk.NET binding resolves through. Context creation via `emscripten_webgl_create_context` /
  `_make_context_current`. Every P/Invoke shape the RHI needs works: `string` in, `out int`, `void*`,
  struct by `ref`, and runtime-resolved function-pointer dispatch.
- ✅ **Payload floor is ~930 KB Brotli**, not tens of megabytes. Measured: 1.99 MB Brotli by default,
  **0.93 MB** with `InvariantGlobalization` + `PublishTrimmed`. The Mono runtime is 911 KB of it, ICU
  ~600 KB (fully removable), and the trimmer reduces `Silk.NET.OpenGLES` from ~2 MB to **25 KB**. An
  earlier draft of this plan estimated "tens of megabytes" — that was wrong by an order of magnitude.
- ⚠ **TFM is `net10.0` with `Sdk="Microsoft.NET.Sdk.WebAssembly"`**, not `net10.0-browser`. Plus
  `<WasmBuildNative>true</WasmBuildNative>` to relink with emcc.
- ⚠ **Required emcc flags: `-lGL -sMAX_WEBGL_VERSION=2 -sMIN_WEBGL_VERSION=2`.** Omitting them does not
  error — the context request silently returns **WebGL 1**, ES 3.00 shaders then fail to compile, and
  Silk.NET's `GetShaderInfoLog` throws `ArgumentOutOfRangeException` instead of reporting the compile
  error, hiding the cause completely. Verified by building without the flags. Mitigations: assert
  `GL_VERSION` contains `WebGL 2` right after context creation and fail with a message naming the flag;
  wrap the info-log getters to query length first; ship the flags in `Vixen.Platform.Web`'s `.targets`
  so a consumer cannot omit them.
- **Runtime is Mono**, confirmed by pack name (`Microsoft.NETCore.App.Runtime.Mono.browser-wasm`).
  "AOT on WASM" means **Mono AOT** (`Microsoft.NET.Runtime.MonoAOTCompiler.Task`), *not* NativeAOT.
  `Microsoft.DotNet.ILCompiler.LLVM` is not on nuget.org at all — only on the `dotnet-experimental`
  feed, all prerelease. Settled and acceptable per [15](15-risks-and-open-questions.md) §2.
- **`Silk.NET.Windowing` has no browser TFM** (groups exist for android/ios/maccatalyst but not
  browser; no Silk.NET package mentions browser/wasm/emscripten). Windowing, surface, and input on the
  web are ours to write — as already assumed.

### Still true, still work

- **`Silk.NET.WebGPU` binds native Dawn/wgpu**, not the browser's `navigator.gpu`. Browser WebGPU needs
  JS interop, so `Vixen.Graphics.WebGPU` carries two surface implementations behind one backend.
- **WebGL2 has no compute shaders.** This cascades: clustered-lighting binning, GPU particle
  simulation, GTAO, compute post FX, and GPU culling all need fullscreen-fragment or CPU fallbacks.
  [06](06-rendering-pipeline.md) requires every post effect to declare a non-compute variant for exactly
  this reason — designed in, not discovered late.
- **Threads** need `SharedArrayBuffer` and therefore COOP/COEP headers. ✅ **`JobScheduler` takes
  `workerCount == 0`**: the graph, the slot ring, the failure log and the batching are unchanged, and
  work runs when a thread reaches `Complete` — which already executed ready work rather than parking,
  so there is no second code path and nothing that only the browser exercises.
  `new JobScheduler()` picks zero on `browser-wasm`, because `Thread.Start` there throws rather than
  being slow. Two things do change and are stated in that project's README: scheduled-and-never-completed
  work never runs, and an automatic parallel-for batch size is one batch rather than four per
  participant. Covered by `SingleThreadedJobSchedulerTests`. **An earlier draft of this document said
  a `workerCount == 0` CI leg already enforced this. There was no such leg and there is not one yet** —
  running the whole suite single-threaded needs the schedulers the rest of the engine constructs to
  take the count from somewhere, which is its own change.
- **Size beyond the floor.** 930 KB is the runtime baseline; the engine's own IL adds to it. ✅
  **Lazy assembly loading is implemented** — `VixenWebLazyAssembly` takes a named assembly out of the
  boot manifest at publish and `WebLazyAssemblies.LoadAsync` fetches it on demand; see
  [`Vixen.Platform.Web`'s README](../../Platform/Vixen.Platform.Web/README.md) for the three
  constraints that come with it. Measured for a head that stands the platform up and runs the loop:
  **978 KB Brotli**, of which the platform layer and everything of Vixen's it needs is 50 KB.

### Path

1. ~~Spike~~ ✅ **done** — [`spikes/web-webgl2/`](spikes/web-webgl2/) holds the working project and the
   full write-up. The fallback plan (hand-written WebGL2 binding via `[JSImport]`) is no longer needed.
2. ~~`Vixen.Platform.Web`~~ ✅ **done** — canvas surface, pointer/keyboard/gamepad/touch events, IME
   through an invisible input over the caret, `requestAnimationFrame` loop with a *measured* refresh
   rate, `ResizeObserver`, DPI, fullscreen, pointer lock, clipboard from the paste event,
   IndexedDB-backed `/data` and `/cache`, `fetch`-based `/app` provider with HTTP range requests for
   streaming, and the emcc flags in its `.targets`.

   Three things the build measured that this plan had wrong or did not know:

   - **The application head is `net10.0-browser`**, not `net10.0` with the WebAssembly SDK. The spike
     used `net10.0` because it referenced nothing; a head that references `Vixen.Platform.Web` cannot,
     because a `net10.0` project cannot reference a `net10.0-browser` one — `NU1201`, at restore. The
     WebAssembly SDK is unchanged.
   - **`BrotliCompressionLevel` takes a `CompressionLevel` name and not a number.** Setting it to `11`
     writes a zero-byte `.br` for every asset, with no diagnostic, and a server doing content
     negotiation then serves empty files to every browser that asks for Brotli.
   - **`System.Text.Json` costs 59 KB Brotli** — six per cent of the payload — so the content
     manifest's reader is hand-written. The generated-context approach the rest of the engine uses is
     trim-clean and is still the wrong trade on the one platform where the payload is the product.
3. `Vixen.Graphics.OpenGL` in its WebGL2 profile, using the verified bridge.
4. Addressable remote groups map naturally to HTTP — Web is where the addressable design pays off most,
   since everything is remote by definition.
5. WebGPU backend once the WebGL2 path is stable, as the performance story.

### Honest scoping

**`Samples/02-HelloUi` and a UI-heavy application are the Web target's real goal, not
`Samples/05-PlatformerGame`.** A UI/2D application in the browser is achievable and genuinely
valuable (the editor's asset browser or a documentation playground running in a page). A full 3D
game at parity with desktop is a stretch goal, and committing to it early distorts the whole
renderer's design toward the weakest platform.

Gates: `Samples/01` (triangle) and `Samples/02` (UI) run in Chrome, Firefox, and Safari; download size
under budget; single-threaded job-system mode verified.

## Cross-platform discipline

| Concern | Rule |
|---|---|
| Paths | Only virtual paths in engine code. `System.IO.Path` is banned outside `Vixen.Platform.*` and editor code, and this is now literally analyzer-enforced: `Core/Vixen.Core.IO.Analyzers` reports `VXIO0001` in every `Core/` project, which `TreatWarningsAsErrors` makes a build failure. The seven host-filesystem places that translate — `PhysicalFileProvider`, the two watchers, the disk caches — turn it off by name in `.editorconfig`, each with a written reason. |
| Case sensitivity | Virtual paths are case-sensitive everywhere, including Windows. A CI check on Linux catches `Texture.PNG` vs `texture.png` before a user does. |
| Endianness | Content is little-endian; no big-endian target exists, but the serializer asserts rather than assumes. |
| Floating point | No reliance on cross-platform FP bit-identity for gameplay. Deterministic simulation, where needed, uses fixed-point or a documented deterministic subset. |
| Feature detection | Always a runtime capability query with a fallback, never `#if PLATFORM`. `#if` is for P/Invoke surface only. |
| Time | `Stopwatch`-based monotonic time; never `DateTime.Now` in the loop. |
| Threading | Every subsystem works with `workerCount == 0`. `JobScheduler` supports it and is tested for it; the test mode that would run the *whole* suite single-threaded does not exist yet — see § Web, where the same claim was corrected. |
| Native binaries | One `Vixen.Platform.Native` project owns RID→binary mapping, `runtimes/<rid>/native/` layout, checksum verification at acquisition time, and a licence manifest. Native binaries are never committed; they are restored by a Nuke target from pinned, checksummed URLs. |

## Platform CI matrix

| Job | Runner | Runs |
|---|---|---|
| build+test | `windows-latest`, `ubuntu-latest`, `macos-14` | full test suite, every PR |
| graphics | `ubuntu-latest` + lavapipe | Vulkan conformance, validation layers, golden images, every PR |
| aot-trim | `ubuntu-latest`, `macos-14` | `PublishAot` + `PublishTrimmed` smoke, zero warnings, every PR |
| android | `ubuntu-latest` | build AAB + emulator smoke test, every PR; physical-device suite nightly |
| ios | `macos-14` | build + simulator smoke test, every PR; physical-device suite nightly |
| web | `ubuntu-latest` | build + Playwright headless Chrome/Firefox smoke test, every PR |
| release | matrix | signed/notarised artefacts, on tag |
