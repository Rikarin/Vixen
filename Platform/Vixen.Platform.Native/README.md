# Vixen.Platform.Native

Where a native library comes from: the `runtimes/<rid>/native/` layout a published application ships,
the RID fallback chain, the file names each platform actually uses, and the `DllImportResolver` that
makes all three answer before the operating system is asked.

Spec: [docs/plan/10](../../docs/plan/10-platforms.md) § Native binaries,
[docs/plan/15](../../docs/plan/15-risks-and-open-questions.md) § R10, R11.

```csharp
NativeLibraries.Describe(new("vulkan", ["1"], ["/opt/homebrew/lib"]));
NativeLibraries.Register(typeof(Silk.NET.Vulkan.Vk).Assembly);
```

## The application's own files first, the system last

A published game ships its natives in `runtimes/<rid>/native/` — the layout NuGet produces and
`dotnet publish` preserves — and those are the versions it was built and tested against. Asking the
operating system first would let a machine with an older system-wide copy silently win, which is the
shape of every "works on my machine" report ever filed about a native dependency.

Search order is **directory-major**: every candidate file name is tried in the most specific directory
before the next directory is considered. Name-major would prefer a system copy under the exact file
name over the application's own copy under a versioned one — the exact preference this inverts.

## The versioned soname is the file that actually exists

`libvulkan.so` and `libvulkan.dylib` are development symlinks, installed by the *-dev* package and
absent from a runtime-only install. The real files are `libvulkan.so.1` and `libvulkan.1.dylib`. Miss
them and you fail to load a library that is sitting right there, with an exception that does not say
which name it tried.

That is not hypothetical — it is the first thing that went wrong when the Vulkan backend met a real
driver, and `Vixen.Graphics.Vulkan`'s own `VulkanLoader` carries the same knowledge for the one case
it had to solve before this project existed.

## The RID chain is computed, not looked up

.NET's RID graph is a build-time artefact: it lives in NuGet's dependency resolution and in
`runtimeconfig.json`, and a NativeAOT application has neither — it is one native binary. So the chain
is written here, in the four lines it takes, rather than read from a file that will not be present on
the target this project exists for.

Architecture-specific first (`osx-arm64`), then architecture-neutral (`osx`), because a great many
native packages ship one directory per operating system and let the fat binary inside sort out the
architecture.

## What this fixes, and what it does not

**It exists because of a measured failure.** Silk.NET locates a native library through
`Assembly.Location` and `DependencyContext.Default`. A NativeAOT application has neither, so
`nuke CheckAot` reports six IL3000/IL3002 diagnostics saying exactly that — see
[R11](../../docs/plan/15-risks-and-open-questions.md).

**A registered resolver runs before the default rules**, so the engine's own layout answers first and
the binding library's probing is never reached at run time. That is the functional fix.

**The diagnostics are gone too, but not because of this.** This README used to say they would need a
suppression, on the reasoning that ILC's analysis is static and code unreachable *in practice* stays
reachable *in the graph*. That is true while something still calls it. All six came from
`Vk.GetApi()`, and `VulkanLoader` no longer calls it — it loads through this project and builds `Vk`
over a `LamdaNativeContext` instead — so `DefaultPathResolver` left the graph as well and the probe
reports zero. No suppression was taken. Verified as a cause rather than a coincidence by putting the
call back, which brings all six straight back.

**iOS is a different problem entirely**, and this project owns the answer without the resolver being
part of it. Everything links statically there, so there is no resolution step to intercept; what is
needed is MoltenVK's symbols in the executable and *exported*, which
[`build/MoltenVK.targets`](build/MoltenVK.targets) arranges. `VulkanLoader` then asks the process
image itself. R11 records both halves, after the first write-up of it conflated them.

## Falling through matters as much as succeeding

`Resolve` returns `IntPtr.Zero` for anything it cannot find, which hands the question back to the
runtime's default rules. Every library the engine does not know about — and every one it does, on a
machine where the system copy is the only copy — has to reach those rules unchanged. A resolver that
answered every question would turn one unshipped dependency into a total failure to start.

## Still to come

**Acquisition is built, and holds one dependency.** `build/native-dependencies.json` and
`nuke RestoreNativeDeps` do the pinning, the SHA-256 verification, the extraction and the licence
manifest ([doc 10](../../docs/plan/10-platforms.md) § Native binaries, R10). MoltenVK for `ios-arm64`
is in it. Jolt, HarfBuzz, SPIRV-Cross, astcenc and Recast are not, and the `.zip` and `.tar.gz` paths
have not yet met a real dependency.

**Both call sites are wired up.** `Vixen.Graphics.Vulkan` and `Vixen.Platform.Desktop` load through
this project and construct their Silk.NET API over the handle, so neither `Vk.GetApi()` nor
`Sdl.GetApi()` is called and Silk's `DefaultPathResolver` is not in the graph at all. Rooting both in
`nuke CheckAot` reports **zero** IL3000/IL3002. Each was checked by putting its `GetApi()` back, which
brings six straight back.

**No suppression has been taken, anywhere in the repository.** That was the expected cost of clearing
this gate and it turned out not to be owed.

Licensed under Apache-2.0.
