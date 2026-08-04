// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     GPU-driven culling — docs/plan/06 § Frame structure, step 4, "parallel <em>or GPU</em>".
/// </summary>
/// <remarks>
///     <para>
///         Three kinds of claim, because a compute pass cannot be run in a unit test and each kind
///         covers what the others cannot. The <em>arithmetic</em> is checked through
///         <see cref="GpuCulling.IsVisible" />, the host's transliteration of the shader, against
///         <see cref="VisibilityGroup" /> — which is the definition — over randomised scenes. The
///         <em>layout</em> is checked against the constants the shader declares, since the two sides
///         agree by construction or not at all. And the <em>frame</em> is checked against the
///         recorded command stream: that the dispatch covers every word of every view, that the copy
///         out of it is separated by a barrier, and that a device which cannot run it still produces
///         the right bits.
///     </para>
///     <para>
///         What none of them cover is the shader still containing the arithmetic the mirror mirrors,
///         which is why <see cref="The_shader_tests_what_the_host_says_it_does" /> reads the source —
///         the same defence, and the same reason, as the clustered path's.
///     </para>
/// </remarks>
public class GpuVisibilityGroupTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public GpuVisibilityGroupTests() {
        effects.AddProvider(new AlwaysCompiles(device));
        pipelines = new(device);
    }

    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The records --------------------------------------------------------

    /// <summary>
    ///     The records are the size and shape <c>Culling.rvn</c> declares.
    /// </summary>
    /// <remarks>
    ///     The host writes bytes and the shader reads structs, so a member that moved is not a
    ///     compile error anywhere — it is a frustum built from a stage mask, which culls everything
    ///     or nothing and says why nowhere.
    /// </remarks>
    [Fact]
    public void The_records_match_the_shaders() {
        // centre + radius, then two halves of the stage mask, flags and the padding the shader
        // declares: two sixteen-byte rows.
        Assert.Equal(32, Marshal.SizeOf<CullObject>());

        // Six planes, then position + cutoff, then two halves of the mask and the two counts — 128
        // bytes of frustum test — and then the occlusion half: a matrix, the level count, the flags
        // and the two error fields. Phase 6's software threshold is the row after that, and the three
        // words of padding beside it are what a field added past a full row costs.
        Assert.Equal(224, Marshal.SizeOf<CullView>());

        Assert.Equal(32, GpuCulling.WordSize);
        Assert.Equal(64, GpuCulling.WorkgroupSize);
        Assert.Equal(8, GpuCulling.ReduceWorkgroupSize);
        Assert.Equal(1u, GpuCulling.Alive);
        Assert.Equal(1u, GpuCulling.Occluders);
        Assert.Equal(6, BoundingFrustum.PlaneCount);
    }

    /// <summary>A word covers 32 objects, and the tail object still gets one.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(32, 1)]
    [InlineData(33, 2)]
    [InlineData(4096, 128)]
    public void The_word_count_covers_every_object(int objects, int expected) =>
        Assert.Equal(expected, GpuCulling.WordsFor(objects));

    /// <summary>Every word of a view is dispatched, including the ones in the last part-workgroup.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(64, 1)]
    [InlineData(65, 2)]
    public void The_dispatch_covers_every_word(int words, int expected) =>
        Assert.Equal(expected, GpuCulling.Groups(words));

    /// <summary>
    ///     Two device words are one host word, low half first.
    /// </summary>
    /// <remarks>
    ///     The device answers in 32-bit words because a 64-bit integer is optional on Vulkan and
    ///     absent from WebGPU, so this is where the two conventions meet. An odd count is the case
    ///     worth pinning: a store of 40 objects is two device words and one host word, and a store of
    ///     20 is one of each — the second half of which nothing wrote.
    /// </remarks>
    [Fact]
    public void The_devices_words_reassemble_into_the_hosts() {
        var host = new ulong[3];

        GpuCulling.Unpack([0x0000_0001u, 0x8000_0000u, 0xFFFF_FFFFu], host);

        Assert.Equal(0x8000_0000_0000_0001UL, host[0]);
        Assert.Equal(0x0000_0000_FFFF_FFFFUL, host[1]);
        Assert.Equal(0UL, host[2]);
    }

    // --- The arithmetic -----------------------------------------------------

    /// <summary>
    ///     What the shader will decide is what the CPU path decides, over randomised scenes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The load-bearing test. <see cref="VisibilityGroup" /> is the definition — it is what
    ///         doc 06's own testing table compares against a brute-force oracle — and this compares
    ///         the packing and the test the device will run against it, object by object and view by
    ///         view. Anything that makes the two paths disagree, from a plane the packer wrote in the
    ///         wrong order to a stage mask whose halves were swapped, fails here.
    ///     </para>
    ///     <para>
    ///         Two views, because one would not notice a packer that ignored
    ///         <see cref="RenderView.MaximumDistance" />, and a stage the second view does not draw,
    ///         because the mask is split in two on the way to the device and a swap of the halves
    ///         looks like nothing else.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_mirror_of_the_shader_agrees_with_the_cpu_path() {
        var scene = Gen.Select(
                Gen.Float[-150f, 150f],
                Gen.Float[-150f, 150f],
                Gen.Float[-150f, 150f],
                Gen.Float[0.1f, 12f],
                Gen.Int[0, 1]
            )
            .Array[1, 200];

        scene.Sample(
            objects => {
                using var store = new RenderObjectStore();
                using var expected = new VisibilityGroup();

                foreach (var (x, y, z, radius, stage) in objects) {
                    store.Add(
                        new() {
                            Bounds = new(new(x, y, z), radius),
                            Stages = RenderStageMask.Of(stage),
                            IsAlive = true
                        }
                    );
                }

                var views = new[] { Camera(RenderStageMask.Of(0) | RenderStageMask.Of(1)), Camera(RenderStageMask.Of(0), 90f) };
                expected.Cull(store, views);

                var wordCount = GpuCulling.WordsFor(store.Count);

                for (var view = 0; view < views.Length; view++) {
                    var packedView = GpuCulling.Pack(views[view], store.Count, wordCount);

                    for (var i = 0; i < store.Count; i++) {
                        Assert.Equal(
                            expected.IsVisible(view, new(i)),
                            GpuCulling.IsVisible(GpuCulling.Pack(store[new(i)]), packedView)
                        );
                    }
                }
            },
            iter: 100
        );
    }

    /// <summary>A dead slot is culled by the flag, before anything is measured.</summary>
    /// <remarks>
    ///     The one rejection the device cannot infer: a removed object keeps its bounds and its stage
    ///     mask — <see cref="RenderObjectStore" /> reuses slots rather than compacting them — so
    ///     without the flag it would be culled by nothing at all.
    /// </remarks>
    [Fact]
    public void A_removed_object_is_culled_by_its_flag() {
        using var store = new RenderObjectStore();

        var id = store.Add(new() { Bounds = new(new(0f, 0f, 10f), 1f), Stages = RenderStageMask.Of(0), IsAlive = true });
        store.Remove(id);

        var view = GpuCulling.Pack(Camera(RenderStageMask.Of(0)), store.Count, GpuCulling.WordsFor(store.Count));

        Assert.False(GpuCulling.IsVisible(GpuCulling.Pack(store[id]), view));
    }

    /// <summary>
    ///     The high half of a stage mask survives the trip.
    /// </summary>
    /// <remarks>
    ///     <see cref="RenderStageMask" /> is 64 bits and the device sees two 32-bit halves, so a
    ///     stage above 31 is the only thing that notices whether the split and the test agree.
    /// </remarks>
    [Fact]
    public void A_stage_above_the_low_half_still_matches() {
        var candidate = GpuCulling.Pack(
            new() { Bounds = new(new(0f, 0f, 10f), 1f), Stages = RenderStageMask.Of(40), IsAlive = true }
        );

        Assert.True(GpuCulling.IsVisible(candidate, GpuCulling.Pack(Camera(RenderStageMask.Of(40)), 1, 1)));
        Assert.False(GpuCulling.IsVisible(candidate, GpuCulling.Pack(Camera(RenderStageMask.Of(8)), 1, 1)));
    }

    /// <summary>
    ///     The shader still tests what the host says it does.
    /// </summary>
    /// <remarks>
    ///     A test that reads shader source, which is worth defending: everything above tests the
    ///     host's <em>mirror</em> of the culling test, and the mirror is not what runs. Narrow on
    ///     purpose — the rounding slack, which is the one number a tighter-looking rewrite would drop
    ///     and which decides every tangent case, and the word size the host unpacks by.
    /// </remarks>
    [Fact]
    public void The_shader_tests_what_the_host_says_it_does() {
        var source = Source("Pipeline", "Culling.rvn");

        Assert.Contains(
            "val slack = RoundingSlack * (radius + dot(abs(center), abs(plane.xyz)) + abs(plane.w))",
            source,
            StringComparison.Ordinal
        );

        Assert.Contains("return distance < -radius - slack", source, StringComparison.Ordinal);
        Assert.Contains($"const val WordSize = {GpuCulling.WordSize}", source, StringComparison.Ordinal);
        Assert.Contains($"[ComputeShader({GpuCulling.WorkgroupSize})]", source, StringComparison.Ordinal);

        // The same number as MathUtil.RoundingSlack, which is what the mirror uses and what the
        // sphere-versus-plane test on the host widens by. Compared as a float rather than as text:
        // the two are spelled differently and only their values have to agree.
        const string declaration = "const val RoundingSlack = ";
        var start = source.IndexOf(declaration, StringComparison.Ordinal);

        Assert.True(start >= 0, "the shader no longer declares a rounding slack");

        start += declaration.Length;
        var literal = source[start..source.IndexOf('f', start)];

        Assert.Equal(MathUtil.RoundingSlack, float.Parse(literal, CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     The late variant reads the word before it writes it, and subtracts what was already drawn.
    /// </summary>
    /// <remarks>
    ///     Source again, and this one earns it more than most: the whole of two-phase culling on the
    ///     device is three lines that turn an answer into a <em>difference</em>, and a rewrite that
    ///     dropped the subtraction would produce the union — which draws every visible object twice
    ///     and looks, in a frame capture, like a scene that is merely expensive.
    /// </remarks>
    [Fact]
    public void The_late_shader_subtracts_what_the_main_pass_drew() {
        var source = Source("Pipeline", "Culling.rvn");

        // The host's key is qualified by the shader and the declaration is not, which is the
        // generator's doing rather than either side's — so the two are tied together here instead of
        // one of them being spelled twice.
        Assert.Equal($"{GpuCulling.ShaderName}.Late", GpuCulling.LateKey);
        Assert.Contains("[Permutation] val Late: bool = false", source, StringComparison.Ordinal);

        // Read from the same buffer it writes, which is what makes the second buffer unnecessary.
        Assert.Contains("already = visibility[slot]", source, StringComparison.Ordinal);
        Assert.Contains("val drawn = (already & (1u << i)) != 0u", source, StringComparison.Ordinal);
        Assert.Contains("if (!drawn && Visible(objects[index], view))", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two phases ask for two variants, and every permutation is named in both.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A key is how a variant is chosen, so this is the one place a phase can be silently
    ///         wrong: a late key that resolved to the main variant would dispatch a pass that writes
    ///         the full frustum answer over the difference, and the late draws would draw the whole
    ///         scene a second time.
    ///     </para>
    ///     <para>
    ///         Which is why <c>Late</c> is set by the phase alone and not gated on occlusion. A
    ///         two-phase frame that has no pyramid yet still runs a late pass, gets an empty
    ///         difference, and draws nothing late — and that is a different thing from not dispatching
    ///         at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_phases_ask_for_two_variants() {
        var main = GpuCulling.Key(true);
        var late = GpuCulling.Key(true, CullPhase.Late);

        Assert.NotEqual(main, late);
        Assert.Equal(GpuCulling.ShaderName, late.ShaderName);

        Assert.Equal("false", Value(main, GpuCulling.LateKey));
        Assert.Equal("true", Value(late, GpuCulling.LateKey));

        // Both permutations named in every key, whatever their values, so that the frames either side
        // of a pyramid appearing ask for keys that differ in value rather than in shape.
        foreach (var key in (EffectKey[])[main, late, GpuCulling.Key(false), GpuCulling.Key(false, CullPhase.Late)]) {
            Assert.NotNull(Value(key, GpuCulling.OcclusionKey));
            Assert.NotNull(Value(key, GpuCulling.LateKey));
        }

        // And with no pyramid the late phase is still the late variant, which is what stops the main
        // pass's bits surviving into the late draws.
        Assert.Equal("true", Value(GpuCulling.Key(false, CullPhase.Late), GpuCulling.LateKey));
    }

    static string? Value(EffectKey key, string name) {
        foreach (var (permutation, value) in key.Values) {
            if (string.Equals(permutation, name, StringComparison.Ordinal)) {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    ///     The late pass owes the difference: visible now, and not already drawn.
    /// </summary>
    /// <remarks>
    ///     The mirror of the whole late decision, and the order matters. An object the main pass drew
    ///     is not re-examined — not because it would fail the test, but because passing it would draw
    ///     it twice, which is the failure mode a union rather than a difference produces.
    /// </remarks>
    [Fact]
    public void The_late_pass_owes_only_the_difference() {
        var camera = Camera(RenderStageMask.Of(0));
        var view = Occluding(camera);
        var candidate = GpuCulling.Pack(Object(10f));

        // Nothing occludes: an empty pyramid is far everywhere, and under reverse-Z far is zero.
        static float Open(int x, int y, int level) => 0f;

        Assert.True(GpuCulling.IsLate(candidate, view, drawn: false, new(64, 64), Open));
        Assert.False(GpuCulling.IsLate(candidate, view, drawn: true, new(64, 64), Open));

        // And a wall in front of it is not late either, however little the main pass drew.
        static float Wall(int x, int y, int level) => 1f;

        Assert.False(GpuCulling.IsLate(candidate, view, drawn: false, new(64, 64), Wall));

        // Nor is something the frustum rejects.
        Assert.False(GpuCulling.IsLate(GpuCulling.Pack(Object(-10f)), view, drawn: false, new(64, 64), Open));
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>
    ///     With nothing to dispatch with, the frame is still culled.
    /// </summary>
    /// <remarks>
    ///     Not a degenerate case but the shipped one for a GL or WebGL target, and the one every
    ///     frame before the culling variant has compiled. A group that answered "nothing is visible"
    ///     until its shader arrived would show as a scene that fades in.
    /// </remarks>
    [Fact]
    public void Without_a_pipeline_it_culls_on_the_cpu() {
        using var store = new RenderObjectStore();
        using var visibility = new GpuVisibilityGroup(device);

        var ahead = Add(store, 10f);
        var behind = Add(store, -10f);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.False(visibility.CulledOnDevice);
        Assert.True(visibility.IsVisible(0, ahead));
        Assert.False(visibility.IsVisible(0, behind));
        Assert.Equal(1, visibility.VisibleCount(0));
    }

    /// <summary>
    ///     A device with no compute culls on the CPU rather than throwing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The WebGL2-class device, which is what <see cref="GraphicsDeviceFeatures.Minimum" />
    ///         is — and the exact target doc 06 keeps the CPU path for. Creating a compute pipeline
    ///         there throws, and the RHI's own message says to ask
    ///         <see cref="GraphicsDeviceFeatures.HasCompute" /> and take the fallback: an exception
    ///         out of the middle of <see cref="GpuVisibilityGroup.Cull" /> is not a fallback, because
    ///         the caller it escapes to has no answer to give the frame.
    ///     </para>
    ///     <para>
    ///         All three pieces, because all three create a compute pipeline and each would have
    ///         thrown on its own.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_device_with_no_compute_falls_back_rather_than_throwing() {
        using var limited = new NullDevice(new() { Record = true, Features = GraphicsDeviceFeatures.Minimum });
        using var store = new RenderObjectStore();

        var effectsFor = new EffectSystem();
        effectsFor.AddProvider(new AlwaysCompiles(limited));

        using var visibility = new GpuVisibilityGroup(limited) { Effects = effectsFor, Pipelines = new(limited) };
        using var pyramid = new HiZPyramid(limited) { Effects = effectsFor, Pipelines = new(limited) };
        using var arguments = new GpuDrawArguments(limited) { Effects = effectsFor, Pipelines = new(limited) };

        Assert.False(GpuCulling.IsSupported(limited));

        var ahead = Add(store, 10f);
        var behind = Add(store, -10f);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        // Not merely "did not throw": the frame still has the right answer, from the CPU.
        Assert.False(visibility.CulledOnDevice);
        Assert.True(visibility.IsVisible(0, ahead));
        Assert.False(visibility.IsVisible(0, behind));

        var depth = limited.CreateTexture(
            new(PixelFormat.Depth32Float, 64, 64, TextureUsage.DepthStencilTarget | TextureUsage.Sampled)
        );

        using var list = limited.BeginCommandList(QueueKind.Compute);

        Assert.False(pyramid.Build(list, limited.CreateTextureView(depth), new(64, 64)));
        Assert.False(pyramid.IsBuilt);

        arguments.Fill(store.Count);
        Assert.False(arguments.Update(list, visibility.Bits, 1, store.Count));
        Assert.False(arguments.IsFilled);

        list.Finish();
        limited.ComputeQueue.Submit([list]);

        Assert.Equal(0, limited.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     Hiding, counting and walking the words work the same on either group.
    /// </summary>
    /// <remarks>
    ///     They are the composed group's, which is the point: there is one bitset implementation in
    ///     the engine, so a feature's <c>Prepare</c> cannot behave differently depending on what
    ///     culled the frame.
    /// </remarks>
    [Fact]
    public void The_answer_is_the_same_shape_whichever_group_holds_it() {
        using var store = new RenderObjectStore();
        using var gpu = new GpuVisibilityGroup(device);
        using var cpu = new VisibilityGroup();

        for (var i = 0; i < 100; i++) {
            Add(store, i % 2 == 0 ? 10f : -10f);
        }

        var views = new[] { Camera(RenderStageMask.Of(0)) };

        cpu.Cull(store, views);
        gpu.Cull(store, views);

        Assert.Equal(cpu.Words(0).ToArray(), gpu.Words(0).ToArray());

        gpu.Hide(0, new(0));
        cpu.Hide(0, new(0));

        Assert.Equal(cpu.VisibleCount(0), gpu.VisibleCount(0));
        Assert.False(gpu.IsVisible(0, new(0)));
    }

    /// <summary>
    ///     One dispatch covers every word of every view, and the copy out of it is behind a barrier.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shape of the pass, asserted where it can be: <c>x</c> covers a view's words and
    ///         <c>y</c> is the view, so four shadow cascades and a camera are one submission rather
    ///         than five.
    ///     </para>
    ///     <para>
    ///         The barrier between the dispatch and the copy is the part that is not a nicety.
    ///         Copying a buffer a dispatch has not finished writing is undefined on every API, and
    ///         the symptom is a bitset that is part of this frame and part of the last one — which
    ///         looks like objects flickering rather than like a synchronisation bug.
    ///     </para>
    /// </remarks>
    [Fact]
    public void It_dispatches_one_invocation_per_word_per_view() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        for (var i = 0; i < 100; i++) {
            Add(store, 10f);
        }

        visibility.Cull(store, [Camera(RenderStageMask.Of(0)), Camera(RenderStageMask.Of(0), 50f)]);

        Assert.True(visibility.CulledOnDevice);
        Assert.True(visibility.Bits.IsValid);

        var stream = device.Recorder!.Commands.ToList();
        var dispatch = stream.FindIndex(command => command.Kind == RecordedCommandKind.Dispatch);
        var copy = stream.FindIndex(command => command.Kind == RecordedCommandKind.CopyBuffer);
        var barrier = stream.FindIndex(dispatch + 1, command => command.Kind == RecordedCommandKind.Barrier);

        Assert.True(dispatch >= 0, "nothing dispatched");
        Assert.Equal(GpuCulling.Groups(GpuCulling.WordsFor(store.Count)), stream[dispatch].A);
        Assert.Equal(2, stream[dispatch].B);
        Assert.Equal(1, stream[dispatch].C);

        Assert.True(barrier > dispatch, "the answer was copied with no barrier after the dispatch");
        Assert.True(copy > barrier, "the copy happened before the barrier that orders it");

        // Two views of a hundred objects: four device words each.
        Assert.Equal(GpuCulling.BufferSize(2, GpuCulling.WordsFor(store.Count)), stream[copy].E);
    }

    /// <summary>
    ///     A frame with no objects or no views does not dispatch, and reports nothing visible.
    /// </summary>
    /// <remarks>
    ///     A dispatch of zero groups is a validation error on Vulkan rather than a no-op, and there
    ///     is nothing to read back either way.
    /// </remarks>
    [Fact]
    public void An_empty_frame_dispatches_nothing() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.False(visibility.CulledOnDevice);

        Add(store, 10f);
        visibility.Cull(store, []);

        Assert.False(visibility.CulledOnDevice);
        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
        Assert.False(visibility.IsVisible(0, new(0)));
    }

    /// <summary>
    ///     A scene that grows is dispatched for whole, and the buffers grow with it.
    /// </summary>
    /// <remarks>
    ///     The bitset is sized by the object count, so a frame that added objects after the buffer
    ///     was made would otherwise dispatch for a word count the buffer cannot hold — which is a
    ///     write past the end on a device that does not check.
    /// </remarks>
    [Fact]
    public void A_growing_scene_is_dispatched_for_whole() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        Add(store, 10f);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        for (var i = 0; i < 500; i++) {
            Add(store, 10f);
        }

        device.Recorder!.Clear();
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        var dispatch = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.Dispatch));
        Assert.Equal(GpuCulling.Groups(GpuCulling.WordsFor(store.Count)), dispatch.A);

        var copy = Assert.Single(device.Recorder.OfKind(RecordedCommandKind.CopyBuffer));
        Assert.Equal(GpuCulling.BufferSize(1, GpuCulling.WordsFor(store.Count)), copy.E);
    }

    /// <summary>
    ///     The render system takes either group, and the frame does not notice.
    /// </summary>
    /// <remarks>
    ///     The whole point of the interface. Sorting walks <see cref="IVisibilityGroup.Words" />,
    ///     which is filled by a job on one path and by a readback on the other, and the work lists
    ///     that come out are the same either way.
    /// </remarks>
    [Fact]
    public void The_render_system_takes_either_group() {
        using var system = new RenderSystem();
        var stage = system.AddStage(new("Opaque"));

        system.Visibility = new GpuVisibilityGroup(device);

        var id = system.Objects.Add(
            new() { Bounds = new(new(0f, 0f, 10f), 1f), Stages = stage.Mask, IsAlive = true, FeatureIndex = -1 }
        );

        system.Objects.Add(
            new() { Bounds = new(new(0f, 0f, -10f), 1f), Stages = stage.Mask, IsAlive = true, FeatureIndex = -1 }
        );

        var view = Camera(stage.Mask);
        system.SetViews([view]);
        system.Draw();

        var node = Assert.Single(system.Nodes(view, stage));
        Assert.Equal(id, node.Object);
    }

    /// <summary>Null is not a visibility group, and finding that out at the setter is the point.</summary>
    [Fact]
    public void The_system_refuses_a_null_group() {
        using var system = new RenderSystem();
        Assert.Throws<ArgumentNullException>(() => system.Visibility = null!);
    }

    /// <summary>Disposing gives back everything it made, and twice is harmless.</summary>
    [Fact]
    public void Disposing_returns_what_it_created() {
        using var store = new RenderObjectStore();
        var visibility = Configured();

        Add(store, 10f);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        visibility.Dispose();
        visibility.Dispose();

        Assert.False(visibility.Bits.IsValid);
    }

    // --- Occlusion ----------------------------------------------------------

    /// <summary>
    ///     An object behind a wall is culled, and the same object with nothing in front of it is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole occlusion test in two calls. Depth is reversed — 1 is the near plane — so a
    ///         pyramid reading 1 everywhere is a wall pressed against the camera, and one reading 0
    ///         is a frame in which nothing was drawn at all.
    ///     </para>
    ///     <para>
    ///         Getting the sense of that comparison backwards is the mistake worth catching, because
    ///         it does not look like a mistake: the scene renders, and what is missing is whatever
    ///         happened to be in front.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_object_behind_a_wall_is_occluded_and_one_in_the_open_is_not() {
        var view = Occluding(Camera(RenderStageMask.Of(0)));
        var candidate = GpuCulling.Pack(Object(10f));

        Assert.True(GpuCulling.IsOccluded(candidate, view, new(64, 64), (_, _, _) => 1f));
        Assert.False(GpuCulling.IsOccluded(candidate, view, new(64, 64), (_, _, _) => 0f));
    }

    /// <summary>A view with no pyramid to test against is never occluded by one.</summary>
    /// <remarks>
    ///     The first frame of every view, and every frame after the view list changed shape. Without
    ///     the flag the test would run against a matrix of zeroes, which projects the scene to a
    ///     point and hides all of it.
    /// </remarks>
    [Fact]
    public void A_view_with_no_pyramid_is_never_occluded() {
        var view = GpuCulling.Pack(Camera(RenderStageMask.Of(0)), 1, 1);

        Assert.False(GpuCulling.IsOccluded(GpuCulling.Pack(Object(10f)), view, new(64, 64), (_, _, _) => 1f));
    }

    /// <summary>
    ///     An object reaching behind the near plane is kept, whatever is in front of it.
    /// </summary>
    /// <remarks>
    ///     A corner with a negative <c>w</c> does not project to a point off the edge of the screen —
    ///     it projects to the other side of it, so the rectangle around the object turns inside out
    ///     and lands wherever the arithmetic takes it. The only safe answer is to stop testing.
    /// </remarks>
    [Fact]
    public void An_object_reaching_behind_the_camera_is_kept() {
        var view = Occluding(Camera(RenderStageMask.Of(0)));
        var candidate = GpuCulling.Pack(Object(1f, radius: 5f));

        Assert.False(GpuCulling.ScreenBounds(candidate, view, out _, out _));
        Assert.False(GpuCulling.IsOccluded(candidate, view, new(64, 64), (_, _, _) => 1f));
    }

    /// <summary>
    ///     The level chosen is the one where four taps cover the rectangle.
    /// </summary>
    /// <remarks>
    ///     One level finer leaves part of the rectangle untested — and the untested part is exactly
    ///     where the object was visible. One coarser costs a little culling and nothing else, which
    ///     is why the rounding goes up.
    /// </remarks>
    [Theory]
    [InlineData(1f, 0)]
    [InlineData(2f, 1)]
    [InlineData(3f, 2)]
    [InlineData(4f, 2)]
    [InlineData(5f, 3)]
    [InlineData(4096f, 7)]
    public void The_level_is_the_one_that_covers_the_rectangle(float extent, int expected) =>
        Assert.Equal(expected, GpuCulling.LevelFor(new(extent, extent), 8));

    /// <summary>A level's size is the mip chain's, floored and never zero.</summary>
    [Fact]
    public void A_levels_size_is_the_mip_chains() {
        Assert.Equal(new Int2(960, 540), GpuCulling.LevelSize(new(960, 540), 0));
        Assert.Equal(new Int2(480, 270), GpuCulling.LevelSize(new(960, 540), 1));
        Assert.Equal(new Int2(1, 1), GpuCulling.LevelSize(new(960, 540), 20));
    }

    /// <summary>
    ///     The two shaders still reduce and compare the way the host says they do.
    /// </summary>
    /// <remarks>
    ///     Both halves of one argument, and both invisible to every other test here. The reduction
    ///     has to take the minimum over a 3×3 block — the minimum because depth is reversed, and 3×3
    ///     because a floored mip chain leaves a trailing row that a 2×2 block never reads; and the
    ///     comparison has to be the object's nearest point against the tile's furthest surface. Each
    ///     of those, written the other way round, culls things that were visible.
    /// </remarks>
    [Fact]
    public void The_shaders_reduce_and_compare_the_way_the_host_says() {
        var reduce = Source("Pipeline", "HiZReduce.rvn");

        Assert.Contains("furthest = min(furthest, source.Load(int3(x, y, 0)).x)", reduce, StringComparison.Ordinal);
        Assert.Contains("for (dx in 0 .. 2)", reduce, StringComparison.Ordinal);
        Assert.Contains("for (dy in 0 .. 2)", reduce, StringComparison.Ordinal);

        Assert.Contains(
            "return maximum.z < min(min(a, b), min(c, d))",
            Source("Pipeline", "Culling.rvn"),
            StringComparison.Ordinal
        );
    }

    // --- The pyramid --------------------------------------------------------

    /// <summary>
    ///     The chain starts at half the depth buffer and runs to a single texel.
    /// </summary>
    /// <remarks>
    ///     Half, because reducing straight into level 0 removes a full-resolution level from both
    ///     the memory and the chain — and an occlusion test that needed the depth of one pixel would
    ///     be a test of something too small to be worth culling.
    /// </remarks>
    [Fact]
    public void The_pyramid_halves_the_depth_buffer_and_runs_to_one_texel() {
        using var pyramid = Pyramid();

        Assert.True(Build(pyramid, new(1920, 1080)));

        Assert.Equal(new Int2(960, 540), pyramid.Size);
        Assert.Equal(10, pyramid.Levels);
        Assert.True(pyramid.IsBuilt);
        Assert.True(pyramid.View.IsValid);
    }

    /// <summary>
    ///     One dispatch per level, each covering its own level and separated by a barrier.
    /// </summary>
    /// <remarks>
    ///     A level cannot be read until the whole of the level above it is written, and a workgroup
    ///     can only wait for itself — so the chain is a dispatch per level with a barrier between,
    ///     rather than one dispatch with a loop in it. Without the barriers a level reads whatever
    ///     part of its parent happened to be finished, which is a pyramid that is subtly too shallow
    ///     and culls what it should not.
    /// </remarks>
    [Fact]
    public void The_pyramid_dispatches_once_per_level_behind_barriers() {
        using var pyramid = Pyramid();

        Build(pyramid, new(64, 64));

        // 32×32 down to 1×1.
        Assert.Equal(6, pyramid.Levels);

        var stream = device.Recorder!.Commands.ToList();
        var dispatches = stream.Where(command => command.Kind == RecordedCommandKind.Dispatch).ToList();

        Assert.Equal(pyramid.Levels, dispatches.Count);

        // Level 0 is 32×32 texels at a workgroup of 8.
        Assert.Equal(4, dispatches[0].A);
        Assert.Equal(4, dispatches[0].B);
        Assert.Equal(1, dispatches[^1].A);

        foreach (var dispatch in dispatches) {
            var before = stream.FindLastIndex(dispatch.Sequence, command => command.Kind == RecordedCommandKind.Barrier);
            var after = stream.FindIndex(dispatch.Sequence, command => command.Kind == RecordedCommandKind.Barrier);

            Assert.True(before >= 0, "a level was written with no barrier before its dispatch");
            Assert.True(after > dispatch.Sequence, "a level was left in the state its dispatch wrote it in");
        }
    }

    /// <summary>Without an effect there is nothing to dispatch, and nothing is recorded.</summary>
    [Fact]
    public void A_pyramid_with_no_shader_builds_nothing() {
        using var pyramid = new HiZPyramid(device);
        using var list = device.BeginCommandList(QueueKind.Compute);

        var (_, view) = Depth(new(64, 64));

        Assert.False(pyramid.Build(list, view, new(64, 64)));
        Assert.False(pyramid.IsBuilt);
        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     A resize rebuilds the chain, and until it is rebuilt there is nothing to test against.
    /// </summary>
    /// <remarks>
    ///     A pyramid at the wrong size is not a stale answer — it is a different frame's screen, and
    ///     an occlusion test against one would cull by geometry from a window that no longer exists.
    /// </remarks>
    [Fact]
    public void A_resize_starts_the_pyramid_again() {
        using var pyramid = Pyramid();

        Build(pyramid, new(64, 64));
        Assert.True(pyramid.IsBuilt);

        Build(pyramid, new(128, 128));

        Assert.Equal(new Int2(64, 64), pyramid.Size);
        Assert.Equal(7, pyramid.Levels);
    }

    // --- The two together ---------------------------------------------------

    /// <summary>
    ///     The first frame is frustum-only and the second tests occlusion.
    /// </summary>
    /// <remarks>
    ///     Not a warm-up quirk but the invariant: a view is only tested against a pyramid when this
    ///     group saw that view's matrix in the frame the pyramid was built in. Before that there is
    ///     no matrix to project the rectangle with, and projecting it with this frame's would compare
    ///     a position from now against pixels from then.
    /// </remarks>
    [Fact]
    public void Occlusion_starts_on_the_frame_after_the_first() {
        using var store = new RenderObjectStore();
        using var pyramid = Pyramid();
        using var visibility = Configured();

        visibility.Occluders = pyramid;
        Build(pyramid, new(64, 64));
        Add(store, 10f);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.True(visibility.CulledOnDevice);
        Assert.False(visibility.OcclusionTested);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.True(visibility.OcclusionTested);
    }

    /// <summary>
    ///     A frame that changed the shape of its view list is frustum-only again.
    /// </summary>
    /// <remarks>
    ///     Views are addressed by index and renumbered every frame, so a frame that added one has
    ///     moved every view after it. Keeping the matrices would test a cascade's rectangle against
    ///     the camera's depth.
    /// </remarks>
    [Fact]
    public void Adding_a_view_turns_occlusion_off_for_that_frame() {
        using var store = new RenderObjectStore();
        using var pyramid = Pyramid();
        using var visibility = Configured();

        visibility.Occluders = pyramid;
        Build(pyramid, new(64, 64));
        Add(store, 10f);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);
        Assert.True(visibility.OcclusionTested);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0)), Camera(RenderStageMask.Of(0), 50f)]);
        Assert.False(visibility.OcclusionTested);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0)), Camera(RenderStageMask.Of(0), 50f)]);
        Assert.True(visibility.OcclusionTested);
    }

    /// <summary>
    ///     A frame with no pyramid still culls on the device.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The regression this exists for was silent in the worst way. The occlusion permutation
    ///         removes the sampling and the branch; it does <em>not</em> remove the declaration, so
    ///         both variants ask for the texture — which a real compiler says and a hand-written
    ///         provider had been letting the host imagine otherwise. A group that refused to bind a
    ///         texture it did not have would then fall back to the CPU on every frustum-only frame,
    ///         for ever, and the only symptom is that GPU culling never happens.
    ///     </para>
    ///     <para>
    ///         So the binding is filled with a texture that exists and every view's flags say it is
    ///         not usable, which is what stops it being read.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_frame_with_no_pyramid_still_culls_on_the_device() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        Assert.NotNull(AlwaysCompiles.Culling().BindingOf("occluders"));
        Assert.Null(visibility.Occluders);

        var ahead = Add(store, 10f);
        var behind = Add(store, -10f);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.True(visibility.CulledOnDevice, "a frame with no pyramid fell back to the CPU");
        Assert.False(visibility.OcclusionTested);
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));

        // And the readback still landed: on the Null backend that is all zeroes, which is what the
        // dispatch is presumed to have written.
        Assert.False(visibility.IsVisible(0, ahead));
        Assert.False(visibility.IsVisible(0, behind));
    }

    /// <summary>
    ///     The node reduces the depth the frame just wrote, after the pass that wrote it.
    /// </summary>
    /// <remarks>
    ///     The declaration is the whole node: depth is a graph resource, so a dispatch that sampled
    ///     it without saying so would be ordered against nothing and read it in the layout the last
    ///     pass left it in. Saying <c>Reads</c> is what puts the pyramid's dispatches after the pass
    ///     and the barrier between them, and it is why doc 06 says GPU occlusion culling needs the
    ///     culler to be part of the compositor rather than something the render system does alone.
    /// </remarks>
    [Fact]
    public void The_node_builds_the_pyramid_after_the_pass_that_filled_depth() {
        using var system = new RenderSystem();
        using var pyramid = Pyramid();
        var graph = new RenderGraph(device);

        var prepass = new RenderPassRenderer { Name = "Prepass", DepthTarget = "SceneDepth" };
        var reduce = new HiZRenderer { Name = "HiZ", Depth = "SceneDepth", Pyramid = pyramid };

        var description = new TextureDescription(
            PixelFormat.Depth32Float,
            64,
            64,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: "SceneDepth"
        );

        var texture = device.CreateTexture(description);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(64, 64),
            Game = new SceneRendererSequence { Children = { prepass, reduce } }
        };

        compositor.Imports["SceneDepth"] = new(texture, device.CreateTextureView(texture), description);

        using (var list = device.BeginCommandList()) {
            graph.Reset();
            compositor.Build(graph, effects, device);
            graph.Execute(list);
            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        graph.DisposePool();

        Assert.True(pyramid.IsBuilt);

        var stream = device.Recorder!.Commands.ToList();
        var pass = stream.FindIndex(command => command.Kind == RecordedCommandKind.EndRenderPass);
        var dispatch = stream.FindIndex(command => command.Kind == RecordedCommandKind.Dispatch);

        Assert.True(pass >= 0, "the depth pass did not run");
        Assert.True(dispatch > pass, "the pyramid was reduced before the depth it reduces was written");
    }

    /// <summary>A node with no pyramid declares nothing, and a frame without it still runs.</summary>
    [Fact]
    public void A_node_with_no_pyramid_adds_no_pass() {
        using var system = new RenderSystem();
        var graph = new RenderGraph(device);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(64, 64),
            Game = new HiZRenderer { Name = "HiZ", Depth = "SceneDepth" }
        };

        using (var list = device.BeginCommandList()) {
            graph.Reset();
            compositor.Build(graph, effects, device);
            graph.Execute(list);
            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        graph.DisposePool();

        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    // --- Drawing from the device's answer ------------------------------------

    /// <summary>
    ///     Dispatching without templates is refused, and says what to do about it.
    /// </summary>
    /// <remarks>
    ///     The pass edits one field of records the host supplies, so a dispatch with no templates
    ///     would leave every draw as whatever the buffer held before — last frame's arguments, or
    ///     nothing at all. Refusing is not defensiveness: an argument buffer that is silently a frame
    ///     stale draws a scene that is almost right, which is the hardest kind of wrong to notice.
    /// </remarks>
    [Fact]
    public void Updating_without_filling_is_refused() {
        using var arguments = new GpuDrawArguments(device) { Effects = effects, Pipelines = pipelines };
        using var list = device.BeginCommandList(QueueKind.Compute);

        var bits = device.CreateBuffer(new(64, BufferUsage.Storage, MemoryAccess.DeviceLocal, "Bits"));
        var thrown = Assert.Throws<InvalidOperationException>(() => arguments.Update(list, bits, 1, 8));

        Assert.Contains("Fill", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every binding of every culling shader is in one set, which is what one dispatch can bind.
    /// </summary>
    /// <remarks>
    ///     This used to be a runtime check inside each class, throwing when the reflection disagreed.
    ///     It does not need to be: the indices are generated from the reflection checked in beside
    ///     the shader, so the question is answered when the engine compiles rather than when a frame
    ///     runs — and here is where an answer that stopped being true should fail.
    /// </remarks>
    [Fact]
    public void Each_culling_shaders_bindings_share_a_set() {
        Assert.Equal(CullingKeys.ObjectsSet, CullingKeys.ViewsSet);
        Assert.Equal(CullingKeys.ObjectsSet, CullingKeys.VisibilitySet);
        Assert.Equal(CullingKeys.ObjectsSet, CullingKeys.OccludersSet);

        Assert.Equal(HiZReduceKeys.SourceSet, HiZReduceKeys.TargetSet);

        Assert.Equal(DrawArgumentsKeys.TemplatesSet, DrawArgumentsKeys.VisibilitySet);
        Assert.Equal(DrawArgumentsKeys.TemplatesSet, DrawArgumentsKeys.CommandsSet);
    }

    /// <summary>
    ///     The names the host used to look up by hand are the ones the shaders declare.
    /// </summary>
    /// <remarks>
    ///     The generated keys exist because a binding index is declaration order within a set, so
    ///     adding a buffer above another renumbers it. A literal in C# survives that; a generated
    ///     constant does not, which is the whole point — this test is what says the two shaders and
    ///     the three classes are still describing the same interface.
    /// </remarks>
    [Fact]
    public void The_shader_names_are_the_generated_ones() {
        Assert.Equal(GpuCulling.ShaderName, CullingKeys.ShaderName);
        Assert.Equal(GpuCulling.ReduceShaderName, HiZReduceKeys.ShaderName);
        Assert.Equal(GpuCulling.ArgumentsShaderName, DrawArgumentsKeys.ShaderName);
        Assert.Equal(GpuCulling.OcclusionKey, CullingKeys.Occlusion.Name);
    }

    /// <summary>A draw's arguments are twenty bytes, which is what the API's stride is.</summary>
    /// <remarks>
    ///     Not ours to choose: the GPU's command processor reads these bytes directly, in this order.
    ///     A field added or moved is a draw of somebody else's numbers.
    /// </remarks>
    [Fact]
    public void A_draw_command_is_the_layout_the_api_reads() {
        Assert.Equal(GpuDrawArguments.Stride, Marshal.SizeOf<DrawCommand>());
        Assert.Equal(20, GpuDrawArguments.Stride);
    }

    /// <summary>
    ///     Without the readback, the host answers with what could be seen and the device narrows it.
    /// </summary>
    /// <remarks>
    ///     The one place this group stops being interchangeable with the CPU one, and the reason it
    ///     is opt-in. The work list has to be a superset — an object the host left out cannot be
    ///     drawn by a GPU that decided it was visible — so the conservative answer keeps the two
    ///     rejections a work list needs (dead, and in no stage this view draws) and drops the
    ///     frustum, which is the work being moved.
    /// </remarks>
    [Fact]
    public void Without_the_readback_the_host_answer_is_conservative() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;

        var ahead = Add(store, 10f);
        var behind = Add(store, -10f);
        var elsewhere = store.Add(
            new() { Bounds = new(new(0f, 0f, 10f), 1f), Stages = RenderStageMask.Of(3), IsAlive = true }
        );

        var dead = Add(store, 10f);
        store.Remove(dead);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.True(visibility.CulledOnDevice);

        // Behind the camera is still in the list: that is the frustum's answer, and the frustum is
        // what the device now owns.
        Assert.True(visibility.IsVisible(0, ahead));
        Assert.True(visibility.IsVisible(0, behind));

        // The two the work list itself needs.
        Assert.False(visibility.IsVisible(0, elsewhere));
        Assert.False(visibility.IsVisible(0, dead));
    }

    /// <summary>
    ///     Without the readback nothing is submitted or waited on during the cull.
    /// </summary>
    /// <remarks>
    ///     The whole point of the setting. The dispatch is left for <see cref="GpuCullingRenderer" />
    ///     to record into the frame's list, because a barrier between two things in one queue is the
    ///     only ordering this RHI can express — it has neither fences nor semaphores.
    /// </remarks>
    [Fact]
    public void Without_the_readback_the_cull_records_nothing_of_its_own() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;
        Add(store, 10f);

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.CopyBuffer));

        using var list = device.BeginCommandList(QueueKind.Compute);

        Assert.True(visibility.Record(list));

        // And only once: a second call has nothing pending, which is what stops a node that runs
        // twice dispatching twice.
        Assert.False(visibility.Record(list));

        list.Finish();
        device.ComputeQueue.Submit([list]);

        Assert.Equal(1, device.Recorder.CountOf(RecordedCommandKind.Dispatch));
        Assert.Equal(0, device.Recorder.CountOf(RecordedCommandKind.CopyBuffer));
    }

    /// <summary>
    ///     Two-phase culling is a second dispatch, and only after the first one has been recorded.
    /// </summary>
    /// <remarks>
    ///     The late pass reads the word the main pass wrote. Without one it would subtract from
    ///     whatever the previous frame left in the buffer, which is a difference computed against the
    ///     wrong frame — the kind of wrong that looks almost right and only shows as objects missing
    ///     where the camera moved last frame.
    /// </remarks>
    [Fact]
    public void The_late_pass_records_a_second_dispatch_after_the_first() {
        using var store = new RenderObjectStore();
        using var pyramid = Pyramid();
        using var visibility = Configured();

        visibility.ReadBack = false;
        visibility.TwoPhase = true;
        visibility.Occluders = pyramid;

        Add(store, 10f);
        Build(pyramid, new(64, 64));

        // Twice, because occlusion needs a matrix from the frame the pyramid was built in — and it is
        // the occlusion-tested frame that has a late pass worth running.
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.True(visibility.OcclusionTested);

        // Counted from here, because building the pyramid is a dispatch per level and this is about
        // the two the culler adds.
        var before = device.Recorder!.CountOf(RecordedCommandKind.Dispatch);

        using (var list = device.BeginCommandList(QueueKind.Compute)) {
            Assert.True(visibility.Record(list));
            Assert.True(visibility.RecordLate(list));

            // And only once each: a node that ran twice would dispatch twice over the same answer,
            // and the second would subtract the first's difference from itself.
            Assert.False(visibility.RecordLate(list));

            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        Assert.True(visibility.LatePhaseRan);
        Assert.Equal(before + 2, device.Recorder.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     A late pass with no main pass records nothing.
    /// </summary>
    /// <remarks>
    ///     The one ordering error a document can make that nothing else would catch: the late
    ///     dispatch's input is the main dispatch's output, and a list holding only the second is one
    ///     that subtracts this frame's visibility from last frame's answer.
    /// </remarks>
    [Fact]
    public void A_late_pass_without_a_main_pass_records_nothing() {
        using var store = new RenderObjectStore();
        using var pyramid = Pyramid();
        using var visibility = Configured();

        visibility.ReadBack = false;
        visibility.TwoPhase = true;
        visibility.Occluders = pyramid;

        Add(store, 10f);
        Build(pyramid, new(64, 64));

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        var before = device.Recorder!.CountOf(RecordedCommandKind.Dispatch);

        using (var list = device.BeginCommandList(QueueKind.Compute)) {
            Assert.False(visibility.RecordLate(list));

            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        Assert.False(visibility.LatePhaseRan);
        Assert.Equal(before, device.Recorder.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     With the readback on there is no late phase, because there is nothing for it to straddle.
    /// </summary>
    /// <remarks>
    ///     The readback path submits and waits inside <see cref="GpuVisibilityGroup.Cull" />, before
    ///     any of the frame's draws exist. Two phases have to sit either side of a set of draws, so
    ///     asking for both is asking for something the ordering cannot hold — and the answer is the
    ///     frame culled exactly as a one-phase frame, not an exception out of the middle of it.
    /// </remarks>
    [Fact]
    public void The_readback_path_has_no_late_phase() {
        using var store = new RenderObjectStore();
        using var pyramid = Pyramid();
        using var visibility = Configured();

        visibility.TwoPhase = true;
        visibility.Occluders = pyramid;

        Add(store, 10f);
        Build(pyramid, new(64, 64));

        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        using var list = device.BeginCommandList(QueueKind.Compute);

        Assert.False(visibility.RecordLate(list));
        Assert.False(visibility.LatePhaseRan);

        list.Finish();
        device.ComputeQueue.Submit([list]);
    }

    /// <summary>
    ///     A two-phase frame with no pyramid still runs the late pass, and gets an empty difference.
    /// </summary>
    /// <remarks>
    ///     Not waste. Skipping it would leave the main pass's bits in the argument buffer for the late
    ///     draws to find, and every visible object would be drawn twice — so the dispatch that writes
    ///     zeroes is what makes the frame before any depth exists look like every other frame.
    /// </remarks>
    [Fact]
    public void A_two_phase_frame_with_no_pyramid_still_records_the_late_pass() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;
        visibility.TwoPhase = true;

        Add(store, 10f);
        visibility.Cull(store, [Camera(RenderStageMask.Of(0))]);

        Assert.True(visibility.CulledOnDevice);
        Assert.False(visibility.OcclusionTested);

        using var list = device.BeginCommandList(QueueKind.Compute);

        Assert.True(visibility.Record(list));
        Assert.True(visibility.RecordLate(list));

        list.Finish();
        device.ComputeQueue.Submit([list]);

        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     The node culls and writes the arguments in one list, in that order.
    /// </summary>
    /// <remarks>
    ///     Two dispatches with a barrier between them, because the second reads what the first wrote.
    ///     Both in the frame's list rather than in a submission of the group's own, which is the
    ///     ordering that needs no fence.
    /// </remarks>
    [Fact]
    public void The_node_culls_and_writes_arguments_in_one_list() {
        using var system = new RenderSystem();
        using var visibility = Configured();
        using var arguments = new GpuDrawArguments(device) { Effects = effects, Pipelines = pipelines };

        visibility.ReadBack = false;
        system.Visibility = visibility;

        var stage = system.AddStage(new("Opaque"));
        var meshes = new MeshRenderFeature { Arguments = arguments };
        system.AddFeature(meshes);

        system.Objects.Add(
            new() { Bounds = new(new(0f, 0f, 10f), 1f), Stages = stage.Mask, IsAlive = true, FeatureIndex = meshes.Index }
        );

        // A pass that draws the stage, because a view only exists if a node declares one — and with
        // no views there is nothing to cull for.
        var colour = new RenderPassRenderer { Name = "Forward" };
        colour.ColourTargets.Add("SceneColour");
        colour.Children.Add(new SingleStageRenderer { View = Camera(stage.Mask), Stage = stage });

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(64, 64),
            Game = new SceneRendererSequence {
                Children = {
                    new GpuCullingRenderer { Name = "Culling", Visibility = visibility, Arguments = arguments },
                    colour
                }
            }
        };

        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            64,
            64,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: "SceneColour"
        );

        var target = device.CreateTexture(description);
        compositor.Imports["SceneColour"] = new(target, device.CreateTextureView(target), description);

        var graph = new RenderGraph(device);

        using (var list = device.BeginCommandList()) {
            graph.Reset();
            compositor.Build(graph, effects, device);
            graph.Execute(list);
            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        graph.DisposePool();

        Assert.True(arguments.IsFilled);
        Assert.True(arguments.Commands.IsValid);

        var stream = device.Recorder!.Commands.ToList();
        var dispatches = stream.Where(command => command.Kind == RecordedCommandKind.Dispatch).ToList();

        Assert.Equal(2, dispatches.Count);
        Assert.True(
            stream.FindIndex(dispatches[0].Sequence, command => command.Kind == RecordedCommandKind.Barrier)
            < dispatches[1].Sequence,
            "the arguments were written with no barrier after the cull they read"
        );
    }

    /// <summary>
    ///     The templates are the numbers the direct draw would have used.
    /// </summary>
    /// <remarks>
    ///     Including the instancing batch, which is why they are filled after <c>Prepare</c> rather
    ///     than inside it. An object that is not drawable, or not indexed, keeps the cleared record it
    ///     arrived as — a draw of no indices, which draws nothing rather than something arbitrary.
    /// </remarks>
    [Fact]
    public void The_templates_are_what_the_direct_draw_would_have_been() {
        using var system = new RenderSystem();

        var stage = system.AddStage(new("Opaque"));
        var meshes = new MeshRenderFeature();
        system.AddFeature(meshes);

        var indexed = system.Objects.Add(
            new() { Bounds = new(Vector3.Zero, 1f), Stages = stage.Mask, IsAlive = true, FeatureIndex = meshes.Index }
        );

        var direct = system.Objects.Add(
            new() { Bounds = new(Vector3.Zero, 1f), Stages = stage.Mask, IsAlive = true, FeatureIndex = meshes.Index }
        );

        var vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex });
        var indices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Index });
        var draws = system.Objects.Data.Data(meshes.Draws);

        draws[indexed.Index] = new() {
            VertexBuffer = vertices,
            IndexBuffer = indices,
            Count = 36,
            FirstIndex = 6,
            VertexOffset = 2,
            InstanceCount = 4
        };

        // Drawable, but with no index buffer — and there is no non-indexed indirect draw to make.
        draws[direct.Index] = new() { VertexBuffer = vertices, Count = 3, InstanceCount = 1 };

        var commands = new DrawCommand[system.Objects.Count];
        meshes.FillArguments(system, commands);

        Assert.Equal(36u, commands[indexed.Index].IndexCount);
        Assert.Equal(4u, commands[indexed.Index].InstanceCount);
        Assert.Equal(6u, commands[indexed.Index].FirstIndex);
        Assert.Equal(2u, commands[indexed.Index].VertexOffset);
        Assert.Equal(0u, commands[indexed.Index].FirstInstance);

        Assert.Equal(default, commands[direct.Index]);
    }

    /// <summary>An object's arguments sit at its own index, per view.</summary>
    /// <remarks>
    ///     No compaction, because compaction needs an atomic counter and Raven has none — so a slot's
    ///     record is at that slot, and a culled object is a record with no instances rather than a
    ///     record that is not there.
    /// </remarks>
    [Fact]
    public void An_objects_arguments_sit_at_its_own_slot() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();
        using var arguments = new GpuDrawArguments(device) { Effects = effects, Pipelines = pipelines };

        visibility.ReadBack = false;

        for (var i = 0; i < 10; i++) {
            Add(store, 10f);
        }

        visibility.Cull(store, [Camera(RenderStageMask.Of(0)), Camera(RenderStageMask.Of(0), 50f)]);

        using var list = device.BeginCommandList(QueueKind.Compute);

        visibility.Record(list);
        arguments.Fill(store.Count);

        Assert.True(arguments.Update(list, visibility.Bits, 2, store.Count));

        list.Finish();
        device.ComputeQueue.Submit([list]);

        Assert.Equal(0, arguments.OffsetOf(0, new(0)));
        Assert.Equal(GpuDrawArguments.Stride * 3, arguments.OffsetOf(0, new(3)));

        // The second view's records start after the first view's, one per object slot.
        Assert.Equal(GpuDrawArguments.Stride * 10, arguments.OffsetOf(1, new(0)));
    }

    // --- The incremental scene -----------------------------------------------

    /// <summary>
    ///     A frame that changes nothing uploads nothing.
    /// </summary>
    /// <remarks>
    ///     The first frame writes the whole scene, because a freshly created buffer holds nothing.
    ///     Every frame after it writes the difference, and when there is no difference there is
    ///     nothing to write — which is the claim doc <c>virtualized-geometry.md</c> § Phase 0 makes
    ///     and the reason the object records stopped being an <c>UploadBuffer</c>.
    /// </remarks>
    [Fact]
    public void A_frame_that_changes_nothing_uploads_nothing() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;

        for (var i = 0; i < 256; i++) {
            Add(store, 10f);
        }

        var views = new[] { Camera(RenderStageMask.Of(0)) };

        visibility.Cull(store, views);
        Assert.True(visibility.CulledOnDevice);

        // The scene arriving for the first time, into every region of the ring.
        Assert.Equal(256 * 32, visibility.ObjectBytesUploaded);

        for (var frame = 0; frame < device.FramesInFlight; frame++) {
            visibility.Cull(store, views);
        }

        // Each region has now had the whole scene once, and nothing has moved since.
        visibility.Cull(store, views);
        Assert.Equal(0, visibility.ObjectBytesUploaded);
        Assert.Equal(0, visibility.ObjectUploadRegions);
    }

    /// <summary>
    ///     A hundred thousand objects, one of which moves, and one object's worth of bytes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The exit criterion doc <c>virtualized-geometry.md</c> § Phase 0 states, asserted the
    ///         way it says to assert it — by counting the upload rather than by timing the frame.
    ///         Deleting the comparison in <see cref="PersistentUploadBuffer{T}.Set" /> fails this
    ///         rather than making the frame slower, which is the point of writing it this way.
    ///     </para>
    ///     <para>
    ///         The change is uploaded once per frame in flight and then stops, because each region
    ///         of the ring is a different set of bytes and each of them is missing the change until
    ///         its own turn comes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void One_object_moving_in_a_hundred_thousand_uploads_one_object() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;

        const int Count = 100_000;

        for (var i = 0; i < Count; i++) {
            Add(store, 10f);
        }

        var views = new[] { Camera(RenderStageMask.Of(0)) };

        // Settle every region of the ring, so what follows is the steady state rather than the
        // first frame's unavoidable upload of everything.
        for (var frame = 0; frame <= device.FramesInFlight; frame++) {
            visibility.Cull(store, views);
        }

        Assert.Equal(0, visibility.ObjectBytesUploaded);

        store[new(Count / 2)].Bounds = new(new(0f, 0f, 11f), 1f);

        for (var frame = 0; frame < device.FramesInFlight; frame++) {
            visibility.Cull(store, views);

            Assert.Equal(32, visibility.ObjectBytesUploaded);
            Assert.Equal(1, visibility.ObjectUploadRegions);
        }

        // And then it is settled again: the change reached every region, and nothing re-sends it.
        visibility.Cull(store, views);
        Assert.Equal(0, visibility.ObjectBytesUploaded);
    }

    /// <summary>
    ///     Every kind of change to the record is seen, not only the one the test above makes.
    /// </summary>
    /// <remarks>
    ///     The record is bounds, a stage mask and a liveness flag, and a comparison that watched only
    ///     the bounds would leave a removed object drawn and an object that changed stages drawn into
    ///     the wrong list — both of which are pictures, not crashes. Comparing the packed bytes is
    ///     what makes this one property rather than three.
    /// </remarks>
    [Theory]
    [InlineData("bounds")]
    [InlineData("stages")]
    [InlineData("removed")]
    public void Every_field_of_the_record_is_watched(string change) {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;

        var id = Add(store, 10f);

        for (var i = 0; i < 8; i++) {
            Add(store, 10f);
        }

        var views = new[] { Camera(RenderStageMask.Of(0)) };

        for (var frame = 0; frame <= device.FramesInFlight; frame++) {
            visibility.Cull(store, views);
        }

        Assert.Equal(0, visibility.ObjectBytesUploaded);

        switch (change) {
            case "bounds":
                store[id].Bounds = new(new(0f, 0f, 11f), 2f);
                break;

            case "stages":
                store[id].Stages = RenderStageMask.Of(1);
                break;

            default:
                store.Remove(id);
                break;
        }

        visibility.Cull(store, views);
        Assert.Equal(32, visibility.ObjectBytesUploaded);
    }

    /// <summary>
    ///     Scattered changes are coalesced, because a call into the driver costs more than the bytes.
    /// </summary>
    /// <remarks>
    ///     Two records with a handful of clean ones between them are one write, not two — the trade
    ///     <see cref="PersistentUploadBuffer{T}.MergeGap" /> makes. A test that only watched the byte
    ///     count would read the extra bytes as a regression and the saved call as nothing, so both
    ///     are asserted.
    /// </remarks>
    [Fact]
    public void Nearby_changes_become_one_write() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;

        for (var i = 0; i < 512; i++) {
            Add(store, 10f);
        }

        var views = new[] { Camera(RenderStageMask.Of(0)) };

        for (var frame = 0; frame <= device.FramesInFlight; frame++) {
            visibility.Cull(store, views);
        }

        store[new(100)].Bounds = new(new(0f, 0f, 11f), 1f);
        store[new(104)].Bounds = new(new(0f, 0f, 11f), 1f);

        visibility.Cull(store, views);

        // One write covering both and the three clean records between them.
        Assert.Equal(1, visibility.ObjectUploadRegions);
        Assert.Equal(5 * 32, visibility.ObjectBytesUploaded);

        // And far apart is two, which is what says the merge has a limit rather than no bound.
        for (var frame = 0; frame < device.FramesInFlight; frame++) {
            visibility.Cull(store, views);
        }

        store[new(10)].Bounds = new(new(0f, 0f, 12f), 1f);
        store[new(400)].Bounds = new(new(0f, 0f, 12f), 1f);

        visibility.Cull(store, views);

        Assert.Equal(2, visibility.ObjectUploadRegions);
        Assert.Equal(2 * 32, visibility.ObjectBytesUploaded);
    }

    /// <summary>
    ///     A scene that grows uploads the objects it grew by, and nothing else.
    /// </summary>
    /// <remarks>
    ///     The case a comparison alone would get wrong. A record that has just come into range has
    ///     never been written to the device, so its bytes are undefined there — and if its value
    ///     happens to equal the host's zeroed copy, a comparison would find no difference and skip
    ///     it. What makes it right is that a region starts entirely dirty and a bit is cleared only
    ///     by actually writing it, so "never written" and "differs" are the same state.
    /// </remarks>
    [Fact]
    public void A_scene_that_grows_uploads_what_it_grew_by() {
        using var store = new RenderObjectStore();
        using var visibility = Configured();

        visibility.ReadBack = false;

        for (var i = 0; i < 64; i++) {
            Add(store, 10f);
        }

        var views = new[] { Camera(RenderStageMask.Of(0)) };

        for (var frame = 0; frame <= device.FramesInFlight; frame++) {
            visibility.Cull(store, views);
        }

        Assert.Equal(0, visibility.ObjectBytesUploaded);

        // Within the buffer's existing capacity, so this is the comparison's problem rather than a
        // reallocation's — the reallocation case re-uploads everything and is not the interesting one.
        Add(store, 10f);
        visibility.Cull(store, views);

        Assert.Equal(32, visibility.ObjectBytesUploaded);
    }

    // --- The fixture --------------------------------------------------------

    readonly EffectSystem effects = new();
    readonly ComputePipelineCache pipelines;

    /// <summary>A group with everything it needs to run on the device.</summary>
    GpuVisibilityGroup Configured() => new(device) { Effects = effects, Pipelines = pipelines };

    /// <summary>A pyramid with everything it needs to build.</summary>
    HiZPyramid Pyramid() => new(device) { Effects = effects, Pipelines = pipelines };

    /// <summary>Builds a pyramid from a depth texture of a size, in a list of its own.</summary>
    /// <remarks>
    ///     Submitted rather than merely finished, because a Null command list hands its recording to
    ///     the recorder when it is submitted — which is also the only point at which a real backend
    ///     would have done anything.
    /// </remarks>
    bool Build(HiZPyramid pyramid, Int2 size) {
        using var list = device.BeginCommandList(QueueKind.Compute);

        var (_, view) = Depth(size);
        var built = pyramid.Build(list, view, size);

        list.Finish();
        device.ComputeQueue.Submit([list]);

        return built;
    }

    (TextureHandle Texture, TextureViewHandle View) Depth(Int2 size) {
        var texture = device.CreateTexture(
            new(
                PixelFormat.Depth32Float,
                size.X,
                size.Y,
                TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
                Name: "SceneDepth"
            )
        );

        return (texture, device.CreateTextureView(texture));
    }

    static RenderObjectId Add(RenderObjectStore store, float z) => store.Add(Object(z));

    static RenderObject Object(float z, float radius = 1f) =>
        new() { Bounds = new(new(0f, 0f, z), radius), Stages = RenderStageMask.Of(0), IsAlive = true };

    /// <summary>A view packed as though its pyramid were built with the matrix it has now.</summary>
    static CullView Occluding(RenderView view, int levels = 8) =>
        GpuCulling.Pack(view, 1, 1, view.ViewProjection, levels);

    /// <summary>A camera at the origin looking down +Z, as the CPU path's own tests build one.</summary>
    static RenderView Camera(RenderStageMask stages, float maximumDistance = 0f) {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") {
            Stages = stages,
            Position = Vector3.Zero,
            ViewProjection = view * projection,
            MaximumDistance = maximumDistance
        };
    }

    /// <summary>A shipped shader's source, found by walking up rather than by counting directories.</summary>
    static string Source(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    ///     A provider that answers with the variants the two passes would have been compiled to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The bindings are what the two classes read to build their sets, so they are the part
    ///         that has to be truthful: the names are the shaders' own, they are all in one set, and
    ///         the culler's texture appears only in the variant that declares it — which is the
    ///         permutation this fixture exists to make visible.
    ///     </para>
    ///     <para>
    ///         <strong>A layout per shader, derived from that shader's own bindings.</strong> One
    ///         layout shared by all three was wrong in a way only a device would report: binding 1 is
    ///         the reduction's storage image, the argument writer's visibility buffer and the culler's
    ///         object buffer, so two of the three wrote a kind the set was not declared for. Building
    ///         it from <see cref="Effect.Bindings" /> keeps the two from drifting apart again — there
    ///         is only one list to get wrong.
    ///     </para>
    /// </remarks>
    sealed class AlwaysCompiles(NullDevice device) : IEffectProvider {
        // The binding order is the real one: the texture first, and the three shaders disagree about
        // what every index after 0 holds.
        //
        // All eleven, including the seven the object cull never reads. That is not padding — a
        // permutation folds away the *code* that would have read them and leaves the declarations, so
        // `Culling.rvn` compiled through RavenEffectCompiler reports all eleven whichever variant was
        // asked for, and a set bound with any of them unwritten is undefined on a device. This fixture
        // listed four for as long as the cluster traversal existed, which is exactly the failure its own
        // comment below warns about: a fixture that invents a leaner variant lets the host get it wrong
        // and says nothing.
        static readonly ImmutableArray<EffectBinding> CullingBindings = [
            new("occluders", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.SampledTexture),
            new("objects", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.StorageBuffer),
            new("views", DescriptorSetSlot.PerMaterial, 2, DescriptorKind.StorageBuffer),
            new("visibility", DescriptorSetSlot.PerMaterial, 3, DescriptorKind.StorageBuffer),
            new("clusterRecords", DescriptorSetSlot.PerMaterial, 4, DescriptorKind.StorageBuffer),
            new("instances", DescriptorSetSlot.PerMaterial, 5, DescriptorKind.StorageBuffer),
            new("children", DescriptorSetSlot.PerMaterial, 6, DescriptorKind.StorageBuffer),
            new("roots", DescriptorSetSlot.PerMaterial, 7, DescriptorKind.StorageBuffer),
            new("visible", DescriptorSetSlot.PerMaterial, 8, DescriptorKind.StorageBuffer),
            new("requests", DescriptorSetSlot.PerMaterial, 9, DescriptorKind.StorageBuffer),
            new("residency", DescriptorSetSlot.PerMaterial, 10, DescriptorKind.StorageBuffer)
        ];

        // Six, and the last three in both variants. A binding is a declared field, so it survives
        // its last reader folding away — the padded variant declares the batch layout it never reads
        // exactly as the compacted one does, and a set short of it is a validation error rather than
        // an unused slot.
        static readonly ImmutableArray<EffectBinding> ArgumentBindings = [
            new("templates", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.StorageBuffer),
            new("visibility", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.StorageBuffer),
            new("commands", DescriptorSetSlot.PerMaterial, 2, DescriptorKind.StorageBuffer),
            new("batches", DescriptorSetSlot.PerMaterial, 3, DescriptorKind.StorageBuffer),
            new("bases", DescriptorSetSlot.PerMaterial, 4, DescriptorKind.StorageBuffer),
            new("counts", DescriptorSetSlot.PerMaterial, 5, DescriptorKind.StorageBuffer)
        ];

        static readonly ImmutableArray<EffectBinding> ReduceBindings = [
            new("source", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.SampledTexture),
            new("target", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.StorageTexture)
        ];

        readonly Dictionary<string, ImmutableArray<DescriptorSetLayoutHandle>> layouts = [];

        public Effect? TryGet(EffectKey key) =>
            key.ShaderName switch {
                GpuCulling.ReduceShaderName => Reduce(key, Layouts(GpuCulling.ReduceShaderName, ReduceBindings)),
                GpuCulling.ArgumentsShaderName =>
                    Arguments(key, Layouts(GpuCulling.ArgumentsShaderName, ArgumentBindings)),
                _ => Culling(key, Layouts(GpuCulling.ShaderName, CullingBindings))
            };

        /// <summary>
        ///     The culling variant, in the shape the real compiler produces.
        /// </summary>
        /// <remarks>
        ///     One shape for both variants, and the texture is in it either way — which is what
        ///     <c>Culling.rvn</c> compiled through <c>RavenEffectCompiler</c> actually reports, and
        ///     what a fixture that invented a leaner variant for the frustum-only case let the host
        ///     get wrong. The binding order is the real one too: the texture first.
        /// </remarks>
        public static Effect Culling(EffectKey key = default, ImmutableArray<DescriptorSetLayoutHandle> layouts = default) =>
            new() {
                Key = key.ShaderName is null ? EffectKey.Of(GpuCulling.ShaderName) : key,
                SetLayouts = layouts.IsDefault ? [] : layouts,
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
                Bindings = CullingBindings
            };

        static Effect Arguments(EffectKey key, ImmutableArray<DescriptorSetLayoutHandle> layouts) =>
            new() {
                Key = key,
                SetLayouts = layouts,
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
                Bindings = ArgumentBindings
            };

        static Effect Reduce(EffectKey key, ImmutableArray<DescriptorSetLayoutHandle> layouts) =>
            new() {
                Key = key,
                SetLayouts = layouts,
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
                Bindings = ReduceBindings
            };

        /// <summary>That shader's set, made once and shaped by the bindings it declares.</summary>
        ImmutableArray<DescriptorSetLayoutHandle> Layouts(string shader, ImmutableArray<EffectBinding> bindings) {
            if (layouts.TryGetValue(shader, out var made)) {
                return made;
            }

            var declared = bindings
                .Select(binding => new DescriptorBinding(binding.Binding, binding.Kind, ShaderStage.Compute))
                .ToArray();

            var all = new DescriptorSetLayoutHandle[(int)DescriptorSetSlot.PerMaterial + 1];

            all[(int)DescriptorSetSlot.PerMaterial] = device.CreateDescriptorSetLayout(
                new(DescriptorSetSlot.PerMaterial, declared, shader)
            );

            made = [.. all];
            layouts[shader] = made;

            return made;
        }
    }
}
