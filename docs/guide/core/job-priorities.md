---
title: Job priorities
slug: core/job-priorities
kind: guide
area: Core
summary: The job scheduler's two tiers — frame work, and work that would rather be late than make a frame late.
api: [T:Vixen.Core.Threading.JobPriority]
tags: [threading, jobs, scheduler, concurrency, frame]
since: 0.1
status: preview
---

## What it is

`JobPriority` is the tier a job is scheduled into, and there are two of them. `Frame` is the default
and is everything a frame is made of. `Background` is work that has to finish eventually and would
rather be late than make a frame late.

Every source of frame work is drained before any source of background work — a thread looks at its
own deque, the shared queue and then its neighbours' deques for frame items, and only when all of
those are empty does it look at the background ones. Two rules keep that from being either useless or
unfair, and both are described below.

## What it is for

The job scheduler runs a fixed set of worker threads and expects every job to be short, because a
frame is short. One long job breaks that expectation in two different ways, and the tier answers
both: it stops the long job being picked up *ahead of* frame work that arrived later, and it stops a
burst of long jobs occupying every worker at once.

A bake, a UV unwrap, a texture decode, a navigation mesh rebuild — anything a user started that the
frame is not waiting for — is what the tier is for.

**The engine's own one is `GlobalDistanceFieldRenderer`.** Its clipmap composite is every cell of
every level against every instance — the most expensive thing in the frame — and about 97 per cent
of it is stale by design, because the levels are snapped so that a camera crossing one cell keeps
almost all of what it had. Given a scheduler it schedules that one slice at a time into
`Background`, keeps the handle, and lets the frame draw the previous refresh.

⚠ **Vixen's own asset import is not one of them, despite looking like the obvious first example.**
Its workers are `Task.Run` over an `async` body that awaits file reads and a dependency barrier, and
it runs in a content build where there is no frame tier to yield to. See the
[module README](https://github.com/Rikarin/Vixen/blob/master/Core/Vixen.Core.Threading/README.md) for
the rest of that reasoning.

⚠ **It is not for expressing "not yet".** A dependency edge is what says a job must not start until
something else has finished, and a tier does not weaken or strengthen an edge. A `Frame` job that
depends on a `Background` job waits for it exactly as it would wait for anything else.

⚠ **Nor is it for blocking work.** A job that waits on a file, a lock or a socket occupies a worker
that cannot be replaced, whatever tier it is in. That work belongs on the thread pool, which is
where the engine's own long operations — thumbnail decoding, streamed texture loads, the UI's
`BackgroundTaskManager` — put it.

## Using it

The tier is an argument to the scheduling call. Naming it is usually clearer than counting
positions, and it also sidesteps the two dependency overloads:

```csharp compile
using Vixen.Core.Threading;

public static class Bakes {
    public static JobHandle Start(JobScheduler jobs, int probes) {
        var bake = new BakeJob();

        // One index per probe, so the tier has somewhere to defer to. See below.
        return jobs.ScheduleParallel(bake, probes, 0, priority: JobPriority.Background);
    }

    struct BakeJob : IJobParallelFor {
        public void Execute(int index) { }
    }
}
```

⚠ **Split long work into batches, or the tier buys nothing.** The scheduler defers background work;
it does not interrupt it. A job is a struct's `Execute` running on a worker thread and there is no
safe point to suspend it at, so a background job that has *started* runs to completion — one job that
takes a hundred milliseconds costs a frame a hundred milliseconds of one thread regardless of its
tier. `ScheduleParallel` with many indices is how a long operation becomes many short deferrable
pieces.

## Examples

**The fairness share.** Strict priority would starve the tier it defers: a program that always has
frame work queued would never run a background job at all. So after 64 frame items a thread takes a
background one if there is one, which means background work drains at a sixty-fourth of the rate
rather than at none of it. The debt is held at 64 rather than counted past it, so a long stretch of
frame-only work cannot bank credit and then pull in a run of background jobs the moment one appears.

**The reserved worker.** One worker is always held out of the background tier, so `WorkerCount` long
background jobs cannot occupy the whole pool and leave a frame with only the thread that is waiting
for it. The reservation is about which work a worker *volunteers* for — a thread inside `Complete`,
inside `Dispose`, or making room in the slot ring is never held back, because it has to be able to
reach any ready item or completing a background handle could wait forever.

The cost is stated rather than hidden: a workload that is *entirely* background, such as an offline
content build, gives up one worker's throughput. That is only paid by callers that ask for the tier.

```csharp no-compile="a fragment; `jobs` and the mesh come from the caller"
// Frame work first, whatever order things were scheduled in.
var unwrap = jobs.ScheduleParallel(new UnwrapJob(charts), charts.Count, 0, priority: JobPriority.Background);
var cull = jobs.ScheduleParallel(new CullJob(views), words);

jobs.Complete(cull);      // Runs the culling, and not the unwrap batches queued before it.
jobs.Complete(unwrap);    // The waiting thread runs those itself rather than parking.
```

⚠ **Completing a background handle still blocks the caller, and blocks it for longer.** The tier
changes which work the *workers* prefer; it does not make the call asynchronous. Worse, the waiting
thread is a taker like any other, so it drains every unrelated frame item it can reach before it runs
the job it is actually waiting for — putting work somebody is blocked on into this tier is not a
no-op, it is a pessimisation. A caller that must not block should not call `Complete` at all: it
should keep the handle and ask `IsCompleted` on a later frame. That — keeping the handle rather than
completing it — is what makes something a consumer of this tier, and it is why a `ParallelFor` cannot
become one by changing its last argument.

**A consumer, in full.** What `GlobalDistanceFieldRenderer` does is the whole pattern in one place,
and every line of it is load-bearing:

```csharp no-compile="a fragment; the node's own fields are elided"
// Nothing to draw instead, so the frame is blocked on this one — and Background on work the
// caller is blocked on is a pessimisation. The tier follows whether the caller waits.
if (!CanDefer) {
    jobs.ParallelFor(new CompositeSliceJob(started), started.SliceCount, batchSize: 1);
    started.Publish();
}

// Otherwise: one slice per item, the handle kept, and no wait anywhere in the frame path.
refresh = started;
refreshHandle = jobs.ScheduleParallel(
    new CompositeSliceJob(started), started.SliceCount, batchSize: 1, priority: JobPriority.Background
);

// …and on a later frame, asked rather than waited on.
if (jobs.IsCompleted(refreshHandle)) {
    jobs.Complete(refreshHandle);   // rethrows a slice that threw; a no-op on a finished handle
    refresh.Publish();
}
```

⚠ **A deferral that ends at the next frame is worth nothing, and looks exactly like one that
does not.** Polling with `Complete` instead of `IsCompleted` defers the frame that *starts* the
work and blocks on the one after it. Both of that node's tests were green against exactly that
until each was made to run a further frame — so a test for this pattern has to outlive the frame
that schedules.

## See also

- [`Vixen.Core.Threading` README](https://github.com/Rikarin/Vixen/blob/master/Core/Vixen.Core.Threading/README.md) —
  the deques, the slot ring and the dependency graph the tiers sit on top of.
