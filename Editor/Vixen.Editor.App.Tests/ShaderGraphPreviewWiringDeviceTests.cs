// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Editor.Testing;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     A shader graph opened in the editor gets node previews, drawn on the device and named to the
///     renderer that draws the editor (#1299).
/// </summary>
/// <remarks>
///     <para>
///         <b>Written against the host's wiring before it moved, and kept green across the move.</b>
///         <c>ShaderGraphPreviewDeviceTests</c> drives the renderer on its own; nothing drove the path
///         an author sees — the editor handing an opened <c>ShaderGraphDocument</c> a preview source,
///         that source drawing inside the host's frame, and the picture's number resolving against
///         the interface renderer. What changes with the move is only <see cref="Host" />: the
///         arrangement a real host makes, which is the part of this test that is the host's.
///     </para>
///     <para>
///         <b>Closed-form, not eyeballed</b>, on <c>ShaderGraphPreviewDeviceTests</c>' gradient: green is
///         <c>v</c> and red is <c>u</c>, so a preview drawn upside down, of the wrong node, or not at all
///         fails a corner. Skips with no Vulkan; <c>VIXEN_REQUIRE_VULKAN=1</c> makes that a failure.
///     </para>
/// </remarks>
public sealed class ShaderGraphPreviewWiringDeviceTests {
    /// <summary>The graph opened the way a double-click does, previewed, and read back off the device.</summary>
    [Fact]
    public void An_opened_shader_graph_previews_its_nodes_on_the_editors_renderer() {
        using var device = Open();

        VulkanDiagnostics.Reset();

        TestContext.Current.TestOutputHelper?.WriteLine($"Shader-graph preview read back on '{device.Adapter.Name}' ({device.Adapter.Kind}).");

        var renderer = new UiRenderer(device, UiShaderLibrary.Load(device), new RenderOutput([PixelFormat.Bgra8UNorm]));

        using var surface = new ThumbnailSurface(device, renderer);
        using var session = EditorSession.Start();
        using var host = new Host(session, device, renderer, surface);

        var document = Opened(session);
        var node = document.Graph.Nodes.Single(candidate => candidate.Type == "Vector/Tiling and Offset");
        var definition = document.Registry.Get(node.Type);
        var sets = renderer.ImageSets;

        host.Frame();

        Assert.NotNull(document.PreviewSource);

        var source = Assert.IsType<ShaderGraphPreviewRenderer>(document.PreviewSource);

        // The first ask registers the node and answers "nothing yet" — the canvas may already have
        // asked while the frame drew it — and the host's frame is what builds it.
        source.TryGet(document.Graph, node, definition, out _);
        host.Frame();

        Assert.True(
            source.TryGet(document.Graph, node, definition, out var preview),
            $"no preview after a frame: {source.RefusalFor(document.Graph, node.Id) ?? "no refusal was recorded"}"
        );

        Assert.NotEqual(0ul, preview.Image);

        // ⚠ Named to *this* renderer, the one the editor's panes draw through: registering a number
        // allocates its descriptor ring here, and a number registered anywhere else draws nothing.
        Assert.True(renderer.ImageSets > sets, "the preview's number was never registered with the editor's renderer");

        var pixels = Read(device, source.TextureOf(document.Graph, node.Id));

        const int Last = ShaderGraphPreviewRenderer.Size - 1;

        Assert.True(Channel(pixels, 0, 0, 1) < 32, $"top-left green is {Channel(pixels, 0, 0, 1)}");
        Assert.True(Channel(pixels, 0, Last, 1) > 220, $"bottom-left green is {Channel(pixels, 0, Last, 1)}");
        Assert.True(Channel(pixels, 0, 0, 0) < 32, $"top-left red is {Channel(pixels, 0, 0, 0)}");
        Assert.True(Channel(pixels, Last, 0, 0) > 220, $"top-right red is {Channel(pixels, Last, 0, 0)}");

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "the preview produced validation errors, so the picture means nothing: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>What a real host does about previews, and nothing else: the part of this test that moves.</summary>
    /// <remarks>
    ///     Mirrors <c>EditorHost.Sync</c>, <c>Present</c> and <c>Release</c> as they are written today:
    ///     the host builds the preview renderer once there is a device and a renderer, updates it inside
    ///     its frame ahead of the thumbnails' flush, and disposes it on the way down.
    /// </remarks>
    sealed class Host : IDisposable {
        readonly VulkanDevice device;
        readonly ThumbnailSurface surface;
        readonly EditorSession session;
        readonly ShaderGraphPreviewRenderer previews;

        public Host(EditorSession session, VulkanDevice device, UiRenderer renderer, ThumbnailSurface surface) {
            this.session = session;
            this.device = device;
            this.surface = surface;

            session.Editor.ThumbnailSurface = surface;
            previews = new ShaderGraphPreviewRenderer(device, ShaderNodeLibrary.Create(), new UiPreviewImages(renderer));
            session.Editor.ShaderGraphPreviews = previews;
        }

        public void Frame() {
            session.Frames(1);

            device.BeginFrame();
            previews.Update();
            surface.Flush();
            device.EndFrame();
            device.WaitIdle();
        }

        public void Dispose() {
            session.Editor.ShaderGraphPreviews = null;
            previews.Dispose();
            session.Editor.ThumbnailSurface = null;
        }
    }

    /// <summary>Writes a two-node gradient graph and opens it the way a double-click in the browser does.</summary>
    static ShaderGraphDocument Opened(EditorSession session) {
        const string Relative = "Assets/Gradient.vxshadergraph";

        var graph = new NodeGraphModel { Name = "Gradient" };
        var uv = graph.Add("Input/UV");
        var tiling = graph.Add("Vector/Tiling and Offset");

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));

        var absolute = session.Project.Paths.Absolute(Relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, YamlSerializer.ToYaml(NodeGraphDocument.Save(graph)));

        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath(Relative, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Frames(2);

        return Assert.Single(session.Project.Documents.OfType<ShaderGraphDocument>());
    }

    static byte Channel(byte[] pixels, int x, int y, int channel) =>
        pixels[(((y * ShaderGraphPreviewRenderer.Size) + x) * 4) + channel];

    static byte[] Read(VulkanDevice device, TextureHandle texture) {
        Assert.True(texture.IsValid, "the renderer has no target for the node it previewed");

        const int Bytes = ShaderGraphPreviewRenderer.Size * ShaderGraphPreviewRenderer.Size * 4;

        var readback = device.CreateBuffer(new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "preview readback"));

        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "preview readback")) {
            commands.Barrier(new BarrierGroup([], [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)]));
            commands.CopyTextureToBuffer(
                new TextureRegion(texture),
                new(ShaderGraphPreviewRenderer.Size, ShaderGraphPreviewRenderer.Size, 1),
                readback,
                0
            );
            commands.Barrier(new BarrierGroup([], [new TextureBarrier(texture, ResourceState.CopySource, ResourceState.ShaderRead)]));
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        var pixels = new byte[Bytes];

        device.Read(readback, 0, pixels);
        device.Destroy(readback);

        return pixels;
    }

    /// <summary>A device, or a skip — or, when one was required, a failure.</summary>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        throw new InvalidOperationException("unreachable");
    }
}
