// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>
///     What a pass renders into, as a pipeline sees it: formats and a sample count, no textures.
/// </summary>
/// <remarks>
///     <para>
///         <strong>A pipeline depends on the formats, not on the images.</strong> That is why this
///         type exists separately from the attachments a pass is actually given, and it is what makes
///         the whole arrangement work: the swapchain hands out a different image every frame and the
///         render graph aliases transient textures freely, yet neither invalidates a single pipeline,
///         because neither changes a format. Keying pipelines by texture would rebuild every one of
///         them three frames out of three.
///     </para>
///     <para>
///         Equality is structural — it is half of a <see cref="PipelineKey" />, and a key compared by
///         reference would miss every hit.
///     </para>
/// </remarks>
public readonly struct RenderOutput : IEquatable<RenderOutput> {
    readonly PixelFormat[] colourFormats;

    /// <summary>The formats of a pass's colour attachments, in order, and its depth format.</summary>
    /// <param name="colourFormats">The colour attachment formats, in the order the shader writes them.</param>
    /// <param name="depthFormat">The depth attachment's format, or
    /// <see cref="PixelFormat.Undefined" /> for a pass with no depth.</param>
    /// <param name="sampleCount">How many samples the attachments have.</param>
    public RenderOutput(
        ReadOnlySpan<PixelFormat> colourFormats,
        PixelFormat depthFormat = PixelFormat.Undefined,
        int sampleCount = 1
    ) {
        this.colourFormats = colourFormats.ToArray();
        DepthFormat = depthFormat;
        SampleCount = sampleCount;
    }

    /// <summary>The colour attachment formats, in order.</summary>
    public ReadOnlySpan<PixelFormat> ColourFormats => colourFormats;

    /// <summary>How many colour attachments the pass has.</summary>
    public int ColourCount => colourFormats?.Length ?? 0;

    /// <summary>The depth attachment's format, or <see cref="PixelFormat.Undefined" />.</summary>
    public PixelFormat DepthFormat { get; }

    /// <summary>How many samples the attachments have.</summary>
    public int SampleCount { get; }

    /// <summary>Whether this describes any attachment at all.</summary>
    public bool IsEmpty => ColourCount == 0 && DepthFormat == PixelFormat.Undefined;

    /// <inheritdoc />
    public bool Equals(RenderOutput other) {
        if (DepthFormat != other.DepthFormat || SampleCount != other.SampleCount) {
            return false;
        }

        return ColourFormats.SequenceEqual(other.ColourFormats);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RenderOutput other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() {
        var hash = new HashCode();
        hash.Add(DepthFormat);
        hash.Add(SampleCount);

        foreach (var format in ColourFormats) {
            hash.Add(format);
        }

        return hash.ToHashCode();
    }

    /// <summary>Whether two outputs describe the same attachments.</summary>
    public static bool operator ==(RenderOutput left, RenderOutput right) => left.Equals(right);

    /// <summary>Whether two outputs describe different attachments.</summary>
    public static bool operator !=(RenderOutput left, RenderOutput right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty
            ? "(nothing)"
            : $"[{string.Join(", ", colourFormats ?? [])}]"
            + (DepthFormat == PixelFormat.Undefined ? "" : $" + {DepthFormat}")
            + (SampleCount > 1 ? $" ×{SampleCount}" : "");
}
