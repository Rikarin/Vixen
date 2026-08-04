// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A snapshot of one target in another — [docs/plan/35 § B1].
/// </summary>
/// <remarks>
///     <para>
///         The compositor could always express "run after lighting, read the scene colour, write the
///         scene colour". What it could not express is the part that makes that legal: sampling a
///         target a pass is also writing is undefined, so the read has to come from a second resource,
///         and that resource has to be one the graph knows the lifetime of.
///     </para>
///     <para>
///         So what is asserted here is not that <c>CopyTexture</c> works. It is that the copy is
///         <em>in the graph</em> — ordered behind whatever produced the source, with a barrier between
///         them, and dropped entirely when nothing reads the destination.
///     </para>
/// </remarks>
public class TextureCopyTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly RenderSystem system = new();
    readonly RenderGraph graph;

    public TextureCopyTests() {
        graph = new(device);
    }

    /// <inheritdoc />
    public void Dispose() {
        graph.DisposePool();
        system.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The fixture --------------------------------------------------------

    const int Size = 16;

    static RenderResourceAsset Declared(string name, TextureUsage usage, float scale = 1f) =>
        new() { Name = name, Format = PixelFormat.Rgba16Float, Usage = usage, Scale = scale };

    GraphicsCompositor Compositor(params SceneRenderer[] nodes) {
        var sequence = new SceneRendererSequence { Name = "Frame" };

        foreach (var node in nodes) {
            sequence.Children.Add(node);
        }

        return new(system) { FrameSize = new(Size, Size), Game = sequence };
    }

    /// <summary>A node that writes the source, so the copy has something to be ordered behind.</summary>
    static DelegateSceneRenderer Producer(string target) =>
        new() {
            Name = "Producer",
            OnBuild = (_, frame) => {
                var texture = frame.Texture("Producer", target);

                frame.Graph.AddPass(
                    "Producer",
                    pass => {
                        pass.ColourAttachment(texture);
                        pass.Execute(context => context.CommandList.Draw(3));
                    }
                );
            }
        };

    /// <summary>A node that reads the copy, so the copy has a consumer culling can see.</summary>
    static DelegateSceneRenderer Consumer(string target) =>
        new() {
            Name = "Consumer",
            OnBuild = (_, frame) => {
                var texture = frame.Texture("Consumer", target);

                frame.Graph.AddPass(
                    "Consumer",
                    pass => {
                        pass.Kind = PassKind.Compute;
                        pass.Reads(texture);
                        pass.SideEffect();
                        pass.Execute(context => context.CommandList.Dispatch(1));
                    }
                );
            }
        };

    void Frame(GraphicsCompositor compositor) {
        var list = device.BeginCommandList();

        graph.Reset();
        compositor.Build(graph, effects, device);
        graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    IReadOnlyList<RecordedCommand> Copies => device.Recorder!.OfKind(RecordedCommandKind.CopyTexture);

    // --- What it does -------------------------------------------------------

    /// <summary>The copy happens, once, between the producer and the reader of the snapshot.</summary>
    /// <remarks>
    ///     ⚠ The ordering is the whole feature. A copy recorded outside the graph runs wherever the
    ///     host happened to write it, and moves whatever the source held before the pass that filled
    ///     it — which is the previous frame's picture, and therefore looks almost right.
    /// </remarks>
    [Fact]
    public void A_copy_runs_between_the_producer_and_the_reader_of_the_snapshot() {
        var copy = new TextureCopyRenderer {
            Name = "SceneColourCopy",
            Source = "SceneColour",
            Destination = "SceneColourCopy"
        };

        var compositor = Compositor(Producer("SceneColour"), copy, Consumer("SceneColourCopy"));

        compositor.Resources.Add(
            Declared("SceneColour", TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource)
        );

        compositor.Resources.Add(
            Declared("SceneColourCopy", TextureUsage.Sampled | TextureUsage.CopyDestination)
        );

        Frame(compositor);

        var moved = Assert.Single(Copies);
        var drawn = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Draw));
        var read = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.Dispatch));

        Assert.True(drawn.Sequence < moved.Sequence, "the copy was recorded before what it copies");
        Assert.True(moved.Sequence < read.Sequence, "the copy was recorded after what reads it");
        Assert.Equal(1, copy.CopyCount);

        var between = device.Recorder.OfKind(RecordedCommandKind.Barrier)
            .Count(barrier => barrier.Sequence > drawn.Sequence && barrier.Sequence < moved.Sequence);

        Assert.True(between > 0, "nothing transitioned the source between the draw and the copy");
    }

    /// <summary>
    ///     A copy nothing reads is culled, so a document with a water node costs nothing without water.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § D8]'s claim about the <c>!Water</c> node, one level down: the pass is in the
    ///     document, and a project that never reads the snapshot pays neither the copy nor the memory
    ///     the destination would have taken.
    /// </remarks>
    [Fact]
    public void A_copy_nothing_reads_is_dropped_with_its_target() {
        var compositor = Compositor(
            Producer("SceneColour"),
            new TextureCopyRenderer { Name = "Copy", Source = "SceneColour", Destination = "SceneColourCopy" }
        );

        compositor.Resources.Add(
            Declared("SceneColour", TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource)
        );

        compositor.Resources.Add(
            Declared("SceneColourCopy", TextureUsage.Sampled | TextureUsage.CopyDestination)
        );

        Frame(compositor);

        Assert.Empty(Copies);
    }

    // --- What it refuses ----------------------------------------------------

    /// <summary>
    ///     ⚠ A source that is not a copy source is named, rather than being a driver's problem.
    /// </summary>
    /// <remarks>
    ///     Missing usage is a validation error on a debug driver and silently nothing on a release
    ///     one — so the build says which resource and which flag, at the point the document could have
    ///     said it.
    /// </remarks>
    [Fact]
    public void A_source_that_cannot_be_copied_from_is_refused_by_name() {
        var compositor = Compositor(
            Producer("SceneColour"),
            new TextureCopyRenderer { Name = "Copy", Source = "SceneColour", Destination = "SceneColourCopy" },
            Consumer("SceneColourCopy")
        );

        compositor.Resources.Add(Declared("SceneColour", TextureUsage.ColourTarget | TextureUsage.Sampled));
        compositor.Resources.Add(Declared("SceneColourCopy", TextureUsage.Sampled | TextureUsage.CopyDestination));

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Equal("SceneColour", thrown.Name);
        Assert.Contains("CopySource", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>And a destination that is not a copy destination.</summary>
    [Fact]
    public void A_destination_that_cannot_be_copied_into_is_refused_by_name() {
        var compositor = Compositor(
            Producer("SceneColour"),
            new TextureCopyRenderer { Name = "Copy", Source = "SceneColour", Destination = "SceneColourCopy" },
            Consumer("SceneColourCopy")
        );

        compositor.Resources.Add(
            Declared("SceneColour", TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource)
        );

        compositor.Resources.Add(Declared("SceneColourCopy", TextureUsage.Sampled));

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Equal("SceneColourCopy", thrown.Name);
        Assert.Contains("CopyDestination", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A destination at a different size is refused rather than silently taking a corner.
    /// </summary>
    /// <remarks>
    ///     The failure this one prevents is the worst-behaved of the three: a half-resolution snapshot
    ///     copies correctly into the top-left quarter and every pixel of it is a plausible colour, so
    ///     what reaches the screen is a refraction that is subtly, consistently wrong.
    /// </remarks>
    [Fact]
    public void A_destination_of_a_different_size_is_refused_rather_than_cropped() {
        var compositor = Compositor(
            Producer("SceneColour"),
            new TextureCopyRenderer { Name = "Copy", Source = "SceneColour", Destination = "Half" },
            Consumer("Half")
        );

        compositor.Resources.Add(
            Declared("SceneColour", TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource)
        );

        compositor.Resources.Add(Declared("Half", TextureUsage.Sampled | TextureUsage.CopyDestination, 0.5f));

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Contains("does not rescale", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>And a destination of a different format, for the same reason.</summary>
    [Fact]
    public void A_destination_of_a_different_format_is_refused() {
        var compositor = Compositor(
            Producer("SceneColour"),
            new TextureCopyRenderer { Name = "Copy", Source = "SceneColour", Destination = "Eight" },
            Consumer("Eight")
        );

        compositor.Resources.Add(
            Declared("SceneColour", TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource)
        );

        compositor.Resources.Add(
            new RenderResourceAsset {
                Name = "Eight",
                Format = PixelFormat.Rgba8UNorm,
                Usage = TextureUsage.Sampled | TextureUsage.CopyDestination
            }
        );

        var thrown = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Contains("moves texels", thrown.Message, StringComparison.Ordinal);
    }
}
