# Vixen.Platform.MacOS

What macOS can do that SDL cannot.

```csharp
// Nothing to wire up: DesktopPlatform picks this up on macOS.
using var platform = new DesktopPlatform(new() { Application = "MyGame" });

// The only desktop that answers this with anything but Nominal.
if (platform.Power.Thermal >= ThermalState.Serious) {
    quality.Reduce();
}
```

## What is here, and why each one

| | |
|---|---|
| **File pickers** | `NSOpenPanel` and `NSSavePanel`. SDL 2 has none. |
| **Pasteboard images** | `public.png` and `public.tiff`, through `NSBitmapImageRep`. |
| **Pasteboard types** | UTIs, passed through — the same namespace the whole system uses for file types. |
| **Thermal state** | `NSProcessInfo.thermalState`, which is where `ThermalState`'s four levels came from. |
| **Low power mode** | `NSProcessInfo.isLowPowerModeEnabled`. |
| **Processor classes** | `hw.perflevel*`, so an Apple silicon worker pool is sized from real numbers. |
| **Answering a selector** | `objc_allocateClassPair` / `class_addMethod`, so a managed method can be an IMP. |

## The decisions

**Objective-C by `objc_msgSend`, as [doc 10](../../docs/plan/10-platforms.md) § macOS says.** Three
runtime functions and one `objc_msgSend` declaration per shape of call. The symbol is a single
untyped entry point and the caller is what gives it a prototype, so getting one wrong is not a
compile error and is a crash — which is why the set is deliberately small and every call site names
the selector it is sending. No Xamarin.Mac bindings.

**The frameworks are `dlopen`ed first.** A .NET process on macOS links neither Foundation nor
AppKit, so `objc_getClass("NSPasteboard")` answers with nothing until something has loaded the
framework that defines it.

**Sending is half of it, and `ObjCRuntime.cs` is the other half.** `NSAccessibility`,
`NSApplicationDelegate` and `NSTextInputClient` are *pull* protocols: AppKit asks an object, and
until a Vixen object can be that object there is no bridge to write. So there is
`objc_allocateClassPair` / `class_addMethod` / `objc_registerClassPair`, with the IMPs supplied as
`[UnmanagedCallersOnly]` static methods — which is also what keeps it NativeAOT-clean, and iOS is
AOT-only — and a side table keyed by the instance pointer rather than an ivar, because
`class_addIvar` means layout arithmetic over a declaration this side never sees.

⚠ **A type encoding is never checked and a wrong one is not an error.** Dispatch does not read one:
`objc_msgSend` jumps to the IMP with whatever ABI the *caller* assumed, so a wrong encoding is
invisible until something introspects — `NSInvocation`, forwarding, KVC, or a framework building an
`NSMethodSignature` to decide how to call you. `ObjCTypes` therefore spells the needed encodings out
as constants, and the tests read each one back through Foundation rather than trusting what was
passed in.

⚠ **And a test that calls the selector from managed code proves nothing about any of this** — it
never leaves the runtime and would pass against a shim that invoked a delegate. `ObjCRuntimeTests`
asserts on what *Objective-C* did instead: `-[NSArray description]` fetching an element's
description, and an `NSInvocation` built from our encoding returning a `BOOL` a managed method
produced. ⚠ The obvious third route does not work and is written down where it was tried:
`-[NSArray indexOfObject:]` sends `isEqual:` to the **argument**, not to the elements, so an
equality override on an object in a collection is not what decides a lookup in it.

**Affinity is not offered, and that is Apple's decision.** `THREAD_AFFINITY_POLICY` was always
documented as a hint about which threads share cache rather than a request for a processor, and it
is unimplemented on Apple silicon. What the system offers instead is quality-of-service classes: a
thread declares whether it is user-interactive or background and the scheduler picks the core, which
on a machine with two kinds of core it is in a much better position to do than we are.
`SupportsAffinity` is `false`, and [doc 03](../../docs/plan/03-core-foundation.md)'s deferred pinning
work has its answer on this platform: do not.

**`runModal`, so the frame loop stops while a panel is open.** AppKit's own event loop runs instead
of ours, so the application is not hung — its windows redraw, the panel works, the menu bar responds
— but nothing of ours advances until the user is finished. The alternative,
`beginSheetModalForWindow:completionHandler:`, takes an Objective-C block, and constructing one from
managed code means hand-building its layout and descriptor to an ABI that is not in any header. That
is worth doing when a sheet is worth having; it is not worth doing to open a project.

**`setAllowedFileTypes:` is deprecated and is used anyway.** Its replacement takes `UTType` objects
built from extensions through a second framework, to say what a list of extensions already says. It
still works, and when it stops it will stop in a way a test on a Mac notices.

## The main-thread rule is wider than "do not create windows"

**Measured on 2026-07-29, by a test that took the runner down.** The image round trip was written
first as a real round trip through the real pasteboard. It crashed with `SIGBUS` and AppKit's
`0xbad4007` — its "this must be called from the main thread" assertion — inside
`TIFFRepresentation`, on a thread that had never gone near a window.

So the rule is not "do not create windows off the main thread", it is **"do not call AppKit off the
main thread"**, and encoding a bitmap is AppKit. Everything here that reaches into AppKit —
the panels, and the two image methods of the clipboard — checks `NSThread.isMainThread` and returns
nothing-chosen or `false` rather than aborting the process. What is left unguarded is the
pasteboard's own reads and writes and `NSProcessInfo`, which are documented as thread-safe and are
exercised from a test runner's worker thread on every run.

That restriction costs nothing in a real head: `IPlatform` is owned by the thread that created it,
and on macOS that has to be the main thread for a window to exist at all. It costs the automated
tests the image round trip, which is the same gap [doc 14](../../docs/plan/14-roadmap.md) already
accepts for presentation — proved by `Samples/01-HelloTriangle`, which has a genuine main thread.

## Testing

The interop is tested against the real frameworks, because there is no useful way to fake
`objc_msgSend` and a wrong signature is wrong in a way only a real message send reveals: the return
width of a `BOOL`, the eleven-argument bitmap initialiser, the ownership of a returned `NSString`.
The pasteboard's data path round-trips through the real pasteboard. The image path is asserted to
refuse rather than abort, for the reason above.

Licensed under Apache-2.0.
