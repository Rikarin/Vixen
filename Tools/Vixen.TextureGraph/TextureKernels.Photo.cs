// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph;

/// <summary>Patina #178's scan and photo kernels and #218's anti-repetition and terrain ones, by kernel name.</summary>
/// <remarks>
///     <para>
///         <b>Each is one gather over pictures other ops prepared.</b> <see cref="FlattenLight" /> divides by a light a
///         <c>Resample</c> and a <c>BlurHq</c> left at a fraction of the size; <see cref="PatchScatter" /> blends round
///         patches of its source about a mean a <c>MeanReduce</c> ladder measured; <see cref="ThermalErosion" /> is one
///         step a node repeats.
///     </para>
///     <para>
///         ⚠ <b>Two of them take a length and neither through <see cref="TextureParameterUnit.TexelsAtBase" /></b>:
///         a patch's size is in cells and a talus is a slope across the image, so each is resolution-independent by
///         construction. <c>Surface/Thermal Erosion</c>'s step count is the one number that is not, and its node says
///         so.
///     </para>
/// </remarks>
[TextureKernelSurface]
static class TexturePhotoKernels {
    /// <summary>A photo's lighting divided out, by luminance or per channel.</summary>
    public const string FlattenLight = "FlattenLight";

    /// <summary>An image rebuilt out of round, turned patches of itself, in a grid that tiles.</summary>
    public const string PatchScatter = "PatchScatter";

    /// <summary>A normal or an albedo from four photos lit from four sides.</summary>
    public const string PhotometricNormal = "PhotometricNormal";

    /// <summary>One step of material sliding down slopes steeper than a talus.</summary>
    public const string ThermalErosion = "ThermalErosion";

    /// <summary>Every one of them.</summary>
    public static IReadOnlyList<string> All { get; } = [FlattenLight, PatchScatter, PhotometricNormal, ThermalErosion];
}
