// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>One <c>property: value</c>, as the cascade sees it.</summary>
/// <param name="Property">The interned property name.</param>
/// <param name="Value">The interned value text.</param>
/// <param name="Important">Whether it carried <c>!important</c>.</param>
/// <remarks>
///     <para>
///         The value is a string, and that is the design rather than a shortcut. The cascade's
///         entire job is deciding <i>which declaration wins</i>; what <c>1.5rem</c> or
///         <c>spring(1, 100, 10)</c> then means is the property system's job, downstream and
///         separate. Keeping them apart is what lets a stylesheet carry a property Vixen has never
///         heard of — including the custom properties the whole <c>var()</c> mechanism is built on —
///         without the cascade needing an opinion about it.
///     </para>
///     <para>
///         Interned, because a stylesheet says <c>color: rgb(255, 0, 0)</c> in forty places and the
///         style-sharing cache compares whole declaration sets for equality. Integers make that a
///         sequence compare instead of forty string compares.
///     </para>
/// </remarks>
public readonly record struct Declaration(int Property, int Value, bool Important);

/// <summary>A run of declarations belonging to one rule.</summary>
/// <param name="Start">Where they begin.</param>
/// <param name="Count">How many there are.</param>
public readonly record struct DeclarationRange(int Start, int Count);
