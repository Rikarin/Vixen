// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Editor.TextureGraph;

/// <summary>A texture supplied for one of a plan's external images, and what it was created with.</summary>
/// <param name="Texture">The caller's texture.</param>
/// <param name="Usage">
///     The <see cref="TextureUsage" /> it was created with — the same expression that was passed to
///     <see cref="IGraphicsDevice.CreateTexture" />, and not a wish.
/// </param>
/// <param name="Size">
///     How big the picture actually is, in texels, or the default for a caller that did not say.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>The usage is here because nothing else can answer for it.</b> A
///         <see cref="TextureHandle" /> is an opaque number and
///         <see cref="IGraphicsDevice" /> has no way to describe one back, so an evaluator handed a
///         bare handle cannot know whether copying out of it is legal —
///         <a href="https://github.com/Rikarin/Vixen/issues/722">#722</a>. Declaring it beside the
///         handle turns "the caller forgot" from undefined behaviour into a refusal with a message.
///     </para>
///     <para>
///         ⚠ <b><see cref="Size" /> is here for exactly the same reason, and
///         <a href="https://github.com/Rikarin/Vixen/issues/1000">#1000</a> is what it costs to be
///         without it.</b> An external image is the one place an absolute size enters a plan, so
///         <see cref="TexturePlan.SizeOf" /> cannot answer for one — it reads a level off an image
///         nothing allocates. A dispatch never needed the number, because every kernel clamps its
///         taps to the source's own <c>GetDimensions</c>; a <see cref="TextureOp.Cpu" /> op does,
///         because it is a <c>vkCmdCopyImageToBuffer</c> and a copy has to name an extent. Sized
///         from the plan's nominal level, that copy asked a 16×16 upload for 64×64 and handed the
///         operation 48 rows of a buffer nothing had written.
///     </para>
///     <para>
///         ⚠ <b>What it is not: proof.</b> A declaration that does not match the description the
///         texture was created with is a lie this type cannot detect, and the behaviour is then
///         exactly what it was before — undefined, and green on a unified adapter. Write the two in
///         one place: create the texture and build the <see cref="TextureExternal" /> from the same
///         <c>TextureUsage</c> expression, the way <c>TextureKernelHarness.Upload</c> does.
///     </para>
/// </remarks>
public readonly record struct TextureExternal(TextureHandle Texture, TextureUsage Usage, Int2 Size = default) {
    /// <summary>Whether the caller said how big the picture is.</summary>
    /// <remarks>
    ///     ⚠ <b>Both axes, and positive rather than non-negative.</b> A zero-sized copy is a
    ///     zero-byte staging buffer and an image with no texels in it, which is the shape of "off"
    ///     that reads as a legitimate value everywhere it is passed on.
    /// </remarks>
    public bool HasSize => Size.X > 0 && Size.Y > 0;

    /// <summary>What every external image needs, because a dispatch samples it.</summary>
    public const TextureUsage Sampled = TextureUsage.Sampled;

    /// <summary>
    ///     What an external image a <see cref="TextureOp.Cpu" /> op reads needs on top of that.
    /// </summary>
    /// <remarks>
    ///     A CPU op is a <c>vkCmdCopyImageToBuffer</c> out of the caller's own image, and both that
    ///     copy and the layout transition either side of it require <c>TRANSFER_SRC</c> —
    ///     VUID-vkCmdCopyImageToBuffer-srcImage-00186 and VUID-VkImageMemoryBarrier-oldLayout-01212.
    ///     ⚠ MoltenVK does not enforce usage bits at the Metal layer, so the whole of that is
    ///     invisible on a unified adapter and wrong on a discrete one.
    /// </remarks>
    public const TextureUsage ReadBack = TextureUsage.CopySource;
}
