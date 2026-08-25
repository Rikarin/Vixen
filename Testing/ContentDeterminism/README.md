<!-- SPDX-FileCopyrightText: Copyright (c) Rikarin -->
<!-- SPDX-License-Identifier: Apache-2.0 -->

# The content-determinism fixture

One small project, built by [`nuke ContentBytes`](../../build/Build.ContentBytes.cs) on each of the
three `test` legs in [`ci.yml`](../../.github/workflows/ci.yml), so that the `content-bytes` job can
compare what three operating systems made of the same input.

## Why it exists

`Tools/Vixen.Cli.Tests` already asserts that two builds of one project are byte-identical, and that
two projects at different paths in opposite creation order agree. Both are **self-relative**: they
compare a machine against itself, so all three legs would pass while producing three different
catalogs. The wire format does not have this problem because it has a committed oracle —
`Core/Vixen.Net.Tests/Wire`. Content has none, and a catalog is the wrong shape for one: it is large,
it changes legitimately whenever an importer does, and a golden would be regenerated rather than
read. So the oracle is **the other runners**, and this is what they build.

## What is in it, and why each choice was made

| Choice | Why |
|---|---|
| **Three `.txt` assets under one group** | `RawImporter` is the fallback, so no importer-specific native library is involved. |
| **GUIDs committed in the sidecars** | A GUID minted per clone would make two runners disagree for a reason that is not a defect. They are `sha256(name)[0..16]`, so they are reproducible and reviewable. |
| **A sidecar for `Assets/Ui` too** | The scan mints one for a folder that has none, with a fresh GUID. It does not reach the catalog, but leaving a per-run random in the input of a determinism gate is not worth the argument later. |
| **Each asset well over 256 bytes** | `ChunkFormat.MinimumCompressedSize` stores anything smaller uncompressed. The existing determinism tests use the strings `"the hero"` and `"the villain"`, so **the LZ4 encoder has never been inside a byte comparison in this repository**. Here it is: the fixture's ~10 KB packs to a 1 075-byte bundle. |
| **No models, no shaders** | ⚠ The three legs install three *different* Assimp builds — `libassimp5` from apt, Homebrew's, and whatever the Silk.NET loader resolves on Windows. Mesh bytes differing between two versions of a native library is worth knowing and is **not a determinism defect in Vixen**; putting one here would make the gate red on its first run for a reason nobody could fix. |
| **LF, pinned in `.gitattributes`** | The bytes of these files are the payload that gets hashed. A Windows checkout that rewrote them would report a determinism failure that is really a checkout. |

## ⚠ The target is pinned and the gate does not work without it

`build/Build.ContentBytes.cs` passes `--target Windows` on every runner. A content build is a
function of its target — the same texture is BC7 on a desktop and ASTC on a phone — and the target
string is written into the catalog. The target nobody names is `ProjectWorkspace.HostTarget`, which
is *the operating system doing the building*.

Measured before the job was written, on one machine:

```
                       two builds for Windows          the same fixture for Linux
UiCore_….bundle        identical                       identical
catalog.bin            identical                       differs
catalog.bin.hash       identical                       differs
scenes.bin             identical                       identical
```

`"Linux"`, `"Windows"` and `"MacOS"` are two different lengths in the catalog's ordinal string table,
so every offset after them and the trailing CRC move. Unpinning the target would make the comparison
job red on its first run, and it would not be reporting a defect.
`VixenCommandTests.TheSameContentBuiltForTwoTargetsIsNotTheSameBytes` is what keeps that from quietly
stopping being true.

## Changing the fixture

Editing anything here changes the manifest, which is the point — it is what makes the instrument
demonstrably live. Nothing is committed *about* the expected bytes, so there is no baseline to
update: the three legs are compared to each other, not to a recorded number.
