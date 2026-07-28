// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Core;

/// <summary>What is selected, in order, as something a view can bind to.</summary>
/// <typeparam name="T">What is selectable — an asset id in the project browser, an entity in a scene.</typeparam>
/// <remarks>
///     <para>
///         Backed by a <see cref="CollectionSignal{T}" /> rather than a plain list, so a selection
///         list in the UI reconciles the items that changed instead of rebuilding — which matters
///         because box-selecting across a hierarchy changes the selection on every mouse-move.
///     </para>
///     <para>
///         <b>Ordered, and the last one added is <see cref="Primary" />.</b> Multi-object editing
///         needs a reference object to show values from and to apply-to-all against, and "the one you
///         clicked last" is what every editor means by it.
///     </para>
///     <para>
///         Deliberately not undoable. Selection is where you are looking, not what you have changed,
///         and an undo stack that rewinds the camera and the selection is one that never gets back to
///         the edit you wanted to take back. Commands that need it read
///         <see cref="EditorContext.Selection" /> at execution time.
///     </para>
/// </remarks>
public sealed class Selection<T> : IReadOnlyList<T> where T : notnull {
    readonly HashSet<T> membership;
    readonly CollectionSignal<T> items = new();

    /// <summary>How many things are selected.</summary>
    public int Count => items.Count;

    /// <summary>Whether nothing is selected.</summary>
    public bool IsEmpty => items.Count == 0;

    /// <summary>The one an inspector shows when several are selected: the most recently added.</summary>
    /// <remarks>
    ///     <see langword="default" /> when nothing is selected, which for a value type is a value and
    ///     not a null — check <see cref="IsEmpty" /> rather than the result.
    /// </remarks>
    public T? Primary => items.Count == 0 ? default : items[items.Count - 1];

    /// <summary>The selection as a list a view can reconcile against.</summary>
    public IReadOnlyList<T> Items => items;

    /// <summary>The item at <paramref name="index" />.</summary>
    /// <param name="index">A valid index.</param>
    public T this[int index] => items[index];

    /// <summary>Creates an empty selection.</summary>
    /// <param name="comparer">How two selected things are compared, or <see langword="null" /> for the default.</param>
    public Selection(IEqualityComparer<T>? comparer = null) => membership = new(comparer);

    /// <summary>Whether something is selected.</summary>
    /// <param name="item">The thing.</param>
    /// <returns>Whether it is in the selection.</returns>
    /// <remarks>
    ///     Reads the collection before answering, so a binding that highlights a row re-evaluates
    ///     when the selection changes rather than when its own row does.
    /// </remarks>
    public bool Contains(T item) {
        _ = items.Count;
        return membership.Contains(item);
    }

    /// <summary>Replaces the selection with one thing.</summary>
    /// <param name="item">The thing.</param>
    public void Set(T item) {
        ArgumentNullException.ThrowIfNull(item);

        if (membership.Count == 1 && membership.Contains(item)) {
            return;
        }

        Clear();
        Add(item);
    }

    /// <summary>Replaces the selection.</summary>
    /// <param name="selection">The things, in the order they should be held.</param>
    public void Set(IEnumerable<T> selection) {
        ArgumentNullException.ThrowIfNull(selection);

        Clear();

        foreach (var item in selection) {
            Add(item);
        }
    }

    /// <summary>Adds something, if it is not already selected.</summary>
    /// <param name="item">The thing.</param>
    /// <returns>Whether it was added.</returns>
    public bool Add(T item) {
        ArgumentNullException.ThrowIfNull(item);

        if (!membership.Add(item)) {
            return false;
        }

        items.Add(item);
        return true;
    }

    /// <summary>Removes something.</summary>
    /// <param name="item">The thing.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(T item) {
        ArgumentNullException.ThrowIfNull(item);

        if (!membership.Remove(item)) {
            return false;
        }

        items.Remove(item);
        return true;
    }

    /// <summary>Selects something if it is not selected, deselects it if it is.</summary>
    /// <param name="item">The thing.</param>
    /// <returns>Whether it ended up selected.</returns>
    public bool Toggle(T item) => !Remove(item) && Add(item);

    /// <summary>Deselects everything.</summary>
    public void Clear() {
        if (membership.Count == 0) {
            return;
        }

        membership.Clear();
        items.Clear();
    }

    /// <summary>The selected things, in order.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<T> GetEnumerator() {
        for (var index = 0; index < items.Count; index++) {
            yield return items[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
