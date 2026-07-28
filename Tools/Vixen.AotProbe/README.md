# Vixen.AotProbe

The subject of the NativeAOT gate. Not a tool anybody runs — an executable that exists so that
`nuke CheckAot` has something to publish.

Spec: [docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 3, and
[docs/plan/10](../../docs/plan/10-platforms.md) § iOS.

```bash
./build.sh CheckAot
```

## Why it roots rather than calls

ILC analyses what is *reachable*. A probe that constructs a few types and calls a few methods proves
those few clean and says nothing about the rest — an assembly could be full of reflection nobody
happened to call from the probe, and the gate would be green until the day somebody called it.

So every runtime assembly is a `TrimmerRootAssembly`, which asks ILC to compile every method in it.
That is the question actually being asked: *is this assembly publishable ahead of time*, not *is this
one path through it*.

The difference is visible in the output. Rooted, the binary is about 8 MB; the same probe relying on
reachability from `Main` produced 1.3 MB. Nothing was trimmed away unexamined, which is the point.

## What it covers, and the one rule every binding-library backend follows

Every `Core/` assembly, plus `Vixen.Platform`, `Vixen.Platform.Native`, `Vixen.Platform.Headless`,
`Vixen.Platform.Desktop`, `Vixen.Graphics.Null`, `Vixen.Graphics.Vulkan` and
`Vixen.Audio.Backend.OpenAL`. All of them publish with **zero** trim or AOT warnings, and the
resulting binary runs.

`Vixen.Audio.Codecs` is in the list for a reason worth stating: NVorbis and Concentus are third-party
decoders, and rooting them here is what makes "both are pure managed and survive trimming" a checked
fact rather than a claim on a NuGet page.

**The three Silk.NET-based backends are in the list only because none of them calls `GetApi()`.**
That call builds Silk.NET's default context, which finds a native library by asking where its own
managed assembly is on disk (`Assembly.Location`) and by reading the dependency manifest
(`DependencyContext.Default`). A NativeAOT application has neither, and rooting an assembly that
reaches it produces six errors, every one inside a dependency:

```
IL3000  Silk.NET.Core.Loader.DefaultPathResolver…  'Assembly.Location' always returns an empty string
IL3002  Silk.NET.Core.Loader.DefaultPathResolver…  'Assembly.CodeBase' throws in a single-file app
IL3002  Silk.NET.Core.Loader.DefaultPathResolver…  'DependencyContext.Default' returns null
IL3000  Silk.NET.Core.Loader.DefaultPathResolver…  'Assembly.Location' …
IL3002  Silk.NET.Core.Loader.DefaultPathResolver…  'DependencyContext.Default' …
IL3002  Microsoft.Extensions.DependencyModel…      'DependencyContext.LoadDefault' …
```

These are not pedantic warnings; they are the loader telling the truth about itself.

So each backend loads its own library through `Vixen.Platform.Native` — which maps a RID to a
binary, knows the `runtimes/<rid>/native/` layout, and registers a `DllImportResolver` — and then
constructs the Silk.NET API object from a `LamdaNativeContext` over the handle. `VulkanLoader` and
`OpenALLoader` are the same thirty lines twice, and both of their file comments record that putting
the `GetApi()` call back brings all six diagnostics straight back. **A new Silk.NET backend that
calls `GetApi()` will fail this gate, and that is the intended outcome.**

**On iOS the failure is a different one, and a resolver does not fix it** — see
`../Vixen.AotProbe.iOS`. Everything links statically there, so Silk.NET's `DllImport`s become symbol
references and the link fails on twelve undefined `vk*` symbols because MoltenVK is not linked in.
Two causes, two fixes; R11 in [doc 15](../../docs/plan/15-risks-and-open-questions.md) carries both.

## The iOS sibling

`../Vixen.AotProbe.iOS` is the same gate for `ios-arm64`, run by `nuke CheckAotIos`. It is a separate
project and is **deliberately not in `Vixen.slnx`**: a `net10.0-ios` project cannot be evaluated at all
without the `ios` workload, so putting it in the solution would break `dotnet build` for every
developer and CI leg that is not a Mac with Xcode. The cost is that `CheckFormat` does not see its two
files.

It also needs `using Foundation;` in its entry point. That is not decoration — without a reference to
the platform assembly the managed registrar fails the link with `MT0099: No platform assembly!`, which
is a confusing way to be told that a console `Main` is not an iOS application.

## Why `PublishAot` is in the project file

Not `-p:PublishAot=true` on the command line. A command-line property is a *global* property: MSBuild
hands it to every project in the graph, including the `netstandard2.1` source generators, and a
generator asked to compile ahead of time fails with `NETSDK1207`. Declaring it in the one project
that means it keeps it where it belongs.

Warnings are errors here (`ILLinkTreatWarningsAsErrors`), and `TrimmerSingleWarn` is off so each one
names its own method rather than collapsing into "this assembly has warnings".

Licensed under Apache-2.0.
