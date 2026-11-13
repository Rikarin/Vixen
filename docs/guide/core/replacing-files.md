---
title: Replacing files
slug: core/replacing-files
kind: guide
area: Core
summary: Write a temporary, move it over the old file, and wait out whoever is reading it rather than fail.
api: [T:Vixen.Core.PatientMove]
tags: [core, files, saving, windows]
since: 0.1
status: stable
related: [editor/texture-graph-compiling]
---

## What it is

`PatientMove.Over(from, to)` moves a finished file over another in one step, as
`File.Move(from, to, true)` does, and waits briefly for whoever is reading the file it replaces.

## Why a save needs it

Every document in the engine saves the same way: write the whole thing to a temporary beside it, then
move the temporary over the old file. A save interrupted halfway — a full disk, a crash — leaves the old
file whole, and a reader in another process sees the whole old file or the whole new one, never half.

⚠ **On Windows that move fails while anybody has the old file open**, with "Access to the path is
denied". A reader that shares delete does not change it. An asset scan hashing the file, a file
watcher's handler reading it back, a second editor or a build: any of them, at the wrong millisecond,
turned a save into an exception. Linux and macOS let the rename through and the reader keeps the old
bytes, so the fault only ever showed on Windows.

A read is short, so `Over` tries again, backing off from one millisecond to fifty, until
`PatientMove.Patience` (two seconds) has passed, and then throws the last failure as it was. A failure
waiting cannot cure — the temporary is gone, the folder is missing — is thrown at once.

## Using it

```csharp
var temporary = path + ".tmp";

File.WriteAllText(temporary, text);
PatientMove.Over(temporary, path);
```

A move that must not replace anything — `File.Move(from, to)` or with `false` — does not need it, since
there is no reader of a file that is not there.

⚠ **A new overwriting `File.Move` outside a test fails `PatientMoveTests`**, which sweeps every `.cs`
and `.vxml` in the repository. A test may still use the framework's move, since it may be standing in
for an outside program that does.
