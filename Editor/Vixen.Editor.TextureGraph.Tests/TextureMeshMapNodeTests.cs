// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.TextureGraph.Nodes;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.8's Mesh Map Input: an image bound by what it measures.</summary>
/// <remarks>
///     <para>
///         <b>What is under test is a <em>reference</em>, because that is the whole of what crosses.</b>
///         A compilation may not open an asset database, so the node's entire output is one external
///         image, one <c>Bitmap</c> dispatch and a string saying which measurement it wants. The half
///         that resolves that string against a project's baked maps lives in
///         <c>Vixen.Editor.Assets.MeshMaps.MeshMapBinding</c> and is proved against a real bake in
///         <c>MeshMapGeneratorBindingTests</c> — including the round trip that keeps this assembly's
///         vocabulary and <c>MeshMapNaming</c>'s from drifting apart.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day the node stops emitting anything.</b> Every case
///         below reads a plan's op list or its external list, and a node that reported and returned
///         produces neither — so the assertions are on things that do not exist rather than on
///         values that are wrong, which is a failure and not a pass.
///         <see cref="A_usage_nothing_bakes_is_refused_rather_than_defaulted" /> is the one case that
///         asserts the absence, and it asserts the diagnostic first.
///     </para>
/// </remarks>
public class TextureMeshMapNodeTests {
    const int Side = 128;

    static TextureGraphCompiler Compiler() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return new(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7 };
    }

    /// <summary>A Mesh Map asking for one usage, wired into an output.</summary>
    static NodeGraphModel Graph(string usage, out GraphNode node) {
        NodeGraphModel graph = new();

        node = graph.Add("Source/Mesh Map");

        var output = graph.Add("Output/Output");

        node.SetText("Map", usage);
        graph.Connect(new(node.Id, "Out"), new(output.Id, "Input"));

        return graph;
    }

    /// <summary>
    ///     Every usage the node offers compiles to one external naming it and one resample.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the node's own list rather than written out here.</b> A theory with nine
    ///     <c>InlineData</c> rows passes in silence on the day a tenth map is baked and not listed,
    ///     which is precisely the drift the binding exists to avoid — and it is the shape
    ///     <c>MeshMapLibraryTests</c> already rejected on the writing side.
    /// </remarks>
    [Fact]
    public void Every_usage_the_node_offers_compiles_to_a_reference_naming_it() {
        Assert.NotEmpty(TextureMeshMaps.Known);

        foreach (var usage in TextureMeshMaps.Known) {
            var compiler = Compiler();
            var compilation = compiler.Compile(Graph(usage, out var node));

            Assert.Empty(compilation.Diagnostics);

            var external = Assert.Single(compiler.Externals);

            // The reference, and nothing about a file: the graph does not know which mesh it is
            // about to be baked for and must not.
            Assert.Equal("meshmap:" + usage, external.Asset);
            Assert.Equal(node.Id, external.Node);
            Assert.Empty(external.Texels);
            Assert.True(compilation.Value.Images[external.Image].External);

            // And exactly one dispatch, which is the resample into the graph's resolution — a mesh
            // map is baked at the bake's resolution and a plan's images are at the graph's.
            var op = Assert.Single(compilation.Value.Ops);

            Assert.Equal("Bitmap", op.Kernel);
            Assert.Equal([external.Image], op.Inputs);
        }
    }

    /// <summary>
    ///     ⚠ The id map is point-sampled and every other map is interpolated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § D12's own sentence, made mechanical: an id is "nearest-sampled and never
    ///         filtered".</b> Interpolating two material indices produces a third that belongs to no
    ///         material, so an id map bilinearly resampled into the graph's resolution grows a
    ///         hairline of a fourth material along every boundary — which is the same defect the
    ///         gutter dilation is excluded from, arriving by a different door. It is invisible in
    ///         every counter and looks like an antialiased edge.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the other eight are asserted too, because a node that point-sampled
    ///         everything would pass the half of this that is about <c>id</c>.</b> A continuous
    ///         measurement read nearest is a blocky curvature map, which reads as a low bake
    ///         resolution rather than as a bug.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_id_map_is_never_interpolated_and_every_other_map_is() {
        foreach (var usage in TextureMeshMaps.Known) {
            var compiler = Compiler();
            var compilation = compiler.Compile(Graph(usage, out _));
            var op = Assert.Single(compilation.Value.Ops);
            var filter = Assert.Single(op.Parameters, parameter => parameter.Name == "filter");

            Assert.Equal(usage == "id" ? 0f : 1f, filter.Value);

            // ⚠ And never decoded. Every one of the nine is a measurement rather than a picture, so
            // running the sRGB curve over one would bend a direction or a distance into a plausible
            // wrong number — the failure `Bitmap.rvn` spends a section on, in its other direction.
            Assert.Equal(0f, Assert.Single(op.Parameters, parameter => parameter.Name == "srgb").Value);
        }
    }

    /// <summary>
    ///     A measurement per texel comes back grey and a direction comes back colour.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves are silent and they are silent in opposite directions.</b> A bent normal
    ///     resolved as grey loses two of its three components at the first thing that reads it; a
    ///     curvature resolved as colour is a type error at any port that measures, which stops a
    ///     generator compiling for a reason that names the wrong node. Nothing about the file says
    ///     which it is — a PNG is four channels either way — so the classification is the node's and
    ///     this is what holds it.
    /// </remarks>
    [Theory]
    [InlineData("height", TextureFormat.R16Float)]
    [InlineData("ao", TextureFormat.R16Float)]
    [InlineData("curvature", TextureFormat.R16Float)]
    [InlineData("thickness", TextureFormat.R16Float)]
    [InlineData("normal", TextureFormat.Rgba16Float)]
    [InlineData("bent", TextureFormat.Rgba16Float)]
    [InlineData("position", TextureFormat.Rgba16Float)]
    [InlineData("world", TextureFormat.Rgba16Float)]
    [InlineData("id", TextureFormat.Rgba16Float)]
    public void A_scalar_map_is_grey_and_a_vector_map_is_colour(string usage, TextureFormat format) {
        var compiler = Compiler();
        var compilation = compiler.Compile(Graph(usage, out _));
        var op = Assert.Single(compilation.Value.Ops);

        Assert.Equal(format, compilation.Value.Images[op.Output].Format);

        // The nine rows above are the whole of the node's vocabulary, so the split cannot quietly
        // stop covering a usage somebody added.
        Assert.Contains(usage, TextureMeshMaps.Known);
    }

    /// <summary>A usage nothing bakes is refused, naming the node and the setting.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than defaulted, and the difference is the whole point of binding by
    ///     usage.</b> A misspelt <c>curvatur</c> that fell back to the first member would hand a
    ///     generator the <em>normal</em> map — a perfectly plausible picture, wired to the wrong
    ///     measurement, with nothing anywhere saying so. Every mesh map looks like a mesh map.
    /// </remarks>
    [Theory]
    [InlineData("curvatur")]
    [InlineData("ambientOcclusion")]
    [InlineData("")]
    public void A_usage_nothing_bakes_is_refused_rather_than_defaulted(string usage) {
        var compiler = Compiler();
        var compilation = compiler.Compile(Graph(usage, out var node));
        var refusal = Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == "TG0010");

        Assert.Equal(node.Id, refusal.Node);
        Assert.Equal("Map", refusal.Port);

        // The message lists what it will take, because the author's next move is to pick one.
        Assert.Contains("curvature", refusal.Message, StringComparison.Ordinal);

        // And nothing was emitted: no dispatch, and — the half that matters — no external image, so a
        // host walking the compilation is not asked to resolve a reference the node refused.
        Assert.Empty(compiler.Externals);
        Assert.Null(compilation.Artefact);
    }

    /// <summary>Two nodes asking for the same map are handed one external image.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/800">#800</a>, and this case is the
    ///         tripwire that was written to go red when it landed.</b> It asserted two entries and two
    ///         images, and said in its own remarks that a generator reading curvature in two places
    ///         makes a host upload one PNG twice — which § 4.9's compounds turn into the normal case,
    ///         because Dirt reads curvature and occlusion and a stack containing both Dirt and
    ///         Curvature Edge Wear reads curvature twice after inlining.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One entry is not the assertion; one entry that both nodes read is.</b> A compiler
    ///         that pooled the list and went on allocating an image per asker would look
    ///         de-duplicated from <c>Externals</c> and cost exactly what it did before — and the
    ///         second image would be one nothing ever supplies a texture for, which
    ///         <c>ExternalViews</c> refuses. So what is read here is the op list: both resamples take
    ///         their input from the same image.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_nodes_asking_for_one_map_are_handed_one_external_image() {
        NodeGraphModel graph = new();
        var first = graph.Add("Source/Mesh Map");
        var second = graph.Add("Source/Mesh Map");
        var blend = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        graph.Connect(new(first.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(second.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));

        var compiler = Compiler();
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var external = Assert.Single(compiler.Externals);

        Assert.Equal("meshmap:curvature", external.Asset);
        Assert.Single(compilation.Value.Images, image => image.External);

        // ⚠ Two resamples still, and that is right rather than a leftover: a mesh map is uploaded at
        // the bake's resolution and each node resamples it into the graph's, so what is shared is the
        // upload and not the dispatch. Both of them read the one image, which is the claim.
        var resamples = compilation.Value.Ops.Where(op => op.Kernel == "Bitmap").ToList();

        Assert.Equal(2, resamples.Count);
        Assert.All(resamples, op => Assert.Equal([external.Image], op.Inputs));
    }
}
