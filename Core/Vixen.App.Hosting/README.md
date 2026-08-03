# Vixen.App.Hosting

The application host — everything `Vixen.App` does except choosing which backend to open.

**You almost certainly want [`Vixen.App`](../../Tools/Vixen.App/README.md) instead.** That package
references this one, adds the two backend selectors, and is where `VixenApp.Run<TGame>(args)` lives.
It is also where the host is documented; this page is only about why there are two packages.

## Why the split

Both assemblies declare the same namespace, `Vixen.App`, so nothing a consumer writes changes. What
changes is which build profile the code is compiled under.

`Directory.Build.props` gives `Tools/**` the TOOLING profile, whose own comment says: *"Reflection
and LINQ permitted; these are compilers and editors, **not frame code**."* The boot sequence and the
frame loop are frame code. Living in `Tools/` meant four things were true of the single
most-depended-on package in the repository:

* `IsAotCompatible=false`, and no trim, AOT or single-file analyzers — while `Samples/01.iOS` boots
  through it on a platform the RUNTIME profile calls NativeAOT-only;
* `Tools/Vixen.AotProbe` rooted every runtime assembly the host pulls in, and not the host;
* `nuke CheckApi` baselines `Core/**` and `Platform/**`, so the host's public surface could change
  without a diff;
* `nuke CheckDocs` never asked it for a guide page.

Moving it fixes all four at once, and the project fought its folder either way — it already had to
override `IsPackable` and `GenerateDocumentationFile` by hand.

## What could not come with it

`CheckArchitecture` fails the build when a `Core/` project references `Platform/`:

> `{name} is in Core and references {reference}, which is not.`

Two functions needed exactly that. `GraphicsHost.Create` picks between `Vixen.Graphics.Vulkan` and
`Vixen.Graphics.Null`; `PlatformHost.Create` picks between `Vixen.Platform.Desktop` and
`Vixen.Platform.Headless`. So they stayed above `Platform/`, in `Tools/Vixen.App`, and the choice
arrives here as `IPlatformFactory` and `IGraphicsBackend`.

Nothing else needed the seam. Building a swapchain looked like it did and does not — every backend
implements `IGraphicsDevice.CreateSwapChain`, so `AppGraphics.SwapChainFor` and
`AppGraphics.FramebufferOf` are plain code with no backend in them.

⚠ **An `AppBuilder` with neither refuses to build, by name.** Falling back to something that opens no
window would turn "this head forgot to install its backends" into a game that boots, runs and shows
nothing — which is the hardest failure in the whole boot path to attribute, and the one the fallback
to headless is *already* used for legitimately.

## The other move

`Vixen.Platform` — the contracts: `IPlatform`, `IWindow`, `IClipboard`, `ILifecycle`, the input and
event types — moved from `Platform/` to `Core/` in the same change, and had to move first.

[Doc 02](../../docs/plan/02-repository-layout.md)'s justification for that folder is that *"backend
projects live under `Platform/` rather than `Core/` because they are platform **implementations** of
a `Core/` contract."* `Vixen.Platform` is not an implementation; it is the contract, and it
references nothing but `Vixen.Core`, `Vixen.Core.IO` and `Vixen.Core.Mathematics`. Filing it as an
implementation had one concrete consequence: no `Core/` assembly was permitted to say the word
"window", which is what kept the host out of this folder in the first place.

Licensed under Apache-2.0.
