// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.WebGPU;

/// <summary>Recording, deferred.</summary>
/// <remarks>
///     <para>
///         Every call is translated here and written into a flat stream;
///         <see cref="WebGpuQueue" /> replays it through an encoder at submit. See
///         <see cref="WebGpuCommand" /> for why, and for what that buys.
///     </para>
///     <para>
///         <b>Handles are resolved at record time, not at replay.</b> A recorded command names a
///         WebGPU object, the way a driver's recorded command does — so a resource destroyed between
///         recording and submitting is still the one that was drawn with, and a handle that was
///         already stale is caught at the call site that used it rather than in the middle of a
///         replay loop with no idea who put it there.
///     </para>
///     <para>
///         The validation is the same set <c>Vixen.Graphics.Null</c> catches, and it is here for the
///         same reason: a draw outside a pass, a copy inside one, a list finished mid-pass. WebGPU
///         would reject each of them too, in an implementation's own words and a frame later.
///     </para>
/// </remarks>
sealed class WebGpuCommandList : ICommandList {
    readonly WebGpuDevice device;
    readonly string name;

    readonly List<WebGpuCommand> commands = [];
    readonly List<WgpuColourAttachment> colourAttachments = [];
    readonly List<WgpuDepthStencilAttachment> depthAttachments = [];
    readonly List<WgpuImageCopyTexture> textureCopies = [];
    readonly List<WgpuImageCopyBuffer> bufferCopies = [];
    readonly List<uint> dynamicOffsets = [];
    readonly List<float> floats = [];
    readonly List<string> labels = [];
    readonly List<byte> pushConstantBlocks = [];
    readonly byte[] pushConstantShadow = new byte[WebGpuCapabilities.PushConstantSize];

    bool inRenderPass;
    int groupDepth;
    int passGroupDepth;
    bool disposed;

    internal WebGpuCommandList(WebGpuDevice device, QueueKind kind, string name) {
        this.device = device;
        this.name = name;
        Kind = kind;
    }

    /// <inheritdoc />
    public QueueKind Kind { get; }

    /// <inheritdoc />
    public bool IsRecorded { get; private set; }

    /// <summary>Whether this list has already been handed to a queue.</summary>
    internal bool Submitted { get; private set; }

    internal string Name => name;

    internal List<WebGpuCommand> Commands => commands;

    internal List<WgpuColourAttachment> ColourAttachments => colourAttachments;

    internal List<WgpuDepthStencilAttachment> DepthAttachments => depthAttachments;

    internal List<WgpuImageCopyTexture> TextureCopies => textureCopies;

    internal List<WgpuImageCopyBuffer> BufferCopies => bufferCopies;

    internal List<uint> DynamicOffsets => dynamicOffsets;

    internal List<float> Floats => floats;

    internal List<string> Labels => labels;

    internal List<byte> PushConstantBlocks => pushConstantBlocks;

    /// <inheritdoc />
    public void Finish() {
        ThrowIfDisposed();

        if (IsRecorded) {
            return;
        }

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"Command list '{name}' was finished inside a render pass. Every pass has to be ended "
                + "before the list is, or the backend has nothing to close it with."
            );
        }

        if (groupDepth != 0) {
            throw new InvalidOperationException(
                $"Command list '{name}' was finished with {groupDepth} debug group(s) still open, which "
                + "makes a capture unreadable from that point on."
            );
        }

        IsRecorded = true;
    }

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void BeginRenderPass(in RenderPassDescription description) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                $"'{description.Name}' began while '{name}' already had a pass open. Passes do not nest — "
                + "no API allows it, and a tiled GPU could not express it at all."
            );
        }

        if (description.ColourAttachments.IsEmpty && description.DepthStencil is null) {
            throw new InvalidOperationException(
                $"Render pass '{description.Name}' has no attachments, so it renders to nothing."
            );
        }

        var first = colourAttachments.Count;

        foreach (var attachment in description.ColourAttachments) {
            var view = device.ResolveView(attachment.View, "a colour attachment");

            // WebGPU wants the resolve target on the attachment and the store operation to say what
            // becomes of the samples. StoreAction.Resolve therefore turns into "discard, and here is
            // where to resolve to", which is the same instruction spelled the other way round.
            var resolve = attachment.Store == StoreAction.Resolve && attachment.ResolveView.IsValid
                ? device.ResolveView(attachment.ResolveView, "a resolve target")
                : WebGpuObject.Null;

            colourAttachments.Add(
                new(
                    view,
                    resolve,
                    WebGpuConversions.ToWebGpu(attachment.Load),
                    WebGpuConversions.ToWebGpu(attachment.Store),
                    attachment.ClearColour.R,
                    attachment.ClearColour.G,
                    attachment.ClearColour.B,
                    attachment.ClearColour.A
                )
            );
        }

        var depthIndex = -1;

        if (description.DepthStencil is { } depth) {
            // WebGPU has no depth resolve: a render pass depth attachment carries no resolve target,
            // and there is no equivalent of VK_KHR_depth_stencil_resolve. Refusing here is the whole
            // point — a silently dropped depth resolve leaves the target holding whatever it held
            // last frame, which reads as a picture that is almost right rather than as an error.
            if (depth.DepthStore == StoreAction.Resolve && depth.ResolveView.IsValid) {
                throw new NotSupportedException(
                    $"Render pass '{description.Name}' resolves its depth attachment, which WebGPU "
                    + "cannot do — the API has no resolve target for depth. Render depth "
                    + "single-sampled, or resolve it with a compute pass."
                );
            }

            depthAttachments.Add(
                new(
                    device.ResolveView(depth.View, "a depth-stencil attachment"),
                    WebGpuConversions.ToWebGpu(depth.DepthLoad),
                    WebGpuConversions.ToWebGpu(depth.DepthStore),
                    depth.ClearDepth,
                    depth.IsReadOnly,
                    WebGpuConversions.ToWebGpu(depth.StencilLoad),
                    WebGpuConversions.ToWebGpu(depth.StencilStore),
                    depth.ClearStencil,
                    depth.IsReadOnly
                )
            );

            depthIndex = depthAttachments.Count - 1;
        }

        inRenderPass = true;
        passGroupDepth = groupDepth;

        commands.Add(
            new(
                WebGpuCommandKind.BeginRenderPass,
                first,
                description.ColourAttachments.Length,
                depthIndex,
                Label(description.Name)
            )
        );
    }

    /// <inheritdoc />
    public void EndRenderPass() {
        ThrowIfRecorded();

        if (!inRenderPass) {
            throw new InvalidOperationException("EndRenderPass without a matching BeginRenderPass.");
        }

        // A debug group may not straddle a pass boundary. WebGPU pushes groups onto a *pass* encoder
        // while a pass is open and onto the command encoder otherwise, and the two are separate
        // stacks — so a group opened outside and closed inside is not a mismatched pop the caller
        // would see, it is two unbalanced stacks and a capture that stops making sense. Caught here,
        // where the name of the list is still to hand.
        if (groupDepth != passGroupDepth) {
            throw new InvalidOperationException(
                $"A render pass in '{name}' ended with {groupDepth - passGroupDepth} debug group(s) opened "
                + "inside it still open. A group has to be closed in the pass that opened it."
            );
        }

        inRenderPass = false;
        commands.Add(new(WebGpuCommandKind.EndRenderPass));
    }

    // ── State ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void SetViewport(in Viewport viewport) {
        ThrowIfNotInPass(nameof(SetViewport));

        var index = floats.Count;
        floats.Add(viewport.X);
        floats.Add(viewport.Y);
        floats.Add(viewport.Width);
        floats.Add(viewport.Height);
        floats.Add(viewport.MinDepth);
        floats.Add(viewport.MaxDepth);

        commands.Add(new(WebGpuCommandKind.SetViewport, index));
    }

    /// <inheritdoc />
    public void SetScissor(in ScissorRect scissor) {
        ThrowIfNotInPass(nameof(SetScissor));

        if (scissor.X < 0 || scissor.Y < 0 || scissor.Width < 0 || scissor.Height < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(scissor),
                $"A scissor rectangle of {scissor.Width}×{scissor.Height} at ({scissor.X}, {scissor.Y}) has "
                + "a negative component. WebGPU's scissor is unsigned, so a negative one would wrap to an "
                + "enormous positive one and clip nothing."
            );
        }

        commands.Add(
            new(WebGpuCommandKind.SetScissor, scissor.X, scissor.Y, scissor.Width, scissor.Height)
        );
    }

    /// <inheritdoc />
    public void SetBlendConstant(in Color4 colour) {
        ThrowIfNotInPass(nameof(SetBlendConstant));

        var index = floats.Count;
        floats.Add(colour.R);
        floats.Add(colour.G);
        floats.Add(colour.B);
        floats.Add(colour.A);

        commands.Add(new(WebGpuCommandKind.SetBlendConstant, index));
    }

    /// <inheritdoc />
    public void SetStencilReference(uint reference) {
        ThrowIfNotInPass(nameof(SetStencilReference));
        commands.Add(new(WebGpuCommandKind.SetStencilReference, unchecked((int)reference)));
    }

    /// <inheritdoc />
    public void BindPipeline(PipelineHandle pipeline) {
        ThrowIfRecorded();

        var resolved = device.ResolvePipeline(pipeline);

        if (resolved.IsCompute && inRenderPass) {
            throw new InvalidOperationException(
                $"Compute pipeline '{resolved.Name}' was bound inside a render pass. WebGPU binds a "
                + "compute pipeline in a compute pass, and the two do not overlap."
            );
        }

        if (!resolved.IsCompute && !inRenderPass) {
            throw new InvalidOperationException(
                $"Graphics pipeline '{resolved.Name}' was bound outside a render pass, so there is no pass "
                + "encoder to bind it to."
            );
        }

        commands.Add(
            new(
                WebGpuCommandKind.BindPipeline,
                resolved.IsCompute ? 1 : 0,
                resolved.PushConstantGroup,
                Object0: resolved.Handle
            )
        );
    }

    /// <inheritdoc />
    public void BindDescriptorSet(
        DescriptorSetSlot slot,
        DescriptorSetHandle descriptors,
        ReadOnlySpan<uint> dynamicOffsets = default
    ) {
        ThrowIfRecorded();

        var set = device.ResolveDescriptorSet(descriptors);

        if (!set.Handle.IsValid) {
            throw new InvalidOperationException(
                $"Descriptor set '{set.Name}' was bound before every one of its {set.Entries.Length} "
                + "bindings had a resource. A WebGPU bind group is built whole or not at all, so an "
                + "incomplete set has nothing to bind — call UpdateDescriptorSet for the rest of it."
            );
        }

        var start = this.dynamicOffsets.Count;

        foreach (var offset in dynamicOffsets) {
            this.dynamicOffsets.Add(offset);
        }

        commands.Add(
            new(
                WebGpuCommandKind.BindDescriptorSet,
                (int)slot,
                start,
                dynamicOffsets.Length,
                Object0: set.Handle
            )
        );
    }

    /// <inheritdoc />
    public void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data) {
        ThrowIfRecorded();

        if (offset < 0 || offset + data.Length > pushConstantShadow.Length) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Writing {data.Length} push-constant bytes at {offset} runs past the "
                + $"{pushConstantShadow.Length}-byte block. WebGPU has no push constants at all; the "
                + "backend emulates a block of exactly that size — see PushConstantRing."
            );
        }

        // The whole block is snapshotted rather than the changed bytes, because the emulation binds a
        // uniform buffer range and a range has to hold everything the shader reads. A partial write
        // that only carried its own bytes would leave the rest of the block as whatever the previous
        // draw put there — which is what push constants do on a real API and is not what a uniform
        // buffer does.
        data.CopyTo(pushConstantShadow.AsSpan(offset));

        var index = pushConstantBlocks.Count / pushConstantShadow.Length;
        pushConstantBlocks.AddRange(pushConstantShadow);

        commands.Add(new(WebGpuCommandKind.PushConstants, index));
    }

    /// <inheritdoc />
    public void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0) {
        ThrowIfNotInPass(nameof(BindVertexBuffer));
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        var resolved = device.ResolveBuffer(buffer, "a vertex buffer");
        ThrowIfPastEnd(resolved, offset, "a vertex buffer");

        commands.Add(
            new(
                WebGpuCommandKind.BindVertexBuffer,
                slot,
                E: offset,
                F: resolved.AllocatedSize - offset,
                Object0: resolved.Handle
            )
        );
    }

    /// <inheritdoc />
    public void BindIndexBuffer(BufferHandle buffer, IndexFormat format = IndexFormat.UInt16, long offset = 0) {
        ThrowIfNotInPass(nameof(BindIndexBuffer));

        var resolved = device.ResolveBuffer(buffer, "an index buffer");
        ThrowIfPastEnd(resolved, offset, "an index buffer");

        commands.Add(
            new(
                WebGpuCommandKind.BindIndexBuffer,
                (int)WebGpuConversions.ToWebGpu(format),
                E: offset,
                F: resolved.AllocatedSize - offset,
                Object0: resolved.Handle
            )
        );
    }

    // ── Drawing ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) {
        ThrowIfNotInPass(nameof(Draw));
        commands.Add(new(WebGpuCommandKind.Draw, vertexCount, instanceCount, firstVertex, firstInstance));
    }

    /// <inheritdoc />
    public void DrawIndexed(
        int indexCount,
        int instanceCount = 1,
        int firstIndex = 0,
        int vertexOffset = 0,
        int firstInstance = 0
    ) {
        ThrowIfNotInPass(nameof(DrawIndexed));

        commands.Add(
            new(
                WebGpuCommandKind.DrawIndexed,
                indexCount,
                instanceCount,
                firstIndex,
                firstInstance,
                vertexOffset
            )
        );
    }

    /// <inheritdoc />
    public void DrawIndexedIndirect(BufferHandle arguments, long offset = 0, int drawCount = 1, int stride = 20) {
        ThrowIfNotInPass(nameof(DrawIndexedIndirect));

        if (drawCount != 1) {
            throw new NotSupportedException(
                $"An indirect draw of {drawCount} was asked for. WebGPU has no multi-draw indirect — one "
                + "call issues one draw — which is what Features.HasMultiDrawIndirect reports. Issue the "
                + "draws separately, or ask the capability and take the other path."
            );
        }

        var resolved = device.ResolveBuffer(arguments, "indirect draw arguments");
        ThrowIfPastEnd(resolved, offset, "indirect draw arguments");

        commands.Add(new(WebGpuCommandKind.DrawIndexedIndirect, E: offset, Object0: resolved.Handle));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     WebGPU has neither multi-draw indirect nor a count buffer, and it is not an omission
    ///     waiting to be filled: the count would have to be validated against the argument buffer's
    ///     length on the GPU, which the specification's safety model does not have a mechanism for.
    ///     The padded form is what runs here permanently.
    /// </remarks>
    public void DrawIndexedIndirectCount(
        BufferHandle arguments,
        BufferHandle count,
        long offset = 0,
        long countOffset = 0,
        int maxDrawCount = 1,
        int stride = 20
    ) =>
        throw new NotSupportedException(
            "A draw whose count comes from a buffer is not something WebGPU has, which is what "
            + "Features.HasDrawIndirectCount reports. Issue DrawIndexedIndirect at the run's maximum "
            + "length with the culled arguments zeroed instead."
        );

    /// <inheritdoc />
    public void BuildAccelerationStructure(
        AccelerationStructureHandle target,
        in AccelerationStructureBuildInput input,
        BufferHandle scratch,
        long scratchOffset = 0
    ) =>
        throw new NotSupportedException(
            "An acceleration-structure build was recorded on the WebGPU backend, which has no ray "
            + "tracing — see WebGpuDevice.CreateAccelerationStructure for why. Ask "
            + "Features.HasRayTracing and take the distance-field tracer."
        );

    /// <inheritdoc />
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                "Dispatch inside a render pass. Compute work belongs between passes — no API allows it "
                + "inside one, and a tiled GPU would have to resolve the tile to run it."
            );
        }

        commands.Add(new(WebGpuCommandKind.Dispatch, groupsX, groupsY, groupsZ));
    }

    /// <inheritdoc />
    public void DispatchIndirect(BufferHandle arguments, long offset = 0) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException("DispatchIndirect inside a render pass.");
        }

        var resolved = device.ResolveBuffer(arguments, "indirect dispatch arguments");
        ThrowIfPastEnd(resolved, offset, "indirect dispatch arguments");

        commands.Add(new(WebGpuCommandKind.DispatchIndirect, E: offset, Object0: resolved.Handle));
    }

    // ── Transfers and synchronisation ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Barrier(in BarrierGroup barriers) {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                "A barrier inside a render pass. The transitions a pass needs are declared by its "
                + "attachments' load and store actions; a barrier here would split the pass."
            );
        }

        // And then nothing. WebGPU tracks resource state itself — there is no barrier call to make,
        // on either surface — so the RHI's barriers are validated for shape and dropped, the same
        // way the GL backend elides them. They are not useless: a render graph that gets them wrong
        // is wrong on Vulkan, and this backend is where that costs nothing to find out.
    }

    /// <inheritdoc />
    public void CopyBuffer(
        BufferHandle source,
        long sourceOffset,
        BufferHandle destination,
        long destinationOffset,
        long size
    ) {
        ThrowIfCopying();

        if (source == destination) {
            throw new ArgumentException(
                "A buffer was copied onto itself. Overlapping copies are undefined on every API, and "
                + "WebGPU rejects the two being the same outright.",
                nameof(destination)
            );
        }

        var from = device.ResolveBuffer(source, "a copy source");
        var to = device.ResolveBuffer(destination, "a copy destination");

        if (sourceOffset < 0 || sourceOffset + size > from.AllocatedSize) {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOffset),
                $"Reading {size} bytes at {sourceOffset} runs off the end of '{from.Description.Name}'."
            );
        }

        if (destinationOffset < 0 || destinationOffset + size > to.AllocatedSize) {
            throw new ArgumentOutOfRangeException(
                nameof(destinationOffset),
                $"Writing {size} bytes at {destinationOffset} runs off the end of '{to.Description.Name}'."
            );
        }

        commands.Add(
            new(
                WebGpuCommandKind.CopyBuffer,
                E: sourceOffset,
                F: destinationOffset,
                G: size,
                Object0: from.Handle,
                Object1: to.Handle
            )
        );
    }

    /// <inheritdoc />
    public void CopyBufferToTexture(BufferHandle source, long sourceOffset, in TextureRegion destination, Int3 size) {
        ThrowIfCopying();

        var from = device.ResolveBuffer(source, "a copy source");
        var to = device.ResolveTexture(destination.Texture, "a copy destination");
        RequireCopyable(to);

        var bufferIndex = bufferCopies.Count;
        bufferCopies.Add(LinearLayout(from.Handle, sourceOffset, to.Description.Format, size));

        var textureIndex = textureCopies.Count;
        textureCopies.Add(Region(to, destination));

        commands.Add(
            new(
                WebGpuCommandKind.CopyBufferToTexture,
                bufferIndex,
                textureIndex,
                E: size.X,
                F: size.Y,
                G: size.Z
            )
        );
    }

    /// <inheritdoc />
    public void CopyTextureToBuffer(in TextureRegion source, Int3 size, BufferHandle destination, long destinationOffset) {
        ThrowIfCopying();

        var from = device.ResolveTexture(source.Texture, "a copy source");
        RequireCopyable(from);
        var to = device.ResolveBuffer(destination, "a copy destination");

        var textureIndex = textureCopies.Count;
        textureCopies.Add(Region(from, source));

        var bufferIndex = bufferCopies.Count;
        bufferCopies.Add(LinearLayout(to.Handle, destinationOffset, from.Description.Format, size));

        commands.Add(
            new(
                WebGpuCommandKind.CopyTextureToBuffer,
                textureIndex,
                bufferIndex,
                E: size.X,
                F: size.Y,
                G: size.Z
            )
        );
    }

    /// <inheritdoc />
    public void CopyTexture(in TextureRegion source, in TextureRegion destination, Int3 size) {
        ThrowIfCopying();

        var from = device.ResolveTexture(source.Texture, "a copy source");
        var to = device.ResolveTexture(destination.Texture, "a copy destination");

        var sourceIndex = textureCopies.Count;
        textureCopies.Add(Region(from, source));

        var destinationIndex = textureCopies.Count;
        textureCopies.Add(Region(to, destination));

        commands.Add(
            new(
                WebGpuCommandKind.CopyTexture,
                sourceIndex,
                destinationIndex,
                E: size.X,
                F: size.Y,
                G: size.Z
            )
        );
    }

    // ── Debugging ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void PushDebugGroup(string name) {
        ThrowIfRecorded();
        groupDepth++;
        commands.Add(new(WebGpuCommandKind.PushDebugGroup, Label(name)));
    }

    /// <inheritdoc />
    public void PopDebugGroup() {
        ThrowIfRecorded();

        if (groupDepth == 0) {
            throw new InvalidOperationException("PopDebugGroup without a matching PushDebugGroup.");
        }

        groupDepth--;
        commands.Add(new(WebGpuCommandKind.PopDebugGroup));
    }

    /// <inheritdoc />
    public void InsertDebugMarker(string name) {
        ThrowIfRecorded();
        commands.Add(new(WebGpuCommandKind.InsertDebugMarker, Label(name)));
    }

    /// <inheritdoc />
    public void ResetQueries(QueryPoolHandle pool, int first, int count) =>
        throw new NotSupportedException(
            "The WebGPU backend has no query pools — see WebGpuDevice.CreateQueryPool for why."
        );

    /// <inheritdoc />
    public void WriteTimestamp(QueryPoolHandle pool, int index) =>
        throw new NotSupportedException(
            "The WebGPU backend has no query pools — see WebGpuDevice.CreateQueryPool for why."
        );

    /// <inheritdoc />
    public void Dispose() => disposed = true;

    internal void MarkSubmitted() => Submitted = true;

    int Label(string text) {
        labels.Add(text ?? string.Empty);
        return labels.Count - 1;
    }

    /// <summary>Where a texture copy touches, with the RHI's defaults resolved.</summary>
    static WgpuImageCopyTexture Region(WebGpuTexture texture, in TextureRegion region) => new(
        texture.Handle,
        region.MipLevel,
        region.Origin.X,
        region.Origin.Y,

        // A 2D array's layer is WebGPU's Z origin, and a 3D texture's slice is the same field. The
        // RHI keeps them apart — ArrayLayer and Origin.Z — and only one of them is ever non-zero.
        texture.Description.Dimension == TextureDimension.Texture3D ? region.Origin.Z : region.ArrayLayer,

        // A copy names one plane of a combined format, never both. The engine's depth is what gets
        // copied — a stencil readback would need its own call, and nothing asks for one.
        texture.Description.Format.HasStencil() && texture.Description.Format.HasDepth()
            ? WgpuTextureAspect.DepthOnly
            : WgpuTextureAspect.All
    );

    /// <summary>How a texture's texels are laid out in a buffer, per WebGPU's rules.</summary>
    /// <remarks>
    ///     <c>bytesPerRow</c> is a multiple of 256 on every implementation, and the RHI's callers do
    ///     not know that. Computing it here from the format's block size is what makes an upload
    ///     land where the caller meant it to; getting it wrong shears the image diagonally, which is
    ///     the single most recognisable texture-upload bug there is.
    /// </remarks>
    static WgpuImageCopyBuffer LinearLayout(WebGpuObject buffer, long offset, PixelFormat format, Int3 size) {
        var (blockWidth, blockHeight) = format.BlockExtent();
        var columns = (size.X + blockWidth - 1) / blockWidth;
        var rows = (size.Y + blockHeight - 1) / blockHeight;
        var bytesPerRow = columns * format.BlockSize();

        return new(buffer, offset, AlignTo256(bytesPerRow), rows);
    }

    static int AlignTo256(int value) => (value + 255) & ~255;

    static void RequireCopyable(WebGpuTexture texture) {
        if (!texture.Description.Format.CanCopy()) {
            throw new NotSupportedException(
                $"Texture '{texture.Description.Name}' is {texture.Description.Format}, which WebGPU spells "
                + "depth24plus-stencil8 — a format whose bit layout the implementation chooses, so there is "
                + "no defined byte pattern for a buffer copy to move. Use Depth32Float, which the engine's "
                + "reversed-Z depth uses anyway."
            );
        }
    }

    static void ThrowIfPastEnd(WebGpuBuffer buffer, long offset, string what) {
        if (offset < 0 || offset >= buffer.AllocatedSize) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Binding {what} at offset {offset} is outside '{buffer.Description.Name}', which is "
                + $"{buffer.Description.Size} bytes."
            );
        }
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    void ThrowIfRecorded() {
        ThrowIfDisposed();

        if (IsRecorded) {
            throw new InvalidOperationException(
                $"Command list '{name}' was recorded into after Finish(). A finished list is immutable."
            );
        }
    }

    void ThrowIfNotInPass(string operation) {
        ThrowIfRecorded();

        if (!inRenderPass) {
            throw new InvalidOperationException(
                $"{operation} outside a render pass. WebGPU's viewport, scissor, vertex bindings and draws "
                + "all belong to a pass encoder, so there is nothing to record it on."
            );
        }
    }

    void ThrowIfCopying() {
        ThrowIfRecorded();

        if (inRenderPass) {
            throw new InvalidOperationException(
                "A copy inside a render pass, which no API allows — a tiled GPU would have to resolve "
                + "the tile to perform it."
            );
        }
    }
}
