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

**It does not silence those diagnostics, and that was verified rather than assumed.** Rooting
`Vixen.Graphics.Vulkan` in the AOT probe with this project in place still reports six. ILC's analysis
is static: code that is unreachable *in practice* is still reachable *in the graph*. Suppressing them
is a separate, deliberate decision that only becomes defensible once this is in force — and it is
deliberately not made here, because a suppression and the thing that justifies it should not arrive in
the same commit.

**iOS is a different problem entirely.** Everything is statically linked there, so there is no
resolution step to intercept; what is needed is MoltenVK's symbols at link time. R11 records both
halves, after the first write-up of it conflated them.

## Falling through matters as much as succeeding

`Resolve` returns `IntPtr.Zero` for anything it cannot find, which hands the question back to the
runtime's default rules. Every library the engine does not know about — and every one it does, on a
machine where the system copy is the only copy — has to reach those rules unchanged. A resolver that
answered every question would turn one unshipped dependency into a total failure to start.

## Still to come

**Acquisition.** [Doc 10](../../docs/plan/10-platforms.md) and R10 also put pinned versions,
checksummed download URLs, SHA-256 verification and a generated licence manifest under this project's
name, restored by a Nuke target and never committed. That half is a build concern —
[doc 02](../../docs/plan/02-repository-layout.md) already reserves `build/Build.Native.cs` for it — and
it is not built. This project is the runtime half: given that the binaries are *there*, it is what
finds them.

**Nothing registers the resolver yet.** `Vixen.Graphics.Vulkan` and `Vixen.Platform.Desktop` still use
their own loading, which works today because neither is published ahead of time. Wiring them up is the
change that makes an AOT desktop build load its natives, and it belongs with the acquisition half that
puts the binaries where this looks for them.

Licensed under Apache-2.0.
