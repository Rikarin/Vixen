// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Ui.Reactive;

/// <summary>A writable value that everything reading it is re-evaluated against.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
///     <para>
///         Writing a value equal to the current one does nothing at all — no version bump, no
///         notification, no effect run. That is not an optimisation bolted on the side; it is the
///         property the rest of the graph is built to preserve, and the reason a text box that
///         re-assigns the same string on every keystroke does not repaint the window.
///     </para>
///     <para>
///         "Equal" means <see cref="EqualityComparer{T}.Default" /> unless a comparer is supplied.
///         For a mutable reference type that is usually wrong — the object is the same instance, so
///         it compares equal, and a change made in place is invisible. Either hold an immutable
///         value, or pass <see cref="SignalComparer.Never{T}" /> and accept that every write
///         propagates.
///     </para>
/// </remarks>
public sealed class Signal<T> : ReactiveNode, IReadOnlySignal<T> {
    readonly IEqualityComparer<T> comparer;
    T value;

    /// <summary>Creates a signal holding <paramref name="initial" />.</summary>
    /// <param name="initial">The starting value.</param>
    /// <param name="comparer">How to decide a write changed nothing. Defaults to the type's own equality.</param>
    public Signal(T initial, IEqualityComparer<T>? comparer = null) {
        value = initial;
        this.comparer = comparer ?? EqualityComparer<T>.Default;
    }

    /// <summary>The current value.</summary>
    /// <remarks>Reading records a dependency; writing an unequal value invalidates every dependent.</remarks>
    public T Value {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return value;
        }
        set {
            ReactiveGraph.AssertOwningThread();
            AssertWriteAllowed();

            if (comparer.Equals(this.value, value)) {
                return;
            }

            this.value = value;
            Version++;
            ReactiveGraph.IncrementEpoch();
            NotifyConsumers();
        }
    }

    /// <inheritdoc />
    public T Peek() => value;

    /// <summary>Replaces the value with a function of the current one.</summary>
    /// <param name="update">Given the current value, returns the new one.</param>
    /// <remarks>
    ///     The read is untracked, which is what makes this safe to call from inside an effect: the
    ///     effect does not become dependent on the signal it is updating, and so does not re-run
    ///     itself forever.
    /// </remarks>
    public void Update(Func<T, T> update) {
        ArgumentNullException.ThrowIfNull(update);
        Value = update(value);
    }

    /// <summary>Replaces the value with a function of the current one and some state.</summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <param name="state">Passed through to <paramref name="update" />.</param>
    /// <param name="update">Given the current value and <paramref name="state" />, returns the new one.</param>
    /// <remarks>
    ///     The overload that lets the caller use a <see langword="static" /> lambda, so a per-frame
    ///     update allocates no closure. The rest of the UI's generated code is written this way for
    ///     the same reason.
    /// </remarks>
    public void Update<TState>(TState state, Func<T, TState, T> update) {
        ArgumentNullException.ThrowIfNull(update);
        Value = update(value, state);
    }

    /// <summary>Forces every dependent to re-evaluate, whatever the comparer thinks.</summary>
    /// <remarks>
    ///     The escape hatch for a value that was mutated in place. It is deliberately a method call
    ///     rather than something the setter does for you, so that the cost is visible at the point
    ///     where it is paid.
    /// </remarks>
    public void Invalidate() {
        ReactiveGraph.AssertOwningThread();
        AssertWriteAllowed();

        Version++;
        ReactiveGraph.IncrementEpoch();
        NotifyConsumers();
    }

    static void AssertWriteAllowed() {
        if (ReactiveGraph.ActiveConsumer is { AllowsSignalWrites: false }) {
            throw new InvalidOperationException(
                "A signal was written from inside a computed. A computed has to be a pure function of "
                + "what it reads — it is re-run whenever the graph feels like it, and possibly not at "
                + "all — so a write in one happens an unpredictable number of times. Move it to an effect."
            );
        }
    }
}

/// <summary>Comparers for the cases where a type's own equality is the wrong answer.</summary>
public static class SignalComparer {
    /// <summary>A comparer that reports nothing equal, so every write propagates.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <returns>The comparer.</returns>
    public static IEqualityComparer<T> Never<T>() => NeverEqual<T>.Instance;

    /// <summary>A comparer that compares by reference identity only.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <returns>The comparer.</returns>
    /// <remarks>
    ///     For a type with a value-like <c>Equals</c> that is expensive and rarely true — a large
    ///     record, a collection snapshot — where comparing instances is the cheaper approximation.
    /// </remarks>
    public static IEqualityComparer<T> Reference<T>() where T : class => ReferenceWrapper<T>.Instance;

    sealed class NeverEqual<T> : IEqualityComparer<T> {
        public static readonly NeverEqual<T> Instance = new();

        public bool Equals(T? x, T? y) => false;

        public int GetHashCode(T value) => 0;
    }

    sealed class ReferenceWrapper<T> : IEqualityComparer<T> where T : class {
        public static readonly ReferenceWrapper<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
