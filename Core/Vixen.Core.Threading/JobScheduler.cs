// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Vixen.Core.Diagnostics;

namespace Vixen.Core.Threading;

/// <summary>
///     Persistent worker threads, a dependency graph, and work stealing. Frame work runs here.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why not the thread pool.</b> The .NET thread pool is tuned for throughput over an
///         unbounded stream of independent work, and its hill-climbing heuristic responds to a busy
///         period by injecting threads — on a schedule measured in hundreds of milliseconds, against
///         a frame budget measured in single digits. A frame's work is also not independent: it is a
///         graph with a deadline, and the pool has no way to express that. So: a fixed set of threads
///         that exist for the lifetime of the process, and a graph the scheduler resolves itself.
///     </para>
///     <para>
///         <b>Shape.</b> <see cref="WorkerCount" /> workers, one fewer than the processor count,
///         because the thread that drives the frame is the missing one and it participates — a call
///         to <see cref="Complete(JobHandle)" /> executes ready work while it waits instead of
///         idling. Each worker owns a <see cref="WorkStealingDeque" />; threads that are not workers
///         push to a shared queue. A worker out of its own work takes from the shared queue, then
///         steals. <see cref="WorkerCount" /> may be zero, which leaves only the participating
///         thread — see <see cref="IsSingleThreaded" />.
///     </para>
///     <para>
///         <b>Cost of scheduling.</b> Nothing on the path from
///         <see cref="Schedule{TJob}(in TJob, JobHandle, JobPriority)" /> to the job running
///         allocates. The job struct is copied into a preallocated array; the slot comes from a
///         preallocated ring; the work item is a <see cref="long" /> in a preallocated buffer. A job
///         type is generic all the way down, so there is no boxing and no delegate.
///     </para>
///     <para>
///         Thread-safe: any thread may schedule, and any thread may complete.
///     </para>
/// </remarks>
public sealed class JobScheduler : IDisposable {
#if DEBUG || VIXEN_JOB_SAFETY
    /// <summary>Whether the debug-only scheduler assertions are compiled into this build.</summary>
    public const bool SafetyChecksEnabled = true;
#else
    /// <summary>Whether the debug-only scheduler assertions are compiled into this build.</summary>
    public const bool SafetyChecksEnabled = false;
#endif

    /// <summary>How many jobs one scheduler can have in flight.</summary>
    /// <remarks>
    ///     The ring is preallocated, and every job type that gets scheduled allocates a payload array
    ///     of this length, so the bound is a memory decision as much as a concurrency one. A frame's
    ///     graph is tens to low hundreds of jobs; a thousand outstanding at once means something has
    ///     scheduled and never completed.
    /// </remarks>
    public const int MaxJobsInFlight = 1024;

    /// <summary>How many schedulers can exist at once.</summary>
    /// <remarks>
    ///     A <see cref="JobHandle" /> names its scheduler by index so it can stay blittable, and the
    ///     index has to come from somewhere bounded. One scheduler is the expected number; the rest
    ///     of the room is for tests, tools, and an editor hosting a game.
    /// </remarks>
    public const int MaxSchedulers = 8;

    const int DequeCapacity = 1024;

    // Smaller than the frame tier's, deliberately. Overflowing a deque is not a failure — the item
    // goes on the shared queue instead — so the only thing capacity buys is locality, and locality
    // is the thing background work is defined as not caring about.
    const int BackgroundDequeCapacity = 256;

    // The fairness share: after this many frame items, a thread takes a background one if there is
    // one. Sized against the scheduler's own statement that a frame's graph is "tens to low
    // hundreds of jobs" — so an ordinary frame pulls in at most one or two background items per
    // thread, while a program that never stops scheduling frame work still drains the background
    // tier at a sixty-fourth of the rate rather than never. It is the whole of the starvation
    // guarantee, and it is asserted as work completed rather than as time elapsed.
    const int FairnessStride = 64;

    // Measured, not guessed. The first version parked a worker after roughly a microsecond of
    // spinning, which meant a burst of a hundred jobs woke and re-parked the whole pool several
    // times over and cost 1.1 microseconds per job in semaphore traffic. Spinning for tens of
    // microseconds instead keeps the workers hot for the length of a burst; parking is for the gap
    // between frames, not for the gap between two jobs.
    const int SpinsBeforeYield = 96;
    const int SpinsBeforeSleep = 192;

    // Flat, not exponential. Backing off exponentially was measured and rejected: it made a single
    // scheduled job round-trip 2.4x faster, because the workers stopped noticing it and the waiting
    // thread ran it inline — and made a burst of a hundred jobs 20% slower, because the workers
    // also stopped noticing those. A frame is a burst.
    const int SpinDuration = 40;
    const int WakeIntervalMilliseconds = 2;

    static readonly JobScheduler?[] Schedulers = new JobScheduler[MaxSchedulers];
    static readonly Lock RegistryGate = new();

    // Never reused, where Id is: a thread's fairness counter below is keyed on it, and keying on Id
    // would let a disposed scheduler's counter carry into whichever one takes its place in the
    // table. Keying on the reference instead would keep a disposed scheduler — and its thousand
    // slots — alive on every thread that ever helped it.
    static long schedulerEpoch;

    [ThreadStatic] static JobScheduler? workerOf;
    [ThreadStatic] static int workerIndex;
    [ThreadStatic] static uint stealSeed;

    // Per thread, not per scheduler: a shared counter incremented on every take is a cache line
    // every worker writes to, which is the one thing the deques exist to avoid. Per thread it costs
    // nothing and means the same thing — this thread has taken this many frame items in a row.
    [ThreadStatic] static long fairnessEpoch;
    [ThreadStatic] static int framesTakenSinceBackground;

    // Slot index *plus one*, so that zero — the value a thread that has never run a work item has —
    // means "this thread is running nothing" instead of aliasing slot 0. It aliased slot 0, and the
    // self-completion guard below fired on the main thread whenever the job it was waiting for
    // happened to live there. Paired with the version, because a slot recycled between the two
    // reads is a different job in the same place.
    [ThreadStatic] static int executingSlotPlusOne;
    [ThreadStatic] static int executingVersion;

#if DEBUG || VIXEN_JOB_SAFETY
    // The declaration in force on this thread. Process-wide rather than per scheduler, because a
    // thread scheduling into two schedulers at once inside one scope is not a shape any caller has,
    // and a per-scheduler table would cost a lookup on the scheduling path to describe it.
    [ThreadStatic] static JobAccess? declaredAccess;

    // 64 bits of ancestry per slot, one row per slot: bit s of row i means "job i transitively
    // depends on whatever is in slot s". A row is rebuilt from the dependencies at Publish, which is
    // the only moment a job's ancestry can change — nothing may be added to a job's dependencies
    // after its handle exists.
    const int AncestorWords = MaxJobsInFlight / 64;

    readonly Lock accessGate = new();
    readonly JobAccess?[] slotAccess = new JobAccess[MaxJobsInFlight];
    readonly int[] slotAccessVersion = new int[MaxJobsInFlight];
    ulong[]? ancestors;
    long accessComparisons;
    long declaredJobsScheduled;
#endif

    readonly JobSlot[] slots = new JobSlot[MaxJobsInFlight];
    readonly int[] freeSlots = new int[MaxJobsInFlight];
    readonly Lock freeGate = new();
    int freeCount;

    readonly JobPayloadStore?[] sequentialStores = new JobPayloadStore[JobTypeIds.MaxJobTypes];
    readonly JobPayloadStore?[] parallelStores = new JobPayloadStore[JobTypeIds.MaxJobTypes];
    readonly Lock storeGate = new();

    readonly JobFailureLog failures = new();

    readonly WorkStealingDeque[] deques;
    readonly WorkStealingDeque[] backgroundDeques;
    readonly ConcurrentQueue<long> shared = new();
    readonly ConcurrentQueue<long> sharedBackground = new();
    readonly SemaphoreSlim signal = new(0);
    readonly Thread[] workers;
    readonly long epoch = Interlocked.Increment(ref schedulerEpoch);

    // How many workers may be inside a background job at once, and how many background items exist.
    // The second is what keeps an idle worker off the first: without it every failed take would
    // write to the reservation counter, and a pool with nothing to do would spend its spin loop
    // fighting over one cache line.
    readonly int backgroundLimit;
    int backgroundRunning;
    int backgroundQueued;

    // Null unless a host supplied one, which is the default: pinning is a pessimisation on a machine
    // that is running anything else, so it is asked for rather than assumed.
    readonly IWorkerPlacement? placement;
    int workersPlaced;

    int sleepingWorkers;
    volatile bool stopping;
    bool disposed;

    /// <summary>How many worker threads this scheduler owns, not counting threads that help.</summary>
    public int WorkerCount { get; }

    /// <summary>Whether this scheduler owns no threads and runs everything on its callers.</summary>
    /// <remarks>
    ///     <para>
    ///         True for <c>new JobScheduler(0)</c>, which is the browser's only option: a
    ///         WebAssembly build without <c>SharedArrayBuffer</c> and the cross-origin isolation
    ///         headers that unlock it cannot start a <see cref="Thread" /> at all, so a scheduler
    ///         that tried would throw <see cref="PlatformNotSupportedException" /> at construction.
    ///     </para>
    ///     <para>
    ///         <c>docs/plan/10 § Cross-platform discipline</c> requires every subsystem to work with
    ///         <c>workerCount == 0</c>. Reading this is for diagnostics and for a caller deciding
    ///         whether to bother splitting work at all; it is deliberately <em>not</em> something the
    ///         scheduling API branches on, because a mode that needs different calls is a mode that
    ///         is only exercised by the platform that needs it.
    ///     </para>
    /// </remarks>
    public bool IsSingleThreaded => WorkerCount == 0;

    /// <summary>This scheduler's index in the process-wide table. Carried by every handle it issues.</summary>
    public int Id { get; }

    /// <summary>Whether the calling thread is one of this scheduler's workers.</summary>
    public bool IsWorkerThread => ReferenceEquals(workerOf, this);

    /// <summary>How many workers an <see cref="IWorkerPlacement" /> actually placed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read this before believing a machine is pinned.</b> Three different facts all
    ///         look identical from outside: a scheduler given no placement, a scheduler given one on
    ///         a platform that answers <see langword="false" /> to everything, and a scheduler whose
    ///         workers are every one of them pinned. Nothing about the work that comes out
    ///         distinguishes them — the same jobs finish, the same counters move — and a benchmark
    ///         that concluded pinning did not help would be right about the number and wrong about
    ///         why. This is the only thing that says which of the three happened, and zero is the
    ///         answer on the day the feature does not run.
    ///     </para>
    ///     <para>
    ///         Racy only while workers are starting or stopping. It rises as each worker places
    ///         itself and falls as each releases, so it reads zero again after
    ///         <see cref="Dispose" />.
    ///     </para>
    /// </remarks>
    public int WorkersPlaced => Volatile.Read(ref workersPlaced);

    /// <summary>How many jobs are scheduled and not yet complete.</summary>
    /// <remarks>
    ///     Racy while work is in flight — it is a snapshot of a number several threads are moving —
    ///     but not racy in the way that matters: a slot is returned before its job's completion
    ///     becomes visible, so once <see cref="Complete(JobHandle)" /> has returned for every handle
    ///     scheduled, this reads zero.
    /// </remarks>
    public int OutstandingJobs {
        get {
            lock (freeGate) {
                return MaxJobsInFlight - freeCount;
            }
        }
    }

    // The three members below read nothing but a thread-static in a build with the safety system
    // compiled out, and CA1822 would have them static there and instance in a build with it. The
    // public surface is not allowed to differ between the two — `CheckApi` baselines the Release one
    // and the ECS calls the Debug one — so the shape is fixed and the rule is answered here.
#pragma warning disable CA1822 // Instance in both configurations on purpose; see above.

    /// <summary>How many jobs have been scheduled carrying a declaration.</summary>
    /// <remarks>
    ///     ⚠ <b>Read this before believing a clean run.</b> A build with
    ///     <see cref="SafetyChecksEnabled" /> false, a build where nothing ever opened a
    ///     <see cref="JobAccessScope" />, and a build where the check ran against every pair and
    ///     found nothing all report no conflict. Only this and <see cref="AccessComparisons" />
    ///     separate them: zero here means the safety system was never fed, which is a different fact
    ///     from "no race".
    /// </remarks>
    public long DeclaredJobsScheduled {
        get {
#if DEBUG || VIXEN_JOB_SAFETY
            return Interlocked.Read(ref declaredJobsScheduled);
#else
            return 0;
#endif
        }
    }

    /// <summary>How many pairs of jobs the safety system has actually compared.</summary>
    /// <remarks>
    ///     The other half of the instrument. A declared job with nothing else in flight is compared
    ///     against nothing, so <see cref="DeclaredJobsScheduled" /> can be large while this is zero —
    ///     which says the declarations exist and never overlapped, and is again not the same fact as
    ///     "no race".
    /// </remarks>
    public long AccessComparisons {
        get {
#if DEBUG || VIXEN_JOB_SAFETY
            return Interlocked.Read(ref accessComparisons);
#else
            return 0;
#endif
        }
    }

    /// <summary>Declares what every job this thread schedules from here on reads and writes.</summary>
    /// <param name="access">The declaration, or <see cref="JobAccess.None" /> to declare nothing.</param>
    /// <returns>A scope that puts the previous declaration back when it is disposed.</returns>
    /// <remarks>
    ///     <para>
    ///         Inside the scope, a job scheduled on this thread is checked at the moment it is
    ///         scheduled against every other declared job that is in flight and that this one does
    ///         not, directly or transitively, depend on. Two such jobs can run at the same time, so
    ///         if their declarations conflict the schedule is a data race and
    ///         <see cref="Schedule{TJob}(in TJob, JobHandle, JobPriority)" /> throws instead of
    ///         letting it happen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The check is only as honest as the declaration.</b> A job that touches more than
    ///         it declared is invisible to it — which is why the ECS derives the declaration from the
    ///         same <c>SystemAccess</c> the system graph orders systems by, rather than from a second
    ///         statement that could disagree with the first.
    ///     </para>
    ///     <para>
    ///         Compiled out entirely unless <see cref="SafetyChecksEnabled" />, where it returns a
    ///         scope whose disposal does nothing.
    ///     </para>
    /// </remarks>
    public JobAccessScope DeclareAccess(JobAccess access) {
        ArgumentNullException.ThrowIfNull(access);

#if DEBUG || VIXEN_JOB_SAFETY
        var previous = declaredAccess;
        declaredAccess = access;
        return new(this, previous);
#else
        return default;
#endif
    }

#pragma warning restore CA1822

    /// <summary>Creates a scheduler with one worker per processor beyond the calling thread.</summary>
    /// <remarks>
    ///     Zero workers where the runtime has no threads to give — a browser tab that is not
    ///     cross-origin isolated. Asking for one there does not fail slowly or run slowly; it throws
    ///     <see cref="PlatformNotSupportedException" /> out of <see cref="Thread.Start()" />, which
    ///     is not an answer a default constructor should give.
    /// </remarks>
    public JobScheduler() : this(DefaultWorkerCount()) { }

    /// <summary>Creates a scheduler with a chosen number of workers.</summary>
    /// <param name="workerCount">
    ///     How many worker threads to start. Zero is the single-threaded mode — see
    ///     <see cref="IsSingleThreaded" />.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount" /> is negative.</exception>
    /// <exception cref="InvalidOperationException">There are already <see cref="MaxSchedulers" /> schedulers.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Zero workers is the same scheduler, not a second one.</b> The graph, the slot ring,
    ///         the failure log and the batching are untouched; what changes is that nothing is
    ///         running work in the background, so a job runs when a thread reaches
    ///         <see cref="Complete(JobHandle)" /> — or <see cref="Dispose" />, which drains — and
    ///         not before. Every dependency edge is still honoured, and a job still cannot start
    ///         until its predecessors have finished, so a graph that is correct with workers is
    ///         correct without them.
    ///     </para>
    ///     <para>
    ///         What a caller loses is overlap, and one guarantee worth stating plainly: work that is
    ///         scheduled and never completed never runs. With workers a fire-and-forget job happens
    ///         anyway; with none it sits in the queue until <see cref="Dispose" />. Code that relies
    ///         on the first is code that has a bug on the web, which is why the test suite has a leg
    ///         that runs at zero.
    ///     </para>
    /// </remarks>
    public JobScheduler(int workerCount) : this(workerCount, placement: null) { }

    /// <summary>Creates a scheduler whose workers place themselves as they start.</summary>
    /// <param name="workerCount">How many worker threads to start.</param>
    /// <param name="placement">
    ///     Where each worker should run, asked of the worker itself, or <see langword="null" /> to
    ///     leave the operating system to decide — which is the default and the right answer on a
    ///     machine that is running anything else.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount" /> is negative.</exception>
    /// <exception cref="InvalidOperationException">There are already <see cref="MaxSchedulers" /> schedulers.</exception>
    /// <remarks>
    ///     ⚠ <b>Supplying a placement is not the same as being placed.</b> Every platform is entitled
    ///     to answer "no" — macOS does, in a browser there is nothing to pin, and under a container
    ///     CPU quota the mask is not ours to set — so a scheduler built with a placement can have
    ///     pinned nothing at all and behave exactly like one built without. <see cref="WorkersPlaced" />
    ///     is what tells the two apart, and it exists because nothing else does.
    /// </remarks>
    public JobScheduler(int workerCount, IWorkerPlacement? placement) {
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);

        this.placement = placement;
        WorkerCount = workerCount;
        Id = Register(this);

        for (var index = 0; index < slots.Length; index++) {
            slots[index] = new();
            freeSlots[index] = index;
        }

        freeCount = MaxJobsInFlight;

        deques = new WorkStealingDeque[workerCount];
        backgroundDeques = new WorkStealingDeque[workerCount];
        workers = new Thread[workerCount];

        // One worker is always kept out of the background tier, so a burst of long background jobs
        // cannot occupy the whole pool and leave a frame with nothing but the thread that is waiting
        // for it. At one worker there is nothing to reserve and the rule degrades to the behaviour
        // there was before it existed.
        backgroundLimit = Math.Max(1, workerCount - 1);

        for (var index = 0; index < workerCount; index++) {
            deques[index] = new(DequeCapacity);
            backgroundDeques[index] = new(BackgroundDequeCapacity);
        }

        for (var index = 0; index < workerCount; index++) {
            var ordinal = index;

            workers[index] = new(() => RunWorker(ordinal)) {
                IsBackground = true,
                Name = $"Vixen Job Worker {ordinal}"
            };

            workers[index].Start();
        }
    }

    /// <summary>Schedules a job to run once, after <paramref name="dependsOn" /> has finished.</summary>
    /// <typeparam name="TJob">The job type. Generic all the way down, so nothing boxes.</typeparam>
    /// <param name="job">The job. Copied into the scheduler; later changes to your copy do nothing.</param>
    /// <param name="dependsOn">What must finish first, or <c>default</c> for nothing.</param>
    /// <param name="priority">
    ///     Which tier it goes in. <see cref="JobPriority.Frame" /> unless said otherwise.
    /// </param>
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle Schedule<TJob>(
        in TJob job,
        JobHandle dependsOn = default,
        JobPriority priority = JobPriority.Frame
    ) where TJob : struct, IJob {
        ValidateDependency(dependsOn);
        var store = SequentialStore<TJob>();
        var index = RentSlot(store, 1, 0, 0, priority, out var version);
        store.Store(index, in job);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Schedules a job to run once, after every one of <paramref name="dependsOn" /> has finished.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="dependsOn">What must finish first. Null handles are ignored.</param>
    /// <param name="priority">
    ///     Which tier it goes in. <see cref="JobPriority.Frame" /> unless said otherwise.
    /// </param>
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle Schedule<TJob>(
        in TJob job,
        ReadOnlySpan<JobHandle> dependsOn,
        JobPriority priority = JobPriority.Frame
    ) where TJob : struct, IJob {
        ValidateDependencies(dependsOn);
        var store = SequentialStore<TJob>();
        var index = RentSlot(store, 1, 0, 0, priority, out var version);
        store.Store(index, in job);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Schedules <paramref name="length" /> indices across the workers.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job. One copy is shared by every batch.</param>
    /// <param name="length">How many indices to run. Zero schedules a job that completes immediately.</param>
    /// <param name="batchSize">
    ///     How many indices one work item covers, or zero to let the scheduler choose. It aims for
    ///     four batches per participating thread, which is enough for stealing to even out uneven
    ///     work without making the per-batch overhead visible.
    /// </param>
    /// <param name="dependsOn">What must finish first, or <c>default</c> for nothing.</param>
    /// <param name="priority">
    ///     Which tier the batches go in. <see cref="JobPriority.Frame" /> unless said otherwise.
    ///     Splitting long work into batches is what makes <see cref="JobPriority.Background" />
    ///     effective, because the tier defers work rather than interrupting it.
    /// </param>
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle ScheduleParallel<TJob>(
        in TJob job,
        int length,
        int batchSize = 0,
        JobHandle dependsOn = default,
        JobPriority priority = JobPriority.Frame
    ) where TJob : struct, IJobParallelFor {
        ValidateDependency(dependsOn);
        var index = RentParallelSlot(in job, length, batchSize, priority, out var version);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Schedules <paramref name="length" /> indices across the workers.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="length">How many indices to run.</param>
    /// <param name="batchSize">How many indices one work item covers, or zero to let the scheduler choose.</param>
    /// <param name="dependsOn">What must finish first. Null handles are ignored.</param>
    /// <param name="priority">
    ///     Which tier the batches go in. <see cref="JobPriority.Frame" /> unless said otherwise.
    /// </param>
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle ScheduleParallel<TJob>(
        in TJob job,
        int length,
        int batchSize,
        ReadOnlySpan<JobHandle> dependsOn,
        JobPriority priority = JobPriority.Frame
    ) where TJob : struct, IJobParallelFor {
        ValidateDependencies(dependsOn);
        var index = RentParallelSlot(in job, length, batchSize, priority, out var version);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Runs <paramref name="length" /> indices across the workers and waits for them.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="length">How many indices to run.</param>
    /// <param name="batchSize">How many indices one work item covers, or zero to let the scheduler choose.</param>
    /// <param name="priority">
    ///     Which tier the batches go in. <see cref="JobPriority.Frame" /> unless said otherwise.
    /// </param>
    /// <exception cref="JobExecutionException">The job threw.</exception>
    /// <remarks>
    ///     ⚠ <b><see cref="JobPriority.Background" /> here does not stop the caller waiting.</b> The
    ///     calling thread still blocks until every batch is done, and — because it participates —
    ///     still runs batches itself. What the tier changes is only whether the <em>workers</em>
    ///     prefer these batches over a frame's.
    /// </remarks>
    public void ParallelFor<TJob>(
        in TJob job,
        int length,
        int batchSize = 0,
        JobPriority priority = JobPriority.Frame
    ) where TJob : struct, IJobParallelFor =>
        // Named rather than positional. `default` on its own for the dependency is ambiguous
        // between the two overloads, and naming the argument is the same thing a caller does.
        Complete(ScheduleParallel(in job, length, batchSize, priority: priority));

    /// <summary>Waits for a job, executing other ready work while it waits.</summary>
    /// <param name="handle">The job to wait for. The null handle returns immediately.</param>
    /// <exception cref="JobExecutionException">The job, or one it depended on, threw.</exception>
    /// <exception cref="ArgumentException">The handle belongs to a different scheduler.</exception>
    /// <remarks>
    ///     <para>
    ///         The waiting thread is not parked. It takes ready work — including work unrelated to
    ///         what it is waiting for — until the job it wants is finished. That is what makes the
    ///         main thread a participant rather than an observer, and it is why a frame that
    ///         schedules its work early and completes it late gets the whole machine.
    ///     </para>
    ///     <para>
    ///         That is also the whole of the single-threaded mode. With
    ///         <see cref="WorkerCount" /> zero this loop is the only thing that ever runs a job, so
    ///         the frame's graph executes here, in dependency order, on the calling thread — no
    ///         second code path, and therefore nothing that only the browser exercises.
    ///     </para>
    /// </remarks>
    public void Complete(JobHandle handle) {
        if (handle.IsNull) {
            return;
        }

        if (handle.SchedulerId != Id) {
            throw new ArgumentException("The handle was issued by a different scheduler.", nameof(handle));
        }

        var slot = slots[handle.Index];

#if DEBUG || VIXEN_JOB_SAFETY
        if (executingSlotPlusOne == handle.Index + 1 && executingVersion == handle.Version) {
            throw new InvalidOperationException(
                "A job cannot complete itself. This would wait forever: the work item doing the "
                + "waiting is the one the job is waiting for."
            );
        }
#endif

        var spins = 0;

        while (!IsComplete(slot, handle.Version)) {
            if (TryExecuteOneWorkItem()) {
                spins = 0;
                continue;
            }

            if (++spins < SpinsBeforeSleep) {
                Thread.SpinWait(20);
            } else {
                Thread.Yield();
                spins = 0;
            }
        }

        ThrowIfFailed(handle);
    }

    /// <summary>Whether a job has finished. Never blocks.</summary>
    /// <param name="handle">The job.</param>
    /// <returns><see langword="true" /> if it is finished, or if the handle is null or stale.</returns>
    public bool IsCompleted(JobHandle handle) =>
        handle.IsNull || handle.SchedulerId != Id || IsComplete(slots[handle.Index], handle.Version);

    /// <summary>Stops the workers, draining whatever is still outstanding first.</summary>
    /// <remarks>
    ///     Draining rather than abandoning. Jobs write into memory their scheduler does not own, so
    ///     tearing down with work in flight would leave half-written buffers behind and no way to
    ///     tell which. The disposing thread helps with the drain, so this is bounded by the work
    ///     remaining and not by a timeout.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        while (OutstandingJobs > 0) {
            if (!TryExecuteOneWorkItem()) {
                Thread.Yield();
            }
        }

        stopping = true;

        // Release(0) is an ArgumentOutOfRangeException, not a no-op, so the single-threaded mode
        // would fail its own teardown on the one line that has nothing to wake.
        if (WorkerCount > 0) {
            signal.Release(WorkerCount);
        }

        foreach (var worker in workers) {
            worker.Join();
        }

        signal.Dispose();
        Unregister(this);
        GC.SuppressFinalize(this);
    }

    internal static JobScheduler? OwnerOf(ReadOnlySpan<JobHandle> handles) {
        JobScheduler? owner = null;

        foreach (var handle in handles) {
            if (handle.IsNull) {
                continue;
            }

            var scheduler = Volatile.Read(ref Schedulers[handle.SchedulerId]);

            if (scheduler is null) {
                continue;
            }

            if (owner is not null && !ReferenceEquals(owner, scheduler)) {
                throw new ArgumentException(
                    "The handles belong to different schedulers, so there is no scheduler that could "
                    + "own the job joining them.",
                    nameof(handles)
                );
            }

            owner = scheduler;
        }

        return owner;
    }

    internal static bool IsHandleCompleted(in JobHandle handle) {
        if (handle.IsNull) {
            return true;
        }

        // A disposed scheduler has drained, so anything it issued is finished by definition.
        var scheduler = Volatile.Read(ref Schedulers[handle.SchedulerId]);
        return scheduler is null || scheduler.IsCompleted(handle);
    }

    internal static void CompleteHandle(in JobHandle handle) {
        if (handle.IsNull) {
            return;
        }

        Volatile.Read(ref Schedulers[handle.SchedulerId])?.Complete(handle);
    }

    internal JobHandle CombineCore(ReadOnlySpan<JobHandle> handles) {
        var empty = default(EmptyJob);
        return Schedule(in empty, handles);
    }

    /// <summary>How many workers the parameterless constructor asks for.</summary>
    /// <remarks>
    ///     <para>
    ///         One per processor beyond the calling thread, which participates — except in a browser,
    ///         where the answer is none.
    ///     </para>
    ///     <para>
    ///         <b>The browser case is decided by the target and not by a count.</b> Threads on
    ///         <c>browser-wasm</c> need <c>SharedArrayBuffer</c>, which needs COOP/COEP headers on
    ///         every response, which is a deployment decision the engine cannot read from inside the
    ///         page — and <see cref="Environment.ProcessorCount" /> answers with the machine's core
    ///         count either way. Defaulting to zero means a page that did not arrange for isolation
    ///         runs, slower; the alternative is a
    ///         <see cref="PlatformNotSupportedException" /> from <see cref="Thread.Start()" /> before
    ///         the first frame. A cross-origin-isolated build that wants the threads it has passes
    ///         the count it wants.
    ///     </para>
    /// </remarks>
    static int DefaultWorkerCount() =>
        OperatingSystem.IsBrowser() ? 0 : Math.Max(1, Environment.ProcessorCount - 1);

    static int Register(JobScheduler scheduler) {
        lock (RegistryGate) {
            for (var index = 0; index < Schedulers.Length; index++) {
                if (Schedulers[index] is null) {
                    Volatile.Write(ref Schedulers[index], scheduler);
                    return index;
                }
            }
        }

        throw new InvalidOperationException(
            $"There are already {MaxSchedulers} job schedulers. A handle names its scheduler by "
            + "index, so the table is fixed; dispose one before creating another."
        );
    }

    static void Unregister(JobScheduler scheduler) {
        lock (RegistryGate) {
            if (ReferenceEquals(Schedulers[scheduler.Id], scheduler)) {
                Volatile.Write(ref Schedulers[scheduler.Id], null);
            }
        }
    }

    static bool IsComplete(JobSlot slot, int version) =>
        Volatile.Read(ref slot.Version) != version || Volatile.Read(ref slot.IsComplete);

    void ThrowIfFailed(JobHandle handle) {
        // From the log, not from the slot. By the time a waiter observes completion the slot may
        // already belong to another job, and reading a failure off it would be reading someone
        // else's.
        var failure = failures.Find(handle.Index, handle.Version);

        if (failure is not null) {
            throw new JobExecutionException("A job threw an exception. See the inner exception.", failure.SourceException);
        }
    }

    static void ComputeBatching(int length, int requested, int participants, out int batchSize, out int batchCount) {
        if (length == 0) {
            // One empty work item, so completion goes down the same path as everything else.
            batchSize = 0;
            batchCount = 1;
            return;
        }

        var size = requested;

        if (size <= 0) {
            // Four batches per participant, so stealing can even out uneven work — except when
            // there is one participant and therefore nobody to steal, where the four are one
            // thread's four and the split buys nothing but three extra work items.
            var target = participants <= 1 ? 1 : participants * 4;
            size = (length + target - 1) / target;
        }

        batchSize = Math.Max(1, size);
        batchCount = (length + batchSize - 1) / batchSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long Pack(int slot, int batch) => ((long)slot << 32) | (uint)batch;


    SequentialJobStore<TJob> SequentialStore<TJob>() where TJob : struct, IJob {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = SequentialJobType<TJob>.Id;
        var store = Volatile.Read(ref sequentialStores[id]);

        if (store is null) {
            lock (storeGate) {
                store = sequentialStores[id];

                if (store is null) {
                    store = new SequentialJobStore<TJob>(MaxJobsInFlight);
                    Volatile.Write(ref sequentialStores[id], store);
                }
            }
        }

        return (SequentialJobStore<TJob>)store;
    }

    ParallelJobStore<TJob> ParallelStore<TJob>() where TJob : struct, IJobParallelFor {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = ParallelJobType<TJob>.Id;
        var store = Volatile.Read(ref parallelStores[id]);

        if (store is null) {
            lock (storeGate) {
                store = parallelStores[id];

                if (store is null) {
                    store = new ParallelJobStore<TJob>(MaxJobsInFlight);
                    Volatile.Write(ref parallelStores[id], store);
                }
            }
        }

        return (ParallelJobStore<TJob>)store;
    }

    int RentParallelSlot<TJob>(in TJob job, int length, int batchSize, JobPriority priority, out int version)
        where TJob : struct, IJobParallelFor {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var store = ParallelStore<TJob>();
        ComputeBatching(length, batchSize, WorkerCount + 1, out var size, out var count);
        var index = RentSlot(store, count, size, length, priority, out version);
        store.Store(index, in job);
        return index;
    }

    int RentSlot(
        JobPayloadStore store,
        int batchCount,
        int batchSize,
        int length,
        JobPriority priority,
        out int version
    ) {
        while (true) {
            var index = -1;

            lock (freeGate) {
                if (freeCount > 0) {
                    index = freeSlots[--freeCount];
                }
            }

            if (index >= 0) {
                version = slots[index].Reset(store, batchCount, batchSize, length, priority);
                return index;
            }

            // Out of slots. The thread asking for one can pay for one, by finishing a job that is
            // already scheduled — which is also the only thing that can free a slot.
            if (!TryExecuteOneWorkItem()) {
                Thread.Yield();
            }
        }
    }

    void ReturnSlot(int index) {
        lock (freeGate) {
            freeSlots[freeCount++] = index;
        }
    }

    JobHandle Publish(int index, int version, JobHandle dependsOn) {
        var slot = slots[index];

        // Count the edge before adding it. The other order lets the dependency finish between the
        // add and the increment, so its decrement lands on a counter that has not been raised yet
        // and the job starts while its graph is still being built.
        Interlocked.Increment(ref slot.PendingDependencies);

        if (!TryAddEdge(dependsOn, index)) {
            Interlocked.Decrement(ref slot.PendingDependencies);
            InheritFailure(slot, dependsOn);
        }

#if DEBUG || VIXEN_JOB_SAFETY
        ReadOnlySpan<JobHandle> only = [dependsOn];
        var violation = RecordDeclaredAccess(index, version, slot, only);

        if (violation is not null) {
            // Failed rather than abandoned: a failed job is skipped by the executor and its slot
            // travels the ordinary completion path, so the racing work never runs and the ring does
            // not leak the slot the throw below is about to abandon.
            Interlocked.CompareExchange(ref slot.Failure, ExceptionDispatchInfo.Capture(violation), null);
        }
#endif

        var handle = Release(index, slot, version);

#if DEBUG || VIXEN_JOB_SAFETY
        if (violation is not null) {
            throw violation;
        }
#endif

        return handle;
    }

    JobHandle Publish(int index, int version, ReadOnlySpan<JobHandle> dependsOn) {
        var slot = slots[index];

        foreach (var dependency in dependsOn) {
            Interlocked.Increment(ref slot.PendingDependencies);

            if (!TryAddEdge(dependency, index)) {
                Interlocked.Decrement(ref slot.PendingDependencies);
                InheritFailure(slot, dependency);
            }
        }

#if DEBUG || VIXEN_JOB_SAFETY
        var violation = RecordDeclaredAccess(index, version, slot, dependsOn);

        if (violation is not null) {
            Interlocked.CompareExchange(ref slot.Failure, ExceptionDispatchInfo.Capture(violation), null);
        }
#endif

        var handle = Release(index, slot, version);

#if DEBUG || VIXEN_JOB_SAFETY
        if (violation is not null) {
            throw violation;
        }
#endif

        return handle;
    }

    /// <summary>Puts back the declaration a scope replaced.</summary>
    /// <remarks>
    ///     Static because the declaration is the thread's rather than the scheduler's — see
    ///     <see cref="DeclareAccess" />. The scope still remembers which scheduler issued it, so that
    ///     the default scope, which no scheduler issued, disposes to nothing.
    /// </remarks>
    internal static void RestoreDeclaredAccess(JobAccess? previous) {
#if DEBUG || VIXEN_JOB_SAFETY
        declaredAccess = previous;
#endif
    }

#if DEBUG || VIXEN_JOB_SAFETY
    /// <summary>
    ///     Records what this job declared and its ancestry, and looks for a job it could race.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why the answer comes back rather than being thrown here.</b> The slot is rented and
    ///         its setup guard is still held at this point, so throwing would leave a slot that is
    ///         neither runnable nor free and <see cref="Dispose" /> would drain it forever. The
    ///         caller instead marks the slot failed — which makes the job skipped rather than run —
    ///         releases it through the ordinary path, and only then throws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It can miss, and never invents.</b> An ancestor row is inherited from the
    ///         dependency's row, and a dependency whose slot has since been re-rented no longer has
    ///         one — that dependency finished, so it cannot be racing anything, but a job it depended
    ///         on and that is somehow still in flight would no longer be recognised as an ancestor.
    ///         The result of that is a conflict reported that a human has to dismiss, not one
    ///         silently dropped, so the direction of the error is the safe one.
    ///     </para>
    /// </remarks>
    InvalidOperationException? RecordDeclaredAccess(
        int index,
        int version,
        JobSlot slot,
        ReadOnlySpan<JobHandle> dependsOn
    ) {
        var access = declaredAccess;

        lock (accessGate) {
            var rows = ancestors ??= new ulong[MaxJobsInFlight * AncestorWords];
            var row = index * AncestorWords;

            // Cleared whether or not anything is declared: a later job that depends on this one
            // inherits this row, and a row left over from whoever had the slot before would name
            // ancestors this job does not have.
            Array.Clear(rows, row, AncestorWords);

            foreach (var dependency in dependsOn) {
                if (dependency.IsNull || dependency.SchedulerId != Id) {
                    continue;
                }

                if (Volatile.Read(ref slots[dependency.Index].Version) != dependency.Version) {
                    continue;
                }

                var source = dependency.Index * AncestorWords;

                for (var word = 0; word < AncestorWords; word++) {
                    rows[row + word] |= rows[source + word];
                }

                rows[row + (dependency.Index >> 6)] |= 1ul << (dependency.Index & 63);
            }

            if (access is null || access.IsUndeclared) {
                // Undeclared, so this job is not policed — and must not be left claiming whatever
                // the slot's previous tenant declared.
                slotAccess[index] = null;
                return null;
            }

            slotAccess[index] = access;
            slotAccessVersion[index] = version;
            Interlocked.Increment(ref declaredJobsScheduled);

            for (var other = 0; other < MaxJobsInFlight; other++) {
                if (other == index) {
                    continue;
                }

                var candidate = slotAccess[other];

                if (candidate is null) {
                    continue;
                }

                var otherSlot = slots[other];

                // A slot whose version has moved on holds a different job, and one that is complete
                // cannot overlap anything scheduled after it.
                if (slotAccessVersion[other] != Volatile.Read(ref otherSlot.Version)
                    || Volatile.Read(ref otherSlot.IsComplete)) {
                    continue;
                }

                // Ordered by an edge, so the two can never be in flight together.
                if ((rows[row + (other >> 6)] & (1ul << (other & 63))) != 0) {
                    continue;
                }

                Interlocked.Increment(ref accessComparisons);

                if (!access.ConflictsWith(candidate)) {
                    continue;
                }

                return new(
                    $"{Describe(slot)} declares {access} and {Describe(otherSlot)} declares "
                    + $"{candidate}, they conflict, and neither depends on the other — so the "
                    + "scheduler is free to run them at the same time. Add a dependency between "
                    + "them, or narrow one of the declarations if it claims more than the job "
                    + "touches."
                );
            }
        }

        return null;
    }

    static string Describe(JobSlot slot) => slot.Store?.Key.Name ?? "a job";
#endif


    JobHandle Release(int index, JobSlot slot, int version) {
        // Drop the setup guard. If every dependency already finished, this is what starts the job.
        if (Interlocked.Decrement(ref slot.PendingDependencies) == 0) {
            MakeReady(index, slot);
        }

        return new(index, version, Id);
    }

    void InheritFailure(JobSlot slot, JobHandle dependency) {
        // The edge was refused because the dependency has already finished. "Finished" and
        // "succeeded" are not the same thing, and the difference is only recoverable from the log.
        if (dependency.IsNull) {
            return;
        }

        var failure = failures.Find(dependency.Index, dependency.Version);

        if (failure is not null) {
            Interlocked.CompareExchange(ref slot.Failure, failure, null);
        }
    }

    void ValidateDependency(JobHandle dependency) {
        // Before the slot is rented, not after. A throw between renting a slot and releasing its
        // setup guard would leave a slot that is neither runnable nor free, and the scheduler would
        // wait for it at Dispose forever.
        if (!dependency.IsNull && dependency.SchedulerId != Id) {
            throw new ArgumentException(
                "A job cannot depend on a job in a different scheduler: neither one can see the "
                + "other's completion.",
                nameof(dependency)
            );
        }
    }

    void ValidateDependencies(ReadOnlySpan<JobHandle> dependencies) {
        foreach (var dependency in dependencies) {
            ValidateDependency(dependency);
        }
    }

    bool TryAddEdge(JobHandle dependency, int successor) {
        if (dependency.IsNull || dependency.SchedulerId != Id) {
            return false;
        }

        var slot = slots[dependency.Index];

        lock (slot.Gate) {
            // Under the gate, so "still live and not finished" cannot become false between the
            // check and the registration — which is the only way an edge could be silently dropped.
            if (slot.Version != dependency.Version || slot.IsComplete) {
                return false;
            }

            slot.Successors.Add(successor);
            return true;
        }
    }

    void MakeReady(int index, JobSlot slot) {
        var batches = slot.BatchCount;
        var priority = slot.Priority;

        for (var batch = 0; batch < batches; batch++) {
            Push(Pack(index, batch), priority);
        }

        // One wake for the whole job. A parallel-for pushes a batch per participating thread times
        // four, and signalling each of them separately is most of what a small parallel-for costs.
        Wake(batches);
    }

    void Push(long item, JobPriority priority) {
        if (priority == JobPriority.Background) {
            // Counted before it is pushed, never after. A taker that sees the count and finds
            // nothing simply looks again; a taker that finds nothing because the count had not
            // caught up goes to sleep next to work that exists.
            Interlocked.Increment(ref backgroundQueued);

            if (ReferenceEquals(workerOf, this) && backgroundDeques[workerIndex].TryPush(item)) {
                return;
            }

            sharedBackground.Enqueue(item);
            return;
        }

        if (ReferenceEquals(workerOf, this) && deques[workerIndex].TryPush(item)) {
            return;
        }

        // Either not a worker of this scheduler, or its deque is full. The shared queue is where
        // both cases land: unbounded, so a burst is slower rather than lost.
        shared.Enqueue(item);
    }

    void Wake(int items) {
        var sleeping = Volatile.Read(ref sleepingWorkers);

        if (sleeping <= 0) {
            return;
        }

        // Never more than there is work for. Waking ten threads for one work item means nine of
        // them pay for the wake-up and find nothing.
        signal.Release(Math.Min(items, sleeping));
    }

    /// <param name="asWorker">
    ///     Whether the caller is a worker scavenging for something to do, as opposed to a thread
    ///     inside <see cref="Complete(JobHandle)" />, <see cref="Dispose" /> or
    ///     <see cref="RentSlot" />.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Only a scavenging worker is held out of the background tier.</b> A thread that is
    ///     waiting for a specific handle has to be able to run <em>any</em> ready item, background
    ///     ones included — otherwise <c>Complete</c> on a background handle could sit there while
    ///     every worker declines the item that would finish it, and <c>Dispose</c> would never
    ///     drain. The reservation is about which work a worker volunteers for, never about which
    ///     work is reachable.
    /// </remarks>
    bool TryExecuteOneWorkItem(bool asWorker = false) {
        if (!TryTakeWorkItem(asWorker, out var item, out var reserved)) {
            return false;
        }

        try {
            Execute(item);
        } finally {
            if (reserved) {
                Interlocked.Decrement(ref backgroundRunning);
            }
        }

        return true;
    }

    bool TryTakeWorkItem(bool asWorker, out long item, out bool reserved) {
        // A thread's fairness debt belongs to the scheduler it ran up against. Keyed on the epoch
        // rather than the reference so a disposed scheduler is not kept alive by every thread that
        // ever helped it, and not on Id because that is reused.
        if (fairnessEpoch != epoch) {
            fairnessEpoch = epoch;
            framesTakenSinceBackground = 0;
        }

        // The fairness share. Held at the stride rather than counting past it, so a long stretch of
        // frame-only work cannot bank credit and then pull in a run of background items the moment
        // one appears: the most that is ever owed is one item.
        if (framesTakenSinceBackground >= FairnessStride
            && TryTakeBackground(asWorker, out item, out reserved)) {
            framesTakenSinceBackground = 0;
            return true;
        }

        if (TryTakeFrame(out item)) {
            if (framesTakenSinceBackground < FairnessStride) {
                framesTakenSinceBackground++;
            }

            reserved = false;
            return true;
        }

        if (TryTakeBackground(asWorker, out item, out reserved)) {
            framesTakenSinceBackground = 0;
            return true;
        }

        reserved = false;
        return false;
    }

    bool TryTakeFrame(out long item) {
        // Own deque first: it is the hottest data and taking from it needs no interlocked operation.
        if (ReferenceEquals(workerOf, this) && deques[workerIndex].TryPop(out item)) {
            return true;
        }

        if (shared.TryDequeue(out item)) {
            return true;
        }

        return TrySteal(deques, out item);
    }

    bool TryTakeBackground(bool asWorker, out long item, out bool reserved) {
        reserved = false;
        item = 0;

        // The cheap gate, and the reason it is here rather than inside the reservation: an idle pool
        // reaches this line on every spin, and an unconditional interlocked write would turn the
        // reservation counter into the contended global the deques exist to avoid.
        if (Volatile.Read(ref backgroundQueued) <= 0) {
            return false;
        }

        if (asWorker) {
            if (Interlocked.Increment(ref backgroundRunning) > backgroundLimit) {
                Interlocked.Decrement(ref backgroundRunning);
                return false;
            }

            reserved = true;
        }

        if ((ReferenceEquals(workerOf, this) && backgroundDeques[workerIndex].TryPop(out item))
            || sharedBackground.TryDequeue(out item)
            || TrySteal(backgroundDeques, out item)) {
            Interlocked.Decrement(ref backgroundQueued);
            return true;
        }

        // Nothing after all — the count is a hint, and another thread got there first.
        if (reserved) {
            Interlocked.Decrement(ref backgroundRunning);
            reserved = false;
        }

        item = 0;
        return false;
    }

    bool TrySteal(WorkStealingDeque[] from, out long item) {
        var count = from.Length;

        if (count == 0) {
            item = 0;
            return false;
        }

        // From a random victim, so every idle thread does not converge on worker 0 and turn its
        // deque into a contended global queue.
        var start = (int)(NextRandom() % (uint)count);

        for (var offset = 0; offset < count; offset++) {
            var victim = start + offset;

            if (victim >= count) {
                victim -= count;
            }

            if (ReferenceEquals(workerOf, this) && victim == workerIndex) {
                continue;
            }

            if (from[victim].TrySteal(out item)) {
                return true;
            }
        }

        item = 0;
        return false;
    }

    void Execute(long item) {
        var index = (int)(item >> 32);
        var batch = (int)item;
        var slot = slots[index];

        // A job whose dependency threw has no inputs. Running it would turn one failure into an
        // unrelated second one somewhere further down, which is the harder bug to read.
        if (slot.Failure is null) {
            var previousSlot = executingSlotPlusOne;
            var previousVersion = executingVersion;
            executingSlotPlusOne = index + 1;
            executingVersion = slot.Version;

            try {
                var start = batch * slot.BatchSize;
                var count = Math.Min(slot.BatchSize, slot.Length - start);

                using (Profiler.Begin(slot.Store!.Key)) {
                    slot.Store.Execute(index, start, count);
                }
            } catch (Exception exception) {
                // A worker thread must not die because a job threw. The exception travels to
                // whoever completes the handle, which is the thread that can act on it.
                Interlocked.CompareExchange(ref slot.Failure, ExceptionDispatchInfo.Capture(exception), null);
            } finally {
                // Restored rather than cleared: a thread that ran out of slots mid-schedule executes
                // somebody else's work item from inside its own, and forgetting what it was doing
                // would blind the guard for the rest of that job.
                executingSlotPlusOne = previousSlot;
                executingVersion = previousVersion;
            }
        }

        OnWorkItemFinished(index, slot);
    }

    void OnWorkItemFinished(int index, JobSlot slot) {
        if (Interlocked.Decrement(ref slot.PendingWork) != 0) {
            return;
        }

        slot.Store?.Release(index);

        // Before completion becomes visible, so a waiter that sees "done" and asks the log why
        // cannot be told "no reason" by arriving between the two.
        if (slot.Failure is not null) {
            failures.Record(index, slot.Version, slot.Failure);
        }

        lock (slot.Gate) {
            // First, before anything that can let another thread observe this job as finished. A
            // successor made ready here can run, complete, and release whoever is waiting on the
            // whole graph — all before this thread gets as far as handing the slot back. The gate
            // is what makes returning it this early safe: whoever rents it next has to take the
            // gate to reset it, so it cannot be reissued until the bookkeeping below is finished.
            ReturnSlot(index);

            foreach (var successorIndex in slot.Successors) {
                var successor = slots[successorIndex];

                if (slot.Failure is not null) {
                    Interlocked.CompareExchange(ref successor.Failure, slot.Failure, null);
                }

                if (Interlocked.Decrement(ref successor.PendingDependencies) == 0) {
                    MakeReady(successorIndex, successor);
                }
            }

            slot.Successors.Clear();

            // Last, so that everything above has happened by the time anyone can see it.
            Volatile.Write(ref slot.IsComplete, true);
        }
    }

    void RunWorker(int ordinal) {
        workerOf = this;
        workerIndex = ordinal;
        stealSeed = (uint)(Environment.CurrentManagedThreadId * 2654435761u) | 1u;
        var spins = 0;

        // Here rather than anywhere else, because every affinity primitive underneath pins the
        // thread that calls it: this is the only line in the process running on the thread being
        // placed. Before the loop, so a worker is not taking work from one core and finishing it on
        // another.
        var placed = TryPlaceWorker(ordinal);

        try {
            RunWorkerLoop(ref spins);
        } finally {
            if (placed) {
                ReleaseWorker();
            }

            workerOf = null;
        }
    }

    bool TryPlaceWorker(int ordinal) {
        if (placement is null) {
            return false;
        }

        bool placed;

        // ⚠ Swallowed on purpose, and this is the one place in the scheduler where that is right.
        // Placement is an optimisation; an unhandled exception on a worker thread takes the process
        // down, and a frame must not be lost to a machine whose affinity mask was not ours to set.
        // It is not silent either — the worker is simply not counted, and WorkersPlaced is short.
        try {
            placed = placement.TryPlace(ordinal, WorkerCount);
        } catch (Exception) {
            return false;
        }

        if (placed) {
            Interlocked.Increment(ref workersPlaced);
        }

        return placed;
    }

    void ReleaseWorker() {
        try {
            placement!.Release();
        } catch (Exception) {
            // As above: a worker on its way out must not take the process with it.
        }

        Interlocked.Decrement(ref workersPlaced);
    }

    void RunWorkerLoop(ref int spins) {
        while (!stopping) {
            if (TryExecuteOneWorkItem(true)) {
                spins = 0;
                continue;
            }

            if (++spins < SpinsBeforeYield) {
                Thread.SpinWait(SpinDuration);
                continue;
            }

            if (spins < SpinsBeforeSleep) {
                Thread.Yield();
                continue;
            }

            Interlocked.Increment(ref sleepingWorkers);

            // Re-check after registering as asleep. A push that happened between the last failed
            // take and the increment saw zero sleepers and did not signal.
            if (HasWork()) {
                Interlocked.Decrement(ref sleepingWorkers);
                spins = 0;
                continue;
            }

            // The timeout is the backstop for the remaining window in the check above: a missed
            // signal costs two milliseconds of latency on one worker, never a stall.
            signal.Wait(WakeIntervalMilliseconds);
            Interlocked.Decrement(ref sleepingWorkers);
            spins = 0;
        }
    }

    /// <remarks>
    ///     Only <see cref="RunWorker" /> asks, which is why the background half is under the
    ///     reservation rule. A reserved worker that counted background work as a reason to stay
    ///     awake would spin at full tilt beside a tier it has already decided not to touch.
    /// </remarks>
    bool HasWork() {
        if (!shared.IsEmpty) {
            return true;
        }

        foreach (var deque in deques) {
            if (deque.ApproximateCount > 0) {
                return true;
            }
        }

        return Volatile.Read(ref backgroundQueued) > 0
            && Volatile.Read(ref backgroundRunning) < backgroundLimit;
    }

    static uint NextRandom() {
        var state = stealSeed;

        if (state == 0) {
            state = (uint)(Environment.CurrentManagedThreadId * 2654435761u) | 1u;
        }

        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        stealSeed = state;
        return state;
    }

    /// <summary>The job behind <see cref="JobHandle.Combine" />: it exists to be depended upon.</summary>
    readonly struct EmptyJob : IJob {
        public void Execute() { }
    }
}
