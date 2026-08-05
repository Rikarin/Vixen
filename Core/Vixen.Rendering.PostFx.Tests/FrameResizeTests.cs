// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A window that resizes, against the one node in the tree that laid state out against the old
///     size.
/// </summary>
/// <remarks>
///     <para>
///         <b>The regression the seam exists for.</b> <c>ScreenProbeGatherRenderer</c> sizes its
///         lattice on the first build and refuses a frame of any other size, and until
///         <see cref="GraphicsCompositor.Resize" /> existed nothing told it a resize had happened —
///         so running <c>Samples/13-ThirdPersonShooter</c> and dragging the window threw out of
///         <c>Build</c>, through the compositor, and out of the host's <c>Draw</c>.
///     </para>
///     <para>
///         Build-only and on a <see cref="NullDevice" />, deliberately: what is under test is the
///         lifecycle — that a second size is accepted and that the lattice moved to it — and no
///         picture is involved. The picture-side proof that a reset frame still draws is
///         <c>ScreenProbeGatherImageTests.AResizeIsADeliberateStep</c>, which needs a real device.
///     </para>
/// </remarks>
public sealed class FrameResizeTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    /// <summary>The reported crash: one frame, then another at a different size, and no exception.</summary>
    [Fact]
    public void A_resized_frame_is_accepted_and_the_lattice_moves_to_it() {
        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        var graph = new RenderGraph(device);
        using var node = Gather(samplers, allocator);

        var compositor = new GraphicsCompositor(system) { Game = new SceneRendererSequence { Children = { node } } };

        Draw(compositor, graph, new(320, 180));

        Assert.Equal(new Int2(320, 180), node.Texture!.Probes.Layout.Viewport);

        // A display that changed scale factor, which is what the report was: not a drag, a jump.
        Assert.Equal(1, compositor.Resize(new(672, 354), device.WaitIdle));

        Draw(compositor, graph, new(672, 354));

        Assert.Equal(new Int2(672, 354), node.Texture!.Probes.Layout.Viewport);

        // And the temporal chain restarted rather than reprojecting through a lattice that is gone.
        Assert.Equal(0, node.Placements);
    }

    /// <summary>The crash itself, so the test above is known to be exercising the real failure.</summary>
    /// <remarks>
    ///     A size written straight onto <see cref="GraphicsCompositor.FrameSize" /> reaches
    ///     <c>Build</c> with nothing having reset the lattice, and the node refuses it — which is the
    ///     exception <c>Samples/13-ThirdPersonShooter</c> died of on every window resize. The backstop
    ///     stays loud; what the seam changes is that a host can no longer arrive here.
    /// </remarks>
    [Fact]
    public void A_size_that_skips_the_seam_is_still_refused() {
        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        var graph = new RenderGraph(device);
        using var node = Gather(samplers, allocator);

        var compositor = new GraphicsCompositor(system) { Game = new SceneRendererSequence { Children = { node } } };

        Draw(compositor, graph, new(320, 180));

        var refused = Assert.Throws<InvalidOperationException>(() => Draw(compositor, graph, new(672, 354)));

        Assert.Contains("laid its probes over", refused.Message, StringComparison.Ordinal);
        Assert.Contains("GraphicsCompositor.Resize", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Back and forth, because a resize is not a one-shot.</summary>
    [Fact]
    public void Repeated_resizes_keep_working() {
        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        var graph = new RenderGraph(device);
        using var node = Gather(samplers, allocator);

        var compositor = new GraphicsCompositor(system) { Game = new SceneRendererSequence { Children = { node } } };

        foreach (var size in new Int2[] { new(320, 180), new(672, 354), new(320, 180), new(256, 256), new(672, 354) }) {
            compositor.Resize(size, device.WaitIdle);
            Draw(compositor, graph, size);

            Assert.Equal(size, node.Texture!.Probes.Layout.Viewport);
        }
    }

    /// <summary>A node switched off across the resize is reset too.</summary>
    /// <remarks>
    ///     <see cref="GraphicsCompositor.Apply" />'s argument one step further on: <c>Enabled</c>
    ///     stops a node drawing, and a node that skipped the resize and came back afterwards would
    ///     refuse the first build that reached it.
    /// </remarks>
    [Fact]
    public void A_disabled_node_is_reset_as_well() {
        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        var graph = new RenderGraph(device);
        using var node = Gather(samplers, allocator);

        var compositor = new GraphicsCompositor(system) { Game = new SceneRendererSequence { Children = { node } } };

        Draw(compositor, graph, new(320, 180));

        node.Enabled = false;

        Assert.Equal(1, compositor.Resize(new(672, 354), device.WaitIdle));

        node.Enabled = true;

        Draw(compositor, graph, new(672, 354));

        Assert.Equal(new Int2(672, 354), node.Texture!.Probes.Layout.Viewport);
    }

    /// <summary>A size that did not change resets nothing, and never idles.</summary>
    /// <remarks>
    ///     The property that keeps a window drag affordable, and the one a naive seam gets wrong: a
    ///     host writes the frame size on every swapchain rebuild, and a surface that keeps answering
    ///     <c>Suboptimal</c> asks for one every frame. Resetting on those would restart the temporal
    ///     chain for ever at no size change at all.
    /// </remarks>
    [Fact]
    public void An_unchanged_size_is_not_a_resize() {
        using var system = new RenderSystem();
        using var node = new ScreenProbeGatherRenderer { Name = "ScreenProbes", Depth = "Depth", Normals = "Normals" };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(320, 180),
            Game = new SceneRendererSequence { Children = { node } }
        };

        var idles = 0;

        Assert.Equal(0, compositor.Resize(new(320, 180), () => idles++));
        Assert.Equal(0, idles);

        Assert.Equal(1, compositor.Resize(new(640, 360), () => idles++));
        Assert.Equal(1, idles);
    }

    /// <summary>A frame with nothing size-dependent in it never idles the device.</summary>
    [Fact]
    public void A_frame_of_nodes_that_do_not_care_costs_no_idle() {
        using var system = new RenderSystem();

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(320, 180),
            Game = new SceneRendererSequence { Children = { new DelegateSceneRenderer() } }
        };

        var idles = 0;

        Assert.Equal(0, compositor.Resize(new(640, 360), () => idles++));
        Assert.Equal(0, idles);
        Assert.Equal(new Int2(640, 360), compositor.FrameSize);
    }

    static ScreenProbeGatherRenderer Gather(SamplerCache samplers, DescriptorAllocator allocator) =>
        new() {
            Name = "ScreenProbes",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Samplers = samplers,
            Allocator = allocator
        };

    /// <summary>One frame at one size, declared and built but never executed.</summary>
    void Draw(GraphicsCompositor compositor, RenderGraph graph, Int2 size) {
        compositor.FrameSize = size;

        foreach (var name in new[] { "Depth", "Normals", "Display" }) {
            compositor.Imports[name] = Import(name, size);
        }

        graph.Reset();
        compositor.Build(graph, effects, device);
        graph.Reset();
    }

    ImportedTexture Import(string name, Int2 size) {
        var description = new TextureDescription(
            PixelFormat.Rgba32Float,
            size.X,
            size.Y,
            TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
            Name: name
        );

        var texture = device.CreateTexture(description);

        return new(texture, device.CreateTextureView(texture), description);
    }

    /// <inheritdoc />
    public void Dispose() => device.Dispose();
}
