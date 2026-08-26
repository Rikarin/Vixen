# Vixen.Core.Threading

The job system. Frame work runs here rather than on the .NET thread pool, and it runs as a dependency
graph rather than as a sequence of barriers.

```csharp
using var jobs = new JobScheduler();

struct IntegrateJob(Span<Body> bodies, float dt) : IJobParallelFor {
    public void Execute(int index) => bodies[index].Integrate(dt);
}

var integrate = jobs.ScheduleParallel(new IntegrateJob(bodies, dt), bodies.Length);
var broadphase = jobs.Schedule(new BroadphaseJob(grid), integrate);
var contacts = jobs.Schedule(new NarrowphaseJob(pairs), broadphase);

// …other work here. The main thread joins in while it waits.
jobs.Complete(contacts);
```

## What is here

| | |
|---|---|
| `IJob` / `IJobParallelFor` | Work, as a struct. Generic all the way down: no boxing, no delegate, no closure. |
| `JobScheduler` | Persistent workers, work-stealing deques, the slot ring, and the graph. |
| `JobPriority` | Two tiers: frame work, and work that would rather be late than make a frame late. |
| `JobHandle` | Twelve bytes naming a scheduled job. Also an edge in the graph. |
| `MainThreadDispatcher` | For work that has to happen on one particular thread, drained at frame points. |

## The decisions, and what they cost

**Not the thread pool.** The pool's hill-climbing heuristic answers a busy period by injecting
threads, on a schedule measured in hundreds of milliseconds, against a frame budget measured in
single digits. It also has no way to express "this graph, by this deadline". So: a fixed set of
threads that live as long as the process, and a graph the scheduler resolves itself. The cost is that
a job which blocks — on IO, on a lock, on a network round trip — occupies a worker that cannot be
replaced. Blocking work belongs on the thread pool; this is for work that is bounded by the CPU.

**A struct job, not a delegate.** `Schedule<TJob>` is generic over the concrete job type, so the job
struct is copied into a preallocated array of exactly that type and executed through generic code
that knows what it is holding. Nothing on the path from `Schedule` to the work running allocates,
which is asserted by a test rather than hoped for. The cost is ceremony: a job is a type, not a
lambda, and its inputs are its fields.

**The main thread participates.** `Complete` does not park; it executes ready work — including work
unrelated to what it is waiting for — until the handle it wants is finished. A frame that schedules
early and completes late gets the whole machine. The corollary is that `Complete` can run arbitrary
other jobs on your thread, so a job holding a lock and then completing something is a deadlock the
scheduler cannot see.

**A lock per slot, not a lock-free continuation list.** Adding an edge to a job that is finishing at
that exact moment is the whole difficulty of a job graph. The lock-free version needs a CAS loop, an
ABA guard, and a separately allocated link node per edge. This takes an uncontended lock instead —
the scheduling thread and one completing worker — and in exchange the correctness of the graph can be
read off the code rather than argued for. A frame's few hundred edges are not where the time goes.

**A thousand slots, recycled on completion.** `MaxJobsInFlight` bounds the ring, and every job type
that gets scheduled allocates a payload array of that length, so the bound is a memory decision as
much as a concurrency one. Scheduling past it does not fail: the thread asking for a slot pays for
one by finishing a job that is already scheduled.

**Zero workers is a supported count, not a degenerate one.** `new JobScheduler(0)` keeps the graph,
the slot ring, the failure log and the batching, and drops the only thing a browser tab cannot have:
threads. Work then runs when somebody reaches `Complete` — which already executes ready work rather
than parking, so there is no second code path and nothing that only the web exercises. `Dispose`
drains for the same reason it always did.

```csharp
// A browser tab that is not cross-origin isolated. new JobScheduler() picks this itself
// on browser-wasm, because Thread.Start() there throws rather than being slow.
using var jobs = new JobScheduler(workerCount: 0);
```

Two things do change, and both are stated rather than smoothed over. Scheduled-and-never-completed
work never runs, where with workers it happens anyway — code that relies on that has a bug on the
web. And `ScheduleParallel` with an automatic batch size emits one batch instead of four per
participant, because four batches for one thread is three extra work items and nobody to steal them.

**Two tiers, and the second one is deferred rather than interrupted.** `JobPriority.Frame` is the
default and everything a frame is made of; `JobPriority.Background` is work that has to finish
eventually and would rather be late than make a frame late. Each worker gets a second, smaller deque
and there is a second shared queue, and every frame source is drained before any background one.

Three rules make that safe rather than merely fast, and each is a test:

- **Deferral, not preemption.** A job is a struct's `Execute` on a worker thread and there is no
  portable way to suspend one mid-call, so a background job that has started runs to completion. The
  tier decides what a thread picks up *next* and nothing else — which means splitting long work into
  batches is what makes it effective, and a single hundred-millisecond background job still costs a
  frame a hundred milliseconds of one thread.
- **A fairness share, or strict priority starves the tier it defers.** After 64 frame items a thread
  takes a background one if there is one, so a program that never stops scheduling frame work still
  drains the background tier at a sixty-fourth of the rate rather than never. The debt is held at 64
  rather than counted past it, so a long frame-only stretch cannot bank credit.
- **One worker is held out of the background tier.** Without it, `WorkerCount` long background jobs
  occupy the whole pool and a frame is left with only the thread that is waiting for it. The
  reservation applies to a worker *volunteering* for work and never to a thread inside `Complete`,
  `Dispose` or a slot rental — those must be able to reach any ready item, or completing a background
  handle could hang. It costs a batch-only workload one worker's throughput, which is the price of
  the guarantee and is only paid by callers that ask for the tier.

**No priority is a correctness device.** A dependency edge is what says "not yet"; a tier only says
"not first". A frame job that depends on a background job waits for it exactly as before.

**Failures survive the slot.** A slot is reused the moment its job finishes, which means it can no
longer answer "did that throw" — and the answer must not depend on how quickly the caller asked. So
the last `JobFailureLog.Capacity` failures move to a side table on the way out, and both
`Complete` and a dependency edge added after the fact read from there. A job whose dependency threw
is marked failed and skipped rather than run against inputs that were never produced.

## What is not here yet

**The safety system.** [Doc 03](../../docs/plan/03-core-foundation.md) describes `VIXEN_JOB_SAFETY`
as jobs declaring read/write access to `NativeArray` regions, with the scheduler asserting that no
two concurrent jobs write the same one. That check is only as good as the declarations, and the
declarations have to come from somewhere — in Unity's design, from the ECS's component access. Vixen
has no ECS yet, so building the declaration API now would be inventing the shape of something whose
only consumer does not exist. It lands with `Vixen.Ecs` in Phase 2.

What *is* compiled in under `DEBUG` or `VIXEN_JOB_SAFETY` is the check that does not need any of
that: a job that completes its own handle is caught and told so, instead of waiting forever for the
work item that is doing the waiting.

**Thread affinity.** Doc 03 asks for workers pinned where the OS allows. `Thread` has no portable
affinity API, the per-platform ones differ in kind rather than in spelling, and pinning is a
pessimisation on a machine that is running anything else. It waits for `Vixen.Platform`, which is
where the per-OS calls will already live.

**A consumer for the background tier.** The tier itself exists and is tested; nothing in this tree
sets it yet, and the reason is one structural fact rather than a to-do: **no process in this tree has
both frame work on a `JobScheduler` and long deferrable CPU work on the same scheduler.** A tier is a
choice between two things a thread could pick up next, and nowhere in the engine are there two such
things to choose between.

*Where the scheduler is.* One production path reaches one consumer. `AppBuilder` makes the scheduler
and hands it to `EngineLoop` → `SystemRunner` → `SystemContext.Jobs`, and the only thing that reads
it is `AnimationSystem.Evaluate` — a `ParallelFor` the frame is blocked on, which is `Frame` work by
construction. There are ten scheduling call sites outside this module; the other nine
(`VfxSimulation`, `NavPathQueue`, `GoapPlanQueue`, `VisibilityGroup`, and the UV and remesh solvers)
sit behind a `JobScheduler?` seam that **no production code assigns** — `Scheduler =` appears only
under `Benchmarks/` and `*.Tests/`. Nine of the ten are unreachable, and the tenth is a frame's.

*Where the long CPU work is.* In another process. The BC7 encode, the meshlet LOD build, the
distance-field bake and the remesh/unwrap solvers are all genuinely CPU-bound, long and splittable —
and every production call site is inside the import pipeline, reached from `vixen content build` or
the editor's content tasks. Neither `Editor/` nor `Tools/` contains a single code reference to
`JobScheduler`; `Vixen.Editor.Assets` does not even reference this project.

⚠ *The import is not the owed consumer, and asking it to be one would be a regression.* Its worker
loop is `Task.Run` over an `async` body: importers are `async ValueTask ImportAsync`, `IJob.Execute`
is synchronous `void`, and the loop `await`s both file I/O and a `TaskCompletionSource` barrier that
holds each asset until its path-order dependencies have re-imported. Blocking a fixed worker on
another job's completion signal is the one thing this pool cannot survive. And even setting that
aside, an offline content build is a workload that is *entirely* background: there is no frame tier
in that process for the work to yield to, so the tier would buy nothing and its reserved worker would
cost a real one — the case the guide names when it says a batch-only workload gives up one worker's
throughput.

⚠ *Nor can any of the ten be relabelled where they stand.* All ten are `ParallelFor` or
`ScheduleParallel(…).Complete()`, so the calling thread is blocked on the batches it just scheduled.
`Background` there is not a no-op, it is a pessimisation: the waiting thread drains every unrelated
frame item it can reach before running the one it is waiting for.
`WaitingOnBackgroundWorkRunsUnrelatedFrameWorkFirst` asserts exactly that, as a take order rather
than as a duration. A consumer has to be one that *keeps* its handle and asks `IsCompleted` on a
later frame; nothing in the tree does.

*What the first consumer will probably be.* `GlobalDistanceField`'s clipmap composite — CPU-bound,
one batch per slice per level, production-reached through `CompositorBuilder` and sample 13, and
already designed around being mostly-stale, since a scroll reuses about 97% of its cells. It is
frame-synchronous today: `GlobalDistanceFieldRenderer.Record` composites and uploads in the same
call. Making it the first background consumer means three things this module cannot do for itself —
plumbing `AppServices.Jobs` through to the compositor's nodes, double-buffering the distances the
upload reads, and a frame that accepts a clipmap one refresh out of date. That is a rendering change,
and it is where the row goes amber-to-green.

Licensed under Apache-2.0.
