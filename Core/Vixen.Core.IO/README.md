# Vixen.Core.IO

Six platforms have six ideas about where files are. This is the one idea the engine has.

```csharp
var vfs = new VirtualFileSystem();
vfs.Mount(MountPoints.App, new PhysicalFileProvider(contentDirectory, isReadOnly: true));
vfs.Mount(MountPoints.Data, new PhysicalFileProvider(saveDirectory));

var settings = await vfs.ReadAllTextAsync(new("/data/settings.json"));
```

Engine code says `/app/textures/x.ktx2` and never learns that on Android that is inside an APK, on
iOS inside a signed bundle, in the browser an HTTP fetch with an IndexedDB cache, and in the editor a
directory.

## What is here

| | |
|---|---|
| `VirtualPath` | Absolute, normalised, case-sensitive, segment-aware. The vocabulary. |
| `MountPoints` | `/app`, `/data`, `/cache`, `/temp`, `/project`, `/db`, and what each promises. |
| `VirtualFileSystem` | The mount table. Longest prefix wins; reads are lock-free. |
| `PhysicalFileProvider` | A directory on disk, with memory-mapped reads. |
| `MemoryFileProvider` | A dictionary. The default for tests, and the reference implementation. |
| `Watch.FileChangeCoalescer` | Raw filesystem events → what actually happened. |
| `Watch.FileWatcher` | `FileSystemWatcher` feeding the coalescer, drained at a frame point. |

Beside it, [`Vixen.Core.IO.Analyzers`](../Vixen.Core.IO.Analyzers/README.md) is the rule about all of
this made checkable: engine code that names `System.IO.Path` fails the build.

## The decisions, and what they cost

**Case-sensitive everywhere, including where the filesystem is not.** `Texture.PNG` and `texture.png`
are two paths, and `PhysicalFileProvider` refuses to serve one under the other's name. The check
costs a cached directory probe per file opened, and it is enabled only where it is needed: the
constructor probes the volume once, so on a case-sensitive filesystem it is off and the kernel is
already doing the work. What it buys is not discovering, eighteen months into a project developed on
a Mac, that the Linux build cannot find a third of its textures.

**Normalisation is the escape guard.** `..` never reaches a provider: `VirtualPath` resolves it at
construction and rejects anything that resolves above the root. Putting the guard in the type rather
than in each provider means the next provider cannot forget it.

**Enumeration is ordered, and that is part of the contract.** It costs materialising a directory
listing instead of streaming it. It buys a content build that hashes a listing and gets the same
answer on ext4 as on APFS.

**Async-first, sync as a documented compromise.** Opening a stream is genuinely remote on some
platforms, so that is asynchronous and the runtime uses that form. The synchronous overloads exist
because editor and tooling code is straight-line file work that would be worse as a state machine.
Enumeration is synchronous only — every provider that exists or is planned answers it from something
local, and an asynchronous form would be a state machine with no caller.

**`MemoryFileProvider` is the reference implementation, and the conformance suite enforces it.** Both
providers run the same tests. Whether writing into a directory that does not exist creates it,
whether deleting a non-empty directory throws, whether case is part of a name — those are differences
that otherwise get discovered by a caller written against one provider and shipped against another.
A third subject runs the same suite against a provider that implements only the members the interface
*requires*, so every default implementation is held to the same contract as an overriding one.

**Appending is a first-class operation, because resuming a download is.** `OpenAppend` adds to what
is there rather than replacing it — a bundle fetch that stopped at 300 MB has to carry on at 300 MB,
and a provider that quietly truncated would turn every dropped connection into starting again. The
interface default reads the file back and rewrites it, which is correct everywhere and wasteful; both
real providers override it with something that genuinely appends.

**Mapping is allowed to decline.** `TryMap` returning `false` is an ordinary answer: a file inside a
compressed APK entry cannot be mapped, and neither can one above two gigabytes, since the result is
a `ReadOnlyMemory<byte>`. Callers fall back to a stream. What it buys where it works is the
serializer reading a bundle without copying it, and never faulting in the pages it does not touch.

## On the file watcher

**Three platform backends were not written.** [Doc 03](../../docs/plan/03-core-foundation.md) asks
for FSEvents, inotify and `ReadDirectoryChangesW`. `FileSystemWatcher` is already exactly that, behind
one type, maintained by people who have to keep it working on OS versions that do not exist yet.

What the BCL does not do is any of the part that makes watching usable, and that is
`FileChangeCoalescer`. Saving one file in a text editor produces, depending on the editor, one write,
or a truncate and three writes, or a new temporary file renamed over the original. A hot-reload
pipeline reacting to each of those separately compiles the same shader four times, twice from a
half-written file. So:

- **Debounced** — nothing is reported until the path has been quiet, and each write extends the
  window rather than running it out, so a large file being written in chunks is reported once, when
  it is finished.
- **Atomic saves folded** — a rename whose source was created inside the same window is the
  write-elsewhere-then-rename pattern, and it comes out as one change to the destination.
- **Cancelling pairs resolved** — created-then-deleted is nothing; deleted-then-created is a change,
  not a deletion, so a consumer does not drop its cache for a file that is sitting right there.
- **Our own writes suppressed** — without it, the asset pipeline writing an artefact wakes the
  watcher, which reimports, which writes an artefact.

Time is a parameter rather than a clock, so all of that is tested at exact timestamps instead of with
sleeps. Two tests touch a real filesystem, and they are the two that cannot be written any other way.

**Changes are pulled, not pushed.** `Drain` is called from wherever the consumer wants the effects to
land — a frame boundary, between the simulation and the render — rather than the watcher raising
events on a platform thread at a moment nobody chose.

**Overflow is reported, not hidden.** If the platform's buffer overflows, events were lost before
this process saw them, and the only correct response is a rescan. `HasOverflowed` says so.

## Still to come

**The other providers.** Android `AAssetManager`, the iOS bundle, browser `fetch` + IndexedDB, and
the bundle-backed read-only provider. The first three need `Vixen.Platform` and the fourth needs the
object database, so each arrives with the thing it reads from. The interface is shaped for them —
that is why `TryMap` is allowed to decline and why paths reaching a provider are mount-relative.

**The synchronous half of the analyzer.** [Doc 10](../../docs/plan/10-platforms.md)'s ban on
`System.IO.Path` in engine code is now enforced by
[`Vixen.Core.IO.Analyzers`](../Vixen.Core.IO.Analyzers/README.md) — `VXIO0001`, an error in every
`Core/` project, off by name in the seven places whose job is the host filesystem. The other half of
that sentence, [doc 03](../../docs/plan/03-core-foundation.md)'s ban on the synchronous open
overloads in runtime hot paths, is not enforced: `IOdbBackend.TryRead` and `ContentUpdate` call them
today from interfaces that are synchronous by contract, so the rule is a decision about those
contracts before it is an analyzer.

Licensed under Apache-2.0.
