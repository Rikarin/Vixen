// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Yaml;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Bytes into a frame's buffers, and answers back out of them.
/// </summary>
/// <remarks>
///     <para>
///         The compute node could always say what it read. What nothing could say is where the values
///         it read came from, or where its answer went: a graph buffer has no handle until the graph
///         compiles, and a device-local one is not addressable by the host at either end. So a
///         histogram had no way to start cleared, and the number it produced had no way home.
///     </para>
///     <para>
///         Both halves are copies, and a copy is a pass — which is the point. What is being asserted
///         here is not that <c>CopyBuffer</c> works; it is that the copy is <em>in the graph</em>, so
///         that the upload is ordered ahead of every reader with a barrier after it, and the readback
///         is ordered behind the producer with a barrier before it. A copy recorded outside the graph
///         reads whatever the buffer held before the dispatch, which is zeroes on a fresh allocation
///         and last frame's contents on a recycled one — and both of those look like an answer.
///     </para>
/// </remarks>
public class BufferTransferTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly RenderSystem system = new();
    readonly RenderGraph graph;

    public BufferTransferTests() {
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

    const long Size = 1024;

    GraphicsCompositor Compositor(params SceneRenderer[] nodes) {
        var sequence = new SceneRendererSequence { Name = "Frame" };

        foreach (var node in nodes) {
            sequence.Children.Add(node);
        }

        return new(system) { FrameSize = new(16, 16), Game = sequence };
    }

    /// <summary>A buffer the host owns, so a test knows the handle a copy should name.</summary>
    /// <remarks>
    ///     Imported rather than declared in most of these, for the reason a swapchain image is: the
    ///     assertion is about <em>which</em> buffer the copy touched, and a transient's handle belongs
    ///     to a pool that may hand out a different one next frame. The declared path gets its own
    ///     fixture below.
    /// </remarks>
    ImportedBuffer Imported(string name, BufferUsage usage) {
        var description = new BufferDescription(Size, usage, MemoryAccess.DeviceLocal, name);
        return new(device.CreateBuffer(description), description);
    }

    /// <summary>Runs one whole frame: build, execute, submit.</summary>
    void Frame(GraphicsCompositor compositor) {
        var list = device.BeginCommandList();

        graph.Reset();
        compositor.Build(graph, effects, device);
        graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <summary>A node that reads a buffer, so that an upload into it has a consumer.</summary>
    /// <remarks>
    ///     A delegate rather than a <see cref="ComputeRenderer" />, because what is being asserted is
    ///     the edge between two passes and a real dispatch would drag in an effect provider, a
    ///     pipeline cache and a descriptor layout to say the same thing.
    /// </remarks>
    static DelegateSceneRenderer Consumer(string buffer) =>
        new() {
            Name = "Consumer",
            OnBuild = (_, frame) => {
                var target = frame.Buffer("Consumer", buffer);

                frame.Graph.AddPass(
                    "Consumer",
                    pass => {
                        pass.Kind = PassKind.Compute;
                        pass.Reads(target);
                        pass.SideEffect();
                        pass.Execute(context => context.CommandList.Dispatch(1));
                    }
                );
            }
        };

    static long Packed(BufferHandle handle) => (long)handle.Value.Packed;

    IReadOnlyList<RecordedCommand> Copies => device.Recorder!.OfKind(RecordedCommandKind.CopyBuffer);

    // --- Upload -------------------------------------------------------------

    /// <summary>
    ///     An upload puts its bytes in the buffer it names.
    /// </summary>
    /// <remarks>
    ///     The destination handle, the destination offset and the length, because all three are things
    ///     a copy can get wrong in a way that produces a buffer rather than an error.
    /// </remarks>
    [Fact]
    public void An_upload_copies_its_bytes_into_the_buffer_it_names() {
        var target = Imported("Histogram", BufferUsage.Storage | BufferUsage.CopyDestination);
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Histogram", Offset = 64 };

        upload.Set<uint>([1, 2, 3, 4]);

        var compositor = Compositor(upload, Consumer("Histogram"));
        compositor.BufferImports["Histogram"] = target;

        Frame(compositor);

        var copy = Assert.Single(Copies);

        Assert.Equal(Packed(target.Buffer), copy.C);
        Assert.Equal(64, copy.D);
        Assert.Equal(16, copy.E);
        Assert.Equal(1, upload.UploadCount);
    }

    /// <summary>
    ///     The upload runs before what reads the buffer, and there is a barrier between them.
    /// </summary>
    /// <remarks>
    ///     The reason the node exists at all. Recording the same copy by hand puts it wherever the
    ///     host happened to write it, and the driver is free to start a dispatch that reads the buffer
    ///     before the copy has landed — a race that reproduces on one vendor and not another.
    /// </remarks>
    [Fact]
    public void An_upload_is_ordered_ahead_of_what_reads_the_buffer() {
        var target = Imported("Histogram", BufferUsage.Storage | BufferUsage.CopyDestination);
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Histogram" };

        upload.Set<uint>([7]);

        var compositor = Compositor(upload, Consumer("Histogram"));
        compositor.BufferImports["Histogram"] = target;

        Frame(compositor);

        var copy = Assert.Single(Copies);
        var dispatch = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.Dispatch));

        Assert.True(copy.Sequence < dispatch.Sequence, "the upload was recorded after its reader");

        var between = device.Recorder.OfKind(RecordedCommandKind.Barrier)
            .Count(barrier => barrier.Sequence > copy.Sequence && barrier.Sequence < dispatch.Sequence);

        Assert.True(between > 0, "nothing transitioned the buffer between the copy and the dispatch");
    }

    /// <summary>
    ///     A node with nothing staged declares no pass.
    /// </summary>
    /// <remarks>
    ///     What an authored frame running against a host that has not filled it in yet should cost,
    ///     which is nothing. The alternative — a zero-length copy — is a pass the graph has to keep and
    ///     a driver call for no bytes.
    /// </remarks>
    [Fact]
    public void An_upload_with_no_bytes_declares_no_pass() {
        var target = Imported("Histogram", BufferUsage.Storage | BufferUsage.CopyDestination);
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Histogram" };

        var compositor = Compositor(upload);
        compositor.BufferImports["Histogram"] = target;

        Frame(compositor);

        Assert.Empty(Copies);
        Assert.Equal(0, upload.UploadCount);
        Assert.Equal(0, graph.PassCount);
    }

    /// <summary>
    ///     What refreshes the bytes runs on every build, before the copy is sized.
    /// </summary>
    [Fact]
    public void An_uploads_callback_supplies_the_bytes_for_the_frame() {
        var target = Imported("Histogram", BufferUsage.Storage | BufferUsage.CopyDestination);
        var counter = 0u;

        using var upload = new BufferUploadRenderer {
            Name = "Fill",
            Buffer = "Histogram",
            OnUpload = node => node.Set<uint>([++counter, counter])
        };

        var compositor = Compositor(upload, Consumer("Histogram"));
        compositor.BufferImports["Histogram"] = target;

        Frame(compositor);
        Frame(compositor);

        Assert.Equal(2, upload.UploadCount);
        Assert.Equal([2u, 2u], MemoryMarshal.Cast<byte, uint>(upload.Data).ToArray());
        Assert.All(Copies, copy => Assert.Equal(8, copy.E));
    }

    /// <summary>
    ///     A buffer that was not declared as a copy destination is refused, naming the node.
    /// </summary>
    /// <remarks>
    ///     The mistake this catches is a usage flag missing from a document, and what it would
    ///     otherwise produce is a validation error on a debug driver and silence on a release one — so
    ///     the frame renders, the histogram is empty, and nothing anywhere says why.
    /// </remarks>
    [Fact]
    public void An_upload_into_a_buffer_no_copy_may_touch_is_refused() {
        var target = Imported("Histogram", BufferUsage.Storage);
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Histogram" };

        upload.Set<uint>([1]);

        var compositor = Compositor(upload);
        compositor.BufferImports["Histogram"] = target;

        var refusal = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Equal("Fill", refusal.Node);
        Assert.Equal("Histogram", refusal.Name);
        Assert.Contains("CopyDestination", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An upload that runs off the end of the buffer is refused before it is recorded.</summary>
    [Fact]
    public void An_upload_that_overruns_the_buffer_is_refused() {
        var target = Imported("Histogram", BufferUsage.Storage | BufferUsage.CopyDestination);

        using var upload = new BufferUploadRenderer {
            Name = "Fill",
            Buffer = "Histogram",
            Offset = Size - 4
        };

        upload.Set<uint>([1, 2, 3]);

        var compositor = Compositor(upload);
        compositor.BufferImports["Histogram"] = target;

        var refusal = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Contains("runs off the end", refusal.Message, StringComparison.Ordinal);
    }

    // --- Readback -----------------------------------------------------------

    /// <summary>
    ///     A readback copies out of the buffer it names, and survives having no consumer.
    /// </summary>
    /// <remarks>
    ///     The second half is the one that would go wrong quietly. Nothing in the frame reads what a
    ///     readback writes — the host does — so a pass that did not declare a side effect is a pass
    ///     culling is right to remove, and what is left is a readback buffer full of zeroes.
    /// </remarks>
    [Fact]
    public void A_readback_copies_out_of_the_buffer_and_is_not_culled() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Exposure", Size = 16 };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Frame(compositor);

        var copy = Assert.Single(Copies);

        Assert.Equal(Packed(source.Buffer), copy.A);
        Assert.Equal(16, copy.E);
        Assert.Equal(1, graph.SurvivingPassCount);
        Assert.Equal(1, readback.CopyCount);
    }

    /// <summary>A readback with no size takes the whole buffer.</summary>
    [Fact]
    public void A_readback_with_no_size_takes_the_whole_buffer() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Exposure", Offset = 256 };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Frame(compositor);

        var copy = Assert.Single(Copies);

        Assert.Equal(256, copy.B);
        Assert.Equal(Size - 256, copy.E);
        Assert.Equal(Size - 256, readback.Length);
    }

    /// <summary>
    ///     Nothing comes back until something has been copied.
    /// </summary>
    /// <remarks>
    ///     A <c>Fetch</c> before the first frame has an obvious wrong answer available — the readback
    ///     buffer's contents, which are whatever the allocation started as — and saying "there was
    ///     nothing that far back" is the difference between a caller waiting a frame and a caller
    ///     believing a zero.
    /// </remarks>
    [Fact]
    public void A_readback_has_nothing_to_fetch_before_the_first_frame() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Exposure", Size = 16 };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Assert.False(readback.Fetch());
        Assert.Equal(0, readback.ReadCount);

        Frame(compositor);
        device.WaitIdle();

        Assert.True(readback.Fetch());
        Assert.Equal(1, readback.ReadCount);
        Assert.Equal(16, readback.Data.Length);
    }

    /// <summary>
    ///     Consecutive frames copy into different regions of the readback buffer.
    /// </summary>
    /// <remarks>
    ///     The ring, and the reason it is one. A readback that wrote the same bytes every frame would
    ///     be a copy into memory the host may be reading for a frame that has not finished — the same
    ///     hazard <see cref="UploadBuffer{T}" /> and the descriptor allocator have, from the far end
    ///     of the frame.
    /// </remarks>
    [Fact]
    public void Consecutive_frames_read_back_into_different_regions() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Exposure", Size = 16 };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Frame(compositor);
        Frame(compositor);
        Frame(compositor);

        var offsets = Copies.Select(copy => copy.D).ToArray();

        // Two frames in flight, so two regions, and the third frame is back where the first was —
        // which is a region the device has finished with by then.
        Assert.Equal(3, offsets.Length);
        Assert.NotEqual(offsets[0], offsets[1]);
        Assert.Equal(offsets[0], offsets[2]);
    }

    /// <summary>
    ///     A latency of a frame or more fetches itself, from the frame it names.
    /// </summary>
    /// <remarks>
    ///     What makes a readback free rather than a stall: the region read belongs to a frame the
    ///     host's loop has already waited on, so the build takes it without anything in the frame loop
    ///     knowing a readback exists. The first two builds have nothing that far back and read
    ///     nothing.
    /// </remarks>
    [Fact]
    public void A_deferred_readback_fetches_the_frame_its_latency_names() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);
        var seen = 0;

        using var readback = new BufferReadbackRenderer {
            Name = "Read",
            Buffer = "Exposure",
            Size = 16,
            Latency = 2,
            OnRead = _ => seen++
        };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Frame(compositor);
        Assert.Equal(0, readback.ReadCount);

        Frame(compositor);
        Assert.Equal(0, readback.ReadCount);

        Frame(compositor);
        Assert.Equal(1, readback.ReadCount);

        Frame(compositor);
        Assert.Equal(2, readback.ReadCount);
        Assert.Equal(2, seen);
    }

    /// <summary>
    ///     Changing how much to read changes how much comes back.
    /// </summary>
    /// <remarks>
    ///     Sixteen bytes and thirty-two round to the same region stride, which is the case that makes
    ///     this worth asserting: a ring that decided nothing had changed because its stride had not
    ///     would keep answering with the first length, out of a buffer whose contents are correct.
    /// </remarks>
    [Fact]
    public void Changing_how_much_to_read_changes_how_much_comes_back() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Exposure", Size = 16 };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Frame(compositor);
        device.WaitIdle();
        readback.Fetch();

        Assert.Equal(16, readback.Data.Length);

        readback.Size = 32;
        Frame(compositor);
        device.WaitIdle();
        readback.Fetch();

        Assert.Equal(32, readback.Length);
        Assert.Equal(32, readback.Data.Length);
        Assert.Equal(32, Copies[^1].E);
    }

    /// <summary>
    ///     A buffer that was not declared as a copy source is refused, naming the node.
    /// </summary>
    [Fact]
    public void A_readback_of_a_buffer_no_copy_may_touch_is_refused() {
        var source = Imported("Exposure", BufferUsage.Storage);
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Exposure" };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        var refusal = Assert.Throws<CompositorBindingException>(() => Frame(compositor));

        Assert.Equal("Read", refusal.Node);
        Assert.Contains("CopySource", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A readback of a range outside the buffer is refused.</summary>
    [Fact]
    public void A_readback_of_a_range_outside_the_buffer_is_refused() {
        var source = Imported("Exposure", BufferUsage.Storage | BufferUsage.CopySource);

        using var readback = new BufferReadbackRenderer {
            Name = "Read",
            Buffer = "Exposure",
            Offset = Size - 8,
            Size = 32
        };

        var compositor = Compositor(readback);
        compositor.BufferImports["Exposure"] = source;

        Assert.Throws<CompositorBindingException>(() => Frame(compositor));
    }

    /// <summary>Neither node can name a buffer the frame does not have.</summary>
    [Fact]
    public void Neither_node_can_name_a_buffer_the_frame_does_not_have() {
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Nowhere" };
        upload.Set<uint>([1]);

        var refusal = Assert.Throws<CompositorBindingException>(() => Frame(Compositor(upload)));

        Assert.Equal("buffer", refusal.Kind);
        Assert.Equal("Nowhere", refusal.Name);
    }

    // --- The pair over a declared buffer ------------------------------------

    /// <summary>
    ///     A buffer the frame declares can be filled by one node and read by another.
    /// </summary>
    /// <remarks>
    ///     The shape every downstream item wants: the buffer is the graph's, so it is aliased with
    ///     whatever else fits its memory and freed at its last use, and the two ends of it are both
    ///     passes the graph ordered. A host that owned the buffer would have to keep it alive across
    ///     the whole frame to do the same thing.
    /// </remarks>
    [Fact]
    public void A_declared_buffer_can_be_filled_and_read_in_one_frame() {
        using var upload = new BufferUploadRenderer { Name = "Fill", Buffer = "Scratch" };
        using var readback = new BufferReadbackRenderer { Name = "Read", Buffer = "Scratch", Size = 16 };

        upload.Set<uint>([9, 9, 9, 9]);

        var compositor = Compositor(upload, readback);

        compositor.BufferResources.Add(
            new() {
                Name = "Scratch",
                Size = 256,
                Usage = BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination
            }
        );

        Frame(compositor);

        var copies = Copies;

        Assert.Equal(2, copies.Count);

        // The same physical buffer at both ends: the upload's destination is the readback's source,
        // which is what says the graph gave one transient to both passes.
        Assert.Equal(copies[0].C, copies[1].A);
        Assert.Equal(2, graph.SurvivingPassCount);
    }

    // --- As a document ------------------------------------------------------

    const string Document = """
        version: 2
        stages:
          - name: Opaque
        buffers:
          - name: Scratch
            size: 256
            usage: Storage, CopySource, CopyDestination
        game: !Sequence
          name: Frame
          children:
            - !Upload
              name: Seed
              buffer: Scratch
              offset: 32
            - !Readback
              name: Answer
              buffer: Scratch
              size: 16
              latency: 2
        """;

    /// <summary>
    ///     A document places both nodes, and the host is handed the ends it has to fill and drain.
    /// </summary>
    /// <remarks>
    ///     The same division every other node has: what the file decides is where the copies go and
    ///     what they touch; what the host supplies is this frame's bytes, which no file can know.
    ///     Handing the nodes back by name is what saves a host walking the tree it did not build.
    /// </remarks>
    [Fact]
    public void A_document_places_both_nodes_and_hands_them_back_by_name() {
        var builder = new CompositorBuilder(system) { Device = device };
        var compositor = builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        var upload = Assert.Contains("Seed", builder.Uploads);
        var readback = Assert.Contains("Answer", builder.Readbacks);

        Assert.Equal(32, upload.Offset);
        Assert.Equal(16, readback.Size);
        Assert.Equal(2, readback.Latency);

        upload.Set<uint>([5, 5]);

        try {
            Frame(compositor);

            var copies = Copies;

            Assert.Equal(2, copies.Count);
            Assert.Equal(32, copies[0].D);
            Assert.Equal(8, copies[0].E);
            Assert.Equal(16, copies[1].E);
        } finally {
            upload.Dispose();
            readback.Dispose();
        }
    }

    /// <summary>
    ///     Both nodes survive being written out and read back as a document.
    /// </summary>
    /// <remarks>
    ///     What an editor does every time somebody drags a node. The tags have to round-trip too, or
    ///     saving turns an upload into whatever the base type deserialises as — and a latency of zero
    ///     is what a dropped field looks like, which is the stall rather than the free path.
    /// </remarks>
    [Fact]
    public void Both_nodes_survive_a_round_trip_through_yaml() {
        var original = YamlSerializer.Parse<GraphicsCompositorAsset>(Document);
        var reread = YamlSerializer.Parse<GraphicsCompositorAsset>(YamlSerializer.ToYaml(original));

        var children = Assert.IsType<SequenceAsset>(reread.Game).Children;
        var upload = Assert.IsType<BufferUploadAsset>(children[0]);
        var readback = Assert.IsType<BufferReadbackAsset>(children[1]);

        Assert.Equal("Scratch", upload.Buffer);
        Assert.Equal(32, upload.Offset);
        Assert.Equal("Scratch", readback.Buffer);
        Assert.Equal(16, readback.Size);
        Assert.Equal(2, readback.Latency);

        // And the buffer they both name, whose usage flags are what makes the two copies legal at all.
        var declared = Assert.Single(reread.Buffers);

        Assert.Equal(
            BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
            declared.Usage
        );
    }

    /// <summary>A build that dropped a node does not leave the host holding it.</summary>
    /// <remarks>
    ///     An editor reloading a document is the case: a stale entry is a host filling a buffer no
    ///     frame copies, which is a value that silently stops arriving.
    /// </remarks>
    [Fact]
    public void Rebuilding_from_a_document_without_the_nodes_forgets_them() {
        var builder = new CompositorBuilder(system) { Device = device };
        builder.Build(YamlSerializer.Parse<GraphicsCompositorAsset>(Document));

        Assert.NotEmpty(builder.Uploads);

        builder.Build(
            YamlSerializer.Parse<GraphicsCompositorAsset>(
                """
                version: 2
                stages:
                  - name: Opaque
                game: !Sequence
                  name: Frame
                """
            )
        );

        Assert.Empty(builder.Uploads);
        Assert.Empty(builder.Readbacks);
    }
}
