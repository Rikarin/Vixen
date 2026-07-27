// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Reactive;

/// <summary>A value that can be read and depended on, but not written by the reader.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
///     This is the type a component exposes and a control binds to. Handing out a
///     <see cref="Signal{T}" /> hands out the write, which is how a view ends up mutating a view
///     model it does not own; handing out this does not.
/// </remarks>
public interface IReadOnlySignal<out T> {
    /// <summary>The current value, recording a dependency if something is computing.</summary>
    T Value { get; }

    /// <summary>The current value without recording a dependency.</summary>
    /// <returns>The value.</returns>
    /// <remarks>
    ///     For the read that must not re-run whatever is doing it — a logging line inside an effect,
    ///     a comparison against the previous value. The same thing
    ///     <see cref="ReactiveGraph.Untracked{T}" /> does for a whole block.
    /// </remarks>
    T Peek();
}
