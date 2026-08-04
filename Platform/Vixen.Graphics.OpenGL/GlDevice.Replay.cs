// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>The half of the backend that makes GL calls.</summary>
/// <remarks>
///     <para>
///         Everything recorded by a <see cref="GlCommandList" /> arrives here, on the thread that
///         owns the context, in the order it was recorded. This is where the RHI's model is actually
///         translated, and where the four differences ADR-001 predicted turn into code:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Pipelines.</b> <see cref="GlStateCache.ApplyPipeline" /> — a program and a
///                 dozen state setters, of which only the ones that changed are sent.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Descriptor sets.</b> A bind records the set; the draw resolves it against the
///                 bound pipeline's <see cref="GlBindingPlan" /> and makes the binds. That deferral
///                 is not laziness — a set may legitimately be bound before the pipeline that gives
///                 its bindings meaning, and GL has nowhere to put it in the meantime.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Barriers.</b> Almost always nothing: GL's execution model orders everything a
///                 command stream does against everything before it. The exception is incoherent
///                 memory — storage buffers and storage images — where <c>glMemoryBarrier</c> is
///                 required and where a backend that elided everything would be wrong in exactly the
///                 way that is hardest to find.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Threading.</b> Already answered: the recording happened elsewhere.
///             </description>
///         </item>
///     </list>
/// </remarks>
public sealed partial class GlDevice {
    const int SlotCount = 4;

    readonly DescriptorSetHandle[] boundSets = new DescriptorSetHandle[SlotCount];
    readonly (int Index, int Count)[] boundOffsets = new (int, int)[SlotCount];
    readonly bool[] setDirty = new bool[SlotCount];
    readonly (BufferHandle Buffer, long Offset)[] vertexBuffers = new (BufferHandle, long)[8];
    readonly bool[] vertexDirty = new bool[8];
    readonly List<float> pushConstants = [];

    GlPipeline? bound;
    IndexFormat indexFormat = IndexFormat.UInt16;
    long indexOffset;
    bool pushDirty;
    int passColourCount;
    (int Index, int Count) passAttachments;

    /// <summary>Replays a recorded command list onto the context.</summary>
    internal void Replay(GlCommandRecorder recorder) {
        ResetReplayState();

        foreach (var command in recorder.Commands) {
            switch (command.Kind) {
                case GlCommandKind.BeginRenderPass:
                    BeginPass(recorder, command);
                    break;

                case GlCommandKind.EndRenderPass:
                    EndPass(recorder);
                    break;

                case GlCommandKind.SetViewport:
                    state.SetViewport(
                        command.Float0,
                        command.Float1,
                        command.Float2,
                        command.Float3,
                        command.Float4,
                        command.Float5
                    );

                    break;

                case GlCommandKind.SetScissor:
                    state.Set(GlConstants.ScissorTest, true);
                    state.SetScissor(command.Int0, command.Int1, command.Int2, command.Int3);
                    break;

                case GlCommandKind.SetBlendConstant:
                    state.SetBlendConstant(command.Float0, command.Float1, command.Float2, command.Float3);
                    break;

                case GlCommandKind.SetStencilReference:
                    state.SetStencilReference(command.Uint0);
                    break;

                case GlCommandKind.BindPipeline:
                    BindPipeline(command.Pipeline);
                    break;

                case GlCommandKind.BindDescriptorSet:
                    boundSets[command.Int0] = command.Descriptors;
                    boundOffsets[command.Int0] = (command.PayloadIndex, command.PayloadCount);
                    setDirty[command.Int0] = true;
                    break;

                case GlCommandKind.PushConstants:
                    StagePushConstants(recorder.Bytes(command.PayloadIndex, command.PayloadCount), command.Int0);
                    break;

                case GlCommandKind.BindVertexBuffer:
                    vertexBuffers[command.Int0] = (command.Buffer0, command.Long0);
                    vertexDirty[command.Int0] = true;
                    break;

                case GlCommandKind.BindIndexBuffer:
                    indexFormat = (IndexFormat)command.Int0;
                    indexOffset = command.Long0;
                    state.BindIndexBuffer(Buffer(command.Buffer0).Name);
                    break;

                case GlCommandKind.Draw:
                    Draw(recorder, command);
                    break;

                case GlCommandKind.DrawIndexed:
                    DrawIndexed(recorder, command);
                    break;

                case GlCommandKind.DrawIndexedIndirect:
                    DrawIndirect(recorder, command);
                    break;

                case GlCommandKind.Dispatch:
                    PrepareDispatch(recorder);
                    gl.DispatchCompute((uint)command.Int0, (uint)command.Int1, (uint)command.Int2);
                    break;

                case GlCommandKind.DispatchIndirect:
                    PrepareDispatch(recorder);
                    state.BindBuffer(GlConstants.DispatchIndirectBuffer, Buffer(command.Buffer0).Name);
                    gl.DispatchComputeIndirect((nint)command.Long0);
                    break;

                case GlCommandKind.Barrier:
                    Barrier(recorder, command);
                    break;

                case GlCommandKind.CopyBuffer:
                    CopyBuffer(command);
                    break;

                case GlCommandKind.CopyBufferToTexture:
                    CopyBufferToTexture(command);
                    break;

                case GlCommandKind.CopyTextureToBuffer:
                    CopyTextureToBuffer(command);
                    break;

                case GlCommandKind.CopyTexture:
                    CopyTexture(command);
                    break;

                case GlCommandKind.PushDebugGroup:
                    if (Profile.HasDebugOutput()) {
                        gl.PushDebugGroup(recorder.Name(command.Int0));
                    }

                    break;

                case GlCommandKind.PopDebugGroup:
                    if (Profile.HasDebugOutput()) {
                        gl.PopDebugGroup();
                    }

                    break;

                case GlCommandKind.InsertDebugMarker:
                    if (Profile.HasDebugOutput()) {
                        gl.DebugMarker(recorder.Name(command.Int0));
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unrecognised recorded command {command.Kind}.");
            }
        }
    }

    void ResetReplayState() {
        Array.Clear(boundSets);
        Array.Clear(setDirty);
        Array.Clear(vertexBuffers);
        Array.Clear(vertexDirty);
        pushConstants.Clear();
        pushDirty = false;
        bound = null;
        indexOffset = 0;
        indexFormat = IndexFormat.UInt16;
    }

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    void BeginPass(GlCommandRecorder recorder, in GlCommand command) {
        var attachments = recorder.Attachments(command.PayloadIndex, command.PayloadCount);
        passColourCount = command.Int0;
        passAttachments = (command.PayloadIndex, command.PayloadCount);

        var framebuffer = framebuffers.Get(attachments, passColourCount, ResolveAttachment);
        state.BindDrawFramebuffer(framebuffer);

        // The pass's size decides the viewport and scissor conversion, so it has to be known before
        // either is set — and a pass with no attachments at all cannot happen, because the RHI
        // rejects a pipeline that writes to nothing.
        var first = View(attachments[0].View);
        var texture = Texture(first.Texture);
        state.TargetHeight = Math.Max(1, texture.Description.Height >> first.BaseMipLevel);

        // A clear obeys the scissor and the colour mask, both of which the last pipeline left set to
        // whatever it wanted. Turning them off is not optional: a pass that clears with a stale
        // scissor clears part of its target and leaves the rest holding the previous frame, which
        // looks like a barrier problem and is not one.
        state.Set(GlConstants.ScissorTest, false);
        state.SetViewport(0, 0, GetWidth(first, texture), state.TargetHeight, 0f, 1f);
        state.PrepareClear();

        for (var index = 0; index < attachments.Length; index++) {
            var attachment = attachments[index];

            if (attachment.Load != LoadAction.Clear) {
                // DontCare is a real instruction and not a no-op: on a tiled GPU it is what keeps
                // the driver from reading the attachment into tile memory before the pass.
                if (attachment.Load == LoadAction.DontCare) {
                    Span<uint> point = [PointOf(attachment, index)];
                    gl.InvalidateFramebuffer(GlConstants.DrawFramebuffer, point);
                }

                continue;
            }

            if (attachment.IsDepth) {
                var format = View(attachment.View).Format;

                if (format.HasStencil()) {
                    gl.ClearBufferDepthStencil(0, attachment.ClearDepth, attachment.ClearStencil);
                } else {
                    Span<float> depth = [attachment.ClearDepth];
                    gl.ClearBuffer(GlConstants.Depth, 0, depth);
                }

                continue;
            }

            var colourFormat = View(attachment.View).Format;

            if (GlFormats.IsInteger(colourFormat)) {
                Span<int> value = [
                    (int)attachment.ClearColour.R,
                    (int)attachment.ClearColour.G,
                    (int)attachment.ClearColour.B,
                    (int)attachment.ClearColour.A
                ];

                gl.ClearBuffer(GlConstants.Colour, index, value);
                continue;
            }

            Span<float> colour = [
                attachment.ClearColour.R,
                attachment.ClearColour.G,
                attachment.ClearColour.B,
                attachment.ClearColour.A
            ];

            gl.ClearBuffer(GlConstants.Colour, index, colour);
        }
    }

    void EndPass(GlCommandRecorder recorder) {
        var attachments = recorder.Attachments(passAttachments.Index, passAttachments.Count);
        Span<uint> discarded = stackalloc uint[attachments.Length];
        var count = 0;

        for (var index = 0; index < attachments.Length; index++) {
            if (attachments[index].Store == StoreAction.DontCare) {
                discarded[count++] = PointOf(attachments[index], index);
            }
        }

        if (count > 0) {
            gl.InvalidateFramebuffer(GlConstants.DrawFramebuffer, discarded[..count]);
        }
    }

    static uint PointOf(in GlAttachment attachment, int index) =>
        attachment.IsDepth ? GlConstants.DepthAttachment : GlConstants.ColourAttachment0 + (uint)index;

    static int GetWidth(GlTextureView view, GlTexture texture) =>
        Math.Max(1, texture.Description.Width >> view.BaseMipLevel);

    (uint Name, uint Target, int Level, int Layer, bool Layered, PixelFormat Format) ResolveAttachment(
        TextureViewHandle handle
    ) {
        var view = View(handle);
        var texture = Texture(view.Texture);

        return (
            texture.Name,
            texture.Target,
            view.BaseMipLevel,
            view.BaseArrayLayer,
            texture.IsLayered,
            view.Format
        );
    }

    // ── Pipelines, bindings and draws ───────────────────────────────────────────────────────

    void BindPipeline(PipelineHandle handle) {
        var pipeline = Pipeline(handle);

        if (ReferenceEquals(bound, pipeline)) {
            return;
        }

        bound = pipeline;
        state.ApplyPipeline(pipeline);

        // Sets are re-resolved because the *plan* may differ: two pipelines with different layouts
        // put the same set at different binding indices, and a set left marked clean would go on
        // pointing at the previous pipeline's units. Where the indices agree the state cache elides
        // the binds, so the conservative marking costs nothing.
        //
        // Push constants are re-sent for a stronger reason. They are a real uniform, and a uniform
        // is *program* state in GL rather than context state — so a program change genuinely loses
        // them, which is the one place GL's model costs work that Vulkan's does not.
        Array.Fill(setDirty, true);
        Array.Fill(vertexDirty, true);
        pushDirty = pushConstants.Count > 0;
    }

    void PrepareDraw(GlCommandRecorder recorder) {
        var pipeline = bound ?? throw new InvalidOperationException(
            "A draw was recorded with no pipeline bound. Vulkan's validation layers say the same thing; "
            + "GL says nothing at all and draws with whatever program was last used."
        );

        ApplyDescriptorSets(recorder, pipeline);
        ApplyPushConstants(pipeline);
        ApplyVertexBuffers(pipeline);
    }

    void PrepareDispatch(GlCommandRecorder recorder) {
        var pipeline = bound ?? throw new InvalidOperationException("A dispatch was recorded with no pipeline bound.");

        if (!pipeline.IsCompute) {
            throw new InvalidOperationException(
                $"Pipeline '{pipeline.Name}' is a graphics pipeline and a dispatch was recorded against it."
            );
        }

        ApplyDescriptorSets(recorder, pipeline);
        ApplyPushConstants(pipeline);
    }

    void ApplyDescriptorSets(GlCommandRecorder recorder, GlPipeline pipeline) {
        for (var slot = 0; slot < SlotCount; slot++) {
            if (!setDirty[slot] || !boundSets[slot].IsValid) {
                continue;
            }

            setDirty[slot] = false;
            var set = DescriptorSet(boundSets[slot]);
            var (offsetIndex, offsetCount) = boundOffsets[slot];
            var offsets = recorder.UInts(offsetIndex, offsetCount);
            var dynamic = 0;

            foreach (var ((binding, arrayIndex), write) in set.Writes.OrderBy(entry => entry.Key.Binding)) {
                if (pipeline.Layout.Plan.Resolve(set.Slot, binding) is not { } resolved) {
                    continue;
                }

                var index = resolved.Index + (uint)arrayIndex;
                var kind = set.KindOf(binding, write.Kind);

                switch (kind) {
                    case DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer:
                    case DescriptorKind.StorageBuffer or DescriptorKind.DynamicStorageBuffer: {
                        var isUniform = kind is DescriptorKind.UniformBuffer
                            or DescriptorKind.DynamicUniformBuffer;

                        var isDynamic = kind is DescriptorKind.DynamicUniformBuffer
                            or DescriptorKind.DynamicStorageBuffer;

                        var buffer = Buffer(write.Buffer);
                        var offset = write.Offset;

                        if (isDynamic) {
                            offset += dynamic < offsets.Length ? offsets[dynamic] : 0;
                            dynamic++;
                        }

                        var size = write.Size > 0 ? write.Size : buffer.Description.Size - offset;

                        state.BindBufferRange(
                            isUniform ? GlConstants.UniformBuffer : GlConstants.ShaderStorageBuffer,
                            index,
                            buffer.Name,
                            (nint)offset,
                            (nuint)size
                        );

                        break;
                    }

                    case DescriptorKind.SampledTexture: {
                        var view = View(write.TextureView);
                        var texture = Texture(view.Texture);

                        // The sampler on the write wins; otherwise the set's one. See
                        // GlDescriptorSet.DefaultSampler for why there has to be a rule here at all.
                        var sampler = write.Sampler.IsValid ? write.Sampler : set.DefaultSampler;

                        state.BindTextureUnit(
                            index,
                            texture.Target,
                            texture.Name,
                            sampler.IsValid ? Sampler(sampler).Name : 0
                        );

                        break;
                    }

                    case DescriptorKind.StorageTexture:
                        throw new NotSupportedException(
                            "Storage images need glBindImageTexture, which this backend does not yet "
                            + "route — the compute paths that would use one all have a "
                            + "fullscreen-fragment variant for WebGL2 already (docs/plan/06)."
                        );

                    // Unreachable in principle — GlBindingPlan.Build refuses the layout — but a
                    // write that somehow got here must not silently bind nothing.
                    case DescriptorKind.AccelerationStructure:
                        throw new NotSupportedException(
                            "An acceleration-structure descriptor reached OpenGL replay. The backend "
                            + "has no ray tracing — ask Features.HasRayTracing and take the "
                            + "distance-field tracer."
                        );

                    // Applied through the texture bindings above rather than on its own, because GL
                    // has no sampler binding point that is not a texture unit.
                    case DescriptorKind.Sampler:
                    default:
                        break;
                }
            }
        }
    }

    void StagePushConstants(ReadOnlySpan<byte> data, int offset) {
        var floats = MemoryMarshal.Cast<byte, float>(data);
        var start = offset / sizeof(float);

        while (pushConstants.Count < start + floats.Length) {
            pushConstants.Add(0f);
        }

        for (var index = 0; index < floats.Length; index++) {
            pushConstants[start + index] = floats[index];
        }

        pushDirty = true;
    }

    void ApplyPushConstants(GlPipeline pipeline) {
        if (!pushDirty || pipeline.PushConstantLocation < 0 || pushConstants.Count == 0) {
            return;
        }

        pushDirty = false;

        // Rounded up to whole vec4s, because that is the shape the uniform array has. A 12-byte
        // block is one vec4 with a wasted component, which is exactly what a Vulkan push-constant
        // block costs too once std430 has aligned it.
        var vectors = (pushConstants.Count + 3) / 4;

        while (pushConstants.Count < vectors * 4) {
            pushConstants.Add(0f);
        }

        gl.Uniform4(pipeline.PushConstantLocation, CollectionsMarshal.AsSpan(pushConstants)[..(vectors * 4)]);
    }

    void ApplyVertexBuffers(GlPipeline pipeline) {
        for (var slot = 0; slot < pipeline.VertexBuffers.Length; slot++) {
            if (!vertexDirty[slot] || !vertexBuffers[slot].Buffer.IsValid) {
                continue;
            }

            vertexDirty[slot] = false;
            var layout = pipeline.VertexBuffers[slot];
            var (handle, offset) = vertexBuffers[slot];
            state.BindBuffer(GlConstants.ArrayBuffer, Buffer(handle).Name);

            foreach (var attribute in layout.Attributes ?? []) {
                var (size, type, normalised, integer) = GlEnums.Vertex(attribute.Format);
                var address = (nint)(offset + attribute.Offset);

                if (integer) {
                    gl.VertexAttribIPointer(attribute.Location, size, type, layout.Stride, address);
                } else {
                    gl.VertexAttribPointer(attribute.Location, size, type, normalised, layout.Stride, address);
                }
            }
        }
    }

    void Draw(GlCommandRecorder recorder, in GlCommand command) {
        PrepareDraw(recorder);
        var topology = bound!.Topology;

        if (command.Int3 == 0) {
            gl.DrawArraysInstanced(topology, command.Int2, command.Int0, command.Int1);
            return;
        }

        RequireBaseInstance();

        gl.DrawArraysInstancedBaseInstance(
            topology,
            command.Int2,
            command.Int0,
            command.Int1,
            (uint)command.Int3
        );
    }

    void DrawIndexed(GlCommandRecorder recorder, in GlCommand command) {
        PrepareDraw(recorder);
        var topology = bound!.Topology;
        var stride = GlEnums.IndexSize(indexFormat);
        var offset = (nint)(indexOffset + ((long)command.Int2 * stride));
        var type = GlEnums.Index(indexFormat);

        if (command.Uint0 == 0) {
            gl.DrawElementsInstancedBaseVertex(
                topology,
                command.Int0,
                type,
                offset,
                command.Int1,
                command.Int3
            );

            return;
        }

        RequireBaseInstance();

        gl.DrawElementsInstancedBaseVertexBaseInstance(
            topology,
            command.Int0,
            type,
            offset,
            command.Int1,
            command.Int3,
            command.Uint0
        );
    }

    void DrawIndirect(GlCommandRecorder recorder, in GlCommand command) {
        if (!Profile.HasIndirect()) {
            throw new NotSupportedException(
                $"An indirect draw was recorded and {Profile} has no glDrawElementsIndirect. This is a "
                + "GPU-driven path; ask GraphicsDeviceFeatures.HasCompute, which is false on the same "
                + "profiles, and take the CPU one."
            );
        }

        PrepareDraw(recorder);
        state.BindBuffer(GlConstants.DrawIndirectBuffer, Buffer(command.Buffer0).Name);

        gl.MultiDrawElementsIndirect(
            bound!.Topology,
            GlEnums.Index(indexFormat),
            (nint)command.Long0,
            command.Int0,
            command.Int1
        );
    }

    void RequireBaseInstance() {
        if (!Profile.HasBaseInstance()) {
            throw new NotSupportedException(
                $"A draw asked to start at a non-zero instance, and {Profile} has no "
                + "glDrawElementsInstancedBaseVertexBaseInstance — GLES has no equivalent at any version. "
                + "Offset the instance data through a dynamic uniform offset instead, which every profile "
                + "has."
            );
        }
    }

    // ── Barriers ────────────────────────────────────────────────────────────────────────────

    /// <summary>Translates a barrier group, which is usually into nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Eliding is correct here, and it is worth being precise about why.</b> GL's memory
    ///         model orders every command against every command before it in the same context, for
    ///         every access except the ones it explicitly calls incoherent. So a barrier from
    ///         <c>ColourTarget</c> to <c>ShaderRead</c> — the single most common barrier a render
    ///         graph emits — needs no GL call at all, and emitting one would be a full pipeline flush
    ///         per pass.
    ///     </para>
    ///     <para>
    ///         The exceptions are the incoherent accesses: shader storage buffers and storage images,
    ///         written by one dispatch and read by the next. Those genuinely need
    ///         <c>glMemoryBarrier</c>, and a backend that elided them would produce a race that shows
    ///         up as intermittently stale data on one vendor's driver.
    ///     </para>
    ///     <para>
    ///         So the rule is: a barrier is nothing unless <see cref="ResourceState.ShaderWrite" />
    ///         is on one side of it. Which is a satisfying answer, because it means the RHI's barrier
    ///         model carries enough information for GL to make the distinction — the thing ADR-001
    ///         was worried about.
    ///     </para>
    /// </remarks>
    void Barrier(GlCommandRecorder recorder, in GlCommand command) {
        var textures = recorder.TextureBarriers(command.PayloadIndex, command.PayloadCount);
        var buffers = recorder.BufferBarriers(command.Int0, command.Int1);
        uint bits = 0;

        foreach (var barrier in buffers) {
            if ((barrier.Before & ResourceState.ShaderWrite) == 0) {
                continue;
            }

            bits |= GlConstants.ShaderStorageBarrierBit;

            if ((barrier.After & ResourceState.VertexInput) != 0) {
                bits |= 0x00000001;
            }

            if ((barrier.After & ResourceState.IndirectArgument) != 0) {
                bits |= 0x00000004;
            }

            if ((barrier.After & ResourceState.UniformRead) != 0) {
                bits |= 0x00000002;
            }
        }

        foreach (var barrier in textures) {
            if ((barrier.Before & ResourceState.ShaderWrite) == 0) {
                continue;
            }

            bits |= GlConstants.ShaderImageAccessBarrierBit;

            if ((barrier.After & ResourceState.ShaderRead) != 0) {
                bits |= GlConstants.TextureFetchBarrierBit;
            }

            if ((barrier.After & (ResourceState.ColourTarget | ResourceState.DepthStencilWrite)) != 0) {
                bits |= GlConstants.FramebufferBarrierBit;
            }
        }

        if (bits != 0) {
            gl.MemoryBarrier(bits);
        }
    }

    // ── Transfers ───────────────────────────────────────────────────────────────────────────

    void CopyBuffer(in GlCommand command) {
        state.BindBuffer(GlConstants.CopyReadBuffer, Buffer(command.Buffer0).Name);
        state.BindBuffer(GlConstants.CopyWriteBuffer, Buffer(command.Buffer1).Name);

        gl.CopyBufferSubData(
            GlConstants.CopyReadBuffer,
            GlConstants.CopyWriteBuffer,
            (nint)command.Long0,
            (nint)command.Long1,
            (nuint)command.Long2
        );
    }

    /// <summary>Uploads from a buffer into a texture.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>No row flip, and that is worth saying because it looks as though there should be
    ///         one.</b> GL's window origin is the lower left and Vulkan's is the upper left, so the
    ///         obvious expectation is that anything this backend renders is stored upside down
    ///         relative to the reference backend — and it would be, if the engine's clip space were
    ///         Vulkan's.
    ///     </para>
    ///     <para>
    ///         It is not. The engine is <b>+Y up</b>
    ///         (<c>Core/Vixen.Core.Mathematics/Conventions.md</c>), which is why the Vulkan backend
    ///         renders through a negative-height viewport. So both backends are flipping, in opposite
    ///         directions, from opposite defaults — and they land in the same place: clip <c>y = +1</c>
    ///         reaches the <em>lowest</em> row of the framebuffer on each, which is texel row zero,
    ///         which is what the RHI calls the top.
    ///     </para>
    ///     <para>
    ///         Working that through was worth the trouble: the first version of this method flipped
    ///         every row, at one transfer call per row, to fix a problem that the engine's convention
    ///         had already fixed. What made it visible was the golden suite — the existing fixtures
    ///         put a triangle's apex at negative <c>y</c> and it comes out at the <em>bottom</em> of
    ///         the reference image, which only happens if the engine is +Y up.
    ///     </para>
    /// </remarks>
    void CopyBufferToTexture(in GlCommand command) {
        var buffer = Buffer(command.Buffer0);
        var texture = Texture(command.Texture0);
        var format = GlFormats.Of(texture.Description.Format, Profile);

        if (texture.Description.Format.IsCompressed()) {
            throw new NotSupportedException(
                $"Uploading compressed texture '{texture.Description.Name}' needs "
                + "glCompressedTexSubImage, which this backend does not route. Compressed content "
                + "reaches GL through the content build's own upload path."
            );
        }

        var (x, y, z) = ((int)command.Float0, (int)command.Float1, (int)command.Float2);
        var (width, height, depth) = ((int)command.Float3, (int)command.Float4, (int)command.Float5);
        var level = command.Int0;
        var layer = command.Int1;

        state.BindBuffer(GlConstants.PixelUnpackBuffer, buffer.Name);
        state.ActiveTexture(0);
        gl.BindTexture(texture.Target, texture.Name);

        if (texture.IsLayered) {
            gl.TexSubImage3D(
                texture.Target,
                level,
                x,
                y,
                z + layer,
                width,
                height,
                depth,
                format.Format,
                format.Type,
                (nint)command.Long0
            );
        } else {
            gl.TexSubImage2D(
                texture.Target,
                level,
                x,
                y,
                width,
                height,
                format.Format,
                format.Type,
                (nint)command.Long0
            );
        }

        // Unbound, because the unpack binding changes what every later glTexSubImage2D means — a
        // stale one turns a pointer upload into an offset into the wrong buffer, silently.
        state.BindBuffer(GlConstants.PixelUnpackBuffer, 0);
        state.Invalidate();
    }

    /// <summary>Reads a texture back into a buffer.</summary>
    /// <remarks>
    ///     One call and no row flip, for the reason <see cref="CopyBufferToTexture" /> sets out: the
    ///     engine's +Y-up clip space means this backend's texture rows land the same way up as the
    ///     reference backend's.
    /// </remarks>
    void CopyTextureToBuffer(in GlCommand command) {
        var buffer = Buffer(command.Buffer0);
        var texture = Texture(command.Texture0);
        var format = GlFormats.Of(texture.Description.Format, Profile);

        var (x, y, _) = ((int)command.Float0, (int)command.Float1, (int)command.Float2);
        var (width, height, _) = ((int)command.Float3, (int)command.Float4, (int)command.Float5);
        var level = command.Int0;

        if (readbackFramebuffer == 0) {
            readbackFramebuffer = gl.GenFramebuffer();
        }

        state.BindReadFramebuffer(readbackFramebuffer);

        if (texture.IsLayered) {
            gl.FramebufferTextureLayer(
                GlConstants.ReadFramebuffer,
                GlConstants.ColourAttachment0,
                texture.Name,
                level,
                command.Int1
            );
        } else {
            gl.FramebufferTexture2D(
                GlConstants.ReadFramebuffer,
                GlConstants.ColourAttachment0,
                texture.Target,
                texture.Name,
                level
            );
        }

        gl.ReadBuffer(GlConstants.ColourAttachment0);
        var status = gl.CheckFramebufferStatus(GlConstants.ReadFramebuffer);

        if (status != GlConstants.FramebufferComplete) {
            throw new InvalidOperationException(
                $"Texture '{texture.Description.Name}' cannot be read back: attaching it for reading "
                + $"produced an incomplete framebuffer (0x{status:X4}). GL has no way to read a texture "
                + "that is not renderable, which is why a readback target has to be a colour format."
            );
        }

        state.BindBuffer(GlConstants.PixelPackBuffer, buffer.Name);
        gl.ReadPixels(x, y, width, height, format.Format, format.Type, (nint)command.Long0);
        state.BindBuffer(GlConstants.PixelPackBuffer, 0);
    }

    /// <summary>Blits an offscreen target onto framebuffer zero, which is how GL presents.</summary>
    /// <remarks>See <see cref="GlSwapChain" /> for why the swapchain image is a texture at all.</remarks>
    internal void BlitToDefaultFramebuffer(TextureHandle handle, Int2 size) {
        var texture = Texture(handle);

        if (readbackFramebuffer == 0) {
            readbackFramebuffer = gl.GenFramebuffer();
        }

        state.BindReadFramebuffer(readbackFramebuffer);

        gl.FramebufferTexture2D(
            GlConstants.ReadFramebuffer,
            GlConstants.ColourAttachment0,
            texture.Target,
            texture.Name,
            0
        );

        gl.ReadBuffer(GlConstants.ColourAttachment0);
        state.BindDrawFramebuffer(0);

        // Scissor off, or a blit inherits the last pass's rectangle and presents a corner of the
        // frame. A clear has the same trap and PrepareClear answers it in the same way.
        state.Set(GlConstants.ScissorTest, false);

        gl.BlitFramebuffer(
            0,
            0,
            size.X,
            size.Y,
            0,
            0,
            size.X,
            size.Y,
            0x00004000,
            GlConstants.Nearest
        );
    }

    void CopyTexture(in GlCommand command) {
        if (!Profile.HasCompute()) {
            throw new NotSupportedException(
                $"A texture-to-texture copy was recorded and {Profile} has no glCopyImageSubData. Render "
                + "the source through a full-screen pass instead, which every profile has."
            );
        }

        var source = Texture(command.Texture0);
        var destination = Texture(command.Texture1);
        var sourceOrigin = GlCommandList.Unpack(command.Long0);
        var destinationOrigin = GlCommandList.Unpack(command.Long1);
        var size = GlCommandList.Unpack(command.Long2);

        gl.CopyImageSubData(
            source.Name,
            source.Target,
            command.Int0,
            sourceOrigin.X,
            sourceOrigin.Y,
            sourceOrigin.Z + command.Int1,
            destination.Name,
            destination.Target,
            command.Int2,
            destinationOrigin.X,
            destinationOrigin.Y,
            destinationOrigin.Z + command.Int3,
            size.X,
            size.Y,
            size.Z
        );
    }
}
