// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders.Generated;
using Vixen.Ui.Renderer;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Four composed panes, on a real device, written out as a picture to look at.</summary>
/// <remarks>
///     <para>
///         <b>The deliverable a passing suite is not.</b> Everything in
///         <see cref="ComposedPaneTests" /> is a claim about counters, indices and names, and this
///         engine's commonest defect is a frame in which every one of those is healthy and no
///         fragment survives — a water surface submitting forty-five patches, a lake four decades too
///         dim, a "split" frame bit-identical to the unsplit one. So this renders the arrangement on
///         the Vulkan backend and writes what came out.
///     </para>
///     <para>
///         ⚠ <b>Off unless it is asked for, by <c>VIXEN_PANE_CAPTURE=&lt;directory&gt;</c>.</b> It
///         opens a device, compiles the editor's Raven library and writes files — none of which
///         belongs in a suite that runs on every push, and all of which is what somebody looking at
///         the viewport wants. The editor's own host cannot do this: it opens a window.
///     </para>
///     <para>
///         ⚠ <b>The harness lends the frame its own colour targets, under the pane's own names.</b> A
///         <see cref="FramePresenter" />'s texture is <c>ColourTarget | Sampled</c> because the
///         interface samples it, and a texture without <c>CopySource</c> is one no readback may
///         touch. An import wins over a declaration <em>and</em> over an earlier import of the same
///         name, so writing over the presenter's import after it prepared is the documented way to
///         take the picture without changing what the editor allocates for a pane it is only going to
///         sample. What is asserted below is that the four textures came back <em>different</em>,
///         which is the claim a one-view frame fails.
///     </para>
/// </remarks>
public sealed class ComposedPaneCaptureTests {
    /// <summary>Where the pictures go, or null when nobody asked for any.</summary>
    static string? Destination => Environment.GetEnvironmentVariable("VIXEN_PANE_CAPTURE");

    /// <summary>The four panes of a quad layout, each drawing its own camera.</summary>
    [Fact]
    public void A_quad_layout_draws_four_panes() {
        var directory = Destination;

        Assert.SkipWhen(
            string.IsNullOrEmpty(directory),
            "VIXEN_PANE_CAPTURE names no directory, so there is nowhere to write a picture."
        );

        Assert.SkipUnless(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using (device!) {
            Capture(device, directory!, "panes-shaded.png", modes: null);

            // ⚠ And with the modes differing per pane, which is the half a shared stage would break.
            // Two panes in wireframe are one stage index and two views; the pipeline cache bakes a
            // stage's state in on the first miss, so a mode that mutated a shared stage would change
            // the menu and not the picture.
            Capture(
                device,
                directory!,
                "panes-modes.png",
                [ViewMode.Shaded, ViewMode.Wireframe, ViewMode.Wireframe, ViewMode.Shaded]
            );
        }
    }

    /// <summary>Renders one arrangement and writes it out as a two-by-two composite.</summary>
    static void Capture(VulkanDevice device, string directory, string file, ViewMode[]? modes) {
        using var session = EditorSession.Start();

        session.Application.GraphicsDevice = device;
        session.Frame();
        session.Run("scene.panes-quad");
        session.Settle();

        var panes = session.Application.Viewports;

        Assert.Equal(EditorWorldRenderer.MaxPanes, panes.Count);

        // ⚠ Four cameras that disagree, because four panes at one orbit are four panes whose views
        // hold the same matrix — which is exactly the picture a single-view frame would also produce.
        for (var index = 0; index < panes.Count; index++) {
            panes[index].Camera.Yaw = 0.9f * index;
            panes[index].Camera.Pitch = -0.35f - (0.1f * index);
            panes[index].Camera.Distance = 7f + (index * 1.5f);

            if (modes is null) {
                continue;
            }

            // ⚠ Registered rather than merely set. `ViewModes.Resolve` falls back to the shaded tree
            // for a mode nothing authored one for, so a device without `fillModeNonSolid` would draw
            // four shaded panes and this capture would call them four modes.
            Assert.Contains(modes[index], panes[index].Modes.Registered);

            panes[index].Modes.Current = modes[index];
        }

        var world = session.Application.Frame!;

        Assert.Null(world.MissingBinding);

        var shaders = new Shaders(device);
        var renderer = new UiRenderer(device, shaders.Ui, new RenderOutput([PixelFormat.Bgra8UNorm]));
        var presenters = new List<FramePresenter>();
        var targets = new List<Target>();

        var pool = new TransientResourcePool(device);
        var graph = new RenderGraph(device, pool);

        try {
            for (var index = 0; index < panes.Count; index++) {
                presenters.Add(
                    new FramePresenter(
                        device,
                        world,
                        shaders.Lines,
                        shaders.Meshes,
                        FramePresenter.ColourFormat,
                        ((ulong) index * 2) + 2,
                        index
                    )
                );
            }

            // ⚠ Several frames, and the number is not decoration. The set-1 layout is adopted off the
            // first shader to resolve, which happens inside the first build — so a one-frame capture
            // is a picture of a frame that has not bound a per-view set yet, and the pane is black for
            // a reason that has nothing to do with the arrangement.
            for (var pass = 0; pass < 4; pass++) {
                Frame(device, graph, world, session, renderer, panes, presenters, targets, capture: pass == 3);
                graph.Reset();
            }

            Assert.True(world.IsComplete, $"set 0 never bound; nothing filled: {world.MissingBinding}");
            Assert.Equal(EditorWorldRenderer.MaxPanes, targets.Count);

            var images = targets.Select(target => target.Read(device)).ToList();

            for (var index = 0; index < images.Count; index++) {
                Assert.True(
                    Interesting(images[index]),
                    $"pane {index} came back a single flat colour, so it drew nothing at all"
                );
            }

            // ⚠ The four have to differ from each other, which is the claim the arrangement is about.
            for (var left = 0; left < images.Count; left++) {
                for (var right = left + 1; right < images.Count; right++) {
                    Assert.False(
                        images[left].Pixels.AsSpan().SequenceEqual(images[right].Pixels),
                        $"panes {left} and {right} are the same pixels, so they drew one camera"
                    );
                }
            }

            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, file), Quad(images));
        } finally {
            foreach (var target in targets) {
                target.Dispose(device);
            }

            foreach (var presenter in presenters) {
                presenter.Dispose();
            }

            renderer.Dispose();
            pool.Dispose();
            shaders.Dispose(device);
        }
    }

    /// <summary>One frame, in <c>EditorHost.Record</c>'s order.</summary>
    static void Frame(
        VulkanDevice device,
        RenderGraph graph,
        EditorWorldRenderer world,
        EditorSession session,
        UiRenderer renderer,
        IReadOnlyList<SceneViewport> panes,
        IReadOnlyList<FramePresenter> presenters,
        List<Target> targets,
        bool capture
    ) {
        device.BeginFrame();

        using var commands = device.BeginCommandList(QueueKind.Graphics, "panes");

        var composing = new List<(FramePresenter Presenter, SceneViewport Viewport)>();

        for (var index = 0; index < panes.Count; index++) {
            if (presenters[index].Resize(panes[index], renderer)) {
                composing.Add((presenters[index], panes[index]));
            }
        }

        world.Begin(commands);

        var trees = new List<SceneRenderer>();
        var reference = Int2.Zero;

        foreach (var (presenter, viewport) in composing) {
            presenter.Upload(commands, session.Application.Scene, viewport);

            if (presenter.Prepare(viewport, out var tree)) {
                trees.Add(tree);
            }

            reference = new(Math.Max(reference.X, presenter.Width), Math.Max(reference.Y, presenter.Height));
        }

        // ⚠ After every pane has prepared and before the one build, because a build is a snapshot of
        // the imports. See this file's remarks for why the harness lends its own colour at all.
        if (capture) {
            for (var index = 0; index < composing.Count; index++) {
                var target = new Target(device, composing[index].Presenter.Width, composing[index].Presenter.Height);

                targets.Add(target);

                world.Compositor.Imports[FramePresenter.Colour(index)] = new(
                    target.Texture,
                    target.View,
                    target.Description,
                    ResourceState.Undefined,
                    ResourceState.CopySource
                );
            }
        }

        if (trees.Count > 0) {
            world.Compose(graph, trees, reference, device.WaitIdle);
        }

        graph.Execute(commands);

        if (capture) {
            foreach (var target in targets) {
                target.Copy(commands);
            }
        }

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        device.EndFrame();
        device.WaitIdle();
    }

    /// <summary>Whether a pane is more than one flat colour.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion a clear-colour frame fails and every counter in it passes.</b> A pane
    ///     that composed, culled, sorted and recorded without a surviving fragment comes back as the
    ///     pass's clear colour, uniformly — which is a picture, and therefore the kind of failure
    ///     somebody attributes to the lighting.
    /// </remarks>
    static bool Interesting(Bitmap image) {
        for (var index = 4; index < image.Pixels.Length; index += 4) {
            if (image.Pixels[index] != image.Pixels[0]
                || image.Pixels[index + 1] != image.Pixels[1]
                || image.Pixels[index + 2] != image.Pixels[2]) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lays the four panes out the way the scene panel does, with a rule between them.</summary>
    static Bitmap Quad(IReadOnlyList<Bitmap> panes) {
        const int Gap = 4;

        var left = Math.Max(panes[0].Width, panes[2].Width);
        var right = Math.Max(panes[1].Width, panes[3].Width);
        var top = Math.Max(panes[0].Height, panes[1].Height);
        var bottom = Math.Max(panes[2].Height, panes[3].Height);

        var width = left + Gap + right;
        var height = top + Gap + bottom;
        var pixels = new byte[width * height * 4];

        for (var index = 3; index < pixels.Length; index += 4) {
            pixels[index] = byte.MaxValue;
        }

        var composite = new Bitmap(width, height, pixels);

        Blit(composite, panes[0], 0, 0);
        Blit(composite, panes[1], left + Gap, 0);
        Blit(composite, panes[2], 0, top + Gap);
        Blit(composite, panes[3], left + Gap, top + Gap);

        return composite;
    }

    static void Blit(in Bitmap into, in Bitmap from, int x, int y) {
        for (var row = 0; row < from.Height; row++) {
            var source = from.Offset(0, row);
            var destination = into.Offset(x, y + row);

            Array.Copy(from.Pixels, source, into.Pixels, destination, from.Width * 4);
        }
    }

    /// <summary>A colour target the harness lends the frame, and the buffer it comes back through.</summary>
    sealed class Target {
        public Target(IGraphicsDevice device, int width, int height) {
            Width = width;
            Height = height;

            Description = new(
                FramePresenter.ColourFormat,
                width,
                height,
                TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
                Name: "capture colour"
            );

            Texture = device.CreateTexture(Description);
            View = device.CreateTextureView(Texture);

            Readback = device.CreateBuffer(
                new(width * height * 4, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "capture readback")
            );
        }

        public int Width { get; }
        public int Height { get; }
        public TextureDescription Description { get; }
        public TextureHandle Texture { get; }
        public TextureViewHandle View { get; }
        public BufferHandle Readback { get; }

        public void Copy(ICommandList commands) =>
            commands.CopyTextureToBuffer(new(Texture), new(Width, Height, 1), Readback, 0);

        public Bitmap Read(IGraphicsDevice device) {
            var pixels = new byte[Width * Height * 4];

            device.Read(Readback, 0, pixels);

            return new(Width, Height, pixels);
        }

        public void Dispose(IGraphicsDevice device) {
            device.Destroy(Readback);
            device.Destroy(View);
            device.Destroy(Texture);
        }
    }

    /// <summary>The editor's own tool modules, read from beside the test binary.</summary>
    sealed class Shaders {
        readonly List<ShaderHandle> made = [];

        public Shaders(IGraphicsDevice device) {
            Lines = new(
                Load(device, ShaderStage.Vertex, "LineVertex.vert.spv"),
                Load(device, ShaderStage.Fragment, "LineFragment.frag.spv")
            ) {
                Locations = new(LineVertexKeys.PositionLocation, LineVertexKeys.VertexColourLocation)
            };

            Meshes = new(
                Load(device, ShaderStage.Vertex, "Mesh.vert.spv"),
                Load(device, ShaderStage.Fragment, "Mesh.frag.spv")
            ) {
                Locations = new(MeshKeys.PositionLocation, MeshKeys.NormalLocation, MeshKeys.VertexColourLocation)
            };

            Ui = new(
                Load(device, ShaderStage.Vertex, "UiVertex.vert.spv"),
                Load(device, ShaderStage.Fragment, "UiBox.frag.spv"),
                Load(device, ShaderStage.Fragment, "UiText.frag.spv"),
                Load(device, ShaderStage.Fragment, "UiSolid.frag.spv")
            ) {
                Image = Load(device, ShaderStage.Fragment, "UiImage.frag.spv"),

                // ⚠ Read out of Raven's reflection, exactly as `EditorHost` does. Left at the
                // default every attribute is at location zero, which a recording device accepts and
                // a driver refuses — `vkCreateGraphicsPipelines` with `ErrorInitializationFailed`,
                // which is how this was found.
                Locations = new(
                    UiVertexKeys.PositionLocation,
                    UiVertexKeys.TexcoordLocation,
                    UiVertexKeys.VertexColourLocation,
                    UiVertexKeys.VertexShapeLocation
                )
            };
        }

        public LineShaders Lines { get; }
        public MeshShaders Meshes { get; }
        public UiShaders Ui { get; }

        ShaderHandle Load(IGraphicsDevice device, ShaderStage stage, string name) {
            var path = Path.Combine(AppContext.BaseDirectory, "ToolShaders", name);

            Assert.True(File.Exists(path), $"the capture needs {name} beside the test binary");

            var handle = device.CreateShader(stage, File.ReadAllBytes(path), name);

            made.Add(handle);

            return handle;
        }

        public void Dispose(IGraphicsDevice device) {
            foreach (var handle in made) {
                device.Destroy(handle);
            }
        }
    }
}
