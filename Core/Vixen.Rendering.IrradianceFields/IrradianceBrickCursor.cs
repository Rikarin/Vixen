// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>Where a round-robin refill has got to, walked over the indirection grid.</summary>
/// <remarks>
///     <para>
///         <b>Doc 19 § L2's "N bricks per frame round robin", as a value both fillers share.</b> The
///         walk is four lines of arithmetic and two subtleties, and the subtleties are the reason this
///         is a type rather than a loop written twice: a coarse brick names itself in every cell it
///         covers, so the walk stops only at the cell that <i>is</i> its origin — otherwise a brick of
///         size eight is filled five hundred and twelve times a lap — and the position has to be
///         dropped when the grid changes shape, because an index into a grid that no longer exists is
///         a different cell every time the resolution changes.
///     </para>
///     <para>
///         <b>And because the two fillers have to agree.</b> One traces on the CPU and one dispatches;
///         comparing them is how the shader is checked at all, and two implementations of the same
///         ordering is exactly the pair that drifts apart into a comparison of different bricks.
///     </para>
///     <para>
///         A struct, so a filler holds one by value and there is nothing to allocate or dispose. It is
///         mutable — it is a cursor — so it belongs in a field rather than in a readonly one.
///     </para>
/// </remarks>
public struct IrradianceBrickCursor {
    Int3 grid;

    /// <summary>Which indirection cell the next take starts at.</summary>
    /// <remarks>
    ///     Public because it is the only way to see that a round robin is making progress, and one
    ///     that silently stopped moving produces a field that is correct where it got to and stale
    ///     everywhere else — which looks like a lighting bug rather than a scheduling one.
    /// </remarks>
    public int Position { get; private set; }

    /// <summary>
    ///     Takes the next bricks to refill, carrying on from where the last call stopped.
    /// </summary>
    /// <param name="field">The field to walk.</param>
    /// <param name="bricks">Where they go. Its length is the budget.</param>
    /// <returns>How many were written, which is fewer than the budget for a mostly empty field.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <remarks>
    ///     Bounded by the grid rather than by the budget alone, so a field with two bricks in a
    ///     thousand cells cannot make one call scan forever looking for work.
    /// </remarks>
    public int Take(IrradianceField field, Span<IrradianceBrick> bricks) {
        ArgumentNullException.ThrowIfNull(field);

        var resolution = field.Indirection.Resolution;
        var total = checked((int)resolution.Volume);

        if (resolution != grid) {
            grid = resolution;
            Position = 0;
        }

        if (total <= 0 || bricks.IsEmpty) {
            return 0;
        }

        var taken = 0;

        for (var walked = 0; walked < total && taken < bricks.Length; walked++) {
            var index = Position;

            Position = (Position + 1) % total;

            var cell = new Int3(
                index % resolution.X,
                index / resolution.X % resolution.Y,
                index / (resolution.X * resolution.Y)
            );

            if (field.Indirection.IsOrigin(cell) && field.Indirection.TryBrick(cell, out var brick)) {
                bricks[taken++] = brick;
            }
        }

        return taken;
    }
}
