// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
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
    public PipelineHandle Pipeline(
        ShaderHandle vertex,
        ShaderHandle fragment,
        BlendState blend,
        DepthStencilState depth,
        VertexBufferLayout[]? vertices = null,
        int pushConstantBytes = 0
    ) {
        var layout = device.CreatePipelineLayout(new(
            [],
            pushConstantBytes > 0 ? [new(ShaderStage.Vertex, 0, pushConstantBytes)] : null,
            "fixture layout"
        ));

        var pipeline = device.CreateGraphicsPipeline(new(
            vertex,
            fragment,
            layout,
            [new(PixelFormat.Rgba8UNorm, blend)],
            vertices,
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: depth,
            DepthFormat: depth.DepthTest ? PixelFormat.Depth32Float : PixelFormat.Undefined,
            Name: "fixture"
        ));

        cleanup.Add(() => {
            device.Destroy(pipeline);
            device.Destroy(layout);
        });

        return pipeline;
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
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[Bytes];
        device.Read(readback, 0, pixels);
        device.Destroy(readback);

        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The fixture produced validation errors, so its picture is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        return new(Side, Side, pixels);
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
