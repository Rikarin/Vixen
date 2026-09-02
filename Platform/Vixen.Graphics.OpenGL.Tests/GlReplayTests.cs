// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>What a recorded command list turns into on the way to the driver.</summary>
/// <remarks>
///     The level ADR-001 is really asking about. Every claim here is one the RHI makes and GL has no
///     native way to keep: passes without framebuffer objects, descriptor sets without a binding
///     model, barriers on an API with no barriers, and a clip space the other way up.
/// </remarks>
public sealed class GlReplayTests {
    /// <summary>A pass builds one framebuffer, names its draw buffers, and clears.</summary>
    /// <remarks>
    ///     <c>glDrawBuffers</c> is said explicitly and always, because GL's default for a user
    ///     framebuffer is attachment zero only — so a pass with two colour targets would write one
    ///     and discard the other, with no error, looking exactly like a shader that forgot its second
    ///     output.
    /// </remarks>
    [Fact]
    public void TurnsAPassIntoAFramebufferAndAClear() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view, LoadAction.Clear, StoreAction.Store, new(0.25f, 0.5f, 0.75f, 1f))]));
            commands.EndRenderPass();
        });

        Assert.Equal(1, gl.Count("GenFramebuffer"));
        Assert.Equal(1, gl.Count("FramebufferTexture2D"));
        Assert.Equal([GlConstants.ColourAttachment0], gl.Single("DrawBuffers").Arguments);

        var clear = gl.Single("ClearBufferF");
        Assert.Equal([GlConstants.Colour, 0, 0.25f, 0.5f, 0.75f, 1f], clear.Arguments);
    }

    /// <summary>The same attachment set in a later pass reuses the framebuffer.</summary>
    /// <remarks>
    ///     Rebuilding an FBO per pass is the single most expensive mistake a GL backend can make:
    ///     attaching a texture makes the driver re-validate the whole set, and several drivers
    ///     recompile internal state when it changes. A renderer has the same twelve attachment sets
    ///     every frame.
    /// </remarks>
    [Fact]
    public void ReusesAFramebufferForTheSameAttachments() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        for (var frame = 0; frame < 4; frame++) {
            Submit(device, commands => {
                commands.BeginRenderPass(new([new(view)]));
                commands.EndRenderPass();
            });
        }

        Assert.Equal(1, device.FramebufferCount);
    }

    /// <summary>Destroying a view drops the framebuffers that named it.</summary>
    /// <remarks>
    ///     GL removes a deleted attachment silently: the attachment point becomes zero and the
    ///     framebuffer becomes incomplete at the next bind, which surfaces as a pass that draws
    ///     nothing several frames after the destroy that caused it.
    /// </remarks>
    [Fact]
    public void ForgetsAFramebufferWhoseAttachmentIsDestroyed() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.EndRenderPass();
        });

        Assert.Equal(1, device.FramebufferCount);
        device.Destroy(view);
        Assert.Equal(0, device.FramebufferCount);
    }

    /// <summary>A <c>DontCare</c> load invalidates rather than clearing.</summary>
    /// <remarks>
    ///     Not a micro-optimisation on a tiled GPU: it is what keeps the driver from reading the
    ///     whole attachment into tile memory before the pass, which on a phone is measurable in
    ///     milliseconds and in battery.
    /// </remarks>
    [Fact]
    public void InvalidatesOnDontCare() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view, LoadAction.DontCare, StoreAction.DontCare)]));
            commands.EndRenderPass();
        });

        Assert.Equal(0, gl.Count("ClearBufferF"));

        // Once at the start for the load and once at the end for the store.
        Assert.Equal(2, gl.Count("InvalidateFramebuffer"));
    }

    /// <summary>A <c>Load</c> action neither clears nor invalidates.</summary>
    [Fact]
    public void KeepsTheContentsOnLoad() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view, LoadAction.Load)]));
            commands.EndRenderPass();
        });

        Assert.Equal(0, gl.Count("ClearBufferF"));
        Assert.Equal(0, gl.Count("InvalidateFramebuffer"));
    }

    /// <summary>Depth clears to zero, which is <em>far</em> under the engine's reversed depth.</summary>
    /// <remarks>
    ///     The single most expensive mistake available in this codebase: a backend that cleared depth
    ///     to one renders a scene that depth-tests away entirely, with no error anywhere. The RHI's
    ///     default carries it and this asserts the backend does not helpfully change it.
    /// </remarks>
    [Fact]
    public void ClearsDepthToFar() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var colour = Colour(device, out _);
        var depth = Depth(device);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(colour)], new DepthStencilAttachment(depth)));
            commands.EndRenderPass();
        });

        var clear = gl.Named("ClearBufferF").Single(call => Equals(call.Arguments[0], GlConstants.Depth));
        Assert.Equal(0f, clear.Arguments[2]);
    }

    /// <summary>A descriptor set resolves to the binding index the plan chose.</summary>
    [Fact]
    public void BindsAUniformBufferAtThePlannedIndex() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var layout = Pipelines.Layout(device);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout);

        var setLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerDraw,
            [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)],
            "draw"
        ));

        var set = device.CreateDescriptorSet(setLayout, "draw");
        var buffer = device.CreateBuffer(new(1024, BufferUsage.Uniform, MemoryAccess.HostUpload, "draw"));
        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, buffer, 0, 64)]);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, set, [256]);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        var bind = gl.Single("BindBufferRange");
        Assert.Equal(GlConstants.UniformBuffer, bind.Arguments[0]);
        Assert.Equal(0u, bind.Arguments[1]);

        // The dynamic offset is added to the write's own, which is how per-draw transforms are bound
        // without a descriptor set per object.
        Assert.Equal(256L, bind.Arguments[3]);
        Assert.Equal(64UL, bind.Arguments[4]);
    }

    /// <summary>A texture binding becomes a unit, a texture and a sampler.</summary>
    [Fact]
    public void BindsATextureAndItsSamplerToOneUnit() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var layout = Pipelines.Layout(device);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout);

        var setLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)],
            "material"
        ));

        var set = device.CreateDescriptorSet(setLayout, "material");
        var albedo = device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Name: "albedo"));
        var albedoView = device.CreateTextureView(albedo);
        var sampler = device.CreateSampler(SamplerDescription.LinearRepeat);
        device.UpdateDescriptorSet(set, [DescriptorWrite.Texture(0, albedoView) with { Sampler = sampler }]);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        Assert.Equal([0u], gl.Named("ActiveTexture")[^1].Arguments);
        Assert.Equal(GlConstants.Texture2D, gl.Named("BindTexture")[^1].Arguments[0]);
        Assert.Equal(0u, gl.Single("BindSampler").Arguments[0]);
    }

    /// <summary>
    ///     A texture with no sampler of its own takes the set's standalone one.
    /// </summary>
    /// <remarks>
    ///     The rule this backend has to invent, stated once. GL's <c>glBindSampler</c> takes a
    ///     texture unit, so a sampler is always attached to a texture and "this sampler, for whichever
    ///     textures the shader pairs it with" cannot be said. A texture's own sampler wins; otherwise
    ///     the set's.
    /// </remarks>
    [Fact]
    public void FallsBackToTheSetsStandaloneSampler() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var layout = Pipelines.Layout(device);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout);

        var setLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "material"
        ));

        var set = device.CreateDescriptorSet(setLayout, "material");
        var albedo = device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Name: "albedo"));
        var albedoView = device.CreateTextureView(albedo);
        var sampler = device.CreateSampler(SamplerDescription.PointClamp);

        device.UpdateDescriptorSet(
            set,
            [DescriptorWrite.Texture(0, albedoView), DescriptorWrite.SamplerAt(1, sampler)]
        );

        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        var bind = gl.Single("BindSampler");
        Assert.Equal(0u, bind.Arguments[0]);
        Assert.NotEqual(0u, bind.Arguments[1]);
    }

    /// <summary>Push constants arrive as one <c>glUniform4fv</c> over the reserved array.</summary>
    [Fact]
    public void UploadsPushConstantsAsAUniformArray() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var layout = Pipelines.Layout(device, sizeof(float) * 4);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout);
        gl.Clear();

        var values = new[] { 1f, 2f, 3f, 4f };

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.PushConstants(ShaderStage.Vertex, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
            commands.Draw(3);
            commands.EndRenderPass();
        });

        var upload = gl.Single("Uniform4fv");
        Assert.Equal([1f, 2f, 3f, 4f], upload.Arguments[1..]);
    }

    /// <summary>An indexed draw's first index becomes a byte offset of the right width.</summary>
    /// <remarks>
    ///     GL takes a byte offset where the RHI takes an index, so the multiplication by the index
    ///     width lives here. Getting it wrong with 16-bit indices draws the right count of the wrong
    ///     triangles, which is a picture rather than an error.
    /// </remarks>
    [Fact]
    public void ConvertsAFirstIndexIntoAByteOffset() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, Pipelines.VertexLayout);
        var indices = device.CreateBuffer(new(512, BufferUsage.Index, MemoryAccess.HostUpload, "indices"));
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindIndexBuffer(indices, IndexFormat.UInt32, 8);
            commands.DrawIndexed(6, 1, 3, 2);
            commands.EndRenderPass();
        });

        var draw = gl.Single("DrawElementsInstancedBaseVertex");
        Assert.Equal(6, draw.Arguments[1]);
        Assert.Equal(GlConstants.UnsignedInt, draw.Arguments[2]);

        // 8 bytes into the buffer, plus 3 indices of 4 bytes each.
        Assert.Equal(20L, draw.Arguments[3]);
        Assert.Equal(2, draw.Arguments[5]);
    }

    /// <summary>Attribute pointers are set when a vertex buffer is bound, not at pipeline creation.</summary>
    /// <remarks>
    ///     In the non-DSA path every profile shares, <c>glVertexAttribPointer</c> captures whatever is
    ///     bound to <c>GL_ARRAY_BUFFER</c> when it is called — so an attribute's format and its buffer
    ///     are one piece of state. The enables and divisors do belong to the pipeline, and are set
    ///     once.
    /// </remarks>
    [Fact]
    public void AppliesAttributeFormatsWhenTheBufferIsBound() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, Pipelines.VertexLayout);
        var vertices = device.CreateBuffer(new(512, BufferUsage.Vertex, MemoryAccess.HostUpload, "vertices"));

        // Two attribute enables at creation, and no pointers yet.
        Assert.Equal(2, gl.Count("EnableVertexAttribArray"));
        Assert.Equal(0, gl.Count("VertexAttribPointer"));
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindVertexBuffer(0, vertices, 128);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        var pointers = gl.Named("VertexAttribPointer");
        Assert.Equal(2, pointers.Count);

        // Position: three floats at the buffer offset.
        Assert.Equal([0u, 3, GlConstants.Float, false, 28, 128L], pointers[0].Arguments);

        // Colour: four floats twelve bytes further in.
        Assert.Equal([1u, 4, GlConstants.Float, false, 28, 140L], pointers[1].Arguments);
    }

    /// <summary>Push constants are re-uploaded after a program change.</summary>
    /// <remarks>
    ///     <para>
    ///         The one place GL's model genuinely costs work Vulkan's does not. A uniform is
    ///         <em>program</em> state in GL, so switching programs loses it — whereas a buffer or
    ///         texture binding is context state and survives, which is why nothing else here has to
    ///         be re-sent.
    ///     </para>
    ///     <para>
    ///         The two pipelines here share a program, and the re-upload still has to happen: the
    ///         backend does not get to reason about whether the driver kept the uniform, and a
    ///         version that did would be right until a permutation stopped sharing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ReUploadsPushConstantsAfterAPipelineChange() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var layout = Pipelines.Layout(device, sizeof(float) * 4);
        var first = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout);
        var second = Pipelines.Handle(device, BlendState.AlphaBlend, DepthStencilState.Disabled, layout: layout);
        gl.Clear();

        var values = new[] { 1f, 2f, 3f, 4f };

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(first);

            commands.PushConstants(
                ShaderStage.Vertex,
                0,
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan())
            );

            commands.Draw(3);
            commands.BindPipeline(second);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        Assert.Equal(2, gl.Count("Uniform4fv"));
    }

    /// <summary>The same set under a layout that places it differently binds at the new index.</summary>
    /// <remarks>
    ///     Why a pipeline change re-resolves the sets rather than trusting what is bound. Two
    ///     pipelines with different layouts put the same set at different binding indices, and a set
    ///     left marked clean would go on pointing at the previous pipeline's. Where the indices
    ///     agree, the state cache elides the bind and the conservative marking costs nothing —
    ///     which is why this test uses layouts that disagree.
    /// </remarks>
    [Fact]
    public void ReResolvesASetAgainstTheNewLayout() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        var perDraw = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerDraw,
            [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)],
            "draw"
        ));

        var perMaterial = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)],
            "material"
        ));

        // One extra per-frame uniform block ahead of the per-draw one, so the same binding lands at
        // index 1 rather than index 0 under the second layout.
        var perFrame = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)],
            "frame"
        ));

        var narrow = device.CreatePipelineLayout(new([perDraw, perMaterial], null, "narrow"));
        var wide = device.CreatePipelineLayout(new([perFrame, perDraw, perMaterial], null, "wide"));

        var first = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: narrow);
        var second = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: wide);

        var set = device.CreateDescriptorSet(perDraw, "draw");
        var buffer = device.CreateBuffer(new(1024, BufferUsage.Uniform, MemoryAccess.HostUpload, "draw"));
        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, buffer)]);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(first);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, set);
            commands.Draw(3);
            commands.BindPipeline(second);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        var binds = gl.Named("BindBufferRange");
        Assert.Equal(2, binds.Count);
        Assert.Equal(0u, binds[0].Arguments[1]);
        Assert.Equal(1u, binds[1].Arguments[1]);
    }

    /// <summary>The layout decides whether a binding is dynamic, not the write.</summary>
    /// <remarks>
    ///     <c>DescriptorWrite.Uniform</c> produces the non-dynamic kind because that is the common
    ///     case and there is no separate helper. A backend that trusted the write would drop the
    ///     dynamic offset of every caller who used the obvious one — putting each per-draw transform
    ///     on the wrong object, which is a picture rather than an error. Vulkan catches it only
    ///     because its validation layers check the write's type against the layout's.
    /// </remarks>
    [Fact]
    public void TakesTheDynamicKindFromTheLayout() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var layout = Pipelines.Layout(device);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout);

        var setLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerDraw,
            [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)],
            "draw"
        ));

        var set = device.CreateDescriptorSet(setLayout, "draw");
        var buffer = device.CreateBuffer(new(1024, BufferUsage.Uniform, MemoryAccess.HostUpload, "draw"));

        // The non-dynamic helper, deliberately: this is what a caller writes.
        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, buffer, 0, 64)]);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerDraw, set, [256]);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        Assert.Equal(256L, gl.Single("BindBufferRange").Arguments[3]);
    }

    /// <summary>An ordinary barrier is nothing at all.</summary>
    /// <remarks>
    ///     GL's memory model orders every command against every command before it in the same
    ///     context, so a colour-target-to-shader-read barrier — the most common one a render graph
    ///     emits — needs no call. Emitting one would be a full pipeline flush per pass.
    /// </remarks>
    [Fact]
    public void ElidesABarrierWithNoIncoherentWrite() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var texture = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            8,
            8,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "target"
        ));

        gl.Clear();

        Submit(device, commands => commands.Barrier(new(
            [],
            [new(texture, ResourceState.ColourTarget, ResourceState.ShaderRead)]
        )));

        Assert.Equal(0, gl.Count("MemoryBarrier"));
    }

    /// <summary>A barrier after a shader write is not.</summary>
    /// <remarks>
    ///     Storage buffers and storage images are the accesses GL calls incoherent, and a backend that
    ///     elided these would produce a race that shows up as intermittently stale data on one
    ///     vendor's driver. That the RHI's barrier model carries enough to tell the two cases apart is
    ///     the reassuring part of ADR-001.
    /// </remarks>
    [Fact]
    public void EmitsAMemoryBarrierAfterAShaderWrite() {
        var gl = new RecordingGlApi(GlProfile.Es32);
        using var device = new GlDevice(new(gl));
        var buffer = device.CreateBuffer(new(256, BufferUsage.Storage, MemoryAccess.DeviceLocal, "particles"));
        gl.Clear();

        Submit(device, commands => commands.Barrier(new(
            [new(buffer, ResourceState.ShaderWrite, ResourceState.VertexInput)],
            []
        )));

        var barrier = gl.Single("MemoryBarrier");
        var bits = (uint)barrier.Arguments[0]!;
        Assert.NotEqual(0u, bits & GlConstants.ShaderStorageBarrierBit);

        // GL_VERTEX_ATTRIB_ARRAY_BARRIER_BIT, because the buffer is about to be read as vertex input.
        Assert.NotEqual(0u, bits & 0x00000001u);
    }

    /// <summary>A base instance is refused where the profile has no entry point for it.</summary>
    /// <remarks>
    ///     GLES has no <c>glDrawElementsInstancedBaseVertexBaseInstance</c> at any version. Silently
    ///     drawing instance zero would be a mesh in the wrong place; the message names the dynamic
    ///     offset that every profile does have.
    /// </remarks>
    [Fact]
    public void RefusesABaseInstanceOnGles() {
        var gl = new RecordingGlApi(GlProfile.Es32);
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);
        var pipeline = Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled);

        var error = Assert.Throws<NotSupportedException>(() => Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.Draw(3, 1, 0, 4);
            commands.EndRenderPass();
        }));

        Assert.Contains("dynamic uniform offset", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A draw with no pipeline bound is refused rather than drawn with the last program.</summary>
    [Fact]
    public void RefusesADrawWithNoPipeline() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        Assert.Throws<InvalidOperationException>(() => Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.Draw(3);
            commands.EndRenderPass();
        }));
    }

    /// <summary>A whole-texture transfer is one call each way, with no row flipping.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Pinned because the obvious expectation is the opposite.</b> GL's window origin is
    ///         the lower left and Vulkan's is the upper left, so it looks as though anything this
    ///         backend renders must be stored upside down — and an earlier version of this backend
    ///         flipped every row on the way in and out to fix that, at one transfer call per row.
    ///     </para>
    ///     <para>
    ///         The engine's clip space is <b>+Y up</b>, which is neither API's default: the Vulkan
    ///         backend renders through a negative-height viewport and this one changes the clip-to-
    ///         window direction, so both land clip <c>y = +1</c> at texel row zero. There is nothing
    ///         to flip. A test that only checked "the picture round-trips" would pass either way,
    ///         which is why this asserts the call <em>count</em>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TransfersWholeTexturesInOneCall() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        var texture = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            4,
            4,
            TextureUsage.CopySource | TextureUsage.CopyDestination | TextureUsage.ColourTarget,
            Name: "square"
        ));

        var staging = device.CreateBuffer(new(
            64,
            BufferUsage.CopySource | BufferUsage.CopyDestination,
            MemoryAccess.HostUpload,
            "staging"
        ));

        gl.Clear();

        Submit(device, commands => {
            commands.CopyBufferToTexture(staging, 0, new(texture), new(4, 4, 1));
            commands.CopyTextureToBuffer(new(texture), new(4, 4, 1), staging, 0);
        });

        var upload = gl.Single("TexSubImage2D");
        var read = gl.Single("ReadPixels");

        // x, y, width, height — the whole texture from its origin, in the order it was given.
        Assert.Equal([0, 0, 4, 4], upload.Arguments[2..6]);
        Assert.Equal([0, 0, 4, 4], read.Arguments[..4]);
    }

    /// <summary>Debug groups reach the driver only where the profile has them.</summary>
    [Theory]
    [InlineData(GlProfile.Core45, 1)]
    [InlineData(GlProfile.Es30, 0)]
    public void EmitsDebugGroupsWhereTheyExist(GlProfile profile, int expected) {
        var gl = new RecordingGlApi(profile);
        using var device = new GlDevice(new(gl));
        gl.Clear();

        Submit(device, commands => {
            commands.PushDebugGroup("shadows");
            commands.InsertDebugMarker("cascade 0");
            commands.PopDebugGroup();
        });

        Assert.Equal(expected, gl.Count("PushDebugGroup"));
        Assert.Equal(expected, gl.Count("DebugMarker"));
    }

    /// <summary>A storage image reaches <c>glBindImageTexture</c> rather than an exception.</summary>
    /// <remarks>
    ///     ⚠ <b>It used to throw, on a profile that had already said yes twice.</b>
    ///     <c>GlProfiles.Features</c> reports <c>HasCompute</c> true from GLES 3.2 up,
    ///     <c>CreateTexture</c> accepts <c>TextureUsage.Storage</c> there, and <c>GlBindingPlan</c>
    ///     has always reserved an image unit for the binding — and then replay refused the write. A
    ///     renderer that asked the capability question got yes, built everything, and failed at the
    ///     draw.
    /// </remarks>
    [Fact]
    public void BindsAStorageImageToAnImageUnit() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        var (pipeline, setLayout) = MaterialPipeline(
            device,
            new DescriptorBinding(0, DescriptorKind.StorageTexture, ShaderStage.Fragment)
        );

        var set = device.CreateDescriptorSet(setLayout, "material");

        var target = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Storage, Name: "storage")
        );

        device.UpdateDescriptorSet(set, [DescriptorWrite.StorageImage(0, device.CreateTextureView(target))]);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        var bind = gl.Single("BindImageTexture");

        Assert.Equal(0u, bind.Arguments[0]);
        Assert.Equal(0, bind.Arguments[2]);
        Assert.Equal(false, bind.Arguments[3]);
        Assert.Equal(0, bind.Arguments[4]);

        // ⚠ GL_READ_WRITE, always. A storage image carries no direction in the RHI, so the widest
        // access is the only one that cannot silently hand undefined values to a shader that reads
        // what was bound write-only.
        Assert.Equal(GlConstants.ReadWrite, bind.Arguments[5]);

        // RGBA8's sized internal format, which is what the image unit reinterprets through.
        Assert.Equal(0x8058u, bind.Arguments[6]);
    }

    /// <summary>
    ///     ⚠ An image unit and a texture unit are separate namespaces, and both start at zero.
    /// </summary>
    /// <remarks>
    ///     The mistake this pins down is the tempting one: a single running index for everything a
    ///     shader reads. <c>GlBindingPlan</c> counts them apart already, so a set holding a sampled
    ///     texture and a storage image gives unit 0 to each — of its own kind. Sharing the counter
    ///     would leave them fighting over one slot, and GL reports nothing, because both binds are
    ///     perfectly legal.
    /// </remarks>
    [Fact]
    public void AStorageImageAndASampledTextureBothTakeSlotZeroOfTheirOwnKind() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        var (pipeline, setLayout) = MaterialPipeline(
            device,
            new DescriptorBinding(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new DescriptorBinding(1, DescriptorKind.StorageTexture, ShaderStage.Fragment)
        );

        var set = device.CreateDescriptorSet(setLayout, "material");

        var sampled = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Name: "albedo")
        );

        var stored = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Storage, Name: "stored")
        );

        device.UpdateDescriptorSet(set, [
            DescriptorWrite.Texture(0, device.CreateTextureView(sampled)),
            DescriptorWrite.StorageImage(1, device.CreateTextureView(stored))
        ]);

        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        Assert.Equal(0u, gl.Single("BindImageTexture").Arguments[0]);
        Assert.Equal(0u, gl.Named("ActiveTexture")[^1].Arguments[0]);
    }

    /// <summary>A view of one mip level binds that level, not level zero.</summary>
    /// <remarks>
    ///     ⚠ <b>And the state cache has to key on it.</b> A compute chain writing a pyramid binds the
    ///     same texture at successive levels; a cache keyed on the texture name alone — which is what
    ///     the sampled-texture cache beside it is — would elide every bind after the first and write
    ///     level 0 three times, producing a chain whose every level is the base. Blurry rather than
    ///     absent, which is why it wants an assertion rather than an eye.
    /// </remarks>
    [Fact]
    public void SuccessiveMipLevelsOfOneTextureAreEachBound() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        var (pipeline, setLayout) = MaterialPipeline(
            device,
            new DescriptorBinding(0, DescriptorKind.StorageTexture, ShaderStage.Fragment)
        );

        var pyramid = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Storage, MipLevels: 4, Name: "pyramid")
        );

        var sets = new DescriptorSetHandle[3];

        for (var level = 0; level < sets.Length; level++) {
            sets[level] = device.CreateDescriptorSet(setLayout, $"level {level}");

            device.UpdateDescriptorSet(sets[level], [
                DescriptorWrite.StorageImage(
                    0,
                    device.CreateTextureView(pyramid, baseMipLevel: level, mipLevelCount: 1)
                )
            ]);
        }

        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);

            foreach (var set in sets) {
                commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
                commands.Draw(3);
            }

            commands.EndRenderPass();
        });

        var binds = gl.Named("BindImageTexture");

        Assert.Equal(3, binds.Count);
        Assert.Equal([0, 1, 2], binds.Select(call => call.Arguments[2]));
    }

    /// <summary>And binding the same image twice running costs one call.</summary>
    /// <remarks>
    ///     The other half of the claim above, and the half a cache keyed on every argument could
    ///     still fail: a set rebound between draws must not re-issue the same
    ///     <c>glBindImageTexture</c>, which is what the cache is for.
    /// </remarks>
    [Fact]
    public void RebindingTheSameStorageImageCostsNothing() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var view = Colour(device, out _);

        var (pipeline, setLayout) = MaterialPipeline(
            device,
            new DescriptorBinding(0, DescriptorKind.StorageTexture, ShaderStage.Fragment)
        );

        var set = device.CreateDescriptorSet(setLayout, "material");

        var stored = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Storage, Name: "stored")
        );

        device.UpdateDescriptorSet(set, [DescriptorWrite.StorageImage(0, device.CreateTextureView(stored))]);
        gl.Clear();

        Submit(device, commands => {
            commands.BeginRenderPass(new([new(view)]));
            commands.BindPipeline(pipeline);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
            commands.Draw(3);
            commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, set);
            commands.Draw(3);
            commands.EndRenderPass();
        });

        Assert.Equal(1, gl.Count("BindImageTexture"));
    }

    /// <summary>A pipeline whose layout declares the per-material bindings a test needs.</summary>
    /// <remarks>
    ///     ⚠ <b>The layout has to be the <em>pipeline's</em>, not just the set's.</b>
    ///     <c>ApplyDescriptorSets</c> resolves every write through
    ///     <c>pipeline.Layout.Plan.Resolve(slot, binding)</c> and skips what the plan does not know —
    ///     so a test that declared a storage image on the set and reused <c>Pipelines.Layout</c>
    ///     would record no bind at all and look like the feature was still missing.
    /// </remarks>
    static (PipelineHandle Pipeline, DescriptorSetLayoutHandle Material) MaterialPipeline(
        GlDevice device,
        params DescriptorBinding[] material
    ) {
        var perDraw = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerDraw,
            [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)],
            "draw"
        ));

        var perMaterial = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, material, "material")
        );

        var layout = device.CreatePipelineLayout(new([perDraw, perMaterial], null, "material layout"));

        return (
            Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled, layout: layout),
            perMaterial
        );
    }

    static TextureViewHandle Colour(GlDevice device, out TextureHandle texture) {
        texture = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            64,
            64,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "colour"
        ));

        return device.CreateTextureView(texture);
    }

    static TextureViewHandle Depth(GlDevice device) {
        var texture = device.CreateTexture(new(
            PixelFormat.Depth32Float,
            64,
            64,
            TextureUsage.DepthStencilTarget,
            Name: "depth"
        ));

        return device.CreateTextureView(texture);
    }

    static void Submit(GlDevice device, Action<ICommandList> record) {
        using var commands = device.BeginCommandList();
        record(commands);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);
    }
}
