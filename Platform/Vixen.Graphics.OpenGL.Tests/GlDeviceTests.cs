// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>Device bring-up, resource creation and the capability gates.</summary>
/// <remarks>
///     The gates matter more here than on any other backend. ADR-001 accepts that GL is the least
///     capable target and asks that the absences be <em>declared</em> rather than discovered — a
///     device that reports compute it does not have is one that crashes in a shipped WebGL2 build,
///     which is the target with no debugger attached.
/// </remarks>
public sealed class GlDeviceTests {
    /// <summary>Desktop GL is told once that its clip space is Vulkan's.</summary>
    [Fact]
    public void SetsClipControlOnDesktop() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        var call = gl.Single("ClipControl");
        Assert.Equal([GlConstants.UpperLeft, GlConstants.ZeroToOne], call.Arguments);
    }

    /// <summary>Every other profile is not, because it cannot be.</summary>
    /// <remarks>The vertex shader carries it instead — see <see cref="GlslTranslatorTests" />.</remarks>
    [Theory]
    [InlineData(GlProfile.WebGl2)]
    [InlineData(GlProfile.Es30)]
    [InlineData(GlProfile.Es32)]
    public void DoesNotSetClipControlWhereThereIsNone(GlProfile profile) {
        var gl = new RecordingGlApi(profile);
        using var device = new GlDevice(new(gl));

        Assert.Equal(0, gl.Count("ClipControl"));
    }

    /// <summary>Only desktop GL is told to honour an sRGB attachment, because only it has to be.</summary>
    /// <remarks>
    ///     <c>GL_FRAMEBUFFER_SRGB</c> is not an enumerant GLES or WebGL2 accept — enabling it there is
    ///     <c>GL_INVALID_ENUM</c> — and they need no such switch: an attachment whose format is sRGB
    ///     is encoded and one whose format is not is not, which is what the RHI means by a format.
    ///     This was the first thing a real GLES context found.
    /// </remarks>
    [Theory]
    [InlineData(GlProfile.WebGl2, 0)]
    [InlineData(GlProfile.Es30, 0)]
    [InlineData(GlProfile.Es32, 0)]
    [InlineData(GlProfile.Core45, 1)]
    public void EnablesFramebufferSrgbOnlyWhereItExists(GlProfile profile, int expected) {
        var gl = new RecordingGlApi(profile);
        using var device = new GlDevice(new(gl));

        Assert.Equal(
            expected,
            gl.Named("Enable").Count(call => call.Arguments[0] is GlConstants.FramebufferSrgb)
        );

        // The one that is enabled on every profile, so the theory above is a gate rather than a
        // device that stopped setting state.
        Assert.Contains(
            gl.Named("Enable"),
            call => call.Arguments[0] is GlConstants.PrimitiveRestartFixedIndex
        );
    }

    /// <summary>What each profile claims it can do.</summary>
    [Theory]
    [InlineData(GlProfile.WebGl2, false, false)]
    [InlineData(GlProfile.Es30, false, false)]
    [InlineData(GlProfile.Es32, true, true)]
    [InlineData(GlProfile.Core45, true, true)]
    public void ReportsWhatTheProfileHas(GlProfile profile, bool compute, bool storage) {
        var gl = new RecordingGlApi(profile);
        using var device = new GlDevice(new(gl));

        Assert.Equal(compute, device.Features.HasCompute);
        Assert.Equal(storage, profile.HasStorageBuffers());

        // Never, on any profile. GL has no core bindless and no semaphores of any kind, so a
        // renderer that gated on these takes the same path everywhere here.
        Assert.False(device.Features.HasBindless);
        Assert.False(device.Features.HasTimelineSemaphores);
        Assert.False(device.Features.HasAsyncCompute);
    }

    /// <summary>A buffer is allocated with the usage hint its access asked for.</summary>
    [Fact]
    public void AllocatesABufferWithAMatchingHint() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        gl.Clear();

        device.CreateBuffer(new(256, BufferUsage.Vertex, MemoryAccess.HostUpload, "vertices"));

        var call = gl.Single("BufferData");
        Assert.Equal(GlConstants.ArrayBuffer, call.Arguments[0]);
        Assert.Equal(256UL, call.Arguments[1]);
        Assert.Equal(GlConstants.StreamDraw, call.Arguments[2]);
    }

    /// <summary>A host write goes through the copy target rather than the buffer's home one.</summary>
    /// <remarks>
    ///     So that writing a vertex buffer between two draws does not knock out the array binding the
    ///     second one needs. <c>GL_COPY_WRITE_BUFFER</c> exists for precisely this.
    /// </remarks>
    [Fact]
    public void WritesThroughTheCopyTarget() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, MemoryAccess.HostUpload, "vertices"));
        gl.Clear();

        device.Write(buffer, 0, new byte[16]);

        Assert.Equal(GlConstants.CopyWriteBuffer, gl.Single("BufferSubData").Arguments[0]);
    }

    /// <summary>A device-local buffer refuses a host write.</summary>
    [Fact]
    public void RefusesAHostWriteToDeviceMemory() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, MemoryAccess.DeviceLocal, "mesh"));

        Assert.Throws<InvalidOperationException>(() => device.Write(buffer, 0, new byte[16]));
    }

    /// <summary>A texture is allocated with immutable storage and a capped mip range.</summary>
    /// <remarks>
    ///     GL's default maximum level is a full chain regardless of what was allocated, and a texture
    ///     sampled beyond its levels is <em>incomplete</em> — which samples as opaque black on every
    ///     driver and looks like a texture that failed to load.
    /// </remarks>
    [Fact]
    public void CapsTheMipRangeItAllocated() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        gl.Clear();

        device.CreateTexture(new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Sampled, MipLevels: 3, Name: "albedo"));

        Assert.Equal(3, gl.Single("TexStorage2D").Arguments[1]);

        var levels = gl.Named("TexParameter")
            .Where(call => Equals(call.Arguments[1], GlConstants.TextureMaxLevel))
            .ToList();

        Assert.Equal(2, Assert.Single(levels).Arguments[2]);
    }

    /// <summary>A storage buffer is refused where the profile has none.</summary>
    [Fact]
    public void RefusesAStorageBufferOnGles30() {
        var gl = new RecordingGlApi(GlProfile.Es30);
        using var device = new GlDevice(new(gl));

        var error = Assert.Throws<NotSupportedException>(
            () => device.CreateBuffer(new(64, BufferUsage.Storage, MemoryAccess.DeviceLocal, "particles"))
        );

        Assert.Contains("HasCompute", error.Message, StringComparison.Ordinal);
    }

    /// <summary>So is a compute pipeline, and the message names the fallback.</summary>
    [Fact]
    public void RefusesAComputePipelineOnWebGl2() {
        var gl = new RecordingGlApi(GlProfile.WebGl2);
        using var device = new GlDevice(new(gl));
        var compute = device.CreateShader(ShaderStage.Compute, Encoding.UTF8.GetBytes(Pipelines.ComputeSource), "c");
        var layout = device.CreatePipelineLayout(new([], null, "empty"));

        var error = Assert.Throws<NotSupportedException>(
            () => device.CreateComputePipeline(new(compute, layout, "simulate"))
        );

        Assert.Contains("fullscreen-fragment", error.Message, StringComparison.Ordinal);
    }

    /// <summary>SPIR-V is refused with the reason, rather than compiled as text.</summary>
    /// <remarks>
    ///     The magic number is checked because the failure otherwise is a driver reporting a syntax
    ///     error on line 1 of what looks like binary noise, which tells nobody anything.
    /// </remarks>
    [Fact]
    public void RefusesSpirv() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var spirv = BitConverter.GetBytes(0x07230203u).Concat(new byte[16]).ToArray();

        var error = Assert.Throws<ArgumentException>(
            () => device.CreateShader(ShaderStage.Vertex, spirv, "mesh.vert")
        );

        Assert.Contains("SPIR-V", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An unbounded descriptor array is refused, because nothing in GL is bindless.</summary>
    [Fact]
    public void RefusesAnUnboundedBinding() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        var error = Assert.Throws<NotSupportedException>(
            () => device.CreateDescriptorSetLayout(new(
                DescriptorSetSlot.PerMaterial,
                [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 0)],
                "bindless"
            ))
        );

        Assert.Contains("HasBindless", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A view that reinterprets a format is refused on every profile rather than one.</summary>
    /// <remarks>
    ///     GL 4.3's <c>glTextureView</c> could do it and GLES cannot. Offering it on the one profile
    ///     that can would mean content that works on desktop and fails on Android, which is worse
    ///     than not offering it.
    /// </remarks>
    [Fact]
    public void RefusesAFormatReinterpretingView() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var texture = device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Name: "t"));

        Assert.Throws<NotSupportedException>(
            () => device.CreateTextureView(texture, PixelFormat.Rgba8UNormSrgb)
        );
    }

    /// <summary>A set with two standalone samplers is refused, and the message says what to do.</summary>
    /// <remarks>
    ///     GL attaches a sampler to a texture unit rather than binding it in its own right, so a set
    ///     with more than one standalone sampler has no unambiguous meaning. Resolving it arbitrarily
    ///     would produce a picture filtered by whichever sampler happened to win.
    /// </remarks>
    [Fact]
    public void RefusesTwoStandaloneSamplers() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        var layout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerMaterial,
            [
                new(0, DescriptorKind.Sampler, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "samplers"
        ));

        var set = device.CreateDescriptorSet(layout, "samplers");
        var point = device.CreateSampler(SamplerDescription.PointClamp);
        var linear = device.CreateSampler(SamplerDescription.LinearRepeat);

        var error = Assert.Throws<NotSupportedException>(
            () => device.UpdateDescriptorSet(
                set,
                [DescriptorWrite.SamplerAt(0, point), DescriptorWrite.SamplerAt(1, linear)]
            )
        );

        Assert.Contains("DescriptorWrite has a Sampler field", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Wireframe is refused on GLES, where <c>glPolygonMode</c> does not exist.</summary>
    [Fact]
    public void RefusesWireframeOnGles() {
        var gl = new RecordingGlApi(GlProfile.Es32);
        using var device = new GlDevice(new(gl));

        var vertex = device.CreateShader(ShaderStage.Vertex, Encoding.UTF8.GetBytes(Pipelines.VertexSource), "v");

        var error = Assert.Throws<NotSupportedException>(() => device.CreateGraphicsPipeline(new(
            vertex,
            ShaderHandle.Null,
            Pipelines.Layout(device),
            [new(PixelFormat.Rgba8UNorm, BlendState.Opaque)],
            Pipelines.VertexLayout,
            Rasterizer: RasterizerState.Default with { Fill = FillMode.Wireframe },
            Name: "wire"
        )));

        Assert.Contains("HasWireframe", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Two pipelines over the same shaders and layout share one program.</summary>
    /// <remarks>
    ///     The permutation case a material system produces by the hundred. Blend state, cull mode and
    ///     depth comparison are loose state in GL and affect nothing about the program, so compiling
    ///     one per pipeline would compile the same GLSL over and over — and compilation is the
    ///     slowest thing a GL driver does.
    /// </remarks>
    [Fact]
    public void SharesOneProgramAcrossStateOnlyPermutations() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));
        var layout = Pipelines.Layout(device);

        Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Default, layout: layout);
        Pipelines.Handle(device, BlendState.AlphaBlend, DepthStencilState.Disabled, layout: layout);
        Pipelines.Handle(device, BlendState.Additive, DepthStencilState.TestOnly, layout: layout);

        Assert.Equal(1, device.ProgramCount);
        Assert.Equal(1, gl.Count("LinkProgram"));
    }

    /// <summary>Two pipelines over the same shaders and different layouts do not.</summary>
    /// <remarks>
    ///     The binding indices are baked into the translated source, so a cache keyed on shaders
    ///     alone would hand the second pipeline a program whose samplers point at the first's texture
    ///     units.
    /// </remarks>
    [Fact]
    public void DoesNotShareAProgramAcrossLayouts() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Default, layout: Pipelines.Layout(device));
        Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Default, layout: Pipelines.Layout(device));

        Assert.Equal(2, device.ProgramCount);
    }

    /// <summary>A compile failure reports the driver's log and the numbered source.</summary>
    /// <remarks>
    ///     The source the driver saw, not the source the engine wrote — they differ by the version
    ///     line, the binding rewrite and the clip fixup, and a line number against the wrong one is
    ///     worse than no line number.
    /// </remarks>
    [Fact]
    public void ReportsTheTranslatedSourceOnACompileFailure() {
        var gl = new RecordingGlApi { CompileLog = "0:12(3): error: no matching function" };
        using var device = new GlDevice(new(gl));

        var error = Assert.Throws<InvalidOperationException>(
            () => Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled)
        );

        Assert.Contains("no matching function", error.Message, StringComparison.Ordinal);
        Assert.Contains("translated source", error.Message, StringComparison.Ordinal);
        Assert.Contains("   1| #version 450 core", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A failed link leaves no GL objects behind.</summary>
    [Fact]
    public void CleansUpAfterAFailedLink() {
        var gl = new RecordingGlApi { LinkLog = "vertex output not consumed" };
        using var device = new GlDevice(new(gl));

        Assert.Throws<InvalidOperationException>(
            () => Pipelines.Handle(device, BlendState.Opaque, DepthStencilState.Disabled)
        );

        Assert.Equal(gl.Count("CreateShader"), gl.Count("DeleteShader"));
        Assert.Equal(gl.Count("CreateProgram"), gl.Count("DeleteProgram"));
    }

    /// <summary>Every resource is returned when the device goes.</summary>
    [Fact]
    public void ReturnsEverythingOnDispose() {
        var gl = new RecordingGlApi();
        var device = new GlDevice(new(gl));

        device.CreateBuffer(new(64, BufferUsage.Vertex, MemoryAccess.HostUpload, "a"));
        device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.Sampled, Name: "b"));
        device.CreateSampler(SamplerDescription.LinearClamp);
        Assert.Equal(3, device.LiveResourceCount);

        device.Dispose();

        Assert.Equal(0, device.LiveResourceCount);
        Assert.Equal(1, gl.Count("DeleteBuffer"));
        Assert.Equal(1, gl.Count("DeleteTexture"));
        Assert.Equal(1, gl.Count("DeleteSampler"));
    }

    /// <summary>The three queues are one queue, and the features say so.</summary>
    [Fact]
    public void ReportsOneQueueUnderThreeNames() {
        var gl = new RecordingGlApi();
        using var device = new GlDevice(new(gl));

        Assert.Same(device.GraphicsQueue, device.ComputeQueue);
        Assert.Same(device.GraphicsQueue, device.TransferQueue);
        Assert.False(device.Features.HasAsyncCompute);
        Assert.False(device.Features.HasAsyncTransfer);
    }
}
