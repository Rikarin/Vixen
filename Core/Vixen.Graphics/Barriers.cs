// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>A buffer changing what it is being used for.</summary>
/// <param name="Buffer">The buffer.</param>
/// <param name="Before">What was using it.</param>
/// <param name="After">What is about to.</param>
public readonly record struct BufferBarrier(BufferHandle Buffer, ResourceState Before, ResourceState After);

/// <summary>A texture changing what it is being used for, and therefore its layout.</summary>
/// <param name="Texture">The texture.</param>
/// <param name="Before">What was using it.</param>
/// <param name="After">What is about to.</param>
/// <param name="BaseMipLevel">The first mip level the barrier covers.</param>
/// <param name="MipLevelCount">How many levels, or <c>0</c> for all of them.</param>
/// <param name="BaseArrayLayer">The first array layer the barrier covers.</param>
/// <param name="ArrayLayerCount">How many layers, or <c>0</c> for all of them.</param>
/// <remarks>
///     Subresource ranges are here rather than implied, because the common case in a real renderer
///     is transitioning one mip of a chain — generating mips, or reading level <c>n</c> while writing
///     level <c>n+1</c> — and a barrier that covers the whole texture there serialises work that did
///     not need serialising.
/// </remarks>
public readonly record struct TextureBarrier(
    TextureHandle Texture,
    ResourceState Before,
    ResourceState After,
    int BaseMipLevel = 0,
    int MipLevelCount = 0,
    int BaseArrayLayer = 0,
    int ArrayLayerCount = 0
);

/// <summary>Everything that changes state at one point in a command list.</summary>
/// <remarks>
///     <para>
///         Submitted as a group rather than one at a time, and that is the whole point: a driver
///         given ten barriers together inserts one pipeline stall, and given them one at a time
///         inserts ten. Batching is not an optimisation the backend can do for us either — by the
///         time it sees the second barrier the first has already been recorded.
///     </para>
///     <para>
///         <c>Vixen.Graphics.RenderGraph</c> builds these automatically for the code that wants that;
///         hand-written barriers stay available for the hot paths that do not.
///     </para>
/// </remarks>
/// <param name="buffers">The buffers changing state.</param>
/// <param name="textures">The textures changing state.</param>
public readonly ref struct BarrierGroup(
    ReadOnlySpan<BufferBarrier> buffers,
    ReadOnlySpan<TextureBarrier> textures
) {
    /// <summary>The buffers changing state.</summary>
    public ReadOnlySpan<BufferBarrier> Buffers { get; } = buffers;

    /// <summary>The textures changing state.</summary>
    public ReadOnlySpan<TextureBarrier> Textures { get; } = textures;

    /// <summary>Whether the group would do anything.</summary>
    public bool IsEmpty => Buffers.IsEmpty && Textures.IsEmpty;
}
