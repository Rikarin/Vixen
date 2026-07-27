// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.ExceptionServices;

namespace Vixen.Ui.Reactive;

/// <summary>A value derived from other signals, evaluated on demand and memoised.</summary>
/// <typeparam name="T">The derived value type.</typeparam>
/// <remarks>
///     <para>
///         Lazy in the strong sense: a computed nobody reads is never evaluated, however many times
///         its dependencies change. That is what makes it safe to define one per bindable property
///         of a component whose panel is collapsed.
///     </para>
///     <para>
///         The computation must be a pure function of what it reads. It is re-run when the graph
///         needs it and not otherwise, so a side effect in one happens an unpredictable number of
///         times — writing a signal from inside one throws for that reason.
///     </para>
///     <para>
///         Dependencies are discovered by running it, so a computation that reads different signals
///         on different runs is fully supported, and the dependency set narrows as well as widens.
///         A computed guarding an expensive read behind a cheap flag stops depending on the
///         expensive one the moment the flag goes false.
///     </para>
/// </remarks>
public sealed class Computed<T> : ReactiveNode, IReadOnlySignal<T> {
    readonly Func<T> compute;
    readonly IEqualityComparer<T> comparer;
    ExceptionDispatchInfo? failure;
    State state = State.Unevaluated;
    T? value;

    /// <summary>Creates a computed value from <paramref name="compute" />.</summary>
    /// <param name="compute">The derivation. Runs with dependency tracking on.</param>
    /// <param name="comparer">
    ///     How to decide a re-run produced the same answer. Defaults to the type's own equality.
    ///     Equality here is a propagation barrier: an unchanged result stops the invalidation dead
    ///     rather than passing it on to everything downstream.
    /// </param>
    public Computed(Func<T> compute, IEqualityComparer<T>? comparer = null) {
        ArgumentNullException.ThrowIfNull(compute);
        this.compute = compute;
        this.comparer = comparer ?? EqualityComparer<T>.Default;

        // Born dirty: a live node that is clean is taken at its word and never evaluated, and this
        // one has never run.
        Dirty = true;
    }

    /// <summary>The derived value, evaluating it if a dependency has moved since the last read.</summary>
    /// <exception cref="Exception">Whatever the computation threw, rethrown with its original stack.</exception>
    public T Value {
        get {
            ReactiveGraph.AssertOwningThread();
            UpdateValueVersion();
            ProducerAccessed(this);

            if (state == State.Faulted) {
                failure!.Throw();
            }

            return value!;
        }
    }

    internal override bool TracksDependencies => true;

    internal override bool AllowsSignalWrites => false;

    /// <remarks>
    ///     <see cref="State.Evaluating" /> belongs in this list. A computed that reads itself reaches
    ///     here mid-run, and the only way the cycle is detected rather than quietly answered with a
    ///     half-built value is for the recursive read to insist on recomputing.
    /// </remarks>
    internal override bool MustRecompute => state is State.Unevaluated or State.Evaluating or State.Faulted;

    /// <inheritdoc />
    public T Peek() {
        // Spelled out rather than deferring to ReactiveGraph.Untracked, which would need a closure
        // over `this` on every call — and a peek inside an effect is a per-frame operation.
        var previous = ReactiveGraph.ActiveConsumer;
        ReactiveGraph.ActiveConsumer = null;
        try {
            return Value;
        } finally {
            ReactiveGraph.ActiveConsumer = previous;
        }
    }

    internal override void OnDependencyMayHaveChanged() =>
        // Invalidation passes straight through without evaluating anything. Whether this computed
        // actually changed is decided when somebody reads it, which may be never.
        NotifyConsumers();

    internal override void RecomputeValue() {
        if (state == State.Evaluating) {
            state = State.Unevaluated;
            throw new InvalidOperationException(
                "A computed read itself, directly or through a chain of other computeds. There is no "
                + "value that would satisfy the definition, so this is reported rather than looped."
            );
        }

        var previousState = state;
        var previousValue = value;
        state = State.Evaluating;

        T next;
        ExceptionDispatchInfo? thrown = null;
        var previousConsumer = BeforeComputation();
        try {
            next = compute();
        } catch (Exception exception) {
            next = default!;
            thrown = ExceptionDispatchInfo.Capture(exception);
        } finally {
            AfterComputation(previousConsumer);
        }

        if (thrown is not null) {
            // A failure is a value like any other as far as propagation goes: dependents have to be
            // told, or they would keep serving a result derived from inputs that no longer produce one.
            state = State.Faulted;
            failure = thrown;
            value = default;
            Version++;
            return;
        }

        if (previousState == State.Valid && comparer.Equals(previousValue!, next)) {
            // The version is deliberately not bumped. This is the equality short-circuit: everything
            // downstream compares versions, sees nothing moved, and does not re-run.
            value = previousValue;
            state = State.Valid;
            return;
        }

        value = next;
        failure = null;
        state = State.Valid;
        Version++;
    }

    enum State {
        /// <summary>Never run.</summary>
        Unevaluated,

        /// <summary>Running right now. Seeing this again means a cycle.</summary>
        Evaluating,

        /// <summary>Holds a value.</summary>
        Valid,

        /// <summary>The last run threw, and reading it rethrows.</summary>
        Faulted
    }
}
