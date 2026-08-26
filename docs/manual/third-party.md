# Third-party attribution

Vixen is Apache-2.0 ([`LICENSE`](../../LICENSE)). This file is the manifest of everything in a build
that is **not** — the managed packages, the native binaries, and the third-party data committed into
the tree — with the licence of each and, for every one of them, **where that licence was read from**.

**Why this exists.** Apache-2.0 §4(d) requires the [`NOTICE`](../../NOTICE) to travel with every
distribution, and most of the licences below — MIT, BSD, Apache-2.0, Zlib, OFL — separately require
their own notice to be reproduced in binary distributions. That obligation is not discharged by a
link. It is discharged by shipping the text, and the first step is knowing precisely which texts.

**Why the licence column is trustworthy in a specific, limited way.** Nothing here is stated from
memory or inferred from a package name. Each row names its evidence, and where the evidence does not
exist offline the row says *unresolved* rather than guessing. An attribution file that confidently
states a wrong licence is worse than one that admits a gap, because the gap gets closed and the
confident error gets shipped.

## How to read the Source column

| Code | Means |
|---|---|
| `nuspec` | The `<license type="expression">` element of the package's own `.nuspec`, read from the restored package in the NuGet cache. This is the publisher's machine-readable declaration and is what NuGet itself displays. |
| `nuspec + text` | As above, but the nuspec points at a licence *file* (`<license type="file">`) and that file ships inside the package. The text was read, and the row names the copyright holder it carries. |
| `manifest` | Recorded in [`build/native-dependencies.json`](../../build/native-dependencies.json) beside the pinned URL and SHA-256. |
| `in-tree text` | A licence file committed in this repository, at the path given. |
| **unresolved** | ⚠ The package ships **no** licence expression and **no** licence text. What is known is recorded; the rest needs a network fetch that has not been done. |

---

## ⚠ Unresolved — four packages, read this first

These four ship no machine-readable licence and no licence file. Each carries a `licenseUrl`
pointing at a file in its upstream repository, which is a promise about a URL rather than a copy of a
text, and a URL's contents can change. **Do not ship a binary distribution until these are confirmed
from upstream and their texts are obtained.**

| Package | What the package actually says | What is claimed elsewhere in this repo | What is needed |
|---|---|---|---|
| `StbImageSharp` 2.30.15 | **Nothing.** No `<license>` element, no `licenseUrl`, no `<copyright>`, and no licence file in the package — only two `.dll`s. Authors: `StbImageSharpTeam`. | [`Directory.Packages.props`](../../Directory.Packages.props) and `docs/overview.md` § 2.1 both say "public domain". | The claim is plausible — upstream `stb_image.h` is dual public-domain/MIT — but **the package asserts it nowhere**. Confirm from `github.com/StbSharp/StbImageSharp` and commit the text. This is the weakest link in the set: it is the only dependency whose licence rests on no artefact at all. |
| `K4os.Compression.LZ4` 1.3.8 | `licenseUrl` → `github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE`. `<copyright>Milosz Krajewski</copyright>`. No licence file in the package. | `NOTICE` says MIT, and notes explicitly that this package "declares its licence in a file rather than as an SPDX expression, so it is not picked up automatically". | `NOTICE` is right that it needs recording by hand, and the note predates this file. Fetch the LICENSE and commit the text. |
| `Antlr4.Runtime` 4.6.6 | `licenseUrl` → `raw.github.com/tunnelvisionlabs/antlr4cs/master/LICENSE.txt`. `<copyright>Copyright © Sam Harwell 2015</copyright>`, authors Sam Harwell and Terence Parr. | `NOTICE` says "BSD-3-Clause — https://github.com/antlr/antlr4". | ⚠ **The URL in `NOTICE` names the wrong project.** This is `tunnelvisionlabs/antlr4cs`, Sam Harwell's C# target, not `antlr/antlr4`. Both are BSD-3-Clause as far as is known, but the notice must attribute the work actually redistributed. |
| `Antlr4.CodeGenerator` 4.6.6 | As above. | As above. | As above. |

---

## ⚠ Copyleft — one dependency, and it ships in every game with audio

**`Silk.NET.OpenAL.Soft.Native` 1.23.1 declares `LGPL-2.0-or-later`** in its `.nuspec` — a
machine-readable SPDX expression, not an inference. It carries `libopenal.so`, `libopenal.dylib` and
`soft_oal.dll` for eight runtime identifiers, and [`Directory.Packages.props`](../../Directory.Packages.props)
takes it deliberately, because "there is no OpenAL on a stock Windows or Linux box" and the point is
that "a published game needs nothing installed". So this is not an optional or import-time
dependency: it is redistributed in every shipped build that plays sound.

Two things follow, and neither is recorded anywhere else in the repository:

1. **The LGPL's obligations attach.** At minimum: the licence text must ship, the library must be
   identified as LGPL, and the recipient must be able to replace it with a modified version. Dynamic
   linking against a separately-shipped shared library — which is what the desktop RIDs above do — is
   the arrangement the LGPL is designed to accommodate, so this is dischargeable. It is not
   discharged today.
2. **Static linking is a different question and it is not answered.** `docs/plan/10` makes
   `ios-arm64` NativeAOT-only, where a `.dylib` is not loadable. If OpenAL Soft is ever linked
   statically into a shipped binary, LGPL §6 requires shipping relinkable objects or equivalent. The
   package publishes no iOS slice today, so the question has not yet arisen — but it will arise from
   the same direction the MoltenVK static-library work came from, and it should be answered before
   then rather than during.

⚠ **The package contradicts itself, and this is worth knowing.** Its bundled `README.md` — Silk.NET's
generic repository readme — states that Silk.NET "is distributed under the very permissive MIT/X11
license and all dependencies are distributed under MIT-compatible licenses." That sentence is about
Silk.NET's *bindings* and is wrong about this package's payload: upstream OpenAL Soft is LGPL, and
the nuspec agrees. **The nuspec is the more specific and more authoritative statement about this
package**, and this file follows it. Anyone re-auditing should not be reassured by the README.

---

## Managed packages

Ground truth is [`Directory.Packages.props`](../../Directory.Packages.props). Central package
management makes it the sole authority: a `csproj` carrying an inline version is an NU1008 build
error, so there is no second place a version can come from. Every pin below is checked against it by
[`CheckAttribution`](../../build/Build.Attribution.cs) — see *Keeping this true*.

Rows marked **build/test only** do not reach a shipped game or editor; the reasoning is in
*What is excluded* below.

<!-- attribution:managed:begin -->

### Graphics, windowing, audio and XR bindings

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `Silk.NET.Core` | 2.23.0 | MIT | `nuspec` | `Silk.NET.Maths` arrives with it and is transitive, not pinned |
| `Silk.NET.SDL` | 2.23.0 | MIT | `nuspec` | ⚠ Bindings are MIT; it hard-depends on `Ultz.Native.SDL` (Zlib), which ships `libSDL2` — see *Native libraries that arrive through managed packages* |
| `Silk.NET.Vulkan` | 2.23.0 | MIT | `nuspec` | Bindings only; loader and ICD come from the platform |
| `Silk.NET.Vulkan.Extensions.KHR` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.Vulkan.Extensions.EXT` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.WebGPU` | 2.23.0 | MIT | `nuspec` | Bindings only; the library is `wgpu-native`, below |
| `Silk.NET.OpenGL` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.OpenGLES` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.OpenXR` | 2.23.0 | MIT | `nuspec` | Bindings only; the loader belongs to the headset runtime |
| `Silk.NET.OpenXR.Extensions.KHR` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.OpenAL` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.OpenAL.Extensions.Enumeration` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.OpenAL.Extensions.EXT` | 2.23.0 | MIT | `nuspec` | |
| `Silk.NET.OpenAL.Soft.Native` | 1.23.1 | **LGPL-2.0-or-later** | `nuspec` | ⚠ **The implementation, not bindings** — read the copyleft section above before shipping |

### Physics

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `JoltPhysicsSharp` | 2.22.0 | MIT | `nuspec` | Brings `JoltPhysics.Native` (MIT, Jorrit Rouwe) — see *Transitive dependencies* |

### UI, text and styling

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `ExCSS` | 4.3.2 | MIT | `nuspec` | The VCSS front end |
| `HarfBuzzSharp` | 14.2.1.1 | MIT | `nuspec` | ⚠ The nuspec covers the *binding*. Upstream HarfBuzz is the "Old MIT" licence, which is MIT-compatible but a distinct text; the native assets below are what carry it |
| `HarfBuzzSharp.NativeAssets.macOS` | 14.2.1.1 | MIT | `nuspec` | Ships `libHarfBuzzSharp.dylib` |
| `HarfBuzzSharp.NativeAssets.Linux` | 14.2.1.1 | MIT | `nuspec` | |
| `HarfBuzzSharp.NativeAssets.Win32` | 14.2.1.1 | MIT | `nuspec` | |

### Serialization, compression and hashing

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `System.IO.Hashing` | 10.0.10 | MIT | `nuspec` | |
| `K4os.Compression.LZ4` | 1.3.8 | **unresolved** (claimed MIT) | — | ⚠ See *Unresolved*. Copyright: Milosz Krajewski |
| `ZstdSharp.Port` | 0.8.8 | MIT | `nuspec` | |
| `YamlDotNet` | 18.1.0 | MIT | `nuspec` | Low-level scanner and parser only |

### Audio codecs

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `NVorbis` | 0.10.5 | MIT | `nuspec + text` | Text read from the package: "Copyright (c) 2020 Andrew Ward" |
| `Concentus` | 2.2.2 | BSD-3-Clause | `nuspec + text` | Text read from the package. It is the Opus/Xiph three-clause notice, held by "Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation, Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry, Logan Stromberg" — ⚠ the notice must reproduce the holders, not just the SPDX id |

### Logging and telemetry

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.10 | MIT | `nuspec` | |
| `ZLogger` | 2.5.10 | MIT | `nuspec` | |
| `OpenTelemetry` | 1.17.0 | Apache-2.0 | `nuspec` | |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.17.0 | Apache-2.0 | `nuspec` | |
| `OpenTelemetry.Exporter.Console` | 1.17.0 | Apache-2.0 | `nuspec` | |
| `OpenTelemetry.Instrumentation.Runtime` | 1.17.0 | Apache-2.0 | `nuspec` | |

### Server control plane (`Live/` only)

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `Microsoft.Orleans.Sdk` | 10.2.2 | MIT | `nuspec` | |
| `Microsoft.Orleans.Server` | 10.2.2 | MIT | `nuspec` | |
| `Microsoft.Orleans.Client` | 10.2.2 | MIT | `nuspec` | |
| `KubernetesClient` | 19.0.2 | Apache-2.0 | `nuspec` | |

### Editor and tooling — import time, never in a runtime assembly

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `StbImageSharp` | 2.30.15 | **unresolved** (claimed public domain) | — | ⚠ See *Unresolved*. The package declares nothing at all |
| `Silk.NET.Assimp` | 2.23.0 | MIT | `nuspec` | Brings `Ultz.Native.Assimp` (BSD-3-Clause) — see below |
| `System.CommandLine` | 2.0.10 | MIT | `nuspec` | |

### Compiler front ends and generators

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `Microsoft.CodeAnalysis.CSharp` | 4.11.0 | MIT | `nuspec` | |
| `Microsoft.CodeAnalysis.Analyzers` | 3.3.4 | MIT | `nuspec` | |
| `Antlr4.Runtime` | 4.6.6 | **unresolved** (claimed BSD-3-Clause) | — | ⚠ See *Unresolved*. **build/test only** — a differential oracle in `Vixen.Raven.Tests` |
| `Antlr4.CodeGenerator` | 4.6.6 | **unresolved** (claimed BSD-3-Clause) | — | ⚠ See *Unresolved*. **build/test only** |

### Build, benchmark and test — none of it distributed

| Package | Version | Licence | Source | Note |
|---|---|---|---|---|
| `Nuke.Common` | 10.1.0 | MIT | `nuspec` | **build only** |
| `BenchmarkDotNet` | 0.15.8 | MIT | `nuspec` | **build/test only** |
| `xunit.v3` | 3.2.2 | Apache-2.0 | `nuspec` | **test only** |
| `xunit.runner.visualstudio` | 3.1.5 | Apache-2.0 | `nuspec` | **test only** |
| `Microsoft.NET.Test.Sdk` | 18.8.1 | MIT | `nuspec` | **test only** |
| `CsCheck` | 4.7.0 | Apache-2.0 | `nuspec` | **test only** |
| `Npgsql` | 9.0.4 | PostgreSQL | `nuspec` | **test only** — one test project. The PostgreSQL licence is a permissive BSD-style text, distinct from MIT |

<!-- attribution:managed:end -->

## Native binaries fetched by `RestoreNativeDeps`

Ground truth is [`build/native-dependencies.json`](../../build/native-dependencies.json). None of
these is committed: `nuke RestoreNativeDeps` downloads each archive, refuses it unless its SHA-256
matches, and extracts only the named files into `artifacts/`, which git ignores. It also copies each
licence text out of the archive it was verified from into `artifacts/native/licences/` and writes
`artifacts/native/THIRD-PARTY-NATIVE.md` — so for these two, a *verified text* exists after a
restore, which is a stronger position than any managed package is in.

<!-- attribution:native:begin -->

| Dependency | Version | Licence | Source | Note |
|---|---|---|---|---|
| `moltenvk` | 1.4.1 | Apache-2.0 | `manifest` | Licence text extracted from the archive (`MoltenVK/LICENSE`). Static library, `ios-arm64` only |
| `wgpu-native` | 0.19.4.1 | MIT OR Apache-2.0 | `manifest` | ⚠ **No text is extracted** — these releases ship headers, libraries and a commit sha and no licence file. An application that ships the library must obtain the upstream text itself |

<!-- attribution:native:end -->

⚠ **This manifest is incomplete, and knowingly so.** It holds exactly two entries while considerably
more native code is used — `libSDL2`, `libassimp`, `libjoltc`, `libopenal` and HarfBuzz all ship, but
arrive through NuGet rather than through this file, and `astcenc`/`ispc_texcomp` are registered in
doc 01 and unbound. Closing that gap is tracked as **task #212**; `docs/overview.md` § 2.3 carries the
same list. The section immediately below is what covers the difference in the meantime, and it is the
reason this page does not simply defer to the manifest.

## Native libraries that arrive through managed packages

⚠ **This is the category the acquisition manifest cannot see, and the one with real obligations.**
These are not pinned in `build/native-dependencies.json`, are not fetched by `RestoreNativeDeps`, and
are not centrally pinned in `Directory.Packages.props` either — they are transitive dependencies of
packages that are. They nonetheless land in `runtimes/<rid>/native/` and ship.

| Library | Arrives as | Licence | Source | Ships |
|---|---|---|---|---|
| **SDL 2.32.10** | `Ultz.Native.SDL` 2.32.10, an unconditional dependency of `Silk.NET.SDL` 2.23.0 in every target-framework group | Zlib | `nuspec` | 11 binaries: `libSDL2-2.0.so` (linux-arm/arm64/x64), `libSDL2-2.0.dylib` (osx), `libSDL2.a` (ios, iossimulator, tvos, tvossimulator), and the Windows slices |
| **Assimp 6.0.2** | `Ultz.Native.Assimp` 6.0.2, via `Silk.NET.Assimp` | BSD-3-Clause | `nuspec` | `libassimp.5/6.dylib` (osx-arm64/x64), `Assimp64.dll` (win-x64/arm64), `Assimp32.dll` (win-x86). Editor and tooling only |
| **Jolt Physics** | `JoltPhysics.Native` 1.1.0, via `JoltPhysicsSharp` | MIT | `nuspec + text` | Text read from the package (`LICENSE.MIT.txt`): "Copyright 2021 Jorrit Rouwe". `libjoltc.so` (android ×3, linux ×2), `libjoltc.dylib` (osx), `joltc.dll`/`joltc_double.dll` (win) |
| **OpenAL Soft** | `Silk.NET.OpenAL.Soft.Native` 1.23.1 — pinned, so it has a row above too | **LGPL-2.0-or-later** | `nuspec` | ⚠ See the copyleft section |
| **HarfBuzz** | `HarfBuzzSharp.NativeAssets.*`, pinned | MIT (per the binding's nuspec) | `nuspec` | ⚠ Upstream HarfBuzz uses the "Old MIT" text, and the packages ship no licence file. Obtain the upstream text before a binary distribution |

⚠ **`NOTICE` currently states the opposite about SDL** — that "the library itself is not vendored and
comes from the system", and that the obligation would arise only "before the bundling happens". The
bundling has happened: `Silk.NET.SDL` 2.23.0's nuspec lists `Ultz.Native.SDL` as a dependency in all
five target-framework groups, with no condition. `docs/overview.md` § 2.3's row saying `libSDL2` comes
"From the system" is wrong for the same reason. Correcting both is listed under
*What `NOTICE` gets wrong*.

## Transitive dependencies

⚠ **The transitive graph is not pinned.** `Directory.Packages.props` sets
`CentralPackageTransitivePinningEnabled` to `false` deliberately — it "makes the graph rigid and
interacts badly with analyzer/generator packages" — with a note to revisit if supply-chain policy
demands it. **A distribution notice is such a policy.** The consequence is that the exact transitive
versions a build resolves are a function of restore, not of a committed file, so this page can
enumerate the graph but cannot pin it, and neither can the gate.

The first-level transitive set is 92 packages. All were read from the NuGet cache; the ones that are
neither BCL nor Microsoft-published, and therefore actually add attribution, are:

| Package | Licence | Source | Comes from |
|---|---|---|---|
| `Ultz.Native.SDL` | Zlib | `nuspec` | `Silk.NET.SDL` — ships `libSDL2`, above |
| `Ultz.Native.Assimp` | BSD-3-Clause | `nuspec` | `Silk.NET.Assimp` — ships `libassimp`, above |
| `JoltPhysics.Native` | MIT | `nuspec + text` | `JoltPhysicsSharp` — ships `libjoltc`, above |
| `Silk.NET.Maths` | MIT | `nuspec` | `Silk.NET.SDL`, `.OpenGL`, `.OpenGLES`, `.OpenXR`, `.Assimp` |
| `Ultz.Bcl.Half` | MIT | `nuspec` | `Silk.NET.OpenGL` |
| `Utf8StringInterpolation` | MIT | `nuspec` | `ZLogger` |
| `Newtonsoft.Json` | MIT | `nuspec` | Orleans — `Live/` only |
| `Fractions` | BSD-3-Clause style | `nuspec + text` | `KubernetesClient` — text read: "Copyright (c) 2013-2022, Daniel Mueller" |
| `Iced`, `Gee.External.Capstone`, `Perfolizer`, `CommandLineParser` | MIT | `nuspec` / `nuspec + text` | `BenchmarkDotNet` — **build/test only** |
| `Octokit`, `Azure.Identity`, `Azure.Security.KeyVault.*`, `Nuke.*` | MIT | `nuspec` | `Nuke.Common` — **build only** |
| `xunit.v3.mtp-v1` | Apache-2.0 | `nuspec` | `xunit.v3` — **test only** |

The remainder are `System.*`, `Microsoft.Extensions.*`, `Microsoft.CodeAnalysis.*`,
`Microsoft.TestPlatform.*` and `NETStandard.Library` — the .NET platform, MIT, published by Microsoft
and covered by the runtime's own notice.

## Third-party data committed into the tree

Distinct from code, and the category most easily missed: no implementation is taken, but expected
values, fixtures and typefaces are, and those are the copyrightable part.

| What | Where | Licence | Source |
|---|---|---|---|
| **Open Sans** (`OpenSans-Regular.ttf`, `OpenSans-SemiBold.ttf`) | `Editor/Vixen.Editor.App/Fonts/` | OFL-1.1 | `in-tree text` — `Editor/Vixen.Editor.App/Fonts/OFL.txt`, "Copyright (c) 2011, Steve Matteson" |
| **text-rendering-tests fonts** — 22 `.ttf`/`.otf` | `Core/Vixen.Ui.Text.Tests/Fonts/` | Suite Apache-2.0; fonts OFL-1.1 | `in-tree text` — the directory `README.md`. ⚠ **No licence file ships in this directory**, unlike the editor's. ⚠ Three Monotype faces carry a *proprietary* notice in their own `name` table; the redistribution basis argued is that they are Data Files of the Unicode-licensed suite. Holders include Google Inc., Unicode Inc., Monotype Hong Kong Ltd. and Monotype Imaging Inc., Thomas A. Rickner, and The Font Bureau, Inc. |
| **Yoga conformance suite** — 534 fixtures, translated | `Core/Vixen.Ui.Layout.Tests/Generated/` | MIT | `NOTICE` |
| **Taffy conformance suite** — 5 524 fixtures, verbatim | `Core/Vixen.Ui.Layout.Tests/Taffy/Corpus/` | MIT | `NOTICE`, and each XML file's own header names its source |
| **Unicode Character Database** 17.0.0 — derived tables, not the raw files | `Core/Vixen.Ui.Text/Generated/`, `Core/Vixen.Ui.Text.Tests/Generated/` | Unicode-3.0 | `NOTICE`; each generated file carries the Unicode terms-of-use URL |

The raw UCD files and the `references/` clones are **not** committed — `.gitignore` covers
`/references/*` but for its `README.md`, and the UCD is fetched locally. Nothing in `references/` is
built, restored or shipped.

### ⚠ Two data items that need a decision

1. **web-platform-tests is used as an oracle and appears in no notice.** Referenced from
   `Core/Vixen.Ui.Layout/LayoutTree.Order.cs`, `Core/Vixen.Ui.Layout.Tests/OrderTests.cs`,
   `Core/Vixen.Ui.Layout.Tests/InlineFormattingTests.cs` and `Core/Vixen.Ui.Tests/OrderTests.cs`.
   The tests describe the fixtures as "re-expressed rather than translated", which is a defensible
   position — but `docs/plan/43` § sets this repository's own bar and it is stricter: BSD-3 "requires
   the copyright notice and the disclaimer to travel with a redistribution, so a `NOTICE` entry is
   required the moment a translated fixture lands." WPT (BSD-3-Clause) is in neither the corpora table
   nor the reference-material table of `NOTICE`. **Someone has to decide which side of that line these
   fixtures fall on**; this file cannot decide it.
2. **`Core/Vixen.Ui.Layout.Tests/Taffy/TaffyAhemMeasure.cs` is a port and is not marked as one.**
   Its own comment says it is "Ported line for line from `AhemTextMeasureData::measure`, including the
   parts that look wrong", while `NOTICE`'s Taffy entry says no Taffy source is ported. Both cannot be
   true. The file carries a `Copyright (c) Rikarin` SPDX header. Taffy is MIT so an attribution line
   resolves it cheaply, but `docs/plan/43` requires a §4b modification notice on ported files as it
   does for Yoga, and this file has neither.

Also unrecorded, and probably harmless but unverified: six `.wav` fixtures under
`Samples/13-ThirdPersonShooter/Assets/Audio/` and three `.opus`/`.ogg` fixtures under
`Core/Vixen.Audio.Codecs.Tests/Fixtures/` have **no provenance statement** anywhere. Their names imply
synthesis; nothing in the tree says so.

## Reference material — read, not incorporated

Studied during design, no code copied. `NOTICE` holds the list and it is the authority: Recast/Detour
(zlib), Stride (MIT), Arch (Apache-2.0), Yoga (MIT), Taffy (MIT), Flexbox/ru-ace (BSD),
SignalsDotnet (MIT), PurrNet (MIT). Where an algorithm is re-derived the origin is credited at the
call site.

## What is excluded, and why

**Test-time and build-time tools are not in a distribution notice.** The obligations above attach to
*distribution* — shipping a copy. A tool that runs on a developer's machine or a CI agent and whose
output is data rather than derived code is not distributed by shipping the engine, so it creates no
notice obligation for a downstream user. Concretely, none of these is committed, packaged or shipped;
each is expected on `PATH` and installed by `.github/workflows/ci.yml`:

- **spirv-tools** (`spirv-val`, `spirv-dis`) — validation and disassembly in `Vixen.Fuzz` and Raven's
  golden tests.
- **shaderc** (`glslc`) — Raven's differential oracle. Note that this is deliberately the **CLI** and
  not `Silk.NET.Shaderc`, precisely "so shaderc's binaries never enter the restore" — the exclusion is
  enforced by the acquisition path, not merely asserted.
- **glslang** — optional; no CI leg installs it and the tests skip when it is absent.
- **spirv-cross, astcenc, ispc_texcomp** — planned, unbound, referenced by no code path that runs.
- **ktx tools** (`ktx validate`, Khronos KTX-Software, Apache-2.0) — the outside opinion in
  `Ktx2ConformanceTests`. Nothing links against libktx; the CLI is expected on `PATH`
  (`brew install ktx`) and the suite skips when it is absent. Its output is a verdict, not data that
  enters a build.
- **bcdec** (MIT / Unlicense, dual) — the reference BCn decoder in `BcnReferenceDecoderTests`.
  ⚠ **This one is a header, so read the exclusion carefully.** `bcdec.h` is *source*, and source
  compiled into something distributed would carry its notice — but it is not committed here and
  nothing distributed is built from it. `Tools/Vixen.BcnOracle/build.sh` downloads it, pinned to a
  commit, into `~/.cache/vixen/bcn-oracle/` and compiles a developer-run oracle binary there. Were it
  ever vendored, or linked into anything shipped, it would need a row in the tables above and a line
  in `NOTICE`; that is the reason it is not.
- **naga, dxc, tint** — not referenced anywhere in the tree.

Packages marked **build only** or **test only** in the managed tables are listed anyway rather than
omitted, because "is this distributed?" is a question a reader should be able to answer from this file
instead of by grepping csproj files, and because the answer changes when a project moves.

⚠ **The reasoning above does not extend to `Taffy`'s fixture corpus or the Unicode tables**, and that
distinction is the point of the *Third-party data* section. Those are not tools that ran; they are
**data committed into this repository**, and 5 524 fixtures redistributed under MIT carry the MIT
notice obligation exactly as a library would. A test-only *tool* is excluded; test-only *data* in the
tree is not.

## What `NOTICE` gets wrong

`NOTICE` has not been modified by the change that added this page — it is the repository owner's to
edit, and the corrections below are recorded rather than applied. All four are demonstrable:

1. **It promises a generator that does not exist.** It says "The full attribution manifest is
   generated at build time by the Nuke `Pack` target and published to docs/manual/third-party.md."
   The `Pack` target in [`build/Build.cs`](../../build/Build.cs) runs `DotNetPack` and nothing else;
   it generates no manifest, and this page did not exist until now. `docs/plan/01` § ADR-015 makes the
   same claim about `RestoreNativeDeps` + `Pack`. `RestoreNativeDeps` does produce
   `artifacts/native/THIRD-PARTY-NATIVE.md`, so half of it is true; the `Pack` half never was.
2. **Its SDL entry is factually wrong.** See *Native libraries that arrive through managed packages*.
3. **Its ANTLR entry names the wrong upstream** — `antlr/antlr4` rather than
   `tunnelvisionlabs/antlr4cs`. See *Unresolved*.
4. **Its Open Sans entry says Open Sans is "the one binary asset the tree carries."** The tree carries
   24 committed fonts, 116 PNGs, 66 `.spv`, 13 `.obj` and nine audio files. The text-rendering-tests
   fonts are covered by their own entry further down the same file, so this is a wording defect rather
   than a missing attribution — but it contradicts the entry below it.

`NOTICE` is also missing an entry for **OpenAL Soft**, which is the most consequential omission on
this page, and for **web-platform-tests** pending the decision above.

## Keeping this true

**This page is checked by a gate, not by a documented refresh step.** A hand-written attribution list
is wrong one package bump after it is written, and nothing about a bump prompts anyone to open it.
[`CheckAttribution`](../../build/Build.Attribution.cs) runs inside `CheckFormat`, beside the SPDX
header check and for the same reason — it reads three files and takes milliseconds — and fails on:

- a package pinned in `Directory.Packages.props` with no row here;
- a row here naming a package that is no longer pinned;
- a version that has drifted between the two;
- a duplicated row, or a section whose delimiting markers have gone missing.

The same three checks run over `build/native-dependencies.json`.

⚠ **What the gate does not do, stated plainly so nobody relies on it for more than it gives.** It
checks the **inventory** — that the set of things attributed is the set of things depended on, at the
versions depended on. It does **not** verify that the licence beside each row is correct. It cannot:
that is a claim about a third party's published metadata, and checking it needs a network fetch, which
would make the gate flaky and useless offline. **The licence column is verified by a person, once,
when the row is added** — which is why every row names its source, so the next reader can re-check the
claim instead of inheriting it.

So the workflow is: add or bump a dependency, and `CheckFormat` fails until this file has a row for it.
Filling that row in is the manual step, and it is the step that should be manual — reading a licence is
a judgement, and a gate that filled the column in automatically would be a gate that never failed and
a document nobody had read. That is the same reasoning `--update-api` and `--update-exemptions` carry
elsewhere in this build.

Two things this arrangement still cannot reach, and both are real:

- **The transitive graph is not pinned**, so the *Transitive dependencies* table is a snapshot rather
  than a checked inventory. Turning on `CentralPackageTransitivePinningEnabled` would make it
  checkable; `Directory.Packages.props` explains why it is off.
- **The in-tree data table is hand-maintained.** A new font or corpus is caught by the SPDX header
  gate only if it is a source file in one of five extensions, and a `.ttf` is not.

## See also

- [`NOTICE`](../../NOTICE) — what actually ships in every NuGet package and distribution
  (`Directory.Build.props` packs it at the package root, per Apache-2.0 §4(d))
- [`LICENSE`](../../LICENSE) — Apache-2.0, the engine's own terms
- [`Directory.Packages.props`](../../Directory.Packages.props) — the managed pins, with each choice's
  reasoning beside it
- [`build/native-dependencies.json`](../../build/native-dependencies.json) — the native pins and their
  checksums
- [`docs/plan/01-technology-decisions.md`](../plan/01-technology-decisions.md) § ADR-015 — the licence
  decision and the dependency audit this page discharges
- [Diagnostic codes](diagnostic-codes.md) and [log event ids](log-events.md) — the other two registers
  in this directory
