# Vixen.Benchmarks.Jobs

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Jobs -- --filter '*'
```

`Vixen.Core.Threading` exists instead of `Task` and `Parallel.For`. That is a claim, and this is where
it gets checked.

## What this found

The first run said a hundred scheduled jobs cost **1.1 µs each** — worse per job than scheduling a
single one and waiting for it, which is backwards. The cause was one semaphore release per work item:
a burst woke the whole pool, the workers found nothing left, re-parked after about a microsecond of
spinning, and were woken again by the next push. Signalling once per *job* rather than once per
*batch*, and spinning for tens of microseconds rather than one, cut a single round-trip from 739 ns
to 277 ns and a hundred-job burst from 112 µs to 77 µs.

The second thing it found was that the obvious next step is wrong. Nine idle workers polling — the
shared queue's head, then both ends of nine deques — measurably slow down the thread doing real work:
the same hundred jobs cost 35 µs with one worker and 84 µs with nine. Exponential backoff on the idle
poll fixes that number and makes things worse overall: a single job round-trip got **2.4× faster**,
because the workers stopped noticing it and the waiting thread ran it inline, and a burst of a hundred
got **20% slower**, because the workers stopped noticing those too. A frame is a burst. The backoff is
not in the code, and the flat spin has a comment saying why.

## Measured on an Apple M1 Max (10 cores), .NET 10, `--job short`

### One job, there and back

| Benchmark | Mean | Allocated |
|---|---|---|
| Called directly, no scheduler | 0.99 ns | 0 B |
| `Schedule` + `Complete` | **277 ns** | **0 B** |
| `Task.Run` + `Wait` | 1 743 ns | 160 B |
| Schedule 100, then complete them | 76.8 µs (768 ns/job) | 0 B |
| A chain of 100, each waiting for the last | 74.9 µs (749 ns/step) | 0 B |

**6.3× faster than `Task.Run` for one round-trip, and it allocates nothing.** The chain is the
interesting one: 749 ns per step is the cost of a dependency being satisfied and the successor being
picked up by another thread, and it is the number that says how deep a frame graph can usefully be.

### `ParallelFor` against the alternatives

| Length | Serial | `Parallel.For` | `JobParallelFor` |
|---|---|---|---|
| 1 024 | **2.8 µs** | 13.7 µs | 18.1 µs |
| 65 536 | 190.6 µs | 112.3 µs | **52.1 µs** |
| 1 048 576 | 3 391.9 µs | 1 601.4 µs | **616.4 µs** |

**2.2–2.6× faster than `Parallel.For` where parallelism pays, and zero bytes against its ~3.5 KB.**

**And 6.4× slower than a serial loop at 1 024 elements**, which is the row worth keeping. Below a few
thousand elements of cheap work, dispatch costs more than the loop does — `Parallel.For` loses there
too, by 4.9×, so this is a property of crossing threads rather than of this implementation. Anything
tempted to schedule a parallel-for over a few hundred items per frame should measure it first.

Numbers from one machine are a sanity check, not a gate. Per
[doc 12](../../docs/plan/12-build-ci-and-testing.md) the timing gate runs nightly rather than per-PR,
because shared CI runners are too noisy to fail a build on — and a scheduler benchmark is the most
runner-sensitive thing in the repository.
