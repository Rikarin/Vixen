// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Water;

/// <summary>One row of a water panel's derived-numbers block, as the <c>@for</c> keys it.</summary>
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
///         ⚠ And the slot is in it so that two rows reading the same cannot collide: "Bodies drawn 0"
///         and "Points laid 0" differ, but <c>BuildContext.For</c> has no answer for two equal keys in
///         one loop and this block's contents are a list somebody will add to.
///     </para>
/// </remarks>
public readonly record struct WaterFactLine(int Slot, string Label, string Value);

/// <summary>The zone panel's derived-numbers block.</summary>
/// <remarks>The part is <c>WaterZoneFacts.vxml</c>, which holds the argument.</remarks>
public sealed partial class WaterZoneFacts;

/// <summary>The body panel's.</summary>
/// <remarks>The part is <c>WaterFacts.vxml</c>.</remarks>
public sealed partial class WaterFacts;

/// <summary>Why the last gesture did nothing.</summary>
/// <remarks>The part is <c>WaterNotice.vxml</c>.</remarks>
public sealed partial class WaterNotice;
