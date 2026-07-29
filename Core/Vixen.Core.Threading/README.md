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

**Priorities and long-running jobs.** Every job here is equal and expected to be short. Streaming and
asset decode want a lower-priority tier that never delays a frame job; that is a second deque per
worker and a scheduling rule, and it should be built when there is something to measure it against.

Licensed under Apache-2.0.
