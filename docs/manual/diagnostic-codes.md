# Diagnostic codes

Every diagnostic the **tools** emit in the MSBuild format carries a stable `VXnnnn` code. This file is
the register for those.

**Compile-time diagnostics are a separate space and are registered with their producer**, because a
Roslyn analyzer's codes belong to its `AnalyzerReleases.{Shipped,Unshipped}.md` pair — that is where the
analyzer-release tracking rules require them, and a second copy here would be a second thing to keep in
step. Where to look:

| Prefix | Producer |
|---|---|
| `RVN` | Raven — the compiler's own diagnostics ([07](../plan/07-raven-shader-pipeline.md)) |
| `VXS` | `Vixen.Core.Serialization.Generator`, `Vixen.Core.Reflection.Generator`, `Vixen.Ui.Generators`, `Vixen.Engine.Generators` (`04xx`) |
| `VXML` | `Vixen.Ui.Markup.Generators` — `1xxx` means the tree is a guess made during recovery, `2xxx` that the tree is right and its meaning is wrong |
| `VXNET` | `Vixen.Net.Generators` — replicators and RPC senders |
| `VXSH` | `Vixen.Shaders.Generators` |
| `VXIN` | `Vixen.Input.Generators` |
| `VXN` | `Vixen.Editor.NodeGraph.Generator` |
| `VXI` | `Vixen.Editor.Inspector.Generator` |
| `VXIO` | `Vixen.Core.IO.Analyzers` — the `System.IO.Path` ban |

**Why this exists.** MSBuild recognises `file: error CODE: text` and nothing else — without a code a
line is prose in a build log rather than an entry in the IDE's error list. And once shipped, a code is
what somebody searches for two years later, long after the sentence beside it has been reworded. This
is the same argument the [log event register](log-events.md) makes, for the same reason.

## Rules

- **A code is permanent.** Once shipped it never changes meaning and is never reused.
- **The message text may change freely.** The code is the contract; the wording is not.
- **The severity may change.** A warning that turns out to be noise can become information without a
  new code.
- **Add the entry in the same commit as the diagnostic.** A register updated later is a register that
  is wrong.
- **Information-level output carries no code**, deliberately. It is not an error-list entry, and
  dressing it as one puts "this project has no addressable assets" in a CI failure summary.

## Ranges

| Range | Subsystem | Status |
|---|---|---|
| VX1000 – VX1999 | Asset import — importers, the pipeline, the asset database | **in use** |
| VX2000 – VX2999 | Content build — the plan, the packer, the catalog | **in use** |
| VX3000 – VX3999 | Shaders and Raven integration (Raven's own are `RVNxxxx`) | reserved |
| VX4000 – VX4999 | UI markup and styling — VXML, VCSS | **in use** |
| VX8000 – VX8999 | Platform packaging — APK, iOS bundle, `wwwroot` | **in use** |
| VX9000 – VX9999 | The tools themselves — invocation, environment | **in use** |

## Allocated

| Code | Meaning | Emitted by |
|---|---|---|
| `VX1001` | An importer said something about an asset. | `vixen import`, `vixen content build` |
| `VX1002` | The asset database found something while scanning. | `vixen import` |
| `VX2001` | The build plan found something: an address nothing can resolve, a group nothing defines, a dependency that would not be packed. | `vixen content build` |
| `VX2002` | The content builder found something while packing. | `vixen content build` |
| `VX2003` | The shader bundle build found something: a manifest that will not read, a variant that will not compile, a variant no shader answers to. | `vixen content build` |
| `VX4001` | A `.vxml` is on disk in a project that globs no markup, so nothing compiles it. | `Directory.Build.targets`, in this repository only |
| `VX4002` | A `.vxml` is compiler input and the VXML compiler is not in `@(Analyzer)`. | `Vixen.Ui.targets`, everywhere it is imported |
| `VX8001` | A pinned native binary has not been restored. | `MoltenVK.targets`, on an iOS build |
| `VX8002` | A restored native archive exports none of the entry points it was linked for. | `MoltenVK.targets`, on an iOS build |
| `VX9001` | The tool could not run: no project where one was expected, or an unusable argument. | every command |

Severity is not part of the code. `VX1001` is an error, a warning or information depending on what the
importer said, because the importer is what knows.

⚠ **`VX4001` and `VX4002` are `VX` and not `VXML`, and the split is the point rather than an
inconsistency.** Everything in the `VXML` space is a claim about a `.vxml`'s contents, made by a
Roslyn generator that has read it. These two are claims that the generator *never ran* — one because
the file is not compiler input, one because it is and no analyzer is loaded — so there is no
generator to emit them and nothing to register in `AnalyzerReleases`. They are MSBuild's, they land
on the `.csproj` and not on the markup, and that is where the mistake is: every message this state
used to produce named the hand-written half of a partial class that was correct. Both are errors, on
the argument that the alternative is a warning printed ahead of thirty C# errors about the wrong
file, or — for a `.vxml` with no hand-written half — a build that succeeds with the component
missing. `<VixenUiMarkupCheck>false</VixenUiMarkupCheck>` is the escape, and each message names it;
`Tools/Vixen.Templates` is the one project in the tree that takes it.

⚠ **`advisory` is a fourth word, and it is a claim about the run rather than about the finding.**
`vixen import --advisory` — which only `Vixen.Sdk`'s pre-compile pass passes, because it runs before
the game assembly exists and therefore cannot resolve a level naming the game's own types — writes
`<path>: advisory VX1001: <message>`. MSBuild reads only `error` and `warning`, so the line stays
prose and no error list gains an entry it would be wrong to act on. The code and the path are
unchanged, so the same search still finds it.
