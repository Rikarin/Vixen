// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv.Packing;

/// <summary>One island inside a unit, as a texel rectangle in the unit's unrotated frame.</summary>
/// <param name="Island">Which island, by index into what the caller handed in.</param>
/// <param name="X">Its left edge inside the unit.</param>
/// <param name="Y">Its bottom edge.</param>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height.</param>
readonly record struct UnitMember(int Island, int X, int Y, int Width, int Height);

/// <summary>What the packer actually places: one island, or a super-patch of several.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D7's third rung — the TOG 2023 idea with the learning taken out. Islands
///         that are already close to rectangular gain nothing from being nested individually and cost
///         a full column scan each; grouped into one composite rectangle they are placed once, and the
///         composite is a shape the skyline handles well by construction.
///     </para>
///     <para>
///         ⚠ <b>The spacing inside a composite is the same margin as outside it.</b> A group that
///         packed its members flush would be a factor-of-two margin error hiding inside a rung nobody
///         looks at, which is the shape of every bleeding bug that reaches a build.
///     </para>
/// </remarks>
sealed class PackUnit {
    /// <summary>The unit's mask, one per quarter turn it may be tried in.</summary>
    public required IslandMask[] Orientations { get; init; }

    /// <summary>The islands inside it, and where they sit.</summary>
    public required UnitMember[] Members { get; init; }

    /// <summary>How many texels the members cover, which is what the descending-area order sorts on.</summary>
    public required int Area { get; init; }

    /// <summary>The lowest island index inside it. ⚠ The explicit tie-break docs/plan/42 § D7 requires.</summary>
    public required int Order { get; init; }

    /// <summary>A key taken from the shape, which orders two equal areas without asking who arrived first.</summary>
    public ulong Signature => Orientations[0].Signature;

    /// <summary>Where a member ends up once the unit has been turned.</summary>
    /// <param name="member">The member.</param>
    /// <param name="rotation">The unit's quarter turns.</param>
    /// <returns>The member's lower corner inside the turned unit.</returns>
    public (int X, int Y) Locate(in UnitMember member, int rotation) {
        var width = Orientations[0].Width;
        var height = Orientations[0].Height;

        return (rotation & 3) switch {
            1 => (height - member.Y - member.Height, member.X),
            2 => (width - member.X - member.Width, height - member.Y - member.Height),
            3 => (member.Y, width - member.X - member.Width),
            _ => (member.X, member.Y)
        };
    }
}
