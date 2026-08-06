// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv;

/// <summary>How hard the packer tries, which is a runtime decision rather than a quality one.</summary>
public enum PackQuality : byte {
    /// <summary>Skyline over island bounding boxes, with quarter turns. The fast path, and the fallback.</summary>
    Rectangle,

    /// <summary>Rasterized island masks against an occupancy bitmap. The quality path.</summary>
    Irregular,

    /// <summary>Near-rectangular neighbours grouped into composite rectangles first, then packed.</summary>
    SuperPatch
}

/// <summary>What the packer does when the islands do not fit at the density asked for.</summary>
public enum PackOverflow : byte {
    /// <summary>Scale everything down uniformly until it fits, and say so in the report.</summary>
    Scale,

    /// <summary>Spill into the next UDIM tile. An island may not straddle a tile boundary.</summary>
    NextTile,

    /// <summary>Refuse, and name the shortfall.</summary>
    Refuse
}

/// <summary>Everything the packer needs, and the resolution is not optional.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Resolution" /> is required because <see cref="Margin" /> is counted in
///         texels.</b> docs/plan/42 § D8. A margin expressed as a fraction of UV space is a bug with
///         a two-year fuse: it looks right at the resolution it was tuned at, and the same asset at
///         half resolution bleeds across islands at mip 3, in a build nobody associates with the
///         packing change. Bleeding is then misdiagnosed as a sampler problem roughly always.
///     </para>
///     <para>
///         ⚠ <b>Spacing is distributed evenly across every chart rather than applied per island as
///         it is placed.</b> That is the visibly better behaviour even when nothing bleeds — uneven
///         gaps read as carelessness in an atlas — and it is the one thing a standalone packer is
///         bought for.
///     </para>
/// </remarks>
public sealed record PackSettings {
    /// <summary>The atlas's edge length in texels. Required, and the reason is <see cref="Margin" />.</summary>
    public required int Resolution { get; init; }

    /// <summary>How many texels of empty space separate two islands.</summary>
    /// <remarks>Four is the usual answer for a 2K atlas: enough for a trilinear tap plus two mip levels.</remarks>
    public int Margin { get; init; } = 4;

    /// <summary>How hard to try.</summary>
    public PackQuality Quality { get; init; } = PackQuality.Irregular;

    /// <summary>What to do when it does not fit.</summary>
    public PackOverflow Overflow { get; init; } = PackOverflow.Scale;

    /// <summary>How many orientations an island may be tried in, as quarter turns. One means none.</summary>
    /// <remarks>
    ///     ⚠ Rotation is why the irregular path rasterizes masks rather than computing no-fit
    ///     polygons: NFP needs a unique polygon per pair <i>per rotation</i>, so sixteen orientations
    ///     of a thousand islands is a quarter of a billion polygons. A bitmask overlap test is
    ///     trivially parallel, trivially deterministic, and gets <i>more</i> accurate as the atlas
    ///     grows — which is the direction the problem actually goes.
    /// </remarks>
    public int Rotations { get; init; } = 4;

    /// <summary>Texels per world unit every island is scaled to, or zero to keep each island's own scale.</summary>
    /// <remarks>
    ///     docs/plan/42 § D9: uniform is the default because non-uniform density is invisible in the
    ///     atlas and glaring in the game.
    /// </remarks>
    public float TexelDensity { get; init; }

    /// <summary>How many islands the expensive core will place before the cheap tail takes over.</summary>
    /// <remarks>
    ///     ⚠ A bounded core is not a hidden truncation — every island is still placed. The tail is
    ///     an O(N log N) sweep of the leftovers into the gaps by descending area, which is what makes
    ///     a mesh with nine thousand tiny islands finish, and the report says how many took it.
    /// </remarks>
    public int CoreLimit { get; init; } = 1024;
}
