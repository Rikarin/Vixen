# Vixen.Ui.Reactive

The signal graph the UI framework is built on. Writing pushes invalidation; reading pulls evaluation.
Nothing is recomputed because something *might* have changed, and nothing runs at the moment a value
is written — effects queue, and the frame decides when.

Per [ADR-007](../../docs/plan/01-technology-decisions.md#adr-007--vixen-implements-its-own-signal-graph-with-signalsdotnet-as-the-api-reference)
this is Vixen's own implementation with Angular's semantics and SignalsDotnet's API as the reference.
The reason for not taking the dependency is one line long and is the whole of the design: a game
engine's UI must flush effects at a *precise* point in the frame, on a known thread, with a hard
budget — and SignalsDotnet's effects are driven by Rx schedulers.

## What is here

| | |
|---|---|
| `Signal<T>` | A writable value. Writing an equal value does nothing at all. |
| `IReadOnlySignal<T>` | The read-only view, plus `Peek()` for reading without subscribing. |
| `Computed<T>` | A derived value: lazy, memoised, dependencies discovered by running it. |
| `Effect` | The only thing that *does* anything. Queued on write, run on `Flush`. |
| `EffectScheduler` | The queue, the per-frame budget, the runaway detector, and `Post` for off-thread results. |
| `CollectionSignal<T>` | A list that reports *what* changed, for the keyed `@for` reconciler. |
| `SignalDictionary<TKey, TValue>` | A map written in place, so a value arriving every frame costs nothing. |
| `LinkedSignal<TSource, T>` | Writable, until the thing it is derived from moves. |
| `AsyncComputed<TRequest, T>` | Asynchronous derivation with loading / value / error as one state. |
| `ReactiveGraph` | `Untracked`, `Batch`, and the owning-thread check. |

## The three properties worth knowing

**Equality stops propagation.** A computed that re-runs and produces an equal value does not bump its
version, so nothing downstream re-runs — and that holds all the way out to the effect, which polls its
dependencies on the way in rather than trusting the wake-up. A toggle that flips a number between 2
and 4 does not repaint a label showing whether it is even.

**Liveness decides who gets pushed to.** A producer only notifies consumers that something is
*watching*, transitively. A computed nobody reads registers no edge back from its dependencies at all
— it is verified by polling on the next read instead. Without this, every computed ever created is
retained forever by whatever signal it happened to read once, which is the classic way a reactive
graph turns into a memory leak. `GraphTests` asserts both directions of it.

**A settled graph allocates nothing.** Dependency edges live in pooled arrays, the effect queue
reuses its storage, and the per-frame path takes no closures. Two tests measure this rather than
claim it: one asserts a thousand write-and-flush cycles allocate exactly zero bytes, and one asserts
that building and tearing down a subscription costs the same as building and tearing down an
identical thing that subscribes to nothing.

## The two collections, and the one node each of them is

Both `CollectionSignal<T>` and `SignalDictionary<TKey, TValue>` are themselves nodes, and **the whole
collection is the dependency**. Reading a count, an item, a key, or enumerating records one edge — so
a binding that read `counters["fps"]` is woken when `counters["draws"]` is written. That
over-approximates and cannot under-approximate: the cost is a re-run, never a stale answer, and a
re-run producing the same string stops at the equality check one level up. Neither is per-key or
per-index, and a design that needs that granularity wants a signal per key held by whatever owns the
keys.

⚠ **They exist because the two obvious alternatives each fail in one direction.** A `Signal<List<T>>`
is silently dead — the comparer sees the same instance, so a write that appended a row propagates
nothing, which is the commonest way a hand-built model draws its first answer for ever. A
`Signal<ImmutableDictionary<K, V>>` is *correct* and rebuilds a balanced tree's spine every time one
number moves. `RemoteInspectorClient`'s counters were the second of those, paid per counter per frame
because `Poll` runs from the panel's tick; they are a `SignalDictionary` now, and
`SignalDictionaryTests` measures the in-place write at exactly zero bytes with the immutable shape
beside it as the control.

⚠ **The change log is the list's and is deliberately not the map's.** A list's log earns its per-write
cost because the keyed `@for` reconciler reads it and turns "inserted at 3" into one appended row.
Nothing reads a map's: `@for` cannot bind to a dictionary at all — a dictionary's order is its hashing,
and a pane of live numbers that reordered itself as values arrived would be unreadable — so a map is
projected to a sorted sequence first and *that* is what gets reconciled. A log here would be a ring
buffer written on every update and read by nobody, which is the cost the type was built to remove
under a different name.

⚠ **`SignalDictionary`, not `DictionarySignal`, and it is the analyzer's call.** CA1710 makes a type
implementing `IReadOnlyDictionary<TKey, TValue>` end in `Dictionary` or `Collection`;
`CollectionSignal<T>` escapes the same rule only because `IReadOnlyList<T>` is not on its list.
Suppressing it was the other option and the suffix is worth more than the symmetry — it is what tells
a caller that `foreach` over the thing yields `KeyValuePair`.

## Where this differs from doc 09, and why

- **Edge storage is pooled arrays, not slices of a shared `ChunkedArray<Edge>`.** A slice has to be
  one contiguous `Span`; chunks are not contiguous with each other. An arena therefore needs either a
  cap on edges per node at the chunk size, or a second allocation path for the nodes that exceed it.
  Pooling whole arrays gives the property that was wanted — nothing allocated once the graph is warm,
  storage reused across nodes that come and go — with no cap and no special case.

- **The thread check is a runtime opt-in, not a `DEBUG` assertion.** It costs one comparison against a
  static field that is usually null, and a plug-in touching the document model from a worker thread is
  exactly the bug worth reporting from a shipping editor. It is off until `ReactiveGraph.OwningThread`
  is set, so a test host — or an editor with more than one independent graph — is not forced into a
  single thread by a library default.

- **`Batch` is about flush ordering, not about coalescing.** Doc 09 lists it as "coalesce writes;
  effects run once at the end", which is what `batch` is for in every other signal library. Here it
  mostly is not needed: effects are queued and drained at a defined point in the frame, and computeds
  are lazy, so ten writes between two frames already cost one effect run and one recomputation with
  nobody asking. What is left for a batch to do is real but narrower — order an explicit `Flush()`
  after the group instead of in the middle of it — and that is what it does.

- **The zero-allocation gate is a test, not a benchmark.** Doc 09 asks for BenchmarkDotNet's
  `MemoryDiagnoser` asserting 0 bytes. `GC.GetAllocatedBytesForCurrentThread` in an xunit test asserts
  the same thing and *fails the build* on every push, which a benchmark run does not.

- **`AsyncComputed` is split into a tracked request and an untracked load.** An `async` computation
  cannot be tracked past its first `await` — the ambient consumer is thread-local and the continuation
  is on another thread — so an async function reading signals after awaiting would silently record
  half its dependencies. Splitting it makes the compiler enforce what the graph can observe. Results
  come back through `EffectScheduler.Post`, so they land on the owning thread at a defined point.

## Deliberately not here

**Cross-graph batching of the notification walk.** A write walks its live consumers marking them
dirty, and a batch of a thousand writes to the same signal does that walk a thousand times. It is a
tight loop over an array and no measurement has asked for better; when one does, the fix is a
generation stamp on the walk rather than a change to the model.

**A `Signal<T>` that is safe to write from any thread.** Deliberate. Single-threaded is what lets the
edge lists be plain arrays with no interlocked anything, and `AsyncComputed` plus
`EffectScheduler.Post` are the supported way across.

## Where the frame drains it

`UiDocument.Update` calls `Effects.Flush()` at the top of a pass, under the guard that refuses a
nested one — so a frame drains the queue exactly once, before it walks the tree, and a control that
re-enters `Update` from a `LayoutFinished` handler drains nothing.

⚠ **This used to be the host's job and was a job hosts did not know they had.** The gap was invisible
because every test in this repository flushes by hand, and of the real hosts only `EditorShell` did:
a game built on the `vixen-app` template drew an interface whose bindings never ran, so a signal
written from a click queued an effect nothing dequeued and the element kept the value it was built
with for the life of the process. `Vixen.Ui.Tests/EffectPassTests` is the gate, and it asserts on
`Update` rather than on `Flush` for exactly that reason — the subject is the frame, not the scheduler.

A host wanting the drain at some other point in its frame still calls `Flush()` itself; `EditorShell`
does, after its dialogs and background tasks are pumped, and the pass then finds an empty queue. That
costs nothing measurable: an empty drain allocates zero bytes, which the same file asserts over
sixty-four settled frames.

Licensed under Apache-2.0.
