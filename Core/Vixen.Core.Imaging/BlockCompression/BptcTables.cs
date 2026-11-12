// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Intrinsics;

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>The partition patterns and anchors BC7 and BC6H share, and choosing between patterns.</summary>
/// <remarks>
///     <para>
///         <b>Written from the BPTC specification and proved against a GPU.</b>
///         <c>GpuBcnDecoderTests</c> feeds the hardware decoder a block for every mode and every
///         partition, so a wrong entry in any of these tables is a test failure rather than an
///         artefact somebody notices in a shipped texture. A check against a table copied from the
///         same place would prove nothing.
///     </para>
///     <para>
///         BC6H uses the first thirty-two two-subset patterns and their anchors, and no three-subset
///         ones. Each pattern says which subset each of the sixteen texels is in, row-major. The
///         anchor of subset zero is always texel zero; the tables hold the rest.
///     </para>
/// </remarks>
static class BptcTables {
    /// <summary>Sixty-four patterns of two subsets, sixteen entries each.</summary>
    public static ReadOnlySpan<byte> TwoSubsets => [
        0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, // 0
        0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, // 1
        0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, // 2
        0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, // 3
        0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, // 4
        0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, // 5
        0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, // 6
        0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, // 7
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, // 8
        0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 9
        0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, // 10
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, // 11
        0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 12
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, // 13
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 14
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, // 15
        0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, // 16
        0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, // 17
        0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, // 18
        0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, // 19
        0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, // 20
        0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, // 21
        0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, // 22
        0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 1, // 23
        0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, // 24
        0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, // 25
        0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, // 26
        0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0, // 27
        0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, // 28
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, // 29
        0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, // 30
        0, 0, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, // 31
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, // 32
        0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, // 33
        0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, // 34
        0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, // 35
        0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, // 36
        0, 1, 0, 1, 0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, // 37
        0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, // 38
        0, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 1, // 39
        0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 0, // 40
        0, 0, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 0, 0, // 41
        0, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0, // 42
        0, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0, // 43
        0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, // 44
        0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, // 45
        0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, // 46
        0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, // 47
        0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, // 48
        0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, // 49
        0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, // 50
        0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, // 51
        0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, // 52
        0, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, // 53
        0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, // 54
        0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0, // 55
        0, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 1, // 56
        0, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 1, // 57
        0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1, // 58
        0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, // 59
        0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, // 60
        0, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, // 61
        0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, // 62
        0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1 // 63
    ];

    /// <summary>Sixty-four patterns of three subsets, sixteen entries each.</summary>
    public static ReadOnlySpan<byte> ThreeSubsets => [
        0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2, // 0
        0, 0, 0, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 2, 1, // 1
        0, 0, 0, 0, 2, 0, 0, 1, 2, 2, 1, 1, 2, 2, 1, 1, // 2
        0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 1, // 3
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, // 4
        0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 2, // 5
        0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, // 6
        0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, // 7
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, // 8
        0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, // 9
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, // 10
        0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, // 11
        0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, 0, 1, 1, 2, // 12
        0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, 0, 1, 2, 2, // 13
        0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 2, // 14
        0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, 2, 2, 2, 0, // 15
        0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 2, // 16
        0, 1, 1, 1, 0, 0, 1, 1, 2, 0, 0, 1, 2, 2, 0, 0, // 17
        0, 0, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, // 18
        0, 0, 2, 2, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 1, // 19
        0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 2, // 20
        0, 0, 0, 1, 0, 0, 0, 1, 2, 2, 2, 1, 2, 2, 2, 1, // 21
        0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, // 22
        0, 0, 0, 0, 1, 1, 0, 0, 2, 2, 1, 0, 2, 2, 1, 0, // 23
        0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0, // 24
        0, 0, 1, 2, 0, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2, // 25
        0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 0, // 26
        0, 0, 0, 0, 0, 1, 1, 0, 1, 2, 2, 1, 1, 2, 2, 1, // 27
        0, 0, 2, 2, 1, 1, 0, 2, 1, 1, 0, 2, 0, 0, 2, 2, // 28
        0, 1, 1, 0, 0, 1, 1, 0, 2, 0, 0, 2, 2, 2, 2, 2, // 29
        0, 0, 1, 1, 0, 1, 2, 2, 0, 1, 2, 2, 0, 0, 1, 1, // 30
        0, 0, 0, 0, 2, 0, 0, 0, 2, 2, 1, 1, 2, 2, 2, 1, // 31
        0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 2, 2, 2, // 32
        0, 2, 2, 2, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 1, // 33
        0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 2, // 34
        0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0, // 35
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, // 36
        0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, // 37
        0, 1, 2, 0, 2, 0, 1, 2, 1, 2, 0, 1, 0, 1, 2, 0, // 38
        0, 0, 1, 1, 2, 2, 0, 0, 1, 1, 2, 2, 0, 0, 1, 1, // 39
        0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, // 40
        0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, // 41
        0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, // 42
        0, 0, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, 1, 1, 2, 2, // 43
        0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 1, // 44
        0, 2, 2, 0, 1, 2, 2, 1, 0, 2, 2, 0, 1, 2, 2, 1, // 45
        0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1, // 46
        0, 0, 0, 0, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, 2, 1, // 47
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, // 48
        0, 2, 2, 2, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 1, // 49
        0, 0, 0, 2, 1, 1, 1, 2, 0, 0, 0, 2, 1, 1, 1, 2, // 50
        0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, // 51
        0, 2, 2, 2, 0, 1, 1, 1, 0, 1, 1, 1, 0, 2, 2, 2, // 52
        0, 0, 0, 2, 1, 1, 1, 2, 1, 1, 1, 2, 0, 0, 0, 2, // 53
        0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, // 54
        0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, 2, 1, 1, 2, // 55
        0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2, 2, // 56
        0, 0, 2, 2, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2, // 57
        0, 0, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 0, 0, 2, 2, // 58
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 1, 2, // 59
        0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, // 60
        0, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 1, 2, 2, 2, // 61
        0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // 62
        0, 1, 1, 1, 2, 0, 1, 1, 2, 2, 0, 1, 2, 2, 2, 0 // 63
    ];

    /// <summary>The anchor texel of subset one in a two-subset pattern.</summary>
    public static ReadOnlySpan<byte> TwoSubsetAnchors => [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15
    ];

    /// <summary>The anchor texel of subset one in a three-subset pattern.</summary>
    public static ReadOnlySpan<byte> ThreeSubsetAnchors1 => [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3
    ];

    /// <summary>The anchor texel of subset two in a three-subset pattern.</summary>
    public static ReadOnlySpan<byte> ThreeSubsetAnchors2 => [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8
    ];

    /// <summary>The interpolation weights of a two-bit index, in sixty-fourths.</summary>
    public static ReadOnlySpan<byte> Weights2 => [0, 21, 43, 64];

    /// <summary>The interpolation weights of a three-bit index, in sixty-fourths.</summary>
    public static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];

    /// <summary>The interpolation weights of a four-bit index, in sixty-fourths.</summary>
    /// <remarks>
    ///     Not evenly spaced, and not a rounding of an even spacing either. Every table here is
    ///     symmetric about its middle, which is what makes swapping two endpoints and inverting every
    ///     index describe the identical palette.
    /// </remarks>
    public static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>The weights for an index of so many bits.</summary>
    /// <param name="bits">Two, three or four.</param>
    /// <returns>Its table.</returns>
    public static ReadOnlySpan<byte> WeightsOf(int bits) =>
        bits switch {
            2 => Weights2,
            3 => Weights3,
            _ => Weights4
        };

    /// <summary>The subset pattern a mode's partition number names.</summary>
    /// <param name="subsets">One, two or three.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="pattern">Sixteen entries to fill.</param>
    public static void PatternOf(int subsets, int partition, Span<byte> pattern) {
        if (subsets == 1) {
            pattern[..16].Clear();
            return;
        }

        (subsets == 2 ? TwoSubsets : ThreeSubsets).Slice(partition * 16, 16).CopyTo(pattern);
    }

    /// <summary>Which texel is a subset's anchor, whose top index bit is not stored.</summary>
    /// <param name="subsets">How many subsets the mode has.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="subset">Which subset.</param>
    /// <returns>The texel.</returns>
    public static int AnchorOf(int subsets, int partition, int subset) =>
        subset == 0
            ? 0
            : subsets == 2
                ? TwoSubsetAnchors[partition]
                : subset == 1
                    ? ThreeSubsetAnchors1[partition]
                    : ThreeSubsetAnchors2[partition];

    /// <summary>
    ///     Scores every partition by how far the block's texels sit from one line per subset, which
    ///     is the error no choice of endpoints can remove.
    /// </summary>
    /// <param name="points">Sixteen texels of four channels each.</param>
    /// <param name="patterns">The table of patterns.</param>
    /// <param name="subsets">How many subsets each pattern has.</param>
    /// <param name="scores">One score per partition to fill; the patterns' count is its length.</param>
    /// <remarks>
    ///     ⚠ <b>Not a sum of per-channel variance, which is the cheap version.</b> Two flat colours
    ///     split badly and two gradients split well can have the same variance; what a subset can
    ///     represent is a line, so the score is what is left after the best line is taken out:
    ///     the covariance's trace less its largest eigenvalue. Running it over every pattern is the
    ///     reason the moments are summed rather than recomputed.
    /// </remarks>
    public static void ScorePartitions(
        ReadOnlySpan<Vector128<float>> points,
        ReadOnlySpan<byte> patterns,
        int subsets,
        Span<float> scores
    ) {
        // Per texel: the channels, then the ten distinct products of pairs of them.
        Span<Vector128<float>> moments = stackalloc Vector128<float>[16 * 4];

        for (var texel = 0; texel < 16; texel++) {
            var point = points[texel];
            var (r, g, b, a) = (point[0], point[1], point[2], point[3]);

            moments[texel * 4] = point;
            moments[texel * 4 + 1] = point * r;
            moments[texel * 4 + 2] = Vector128.Create(g * g, g * b, g * a, b * b);
            moments[texel * 4 + 3] = Vector128.Create(b * a, a * a, 0f, 0f);
        }

        Span<Vector128<float>> sums = stackalloc Vector128<float>[3 * 4];
        Span<int> counts = stackalloc int[3];

        for (var partition = 0; partition < scores.Length; partition++) {
            sums.Clear();
            counts.Clear();

            var pattern = patterns.Slice(partition * 16, 16);

            for (var texel = 0; texel < 16; texel++) {
                var subset = pattern[texel];

                counts[subset]++;

                for (var moment = 0; moment < 4; moment++) {
                    sums[subset * 4 + moment] += moments[texel * 4 + moment];
                }
            }

            var score = 0f;

            for (var subset = 0; subset < subsets; subset++) {
                score += Residual(sums.Slice(subset * 4, 4), counts[subset]);
            }

            scores[partition] = score;
        }
    }

    /// <summary>The <paramref name="kept" />.Length lowest-scoring partitions, best first.</summary>
    /// <param name="scores">The scores.</param>
    /// <param name="kept">Filled with partition numbers.</param>
    /// <remarks>Ties go to the lower number, so the order is a function of the scores alone.</remarks>
    public static void KeepBest(ReadOnlySpan<float> scores, Span<byte> kept) {
        Span<float> best = stackalloc float[kept.Length];
        var filled = 0;

        for (var partition = 0; partition < scores.Length; partition++) {
            var score = scores[partition];
            var at = filled;

            while (at > 0 && score < best[at - 1]) {
                at--;
            }

            if (at >= kept.Length) {
                continue;
            }

            filled = Math.Min(filled + 1, kept.Length);

            for (var move = filled - 1; move > at; move--) {
                best[move] = best[move - 1];
                kept[move] = kept[move - 1];
            }

            best[at] = score;
            kept[at] = (byte)partition;
        }
    }

    /// <summary>What is left of a subset's variance once its dominant direction is taken out.</summary>
    static float Residual(ReadOnlySpan<Vector128<float>> sum, int count) {
        if (count < 2) {
            return 0f;
        }

        var inverse = 1f / count;
        var mean = sum[0];
        var (r, g, b, a) = (mean[0], mean[1], mean[2], mean[3]);
        var rows = sum[1];
        var more = sum[2];
        var rest = sum[3];

        var rr = rows[0] - r * r * inverse;
        var rg = rows[1] - r * g * inverse;
        var rb = rows[2] - r * b * inverse;
        var ra = rows[3] - r * a * inverse;
        var gg = more[0] - g * g * inverse;
        var gb = more[1] - g * b * inverse;
        var ga = more[2] - g * a * inverse;
        var bb = more[3] - b * b * inverse;
        var ba = rest[0] - b * a * inverse;
        var aa = rest[1] - a * a * inverse;

        var trace = rr + gg + bb + aa;

        if (trace <= 1e-3f) {
            return 0f;
        }

        // Power iteration from the longest diagonal's column: three rounds is enough to rank
        // patterns, and the winner gets a real fit afterwards.
        float x, y, z, w;

        if (rr >= gg && rr >= bb && rr >= aa) {
            (x, y, z, w) = (rr, rg, rb, ra);
        } else if (gg >= bb && gg >= aa) {
            (x, y, z, w) = (rg, gg, gb, ga);
        } else if (bb >= aa) {
            (x, y, z, w) = (rb, gb, bb, ba);
        } else {
            (x, y, z, w) = (ra, ga, ba, aa);
        }

        var length = 1f / MathF.Sqrt(x * x + y * y + z * z + w * w + 1e-12f);

        (x, y, z, w) = (x * length, y * length, z * length, w * length);

        var largest = 0f;

        for (var round = 0; round < 3; round++) {
            var nx = rr * x + rg * y + rb * z + ra * w;
            var ny = rg * x + gg * y + gb * z + ga * w;
            var nz = rb * x + gb * y + bb * z + ba * w;
            var nw = ra * x + ga * y + ba * z + aa * w;

            largest = MathF.Sqrt(nx * nx + ny * ny + nz * nz + nw * nw);

            if (largest < 1e-9f) {
                return trace;
            }

            var scale = 1f / largest;

            (x, y, z, w) = (nx * scale, ny * scale, nz * scale, nw * scale);
        }

        return Math.Max(trace - largest, 0f);
    }
}
