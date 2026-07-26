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
///         steals.
///     </para>
///     <para>
///         <b>Cost of scheduling.</b> Nothing on the path from
///         <see cref="Schedule{TJob}(in TJob, JobHandle)" /> to the job running allocates. The job
///         struct is copied into a preallocated array; the slot comes from a preallocated ring; the
///         work item is a <see cref="long" /> in a preallocated buffer. A job type is generic all the
///         way down, so there is no boxing and no delegate.
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

    [ThreadStatic] static JobScheduler? workerOf;
    [ThreadStatic] static int workerIndex;
    [ThreadStatic] static uint stealSeed;
    [ThreadStatic] static int executingSlot;

    readonly JobSlot[] slots = new JobSlot[MaxJobsInFlight];
    readonly int[] freeSlots = new int[MaxJobsInFlight];
    readonly Lock freeGate = new();
    int freeCount;

    readonly JobPayloadStore?[] sequentialStores = new JobPayloadStore[JobTypeIds.MaxJobTypes];
    readonly JobPayloadStore?[] parallelStores = new JobPayloadStore[JobTypeIds.MaxJobTypes];
    readonly Lock storeGate = new();

    readonly JobFailureLog failures = new();

    readonly WorkStealingDeque[] deques;
    readonly ConcurrentQueue<long> shared = new();
    readonly SemaphoreSlim signal = new(0);
    readonly Thread[] workers;

    int sleepingWorkers;
    volatile bool stopping;
    bool disposed;

    /// <summary>How many worker threads this scheduler owns, not counting threads that help.</summary>
    public int WorkerCount { get; }

    /// <summary>This scheduler's index in the process-wide table. Carried by every handle it issues.</summary>
    public int Id { get; }

    /// <summary>Whether the calling thread is one of this scheduler's workers.</summary>
    public bool IsWorkerThread => ReferenceEquals(workerOf, this);

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

    /// <summary>Creates a scheduler with one worker per processor beyond the calling thread.</summary>
    public JobScheduler() : this(Math.Max(1, Environment.ProcessorCount - 1)) { }

    /// <summary>Creates a scheduler with a chosen number of workers.</summary>
    /// <param name="workerCount">
    ///     How many worker threads to start. Must be at least one — a scheduler with no workers
    ///     would run everything on whichever thread happened to call
    ///     <see cref="Complete(JobHandle)" />, which is a different design wearing this one's API.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workerCount" /> is less than one.</exception>
    /// <exception cref="InvalidOperationException">There are already <see cref="MaxSchedulers" /> schedulers.</exception>
    public JobScheduler(int workerCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);

        WorkerCount = workerCount;
        Id = Register(this);

        for (var index = 0; index < slots.Length; index++) {
            slots[index] = new();
            freeSlots[index] = index;
        }

        freeCount = MaxJobsInFlight;

        deques = new WorkStealingDeque[workerCount];
        workers = new Thread[workerCount];

        for (var index = 0; index < workerCount; index++) {
            deques[index] = new(DequeCapacity);
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
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle Schedule<TJob>(in TJob job, JobHandle dependsOn = default) where TJob : struct, IJob {
        ValidateDependency(dependsOn);
        var store = SequentialStore<TJob>();
        var index = RentSlot(store, 1, 0, 0, out var version);
        store.Store(index, in job);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Schedules a job to run once, after every one of <paramref name="dependsOn" /> has finished.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="dependsOn">What must finish first. Null handles are ignored.</param>
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle Schedule<TJob>(in TJob job, ReadOnlySpan<JobHandle> dependsOn) where TJob : struct, IJob {
        ValidateDependencies(dependsOn);
        var store = SequentialStore<TJob>();
        var index = RentSlot(store, 1, 0, 0, out var version);
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
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle ScheduleParallel<TJob>(in TJob job, int length, int batchSize = 0, JobHandle dependsOn = default)
        where TJob : struct, IJobParallelFor {
        ValidateDependency(dependsOn);
        var index = RentParallelSlot(in job, length, batchSize, out var version);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Schedules <paramref name="length" /> indices across the workers.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="length">How many indices to run.</param>
    /// <param name="batchSize">How many indices one work item covers, or zero to let the scheduler choose.</param>
    /// <param name="dependsOn">What must finish first. Null handles are ignored.</param>
    /// <returns>A handle for the scheduled job.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The scheduler has been disposed.</exception>
    public JobHandle ScheduleParallel<TJob>(in TJob job, int length, int batchSize, ReadOnlySpan<JobHandle> dependsOn)
        where TJob : struct, IJobParallelFor {
        ValidateDependencies(dependsOn);
        var index = RentParallelSlot(in job, length, batchSize, out var version);
        return Publish(index, version, dependsOn);
    }

    /// <summary>Runs <paramref name="length" /> indices across the workers and waits for them.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="length">How many indices to run.</param>
    /// <param name="batchSize">How many indices one work item covers, or zero to let the scheduler choose.</param>
    /// <exception cref="JobExecutionException">The job threw.</exception>
    public void ParallelFor<TJob>(in TJob job, int length, int batchSize = 0) where TJob : struct, IJobParallelFor =>
        Complete(ScheduleParallel(in job, length, batchSize));

    /// <summary>Waits for a job, executing other ready work while it waits.</summary>
    /// <param name="handle">The job to wait for. The null handle returns immediately.</param>
    /// <exception cref="JobExecutionException">The job, or one it depended on, threw.</exception>
    /// <exception cref="ArgumentException">The handle belongs to a different scheduler.</exception>
    /// <remarks>
    ///     The waiting thread is not parked. It takes ready work — including work unrelated to what
    ///     it is waiting for — until the job it wants is finished. That is what makes the main thread
    ///     a participant rather than an observer, and it is why a frame that schedules its work early
    ///     and completes it late gets the whole machine.
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
        if (executingSlot == handle.Index && Volatile.Read(ref slot.Version) == handle.Version) {
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
        signal.Release(WorkerCount);

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
            var target = Math.Max(1, participants * 4);
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

    int RentParallelSlot<TJob>(in TJob job, int length, int batchSize, out int version)
        where TJob : struct, IJobParallelFor {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var store = ParallelStore<TJob>();
        ComputeBatching(length, batchSize, WorkerCount + 1, out var size, out var count);
        var index = RentSlot(store, count, size, length, out version);
        store.Store(index, in job);
        return index;
    }

    int RentSlot(JobPayloadStore store, int batchCount, int batchSize, int length, out int version) {
        while (true) {
            var index = -1;

            lock (freeGate) {
                if (freeCount > 0) {
                    index = freeSlots[--freeCount];
                }
            }

            if (index >= 0) {
                version = slots[index].Reset(store, batchCount, batchSize, length);
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

        return Release(index, slot, version);
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

        return Release(index, slot, version);
    }

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

        for (var batch = 0; batch < batches; batch++) {
            Push(Pack(index, batch));
        }

        // One wake for the whole job. A parallel-for pushes a batch per participating thread times
        // four, and signalling each of them separately is most of what a small parallel-for costs.
        Wake(batches);
    }

    void Push(long item) {
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

    bool TryExecuteOneWorkItem() {
        if (TryTakeWorkItem(out var item)) {
            Execute(item);
            return true;
        }

        return false;
    }

    bool TryTakeWorkItem(out long item) {
        // Own deque first: it is the hottest data and taking from it needs no interlocked operation.
        if (ReferenceEquals(workerOf, this) && deques[workerIndex].TryPop(out item)) {
            return true;
        }

        if (shared.TryDequeue(out item)) {
            return true;
        }

        return TrySteal(out item);
    }

    bool TrySteal(out long item) {
        var count = deques.Length;

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

            if (deques[victim].TrySteal(out item)) {
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
            var previous = executingSlot;
            executingSlot = index;

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
                executingSlot = previous;
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
        executingSlot = -1;

        var spins = 0;

        while (!stopping) {
            if (TryExecuteOneWorkItem()) {
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

        workerOf = null;
    }

    bool HasWork() {
        if (!shared.IsEmpty) {
            return true;
        }

        foreach (var deque in deques) {
            if (deque.ApproximateCount > 0) {
                return true;
            }
        }

        return false;
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
