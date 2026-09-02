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

**The tier's first consumer, and why it took until task #388 to have one.** It is `GlobalDistanceField`'s
clipmap composite, through `GlobalDistanceFieldRenderer`. It was the one candidate in the tree, and
what disqualified every other one is worth keeping written down, because the shape of the
disqualification is the shape of the rule.

*Why it took this long.* A tier is a choice between two things a thread could pick up next, and
until this landed **no process in this tree had both frame work on a `JobScheduler` and long
deferrable CPU work on the same scheduler**. One production path reached one consumer — `AppBuilder`
makes the scheduler and hands it to `EngineLoop` → `SystemRunner` → `SystemContext.Jobs`, and the
only thing that read it was `AnimationSystem.Evaluate`, a `ParallelFor` the frame is blocked on,
which is `Frame` work by construction. Of the ten scheduling call sites outside this module the
other nine sit behind a `JobScheduler?` seam that **no production code assigns** — `Scheduler =`
still appears only under `Benchmarks/` and `*.Tests/`. And the long CPU work — BC7 encode, meshlet
LOD build, distance-field bake, remesh and unwrap — is all in the import pipeline, in a process with
no scheduler at all.

*What the consumer is.* A composite is every cell of every level against every instance, it is the
most expensive thing in the frame by a wide margin, and the levels are snapped to their own grids
precisely so that a camera crossing one cell keeps about 97 per cent of what it already had. A frame
drawing last refresh's clipmap is therefore drawing something very nearly right. `Update` is split
into `BeginUpdate` → one Z slice of one level per index → `Publish`; the spare buffer each level
already had for scrolling is also the buffer a refresh writes while the frame uploads and samples
the other one, so nothing a reader can see — the cells, the box, the view position — moves until
`Publish`. `CompositorBuilder.Jobs` is how the application's scheduler reaches the node, from
`AppBuilder` through `AppGraphics`.

⚠ *The first composite is deliberately not in this tier, and that is the rule rather than an
exception to it.* Before there is a clipmap there is nothing to draw instead — `Apply` names no
volume, the pass's set is filled one binding short, and every draw in it is refused — so the frame
genuinely is blocked on that one. It is scheduled `Frame` and completed at once. **The tier follows
whether the caller waits.**

⚠ *And the poll is `IsCompleted`, never `Complete`.* A node that completed its handle in the frame
path would defer the frame that *starts* the composite and block on the very next one: deferral by
exactly one frame, which is worth almost nothing and looks exactly like the fix. Both the counter
test and the picture test were green against that defect on their first attempt, because both
stopped after the frame that starts the refresh. A deferral test that photographs one frame is
photographing the wrong one.

⚠ *One slice per work item, not one job.* The tier defers work and cannot interrupt it, so a refresh
handed over whole would hold a worker for the entire composite and the frame behind it would wait
exactly as long as if it had never been deferred.

⚠ *The import is still not a consumer, and asking it to be one would still be a regression.* Its
worker loop is `Task.Run` over an `async` body: importers are `async ValueTask ImportAsync`,
`IJob.Execute` is synchronous `void`, and the loop `await`s both file I/O and a
`TaskCompletionSource` barrier that holds each asset until its path-order dependencies have
re-imported. Blocking a fixed worker on another job's completion signal is the one thing this pool
cannot survive. And an offline content build is a workload that is *entirely* background: there is
no frame tier in that process to yield to, so the tier would buy nothing and its reserved worker
would cost a real one.

⚠ *Nor can the other ten be relabelled where they stand.* All ten are `ParallelFor` or
`ScheduleParallel(…).Complete()`, so the calling thread is blocked on the batches it just scheduled.
`Background` there is not a no-op, it is a pessimisation: the waiting thread drains every unrelated
frame item it can reach before running the one it is waiting for.
`WaitingOnBackgroundWorkRunsUnrelatedFrameWorkFirst` asserts exactly that, as a take order rather
than as a duration. A consumer has to be one that *keeps* its handle and asks `IsCompleted` on a
later frame.

**No priority is a correctness device.** A dependency edge is what says "not yet"; a tier only says
"not first". A frame job that depends on a background job waits for it exactly as before.

**Failures survive the slot.** A slot is reused the moment its job finishes, which means it can no
longer answer "did that throw" — and the answer must not depend on how quickly the caller asked. So
the last `JobFailureLog.Capacity` failures move to a side table on the way out, and both
`Complete` and a dependency edge added after the fact read from there. A job whose dependency threw
is marked failed and skipped rather than run against inputs that were never produced.

## The safety system

Two checks, both compiled in under `DEBUG` or `VIXEN_JOB_SAFETY` and out of everything else;
`JobScheduler.SafetyChecksEnabled` says which build this is.

The first needs nothing from anybody: a job that completes its own handle is caught and told so,
instead of waiting forever for the work item that is doing the waiting.

The second is [doc 03](../../docs/plan/03-core-foundation.md)'s — jobs declare what they touch and
the scheduler refuses a schedule that lets two conflicting ones run together. It differs from the
design in two ways, both because of where the declarations come from.

- **A resource is an `int`, not a `NativeArray`.** This assembly cannot know what a resource is. A
  `JobAccess` is two sets of opaque ids, and the one consumer that gives them meaning is `Vixen.Ecs`,
  which passes component type ids. Doc 03 named `NativeArray` because Unity's safety system is built
  on one; the numbering being somebody else's is the same design with the dependency the right way
  round.
- **The declaration is a scope, not an argument.** `scheduler.DeclareAccess(access)` returns a
  disposable that applies to every job the calling thread schedules inside it. The caller that knows
  the access and the caller that schedules the work are usually not the same one: `SystemRunner`
  brackets each system's `Update` with what the system graph ordered that system by, and the system
  inside schedules whatever it likes.

**The check is at schedule time, and it is exact rather than opportunistic.** A new job is compared
against every declared job that is in flight and that it does not — directly or transitively — depend
on, because those are precisely the ones the scheduler is free to run alongside it. Ancestry is a bit
per slot, inherited from each dependency's row at `Publish`. Checking instead whether two conflicting
jobs *happened* to overlap would make the detector's own tests a matter of how busy the machine was.

⚠ **What it actually catches is a system that drops its handle.** `Update` returning `dependency`
instead of the handle for the work it just scheduled compiles, type-checks, and produces a phase
whose `Complete` loop waits for nothing — so the job runs on into the next system's turn, where the
runner's conflict graph, which is about systems and not about jobs, cannot see it. Nothing else in
the repository catches that.

⚠ **A clean run is three different facts, so read the counters.** `DeclaredJobsScheduled` and
`AccessComparisons` are public for that reason: a build with the checks compiled out, a build where
nothing ever opened a scope, and a build that compared every pair and found nothing all raise no
exception, and only those two numbers tell them apart.

**`JobAccess.None` and `JobAccess.Everything` are not opposites.** `None` means *undeclared* — not
policed at all, which is what every job outside the ECS is, and what keeps the detector from firing
on an asset import that overlapped a frame. `Everything` means *declared, and touching all of it*.
An undeclared **system** maps onto `Everything`, because "I did not say" already means "conflicts
with everything" in `SystemAccess`.

## What is not here yet

**Thread affinity.** Doc 03 asks for workers pinned where the OS allows. `Thread` has no portable
affinity API, the per-platform ones differ in kind rather than in spelling, and pinning is a
pessimisation on a machine that is running anything else. It waits for `Vixen.Platform`, which is
where the per-OS calls will already live.

Licensed under Apache-2.0.
