// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.VirtualGeometry;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Samples.VirtualGeometry;

/// <summary>
///     One authored mesh through the virtualized-geometry path, on screen: cluster DAG, streamed
///     pages, device-side traversal, one indirect draw — presented as a debug view of the
///     visibility buffer.
/// </summary>
/// <remarks>
///     <para>
///         <b>The sample <c>docs/plan/22-virtualized-geometry.md</c> phase 5 records as owed.</b> Every
///         piece of drawing a frame existed and was tested, and nothing outside a test project put
///         them together: <c>Vixen.App.Game</c> was a window and a frame loop, and the samples opened
///         a device and issued draws directly. This is the join — a <see cref="Game" /> whose frame
///         is <see cref="SceneRenderHost.Draw" />, with the frame's shape coming from a YAML document
///         rather than from code.
///     </para>
///     <para>
///         <b>What the sample is about is the division of labour.</b> The document decides where the
///         passes go (<see cref="Document" /> — a depth clear, the cluster traversal, the
///         visibility-buffer draw). The host decides what exists: a device, an effect system, the
///         <see cref="VirtualGeometrySystem" /> that owns every buffer of the virtualized path, and
///         the textures lent to the frame by name. Neither knows the other's half, which is what lets
///         one document run against a swapchain here and a scratch texture in the golden tests.
///     </para>
///     <para>
///         <b>What is on screen is phase 4's output, deliberately.</b> The visibility buffer holds a
///         cluster and triangle identity per pixel, and the present pass hashes identities to
///         colours — so the patches you see <em>are</em> the traversal's cut, and watching them
///         change size as the camera orbits is watching per-cluster level of detail decide, per
///         frame, with the host never learning a triangle count. Shading those identities for real
///         is the material resolve's job and needs the whole clustered-lighting frame around it,
///         which is a different sample.
///     </para>
///     <para>
///         <b>Effects compile at start-up, which no shipped game would do.</b> A shipped game loads
///         the bundle its content build baked; this sample runs from the repository and compiles
///         <c>Raven/Library</c> on first use, exactly as the golden device tests do, because the
///         repository has the sources and a sample should not need a content build to run. The cost
///         is a moment on the first frame that resolves each variant.
///     </para>
/// </remarks>
public sealed class VirtualGeometryGame : Game {
    VulkanDevice? device;
    ISwapChain? swapChain;
    ILogger? log;

    EffectSystem? effects;
    ComputePipelineCache? computePipelines;
    VirtualGeometrySystem? geometry;
    SceneRenderHost? host;
    RenderView? camera;

    TextureHandle visibility;
    TextureViewHandle visibilityView;

    DescriptorSetLayoutHandle presentLayout;
    DescriptorAllocator? descriptors;
    SamplerHandle pointSampler;
    PipelineLayoutHandle presentPipelineLayout;
    PipelineHandle presentPipeline;
    ShaderHandle presentVertex;
    ShaderHandle presentFragment;

    bool lost;
    bool waiting;

    // The orbit the camera is on: drag with the primary button to steer it. Until the first drag it
    // turns by itself, so the sample shows its point unattended; the first drag hands it over.
    float yaw;
    float pitch = 0.34f;
    bool dragging;
    bool steered;

    /// <summary>The camera's vertical field of view.</summary>
    const float FieldOfView = MathF.PI / 3f;

    /// <summary>How far one logical point of drag turns the orbit, in radians.</summary>
    /// <remarks>
    ///     Roughly a half-turn across a 720-point window, which is the "grab the globe" rate: the
    ///     surface under the cursor approximately follows it at the sphere's near face.
    /// </remarks>
    const float DragRate = MathF.PI / 720f;

    /// <summary>How far the orbit may tilt, short of the poles where LookAt's up degenerates.</summary>
    const float PitchLimit = 1.45f;

    /// <summary>
    ///     The frame, as a document: clear the depth, traverse, draw the visibility buffer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same document <c>VirtualGeometryDeviceTests</c> runs on a scratch device, and that
    ///         is the point of it being a document: the configuration a project ships is the one the
    ///         tests exercise. The names — <c>Camera</c>, <c>SceneDepth</c>,
    ///         <c>VisibilityBuffer</c> — are the seams where this host binds its view and its
    ///         textures.
    ///     </para>
    ///     <para>
    ///         ⚠ The depth clear is load-bearing. <c>VisibilityBuffer</c> <em>loads</em> depth — it is
    ///         designed to share the frame's depth with classic draws — so a document without a
    ///         producer for <c>SceneDepth</c> is refused by the render graph by name, rather than
    ///         being a frame that depth-tests against garbage.
    ///     </para>
    /// </remarks>
    const string Document = """
        version: 2
        resources:
          - name: SceneColour
            format: Rgba16Float
            usage: ColourTarget, Sampled, Storage
          - name: SceneDepth
            format: Depth32Float

            # Sampled as well as an attachment, because phase 6's merge reads the depth the hardware
            # raster wrote to decide whether its own surface is in front of it. Costs nothing on a
            # device with no 64-bit atomics, where that pass is not in the graph at all.
            usage: DepthStencilTarget, Sampled
        stages:
          - name: Opaque
        game: !Sequence
          name: Frame
          children:
            - !RenderPass
              name: ClearDepth
              depthTarget: SceneDepth
            - !ClusterCulling
              name: Traversal
            - !VisibilityBuffer
              name: Visibility
              view: Camera
              depth: SceneDepth
              output: VisibilityBuffer
        """;

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        config.Name = "Virtual Geometry";

        // ⚠ `IsVisible` follows `Headless`. `AppConfig.Apply` reads the command line *before* this
        // hook — deliberately, so a game can override what an operator asked for — which makes an
        // unconditional `true` here an assignment over `--vixen-headless`. It is not what opened a
        // window on a headless run (see `Program`), because the headless platform's windows have no
        // picture whatever this says; it is that the sample would otherwise be telling a window
        // nobody can see to show itself, and `HeadlessPlatform.CreateWindow` posts a `WindowShown`
        // for it.
        config.Window = new() {
            Title = "Vixen — Virtual Geometry",
            Size = new(1280, 720),
            IsVisible = !config.Headless
        };

        // The division of labour this sample is about is between a document and a host it builds
        // itself — the VirtualGeometrySystem, its page pool and the visibility buffer are all lent to
        // the frame by hand. The host's own renderer would be a second device and a second swapchain
        // on the same window.
        config.Graphics.Enabled = false;
    }

    /// <inheritdoc />
    protected override void OnInitialise() => log = Services.LoggerFactory.CreateLogger("VirtualGeometry");

    /// <inheritdoc />
    /// <remarks>
    ///     The drag is read straight off the platform events, which is the layer a sample this size
    ///     wants: <c>Vixen.Input</c>'s action maps are for games with bindings to manage, and one
    ///     button and a delta is not that. Never handled (<see langword="false" />), so the host
    ///     still sees every event — see <see cref="Game.OnEvent" />.
    /// </remarks>
    protected override bool OnEvent(in PlatformEvent platformEvent) {
        switch (platformEvent.Kind) {
            case PlatformEventKind.Suspending:
                Release();
                break;

            case PlatformEventKind.MouseButtonDown when platformEvent.MouseButton == MouseButton.Primary:
                // The handover is seamless because UpdateCamera keeps `yaw` current while the orbit
                // still turns itself — a first drag that snapped the camera somewhere else would
                // read as a glitch.
                dragging = true;
                steered = true;
                break;

            case PlatformEventKind.MouseButtonUp when platformEvent.MouseButton == MouseButton.Primary:
                dragging = false;
                break;

            case PlatformEventKind.MouseMoved when dragging:
                // Grab-the-globe: drag right pulls the near face right, which swings the eye left.
                yaw -= platformEvent.Delta.X * DragRate;
                pitch = Math.Clamp(pitch + (platformEvent.Delta.Y * DragRate), -PitchLimit, PitchLimit);
                break;
        }

        return false;
    }

    /// <inheritdoc />
    protected override void OnRender(GameTime time) {
        if (lost || !EnsureDevice()) {
            return;
        }

        device!.BeginFrame();
        var status = swapChain!.AcquireNextImage(out var backbuffer);

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

        UpdateCamera(time);
        descriptors!.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "frame")) {
            // The whole virtualized frame — depth clear, page copies, traversal dispatch, indirect
            // draw — recorded by the host from the document. Nothing here knows how many clusters
            // were drawn, which is the property the entire pipeline exists to have.
            host!.Draw(commands);

            RecordPresent(commands, backbuffer);

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();

        if (swapChain.Present() is SwapChainStatus.OutOfDate or SwapChainStatus.Suboptimal) {
            Recreate();
        }
    }

    /// <inheritdoc />
    protected override void OnShutdown() {
        // The one number that says the path did something: how many clusters the last serviced
        // frame's traversal accepted. It is read back beside the page requests, a frame stale, and
        // zero here after a real run means a frame that drew no virtualized geometry at all —
        // which is what a CI leg running `--vixen-frames` wants to be able to grep for.
        if (log is not null && geometry is not null) {
            SampleLog.TraversalSettled(
                log,
                geometry.Visibility.VisibleClusters,
                geometry.Visibility.ClusterCount,
                geometry.Visibility.SoftwareClusters,
                geometry.Residency.ResidentPages
            );
        }

        Release();
    }

    /// <summary>The debug view: the visibility buffer's identities, hashed to colours.</summary>
    /// <remarks>
    ///     A raw render pass rather than a compositor node, because the pass belongs to the sample
    ///     and not to the frame: the document's frame ends at the visibility buffer, whose import
    ///     exit state is <see cref="ResourceState.ShaderRead" /> — so by the time this records, the
    ///     graph has already placed the barrier that makes sampling it legal.
    /// </remarks>
    void RecordPresent(ICommandList commands, TextureViewHandle backbuffer) {
        commands.Barrier(
            new([], [new(swapChain!.CurrentTexture, ResourceState.Undefined, ResourceState.ColourTarget)])
        );

        commands.BeginRenderPass(new([new ColourAttachment(backbuffer, LoadAction.DontCare)], null, "present"));

        // The visible list rides along so the fragment stage can turn a pixel's slot into its
        // cluster. The frame left the buffer in ShaderRead — the raster's count copy transitions it
        // back — and this pass records after host.Draw on the same queue, so no barrier is ours to
        // place.
        var set = descriptors!.Allocate(
            presentLayout,
            [
                DescriptorWrite.Texture(0, visibilityView),
                DescriptorWrite.SamplerAt(1, pointSampler),
                DescriptorWrite.Storage(2, geometry!.Visibility.Visible)
            ]
        );

        commands.BindPipeline(presentPipeline);
        commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, set);
        commands.Draw(3);
        commands.EndRenderPass();

        commands.Barrier(
            new([], [new(swapChain.CurrentTexture, ResourceState.ColourTarget, ResourceState.Present)])
        );
    }

    /// <summary>Orbits the mesh, moving in and out so the traversal's cut visibly changes.</summary>
    /// <remarks>
    ///     The direction is the drag's once the user has taken over, and the sample's own until
    ///     then. The distance keeps breathing either way — between two and a half and nine radii,
    ///     the whole range the error metric decides over — because the level of detail responding to
    ///     it is the thing being demonstrated.
    /// </remarks>
    void UpdateCamera(GameTime time) {
        if (!steered) {
            yaw = (float)time.Total.TotalSeconds * 0.3f;
        }

        var distance = 5.75f + (3.25f * MathF.Sin((float)time.Total.TotalSeconds * 0.21f));

        var eye = new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch)
        ) * distance;

        var projection = Matrix4x4.PerspectiveFieldOfView(
            FieldOfView,
            swapChain!.Size.X / (float)Math.Max(1, swapChain.Size.Y),
            0.1f,
            1000f
        );

        // The one view the document names. Setting ViewProjection derives the frustum the traversal
        // culls against; the screen-height scale is what turns a cluster's object-space error into
        // pixels — see GpuClusterCulling.ErrorScaleFor.
        camera!.ViewProjection = Matrix4x4.LookAt(eye, Vector3.Zero, Vector3.UnitY) * projection;
        camera.Position = eye;
        camera.ScreenHeightScale = 1f / MathF.Tan(FieldOfView * 0.5f);

        geometry!.Feature.ScreenHeight = swapChain.Size.Y;
        host!.FrameSize = new(swapChain.Size.X, swapChain.Size.Y);
    }

    /// <summary>Builds the device and the whole stack on it, once there is something to present to.</summary>
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

        swapChain = device.CreateSwapChain(new(
            window.Surface.Handle,
            new(window.FramebufferSize.X, window.FramebufferSize.Y),
            PixelFormat.Bgra8UNormSrgb
        ));

        BuildRenderer();
        BuildContent();
        BuildPresent();

        return true;
    }

    /// <summary>The stack: effects, the virtualized systems, and the host that runs the document.</summary>
    void BuildRenderer() {
        effects = new();
        effects.AddProvider(new LibraryEffects(new(device!)));

        computePipelines = new(device!);

        // Sixty-four slots of the default page size is eight megabytes of resident geometry — the
        // whole streaming budget, for every virtualized mesh this system ever registers.
        geometry = new(device!, slots: 64);

        geometry.Effects = effects;
        geometry.Pipelines = computePipelines;
        geometry.Modules = new(device!);

        host = new(device!, effects);

        geometry.Register(host.System);
        geometry.Supply(host.Builder);

        // The view the document's Visibility node names. One object, bound by name — the same seam
        // the golden tests bind a scratch view through.
        camera = new("camera");
        host.Builder.Views["Camera"] = camera;

        host.Load(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));
        host.FrameSize = new(swapChain!.Size.X, swapChain.Size.Y);

        // The visibility buffer, lent to the frame by name. Imported rather than left to the node's
        // own transient for the reason the golden fixture imports it: the present pass reads it
        // *outside* the graph, and a transient nothing inside the frame reads is memory the graph is
        // entitled to discard. The exit state is what makes sampling it afterwards legal.
        var description = new TextureDescription(
            GpuClusterRaster.Format,
            swapChain.Size.X,
            swapChain.Size.Y,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "VisibilityBuffer"
        );

        visibility = device!.CreateTexture(description);
        visibilityView = device.CreateTextureView(visibility);

        host.Import(
            "VisibilityBuffer",
            new(visibility, visibilityView, description, ResourceState.Undefined, ResourceState.ShaderRead)
        );
    }

    /// <summary>
    ///     Builds the mesh, cuts it into a cluster DAG, pages it, and places one instance of it.
    /// </summary>
    /// <remarks>
    ///     In a game the DAG and the pages are two artefacts the model importer wrote and this is a
    ///     load. The sample builds them at start-up because a sample should not need a content build
    ///     to run — and because the builder being callable at runtime is what the editor's reimport
    ///     uses anyway.
    /// </remarks>
    void BuildContent() {
        var (positions, indices) = Icosphere.Build(subdivisions: 5);

        var input = new MeshletBuildInput { Positions = positions, Indices = indices };
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, positions, [], new());

        var registered = geometry!.Content(0, new VirtualGeometryAsset(mesh, pages.WithoutData()), new MemoryStream(pages.Data));

        var opaque = host!.Builder.Stages["Opaque"];

        var id = host.System.Objects.Add(new() {
            Bounds = new(Vector3.Zero, 1.1f),
            Stages = opaque.Mask,
            FeatureIndex = geometry.Feature.Index
        });

        host.System.Objects.Data.Data(geometry.Feature.Draws)[id.Index] = new() {
            Mesh = registered,
            Position = Vector3.Zero,
            Scale = 1f
        };

        if (log is not null) {
            SampleLog.MeshBuilt(
                log,
                indices.Length / 3,
                mesh.Meshlets.Length,
                pages.Pages.Length,
                device!.Adapter.Name,
                device.Adapter.Kind
            );
        }
    }

    /// <summary>The present pass's pipeline: a fullscreen triangle over the identity buffer.</summary>
    void BuildPresent() {
        presentVertex = device!.CreateShader(ShaderStage.Vertex, Load("present.vert.spv"), "present vertex");
        presentFragment = device.CreateShader(ShaderStage.Fragment, Load("present.frag.spv"), "present fragment");

        presentLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [
                new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment),

                // The traversal's visible list, so a pixel's slot can be decoded to its cluster —
                // without it the colours hash the slot, which the atomic append reshuffles every
                // frame, and the whole sphere flickers.
                new(2, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
            ],
            "present"
        ));

        descriptors = new(device, "VirtualGeometry");
        pointSampler = device.CreateSampler(SamplerDescription.PointClamp);

        presentPipelineLayout = device.CreatePipelineLayout(new([presentLayout], null, "present layout"));

        presentPipeline = device.CreateGraphicsPipeline(new(
            presentVertex,
            presentFragment,
            presentPipelineLayout,
            [new(swapChain!.Format, BlendState.Opaque)],
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "present"
        ));
    }

    /// <summary>Tears the stack down, in the reverse of the order it was built.</summary>
    void Release() {
        if (device is null) {
            return;
        }

        device.WaitIdle();
        swapChain?.Dispose();

        device.Destroy(presentPipeline);
        device.Destroy(presentPipelineLayout);
        device.Destroy(presentFragment);
        device.Destroy(presentVertex);
        device.Destroy(pointSampler);
        descriptors?.Dispose();
        device.Destroy(presentLayout);

        device.Destroy(visibilityView);
        device.Destroy(visibility);

        host?.Dispose();
        geometry?.Dispose();
        computePipelines?.Clear();

        device.Dispose();

        swapChain = null;
        descriptors = null;
        camera = null;
        host = null;
        geometry = null;
        computePipelines = null;
        effects = null;
        device = null;
    }

    /// <summary>Rebuilds what the window's size decided: the swapchain and the visibility buffer.</summary>
    void Recreate() {
        if (device is null || swapChain is null || Services.Window is not { } window) {
            return;
        }

        device.WaitIdle();
        swapChain.Resize(new(window.FramebufferSize.X, window.FramebufferSize.Y));

        // The visibility buffer is the frame's size, so it goes with the swapchain — and the import
        // is re-lent because the old handle now names a destroyed texture.
        device.Destroy(visibilityView);
        device.Destroy(visibility);

        var description = new TextureDescription(
            GpuClusterRaster.Format,
            swapChain.Size.X,
            swapChain.Size.Y,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "VisibilityBuffer"
        );

        visibility = device.CreateTexture(description);
        visibilityView = device.CreateTextureView(visibility);

        host!.Import(
            "VisibilityBuffer",
            new(visibility, visibilityView, description, ResourceState.Undefined, ResourceState.ShaderRead)
        );

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

    /// <summary>
    ///     Compiles the shipped shader library on demand — what a content build bakes ahead of time.
    /// </summary>
    /// <remarks>
    ///     The golden device tests' arrangement, carried here on the same terms: one compiler over
    ///     the whole library, effects cached by key, the library found by walking up from the binary
    ///     because the samples run from the repository. The moment a content build exists, this class
    ///     is what it replaces.
    /// </remarks>
    sealed class LibraryEffects(EffectLoader loader) : IEffectProvider {
        readonly Dictionary<EffectKey, Effect> cache = [];
        RavenEffectCompiler? compiler;

        public Effect? TryGet(EffectKey key) {
            if (cache.TryGetValue(key, out var existing)) {
                return existing;
            }

            compiler ??= new(Directory.GetFiles(Library(), "*.rvn", SearchOption.AllDirectories));

            if (compiler.TryGet(key) is not { } data) {
                return null;
            }

            var effect = loader.Load(data);
            cache[key] = effect;

            return effect;
        }

        static string Library() {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
                var candidate = Path.Combine(directory.FullName, "Raven", "Library");

                if (Directory.Exists(candidate)) {
                    return candidate;
                }
            }

            throw new DirectoryNotFoundException($"Raven/Library was not found above '{AppContext.BaseDirectory}'.");
        }
    }
}
