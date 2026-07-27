# Vixen.Platform.Android

Android behind `IPlatform`: a `SurfaceView` giving Vulkan an `ANativeWindow`, the activity lifecycle
translated into the engine's, the `Choreographer` where a `while` loop would be on a desktop, assets
inside the APK reached through the virtual file system, and multi-touch.

Spec: [docs/plan/10](../../docs/plan/10-platforms.md) § Android. API 26 minimum, which is where
Vulkan 1.0 is guaranteed.

```csharp
[Activity(MainLauncher = true, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : AndroidActivityHost {
    VixenApplication? application;

    protected override Action Start(AndroidPlatform platform) {
        application = VixenApp.Create([]).WithPlatform(platform).Build(new MyGame());
        return application.RunFrame;
    }
}
```

## The surface comes and goes, and everything here is shaped by that

Doc 10 calls lifecycle the biggest source of bugs on this platform. The reason is specific: the
`ANativeWindow` a swapchain was built from is **destroyed** when the activity stops and a **new one**
appears when it starts again. There is no desktop equivalent — a minimised window still owns its
surface; a stopped activity does not.

Android's contract is that `surfaceDestroyed` must not return until nothing is using the window. The
ordering here is what makes that true without a cross-thread handshake:

| Callback | What happens |
|---|---|
| `OnPause` | frame callback removed **first**, touches released, state → Background |
| `OnStop` | state → Suspended, `Suspending` raised — the renderer drops its swapchain while the window is still valid |
| `surfaceDestroyed` | nothing is in flight; the `ANativeWindow` reference is released |

`IWindow.Surface` reports `CanPresent` false in between, which is a state an application spends real
time in rather than an error. A renderer that checks it — as the Hello Triangle sample already does
before building a device — needs no Android-specific path.

## Why the Choreographer and not a render thread

A dedicated render thread is the usual Android answer and is the wrong one here. The surface callbacks
arrive on the main thread, and `surfaceDestroyed` blocking until the renderer is idle means a
handshake on the hot path of every suspend. `Choreographer` posts on the main thread in step with
vsync, so the surface's lifetime and the frame's are ordered by construction rather than by a lock.

`RunFrame` is public for exactly this ([doc 17](../../docs/plan/17-app-heads-and-shipping.md)), and
`PumpEvents` drains rather than polls: the callbacks already posted everything as it arrived.

## The APK is not a file system

`AndroidAssetProvider` is the case doc 10 says the VFS exists for. An asset inside an APK is a range
of a zip, there is no path to hand anybody, and `File.OpenRead` cannot reach it. Every other
platform's `/app` mount is a directory; this one is an `AssetManager`, and nothing above notices
because everything above asks `IFileProvider`.

Compressed assets are **not seekable** — `AssetManager.Open` returns a stream over an inflater and
seeking throws — so a read is buffered into memory when the stream will not seek and passed straight
through when it will. That means the large files, the ones worth not copying, are exactly the ones
that avoid the copy, provided the packager stored them uncompressed.

Existence is answered by opening and closing, because `AssetManager` has no `Exists` and `List` does
not recurse.

## What is deliberately absent

**No dialogs at all**, not even a message box. Every file operation goes through the Storage Access
Framework — an intent, a result on the activity, a content URI rather than a path — and an
`AlertDialog` needs an activity with a live window. The iOS platform reaches the same conclusion about
its document picker; it does implement `ShowMessageAsync`, and this does not, because `UIAlertController`
needs only a view controller.

**Clipboard text only.** Images on the Android clipboard are content URIs into another application's
provider, and resolving one means a permission grant and a stream copy.

**No gamepads, no sensors.** Both are real work with real device-fragmentation problems; an empty list
is honest where a stub is not.

**No key translation.** Hardware keys arrive on the view as `KeyEvent` and are not mapped to `Key`.
The map is a table and the table is the easy part; what makes it worth doing carefully is that `Key`
is a *physical position* by contract, and Android's keycodes are a mix of positions and labels.

**No safe-area insets.** `IDisplayInfo` reports the full bounds as the work area. `WindowInsets` is
the right source and needs an attached window; the full bounds is wrong under a display cutout and is
at least *visibly* wrong, where a guessed inset would be invisibly wrong.

**No thermal state below API 29**, reported as `Nominal`. The honest answer is "this device will not
say"; inferring heat from throttling is inferring the cause from the symptom.

## Owed

**It runs, on the emulator — and the emulator's GPU mode decides whether you can see it.**
`Samples/01-HelloTriangle.Android` reaches a Vulkan device on the device's own `libvulkan.so`, builds
a swapchain from the `ANativeWindow` this assembly hands over, and draws the triangle. The lazy
device-creation path is exercised exactly as designed: "no window to present to" once, then the
surface arrives and the device is built.

> ⚠ **Start the emulator with `-gpu swiftshader_indirect`.**
>
> With the default `-gpu host`, everything reports success — device created, swapchain built, buffers
> queued and imported by SurfaceFlinger at 1080×2400 RGBA8888, ninety per cent CPU — **and the screen
> stays blank**. The same APK on the same emulator with SwiftShader draws the triangle. So the
> emulator's GFXStream host-GPU path does not present a `SurfaceView`-backed Vulkan swapchain, and
> nothing about it is this engine's doing.
>
> That cost an hour and two wrong fixes: a `SetZOrderOnTop(true)` and a null window background, both
> reasoned from the symptom, both reverted. What settled it was changing the *one* variable neither
> touched. Worth remembering the next time an Android surface is invisible: rule out the emulator
> before rewriting the view.

**Packaging is a `dotnet build` away and not more.** Installing the APK by hand needs
`-p:EmbedAssembliesIntoApk=true`, because a Debug build otherwise relies on Fast Deployment pushing
the assemblies separately — and without it the process aborts in `monodroid` with "No assemblies
found".

**No GLES fallback.** Doc 10 asks for one that is genuinely maintained rather than aspirational, plus
a device-capability deny-list keyed on GPU and driver version. Neither exists; the Vulkan backend is
the only one, and on a device that reports Vulkan and then fails an extension there is currently
nowhere to go.

**NativeAOT is not the target here.** `warning XA1040` calls it experimental and not suitable for
production, and the plan only ever committed to NativeAOT for iOS. Android's gate should be its
default runtime.

Licensed under Apache-2.0.
