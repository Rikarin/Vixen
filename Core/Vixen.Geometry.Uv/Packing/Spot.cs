// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Packing;

/// <summary>A place a unit could go, and how good it is, under a total order with no ties left in it.</summary>
/// <param name="Level">Where the unit's top edge would end up. Lower is better, and it is the first word.</param>
/// <param name="Waste">Texels stranded under the unit. The tie-break that makes a concave underside worth having.</param>
/// <param name="X">Where its left edge goes.</param>
/// <param name="Y">Where its bottom edge goes.</param>
/// <param name="Rotation">Which quarter turn.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The order is total, and that is a determinism requirement rather than tidiness.</b>
///         docs/plan/42 § B6 and § D12 exclude every metaheuristic the irregular-packing literature
///         reaches for — no annealing, no genetic search, no random restarts — which leaves the
///         comparison itself as the only place non-determinism could enter. Two spots that compared
///         equal would be separated by whichever the scan reached first, and that is exactly what a
///         thread count is allowed to change.
///     </para>
///     <para>
///         <c>X</c> and <c>Rotation</c> together identify the candidate uniquely inside one unit's
///         scan, so no two comparable spots are ever equal.
///     </para>
/// </remarks>
readonly record struct Spot(int Level, int Waste, int X, int Y, int Rotation) {
    /// <summary>No spot at all, and worse than every real one so it falls out of any comparison.</summary>
    public static Spot None => new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, -1);

    /// <summary>Whether this is a real placement.</summary>
    public bool Exists => Rotation >= 0;

    /// <summary>Whether this spot beats another under the total order.</summary>
    /// <param name="other">The spot to compare with.</param>
    /// <returns><c>true</c> when this one is strictly better.</returns>
    public bool Beats(in Spot other) {
        if (!other.Exists) {
            return Exists;
        }

        if (!Exists) {
            return false;
        }

        if (Level != other.Level) {
            return Level < other.Level;
        }

        if (Waste != other.Waste) {
            return Waste < other.Waste;
        }

        if (X != other.X) {
            return X < other.X;
        }

        return Rotation < other.Rotation;
    }
}
