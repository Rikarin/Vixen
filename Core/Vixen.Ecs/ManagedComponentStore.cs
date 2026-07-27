// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Collections;

namespace Vixen.Ecs;

/// <summary>The type-erased part of a managed component store, so the world can hold a table of them.</summary>
internal interface IManagedComponentStore {
    /// <summary>Gives a slot back, clearing it so it stops rooting whatever it held.</summary>
    /// <param name="handle">The handle to release. Zero means there was no slot.</param>
    void Release(int handle);

    /// <summary>Drops every slot.</summary>
    void Clear();

    /// <summary>Reads a slot as an object, for the type-erased copy paths.</summary>
    /// <param name="handle">The one-based handle, or zero for nothing.</param>
    /// <returns>The value, boxed if it is a struct.</returns>
    /// <remarks>
    ///     Boxing, and deliberately so: this is how a prefab, a scene load and the editor's clipboard
    ///     move a managed component between worlds without knowing its type. All three are one-off
    ///     operations measured in entities per user action, not per frame.
    /// </remarks>
    object? Box(int handle);

    /// <summary>Writes a boxed value into a slot that already exists.</summary>
    /// <param name="handle">The one-based handle. Zero does nothing.</param>
    /// <param name="value">The value, boxed. A value of the wrong type writes the default.</param>
    void Unbox(int handle, object? value);

    /// <summary>Takes an empty slot.</summary>
    /// <returns>The one-based handle.</returns>
    int TakeSlot();

    /// <summary>An empty store of the same component type.</summary>
    /// <returns>The new store.</returns>
    /// <remarks>
    ///     How a world that has never seen a component type gets a correctly typed store for it
    ///     without knowing the type. The store does know — it is the closed generic — so it can make
    ///     one, where reflection would have to construct a generic type at run time and would not
    ///     survive NativeAOT.
    /// </remarks>
    IManagedComponentStore CreateSibling();
}

/// <summary>
///     Where components that are, or contain, references actually live.
/// </summary>
/// <typeparam name="T">The component type.</typeparam>
/// <remarks>
///     <para>
///         A chunk is a byte array and the garbage collector cannot see references inside one, so a
///         managed component's chunk column holds a four-byte handle into here instead. This is the
///         reason the design discourages them: every access is an extra indirection into memory that
///         has nothing to do with the entity's neighbours, which is precisely the cache behaviour an
///         archetype ECS exists to avoid. They exist because <c>Behavior</c>, <c>Mesh</c> and
///         <c>Material</c> references are reference types and pretending otherwise would be dogma.
///     </para>
///     <para>
///         Built on <see cref="ChunkedArray{T}" /> rather than a list because <c>World.Get</c> hands
///         out a <c>ref</c> into it, and a list that reallocates on growth would leave that
///         reference pointing at the old array.
///     </para>
///     <para>
///         Handles are one-based. A chunk row is zeroed when it is allocated or moved into, so zero
///         has to mean "no slot yet" rather than "slot zero".
///     </para>
/// </remarks>
internal sealed class ManagedComponentStore<T> : IManagedComponentStore {
    readonly ChunkedArray<T> values = new(256);
    readonly Stack<int> free = new();

    /// <summary>Takes a slot for a value.</summary>
    /// <param name="value">What to put in it.</param>
    /// <returns>The one-based handle.</returns>
    public int Allocate(T value) {
        if (free.TryPop(out var reused)) {
            values[reused] = value;
            return reused + 1;
        }

        return values.Add(value) + 1;
    }

    /// <summary>A reference to a slot's value.</summary>
    /// <param name="handle">The one-based handle.</param>
    /// <returns>A reference that stays valid as the store grows.</returns>
    public ref T Get(int handle) => ref values[handle - 1];

    /// <inheritdoc />
    public void Release(int handle) {
        if (handle == 0) {
            return;
        }

        // Cleared, not just marked free. A released slot that still held the reference would keep a
        // texture or a behaviour alive for as long as the world lives, which is a leak that looks
        // exactly like normal memory growth.
        values[handle - 1] = default!;
        free.Push(handle - 1);
    }

    /// <inheritdoc />
    public object? Box(int handle) => handle == 0 ? null : values[handle - 1];

    /// <inheritdoc />
    public void Unbox(int handle, object? value) {
        if (handle != 0) {
            values[handle - 1] = value is T typed ? typed : default!;
        }
    }

    /// <inheritdoc />
    public int TakeSlot() => Allocate(default!);

    /// <inheritdoc />
    public IManagedComponentStore CreateSibling() => new ManagedComponentStore<T>();

    /// <inheritdoc />
    public void Clear() {
        values.Clear();
        free.Clear();
    }
}
