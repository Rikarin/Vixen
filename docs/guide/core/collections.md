---
title: Collections the BCL does not have
slug: core/collections
kind: guide
area: Core
summary: The eight structures in Vixen.Core.Collections, the one frame-loop property each of them buys that List and Dictionary cannot, and which of the two identity types to reach for.
api: [T:Vixen.Core.Collections.Handle`1, T:Vixen.Core.Collections.HandlePool`1, T:Vixen.Core.Collections.HandlePool`1.Enumerator, T:Vixen.Core.Collections.FreeList`1, T:Vixen.Core.Collections.FreeList`1.Enumerator, T:Vixen.Core.Collections.SparseSet`1, T:Vixen.Core.Collections.SparseSet`1.Enumerator, T:Vixen.Core.Collections.BitSet, T:Vixen.Core.Collections.BitSet.Enumerator, T:Vixen.Core.Collections.ChunkedArray`1, T:Vixen.Core.Collections.ChunkedArray`1.Enumerator, T:Vixen.Core.Collections.RingBuffer`1, T:Vixen.Core.Collections.RingBuffer`1.Enumerator, T:Vixen.Core.Collections.IndexedPriorityQueue`1, T:Vixen.Core.Collections.SmallList`2, T:Vixen.Core.Collections.IInlineBuffer`1, T:Vixen.Core.Collections.Buffer4`1, T:Vixen.Core.Collections.Buffer8`1, T:Vixen.Core.Collections.Buffer16`1, T:Vixen.Core.Collections.Buffer32`1]
tags: [core, collections, performance, handles, allocation]
since: 0.1
status: stable
related: [core/job-priorities, ecs/queries, ecs/structural-changes]
---

## What it is

`Vixen.Core.Collections` is not a replacement for `List<T>` and `Dictionary<K,V>`. Those are
excellent, and most engine code should keep using them. Every type here exists because one specific
frame-loop shape needs a property the BCL does not offer, and the useful way to read the library is
one property at a time:

| Type | The property the BCL cannot give |
|---|---|
| `Handle<T>`, `HandlePool<T>` | A reference that **detects** use-after-free instead of following it. |
| `FreeList<T>` | Index recycling where identity never leaves the structure. Catches double release. |
| `SparseSet<T>` | O(1) keyed lookup **and** dense contiguous iteration of the values. |
| `BitSet` | Sixty-four flags tested per instruction, and set algebra over them. |
| `SmallList<T, TBuffer>` | The first N elements live inside the struct; nothing is allocated for the common case. |
| `ChunkedArray<T>` | A `ref` into it stays valid when it grows. |
| `RingBuffer<T>` | Bounded history that overwrites, and says how much it dropped. |
| `IndexedPriorityQueue<TPriority>` | Reaching an entry that is already queued, to change its priority. |

## What it is for

### Identity: which of the two

`FreeList<T>` and `HandlePool<T>` both recycle slots, and choosing between them is a question about
*who owns the identity* rather than about performance.

Use a **free list** where the index never leaves the structure that holds it — the nodes of a tree,
the entries of a graph, a scheduler's task table. A stale index is then impossible by construction,
which is why there is no generation counter to pay for. An index kept past its release will read
whatever landed there next; what *is* caught is releasing the same index twice, because that would
queue one slot for reuse twice and hand it to two callers:

```csharp compile
using Vixen.Core.Collections;

public static class Nodes {
    public static void Recycle() {
        var nodes = new FreeList<string>();

        var first = nodes.Add("root");
        nodes.Release(first);

        // The slot comes back, so indices stay dense.
        var second = nodes.Add("replacement");

        // And releasing the same index twice throws rather than corrupting the free list.
        // nodes.Release(first);  --> InvalidOperationException
        _ = nodes.IsLive(second);
    }
}
```

Use a **handle pool** wherever the reference is handed to somebody else, where staleness is not only
possible but expected and needs detecting. `Handle<T>` is eight blittable bytes — a slot index and
the generation that slot was on when the handle was taken — so it is cheap to copy into a command
list, and a handle taken before a `Remove` fails a comparison rather than addressing whatever now
lives in the slot:

```csharp compile
using Vixen.Core.Collections;

public sealed class Buffers {
    readonly HandlePool<string> pool = new();

    public Handle<string> Create(string name) => pool.Add(name);

    public bool StillThere(Handle<string> handle) => pool.Contains(handle);

    public string? Read(Handle<string> handle) => pool.TryGet(handle, out var name) ? name : null;

    public void Destroy(Handle<string> handle) => pool.Remove(handle);
}
```

⚠ **A slot is live when its generation is odd.** `Remove` increments, so a freed slot's generation is
even and matches no handle at all — including the zeroed one, whose generation is 0 and which
`Handle<T>.Null` and `IsNull` are about. That is why a forged or stale handle is rejected rather than
believed, and it is also why the check costs one comparison rather than a table lookup.

**This is why the RHI exposes no reference types for GPU resources.** A buffer is
`Handle<GpuBuffer>`; destroying it bumps the slot, and every handle taken beforehand reports a
use-after-free where a raw pointer would have crashed in native code or, worse, quietly addressed
the next resource to take the slot.

## Using it

### Iterating what is there, not what could be there

`SparseSet<T>` is for keys that are dense, small and integer — entity ids, component indices. A
sparse array maps a key to a position in a dense array, and the dense arrays hold the keys and values
packed together, so iteration touches no cache line it does not need:

```csharp compile
using Vixen.Core.Collections;

public static class Scores {
    public static int Total() {
        var scores = new SparseSet<int>();

        scores.Set(7, 300);
        scores.Set(9000, 12);

        var total = 0;

        foreach (var value in scores.Values) {
            total += value;
        }

        return total;
    }
}
```

Two properties of that come as a pair and both matter. **Memory is proportional to the largest key
ever added**, not to the number of entries, which is the right trade for entity ids and the wrong one
for anything sparse and unbounded — a dictionary belongs there. And **removal reorders**: the last
entry is swapped into the hole, so anything that depends on iteration order has to sort, and a dense
index held across a removal is the wrong index.

`BitSet` answers the other half of that question — *which* of ten thousand things changed — over
64-bit words. A `bool[]` would be eight times the memory and would test one flag per instruction.
`Words` is exposed for bulk work and for uploading a mask to the GPU, and the set algebra
(`UnionWith`, `IntersectWith`, `ExceptWith`, `Contains`, `Intersects`) is what an archetype mask is
matched with.

### Growth without invalidation

`ChunkedArray<T>` is the one a `List<T>` cannot substitute for. Growing a list reallocates, and every
outstanding `ref` into it points at the abandoned array. Anything that hands out references into its
own storage *and* grows — an ECS chunk store, a node pool, an interned table — needs chunks.

The cost is stated plainly: the elements are not contiguous, so there is no whole-collection `Span`.
Iterate with `GetChunk`, which is the granularity a vectorised sweep wants anyway:

```csharp compile
using Vixen.Core.Collections;

public static class Sweep {
    public static long Sum(ChunkedArray<long> values) {
        ArgumentNullException.ThrowIfNull(values);

        var total = 0L;

        for (var chunk = 0; chunk < values.ChunkCount; chunk++) {
            foreach (var value in values.GetChunk(chunk)) {
                total += value;
            }
        }

        return total;
    }
}
```

### Bounded history, and a queue you can reach back into

`RingBuffer<T>` overwrites its oldest entry when full, which is the point rather than a limitation: a
log that grows is a memory leak with a long fuse, and one that keeps the last ten thousand lines is a
diagnostic tool with a fixed cost. `OverwrittenCount` is how a reader learns it missed something, and
`TryEnqueue` is there for the callers that would rather be told than lose the oldest entry. It is not
thread-safe — the profiler writes one ring per thread and reads them after a barrier.

`IndexedPriorityQueue<TPriority>` exists because the BCL's `PriorityQueue` cannot reach an element it
has already queued. The usual workaround — enqueue a second copy at the new priority and skip the
stale one on the way out — unbounds the queue and makes `Count` a lie. `SetPriority` and
`TryDecreasePriority` reach the entry instead, which is what a job graph whose successors become
ready as dependencies complete actually needs. Ids map to heap positions through a flat array, so
they should be dense and small: they are indices into something the caller already has, not arbitrary
keys.

## Examples

### Not allocating for the common case

`SmallList<T, TBuffer>` keeps its first `TBuffer.Capacity` elements inside itself and reaches for the
heap only beyond that. It is for the shape of data the engine is full of — descriptor slots, the
children of a UI node, the bones affecting a vertex — almost always small, occasionally not. The
inline capacity is chosen by picking a buffer: `Buffer4<T>`, `Buffer8<T>`, `Buffer16<T>` or
`Buffer32<T>`, each an `IInlineBuffer<T>`.

```csharp compile
using Vixen.Core.Collections;

public static class Bones {
    public static int Influences() {
        var list = new SmallList<int, Buffer8<int>>();

        list.Add(3);
        list.Add(11);

        // Nothing was allocated: eight fit inside the struct.
        var inline = list.HasSpilled;

        list.Dispose();
        return inline ? 0 : list.Count;
    }
}
```

⚠ **It is a mutable struct, with everything that implies.** Copying one copies the inline elements
and *shares* the spill buffer, so the two diverge in a way that is hard to see — pass it by `ref`, or
hand out `Span`. `Dispose` returns a spilled array to the pool; forgetting it forfeits the array and
corrupts nothing.

### Deliberately not here

**`RobinHoodDictionary<K,V>`**, which doc 03 lists. The BCL's `Dictionary<K,V>` is already a
well-tuned open-addressing map, and there is no benchmark yet for a replacement to beat. Writing a
second hash table before there is a measurement is the unmeasured optimisation doc 00 rules out.

**A `FixedBitSet<N>`.** The inline-buffer machinery is here and building one on it is small, but the
sensible capacity is whatever the ECS's component-count budget turns out to be, and guessing produces
a type nobody can use without casting.

## See also

- [Job priorities](core/job-priorities) — the scheduler `IndexedPriorityQueue` exists for, and the
  reason a successor becoming ready has to reach an entry that is already queued.
- [Entity queries](ecs/queries) — the dense iteration `SparseSet` and `BitSet` are the shape of, one
  layer up and over archetypes.
- [Structural changes](ecs/structural-changes) — where a `ref` into growable storage staying valid
  stops being a nicety and becomes the constraint `ChunkedArray` was written for.
