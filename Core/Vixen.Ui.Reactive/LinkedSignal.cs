// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Reactive;

/// <summary>A writable value that goes back to being derived whenever its source changes.</summary>
/// <typeparam name="TSource">What the value is derived from.</typeparam>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
///     <para>
///         The shape of nearly every selection in an editor. The inspector shows the selected
///         object's first tab; the user clicks the third; the selection changes and the tab has to go
///         back to the first. Written as a <see cref="Signal{T}" />, that last step is an effect that
///         watches the selection and writes the tab, and it is one of the most reliable sources of
///         bugs in a UI — the write happens at the wrong time, or twice, or races the panel being
///         rebuilt.
///     </para>
///     <para>
///         Written as a linked signal it is a declaration: the tab <i>is</i> the first tab of the
///         selection, unless somebody has since said otherwise. Angular's <c>linkedSignal</c>, which
///         ADR-007 names.
///     </para>
/// </remarks>
public sealed class LinkedSignal<TSource, T> : ReactiveNode, IReadOnlySignal<T> {
    readonly IEqualityComparer<T> comparer;
    readonly Func<TSource, T, T> compute;
    readonly Func<TSource> source;
    readonly IEqualityComparer<TSource> sourceComparer;
    bool hasValue;
    TSource? lastSource;
    T? value;

    /// <summary>Creates a linked signal.</summary>
    /// <param name="source">What it is derived from. Read with dependency tracking on.</param>
    /// <param name="compute">
    ///     Given the new source and the value before it changed, produces the new value. The previous
    ///     value is passed because resetting is not always the same as recomputing from nothing —
    ///     "keep the selected row if it is still in the new list" needs both.
    /// </param>
    /// <param name="sourceComparer">How to decide the source changed. Defaults to its own equality.</param>
    /// <param name="comparer">How to decide the result changed. Defaults to its own equality.</param>
    public LinkedSignal(
        Func<TSource> source,
        Func<TSource, T, T> compute,
        IEqualityComparer<TSource>? sourceComparer = null,
        IEqualityComparer<T>? comparer = null
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compute);

        this.source = source;
        this.compute = compute;
        this.sourceComparer = sourceComparer ?? EqualityComparer<TSource>.Default;
        this.comparer = comparer ?? EqualityComparer<T>.Default;

        Dirty = true;
    }

    /// <summary>The current value: derived, or whatever was last written since the source last moved.</summary>
    public T Value {
        get {
            ReactiveGraph.AssertOwningThread();
            UpdateValueVersion();
            ProducerAccessed(this);
            return value!;
        }
        set {
            ReactiveGraph.AssertOwningThread();

            // The source is settled first, deliberately. A write landing while a source change is
            // still pending would otherwise be thrown away by the next read, which reads as the UI
            // ignoring the user.
            UpdateValueVersion();

            if (hasValue && comparer.Equals(this.value!, value)) {
                return;
            }

            this.value = value;
            hasValue = true;
            Version++;
            ReactiveGraph.IncrementEpoch();
            NotifyConsumers();
        }
    }

    internal override bool TracksDependencies => true;

    internal override bool MustRecompute => !hasValue;

    /// <inheritdoc />
    public T Peek() {
        var previous = ReactiveGraph.ActiveConsumer;
        ReactiveGraph.ActiveConsumer = null;
        try {
            return Value;
        } finally {
            ReactiveGraph.ActiveConsumer = previous;
        }
    }

    internal override void OnDependencyMayHaveChanged() => NotifyConsumers();

    internal override void RecomputeValue() {
        TSource next;
        var previousConsumer = BeforeComputation();
        try {
            next = source();
        } finally {
            AfterComputation(previousConsumer);
        }

        if (hasValue && sourceComparer.Equals(lastSource!, next)) {
            // The source was re-read and had not moved, so whatever is here — derived or written —
            // stands. This is the case that makes a write survive an unrelated invalidation.
            return;
        }

        var previousValue = hasValue ? value! : default!;
        lastSource = next;

        var computed = compute(next, previousValue);
        if (hasValue && comparer.Equals(previousValue, computed)) {
            return;
        }

        value = computed;
        hasValue = true;
        Version++;
    }
}
