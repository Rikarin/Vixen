# Vixen.Platform.iOS

iOS behind `IPlatform`: a `CAMetalLayer`-backed view for MoltenVK to present to, UIKit's lifecycle
translated into the engine's, a `CADisplayLink` where a `while` loop would be on a desktop, and
multi-touch.

Spec: [docs/plan/10](../../docs/plan/10-platforms.md) § iOS.

```csharp
[Register("AppDelegate")]
public sealed class AppDelegate : IosApplicationHost {
    VixenApplication? application;

    protected override Action Start(IosPlatform platform) {
        application = VixenApp.Create([]).WithPlatform(platform).Build(new MyGame());
        return application.RunFrame;
    }
}
```

## There is no loop, so a display link runs the frame

`UIApplicationMain` never returns and owns the main thread; there is nowhere to put
`while (!IsStopping)`. A background thread is not the answer either — a `UIView` may only be touched
from the main thread, and iOS forbids GPU submission entirely while an application is suspended.

So `IosApplicationHost` drives `VixenApplication.RunFrame` from a `CADisplayLink`, which calls back on
the main thread in step with the display. `RunFrame` is public for exactly this
([doc 17](../../docs/plan/17-app-heads-and-shipping.md): nothing in the boot path is inaccessible),
and the arrangement is better than the loop it replaces — ProMotion's variable refresh is the
system's problem rather than the frame limiter's.

`PumpEvents` therefore drains rather than polls. UIKit already delivered the touches and the
lifecycle callbacks on its own schedule; each was posted into the event buffer as it arrived, and
draining once per frame gives the application the same "everything since the last frame, in order"
contract the desktop's real pump does.

## The link is paused across suspension, and that is not an optimisation

A frame that runs after `applicationDidEnterBackground` submits a GPU command the application is not
entitled to submit, and the penalty is the system killing the process. Pausing is the only correct
response, which is why the lifecycle callbacks here do more than post an event.

`MobileLifecycle` — shared with Android, in `Vixen.Platform` — holds the state machine. The mapping
is: `OnActivated` → foreground, `OnResignActivation` → background, `DidEnterBackground` → suspend,
`WillTerminate` → stopping. Only the third raises `Suspending`, because losing focus is not losing the
surface: a notification shade produces `willResignActive` and never `didEnterBackground`, and tearing
down a swapchain because somebody swiped down from the top would be a bug nobody could reproduce.

Touches are released on the way out. UIKit does not reliably cancel them, and a finger left down
across a suspend is one that is still down when the application comes back minutes later.

## The view exists to answer one question

A `UIView` cannot be given a `CAMetalLayer` after the fact — the layer class is decided once, by the
class, before any instance exists. `IosMetalView` overrides `layerClass` to say so, and that is the
entire reason it is a type rather than a configured `UIView`.

Its drawable size is set explicitly rather than left to UIKit, because a `CAMetalLayer` sizes drawables
from `bounds × contentsScale` and both change on rotation and on a move between a 2× and a 3× screen.
One pixel out is a swapchain that fails validation.

## What is deliberately absent

**Windowing is real; most of `IWindow` is not.** One window, screen-sized, unmovable, no cursor, no
title bar. `Position` reads as zero, `Mode` is always borderless fullscreen, and setters that cannot
do anything do nothing — which is the contract `WindowOptions` already describes for a platform
without `WindowPositioning`. Asking for a second window throws, and `MultiWindow` is absent from
`Capabilities` so an application can ask first.

**No file dialogs.** iOS has a document picker, not a file system: it browses iCloud and other
applications' containers, returns a security-scoped URL rather than a path, and needs an entitlement.
Handing one back as a `string` would produce something that looks like a path and cannot be opened.
All four refuse; `ShowMessageAsync` is real.

**Clipboard text only.** `UIPasteboard` carries images and arbitrary types, and doing it properly
means `UIImage` encode/decode and a UTI per format. A half-done version that silently loses the alpha
channel is worse than a `false` the caller can see.

**No gamepads.** `GameController.framework` is the right answer and is real work — profiles,
connection notifications, player index lights. An empty list is honest; a stub reporting one
disconnected pad is not.

**No hardware keyboard or trackpad.** An iPad with either reports through `UIPress` and
`UIPointerInteraction`, which are their own work. The soft keyboard *is* implemented, including
reading its rectangle from the system notification rather than guessing a height that is wrong on
some language, device, or predictive-bar setting.

## Owed

**It has not run on a device.** MoltenVK is linked, force-loaded and has its 431 entry points
exported — see [`Vixen.Platform.Native`](../Vixen.Platform.Native/build/MoltenVK.targets) and R11 —
and `VulkanLoader` resolves them from the process image. What has not happened is a frame appearing
on a screen. That is the phase's exit criterion and it needs a sample with an iOS head.

**Scenes are used for window creation and not for lifecycle.** The window is built from the connected
`UIWindowScene`, which is what iOS 26 wants; the lifecycle still comes through `UIApplicationDelegate`.
A full `UIWindowSceneDelegate` adoption also needs a scene manifest in the application's `Info.plist`,
which is a bundle concern this assembly cannot supply, so it is named here rather than half-done.

**No sensors, no haptics, no Metal-layer HDR.** Each is a real capability the contracts already have
room for.

Licensed under Apache-2.0.
