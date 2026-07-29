// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vixen.Core.Mathematics;
using VkViewport = Silk.NET.Vulkan.Viewport;

namespace Vixen.Graphics.Vulkan;

/// <summary>Recording, in Vulkan.</summary>
/// <remarks>
///     <para>
///         Thin by design: almost every method is a translation and a <c>vkCmd…</c>. The state it does
///         keep is what the RHI's contract requires and Vulkan does not check — whether a pass is
///         open, whether a pipeline is bound — because those are the mistakes that produce undefined
///         behaviour rather than an error, and catching them here costs a branch on a path that is
///         already making a driver call.
///     </para>
///     <para>
///         One list per thread per frame. The command pool it allocates from belongs to that pair, so
///         no two threads ever touch the same pool — which is the rule Vulkan states and does not
///         enforce.
///     </para>
/// </remarks>
sealed unsafe class VulkanCommandList : ICommandList {
    readonly VulkanDevice device;
    readonly Vk api;

    bool inRenderPass;
    bool usingRenderPassObject;
    VulkanPipeline? bound;
    bool disposed;

    internal VulkanCommandList(VulkanDevice device, CommandBuffer buffer, QueueKind kind, string name) {
        this.device = device;
        api = device.Api;
        Buffer = buffer;
        Kind = kind;
        Name = name;
        device.Name(ObjectType.CommandBuffer, (ulong)buffer.Handle, name);
    }

    /// <inheritdoc />
    public QueueKind Kind { get; }

    /// <inheritdoc />
    public bool IsRecorded { get; private set; }

    internal CommandBuffer Buffer { get; }

    internal string Name { get; }

    /// <inheritdoc />
    public void Finish() {
        if (IsRecorded) {
            return;
        }

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"Command list '{Name}' finished with a render pass still open. Vulkan requires the pass "
                + "to end in the same list it began in."
            );
        }

        VulkanDevice.Check(api.EndCommandBuffer(Buffer), "vkEndCommandBuffer");
        IsRecorded = true;
    }

    /// <inheritdoc />
    public void BeginRenderPass(in RenderPassDescription description) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"'{description.Name}' began while '{Name}' already had a pass open. Passes do not nest."
            );
        }

        var colour = description.ColourAttachments;

        if (colour.IsEmpty && description.DepthStencil is null) {
            throw new ArgumentException(
                $"Render pass '{description.Name}' has no attachments, so nothing could be rendered."
            );
        }

        var extent = Extent(description);
        PushDebugGroup(description.Name);

        if (device.UsesDynamicRendering) {
            BeginDynamic(description, extent);
            usingRenderPassObject = false;
        } else {
            BeginWithPassObject(description, extent);
            usingRenderPassObject = true;
        }

        inRenderPass = true;

        // The viewport and scissor are dynamic state on every pipeline this backend builds, so a
        // pass that set neither would draw nothing. Defaulting them to the whole attachment is what
        // every caller wants and what the alternative — an empty screen and no error — does not.
        SetViewport(new(0, 0, extent.Width, extent.Height));
        SetScissor(new(0, 0, (int)extent.Width, (int)extent.Height));
    }

    /// <inheritdoc />
    public void EndRenderPass() {
        ThrowIfRecorded();

        if (!inRenderPass) {
            throw new InvalidOperationException($"'{Name}' ended a render pass that was never begun.");
        }

        if (usingRenderPassObject) {
            api.CmdEndRenderPass(Buffer);
        } else if (device.DynamicRendering is { } extension) {
            extension.CmdEndRendering(Buffer);
        } else {
            api.CmdEndRendering(Buffer);
        }

        inRenderPass = false;
        bound = null;
        PopDebugGroup();
    }

    /// <inheritdoc />
    public void SetViewport(in Vixen.Core.Mathematics.Viewport viewport) {
        ThrowIfRecorded();

        // Y is flipped: Vulkan's clip space has +Y down and the engine's convention is +Y up
        // (Core/Vixen.Core.Mathematics/Conventions.md). A negative-height viewport is the standard way
        // to express that, is core since 1.1, and avoids a flip in every vertex shader.
        //
        // ⚠ It also reverses which winding is front, because facing is decided from the signed area
        // in framebuffer coordinates and this mirrors them. `VulkanEnums.ToVulkan(FrontFace)` is
        // where that is paid for, and its remarks say what goes wrong when it is not.
        var vk = new VkViewport {
            X = viewport.X,
            Y = viewport.Y + viewport.Height,
            Width = viewport.Width,
            Height = -viewport.Height,
            MinDepth = viewport.MinDepth,
            MaxDepth = viewport.MaxDepth
        };

        api.CmdSetViewport(Buffer, 0, 1, &vk);
    }

    /// <inheritdoc />
    public void SetScissor(in ScissorRect scissor) {
        ThrowIfRecorded();

        var rect = new Rect2D {
            Offset = new(scissor.X, scissor.Y),
            Extent = new((uint)Math.Max(0, scissor.Width), (uint)Math.Max(0, scissor.Height))
        };

        api.CmdSetScissor(Buffer, 0, 1, &rect);
    }

    /// <inheritdoc />
    public void SetBlendConstant(in Color4 colour) {
        ThrowIfRecorded();
        var constants = stackalloc float[4] { colour.R, colour.G, colour.B, colour.A };
        api.CmdSetBlendConstants(Buffer, constants);
    }

    /// <inheritdoc />
    public void SetStencilReference(uint reference) {
        ThrowIfRecorded();
        api.CmdSetStencilReference(Buffer, StencilFaceFlags.FaceFrontAndBack, reference);
    }

    /// <inheritdoc />
    public void BindPipeline(PipelineHandle pipeline) {
        ThrowIfRecorded();
        var resolved = device.Resolve(pipeline);
        api.CmdBindPipeline(Buffer, resolved.BindPoint, resolved.Handle);
        bound = resolved;
    }

    /// <inheritdoc />
    public void BindDescriptorSet(
        DescriptorSetSlot slot,
        DescriptorSetHandle descriptors,
        ReadOnlySpan<uint> dynamicOffsets = default
    ) {
        ThrowIfRecorded();
        var pipeline = RequirePipeline("BindDescriptorSet");
        var set = device.Resolve(descriptors).Handle;

        fixed (uint* offsets = dynamicOffsets) {
            api.CmdBindDescriptorSets(
                Buffer,
                pipeline.BindPoint,
                pipeline.Layout,
                (uint)slot,
                1,
                &set,
                (uint)dynamicOffsets.Length,
                dynamicOffsets.IsEmpty ? null : offsets
            );
        }
    }

    /// <inheritdoc />
    public void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data) {
        ThrowIfRecorded();
        var pipeline = RequirePipeline("PushConstants");

        fixed (byte* bytes = data) {
            api.CmdPushConstants(
                Buffer,
                pipeline.Layout,
                VulkanEnums.ToVulkan(stages),
                (uint)offset,
                (uint)data.Length,
                bytes
            );
        }
    }

    /// <inheritdoc />
    public void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0) {
        ThrowIfRecorded();
        var handle = device.Resolve(buffer).Handle;
        var start = (ulong)offset;
        api.CmdBindVertexBuffers(Buffer, (uint)slot, 1, &handle, &start);
    }

    /// <inheritdoc />
    public void BindIndexBuffer(BufferHandle buffer, IndexFormat format = IndexFormat.UInt16, long offset = 0) {
        ThrowIfRecorded();

        api.CmdBindIndexBuffer(
            Buffer,
            device.Resolve(buffer).Handle,
            (ulong)offset,
            VulkanEnums.ToVulkan(format)
        );
    }

    /// <inheritdoc />
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) {
        RequireRenderPass("Draw");
        RequirePipeline("Draw");
        api.CmdDraw(Buffer, (uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
    }

    /// <inheritdoc />
    public void DrawIndexed(
        int indexCount,
        int instanceCount = 1,
        int firstIndex = 0,
        int vertexOffset = 0,
        int firstInstance = 0
    ) {
        RequireRenderPass("DrawIndexed");
        RequirePipeline("DrawIndexed");

        api.CmdDrawIndexed(
            Buffer,
            (uint)indexCount,
            (uint)instanceCount,
            (uint)firstIndex,
            vertexOffset,
            (uint)firstInstance
        );
    }

    /// <inheritdoc />
    public void DrawIndexedIndirect(BufferHandle arguments, long offset = 0, int drawCount = 1, int stride = 20) {
        RequireRenderPass("DrawIndexedIndirect");
        RequirePipeline("DrawIndexedIndirect");

        if (drawCount > 1 && !device.Features.HasMultiDrawIndirect) {
            throw new InvalidOperationException(
                $"{drawCount} indirect draws were asked for on a device without multi-draw-indirect. "
                + "Issue them one at a time, which is what the capability flag is there to decide."
            );
        }

        api.CmdDrawIndexedIndirect(
            Buffer,
            device.Resolve(arguments).Handle,
            (ulong)offset,
            (uint)drawCount,
            (uint)stride
        );
    }

    /// <inheritdoc />
    public void DrawIndexedIndirectCount(
        BufferHandle arguments,
        BufferHandle count,
        long offset = 0,
        long countOffset = 0,
        int maxDrawCount = 1,
        int stride = 20
    ) {
        RequireRenderPass("DrawIndexedIndirectCount");
        RequirePipeline("DrawIndexedIndirectCount");

        // The entry point is loaded only where the extension was enabled, so this is the same
        // question the capability answers — asked again, here, because a null here is a crash and a
        // refusal is a sentence naming the flag.
        if (device.DrawIndirectCount is not { } indirect) {
            throw new InvalidOperationException(
                "DrawIndexedIndirectCount needs GraphicsDeviceFeatures.HasDrawIndirectCount, which "
                + "this device reports absent. The fallback is DrawIndexedIndirect at the run's "
                + "maximum length with the culled arguments zeroed, which is what GpuDrawArguments "
                + "writes when the capability is missing."
            );
        }

        indirect.CmdDrawIndexedIndirectCount(
            Buffer,
            device.Resolve(arguments).Handle,
            (ulong)offset,
            device.Resolve(count).Handle,
            (ulong)countOffset,
            (uint)maxDrawCount,
            (uint)stride
        );
    }

    /// <inheritdoc />
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"'{Name}' dispatched inside a render pass. Compute does not run in one on any API."
            );
        }

        RequirePipeline("Dispatch");
        api.CmdDispatch(Buffer, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
    }

    /// <inheritdoc />
    public void DispatchIndirect(BufferHandle arguments, long offset = 0) {
        ThrowIfRecorded();
        RequirePipeline("DispatchIndirect");
        api.CmdDispatchIndirect(Buffer, device.Resolve(arguments).Handle, (ulong)offset);
    }

    /// <inheritdoc />
    public void Barrier(in BarrierGroup barriers) {
        ThrowIfRecorded();

        if (barriers.IsEmpty) {
            return;
        }

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"'{Name}' recorded a barrier inside a render pass. Vulkan permits only self-dependency "
                + "barriers there, which the RHI does not expose — end the pass first."
            );
        }

        var bufferCount = barriers.Buffers.Length;
        var textureCount = barriers.Textures.Length;
        var bufferBarriers = stackalloc BufferMemoryBarrier[Math.Max(1, bufferCount)];
        var imageBarriers = stackalloc ImageMemoryBarrier[Math.Max(1, textureCount)];

        var source = PipelineStageFlags.None;
        var destination = PipelineStageFlags.None;

        for (var index = 0; index < bufferCount; index++) {
            var barrier = barriers.Buffers[index];
            var buffer = device.Resolve(barrier.Buffer);
            source |= VulkanBarriers.ToStage(barrier.Before);
            destination |= VulkanBarriers.ToStage(barrier.After);

            bufferBarriers[index] = new() {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = VulkanBarriers.ToAccess(barrier.Before),
                DstAccessMask = VulkanBarriers.ToAccess(barrier.After),
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer.Handle,
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        for (var index = 0; index < textureCount; index++) {
            var barrier = barriers.Textures[index];
            var texture = device.Resolve(barrier.Texture);
            source |= VulkanBarriers.ToStage(barrier.Before);
            destination |= VulkanBarriers.ToStage(barrier.After);

            var levels = barrier.MipLevelCount > 0
                ? (uint)barrier.MipLevelCount
                : Vk.RemainingMipLevels;

            var layers = barrier.ArrayLayerCount > 0
                ? (uint)barrier.ArrayLayerCount
                : Vk.RemainingArrayLayers;

            imageBarriers[index] = new() {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = VulkanBarriers.ToAccess(barrier.Before),
                DstAccessMask = VulkanBarriers.ToAccess(barrier.After),
                OldLayout = VulkanBarriers.ToLayout(barrier.Before),
                NewLayout = VulkanBarriers.ToLayout(barrier.After),
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = texture.Handle,
                SubresourceRange = new() {
                    AspectMask = VulkanFormats.AspectOf(texture.Description.Format),
                    BaseMipLevel = (uint)barrier.BaseMipLevel,
                    LevelCount = levels,
                    BaseArrayLayer = (uint)barrier.BaseArrayLayer,
                    LayerCount = layers
                }
            };
        }

        api.CmdPipelineBarrier(
            Buffer,
            source,
            destination,
            0,
            0,
            null,
            (uint)bufferCount,
            bufferCount > 0 ? bufferBarriers : null,
            (uint)textureCount,
            textureCount > 0 ? imageBarriers : null
        );
    }

    /// <inheritdoc />
    public void CopyBuffer(
        BufferHandle source,
        long sourceOffset,
        BufferHandle destination,
        long destinationOffset,
        long size
    ) {
        ThrowIfRecorded();

        var region = new BufferCopy {
            SrcOffset = (ulong)sourceOffset,
            DstOffset = (ulong)destinationOffset,
            Size = (ulong)size
        };

        api.CmdCopyBuffer(
            Buffer,
            device.Resolve(source).Handle,
            device.Resolve(destination).Handle,
            1,
            &region
        );
    }

    /// <inheritdoc />
    public void CopyBufferToTexture(
        BufferHandle source,
        long sourceOffset,
        in TextureRegion destination,
        Int3 size
    ) {
        ThrowIfRecorded();
        var texture = device.Resolve(destination.Texture);
        var region = Region(texture, destination, sourceOffset, size);

        api.CmdCopyBufferToImage(
            Buffer,
            device.Resolve(source).Handle,
            texture.Handle,
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );
    }

    /// <inheritdoc />
    public void CopyTextureToBuffer(
        in TextureRegion source,
        Int3 size,
        BufferHandle destination,
        long destinationOffset
    ) {
        ThrowIfRecorded();
        var texture = device.Resolve(source.Texture);
        var region = Region(texture, source, destinationOffset, size);

        api.CmdCopyImageToBuffer(
            Buffer,
            texture.Handle,
            ImageLayout.TransferSrcOptimal,
            device.Resolve(destination).Handle,
            1,
            &region
        );
    }

    /// <inheritdoc />
    public void CopyTexture(in TextureRegion source, in TextureRegion destination, Int3 size) {
        ThrowIfRecorded();
        var from = device.Resolve(source.Texture);
        var to = device.Resolve(destination.Texture);

        var region = new ImageCopy {
            SrcSubresource = Layers(from, source),
            SrcOffset = new(source.Origin.X, source.Origin.Y, source.Origin.Z),
            DstSubresource = Layers(to, destination),
            DstOffset = new(destination.Origin.X, destination.Origin.Y, destination.Origin.Z),
            Extent = new((uint)size.X, (uint)size.Y, (uint)size.Z)
        };

        api.CmdCopyImage(
            Buffer,
            from.Handle,
            ImageLayout.TransferSrcOptimal,
            to.Handle,
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );
    }

    /// <inheritdoc />
    public void PushDebugGroup(string name) {
        if (device.DebugUtils is not { } utils || string.IsNullOrEmpty(name)) {
            return;
        }

        var text = (byte*)SilkMarshal.StringToPtr(name);

        try {
            var label = new DebugUtilsLabelEXT {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = text
            };

            utils.CmdBeginDebugUtilsLabel(Buffer, &label);
        } finally {
            SilkMarshal.Free((nint)text);
        }
    }

    /// <inheritdoc />
    public void PopDebugGroup() => device.DebugUtils?.CmdEndDebugUtilsLabel(Buffer);

    /// <inheritdoc />
    public void InsertDebugMarker(string name) {
        if (device.DebugUtils is not { } utils || string.IsNullOrEmpty(name)) {
            return;
        }

        var text = (byte*)SilkMarshal.StringToPtr(name);

        try {
            var label = new DebugUtilsLabelEXT {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = text
            };

            utils.CmdInsertDebugUtilsLabel(Buffer, &label);
        } finally {
            SilkMarshal.Free((nint)text);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // Nothing to return: the pool this came from is reset wholesale at the start of its frame,
        // which is both cheaper than freeing buffers individually and the reason the pool is created
        // transient.
    }

    void BeginDynamic(in RenderPassDescription description, Extent2D extent) {
        var colour = description.ColourAttachments;
        var attachments = stackalloc RenderingAttachmentInfo[Math.Max(1, colour.Length)];

        for (var index = 0; index < colour.Length; index++) {
            var attachment = colour[index];
            var view = device.Resolve(attachment.View);

            attachments[index] = new() {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view.Handle,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = VulkanEnums.ToVulkan(attachment.Load),
                StoreOp = VulkanEnums.ToVulkan(attachment.Store),
                ClearValue = new() {
                    Color = new(
                        attachment.ClearColour.R,
                        attachment.ClearColour.G,
                        attachment.ClearColour.B,
                        attachment.ClearColour.A
                    )
                }
            };

            if (attachment.Store == StoreAction.Resolve && attachment.ResolveView.IsValid) {
                attachments[index].ResolveMode = ResolveModeFlags.AverageBit;
                attachments[index].ResolveImageView = device.Resolve(attachment.ResolveView).Handle;
                attachments[index].ResolveImageLayout = ImageLayout.ColorAttachmentOptimal;
            }
        }

        var depth = new RenderingAttachmentInfo();
        var stencil = new RenderingAttachmentInfo();
        var hasStencil = false;

        if (description.DepthStencil is { } target) {
            var view = device.Resolve(target.View);

            var layout = target.IsReadOnly
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            depth = new() {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view.Handle,
                ImageLayout = layout,
                LoadOp = VulkanEnums.ToVulkan(target.DepthLoad),
                StoreOp = VulkanEnums.ToVulkan(target.DepthStore),
                ClearValue = new() { DepthStencil = new(target.ClearDepth, target.ClearStencil) }
            };

            hasStencil = view.Format.HasStencil();

            if (hasStencil) {
                stencil = depth with {
                    LoadOp = VulkanEnums.ToVulkan(target.StencilLoad),
                    StoreOp = VulkanEnums.ToVulkan(target.StencilStore)
                };
            }
        }

        var info = new RenderingInfo {
            SType = StructureType.RenderingInfo,
            RenderArea = new(new(0, 0), extent),
            LayerCount = 1,
            ColorAttachmentCount = (uint)colour.Length,
            PColorAttachments = colour.Length > 0 ? attachments : null,
            PDepthAttachment = description.DepthStencil is null ? null : &depth,
            PStencilAttachment = hasStencil ? &stencil : null
        };

        if (device.DynamicRendering is { } extension) {
            extension.CmdBeginRendering(Buffer, &info);
        } else {
            api.CmdBeginRendering(Buffer, &info);
        }
    }

    void BeginWithPassObject(in RenderPassDescription description, Extent2D extent) {
        var colour = description.ColourAttachments;
        var total = colour.Length + (description.DepthStencil is null ? 0 : 1);
        var keys = new AttachmentKey[colour.Length];
        var views = new ImageView[total];
        var clears = stackalloc ClearValue[Math.Max(1, total)];

        for (var index = 0; index < colour.Length; index++) {
            var attachment = colour[index];
            var view = device.Resolve(attachment.View);
            var texture = device.Resolve(view.Texture);

            keys[index] = new(
                VulkanFormats.ToVulkan(view.Format),
                VulkanFormats.ToSampleCount(texture.Description.SampleCount),
                VulkanEnums.ToVulkan(attachment.Load),
                VulkanEnums.ToVulkan(attachment.Store)
            );

            views[index] = view.Handle;

            clears[index] = new() {
                Color = new(
                    attachment.ClearColour.R,
                    attachment.ClearColour.G,
                    attachment.ClearColour.B,
                    attachment.ClearColour.A
                )
            };
        }

        AttachmentKey? depthKey = null;

        if (description.DepthStencil is { } target) {
            var view = device.Resolve(target.View);
            var texture = device.Resolve(view.Texture);

            depthKey = new(
                VulkanFormats.ToVulkan(view.Format),
                VulkanFormats.ToSampleCount(texture.Description.SampleCount),
                VulkanEnums.ToVulkan(target.DepthLoad),
                VulkanEnums.ToVulkan(target.DepthStore),
                VulkanEnums.ToVulkan(target.StencilLoad),
                VulkanEnums.ToVulkan(target.StencilStore)
            );

            views[colour.Length] = view.Handle;

            clears[colour.Length] = new() {
                DepthStencil = new(target.ClearDepth, target.ClearStencil)
            };
        }

        var pass = device.RenderPasses.Get(new(keys, depthKey));
        var framebuffer = device.RenderPasses.GetFramebuffer(pass, views, extent.Width, extent.Height);

        var info = new RenderPassBeginInfo {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = framebuffer,
            RenderArea = new(new(0, 0), extent),
            ClearValueCount = (uint)total,
            PClearValues = total > 0 ? clears : null
        };

        api.CmdBeginRenderPass(Buffer, &info, SubpassContents.Inline);
    }

    Extent2D Extent(in RenderPassDescription description) {
        var view = description.ColourAttachments.IsEmpty
            ? device.Resolve(description.DepthStencil!.Value.View)
            : device.Resolve(description.ColourAttachments[0].View);

        var texture = device.Resolve(view.Texture);

        // The mip level's size, not the texture's: a pass that renders into level 2 of a chain has a
        // render area a quarter the width, and using the base size would scissor away three quarters
        // of it and produce a picture that is right in one corner.
        return new(
            (uint)Math.Max(1, texture.Description.Width >> view.BaseMipLevel),
            (uint)Math.Max(1, texture.Description.Height >> view.BaseMipLevel)
        );
    }

    static BufferImageCopy Region(VulkanTexture texture, in TextureRegion region, long offset, Int3 size) =>
        new() {
            BufferOffset = (ulong)offset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = Layers(texture, region),
            ImageOffset = new(region.Origin.X, region.Origin.Y, region.Origin.Z),
            ImageExtent = new((uint)size.X, (uint)size.Y, (uint)size.Z)
        };

    static ImageSubresourceLayers Layers(VulkanTexture texture, in TextureRegion region) => new() {
        AspectMask = VulkanFormats.AspectOf(texture.Description.Format),
        MipLevel = (uint)region.MipLevel,
        BaseArrayLayer = (uint)region.ArrayLayer,
        LayerCount = 1
    };

    VulkanPipeline RequirePipeline(string call) =>
        bound ?? throw new InvalidOperationException(
            $"{call} on '{Name}' with no pipeline bound. Vulkan does not check this and the result is "
            + "undefined rather than an error."
        );

    void RequireRenderPass(string call) {
        ThrowIfRecorded();

        if (!inRenderPass) {
            throw new InvalidOperationException(
                $"{call} on '{Name}' outside a render pass. Every API requires one, and Vulkan's "
                + "diagnosis of the omission arrives as a crash inside the driver."
            );
        }
    }

    void ThrowIfRecorded() {
        if (IsRecorded) {
            throw new InvalidOperationException(
                $"'{Name}' was recorded into after Finish(). A finished list is immutable until it is "
                + "returned to its pool."
            );
        }
    }
}
