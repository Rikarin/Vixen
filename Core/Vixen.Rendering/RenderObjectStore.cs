// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Memory;

namespace Vixen.Rendering;

/// <summary>
///     Every renderable in the scene, as one flat array plus the per-feature arrays beside it.
/// </summary>
/// <remarks>
///     <para>
///         Ids are dense and reused. Removal marks a slot dead and pushes it onto a free list rather
///         than compacting, because the id is what every feature's parallel array is indexed by —
///         compacting would move objects and invalidate every registered array at once, which is the
///         opposite of what the <see cref="RenderDataHolder" /> arrangement is for.
///     </para>
///     <para>
///         The cost of that is holes: a scene that streamed a thousand objects out leaves a thousand
///         dead slots the culling loop still walks. It is a real cost and a cheap one — a dead slot
///         is one predictable branch on a value already in cache — and it buys stable ids, which
///         everything else here depends on.
///     </para>
/// </remarks>
public sealed class RenderObjectStore : IDisposable {
    readonly Stack<int> free = new();
    NativeArray<RenderObject> objects;
    int count;
    bool disposed;

    /// <summary>Per-feature arrays, indexed by the same ids as this store.</summary>
    public RenderDataHolder Data { get; } = new();

    /// <summary>One past the highest id ever handed out — the length every array is grown to.</summary>
    /// <remarks>
    ///     Not the number of live objects: a scene with one object at id 900 has a count of 901 here,
    ///     because that is how far a loop over the arrays has to go.
    /// </remarks>
    public int Count => count;

    /// <summary>How many slots hold a live object.</summary>
    public int LiveCount => count - free.Count;

    /// <summary>The objects, live and dead, as one span.</summary>
    public Span<RenderObject> All => objects.AsSpan(0, count);

    /// <summary>The object with this id.</summary>
    public ref RenderObject this[RenderObjectId id] {
        get {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(id.Index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id.Index, count);
            return ref objects[id.Index];
        }
    }

    /// <summary>Adds an object, reusing a dead slot where there is one.</summary>
    public RenderObjectId Add(in RenderObject value) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (free.Count > 0) {
            var reused = new RenderObjectId(free.Pop());

            // Cleared on reuse rather than on removal: the frame that removed it may still be in
            // flight, and a slot nothing reads does not need to be tidy.
            Data.ClearSlot(reused);
            objects[reused.Index] = value;
            objects[reused.Index].IsAlive = true;
            return reused;
        }

        EnsureCapacity(count + 1);

        var id = new RenderObjectId(count++);
        objects[id.Index] = value;
        objects[id.Index].IsAlive = true;
        return id;
    }

    /// <summary>Removes an object, freeing its slot for reuse.</summary>
    /// <remarks>Removing an already-dead id does nothing, so a double remove cannot corrupt the free list.</remarks>
    public void Remove(RenderObjectId id) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (id.Index < 0 || id.Index >= count || !objects[id.Index].IsAlive) {
            return;
        }

        objects[id.Index].IsAlive = false;
        objects[id.Index].Stages = RenderStageMask.None;
        free.Push(id.Index);
    }

    /// <summary>Drops every object, keeping the memory for the next scene.</summary>
    public void Clear() {
        ObjectDisposedException.ThrowIf(disposed, this);

        objects.AsSpan(0, count).Clear();
        free.Clear();
        count = 0;
    }

    void EnsureCapacity(int required) {
        if (required <= objects.Length) {
            Data.EnsureCapacity(required);
            return;
        }

        var grown = Math.Max(required, Math.Max(objects.Length * 2, 64));
        var replacement = NativeArray<RenderObject>.Zeroed(grown, name: "RenderObjects");

        if (count > 0) {
            objects.AsSpan(0, count).CopyTo(replacement.AsSpan());
        }

        objects.Dispose();
        objects = replacement;

        // In lockstep, and through the same call: a feature array shorter than the object array is
        // an out-of-range read in a job, which is the least debuggable shape this code has.
        Data.EnsureCapacity(grown);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Data.Dispose();
        objects.Dispose();
        objects = default;
        count = 0;
        free.Clear();
    }
}
