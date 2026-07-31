// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;

namespace Vixen.Samples.PbrShowcase;

/// <summary>A grid of materials under one light, tone-mapped onto the swapchain.</summary>
/// <remarks>
///     <para>
///         Five rows of metallic against five columns of roughness, which is the arrangement that
///         makes a microfacet BRDF legible in one picture: down the grid a dielectric becomes a
///         metal, and across it a mirror becomes a diffuse surface. Any mistake in the model shows
///         up as a row or a column behaving unlike its neighbours.
///     </para>
///     <para>
///         <b>Two passes and an HDR target between them.</b> The scene renders into
///         <c>Rgba16Float</c> — radiance, unbounded — and a full-screen pass turns that into the
///         sRGB swapchain. Rendering straight to the swapchain would clamp every specular highlight
///         to white at the moment it was written, which is the difference between a rough metal and
///         a smooth one disappearing entirely.
///     </para>
///     <para>
///         <b>What this is not.</b> It drives the RHI and the render graph directly, like
///         <c>Samples/01</c>, rather than going through <c>Vixen.Rendering</c>'s compositor. That is
///         a deliberate choice for a sample about <em>shading</em>: the compositor's node graph, its
///         render features and its effect system are each worth a sample of their own, and putting
///         all of them here would bury the twelve lines that are actually about the BRDF.
///         <c>Samples/06</c> is where the compositor is the subject.
///     </para>
///     <para>
///         <b>What it still owes.</b> <c>docs/plan/14</c> Phase 5 asks this sample for shadows and
///         image-based lighting from a real environment, and it has neither: the ambient term is the
///         constant-radiance environment a prefiltered cube and a BRDF LUT integrate to, and nothing
///         casts. Both are content-pipeline work — an importer that produces the cube, and a shadow
///         atlas the renderer owns — rather than shader work, and the README says so at more length
///         so that nobody mistakes the placeholder for the design.
///     </para>
/// </remarks>
public sealed class PbrShowcaseGame : Game {
    const int Rows = 5;
    const int Columns = 5;

    /// <summary>The push-constant block both passes share the shape of: a matrix and a vector.</summary>
    /// <remarks>
    ///     Eighty bytes, which is inside the RHI's guaranteed 128 and therefore inside what every
    ///     backend can carry — including the OpenGL one, where push constants are emulated as a
    ///     <c>vec4</c> array and the floor is what keeps that cheap
    ///     (<c>docs/rhi-backend-mapping.md</c> § 3).
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct DrawConstants {
        public Matrix4x4 Model;
        public Vector4 Material;
    }

    /// <summary>What the per-frame uniform block holds.</summary>
    /// <remarks>
    ///     ⚠ Every member is a <c>vec4</c> or a <c>mat4</c>, and that is not stylistic. <c>std140</c>
    ///     rounds a <c>vec3</c> up to sixteen bytes anyway, so a block written as three floats and
    ///     read as a <c>vec3</c> agrees only by accident — and the accident stops when a member is
    ///     added above it. Padding deliberately is the version of this that survives editing.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ViewConstants {
        public Matrix4x4 ViewProjection;
        public Vector4 Eye;
        public Vector4 LightDirection;
        public Vector4 LightColour;
        public Vector4 Ambient;
    }

    VulkanDevice? device;
    TransientResourcePool? pool;
    RenderGraph? graph;
    ISwapChain? swapChain;
    ILogger? log;

    BufferHandle vertices;
    BufferHandle indices;
    BufferHandle viewConstants;
    int indexCount;

    DescriptorSetLayoutHandle viewLayout;
    DescriptorSetLayoutHandle sceneLayout;
    DescriptorSetHandle viewSet;
    SamplerHandle sceneSampler;

    /// <summary>Where the tonemap pass's set comes from.</summary>
    /// <remarks>
    ///     <para>
    ///         Not a set of its own, and the difference is the kind that only a validation layer
    ///         tells you about. The tonemap pass binds a texture the <em>graph</em> owns, so the set
    ///         cannot be written until the graph has compiled — and rewriting one set every frame
    ///         points a descriptor at something else while the GPU is still reading it for the frame
    ///         before. Most drivers do that silently.
    ///     </para>
    ///     <para>
    ///         <see cref="DescriptorAllocator" /> is the answer the RHI already ships: a ring
    ///         <c>FramesInFlight</c> deep, content-addressed so that identical writes share a set.
    ///         Reaching for it here rather than writing the ring by hand is the point — a sample is
    ///         where somebody learns which piece to use.
    ///     </para>
    /// </remarks>
    DescriptorAllocator? descriptors;

    PipelineLayoutHandle scenePipelineLayout;
    PipelineLayoutHandle tonemapPipelineLayout;
    PipelineHandle scenePipeline;
    PipelineHandle tonemapPipeline;

    ShaderHandle sceneVertex;
    ShaderHandle sceneFragment;
    ShaderHandle tonemapVertex;
    ShaderHandle tonemapFragment;

    bool lost;
    bool waiting;

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        config.Name = "PBR Showcase";
        config.Window = new() { Title = "Vixen — PBR Showcase", Size = new(1280, 720), IsVisible = true };

        // The sample builds its own device, frame and present pass — a second host swapchain on the
        // same surface would be two things presenting to one window.
        config.Graphics.Enabled = false;
    }

    /// <inheritdoc />
    protected override void OnInitialise() => log = Services.LoggerFactory.CreateLogger("PbrShowcase");

    /// <inheritdoc />
    protected override bool OnEvent(in PlatformEvent platformEvent) {
        if (platformEvent.Kind is PlatformEventKind.Suspending) {
            Release();
        }

        return false;
    }

    /// <inheritdoc />
    protected override void OnRender(GameTime time) {
        if (lost || !EnsureDevice()) {
            return;
        }

        device!.BeginFrame();
        var status = swapChain!.AcquireNextImage(out var view);

        if (status is SwapChainStatus.OutOfDate) {
            Recreate();
            return;
        }

        if (status is SwapChainStatus.DeviceLost) {
            if (log is not null) {
                SampleLog.DeviceLost(log);
            }

            lost = true;
            return;
        }

        UpdateView(time);
        descriptors!.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "frame")) {
            var backbuffer = graph!.ImportTexture(
                swapChain.CurrentTexture,
                view,
                new(swapChain.Format, swapChain.Size.X, swapChain.Size.Y, TextureUsage.ColourTarget, Name: "backbuffer"),
                ResourceState.Undefined,
                ResourceState.Present
            );

            // Declared, not imported: the graph owns the HDR target's lifetime and its aliasing, and
            // deriving the barrier between the two passes from the declaration is the whole reason
            // the render graph exists.
            var scene = graph.CreateTexture(new(
                PixelFormat.Rgba16Float,
                swapChain.Size.X,
                swapChain.Size.Y,
                TextureUsage.ColourTarget | TextureUsage.Sampled,
                Name: "scene"
            ));

            var depth = graph.CreateTexture(new(
                PixelFormat.Depth32Float,
                swapChain.Size.X,
                swapChain.Size.Y,
                TextureUsage.DepthStencilTarget,
                Name: "depth"
            ));

            RecordScene(scene, depth);
            RecordTonemap(scene, backbuffer);

            graph.Execute(commands);
            graph.Reset();

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();

        if (swapChain.Present() is SwapChainStatus.OutOfDate or SwapChainStatus.Suboptimal) {
            Recreate();
        }
    }

    /// <inheritdoc />
    protected override void OnShutdown() => Release();

    void RecordScene(GraphTexture scene, GraphTexture depth) => graph!.AddPass("scene", pass => {
        pass.ColourAttachment(scene, LoadAction.Clear, new(0.02f, 0.025f, 0.035f, 1f));

        // Cleared to zero, which is *far*: depth is reversed and the comparison is Greater. Clearing
        // to one here is the classic mistake and depth-tests the whole scene away, with no error
        // from anywhere — which is why the golden suite has a fixture for exactly this.
        pass.DepthAttachment(depth, LoadAction.Clear);

        pass.Execute(context => {
            var list = context.CommandList;
            list.BindPipeline(scenePipeline);
            list.BindDescriptorSet(DescriptorSetSlot.PerFrame, viewSet);
            list.BindVertexBuffer(0, vertices);
            list.BindIndexBuffer(indices, IndexFormat.UInt16);

            for (var row = 0; row < Rows; row++) {
                for (var column = 0; column < Columns; column++) {
                    var constants = new DrawConstants {
                        Model = Matrix4x4.Compose(
                            new(0.42f),
                            Quaternion.Identity,
                            new((column - ((Columns - 1) * 0.5f)) * 1.1f, (row - ((Rows - 1) * 0.5f)) * 1.1f, 0f)
                        ),
                        Material = new(
                            row / (float)(Rows - 1),
                            column / (float)(Columns - 1),

                            // The base colour, one value for the whole grid. Varying it as well
                            // would confound the two axes the grid exists to separate.
                            1f,
                            0f
                        )
                    };

                    list.PushConstants(
                        ShaderStage.Vertex | ShaderStage.Fragment,
                        0,
                        MemoryMarshal.AsBytes(new ReadOnlySpan<DrawConstants>(in constants))
                    );

                    list.DrawIndexed(indexCount);
                }
            }
        });
    });

    void RecordTonemap(GraphTexture scene, GraphTexture backbuffer) => graph!.AddPass("tonemap", pass => {
        // Stated, and it is what places the barrier: the graph turns "this pass reads that texture"
        // into the transition out of ColourTarget and into ShaderRead, and a pass that rendered into
        // the target and forgot to say it read it would sample it mid-write.
        pass.Reads(scene);
        pass.ColourAttachment(backbuffer, LoadAction.DontCare);

        pass.Execute(context => {
            var list = context.CommandList;

            // Allocated here rather than written once at start-up, because the graph may hand the
            // pass a different physical texture each frame: transients are aliased, and a set
            // written at start-up would point at whatever the pool assigned on the first one.
            var set = descriptors!.Allocate(
                sceneLayout,
                [
                    DescriptorWrite.Texture(0, graph!.ViewOf(scene)),
                    DescriptorWrite.SamplerAt(1, sceneSampler)
                ]
            );

            list.BindPipeline(tonemapPipeline);
            list.BindDescriptorSet(DescriptorSetSlot.PerFrame, set);

            var exposure = new Vector4(1.6f, 0f, 0f, 0f);

            list.PushConstants(
                ShaderStage.Fragment,
                0,
                MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in exposure))
            );

            list.Draw(3);
        });
    });

    void UpdateView(GameTime time) {
        var angle = (float)time.Total.TotalSeconds * 0.35f;

        // Far enough back that the grid clears the frame at 16:9. Five rows at 1.1 apart plus a
        // radius each side is 5.2 units tall, and a 45° vertical field of view shows 0.83 units per
        // unit of distance — so anything nearer than about 6.3 crops the top row, which is the row
        // whose behaviour the sample most wants shown.
        var eye = new Vector3(MathF.Sin(angle) * 2.4f, 0.5f, MathF.Cos(angle) * 7.6f);

        var projection = Matrix4x4.PerspectiveFieldOfView(
            MathF.PI / 4f,
            swapChain!.Size.X / (float)Math.Max(1, swapChain.Size.Y),
            0.1f,
            100f
        );

        var constants = new ViewConstants {
            ViewProjection = Matrix4x4.LookAt(eye, Vector3.Zero, Vector3.UnitY) * projection,
            Eye = new(eye, 1f),
            LightDirection = new(Vector3.Normalize(new(0.45f, 0.7f, 0.55f)), 0f),

            // Well above one, because this is radiance and the target is float. A light clamped to
            // [0, 1] gives a picture with no headroom, which is the same as not having an HDR pass.
            LightColour = new(4.2f, 4.0f, 3.7f, 0f),
            Ambient = new(0.12f, 0.14f, 0.18f, 0f)
        };

        device!.Write(viewConstants, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<ViewConstants>(in constants)));
    }

    /// <summary>Builds the device and everything on it, once there is something to present to.</summary>
    bool EnsureDevice() {
        if (device is not null) {
            return true;
        }

        if (Services.Window is not { } window || !window.Surface.Handle.CanPresent) {
            if (!waiting && log is not null) {
                SampleLog.NoWindow(log);
                waiting = true;
            }

            return false;
        }

        waiting = false;

        device = VulkanDevice.Create(new() {
            Surface = window.Surface.Handle,
            Logger = Services.LoggerFactory.CreateLogger("Vulkan")
        });

        pool = new(device);
        graph = new(device, pool);

        swapChain = device.CreateSwapChain(new(
            window.Surface.Handle,
            new(window.FramebufferSize.X, window.FramebufferSize.Y),
            PixelFormat.Bgra8UNormSrgb
        ));

        BuildGeometry();
        BuildBindings();
        BuildPipelines();

        if (log is not null) {
            SampleLog.SceneReady(
                log,
                Rows,
                Columns,
                device.Adapter.Name,
                device.Adapter.Kind,
                swapChain.Size.X,
                swapChain.Size.Y,
                swapChain.Format
            );
        }

        return true;
    }

    void BuildGeometry() {
        var (mesh, triangles) = Sphere.Build();
        indexCount = triangles.Length;

        // Host-visible rather than staged. A 32×16 sphere is about twenty kilobytes and is written
        // once; a staging copy would cost a transfer, a barrier and forty lines to save nothing.
        // A real mesh belongs in device memory, which is what the asset pipeline's uploader does.
        vertices = Upload(MemoryMarshal.AsBytes(mesh.AsSpan()), BufferUsage.Vertex, "sphere vertices");
        indices = Upload(MemoryMarshal.AsBytes(triangles.AsSpan()), BufferUsage.Index, "sphere indices");
    }

    void BuildBindings() {
        viewLayout = device!.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment)],
            "view"
        ));

        sceneLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "scene"
        ));

        viewSet = device.CreateDescriptorSet(viewLayout, "view");
        descriptors = new(device, "PbrShowcase");

        viewConstants = device.CreateBuffer(new(
            Marshal.SizeOf<ViewConstants>(),
            BufferUsage.Uniform,
            MemoryAccess.HostUpload,
            "view constants"
        ));

        device.UpdateDescriptorSet(viewSet, [DescriptorWrite.Uniform(0, viewConstants)]);
        sceneSampler = device.CreateSampler(SamplerDescription.LinearClamp);
    }

    void BuildPipelines() {
        sceneVertex = device!.CreateShader(ShaderStage.Vertex, Load("pbr.vert.spv"), "pbr vertex");
        sceneFragment = device.CreateShader(ShaderStage.Fragment, Load("pbr.frag.spv"), "pbr fragment");
        tonemapVertex = device.CreateShader(ShaderStage.Vertex, Load("tonemap.vert.spv"), "tonemap vertex");
        tonemapFragment = device.CreateShader(ShaderStage.Fragment, Load("tonemap.frag.spv"), "tonemap fragment");

        scenePipelineLayout = device.CreatePipelineLayout(new(
            [viewLayout],
            [new(ShaderStage.Vertex | ShaderStage.Fragment, 0, Marshal.SizeOf<DrawConstants>())],
            "scene layout"
        ));

        scenePipeline = device.CreateGraphicsPipeline(new(
            sceneVertex,
            sceneFragment,
            scenePipelineLayout,
            [new(PixelFormat.Rgba16Float, BlendState.Opaque)],
            [
                new(
                    Vertex.Stride,
                    [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X3, 12)]
                )
            ],
            Rasterizer: RasterizerState.Default,
            DepthStencil: DepthStencilState.Default,
            DepthFormat: PixelFormat.Depth32Float,
            Name: "scene"
        ));

        tonemapPipelineLayout = device.CreatePipelineLayout(new(
            [sceneLayout],
            [new(ShaderStage.Fragment, 0, 16)],
            "tonemap layout"
        ));

        tonemapPipeline = device.CreateGraphicsPipeline(new(
            tonemapVertex,
            tonemapFragment,
            tonemapPipelineLayout,
            [new(swapChain!.Format, BlendState.Opaque)],
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "tonemap"
        ));
    }

    BufferHandle Upload(ReadOnlySpan<byte> data, BufferUsage usage, string name) {
        var handle = device!.CreateBuffer(new(data.Length, usage, MemoryAccess.HostUpload, name));
        device.Write(handle, 0, data);
        return handle;
    }

    /// <summary>Tears the device down, in the reverse of the order it was built.</summary>
    void Release() {
        if (device is null) {
            return;
        }

        device.WaitIdle();
        swapChain?.Dispose();

        device.Destroy(tonemapPipeline);
        device.Destroy(scenePipeline);
        device.Destroy(tonemapPipelineLayout);
        device.Destroy(scenePipelineLayout);
        device.Destroy(tonemapFragment);
        device.Destroy(tonemapVertex);
        device.Destroy(sceneFragment);
        device.Destroy(sceneVertex);
        device.Destroy(sceneSampler);
        descriptors?.Dispose();
        device.Destroy(viewSet);
        device.Destroy(sceneLayout);
        device.Destroy(viewLayout);
        device.Destroy(viewConstants);
        device.Destroy(indices);
        device.Destroy(vertices);

        pool?.Dispose();
        device.Dispose();

        swapChain = null;
        descriptors = null;
        graph = null;
        pool = null;
        device = null;
    }

    void Recreate() {
        if (device is null || swapChain is null || Services.Window is not { } window) {
            return;
        }

        device.WaitIdle();
        swapChain.Resize(new(window.FramebufferSize.X, window.FramebufferSize.Y));

        if (log is not null) {
            SampleLog.SwapChainRebuilt(log, swapChain.Size.X, swapChain.Size.Y);
        }
    }

    static byte[] Load(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(entry => entry.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
