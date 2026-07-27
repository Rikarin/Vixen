// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Collections.Immutable;

namespace Vixen.Ui.Markup.Generators;

/// <summary>An array that compares by its contents, so an incremental pipeline can cache on it.</summary>
/// <typeparam name="T">The element type, itself compared by value.</typeparam>
/// <remarks>
///     ⚠ <b><see cref="ImmutableArray{T}" /> compares by reference</b> — its
///     <see cref="IEquatable{T}" /> asks whether two values wrap the same underlying array, not
///     whether they hold the same elements. A model carrying one therefore never compares equal to
///     the model the previous compilation produced, every downstream step re-runs, and the
///     generator is incremental in name only. That failure is silent: the output is correct and the
///     build is slow, which is exactly the kind of thing nobody notices until a project has two
///     hundred files in it.
/// </remarks>
internal readonly struct EquatableArray<T>(ImmutableArray<T> values)
    : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T> {
    readonly ImmutableArray<T> values = values;

    /// <summary>An empty array.</summary>
    public static EquatableArray<T> Empty { get; } = new(ImmutableArray<T>.Empty);

    /// <summary>How many elements there are.</summary>
    public int Count => values.IsDefault ? 0 : values.Length;

    /// <summary>The element at an index.</summary>
    /// <param name="index">Which one.</param>
    public T this[int index] => values[index];

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other) {
        if (values.IsDefault || other.values.IsDefault) {
            return values.IsDefault && other.values.IsDefault;
        }

        if (values.Length != other.values.Length) {
            return false;
        }

        for (var i = 0; i < values.Length; i++) {
            if (!values[i].Equals(other.values[i])) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() {
        if (values.IsDefault) {
            return 0;
        }

        var hash = 17;
        foreach (var value in values) {
            hash = (hash * 31) + (value?.GetHashCode() ?? 0);
        }

        return hash;
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() =>
        (values.IsDefault ? ImmutableArray<T>.Empty : values).AsEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
