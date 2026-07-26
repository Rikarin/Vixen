# Vixen.Core.Collections

Collections the BCL does not have, for the places where its ones cost too much. Not a replacement
for `List<T>` or `Dictionary<K,V>` — those are excellent, and most engine code should keep using
them. Each type here exists because a specific frame-loop shape needs a property the BCL does not
offer.

## What is here

| | The property the BCL cannot give |
|---|---|
| `Handle<T>`, `HandlePool<T>` | A reference that **detects** use-after-free instead of following it. |
| `FreeList<T>` | Index recycling where identity never leaves the structure. Catches double release. |
| `SparseSet<T>` | O(1) keyed lookup **and** dense contiguous iteration of the values. |
| `BitSet` | Sixty-four flags tested per instruction, and set algebra for archetype masks. |
| `SmallList<T, TBuffer>` | The first N elements live inside the struct; nothing is allocated for the common case. |
| `ChunkedArray<T>` | A `ref` into it stays valid when it grows. |
| `RingBuffer<T>` | Bounded history that overwrites, and says how much it dropped. |
| `IndexedPriorityQueue<TPriority>` | Reaching an entry that is already queued, to change its priority. |

## The two that carry the most weight

**`Handle<T>` is the RHI's public currency.** The graphics layer exposes no reference types for GPU
resources. A buffer is `Handle<GpuBuffer>` — eight blittable bytes of slot index plus generation.
Destroying the buffer bumps the slot's generation, so every handle taken beforehand fails a
comparison rather than addressing whatever now lives there. Generations are odd while live and even
while free, which is also why a forged handle carrying a freed generation is rejected rather than
believed.

**`ChunkedArray<T>` is the one a `List<T>` cannot substitute for.** Growing a list reallocates, and
every outstanding `ref` into it points at the abandoned array. Anything that hands out references
into its own storage and also grows needs chunks. The cost is that there is no whole-collection
`Span` — iterate with `GetChunk`, which is the granularity a vectorised sweep wants anyway.

## Deliberately not here

**`RobinHoodDictionary<K,V>`**, which [doc 03](../../docs/plan/03-core-foundation.md) lists. The BCL's
`Dictionary<K,V>` is already a well-tuned open-addressing map, its consumers here (asset URL →
content, style key → computed style) are Phase 3 and Phase 4 concerns, and there is no benchmark yet
for a replacement to beat. Writing a second hash table before there is a measurement is the kind of
unmeasured optimisation [doc 00](../../docs/plan/00-vision-and-principles.md) rules out for
`AggressiveInlining`, and the same reasoning applies. It lands when a profile asks for it.

**A `FixedBitSet<N>`.** The inline-buffer machinery is here (`IInlineBuffer<T>`, `Buffer4/8/16/32`)
and building one on it is small, but the sensible capacity is whatever the ECS's component-count
budget turns out to be, and that is not decided yet. Guessing produces a type nobody can use without
casting.

Licensed under Apache-2.0.
