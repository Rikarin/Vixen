// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Graphics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     One node's sub-expression, compiled on its own — doc 11 § preview thumbnails.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim under test is that a sub-expression is a whole shader.</b> Everything else
///         about a preview — a target, a pipeline, a picture — rests on being able to compile the
///         expression one node produces without the rest of the graph, and the honest way to check
///         that is the same one <see cref="ShaderGraphCompilerTests" /> uses for a whole graph: put
///         the emitted text through the real Raven front end. A golden string would pass on a shader
///         that does not type check.
///     </para>
///     <para>
///         No device anywhere in this file. What needs one is
///         <c>ShaderGraphPreviewDeviceTests</c>, which is about whether the picture is a picture.
///     </para>
/// </remarks>
public class ShaderGraphPreviewTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>Parses, binds, lowers and verifies. Returns everything that objected.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside the same library sources the renderer compiles a preview with</b>, and this
    ///     used to be one tree — <a href="https://github.com/Rikarin/Vixen/issues/510">#510</a>. A
    ///     checker with a narrower compilation than production is the hazard this repository names
    ///     most: it would report a node that calls <c>ComputeColor</c> as broken while the editor
    ///     drew it perfectly, and, read the other way, it is why a wider one would report a node as
    ///     fine that the editor cannot show. The set comes from
    ///     <c>ShaderGraphPreviewPrelude.Sources</c> rather than from a list here, so the two cannot
    ///     drift.
    /// </remarks>
    static IReadOnlyList<Diagnostic> Check(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Preview.rvn");

        if (tree.Diagnostics.Count > 0) {
            return tree.Diagnostics;
        }

        var trees = new List<SyntaxTree>();

        foreach (var (name, text) in ShaderGraphPreviewPrelude.Sources) {
            trees.Add(SyntaxTree.ParseText(text, path: name));
        }

        trees.Add(tree);

        var compilation = Compilation.Create("Preview", trees);
        var semantic = compilation.GetDiagnostics();

        if (semantic.Count > 0) {
            return semantic;
        }

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);

        return bag.ToArray();
    }

    static void Compiles(string source) {
        var diagnostics = Check(source);

        Assert.True(
            diagnostics.Count == 0,
            $"The preview's Raven did not compile:{Environment.NewLine}"
            + $"{string.Join(Environment.NewLine, diagnostics)}{Environment.NewLine}{Environment.NewLine}{source}"
        );
    }

    /// <summary>A UV node fed into a tiling node: the preview is the tiling node's own value.</summary>
    static (NodeGraphModel Graph, GraphNode Uv, GraphNode Tiling, GraphNode Master) Tinted() {
        var graph = new NodeGraphModel { Name = "Tiled" };

        var uv = graph.Add("Input/UV", new(0f, 0f));
        var tiling = graph.Add("Vector/Tiling and Offset", new(200f, 0f));
        var master = graph.Add("Master/Unlit", new(400f, 0f));

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));
        graph.Connect(new(tiling.Id, "Out"), new(master.Id, "Colour"));

        return (graph, uv, tiling, master);
    }

    /// <summary>The middle of a graph compiles on its own, and what comes out is a shader.</summary>
    [Fact]
    public void A_node_in_the_middle_compiles_to_a_shader() {
        var (graph, _, tiling, _) = Tinted();

        var result = ShaderGraphPreview.Compile(graph, tiling.Id, Library());

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(one => one.Message)));
        Compiles(result.Artefact!.Source);
    }

    /// <summary>Only the closure: the master downstream of the node is not in the preview.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim that makes this a sub-expression and not the graph.</b> A preview that
    ///     dragged the author's master along would be showing what the shader outputs rather than what
    ///     the node computes — and for a node feeding a PBR master it would show it lit, which is a
    ///     different question and one in a different unit system.
    /// </remarks>
    [Fact]
    public void A_preview_holds_the_nodes_upstream_and_no_others() {
        var (graph, uv, tiling, master) = Tinted();

        var source = ShaderGraphPreview.Compile(graph, tiling.Id, Library()).Artefact!.Source;

        // The compiler names an output's variable after its node's identity, so the identities are
        // what the emitted text can be asked about.
        Assert.Contains($"n{uv.Id.Value}_UV", source, StringComparison.Ordinal);
        Assert.Contains($"n{tiling.Id.Value}_Out", source, StringComparison.Ordinal);
        Assert.DoesNotContain($"n{master.Id.Value}_", source, StringComparison.Ordinal);
    }

    /// <summary>The preview of a node upstream of everything is that node alone.</summary>
    [Fact]
    public void A_source_node_previews_on_its_own() {
        var (graph, uv, tiling, _) = Tinted();

        var result = ShaderGraphPreview.Compile(graph, uv.Id, Library());

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(one => one.Message)));
        Assert.DoesNotContain($"n{tiling.Id.Value}_", result.Artefact!.Source, StringComparison.Ordinal);
        Compiles(result.Artefact.Source);
    }

    /// <summary>A master previews as itself, without a second one being added.</summary>
    [Fact]
    public void A_master_previews_as_itself() {
        var (graph, _, _, master) = Tinted();

        var result = ShaderGraphPreview.Compile(graph, master.Id, Library());

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(one => one.Message)));
        Compiles(result.Artefact!.Source);
    }

    /// <summary>A lit master's preview is the lit shader, uniforms and all.</summary>
    /// <remarks>
    ///     The one case where a preview is not unlit, and it is not a special case: the node <i>is</i>
    ///     the master, so the closure ends at it and what is compiled is what the graph emits.
    /// </remarks>
    [Fact]
    public void A_pbr_master_previews_as_the_lit_shader() {
        var graph = new NodeGraphModel { Name = "Lit" };
        var master = graph.Add("Master/PBR");

        var result = ShaderGraphPreview.Compile(graph, master.Id, Library());

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(one => one.Message)));
        Assert.Contains("lightDirection", result.Artefact!.Source, StringComparison.Ordinal);
        Compiles(result.Artefact.Source);
    }

    /// <summary>Every node type in the library that asks for a preview can be given one.</summary>
    /// <remarks>
    ///     ⚠ <b>Over the library rather than over a list written here.</b> <c>[Node(Preview = true)]</c>
    ///     is a declaration a new node type makes on its own, and a test naming the six that have it
    ///     today would go on passing while the seventh emitted a shader that does not compile.
    /// </remarks>
    [Fact]
    public void Every_node_that_asks_for_a_preview_compiles_to_one() {
        var registry = Library();
        var asked = 0;

        foreach (var definition in registry.Types) {
            if (!definition.Preview) {
                continue;
            }

            asked++;

            var graph = new NodeGraphModel { Name = "One" };
            var node = graph.Add(definition.Path);
            var result = ShaderGraphPreview.Compile(graph, node.Id, registry);

            Assert.True(
                result.Succeeded,
                $"'{definition.Path}' asks for a preview and does not compile to one: "
                + string.Join("; ", result.Diagnostics.Select(one => one.Message))
            );

            Compiles(result.Artefact!.Source);
        }

        Assert.True(asked > 0, "No node type in the library asks for a preview, so this test checked nothing.");
    }

    /// <summary>A node that is not in the graph is a diagnostic, not an exception.</summary>
    [Fact]
    public void A_node_that_is_not_there_is_reported() {
        var (graph, _, _, _) = Tinted();

        var result = ShaderGraphPreview.Compile(graph, new NodeId(999), Library());

        Assert.Null(result.Artefact);
        Assert.Contains(result.Diagnostics, one => one.Id == "SGP0001");
    }

    /// <summary>An inline value an author typed reaches the preview.</summary>
    /// <remarks>
    ///     The claim the renderer's invalidation rests on: if the numbers on a port did not reach the
    ///     emitted text, then a preview keyed on that text would never notice an edit at all — and a
    ///     preview that never changes is indistinguishable from one that is not implemented.
    /// </remarks>
    [Fact]
    public void An_edited_value_changes_the_emitted_source() {
        var (graph, _, tiling, _) = Tinted();
        var registry = Library();

        var before = ShaderGraphPreview.Compile(graph, tiling.Id, registry).Artefact!.Source;

        tiling.SetValue("Tiling", 4f, 4f);

        var after = ShaderGraphPreview.Compile(graph, tiling.Id, registry).Artefact!.Source;

        Assert.NotEqual(before, after);
        Compiles(after);
    }

    /// <summary>Moving a node does not.</summary>
    /// <remarks>
    ///     The other half of the same claim, and the one that makes the throttling work: a canvas
    ///     gesture emits the source that is already cached, so it costs no compilation.
    /// </remarks>
    [Fact]
    public void Moving_a_node_does_not_change_the_emitted_source() {
        var (graph, _, tiling, _) = Tinted();
        var registry = Library();

        var before = ShaderGraphPreview.Compile(graph, tiling.Id, registry).Artefact!.Source;

        tiling.Position = new(900f, 900f);

        Assert.Equal(before, ShaderGraphPreview.Compile(graph, tiling.Id, registry).Artefact!.Source);
    }

    /// <summary>
    ///     ⚠ A node that calls the library previews, all the way to a variant — and it costs the
    ///     preview no binding.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/510">#510</a>, through the production
    ///         seam rather than beside it: <c>ShaderGraphPreviewPrelude.Compile</c> is what
    ///         <c>ShaderGraphPreviewRenderer</c> calls, so a change that widened the front end and
    ///         not the renderer would leave this red.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second assertion is the one the issue asked to establish first.</b> The
    ///         renderer refuses a variant whose reflection asks for anything but its one uniform
    ///         block — so a prelude that pulled a texture or a sampler into scope would have turned
    ///         "these five nodes have no preview" into "every preview is refused", which is a worse
    ///         state arrived at by a fix. The library files are structs of pure functions and a
    ///         function nothing calls does not reach the module; this is the check rather than the
    ///         claim.
    ///     </para>
    ///     <para>
    ///         ⚠ And it is a <c>Procedural/Noise</c> rather than a maths node on purpose: with the
    ///         prelude emptied, <c>TryGet</c> comes back null here and every other case in this file
    ///         goes on passing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_node_that_calls_the_library_compiles_to_a_variant_that_binds_one_block() {
        var graph = new NodeGraphModel { Name = "Noisy" };
        var noise = graph.Add("Procedural/Noise");
        var master = graph.Add("Master/Unlit", new(300f, 0f));

        graph.Connect(new(noise.Id, "Out"), new(master.Id, "Colour"));

        var result = ShaderGraphPreview.Compile(graph, noise.Id, Library());

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(one => one.Message)));
        Assert.Contains("ComputeColor.ValueNoise", result.Artefact!.Source, StringComparison.Ordinal);

        var data = ShaderGraphPreviewPrelude
            .Compile(ShaderGraphPreview.Name + ".rvn", result.Artefact.Source)
            .TryGet(EffectKey.Of(ShaderGraphPreview.Name));

        Assert.NotNull(data);

        // Exactly what ShaderGraphPreviewRenderer will accept: uniform blocks and nothing else.
        Assert.All(
            data.Bindings,
            binding => Assert.True(
                binding.Kind is DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer,
                $"'{binding.Name}' is a {binding.Kind}, which the preview renderer refuses — the "
                + "prelude has pulled a resource into the preview's reflection."
            )
        );
    }

    /// <summary>The same graph emits the same text however many times it is asked.</summary>
    [Fact]
    public void The_same_graph_emits_the_same_text() {
        var (graph, _, tiling, _) = Tinted();
        var registry = Library();

        Assert.Equal(
            ShaderGraphPreview.Compile(graph, tiling.Id, registry).Artefact!.Source,
            ShaderGraphPreview.Compile(graph, tiling.Id, registry).Artefact!.Source
        );
    }
}
