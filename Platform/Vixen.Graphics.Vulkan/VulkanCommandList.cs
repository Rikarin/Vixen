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
    /// <summary>What a group with no name is opened under.</summary>
    /// <remarks>
    ///     ⚠ <b>A placeholder, not a skip.</b> Declining to open a group a caller asked for is only
    ///     half a decision: the matching <see cref="PopDebugGroup" /> still arrives, and it closes
    ///     whichever group was open instead — so one unnamed pass renests every pass after it under a
    ///     label it has nothing to do with. A capture that is wrong and looks authoritative is worse
    ///     than one with no labels at all, and there is no crash and no validation message to say so.
    ///     A name nobody wrote is worth a line in the tree that says exactly that.
    /// </remarks>
    internal const string UnnamedGroup = "(unnamed)";

    readonly VulkanDevice device;
    readonly Vk api;

    bool inRenderPass;
    bool usingRenderPassObject;
    VulkanPipeline? bound;
    bool disposed;
    int debugGroupDepth;

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
        // ⚠ It does NOT call for a winding compensation. Facing is decided in framebuffer
        // coordinates, which sit two mirrors from the engine's clip space — Vulkan's +Y-down
        // convention and this flip — and two mirrors cancel. `VulkanEnums.ToVulkan(FrontFace)` maps
        // straight through, and its remarks record the time it was inverted to "pay" for this.
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
    public void BuildAccelerationStructure(
        AccelerationStructureHandle target,
        in AccelerationStructureBuildInput input,
        BufferHandle scratch,
        long scratchOffset = 0
    ) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"'{Name}' built an acceleration structure inside a render pass. Builds run outside "
                + "one, like a dispatch."
            );
        }

        // The entry points are loaded only where the capability holds, so this is the capability
        // asked again — a null here is a crash and a refusal is a sentence naming the flag.
        if (device.AccelerationStructures is not { } extension) {
            throw new NotSupportedException(
                "BuildAccelerationStructure needs GraphicsDeviceFeatures.HasRayTracing, which this "
                + "device reports absent. Ask Features.HasRayTracing and take the distance-field tracer."
            );
        }

        var structure = device.Resolve(target);

        // The same helper sizing used, now with real addresses — the one place the input is
        // translated, so sizing and building cannot describe different geometry.
        var geometry = device.DescribeGeometry(input, true, out var primitiveCount);
        var build = VulkanDevice.DescribeBuild(input.Kind, &geometry);
        build.DstAccelerationStructure = structure.Handle;
        build.ScratchData = new() {
            DeviceAddress = device.AddressOf(scratch, "scratch") + (ulong)scratchOffset
        };

        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = primitiveCount };
        var ranges = &range;
        extension.CmdBuildAccelerationStructures(Buffer, 1, &build, &ranges);

        // The command's own epilogue, promised by ICommandList: the structure is readable — by ray
        // queries in compute and fragment work, and by a later top-level build — the moment this
        // returns. Builds are rare and coarse, so one barrier per build costs nothing measurable,
        // and the alternative is a resource-state vocabulary every caller learns before the first
        // query works.
        var barrier = new MemoryBarrier {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.AccelerationStructureWriteBitKhr,
            DstAccessMask = AccessFlags.AccelerationStructureReadBitKhr | AccessFlags.ShaderReadBit
        };

        api.CmdPipelineBarrier(
            Buffer,
            PipelineStageFlags.AccelerationStructureBuildBitKhr,
            PipelineStageFlags.AccelerationStructureBuildBitKhr
            | PipelineStageFlags.ComputeShaderBit
            | PipelineStageFlags.FragmentShaderBit,
            0,
            1,
            &barrier,
            0,
            null,
            0,
            null
        );
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

        var stages = VulkanBarriers.SupportedStages(Kind);
        var accesses = VulkanBarriers.SupportedAccess(Kind);

        var source = PipelineStageFlags.None;
        var destination = PipelineStageFlags.None;

        for (var index = 0; index < bufferCount; index++) {
            var barrier = barriers.Buffers[index];
            var buffer = device.Resolve(barrier.Buffer);
            var families = Ownership(barrier.SourceQueue, barrier.DestinationQueue, buffer.Description.Name);
            var half = HalfOf(families, barrier.SourceQueue);

            if (half != Handover.Acquire) {
                source |= VulkanBarriers.SourceStage(barrier.Before) & stages;
            }

            if (half != Handover.Release) {
                destination |= VulkanBarriers.ToStage(barrier.After) & stages;
            }

            bufferBarriers[index] = new() {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = half == Handover.Acquire
                    ? AccessFlags.None
                    : VulkanBarriers.SourceAccess(barrier.Before) & accesses,
                DstAccessMask = half == Handover.Release
                    ? AccessFlags.None
                    : VulkanBarriers.ToAccess(barrier.After) & accesses,
                SrcQueueFamilyIndex = families.Source,
                DstQueueFamilyIndex = families.Destination,
                Buffer = buffer.Handle,
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        for (var index = 0; index < textureCount; index++) {
            var barrier = barriers.Textures[index];
            var texture = device.Resolve(barrier.Texture);
            var families = Ownership(barrier.SourceQueue, barrier.DestinationQueue, texture.Description.Name);
            var half = HalfOf(families, barrier.SourceQueue);

            if (half != Handover.Acquire) {
                source |= VulkanBarriers.SourceStage(barrier.Before) & stages;
            }

            if (half != Handover.Release) {
                destination |= VulkanBarriers.ToStage(barrier.After) & stages;
            }

            var levels = barrier.MipLevelCount > 0
                ? (uint)barrier.MipLevelCount
                : Vk.RemainingMipLevels;

            var layers = barrier.ArrayLayerCount > 0
                ? (uint)barrier.ArrayLayerCount
                : Vk.RemainingArrayLayers;

            imageBarriers[index] = new() {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = half == Handover.Acquire
                    ? AccessFlags.None
                    : VulkanBarriers.SourceAccess(barrier.Before) & accesses,
                DstAccessMask = half == Handover.Release
                    ? AccessFlags.None
                    : VulkanBarriers.ToAccess(barrier.After) & accesses,

                // ⚠ Never clamped, unlike the stages and the accesses. The two halves of a handover
                // must name identical layouts, and they are recorded on two queues with different
                // capabilities — so a layout narrowed to what one of them can execute would be a
                // release and an acquire that disagree, which is undefined rather than an error.
                OldLayout = VulkanBarriers.ToLayout(barrier.Before),
                NewLayout = VulkanBarriers.ToLayout(barrier.After),
                SrcQueueFamilyIndex = families.Source,
                DstQueueFamilyIndex = families.Destination,
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

        // Neither mask may be empty, and both ends have a stage that is always supported and always
        // means "nothing to wait for" — which is exactly what a mask that clamped away to nothing is
        // saying.
        api.CmdPipelineBarrier(
            Buffer,
            source == PipelineStageFlags.None ? PipelineStageFlags.TopOfPipeBit : source,
            destination == PipelineStageFlags.None ? PipelineStageFlags.BottomOfPipeBit : destination,
            0,
            0,
            null,
            (uint)bufferCount,
            bufferCount > 0 ? bufferBarriers : null,
            (uint)textureCount,
            textureCount > 0 ? imageBarriers : null
        );
    }

    /// <summary>Which half of a queue handover this list is recording, if either.</summary>
    enum Handover {
        /// <summary>Not a handover at all: both stage masks describe work on this queue.</summary>
        None,

        /// <summary>The half on the queue giving the resource up.</summary>
        Release,

        /// <summary>The half on the queue taking it.</summary>
        Acquire
    }

    /// <summary>Which half of a handover the list is recording.</summary>
    /// <param name="families">What <see cref="Ownership" /> resolved the two queues to.</param>
    /// <param name="from">The queue the barrier says currently owns the resource.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two halves are not symmetric, and recording them as if they were is invalid
    ///         usage on any queue that can do less than the graphics queue.</b> A release is recorded
    ///         on the queue giving the resource up, so its <em>destination</em> stages describe work
    ///         on the other queue; an acquire is recorded on the queue taking it, so its
    ///         <em>source</em> stages do. Vulkan ignores the far half of each — but a stage mask is
    ///         still checked against the recording queue's capabilities before it is ignored, so a
    ///         release from compute to graphics naming <c>ColorAttachmentOutput</c> is an error on
    ///         the compute list that records it.
    ///     </para>
    ///     <para>
    ///         Decided from the resolved families rather than from the two <see cref="QueueKind" />s,
    ///         so that two kinds landing on one family are not a handover at all — the collapse is
    ///         what keeps a scheduled frame identical to an unscheduled one on a device with a single
    ///         universal family, which is every device this engine has been developed on.
    ///     </para>
    /// </remarks>
    Handover HalfOf((uint Source, uint Destination) families, QueueKind from) {
        if (families.Source == Vk.QueueFamilyIgnored) {
            return Handover.None;
        }

        return Kind == from ? Handover.Release : Handover.Acquire;
    }

    /// <summary>The family pair a barrier's two queues resolve to, refusing a list at neither end.</summary>
    /// <remarks>
    ///     <para>
    ///         An ownership transfer is a <em>pair</em> of identical barriers, the release recorded on
    ///         the source queue and the acquire on the destination. Recording one on a third queue is
    ///         not an error Vulkan reports: the release never happens, the acquire waits for a handover
    ///         nobody made, and the destination reads whatever the memory held. Refusing it here is the
    ///         only place the mistake is still attached to the list that made it.
    ///     </para>
    ///     <para>
    ///         Checked before the collapse rather than after, so the diagnostic is the same on a
    ///         device where the two kinds share a family as on one where they do not — otherwise the
    ///         bug is invisible on every machine anyone develops on and appears on the discrete card
    ///         in CI.
    ///     </para>
    /// </remarks>
    (uint Source, uint Destination) Ownership(QueueKind source, QueueKind destination, string resource) {
        if (source != destination && Kind != source && Kind != destination) {
            throw new InvalidOperationException(
                $"'{Name}' is a {Kind} list and recorded an ownership transfer of "
                + $"'{resource}' from {source} to {destination}. A transfer is two barriers — the "
                + "release on the source queue's list and the acquire on the destination's — and a "
                + "list at neither end records neither half."
            );
        }

        return device.FamiliesFor(source, destination);
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
    public void ResetQueries(QueryPoolHandle pool, int first, int count) {
        ThrowIfRecorded();

        if (count <= 0) {
            return;
        }

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"Command list '{Name}' reset queries inside a render pass, which Vulkan does not "
                + "allow. Reset the range at the top of the list, before the first pass begins."
            );
        }

        Bounds(pool, first, count);
        api.CmdResetQueryPool(Buffer, device.Resolve(pool).Handle, (uint)first, (uint)count);
    }

    /// <inheritdoc />
    public void WriteTimestamp(QueryPoolHandle pool, int index) {
        ThrowIfRecorded();
        Bounds(pool, index, 1);

        // ⚠ BottomOfPipeBit, which is what makes a pair around a pass mean the pass. A top-of-pipe
        // write records when the GPU *reached* this point in the stream, which on a pipelined device
        // is long before the work in front of it finished — so two top-of-pipe writes around a pass
        // measure how fast the front end consumed commands rather than how long the pass took.
        api.CmdWriteTimestamp(
            Buffer,
            PipelineStageFlags.BottomOfPipeBit,
            device.Resolve(pool).Handle,
            (uint)index
        );
    }

    void Bounds(QueryPoolHandle pool, int first, int count) {
        var resolved = device.Resolve(pool);

        if (first < 0 || count < 0 || first + count > resolved.Count) {
            throw new ArgumentOutOfRangeException(
                nameof(first),
                $"Queries {first}..{first + count - 1} are outside a pool holding {resolved.Count}. "
                + "Vulkan's behaviour there is undefined rather than an error."
            );
        }
    }

    /// <summary>How many labels this list has opened and not yet closed.</summary>
    /// <remarks>
    ///     Counts what was <em>emitted</em>, so it is zero throughout on a device without
    ///     <c>VK_EXT_debug_utils</c> — where neither half of the pair is recorded and the stack is
    ///     balanced by never existing. A test asserts on it because nothing else can see a label
    ///     stack: the commands go straight into a buffer, and the only other reader is a capture.
    /// </remarks>
    internal int DebugGroupDepth => debugGroupDepth;

    /// <inheritdoc />
    public void PushDebugGroup(string name) {
        if (device.DebugUtils is not { } utils) {
            return;
        }

        var text = (byte*)SilkMarshal.StringToPtr(string.IsNullOrEmpty(name) ? UnnamedGroup : name);

        try {
            var label = new DebugUtilsLabelEXT {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = text
            };

            utils.CmdBeginDebugUtilsLabel(Buffer, &label);
            debugGroupDepth++;
        } finally {
            SilkMarshal.Free((nint)text);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Conditional on there being something to close</b>, which is the second half of the
    ///     guarantee <see cref="UnnamedGroup" /> makes. A pop with nothing under it is
    ///     <c>VUID-vkCmdEndDebugUtilsLabelEXT-commandBuffer-01912</c> and, unvalidated, closes a label
    ///     opened by whatever ran before this list — an unbalanced stack that the rest of the frame
    ///     inherits. Swallowing it here keeps a caller's own accounting mistake local to the caller.
    /// </remarks>
    public void PopDebugGroup() {
        if (device.DebugUtils is not { } utils || debugGroupDepth == 0) {
            return;
        }

        debugGroupDepth--;
        utils.CmdEndDebugUtilsLabel(Buffer);
    }

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

            // A depth resolve is a mode, not just a target. Colour above can say "resolve" and mean
            // one thing; depth cannot, because averaging depths yields a surface nothing occupies —
            // so the mode travels with the attachment and the backend never picks one silently.
            if (target.DepthStore == StoreAction.Resolve && target.ResolveView.IsValid) {
                // ⚠ Clamped against the device, not passed through. A mode outside
                // supportedDepthResolveModes is invalid usage under
                // VUID-VkRenderingInfo-pDepthAttachment-06102 rather than a slow path, and lavapipe
                // — which offers SampleZero alone while rendering 4× depth quite happily — is the
                // device that showed it: the frame drew, the layers complained, and what the
                // resolve wrote afterwards was whatever the driver felt like.
                depth.ResolveMode = VulkanEnums.ToVulkan(
                    device.Features.ClampDepthResolveMode(target.ResolveMode)
                );

                depth.ResolveImageView = device.Resolve(target.ResolveView).Handle;
                depth.ResolveImageLayout = ImageLayout.DepthStencilAttachmentOptimal;
            }

            if (hasStencil) {
                stencil = depth with {
                    LoadOp = VulkanEnums.ToVulkan(target.StencilLoad),
                    StoreOp = VulkanEnums.ToVulkan(target.StencilStore),

                    // Stencil is not resolved even when depth is. Vulkan requires the two modes to
                    // agree when both resolve, and there is no meaningful "nearest" for a stencil
                    // value — so the depth resolve stands alone and the stencil samples are dropped.
                    ResolveMode = ResolveModeFlags.None,
                    ResolveImageView = default,
                    ResolveImageLayout = ImageLayout.Undefined
                };
            }
        }

        var info = new RenderingInfo {
            SType = StructureType.RenderingInfo,
            RenderArea = Area(description, extent),
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

        // ⚠ Refused, because until this it was *dropped*. `AttachmentKey` has no notion of a resolve
        // — `StoreAction.Resolve` translates to `VK_ATTACHMENT_STORE_OP_STORE` like any other store
        // — so a pass that asked for one got a multisampled image stored, a resolve target nothing
        // ever wrote, and no validation message at all. Measured on MoltenVK with a 4× red clear:
        // the dynamic-rendering path reads (255, 0, 0, 255) out of the resolve target and this one
        // reads (255, 0, 255, 255), which is whatever the allocation happened to hold, with
        // ErrorCount = 0. A silently wrong picture on the one path that is mandatory rather than
        // optional ([10](../../docs/plan/10-platforms.md) § Android: a large slice of Android is
        // still on Vulkan 1.1), and invisible to both resolve fixtures, which only ever run here
        // with dynamic rendering.
        //
        // Filling it in means `vkCreateRenderPass2` — resolve attachments in the description,
        // `pResolveAttachments` in the subpass, their views in the framebuffer, and
        // `VkSubpassDescriptionDepthStencilResolve` chained for the depth half, which
        // `VkRenderPassCreateInfo` cannot carry at all. That is a rewrite of `RenderPassCache` and
        // is filed rather than smuggled in here; a refusal that names the gap is the honest state
        // until it lands, and it cannot be mistaken for a frame that worked.
        foreach (var attachment in colour) {
            if (attachment.Store == StoreAction.Resolve && attachment.ResolveView.IsValid) {
                throw new NotSupportedException(Unresolvable("A colour attachment"));
            }
        }

        if (description.DepthStencil is { DepthStore: StoreAction.Resolve, ResolveView.IsValid: true }) {
            throw new NotSupportedException(Unresolvable("The depth attachment"));
        }

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
            RenderArea = Area(description, extent),
            ClearValueCount = (uint)total,
            PClearValues = total > 0 ? clears : null
        };

        api.CmdBeginRenderPass(Buffer, &info, SubpassContents.Inline);
    }

    /// <summary>Why a resolve cannot be honoured without dynamic rendering, said once.</summary>
    static string Unresolvable(string what) =>
        $"{what} of '{nameof(StoreAction.Resolve)}' was recorded on a device using VkRenderPass "
        + "objects, and the Vulkan backend cannot resolve there yet — it would store the "
        + "multisampled image and leave the resolve target untouched, with nothing said. Either the "
        + "device has no VK_KHR_dynamic_rendering, or VulkanDeviceOptions.PreferRenderPassObjects "
        + "was set; render single-sampled on such a device until the render-pass path carries "
        + "resolve attachments.";

    /// <summary>The pass's render area: the caller's, clamped inside the attachment, or all of it.</summary>
    /// <remarks>
    ///     The clamp is not politeness — a render area outside the framebuffer is a validation
    ///     error, and the caller computing a tile rectangle against a mis-declared texture should
    ///     fail at its own guard, not here.
    /// </remarks>
    static Rect2D Area(in RenderPassDescription description, Extent2D extent) {
        if (description.RenderArea is not { } area) {
            return new(new(0, 0), extent);
        }

        var x = Math.Clamp(area.X, 0, (int)extent.Width);
        var y = Math.Clamp(area.Y, 0, (int)extent.Height);
        var width = Math.Clamp(area.Width, 0, (int)extent.Width - x);
        var height = Math.Clamp(area.Height, 0, (int)extent.Height - y);

        return new(new(x, y), new((uint)width, (uint)height));
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
