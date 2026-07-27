# Spike: HarfBuzzSharp as the shaping engine — ✅ **PASSED, with one thing unverified**

Run on macOS arm64, .NET SDK 10.0.302, HarfBuzzSharp **14.2.1.1** — the version
[doc 01](../../01-technology-decisions.md) pins.

Doc 09 calls shaping "non-negotiable" and names HarfBuzzSharp for it, and that is right: nothing else
gets Arabic, Indic, emoji clusters and variable fonts correct. But it is the **first native dependency
in the UI stack**, and Phase 4's exit says `Samples/02` runs *in a browser* while Phase 3's still owes
an iOS NativeAOT publish. Sequencing rule 3 says spike the unknown before planning around it, so this
is that — asked before a line of shaping code exists rather than after.

`Probe.cs` beside this file is what was run.

## What was proven

**The pinned version is real and restores.** Worth checking first: the version register said
14.2.1.1 and the package's own history jumps 8.3.1.x → 14.2.x, which is the kind of gap that is
sometimes a typo. It is not — 14.2.1.1 is the current release.

**Shaping works, and does the things only a shaper does.**

```
سلام   4 UTF-16 units → 3 glyphs      clusters 3, 1, 0
AVA    3 units → 3 glyphs             advances 607, 607, 683
á      2 units (e + combining acute) → 1 glyph
```

Arabic joins its letters and comes out with fewer glyphs than characters. Kerning fires — the same
glyph id 36 appears with advance 607 when a `V` follows it and 683 when nothing does. A decomposed
`á` shapes to the single precomposed glyph.

⚠ **Two API facts worth knowing before writing against it.** For right-to-left runs the glyphs come
back in **visual** order with cluster indices *descending*, not in logical order — so a caller
mapping glyphs to characters must not assume clusters ascend. And a cluster is a *range*, identified
by its first character: `á` is one glyph at cluster 0 covering two code points, and the end of a
cluster is only knowable from where the next one starts.

**NativeAOT works, with no IL warnings at all.**

```
dotnet publish -c Release -r osx-arm64      (PublishAot=true, TrimmerSingleWarn=false)
  → hbspike                1.3 MB
  → libHarfBuzzSharp.dylib 2.8 MB
```

Zero `IL2xxx`/`IL3xxx` warnings, and the published binary produces byte-identical output to the JIT
run. This is a stronger result than the ExCSS spike could give: there the analyzers could not see
into an unannotated dependency, so a clean build was *absence of evidence*. Here the managed surface
is a thin P/Invoke layer and the analyzers can see all of it.

**Every platform the plan targets has a native asset package at the same version** —
`WebAssembly`, `iOS`, `Android`, `Linux`, `Win32`, all at 14.2.1.1.

**iOS ships a framework for device and simulator both**, 2.8 MB each, under
`runtimes/ios/native/` and `runtimes/iossimulator/native/`. A `.framework` rather than a static `.a`,
which is what an embedded-and-signed iOS app wants.

**WebAssembly ships static archives, and the Emscripten versions line up.**

```
buildTransitive/netstandard1.0/libHarfBuzzSharp.a/3.1.34/{st,mt}/libHarfBuzzSharp.wasm.a
buildTransitive/netstandard1.0/libHarfBuzzSharp.a/3.1.56/{st,mt}/libHarfBuzzSharp.wasm.a
```

This was the risk worth spiking. A static archive has to be linked by the *same* Emscripten the .NET
WASM build uses, and the package chooses which versions it supports. It ships 3.1.34 and 3.1.56 —
and .NET 10's `Microsoft.NET.Workload.Emscripten.Current.Manifest-10.0.100` pins
**`Emscripten.3.1.56.Sdk`**. They match, single-threaded and multi-threaded both.

## What was not verified

⚠ **A WASM link was not actually performed.** The `wasm-tools` workload is not installed on this
machine, so the version alignment above is read from the two manifests rather than demonstrated by a
build. It is a strong signal and it is not a proof, and the distinction is the same one the ExCSS
spike drew about the trim analyzers.

The consequence if it turns out wrong is bounded and worth stating: a WASM build would need
`wasm-tools` and a native relink, which is already true of any WASM build that uses native code, and
the fallback is a managed shaper for the browser target only.

⚠ **No iOS device build was made either.** The framework exists at the right version; whether it
survives Phase 3's NativeAOT publish is that publish's job to answer, and it is already owed.

## What this means for the design

**Bidi comes before shaping, and it is already built.** HarfBuzz shapes one run at a time and wants
runs already itemised — by direction first, then by script, then by font. That is exactly the order
`Vixen.Ui.Text` now has: UAX#9 produces the direction runs, and shaping consumes them. Had shaping
been built first it would have been built against a run model that did not exist.

**The cluster model has to be reconciled with the grapheme model, not assumed equal to it.**
HarfBuzz clusters are a shaping concept and grapheme clusters are a user-perceived-character concept,
and they agree often enough to be dangerous. A caret moves in graphemes; a glyph is drawn per shaping
cluster; the mapping between them is Vixen's to maintain.

## Verdict

Doc 01's choice stands, and nothing about it needs to change. The one thing to carry forward is that
**the WASM path is a version-coupled static link**, so a bump of either HarfBuzzSharp or the .NET SDK
is a bump that has to be checked against the other. Cheap to know now.
