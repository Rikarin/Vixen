# Vixen.AssetCompiler

Runs importers in worker processes, so that a file which crashes one fails *that asset* and the
import carries on.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § "Out-of-process,
parallel, crash-isolated".

```
vixen import --isolated
  Importing in 9 worker process(es).
  note    Assets/hero.obj: 1 mesh(es), 1 material(s), 2 node(s).
Imported 3, 0 unchanged, 0 failed in 273 ms.
```

## The promise, and what it is not

`ImportPipeline` already catches an importer that **throws** and fails that one asset. It cannot
catch an importer that takes the process down — a malformed FBX inside a C++ library, a stack
overflow inside a recursive scene graph, a native access violation. Doc 08 calls that the difference
between "one bad file" and "the editor won't open", and the only way to have it is a process
boundary.

So the test that matters kills a real worker mid-build and asserts that the asset it was importing
fails, the next asset gets a fresh worker, and `Restarts` says it happened. There is no fake worker
anywhere in the tests: a stub that "dies" by returning null would be testing the handling of a value
this code writes itself.

## Where the seam is

`IImportExecutor`, in `Vixen.Editor.Assets`. One asset's worth of work crosses the boundary and
nothing else: which importer claims a file, what the cache key is, whether anything needs to run at
all, what gets written back to the sidecar — all of it stays in `ImportPipeline` in one copy,
whichever executor is in force. There is a test that runs the same job through both executors and
compares the results, because "the process boundary is invisible" is the claim the seam is for.

A job carries its settings as **YAML text** rather than as a bound settings object. A bound object
cannot cross a process without a serializer for every importer's settings type; the text it was bound
from crosses trivially, is what the cache key already hashes, and makes the binding failure — and its
message — happen in the same place either way.

## The protocol

Length-prefixed messages, `[DataContract]` records, the engine's own binary serializer, exactly as
doc 08 specifies. Four bytes little-endian and then the payload, because **a pipe is a stream and not
a sequence of messages**: a reader that treated one `ReadAsync` as one message would work for every
small mesh and fail for the first big one, which is the worst possible distribution of that bug. A
length that is not plausible is refused rather than allocated — the number came from another process.

**One pipe per worker**, not one pipe with many instances. A shared pipe would need a correlation id
in every message and every worker told which replies are its own; a pipe per worker makes a request
and its response the only two things on that stream.

**Artefacts come back over the wire**; the worker writes nothing. N processes writing into one
content-addressed store is a correctness problem — partial files, torn reads, no single place that
knows what was written — in exchange for saving a memory copy. The coordinator stays the only writer.

The wire types are a flat mirror of the domain types rather than the domain types themselves, so the
format is a thing this project defines completely and a change to `ImportJob` is not silently a
protocol break.

## One importer list

`BuiltInImporters.Create()`, called by the CLI and by every worker. A worker whose registry differs
from its coordinator's produces different artefacts for the same file, and the disagreement surfaces
as a cache that never hits — or, worse, as a build whose output depends on how many cores the machine
has. Moving that list out of `Vixen.Cli` is what makes the two provably the same.

## The pool is given N jobs, not one

This section used to be the first entry under *Owed* and is not any more. `ImportPipeline` dispatches
`MaxConcurrency` imports at once, so a pool of N workers has N jobs in flight.

⚠ **The scheduler is not the batching one that was asked for here**, and the reason is worth keeping.
Batching by dependency would run a dependency before a dependant that sits *earlier* in path order —
which is more sensible-looking and produces different bytes, because a sequential loop shows that
dependant its dependency's *old* artefacts. What the scheduler does instead is reproduce the
sequential loop's view of the cache per asset: wait for a dependency only when it comes earlier in path
order, and read its pre-run record when it comes later. The answer is identical and only the timing
differs. `Editor/Vixen.Editor.Assets/README.md` has the argument, and `ImportParallelismTests` is the
gate — sixteen at once against one at a time, eight times, with a concurrency high-water mark as its
control.

⚠ **`--isolated` is still off by default, and for its own reason rather than for that one.** It costs a
process start and a copy of every artefact over a pipe, and the failure it protects against — an
importer that takes its process down — is rare enough to be worth asking for. That trade did not
change.

## Owed, and named

**A worker is not reused across projects.** Each pool starts its own, rooted at one project directory.
An editor holding one long-lived pool is what a Phase 4 editor wants and is not what a CLI needs.

**Nothing is sandboxed.** A worker runs the same importers with the same file access as the process
that started it; the boundary is there for crashes, not for trust.

Licensed under Apache-2.0.
