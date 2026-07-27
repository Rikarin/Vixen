// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Memory;

namespace Vixen.Rendering;

/// <summary>A typed handle for one array a feature registered.</summary>
/// <typeparam name="T">The element type. Unmanaged, because the arrays are native memory.</typeparam>
public readonly record struct RenderDataKey<T>(int Slot) where T : unmanaged;

/// <summary>
///     The per-object data arrays features declare, grown in lockstep and addressed by
///     <see cref="RenderObjectId" />.
/// </summary>
/// <remarks>
///     <para>
///         <strong>This is the extensibility mechanism, and it is worth understanding before adding
///         anything to the renderer.</strong> A feature that needs per-object state does not extend a
///         type and does not add a field to <see cref="RenderObject" />: it registers an array of its
///         own, which this holder keeps the same length as every other. Adding skinning therefore
///         does not touch the mesh feature, and an object that is not skinned costs one unused slot
///         rather than a branch on every path.
///     </para>
///     <para>
///         <strong>Structure of arrays, not array of structures.</strong> Each registered array is
///         contiguous and holds one field for every object, so a job that reads only transforms
///         touches only transforms. The alternative — one fat struct per object — makes every pass
///         pull in every feature's bytes, and the passes that hurt are the ones that read one small
///         thing for every object in the scene.
///     </para>
///     <para>
///         Native memory rather than <c>T[]</c>: these are read by jobs, they are reallocated as the
///         object count grows, and they are the arrays that would otherwise dominate a frame's GC
///         pressure. The <c>unmanaged</c> constraint follows from that and is the reason a feature
///         stores a handle or an index here rather than a reference — see
///         <see cref="RootRenderFeature" /> for where references live.
///     </para>
/// </remarks>
public sealed class RenderDataHolder : IDisposable {
    readonly List<IArray> arrays = [];
    int capacity;
    bool disposed;

    /// <summary>How many objects every registered array currently has room for.</summary>
    public int Capacity => capacity;

    /// <summary>How many arrays have been registered.</summary>
    public int ArrayCount => arrays.Count;

    /// <summary>Registers an array of <typeparamref name="T" />, one element per object.</summary>
    /// <remarks>
    ///     Called once, when a feature is attached — not per frame. The returned key is how the
    ///     feature reaches its data afterwards, and it is typed so that reading a transform array as
    ///     bone matrices does not compile.
    /// </remarks>
    public RenderDataKey<T> Register<T>() where T : unmanaged {
        ObjectDisposedException.ThrowIf(disposed, this);

        var array = new TypedArray<T>();
        array.Grow(capacity);
        arrays.Add(array);

        return new(arrays.Count - 1);
    }

    /// <summary>The array a key names, as a span over live storage.</summary>
    /// <remarks>
    ///     A span rather than the <see cref="NativeArray{T}" />, because the storage moves when the
    ///     holder grows: handing out the array itself would let a feature keep a pointer across a
    ///     resize. Taking the span per frame costs an indirection and removes a class of bug that
    ///     only appears once a scene gets big enough to reallocate.
    /// </remarks>
    public Span<T> Data<T>(RenderDataKey<T> key) where T : unmanaged {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ((TypedArray<T>)arrays[key.Slot]).Span;
    }

    /// <summary>Makes room for <paramref name="objectCount" /> objects in every array.</summary>
    /// <remarks>
    ///     Growth is geometric and never shrinks. A renderer's object count oscillates as things
    ///     stream in and out, and returning memory at the low-water mark means paying for the
    ///     reallocation again on the next spike — for arrays whose total size is measured in
    ///     megabytes, not hundreds.
    /// </remarks>
    public void EnsureCapacity(int objectCount) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (objectCount <= capacity) {
            return;
        }

        var grown = Math.Max(objectCount, Math.Max(capacity * 2, 64));

        foreach (var array in arrays) {
            array.Grow(grown);
        }

        capacity = grown;
    }

    /// <summary>Zeroes every array's slot for one object, so a reused id starts clean.</summary>
    /// <remarks>
    ///     Cleared on <em>reuse</em> rather than on removal, which is the same work at a better
    ///     time: removal happens while the frame that referenced the object may still be in flight,
    ///     and a slot nothing reads does not need to be tidy.
    /// </remarks>
    public void ClearSlot(RenderObjectId id) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (id.Index < 0 || id.Index >= capacity) {
            return;
        }

        foreach (var array in arrays) {
            array.ClearAt(id.Index);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var array in arrays) {
            array.Dispose();
        }

        arrays.Clear();
        capacity = 0;
    }

    interface IArray : IDisposable {
        void Grow(int length);

        void ClearAt(int index);
    }

    sealed class TypedArray<T> : IArray where T : unmanaged {
        NativeArray<T> storage;

        public Span<T> Span => storage.AsSpan();

        public void Grow(int length) {
            if (length <= storage.Length) {
                return;
            }

            // Zeroed, so a feature reading a slot it has not filled sees a default rather than
            // whatever the allocator handed back — which on a transform array is a NaN matrix and an
            // object that vanishes for one frame.
            var grown = NativeArray<T>.Zeroed(length, name: $"RenderData<{typeof(T).Name}>");

            if (storage.Length > 0) {
                storage.AsSpan().CopyTo(grown.AsSpan());
                storage.Dispose();
            }

            storage = grown;
        }

        public void ClearAt(int index) {
            if (index < storage.Length) {
                storage[index] = default;
            }
        }

        public void Dispose() {
            storage.Dispose();
            storage = default;
        }
    }
}
