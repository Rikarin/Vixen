// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>Patina #180's kernels for graphs that make environments, by kernel name.</summary>
/// <remarks>
///     <para>
///         <b>Each reads and writes an equirectangular panorama</b>: once round the horizon across the width, the
///         zenith at the top row and the nadir at the bottom. They share one convention for a direction, the one
///         <c>Surface/Environment Light</c> reads a panorama by, so a sun drawn by <see cref="Sky" /> and a light
///         added by <see cref="PanoramaLight" /> at one azimuth and elevation land on one texel.
///     </para>
///     <para>
///         ⚠ <b>A panorama wraps round the horizon and does not wrap top to bottom</b>, since those rows are the two
///         poles. The kernels read across the left and right edges whatever the plan says, and never across the top
///         and bottom.
///     </para>
///     <para>
///         ⚠ <b>Their nodes say they are no adjustment</b>, so a layer stack offers none of them as a filter or a mask
///         step: a layer is a picture in a model's UV layout, where a row is not an elevation and a column not an
///         azimuth, and a horizon or a light at a direction means nothing there. Patina's roll call of every offered
///         filter found Horizon on a mask, where it changed nothing.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TextureEnvironmentKernels {
    /// <summary>A clear sky by Preetham's analytic daylight model, with a sun and a ground.</summary>
    public const string Sky = "Sky";

    /// <summary>A panorama turned about the vertical, tipped and rolled.</summary>
    public const string PanoramaTurn = "PanoramaTurn";

    /// <summary>A disc of light added to a panorama at a direction.</summary>
    public const string PanoramaLight = "PanoramaLight";

    /// <summary>A panorama's ground below a horizon replaced by a colour.</summary>
    public const string PanoramaHorizon = "PanoramaHorizon";

    /// <summary>Every one of them.</summary>
    public static IReadOnlyList<string> All { get; } = [PanoramaHorizon, PanoramaLight, PanoramaTurn, Sky];
}
