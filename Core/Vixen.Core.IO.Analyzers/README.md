<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Core.IO.Analyzers

[Vixen.Core.IO](../Vixen.Core.IO/README.md) gives the engine one idea about where files are. This is
what keeps the other six out.

Rule: [doc 10](../../docs/plan/10-platforms.md) § "Cross-platform discipline" — only virtual paths in
engine code, and `System.IO.Path` outside `Vixen.Platform.*`, the editor and the tools. The rule is as
old as the plan; until now it was enforced by review, which is a check that runs when somebody
remembers to run it.

## Diagnostics

| Code | Meaning |
|---|---|
| `VXIO0001` | Engine code named `System.IO.Path`. Warning by default, which `TreatWarningsAsErrors` makes an error. |

## What counts as naming it

Four forms, because three of them are how a ban gets around:

```csharp
Path.Combine(root, name);              // the type
System.IO.Path.Combine(root, name);    // qualified
using static System.IO.Path;           // imported, and every unqualified Combine after it
using Files = System.IO.Path;          // renamed, and every Files.Combine after it
```

Each is reported once, at the whole expression rather than at the `Path` in the middle of it. A
`<see cref="Path.GetExtension(string)" />` is not reported: documentation that says what a virtual
path does differently has to be able to name what it is different from. Generated code is not
reported either — it is not the author's to fix, and the generator that emitted it is not in scope.

What is *not* reported is a `Path` of our own. `entry.Path`, `mount.Path` and a local `Combine` are
everywhere in the engine; a rule that matched on the name rather than on the symbol would report all
of them and be switched off inside a week. `Vixen.Core.IO.Analyzers.Tests` holds that case, and the
four above, as tests.

## Where it runs

Referenced from `Directory.Build.props` by every project under `Core/` that is not a test, a
generator or this project — not per-project, because a rule that has to be opted into is a rule the
next library forgets. `Platform/**`, `Editor/**`, `Tools/**` and `Raven/**` do not get the analyzer
at all rather than getting it and switching it off: translating a virtual path into a host path is
what those layers are for.

## Where it is switched off, and why

Seven places inside `Core/` are host-filesystem layers, and each turns the rule off by name in
`.editorconfig` with a written reason, beside every other named exclusion in the repository. Scoped
to the file, never to the project, so the next file in the same library is still reported.

| File | Because |
|---|---|
| `Vixen.Core.IO/PhysicalFileProvider.cs` | The translation itself — the reason every other file can be held to the rule. |
| `Vixen.Core.IO/Watch/FileWatcher.cs` | `FileSystemWatcher` reports host paths; turning them back into virtual ones is the file's job. |
| `Vixen.Ui.HotReload/HotReloadWatcher.cs` | The same, for markup. |
| `Vixen.Ui.Testing/Visual/*.cs` | Golden images on the disk of whoever ran the tests. |
| `Vixen.Net.Fuzz/Corpus.cs` | A corpus directory of loose `.bin` files, handed in as an argument. |
| `Vixen.Shaders/EffectDiskCache.cs` | Named for what it is: compiled variants in a host directory. |
| `Vixen.Video/VideoContent.cs` | `FileVideoContentSource` streams a cutscene from beside the executable. |

One site was fixed rather than excused: `Vixen.Ui.Reactive/Effect.cs` used `Path.GetFileName` to trim
a `[CallerFilePath]` string for a log message. That path is one the *compiler* wrote down on the
machine that built the assembly, so asking the running platform how paths are separated was the wrong
question in the first place; it now looks for both separators and takes the last segment.

## Still to come

**The synchronous-IO half.** [Doc 03](../../docs/plan/03-core-foundation.md) bans the synchronous
open overloads — which exist for editor and tooling code — from runtime hot paths, in the same
sentence that bans `System.IO.Path`. It is not implemented here, and the reason is not effort:
`IOdbBackend.TryRead`, `BundleOdbBackend.Open` and `ContentUpdate.CachedVersion` call them today from
interfaces that are synchronous by contract. Enforcing the rule means either making those contracts
asynchronous or granting three exemptions that would hollow it out, and that is a design decision
about the object database rather than an analyzer.

Licensed under Apache-2.0.
