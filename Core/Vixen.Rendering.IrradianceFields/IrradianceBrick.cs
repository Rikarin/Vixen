// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>One brick, where it is and how much of the world it covers.</summary>
/// <remarks>
///     <para>
///         Sixty-four probes always, over <see cref="Size" /> cubed of the indirection grid's finest
///         cells. A brick of size one is as fine as the field gets; a brick of size eight covers five
///         hundred and twelve times the volume with the same sixty-four probes, and its probes are
///         eight times further apart.
///     </para>
///     <para>
///         <b>A brick is aligned to its own size</b>, which is what makes the sampling arithmetic
///         work: dividing a cell coordinate by the size and taking the fractional part only gives a
///         position inside the brick if the brick started at a multiple of its size. Everything that
///         allocates one enforces that, and it is why refinement halves rather than subdividing by
///         arbitrary factors.
///     </para>
/// </remarks>
/// <param name="Slot">Where its probes live in the pool.</param>
/// <param name="Cell">Its origin, in the indirection grid's finest cells.</param>
/// <param name="Size">How many finest cells it spans along each axis. A power of two.</param>
public readonly record struct IrradianceBrick(int Slot, Int3 Cell, int Size);

/// <summary>What one cell of the indirection grid holds.</summary>
/// <param name="Slot">The brick covering it, or <see cref="IrradianceIndirection.Empty" />.</param>
/// <param name="Size">
///     That brick's extent in cells. Every cell a brick covers repeats both numbers, so a lookup is
///     one fetch and never a search.
/// </param>
public readonly record struct IrradianceCell(int Slot, int Size) {
    /// <summary>A cell no brick covers.</summary>
    public static IrradianceCell Empty => new(IrradianceIndirection.Empty, 0);

    /// <summary>Whether a brick covers this cell.</summary>
    public bool HasBrick => Slot != IrradianceIndirection.Empty;
}
