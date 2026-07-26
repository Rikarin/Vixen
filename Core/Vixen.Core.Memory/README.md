# Vixen.Core.Memory

Memory the garbage collector does not manage, for the three cases where it should not: storage the
GPU reads directly, storage whose lifetime is exactly one frame, and storage that lives in a device
heap the engine suballocates itself.

## What is here

| | |
|---|---|
| `NativeArray<T>` | Aligned unmanaged storage. Does not move, so a pointer into it needs no pinning; invisible to the collector, so its size costs nothing at collection time. |
| `ArenaAllocator` | A bump allocator. Allocation is an add and a compare; release is all-at-once. |
| `FrameArena` | The two process-wide arenas — per-frame and scoped scratch — one pair per thread. |
| `BuddyAllocator` | Offset suballocation within a region, with O(log n) merging and no search. |

## What each one trades away

**`NativeArray<T>` is not collected.** Forgetting to dispose one leaks memory the profiler will not
attribute to anything. Under a debug build every allocation registers with `Vixen.Core.LeakTracker`,
so the leak arrives as a stack trace; in release that compiles away and the discipline is yours.
Copying the struct copies the pointer, so exactly one owner disposes and everything else takes an
`AsSpan()`. Bounds checks on the indexer exist under `DEBUG` only — a check on every ECS chunk access
is the cost the type exists to avoid — so use the span where the check is wanted in release too.

**An arena's pointers all dangle at `Reset()`.** That is the contract rather than a caveat: a bump
allocator is this cheap precisely because it does not track what is still in use. Scope it tightly
and the property is a guarantee; hold a pointer across a frame boundary and nothing will tell you.

**A buddy allocator wastes up to half of every allocation.** Every request rounds to a power of two,
so a 33 KiB resource occupies 64 KiB. In exchange, finding a free block's partner is one XOR instead
of a search, which is what keeps a device heap from fragmenting into uselessness over a long session.
The backend sends allocations above a threshold straight to the driver instead.

## Still to come

**`GpuUploadRing`**, which [doc 03](../../docs/plan/03-core-foundation.md) lists here. It needs
persistently mapped memory and frame fences — both RHI concepts — so it lands with
`Vixen.Graphics` in Phase 1 rather than being built here against an invented abstraction.

The Vulkan-specific half of the device suballocator (`VK_EXT_memory_budget` awareness, dedicated
allocations above a threshold) belongs to the backend for the same reason. `BuddyAllocator` is the
part that is pure arithmetic, which is why it is here and can be tested exhaustively without a GPU.

Licensed under Apache-2.0.
