// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;

namespace Vixen.Net.Engine;

/// <summary>A value the server sets and every client that can see the object ends up with.</summary>
/// <typeparam name="T">
///     What it holds. One of the types the wire knows, or one a game registered a codec for.
/// </typeparam>
/// <remarks>
///     <para>
///         The convenient authoring style, and the same mechanism underneath as the ECS-native one.
///         A module's fields become a lane layout, a lane layout is what
///         <c>DeltaCodec</c> needs, and so a <c>SyncVar</c> is delta-encoded, priority-shed,
///         acknowledged and attributed per field without a line of code here doing any of it.
///     </para>
///     <para>
///         <b>Assigning it on a client changes nothing that lasts.</b> The value is whatever the last
///         snapshot said, so a local write is overwritten by the next one that mentions it. That is
///         deliberately not an exception: prediction is a Phase 9+ feature with a design of its own,
///         and a <c>SyncVar</c> that threw would make every optimistic UI update a crash.
///     </para>
///     <para>
///         <see cref="Changed" /> fires when a value <i>arrives</i> and differs, not when it is set
///         locally on the server. So a handler is the place to react to a change, not the place to
///         cause one — and the two ends run the same handler for the same reason.
///     </para>
/// </remarks>
public sealed class SyncVar<T> : ISyncField {
    readonly ISyncCodec<T> codec;
    readonly IEqualityComparer<T> comparer;

    T value;

    /// <inheritdoc />
    public string Name { get; private set; } = string.Empty;

    /// <inheritdoc />
    public ReadOnlySpan<WireLane> Lanes => codec.Lanes;

    /// <inheritdoc />
    public bool IsDirty { get; private set; }

    /// <summary>Raised when a value arrives that differs from the one held.</summary>
    public event Action<T, T>? Changed;

    /// <summary>What it holds.</summary>
    public T Value {
        get => value;
        set {
            if (comparer.Equals(this.value, value)) {
                return;
            }

            this.value = value;
            IsDirty = true;
        }
    }

    /// <summary>Creates one.</summary>
    /// <param name="initial">What it starts as.</param>
    /// <param name="comparer">How to tell two of them apart. The default one, if null.</param>
    /// <exception cref="NotSupportedException">The wire does not know this type.</exception>
    public SyncVar(T initial = default!, IEqualityComparer<T>? comparer = null) {
        codec = SyncCodecs.For<T>();
        this.comparer = comparer ?? EqualityComparer<T>.Default;
        value = initial;
    }

    /// <inheritdoc />
    public void Write(ref BitWriter writer) => codec.Write(ref writer, in value);

    /// <inheritdoc />
    public bool Apply(ref BitReader reader) {
        if (!codec.Read(ref reader, out var arrived)) {
            return false;
        }

        if (comparer.Equals(value, arrived)) {
            return true;
        }

        var was = value;
        value = arrived;
        Changed?.Invoke(was, arrived);

        return true;
    }

    /// <inheritdoc />
    public void ClearDirty() => IsDirty = false;

    /// <inheritdoc />
    public void Rename(string name) => Name = name;

    /// <inheritdoc />
    public override string ToString() => $"{Name} = {value}";

    /// <summary>Reads the value, so a sync var reads like the thing it holds.</summary>
    /// <param name="variable">The variable.</param>
    public static implicit operator T(SyncVar<T> variable) {
        ArgumentNullException.ThrowIfNull(variable);

        return variable.value;
    }
}
