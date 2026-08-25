// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>
///     A node's preview, rendered on a real device, asserted to be a picture.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A preview that renders nothing looks exactly like a preview that is not
///         implemented.</b> Every structural claim about this renderer — a target was made, a pipeline
///         was built, a draw was recorded, the counters went up — is true of a black square, and this
///         repository has shipped three of those this month. So what is asserted here is the bytes:
///         how many distinct colours came back, what the mean channel is, and which corner is which.
///     </para>
///     <para>
///         ⚠ <b>The orientation is asserted separately from the histogram, because a vertically
///         flipped thumbnail passes every histogram.</b> The graph under test previews
///         <c>float3(u, v, 1)</c> over the quad, so the top row is green-dark and the bottom row is
///         green-bright — in the engine's convention, where clip <c>y = +1</c> is the top. A renderer
///         that got the convention backwards produces a picture with exactly the same colour count and
///         exactly the same mean.
///     </para>
///     <para>
///         ⚠ <b>Skips when there is no Vulkan, and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a
///         failure.</b> A gate that silently skips is a gate that passes, which is what makes a device
///         test worth less than nothing on a machine where nobody reads the skip count.
///     </para>
/// </remarks>
public class ShaderGraphPreviewDeviceTests {
    /// <summary>What the graph under test previews: <c>UV</c> through a tiling node.</summary>
    static (NodeGraphModel Graph, GraphNode Node) Gradient() {
        var graph = new NodeGraphModel { Name = "Gradient" };

        var uv = graph.Add("Input/UV");
        var tiling = graph.Add("Vector/Tiling and Offset");

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));

        return (graph, tiling);
    }

    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
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

    /// <summary>Asks for a preview, renders it, and reads the pixels back.</summary>
    static byte[] Render(
        VulkanDevice device,
        ShaderGraphPreviewRenderer renderer,
        NodeGraphModel graph,
        GraphNode node,
        NodeTypeDefinition definition
    ) {
        // The first ask registers the node and answers "nothing yet"; Update is what builds and draws.
        renderer.TryGet(graph, node, definition, out _);

        const int Bytes = ShaderGraphPreviewRenderer.Size * ShaderGraphPreviewRenderer.Size * 4;

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "preview readback")
        );

        device.BeginFrame();

        var built = renderer.Update();

        Assert.True(
            built == 1,
            $"The renderer built {built} previews rather than one: {renderer.RefusalFor(graph, node.Id) ?? "no refusal was recorded"}"
        );

        var texture = renderer.TextureOf(graph, node.Id);

        Assert.True(texture.IsValid, "The renderer has no target for the node it just drew.");

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "preview readback")) {
            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(
                new TextureRegion(texture),
                new(ShaderGraphPreviewRenderer.Size, ShaderGraphPreviewRenderer.Size, 1),
                readback,
                0
            );

            commands.Barrier(
                new BarrierGroup([], [new TextureBarrier(texture, ResourceState.CopySource, ResourceState.ShaderRead)])
            );

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

    static int Distinct(byte[] pixels) {
        HashSet<int> seen = [];

        for (var at = 0; at + 3 < pixels.Length; at += 4) {
            seen.Add((pixels[at] << 16) | (pixels[at + 1] << 8) | pixels[at + 2]);
        }

        return seen.Count;
    }

    static double Mean(byte[] pixels) {
        long sum = 0;

        for (var at = 0; at + 3 < pixels.Length; at += 4) {
            sum += pixels[at] + pixels[at + 1] + pixels[at + 2];
        }

        return sum / (double)(pixels.Length / 4 * 3);
    }

    static byte Channel(byte[] pixels, int x, int y, int channel) =>
        pixels[(((y * ShaderGraphPreviewRenderer.Size) + x) * 4) + channel];

    /// <summary>The preview is a gradient: many colours, a mid mean, and the right way up.</summary>
    [Fact]
    public void A_preview_is_a_picture_and_not_a_black_square() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var registry = Library();
            var (graph, node) = Gradient();

            using var renderer = new ShaderGraphPreviewRenderer(device, registry);

            var pixels = Render(device, renderer, graph, node, registry.Get(node.Type));

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                "The preview produced validation errors, so its picture means nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );

            var distinct = Distinct(pixels);
            var mean = Mean(pixels);

            // A 64×64 ramp in two channels: 64 reds × 64 greens is 4 096 colours if every step lands
            // on its own byte, and rounding costs a few. A thousand is far above anything a flat fill,
            // a clear or an undefined target produces, and far below what this actually renders.
            Assert.True(distinct >= 1000, $"the preview holds {distinct} distinct colour(s), which is not a gradient");

            // Red and green ramp 0…1 and blue is 1, so the mean channel is about (0.5 + 0.5 + 1) / 3
            // of 255 — near 170. A black square is 0, a white one 255, and an unlit magenta error
            // colour would be neither.
            Assert.InRange(mean, 140d, 200d);

            const int Last = ShaderGraphPreviewRenderer.Size - 1;

            // ⚠ The orientation. Green is v, and v is 0 at the top: clip y = +1 is the TOP in this
            // engine and the Vulkan backend's negative-height viewport is what implements it. A
            // preview drawn upside down passes both assertions above.
            Assert.True(
                Channel(pixels, 0, 0, 1) < 32,
                $"the top-left pixel's green is {Channel(pixels, 0, 0, 1)}, so the preview is upside down"
            );

            Assert.True(
                Channel(pixels, 0, Last, 1) > 220,
                $"the bottom-left pixel's green is {Channel(pixels, 0, Last, 1)}, so the preview is upside down"
            );

            // And the other axis, which no flip would disturb: red is u.
            Assert.True(Channel(pixels, 0, 0, 0) < 32);
            Assert.True(Channel(pixels, Last, 0, 0) > 220);
        }
    }

    /// <summary>Asking again for an unchanged graph compiles nothing and draws nothing.</summary>
    /// <remarks>
    ///     The throttling story, as a measurement. "A shader is not compiled per keystroke" is a claim
    ///     about <see cref="ShaderGraphPreviewRenderer.Compilations" /> and about nothing else.
    /// </remarks>
    [Fact]
    public void An_unchanged_graph_costs_no_compilation() {
        using (var device = Open()) {
            var registry = Library();
            var (graph, node) = Gradient();
            var definition = registry.Get(node.Type);

            using var renderer = new ShaderGraphPreviewRenderer(device, registry);

            Render(device, renderer, graph, node, definition);

            var compilations = renderer.Compilations;
            var draws = renderer.Draws;
            var created = renderer.Created;

            var emissions = renderer.Emissions;

            // Twenty asks with nothing at all having happened — a canvas redrawing at sixty hertz
            // while the author reads the graph. The first gate: no emission either.
            for (var frame = 0; frame < 20; frame++) {
                // False, because this renderer was given no image sink — there is no number to draw
                // by. What is being measured is that asking cost nothing, not what the answer was.
                renderer.TryGet(graph, node, definition, out _);

                Assert.Equal(0, renderer.Update());
            }

            Assert.Equal(emissions, renderer.Emissions);
            Assert.Equal(compilations, renderer.Compilations);

            // And twenty with the node moved, which is what a drag is: every `NodeGraphCommand`
            // touches the graph, so the revision moves and the source is emitted again — and the
            // second gate finds the same text, so nothing is compiled and nothing is drawn.
            for (var frame = 0; frame < 20; frame++) {
                node.Position = new(frame * 10f, 0f);
                graph.Touch();

                renderer.TryGet(graph, node, definition, out _);

                Assert.Equal(0, renderer.Update());
            }

            Assert.Equal(emissions + 20, renderer.Emissions);
            Assert.Equal(compilations, renderer.Compilations);
            Assert.Equal(draws, renderer.Draws);
            Assert.Equal(created, renderer.Created);

            // And an edit that does change the expression costs exactly one more of each, into the
            // target that already exists rather than into a new one.
            node.SetValue("Tiling", 3f, 3f);
            graph.Touch();

            renderer.TryGet(graph, node, definition, out _);

            device.BeginFrame();
            Assert.Equal(1, renderer.Update());
            device.EndFrame();
            device.WaitIdle();

            Assert.Equal(compilations + 1, renderer.Compilations);
            Assert.Equal(draws + 1, renderer.Draws);
            Assert.Equal(created, renderer.Created);
        }
    }

    /// <summary>Everything the renderer made is destroyed when it goes.</summary>
    /// <remarks>
    ///     ⚠ <b>A render target per node per keystroke is a leak, and this editor has paid for one
    ///     before.</b> The counters are equal or they are not; a claim about a leak that cannot be
    ///     measured is one nobody can check.
    /// </remarks>
    [Fact]
    public void Disposing_destroys_every_target_and_gives_up_every_number() {
        using (var device = Open()) {
            var registry = Library();
            var (graph, node) = Gradient();
            var images = new CountingImages();

            var renderer = new ShaderGraphPreviewRenderer(device, registry, images);

            Render(device, renderer, graph, node, registry.Get(node.Type));

            Assert.Equal(1, renderer.Created);
            Assert.Equal(0, renderer.Destroyed);
            Assert.Equal(1, images.Registered);
            Assert.Equal(0, images.Released);

            renderer.Dispose();

            Assert.Equal(renderer.Created, renderer.Destroyed);
            Assert.Equal(0, renderer.Live);
            Assert.Equal(images.Registered, images.Released);

            // Idempotent, because a host that disposes on shutdown and again on a project close must
            // not destroy the same texture twice.
            renderer.Dispose();

            Assert.Equal(renderer.Created, renderer.Destroyed);
        }
    }

    /// <summary>A capacity full of nodes evicts, and eviction destroys.</summary>
    [Fact]
    public void Eviction_destroys_the_target_it_drops() {
        using (var device = Open()) {
            var registry = Library();
            var graph = new NodeGraphModel { Name = "Many" };
            var images = new CountingImages();

            using var renderer = new ShaderGraphPreviewRenderer(device, registry, images) { Capacity = 2 };

            var definition = registry.Get("Vector/Tiling and Offset");
            List<GraphNode> nodes = [];

            for (var index = 0; index < 4; index++) {
                var node = graph.Add("Vector/Tiling and Offset");

                // Distinct expressions, so no two previews share a source and the eviction is about
                // the count rather than about the cache.
                node.SetValue("Tiling", index + 1f, index + 1f);
                nodes.Add(node);

                renderer.TryGet(graph, node, definition, out _);

                device.BeginFrame();
                renderer.Update();
                device.EndFrame();
                device.WaitIdle();
            }

            Assert.Equal(2, renderer.Live);
            Assert.Equal(2, renderer.Destroyed);
            Assert.Equal(renderer.Created - renderer.Destroyed, renderer.Live);
            Assert.Equal(images.Registered - images.Released, renderer.Live);
        }
    }

    /// <summary>A node needing a texture is refused rather than drawn as an unbound descriptor.</summary>
    [Fact]
    public void A_node_that_needs_a_resource_is_refused() {
        using (var device = Open()) {
            VulkanDiagnostics.Reset();

            var registry = Library();
            var graph = new NodeGraphModel { Name = "Sampled" };
            var node = graph.Add("Texture/Sample 2D");

            using var renderer = new ShaderGraphPreviewRenderer(device, registry);

            renderer.TryGet(graph, node, registry.Get(node.Type), out _);

            device.BeginFrame();

            Assert.Equal(0, renderer.Update());

            device.EndFrame();
            device.WaitIdle();

            Assert.Equal(1, renderer.Refusals);
            Assert.NotNull(renderer.RefusalFor(graph, node.Id));
            Assert.Equal(0, renderer.Draws);
            Assert.Equal(0, VulkanDiagnostics.ErrorCount);
        }
    }

    /// <summary>Two graphs whose nodes have the same identity get two previews.</summary>
    /// <remarks>
    ///     ⚠ <b>Every graph starts numbering at one</b>, so two shader graphs open in two tabs both
    ///     have a <c>#1</c>. A cache keyed on the node alone shows one tab's picture under the other
    ///     tab's node — which looks like a preview that is simply wrong rather than like a cache that
    ///     is confused.
    /// </remarks>
    [Fact]
    public void Two_graphs_with_the_same_node_identity_get_their_own_targets() {
        using (var device = Open()) {
            var registry = Library();
            var definition = registry.Get("Vector/Tiling and Offset");

            var first = new NodeGraphModel { Name = "First" };
            var second = new NodeGraphModel { Name = "Second" };

            var a = first.Add("Vector/Tiling and Offset");
            var b = second.Add("Vector/Tiling and Offset");

            Assert.Equal(a.Id, b.Id);

            b.SetValue("Tiling", 7f, 7f);

            using var renderer = new ShaderGraphPreviewRenderer(device, registry);

            renderer.TryGet(first, a, definition, out _);
            renderer.TryGet(second, b, definition, out _);

            device.BeginFrame();

            Assert.Equal(2, renderer.Update());

            device.EndFrame();
            device.WaitIdle();

            Assert.Equal(2, renderer.Live);
            Assert.Equal(2, renderer.Created);
            Assert.NotEqual(renderer.TextureOf(first, a.Id), renderer.TextureOf(second, b.Id));
        }
    }

    sealed class CountingImages : IPreviewImages {
        ulong next = 0x9000;

        public int Registered { get; private set; }
        public int Released { get; private set; }

        public ulong Register(TextureViewHandle view) {
            Registered++;

            return next++;
        }

        public void Release(ulong image) => Released++;
    }
}
