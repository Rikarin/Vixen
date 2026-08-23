// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Terrain;

/// <summary>One row of a derived-numbers block, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the block.</param>
/// <param name="Label">What it is called.</param>
/// <param name="Value">And what it reads.</param>
/// <remarks>
///     ⚠ <b>The whole record is the key.</b> A <c>FactRow</c> holds no signals, so a binding inside
///     the loop body would have nothing to notice a changed number with — the value is the identity,
///     and a new reading is a new key whose region is built fresh. That is the immutable-data half of
///     the <c>@for</c> rule, and it is the opposite of what <c>VXML2011</c> teaches a reader who has
///     only met that warning.
///     <para>
///         ⚠ And the slot is in it so that two rows reading the same label and the same value cannot
///         collide: <c>BuildContext.For</c> has no answer for two equal keys in one loop.
///     </para>
/// </remarks>
public readonly record struct FactLine(int Slot, string Label, string Value);

/// <summary>The derived-numbers block the grass, growth and spline panels are made of.</summary>
/// <remarks>
///     The part is <c>FactBlock.vxml</c>, which holds the argument; this file is the row record and
///     the accessibility modifier.
/// </remarks>
public sealed partial class FactBlock;
