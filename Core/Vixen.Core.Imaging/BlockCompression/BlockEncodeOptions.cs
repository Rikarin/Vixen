// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>How far a whole-texture encode has got.</summary>
/// <param name="BlocksDone">How many blocks are finished, over every level, layer and face.</param>
/// <param name="BlocksTotal">How many blocks the texture has.</param>
public readonly record struct BlockEncodeProgress(long BlocksDone, long BlocksTotal) {
    /// <summary>How far through, zero to one.</summary>
    public double Fraction => BlocksTotal == 0 ? 1.0 : (double)BlocksDone / BlocksTotal;
}

/// <summary>How <see cref="BlockCompressor.Encode(TextureData, Vixen.Graphics.PixelFormat, BlockEncodeOptions)" /> should run.</summary>
public sealed record BlockEncodeOptions {
    /// <summary>How hard to search. Only BC7 and BC6H have a search; see <see cref="BlockQuality" />.</summary>
    public BlockQuality Quality { get; init; } = BlockQuality.Balanced;

    /// <summary>
    ///     How many threads may encode at once. Zero leaves it to the thread pool. The bytes written
    ///     do not depend on this: every block is a pure function of its own texels.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>
    ///     The source's values are already sRGB-encoded, in a format with no way of saying so.
    /// </summary>
    /// <remarks>
    ///     There is no sRGB form of <c>Rgba16UNorm</c>, so a sixteen-bit albedo arrives looking
    ///     linear. Without this, naming an sRGB target for it is refused as a transfer function
    ///     nobody asked for; with it, the values round to eight bits and are stored as they are.
    /// </remarks>
    public bool SourceIsSrgbEncoded { get; init; }

    /// <summary>Told how far the encode has got, in increasing order, from whichever thread finished last.</summary>
    public IProgress<BlockEncodeProgress>? Progress { get; init; }

    /// <summary>Stops the encode between blocks, by throwing <see cref="OperationCanceledException" />.</summary>
    public CancellationToken CancellationToken { get; init; }
}
