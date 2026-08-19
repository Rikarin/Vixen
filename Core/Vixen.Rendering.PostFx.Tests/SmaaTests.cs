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
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     SMAA: the coverage table it looks up, and the three passes that look it up.
/// </summary>
/// <remarks>
///     <para>
///         The table is where the arithmetic is, and it is the half a picture cannot check: a table
///         off by a block reads a neighbouring pattern's coverage, which is a blend in the right place
///         of the wrong amount — a frame that is subtly soft rather than one that is obviously broken.
///         So the values pinned below are ones a pencil produces, not ones this generator produced.
///     </para>
///     <para>
///         The chain is where the wiring is. Three passes over one shader, each binding every texture
///         the shader declares whether its mode reads it or not, because a descriptor set is written
///         whole or not at all — and a set one binding short refuses every draw while the draw count
///         still reports fine.
///     </para>
/// </remarks>
public class SmaaTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly EffectPipelineDescriber describer;
    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = [];

    public SmaaTests() {
        allocator = new(device);
        samplers = new(device);
        describer = new(device);

        Declare(
            SmaaKeys.ShaderName,
            new(SmaaKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(SmaaKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SmaaKeys.EdgesBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SmaaKeys.WeightsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SmaaKeys.AreaBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SmaaKeys.LinearSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(SmaaKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            TonemapKeys.ShaderName,
            new(TonemapKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(TonemapKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.LutBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(TonemapKeys.LutSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        effects.AddProvider(new AlwaysCompiles(layouts));
    }

    void Declare(string shader, params DescriptorBinding[] bindings) =>
        layouts[shader] = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerMaterial, bindings, shader));

    /// <inheritdoc />
    public void Dispose() {
        samplers.Dispose();
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The coverage table -------------------------------------------------

    /// <summary>The table is the size the shader is told it is.</summary>
    [Fact]
    public void The_table_is_eighty_square_and_two_channels() {
        var texels = SmaaAreaTexture.Generate();

        Assert.Equal(80, SmaaAreaTexture.Side);
        Assert.Equal(SmaaAreaTexture.Side * SmaaAreaTexture.Side * 2, texels.Length);
        Assert.Equal(SmaaAreaTexture.ByteCount, texels.Length);
    }

    /// <summary>
    ///     The three coverages a pencil can produce, and the two shapes they come from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         With a run one pixel long, an L whose crossing goes down at the left is the line from
    ///         the lower pixel's centre up to the boundary at the run's middle — a triangle half a
    ///         pixel wide and half a pixel tall, so an eighth of a pixel of coverage below the line
    ///         and none above. 0.125 × 255 is 31.875, which rounds to 32.
    ///     </para>
    ///     <para>
    ///         A Z — up at the left, down at the right — is one line straight across, so it is that
    ///         triangle twice, once on each side of the boundary: 32 in both channels.
    ///     </para>
    ///     <para>
    ///         A U is the L twice on the same side, and it is the one shape that is smoothed: a
    ///         one-pixel U is a bump rather than a staircase, so each half is pulled toward the
    ///         rounder √(2a)/2 — a quarter — and eased back toward the exact eighth by d/32, which is
    ///         1/32 here. 0.25 + (0.125 − 0.25)/32 is 0.24609375 a half, 0.4921875 together, and
    ///         0.4921875 × 255 is 125.51, which rounds to 126.
    ///     </para>
    /// </remarks>
    [Theory]
    // An L with the crossing below at the left end: an eighth below the line, nothing above.
    [InlineData(1, 0, 0, 32, 0)]
    // The same L mirrored top to bottom: the eighth moves to the other channel and nothing else does.
    [InlineData(4, 0, 0, 0, 32)]
    // A Z: the same triangle on each side of the boundary.
    [InlineData(6, 0, 0, 32, 32)]
    // A U opening down, and the smoothing that only a U gets.
    [InlineData(3, 0, 0, 126, 0)]
    // The same U opening up.
    [InlineData(12, 0, 0, 0, 126)]
    // A flat run has no silhouette to reconstruct and no coverage to give.
    [InlineData(0, 0, 0, 0, 0)]
    // A T junction has no single line through it, whichever way it points.
    [InlineData(5, 0, 0, 0, 0)]
    [InlineData(15, 0, 0, 0, 0)]
    // Texel (0, 2) is a run reaching four pixels to the right and none to the left, so the line from
    // the left crossing to the middle of a five-pixel run drops from a half to three tenths across
    // this pixel: a trapezoid of mean 0.4, and 0.4 × 255 is exactly 102.
    [InlineData(1, 0, 2, 102, 0)]
    // And the mirror of it — four pixels to the *left* of an L that turns down at the left — is
    // entirely on the far side of the run's middle, which pattern 1 does not filter at all.
    [InlineData(1, 2, 0, 0, 0)]
    public void The_table_holds_the_coverage_the_geometry_gives(int pattern, int x, int y, int red, int green) {
        var texels = SmaaAreaTexture.Generate();
        var (blockX, blockY) = SmaaAreaTexture.Block(pattern);

        var at = 2 * ((((blockY * SmaaAreaTexture.MaxDistance) + y) * SmaaAreaTexture.Side)
            + (blockX * SmaaAreaTexture.MaxDistance) + x);

        Assert.Equal(red, texels[at]);
        Assert.Equal(green, texels[at + 1]);
    }

    /// <summary>
    ///     Every pattern with two crossings at one end contributes nothing, everywhere in its block.
    /// </summary>
    /// <remarks>
    ///     Seven of the sixteen, and the reason is geometric rather than conservative: a T or a cross
    ///     junction has no single line through it, and a morphological filter that blends one anyway
    ///     is one that smears a corner.
    /// </remarks>
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(0)]
    public void A_junction_has_no_line_and_therefore_no_coverage(int pattern) {
        var texels = SmaaAreaTexture.Generate();
        var (blockX, blockY) = SmaaAreaTexture.Block(pattern);

        for (var y = 0; y < SmaaAreaTexture.MaxDistance; y++) {
            for (var x = 0; x < SmaaAreaTexture.MaxDistance; x++) {
                var at = 2 * ((((blockY * SmaaAreaTexture.MaxDistance) + y) * SmaaAreaTexture.Side)
                    + (blockX * SmaaAreaTexture.MaxDistance) + x);

                Assert.Equal(0, texels[at]);
                Assert.Equal(0, texels[at + 1]);
            }
        }
    }

    /// <summary>
    ///     No coverage exceeds half a pixel, which is what makes the blend a bilinear tap.
    /// </summary>
    /// <remarks>
    ///     What the shader does with the number is offset a sample by it, in texels. An offset above
    ///     a half reaches past the neighbour it is blending with and starts averaging a pixel that
    ///     has nothing to do with this edge — so this bound is the filter's correctness, not its
    ///     range.
    /// </remarks>
    [Fact]
    public void No_coverage_reaches_past_the_neighbour() {
        var texels = SmaaAreaTexture.Generate();

        foreach (var texel in texels) {
            Assert.True(texel <= 128, $"a coverage of {texel / 255f:0.###} would sample past the neighbour");
        }
    }

    /// <summary>
    ///     Mirroring a pattern left to right mirrors its block and swaps its two distances.
    /// </summary>
    /// <remarks>
    ///     The structural check the pinned values cannot make: it holds over the whole table rather
    ///     than at four texels, and it is exactly the symmetry a transposed <c>left</c> and
    ///     <c>right</c>, or a block index built the wrong way round, would break.
    /// </remarks>
    [Theory]
    // Crossing down at the left, mirrored, is crossing down at the right.
    [InlineData(1, 2)]
    // And up at the left is up at the right.
    [InlineData(4, 8)]
    // A U is its own mirror.
    [InlineData(3, 3)]
    [InlineData(12, 12)]
    public void A_mirrored_pattern_is_the_same_table_transposed(int pattern, int mirrored) {
        for (var y = 0; y < SmaaAreaTexture.MaxDistance; y++) {
            for (var x = 0; x < SmaaAreaTexture.MaxDistance; x++) {
                var here = SmaaAreaTexture.Coverage(pattern, x * x, y * y);
                var there = SmaaAreaTexture.Coverage(mirrored, y * y, x * x);

                Assert.Equal(here.Below, there.Below, 9);
                Assert.Equal(here.Above, there.Above, 9);
            }
        }
    }

    /// <summary>Mirroring a pattern top to bottom swaps the two channels and nothing else.</summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 12)]
    [InlineData(6, 9)]
    public void A_flipped_pattern_swaps_the_two_sides(int pattern, int flipped) {
        for (var y = 0; y < SmaaAreaTexture.MaxDistance; y++) {
            for (var x = 0; x < SmaaAreaTexture.MaxDistance; x++) {
                var here = SmaaAreaTexture.Coverage(pattern, x * x, y * y);
                var there = SmaaAreaTexture.Coverage(flipped, x * x, y * y);

                Assert.Equal(here.Below, there.Above, 9);
                Assert.Equal(here.Above, there.Below, 9);
            }
        }
    }

    /// <summary>
    ///     The sixteen patterns land in sixteen distinct blocks, and never in the middle slot.
    /// </summary>
    /// <remarks>
    ///     The grid is five wide because the weight pass indexes it as <c>3·below + above</c>, which
    ///     takes 0, 1, 3 and 4. Slot 2 exists so that arithmetic is a multiply and an add rather than
    ///     a table of its own, and it is never addressed — which is the fact that would break first
    ///     if the index were ever built some other way.
    /// </remarks>
    [Fact]
    public void The_sixteen_patterns_occupy_sixteen_blocks_and_skip_the_middle() {
        var seen = new HashSet<(int, int)>();

        for (var pattern = 0; pattern < 16; pattern++) {
            var block = SmaaAreaTexture.Block(pattern);

            Assert.True(seen.Add(block), $"pattern {pattern} shares block {block} with another");
            Assert.NotEqual(2, block.X);
            Assert.NotEqual(2, block.Y);
            Assert.InRange(block.X, 0, SmaaAreaTexture.Patterns - 1);
            Assert.InRange(block.Y, 0, SmaaAreaTexture.Patterns - 1);
        }

        Assert.Equal(16, seen.Count);
    }

    /// <summary>The generator is a function of nothing, so two runs agree byte for byte.</summary>
    [Fact]
    public void The_table_is_the_same_table_every_time() =>
        Assert.Equal(SmaaAreaTexture.Generate(), SmaaAreaTexture.Generate());

    // --- The chain ----------------------------------------------------------

    /// <summary>Three passes, in the order each one's input is written.</summary>
    [Fact]
    public void The_chain_is_edges_then_weights_then_blend() {
        using var h = Build();
        Frame(h);

        Assert.Equal(3, h.Smaa.Passes.Count);

        Assert.Equal(0, h.Smaa.Passes[0].Parameters.Get(SmaaKeys.Mode));
        Assert.Equal(1, h.Smaa.Passes[1].Parameters.Get(SmaaKeys.Mode));
        Assert.Equal(2, h.Smaa.Passes[2].Parameters.Get(SmaaKeys.Mode));

        Assert.Equal(h.Smaa.EdgesName, Assert.Single(h.Smaa.Passes[0].ColourTargets));
        Assert.Equal(h.Smaa.WeightsName, Assert.Single(h.Smaa.Passes[1].ColourTargets));
        Assert.Equal("Antialiased", Assert.Single(h.Smaa.Passes[2].ColourTargets));

        // The reads are what order them, and one without the other is a race the picture shows
        // intermittently: the weight pass after the edges, the blend after the weights.
        Assert.Contains(h.Smaa.EdgesName, h.Smaa.Passes[1].Reads);
        Assert.Contains(h.Smaa.WeightsName, h.Smaa.Passes[2].Reads);
    }

    /// <summary>
    ///     Every pass fills every binding the shader declares, whatever its mode reads.
    /// </summary>
    /// <remarks>
    ///     ⚠ The one that matters most here. A permutation folds code, not declarations — the
    ///     reflection beside <c>Smaa.rvn</c> lists all four textures and both samplers from any
    ///     variant — and <c>EffectSetWriter</c> fills a set whole or not at all. A pass that left the
    ///     table slot empty because its mode does not read one would refuse every draw, while its
    ///     draw count still reported fine.
    /// </remarks>
    [Fact]
    public void Every_pass_fills_every_binding() {
        using var h = Build();
        Frame(h);

        uint[] required = [
            SmaaKeys.SourceBinding,
            SmaaKeys.EdgesBinding,
            SmaaKeys.WeightsBinding,
            SmaaKeys.AreaBinding,
            SmaaKeys.LinearSamplerBinding,
            SmaaKeys.PointSamplerBinding
        ];

        foreach (var pass in h.Smaa.Passes) {
            foreach (var binding in required) {
                var matches = pass.Descriptors.Bindings.Where(b => b.Binding == binding).ToList();

                Assert.True(
                    matches.Count == 1,
                    $"{pass.Name} wrote binding {binding} {matches.Count} times, and a set is written "
                    + "whole or not at all"
                );

                var written = matches[0];

                Assert.True(
                    written.Kind == DescriptorKind.Sampler
                        ? written.Sampler.IsValid
                        : !string.IsNullOrEmpty(written.Resource),
                    $"{pass.Name} left binding {binding} empty, so its set is short and it draws nothing"
                );
            }
        }
    }

    /// <summary>
    ///     A pass never binds the resource it writes, which would be a cycle rather than a stand-in.
    /// </summary>
    [Fact]
    public void No_pass_reads_its_own_target() {
        using var h = Build();
        Frame(h);

        foreach (var pass in h.Smaa.Passes) {
            var target = Assert.Single(pass.ColourTargets);
            Assert.DoesNotContain(target, pass.Reads);
        }
    }

    /// <summary>
    ///     The coverage table is copied once, however many frames run.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>And <see cref="SmaaRenderer.Uploaded" /> is set by the copy, not by the build that
    ///     declared it.</b> A node that marked itself uploaded while declaring would, in a frame that
    ///     is built and never executed, import a table full of whatever that memory held — which
    ///     reads as a filter blending in the wrong direction rather than as a missing copy.
    /// </remarks>
    [Fact]
    public void The_table_is_uploaded_once_and_only_once() {
        using var h = Build();

        Assert.False(h.Smaa.Uploaded);

        Frame(h);
        Assert.True(h.Smaa.Uploaded);
        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));

        Frame(h);
        Frame(h);

        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));
    }

    /// <summary>The shader is told the frame's texel size and the table's, and they are not the same.</summary>
    /// <remarks>
    ///     Two texel sizes because two grids: the neighbourhood walk steps in the frame's, and the
    ///     coverage lookup steps in the table's. One number used for both is a lookup that reads
    ///     whatever is 80/320 of the way into the pattern it wanted.
    /// </remarks>
    [Fact]
    public void Both_texel_sizes_reach_the_shader() {
        using var h = Build(new(320, 180));
        Frame(h);

        foreach (var pass in h.Smaa.Passes) {
            var frame = pass.Parameters.Get(SmaaKeys.TexelSize);
            Assert.Equal(1f / 320f, frame.X, 6);
            Assert.Equal(1f / 180f, frame.Y, 6);

            var table = pass.Parameters.Get(SmaaKeys.AreaTexelSize);
            Assert.Equal(1f / SmaaAreaTexture.Side, table.X, 6);
            Assert.Equal(1f / SmaaAreaTexture.Side, table.Y, 6);

            Assert.Equal(SmaaAreaTexture.MaxDistance, pass.Parameters.Get(SmaaKeys.AreaMaxDistance), 3);
        }
    }

    /// <summary>The thresholds reach the shader rather than staying on the node.</summary>
    [Fact]
    public void The_thresholds_reach_the_shader() {
        using var h = Build();
        h.Smaa.EdgeThreshold = 0.05f;
        h.Smaa.ContrastAdaptation = 3f;
        h.Smaa.LumaFloor = 0.5f;

        Frame(h);

        Assert.Equal(0.05f, h.Smaa.Passes[0].Parameters.Get(SmaaKeys.EdgeThreshold), 5);
        Assert.Equal(3f, h.Smaa.Passes[0].Parameters.Get(SmaaKeys.ContrastAdaptation), 5);
        Assert.Equal(0.5f, h.Smaa.Passes[0].Parameters.Get(SmaaKeys.LumaFloor), 5);
    }

    /// <summary>A node with nothing to compile with says so rather than drawing a wrong frame.</summary>
    [Fact]
    public void A_node_with_no_modules_declines_and_says_why() {
        // No consumer: a node that declines declares no output, so a pass reading one by name would
        // throw before the reason could be asked for — which is the same answer every effect here
        // gives and not what this is about.
        using var h = Build(consumer: false);
        h.Smaa.Modules = null;

        Frame(h);

        Assert.Contains("no Modules", h.Smaa.Degraded ?? "");
    }

    // --- The document -------------------------------------------------------

    /// <summary>
    ///     The two new antialiasing modes are appended, and their numbers are what documents say.
    /// </summary>
    /// <remarks>
    ///     ⚠ A <c>[DataContract]</c> enum's values are a saved document's vocabulary. Inserting
    ///     <c>Smaa</c> beside <c>Fxaa</c>, which is where it belongs by meaning, would renumber
    ///     <c>Taa</c> and <c>TaaFxaa</c> — so every scene already authored as one of those would load
    ///     as the other. This test is the enum's ordering, pinned.
    /// </remarks>
    [Fact]
    public void The_new_modes_are_appended_rather_than_inserted() {
        Assert.Equal(0, (int)AntialiasingMode.Off);
        Assert.Equal(1, (int)AntialiasingMode.Fxaa);
        Assert.Equal(2, (int)AntialiasingMode.Taa);
        Assert.Equal(3, (int)AntialiasingMode.TaaFxaa);
        Assert.Equal(4, (int)AntialiasingMode.Smaa);
        Assert.Equal(5, (int)AntialiasingMode.TaaSmaa);
    }

    /// <summary>A document naming <c>!Smaa</c> gets the node, with its numbers.</summary>
    [Fact]
    public void The_factory_builds_the_node_from_the_asset() {
        using var system = new RenderSystem();

        var builder = new CompositorBuilder(system) {
            Device = device,
            Modules = describer,
            Samplers = samplers,
            Descriptors = allocator
        };

        var declared = new SmaaAsset {
            Name = "Edges",
            Source = "SceneGraded",
            Output = "Display",
            Format = PixelFormat.Rgba8UNormSrgb,
            EdgeThreshold = 0.05f,
            ContrastAdaptation = 3f,
            LumaFloor = 0.25f
        };

        using var node = Assert.IsType<SmaaRenderer>(new PostEffectFactory().Create(declared, builder));

        Assert.Equal("Edges", node.Name);
        Assert.Equal("SceneGraded", node.Source);
        Assert.Equal("Display", node.Output);
        Assert.Equal(PixelFormat.Rgba8UNormSrgb, node.Format);
        Assert.Equal(0.05f, node.EdgeThreshold, 5);
        Assert.Equal(3f, node.ContrastAdaptation, 5);
        Assert.Equal(0.25f, node.LumaFloor, 5);

        // The seams the node cannot make for itself, and the four a node that drew nothing would be
        // missing exactly one of.
        Assert.NotNull(node.Modules);
        Assert.NotNull(node.Samplers);
        Assert.NotNull(node.Descriptors);
        Assert.NotNull(node.Device);
    }

    /// <summary>
    ///     <c>antialiasing: Smaa</c> emits the node after the tonemap, and emits no FXAA.
    /// </summary>
    [Fact]
    public void The_standard_frame_emits_smaa_after_the_tonemap() {
        var nodes = Expanded(AntialiasingMode.Smaa);
        var names = nodes.Select(node => node.GetType().Name).ToList();

        Assert.Contains(nameof(SmaaAsset), names);
        Assert.DoesNotContain(nameof(FxaaAsset), names);

        var tonemap = names.IndexOf(nameof(TonemapAsset));
        var smaa = names.IndexOf(nameof(SmaaAsset));

        Assert.True(tonemap >= 0, "the expansion emitted no tonemap");
        Assert.True(smaa > tonemap, "SMAA was emitted before the tonemap, where the contrast is unbounded");

        // It reads what the tonemap wrote and hands the chain along, which is the whole of what a
        // node spliced into a post chain has to get right.
        var node = Assert.IsType<SmaaAsset>(nodes[smaa]);

        Assert.Equal("SceneGraded", node.Source);
        Assert.Equal("SceneAntialiased", node.Output);

        // And the tier's lens is still after it, still writing the frame's own output — the count
        // that decides who writes it did not lose track when this node joined the queue.
        Assert.Equal("Display", Assert.IsType<VignetteAsset>(nodes[^1]).Output);
        Assert.Equal("SceneAntialiased", Assert.IsType<VignetteAsset>(nodes[^1]).Source);
    }

    /// <summary>
    ///     <c>TaaSmaa</c> keeps the velocity pass that <c>Smaa</c> alone does not pay for.
    /// </summary>
    /// <remarks>
    ///     The knob is not decoration: TAA is what a motion-vector texture exists for, and a frame
    ///     that emitted the resolve without the pass that fills it would reproject every pixel onto
    ///     itself — TAA with its first defence removed.
    /// </remarks>
    [Fact]
    public void Taa_and_smaa_together_keep_the_temporal_resolve() {
        var names = Expanded(AntialiasingMode.TaaSmaa).Select(node => node.GetType().Name).ToList();

        Assert.Contains(nameof(SmaaAsset), names);
        Assert.Contains(nameof(TemporalAntialiasingAsset), names);
    }

    /// <summary>And the modes that do not ask for it do not get it.</summary>
    [Theory]
    [InlineData(AntialiasingMode.Off)]
    [InlineData(AntialiasingMode.Fxaa)]
    [InlineData(AntialiasingMode.Taa)]
    [InlineData(AntialiasingMode.TaaFxaa)]
    public void The_other_modes_emit_no_smaa(AntialiasingMode mode) =>
        Assert.DoesNotContain(Expanded(mode), node => node is SmaaAsset);

    /// <summary>What the standard frame expands to, flattened to the nodes it emitted.</summary>
    static IReadOnlyList<ISceneRendererAsset> Expanded(AntialiasingMode mode) {
        var document = StandardFrame.Expand(
            new() { Game = new StandardFrameAsset { Antialiasing = mode, Output = "Display" } }
        );

        return [.. Assert.IsType<SequenceAsset>(document.Game).Children];
    }

    // --- The fixture --------------------------------------------------------

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required SmaaRenderer Smaa { get; init; }
        public FullScreenRenderer? Consumer { get; init; }

        public void Dispose() {
            Smaa.Dispose();
            Consumer?.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }

    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle layout) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ],
            SetLayouts = [default, default, layout, default],
            ConstantBufferSize = 48
        };

    sealed class AlwaysCompiles(Dictionary<string, DescriptorSetLayoutHandle> layouts) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            Compiled(key, layouts.TryGetValue(key.ShaderName, out var layout) ? layout : default);
    }

    ImportedTexture Colour(string name, Int2 size) {
        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            size.X,
            size.Y,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    /// <summary>
    ///     The chain, plus something that reads its result.
    /// </summary>
    /// <remarks>
    ///     The consumer is not scaffolding. The chain's output is a transient the graph owns, so a
    ///     pass nothing reads is a pass the graph culls — and a fixture without one asserts about a
    ///     chain that was never scheduled.
    /// </remarks>
    Harness Build(Int2 size = default, bool consumer = true) {
        size = size == default ? new(320, 180) : size;

        var system = new RenderSystem();

        var smaa = new SmaaRenderer {
            Name = "Smaa",
            Source = "SceneGraded",
            Output = "Antialiased",
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers,
            Device = device
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = size };

        compositor.Imports["SceneGraded"] = Colour("SceneGraded", size);
        compositor.Imports["Display"] = Colour("Display", size);

        if (!consumer) {
            compositor.Game = smaa;
            return new() { System = system, Compositor = compositor, Graph = new(device), Smaa = smaa };
        }

        var present = new FullScreenRenderer {
            Name = "Present",
            ShaderName = TonemapKeys.ShaderName,
            ConstantBinding = TonemapKeys.ConstantBufferBinding,
            Modules = describer,
            Device = device,
            Samplers = samplers
        };

        present.ColourTargets.Add("Display");
        present.Reads.Add("Antialiased");
        present.Descriptors.Allocator = allocator;

        present.Descriptors.Bindings.Add(
            new() {
                Binding = TonemapKeys.SourceBinding,
                Kind = DescriptorKind.SampledTexture,
                Resource = "Antialiased"
            }
        );

        compositor.Game = new SceneRendererSequence { Children = { smaa, present } };

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Smaa = smaa,
            Consumer = present
        };
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        allocator.BeginFrame();
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }
}
