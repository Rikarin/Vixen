// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Core.Imaging;
using Vixen.Ui.Testing.Visual;
using Graph = Vixen.Graphics.RenderGraph.RenderGraph;
using GraphTexture = Vixen.Graphics.RenderGraph.GraphTexture;
using TransientResourcePool = Vixen.Graphics.RenderGraph.TransientResourcePool;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>A device, a render graph and a readback, set up once per fixture.</summary>
/// <remarks>
///     <para>
///         Everything renders offscreen through the render graph rather than straight to a command
///         list. That is deliberate: a golden-image suite that bypassed the graph would verify the
///         backend and leave the layer most engine code actually uses untested against a picture, and
///         the graph's barriers and store actions are exactly the sort of thing whose mistakes are
///         invisible until they are visible.
///     </para>
///     <para>
///         Small images — 128×128. A golden suite's cost is dominated by the bytes in git, not by the
///         rendering, and a rasterisation or blending mistake is as visible at 128 as at 1024.
///     </para>
/// </remarks>
sealed class Fixture : IDisposable {
    /// <summary>How large every fixture renders.</summary>
    public const int Side = 128;

    readonly VulkanDevice device;
    readonly TransientResourcePool pool;
    readonly List<Action> cleanup = [];

    Fixture(VulkanDevice device) {
        this.device = device;
        pool = new(device);
        Graph = new(device, pool);
    }

    /// <summary>The graph every fixture declares into.</summary>
    public Graph Graph { get; }

    /// <summary>The device, for the resources a fixture needs outside the graph.</summary>
    public VulkanDevice Device => device;

    /// <summary>Opens a device, or says why it could not.</summary>
    public static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (!VulkanDevice.TryCreate(new(), out var device, out reason)) {
            fixture = null;
            return false;
        }

        fixture = new(device);
        return true;
    }

    /// <summary>Loads one of the fixture shaders.</summary>
    /// <param name="name">Its file name, such as <c>triangle.vert.spv</c>.</param>
    /// <param name="stage">Which stage it is.</param>
    public ShaderHandle Shader(string name, ShaderStage stage) {
        var path = Path.Combine(AppContext.BaseDirectory, "Shaders", name);
        var handle = device.CreateShader(stage, File.ReadAllBytes(path), name);
        cleanup.Add(() => device.Destroy(handle));
        return handle;
    }

    /// <summary>Creates a buffer that lives as long as the fixture.</summary>
    public BufferHandle Buffer<T>(ReadOnlySpan<T> data, BufferUsage usage) where T : unmanaged {
        var bytes = MemoryMarshal.AsBytes(data);

        var handle = device.CreateBuffer(
            new(bytes.Length, usage, MemoryAccess.HostUpload, $"fixture {usage}")
        );

        device.Write(handle, 0, bytes);
        cleanup.Add(() => device.Destroy(handle));
        return handle;
    }

    /// <summary>Creates a pipeline that lives as long as the fixture.</summary>
    /// <param name="vertex">The vertex shader.</param>
    /// <param name="fragment">The fragment shader.</param>
    /// <param name="blend">How its fragments combine with the target.</param>
    /// <param name="depth">The depth and stencil tests.</param>
    /// <param name="vertices">The vertex buffer layouts.</param>
    /// <param name="pushConstantBytes">How many bytes of push constants, or <c>0</c> for none.</param>
    /// <param name="rasterizer">
    ///     How triangles become fragments. Two-sided by default, because most fixtures are about
    ///     something else and a winding mistake in the fixture's own geometry should not hide it.
    /// </param>
    /// <param name="topology">What the vertices mean.</param>
    /// <param name="sets">Descriptor set layouts, for a fixture that binds resources.</param>
    /// <param name="targets">
    ///     The colour targets, or <see langword="null" /> for one opaque <c>Rgba8UNorm</c>. Given
    ///     explicitly by the fixtures about multiple targets and about sRGB.
    /// </param>
    public PipelineHandle Pipeline(
        ShaderHandle vertex,
        ShaderHandle fragment,
        BlendState blend,
        DepthStencilState depth,
        VertexBufferLayout[]? vertices = null,
        int pushConstantBytes = 0,
        RasterizerState? rasterizer = null,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        DescriptorSetLayoutHandle[]? sets = null,
        ColourTargetState[]? targets = null,
        PixelFormat depthFormat = PixelFormat.Depth32Float
    ) {
        var layout = device.CreatePipelineLayout(new(
            sets ?? [],
            pushConstantBytes > 0 ? [new(ShaderStage.Vertex, 0, pushConstantBytes)] : null,
            "fixture layout"
        ));

        var pipeline = device.CreateGraphicsPipeline(new(
            vertex,
            fragment,
            layout,
            targets ?? [new(PixelFormat.Rgba8UNorm, blend)],
            vertices,
            topology,
            rasterizer ?? RasterizerState.TwoSided,
            depth,
            depth.DepthTest || depth.StencilTest ? depthFormat : PixelFormat.Undefined,
            Name: "fixture"
        ));

        cleanup.Add(() => {
            device.Destroy(pipeline);
            device.Destroy(layout);
        });

        return pipeline;
    }

    /// <summary>A sampler that lives as long as the fixture.</summary>
    public SamplerHandle Sampler(in SamplerDescription description) {
        var handle = device.CreateSampler(description);
        cleanup.Add(() => device.Destroy(handle));
        return handle;
    }

    /// <summary>A descriptor set layout that lives as long as the fixture.</summary>
    public DescriptorSetLayoutHandle SetLayout(in DescriptorSetLayoutDescription description) {
        var handle = device.CreateDescriptorSetLayout(description);
        cleanup.Add(() => device.Destroy(handle));
        return handle;
    }

    /// <summary>A descriptor set that lives as long as the fixture.</summary>
    public DescriptorSetHandle DescriptorSet(DescriptorSetLayoutHandle layout, string name) {
        var handle = device.CreateDescriptorSet(layout, name);
        cleanup.Add(() => device.Destroy(handle));
        return handle;
    }

    /// <summary>Points a set's bindings at resources.</summary>
    public void Bind(DescriptorSetHandle set, params DescriptorWrite[] writes) =>
        device.UpdateDescriptorSet(set, writes);

    /// <summary>
    ///     A small sampled texture, with its contents staged and ready to copy.
    /// </summary>
    /// <param name="name">A name for the debugger.</param>
    /// <param name="side">Its width and height in texels.</param>
    /// <param name="texels">The contents, RGBA8, row-major from the top.</param>
    /// <remarks>
    ///     The staging buffer is returned rather than copied here, because a copy into a texture
    ///     cannot be recorded inside a render pass and everything a graph pass executes is inside
    ///     one. <see cref="Render" />'s <c>before</c> is where it goes.
    /// </remarks>
    public (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) Sampled(
        string name,
        int side,
        ReadOnlySpan<byte> texels
    ) {
        var owned = Owned(
            name,
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm,
            side,
            side
        );

        var staging = device.CreateBuffer(
            new(texels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, $"{name} staging")
        );

        device.Write(staging, 0, texels);
        cleanup.Add(() => device.Destroy(staging));
        return (owned.Texture, owned.View, staging);
    }

    /// <summary>Runs the declared graph and reads the colour target back.</summary>
    /// <param name="colour">The imported target holding the picture.</param>
    /// <param name="before">
    ///     Work to record before the graph runs, for a fixture with a resource of its own to upload.
    /// </param>
    /// <remarks>
    ///     ⚠ <paramref name="before" /> exists because a copy into a texture cannot be recorded inside
    ///     a render pass, and everything a graph pass executes is inside one. A fixture that owns a
    ///     texture the graph does not know about — a glyph atlas, a lookup table — has nowhere else to
    ///     put the transfer.
    /// </remarks>
    public Bitmap Render(GraphTexture colour, Action<ICommandList>? before = null) {
        const int Bytes = Side * Side * 4;

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "golden readback")
        );

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "fixture")) {
            before?.Invoke(commands);
            Graph.Execute(commands);

            // The graph has already transitioned the target to CopySource, because that is the exit
            // state it was imported with. Reading it is the harness's business, not the frame's.
            commands.CopyTextureToBuffer(new(Graph.TextureOf(colour)), new(Side, Side, 1), readback, 0);
            commands.Finish();

            // ⚠ Before the submit, not only after it. Everything the validation layer says about
            // *recording* — a set the pipeline needs and nothing bound, a resource in the wrong
            // layout — it has already said by now, and submitting anyway is submitting work the
            // driver has been told is invalid. That is not a test failure: it is a GPU fault, a dead
            // test process, and every test after this one in the collection never running. One such
            // frame cost fifty-six of them, and the assertion that would have caught it was in the
            // right file — three lines below the submit that killed the process.
            Fail(readback);

            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[Bytes];
        device.Read(readback, 0, pixels);
        device.Destroy(readback);

        Fail(default);

        return new(Side, Side, pixels);
    }

    /// <summary>Throws if the validation layer has said anything, cleaning up first.</summary>
    /// <param name="readback">A buffer to destroy on the way out, or an invalid handle for none.</param>
    /// <remarks>
    ///     The cleanup matters because this is called before the submit as well as after it: throwing
    ///     out of the recording block leaves a command list that is never submitted and a readback
    ///     buffer nobody frees, and a leak reported at device teardown would bury the message that
    ///     says what actually went wrong.
    /// </remarks>
    void Fail(BufferHandle readback) {
        if (VulkanDiagnostics.ErrorCount == 0) {
            return;
        }

        if (readback.IsValid) {
            device.Destroy(readback);
        }

        throw new InvalidOperationException(
            "The fixture produced validation errors, so its picture is meaningless: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>The target a fixture renders into and the harness reads back.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Imported, not created.</b> A transient the graph creates is stored only if something
    ///         inside the graph reads it afterwards — and nothing does, because the readback happens
    ///         outside. So the graph correctly derives <c>StoreAction.DontCare</c>, the contents are
    ///         discarded, and the readback reads whatever was in that memory. That is not a bug in the
    ///         graph; it is the fixture failing to say that the picture outlives the frame.
    ///     </para>
    ///     <para>
    ///         Importing says it. An imported resource is always stored, precisely because the graph
    ///         cannot know what its owner will do with it — and the exit state does the barrier into
    ///         <c>CopySource</c> as well, so the harness does not need one.
    ///     </para>
    ///     <para>
    ///         Every fixture in the suite rendered a discarded target before this was understood, and
    ///         each produced a uniform block of undefined memory. Worth recording, because a
    ///         golden-image suite whose fixtures all render the same wrong thing is one that would
    ///         have been "passing" from the day it was written.
    ///     </para>
    /// </remarks>
    public GraphTexture ColourTarget(string name) {
        var owned = Owned(name, TextureUsage.ColourTarget | TextureUsage.CopySource);
        return Graph.ImportTexture(
            owned.Texture,
            owned.View,
            owned.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );
    }

    /// <summary>A texture and its view, owned by the fixture, for a caller that imports it itself.</summary>
    /// <remarks>
    ///     What <see cref="ColourTarget" /> is built on, exposed for the fixtures that cannot use it:
    ///     a compositor imports its own targets by name, so a harness that had already imported them
    ///     would hand the graph two virtual resources over one texture and get a barrier between a
    ///     pass and itself.
    /// </remarks>
    public (TextureHandle Texture, TextureViewHandle View, TextureDescription Description) Owned(
        string name,
        TextureUsage usage,
        PixelFormat format = PixelFormat.Rgba8UNorm,
        int width = Side,
        int height = Side
    ) {
        var description = new TextureDescription(format, width, height, usage, Name: name);
        var texture = device.CreateTexture(description);
        var view = device.CreateTextureView(texture);

        cleanup.Add(() => {
            device.Destroy(view);
            device.Destroy(texture);
        });

        return (texture, view, description);
    }

    /// <summary>Registers something for the fixture to dispose.</summary>
    public void Owns(Action dispose) => cleanup.Add(dispose);

    /// <summary>A depth target the graph will provide.</summary>
    public GraphTexture DepthTarget(string name) =>
        Graph.CreateTexture(new(
            PixelFormat.Depth32Float,
            Side,
            Side,
            TextureUsage.DepthStencilTarget,
            Name: name
        ));

    /// <summary>A combined depth-stencil target the graph will provide.</summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="DepthTarget" /> rather than replacing it, because a stencil
    ///         buffer is not free and <c>Depth32Float</c> is what the engine's reversed-Z wants
    ///         everywhere it does not need one. A fixture that quietly used the combined format
    ///         would stop testing the format the renderer actually uses.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>Depth32FloatStencil8</c> and not <c>Depth24UNormStencil8</c>, which is the
    ///         obvious choice and is unavailable on Apple hardware: Metal has no 24-bit depth, so
    ///         MoltenVK reports <c>VK_ERROR_FORMAT_NOT_SUPPORTED</c> for it. Vulkan guarantees one of
    ///         the two and not both, and this is the one that is also what reversed-Z wants.
    ///     </para>
    /// </remarks>
    public GraphTexture DepthStencilTarget(string name) =>
        Graph.CreateTexture(new(
            PixelFormat.Depth32FloatStencil8,
            Side,
            Side,
            TextureUsage.DepthStencilTarget,
            Name: name
        ));

    /// <inheritdoc />
    public void Dispose() {
        device.WaitIdle();

        foreach (var action in cleanup) {
            action();
        }

        pool.Dispose();
        device.Dispose();
    }
}
