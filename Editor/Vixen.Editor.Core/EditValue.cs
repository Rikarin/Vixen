// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>What a member holds across a selection: one value, or the fact that they disagree.</summary>
/// <param name="Value">The value they share, or <see langword="null" /> when they do not.</param>
/// <param name="IsMixed">Whether the selected objects hold different values.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Disagreement is a state, not an average.</b> Twenty objects with three different values
///         must not show one of them as though it were the answer — the user would change something
///         else and never know it happened.
///     </para>
///     <para>
///         <b>Here rather than in the inspector, which is where it was written.</b> Mixed is not an
///         inspector concept: a gizmo holding twenty objects at different rotations, a graph editor
///         with two nodes selected and a settings page over two build profiles all have the same
///         question to answer. It moved down so that every editing surface answers it the same way
///         instead of each one deciding afresh what to show.
///     </para>
/// </remarks>
public readonly record struct EditValue(object? Value, bool IsMixed) {
    /// <summary>What a property with no objects behind it reads as.</summary>
    public static EditValue None => default;

    /// <summary>The value if they agree, or a fallback if they do not.</summary>
    /// <typeparam name="T">What it holds.</typeparam>
    /// <param name="fallback">What to answer when they disagree.</param>
    /// <returns>The value.</returns>
    public T Or<T>(T fallback) => IsMixed || Value is not T value ? fallback : value;
}
