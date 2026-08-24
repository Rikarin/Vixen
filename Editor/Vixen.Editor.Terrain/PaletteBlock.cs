// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Terrain;

/// <summary>One entry of the foliage palette, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Which entry of the volume's palette it is, which is what a handler needs.</param>
/// <param name="Name">The type's name, which is the check box's label.</param>
/// <param name="Chosen">Whether a stroke would place it.</param>
/// <param name="Detail">Whether it is stored or derived, and at what spacing.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The whole record is the key</b>, which is the immutable-data half of the <c>@for</c>
///         rule: nothing behind this list is signal-backed, so a re-read palette has to be a changed
///         identity. <see cref="Chosen" /> is in it deliberately — the alternative is a second signal
///         and a binding, and here the panel rebuilds the whole list on every tick of a box anyway.
///     </para>
///     <para>
///         ⚠ <b><see cref="Slot" /> is load-bearing twice over.</b> It disambiguates two entries of
///         the same type at the same spacing, which is an ordinary thing to author while a species is
///         being split in two; and it is what a row's <c>change:</c> handler closes over to say which
///         entry was ticked, which is why this part needs no <c>refs</c>.
///     </para>
/// </remarks>
internal readonly record struct PaletteEntry(int Slot, string Name, bool Chosen, string Detail);

/// <summary>The foliage palette the Foliage panel is made of.</summary>
/// <remarks>
///     The part is <c>PaletteBlock.vxml</c>, which holds the argument; this file is the row record
///     and the accessibility modifier.
/// </remarks>
internal sealed partial class PaletteBlock;

/// <summary>The edit-layer stack the Terrain panel is made of.</summary>
/// <remarks>
///     The part is <c>LayerBlock.vxml</c>, which holds the argument. It keys on
///     <see cref="FactLine" />, which <c>FactBlock.cs</c> declares — the two lists are the same shape
///     under two tags, which is the one thing <c>@tag</c> being a compile-time directive stops a
///     single type from expressing.
/// </remarks>
internal sealed partial class LayerBlock;
