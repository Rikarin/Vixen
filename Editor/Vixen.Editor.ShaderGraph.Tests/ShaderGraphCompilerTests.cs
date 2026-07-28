// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     A node graph compiled to Raven — doc 11 § node graphs, doc 07 § generated source.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every test here puts the emitted source through the real compiler.</b> A generated
///         shader that reads beautifully and does not compile is the only failure mode this stage
///         really has, and a golden-text assertion is exactly the kind of test that cannot see it.
///         There <i>is</i> a golden-text test as well, because doc 11's table asks for one and
///         because it is the test that notices a change nobody meant to make — but it is the second
///         line of defence, not the first.
///     </para>
///     <para>
///         No UI anywhere in this. What is being checked is the graph, the typing and the emission;
///         a canvas is a different thing that will consume all three.
///     </para>
/// </remarks>
public class ShaderGraphCompilerTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        Vixen.Editor.ShaderGraph.NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>Parses, binds, lowers and verifies. Returns everything that objected.</summary>
    static IReadOnlyList<Diagnostic> Check(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Graph.rvn");

        if (tree.Diagnostics.Count > 0) {
            return tree.Diagnostics;
        }

        var compilation = Compilation.Create("ShaderGraph", tree);
        var semantic = compilation.GetDiagnostics();

        if (semantic.Count > 0) {
            return semantic;
        }

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);

        return bag.ToArray();
    }

    /// <summary>Asserts the source compiles, with the source in the message when it does not.</summary>
    static void Compiles(string source) {
        var diagnostics = Check(source);

        Assert.True(
            diagnostics.Count == 0,
            "The generated shader did not compile:\n"
            + string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString()))
            + "\n\n"
            + source
        );
    }

    /// <summary>Runs a backend over it too, which is what a device would be handed.</summary>
    static void Generates(string source, string target) {
        Compiles(source);

        var tree = SyntaxTree.ParseText(source, path: "Graph.rvn");
        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(Compilation.Create("ShaderGraph", tree), bag);
        var backend = TargetBackends.Create(target);

        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);

        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(diagnostic => diagnostic.ToString())));
        Assert.NotEmpty(generated);
    }

    /// <summary>The simplest graph there is: a colour, written out.</summary>
    [Fact]
    public void A_graph_with_only_a_master_compiles() {
        var graph = new NodeGraphModel { Name = "Flat" };
        var master = graph.Add("Master/Unlit");

        master.SetValue("Colour", 0.25f, 0.5f, 0.75f);

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        Assert.Equal("Flat", result.Value.Name);
        Compiles(result.Value.Source);
    }

    /// <summary>The golden one: a known graph, and the exact text it emits.</summary>
    /// <remarks>
    ///     Written out in full rather than matched in pieces, because what this test is for is
    ///     noticing a change nobody meant to make — a renamed variable, a reordered uniform, a
    ///     varying that started being interpolated. Every one of those still compiles.
    /// </remarks>
    [Fact]
    public void The_emitted_source_is_exactly_this() {
        var graph = new NodeGraphModel { Name = "Tinted" };

        var uv = graph.Add("Input/UV");
        var tiling = graph.Add("Vector/Tiling and Offset");
        var sample = graph.Add("Texture/Sample 2D");
        var master = graph.Add("Master/Unlit");

        tiling.SetValue("Tiling", 2f, 2f);

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));
        graph.Connect(new(tiling.Id, "Out"), new(sample.Id, "UV"));
        graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "Colour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        const string Expected = """
            // Generated from a node graph by Vixen.Editor.ShaderGraph. The graph is the source; this is not.

            package Vixen.ShaderGraph.Generated

            shader Tinted {
                /// The object-to-clip transform. Every graph has one; none of them author it.
                var worldViewProjection: mat4

                /// The object-to-world transform, for the world-space interpolators.
                var world: mat4

                var albedo: Texture2D

                var albedoSampler: Sampler

                stream var uv: float2

                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3, texcoord: float2): float4 {
                    uv = texcoord
                    return worldViewProjection * float4(position, 1f)
                }

                [PixelShader]
                [Semantic("SV_Target")]
                func Pixel(): float4 {
                    val n1_UV = uv
                    val n2_Out = n1_UV * float2(2f, 2f) + float2(0f, 0f)
                    val n3_RGBA = albedo.Sample(albedoSampler, n2_Out)
                    val surface = float4(n3_RGBA.xyz, 1f)
                    return surface
                }
            }

            """;

        Assert.Equal(Expected.ReplaceLineEndings(), result.Value.Source.ReplaceLineEndings());
        Compiles(result.Value.Source);
    }

    /// <summary>Compiling the same graph twice gives the same text, which is what a golden test needs.</summary>
    [Fact]
    public void The_same_graph_emits_the_same_source_every_time() {
        var graph = new NodeGraphModel { Name = "Stable" };

        var a = graph.Add("Math/Add");
        var b = graph.Add("Math/Multiply");
        var master = graph.Add("Master/Unlit");

        graph.Connect(new(a.Id, "Out"), new(b.Id, "A"));
        graph.Connect(new(b.Id, "Out"), new(master.Id, "Colour"));

        var compiler = new ShaderGraphCompiler(Library());

        Assert.Equal(compiler.Compile(graph).Value.Source, compiler.Compile(graph).Value.Source);
    }

    /// <summary>Every node in the library, in one graph, through both backends.</summary>
    /// <remarks>
    ///     The test that earns the node library its keep: a node whose Raven is subtly wrong compiles
    ///     nowhere, and one whose emitted expression is the wrong width fails to type-check. Neither
    ///     shows up in a graph that never uses it.
    /// </remarks>
    [Fact]
    public void Every_node_in_the_library_reaches_both_backends() {
        var graph = new NodeGraphModel { Name = "Everything" };
        var master = graph.Add("Master/PBR");

        // One of each, wired where wiring proves something and left alone where it does not: an
        // unconnected input still emits, through its default, which is the path a fresh node takes.
        var uv = graph.Add("Input/UV");
        var tiling = graph.Add("Vector/Tiling and Offset");
        var sample = graph.Add("Texture/Sample 2D");
        var split = graph.Add("Vector/Split");
        var combine = graph.Add("Vector/Combine");

        graph.Add("Input/World Position");
        graph.Add("Input/World Normal");
        graph.Add("Input/Vertex Colour");
        graph.Add("Input/Time");
        graph.Add("Input/Constant");
        graph.Add("Input/Colour Property");
        graph.Add("Input/Float Property");

        foreach (var path in new[] {
            "Math/Add", "Math/Subtract", "Math/Multiply", "Math/Divide", "Math/Lerp", "Math/Saturate",
            "Math/One Minus", "Math/Power", "Math/Absolute", "Math/Fraction", "Math/Sine",
            "Math/Smoothstep", "Math/Dot", "Math/Normalize"
        }) {
            graph.Add(path);
        }

        graph.Connect(new(uv.Id, "UV"), new(tiling.Id, "UV"));
        graph.Connect(new(tiling.Id, "Out"), new(sample.Id, "UV"));
        graph.Connect(new(sample.Id, "RGBA"), new(split.Id, "In"));
        graph.Connect(new(split.Id, "X"), new(combine.Id, "X"));
        graph.Connect(new(combine.Id, "Out"), new(master.Id, "BaseColour"));

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        Generates(result.Value.Source, "glsl");
        Generates(result.Value.Source, "spirv");
    }

    /// <summary>A graph that never reads a normal does not interpolate one.</summary>
    [Fact]
    public void Only_the_varyings_the_graph_asked_for_are_interpolated() {
        var graph = new NodeGraphModel { Name = "Flat" };

        graph.Add("Master/Unlit");

        var flat = new ShaderGraphCompiler(Library()).Compile(graph).Value.Source;

        Assert.DoesNotContain("stream var", flat, StringComparison.Ordinal);

        var lit = new NodeGraphModel { Name = "Lit" };

        lit.Add("Master/PBR");

        var source = new ShaderGraphCompiler(Library()).Compile(lit).Value.Source;

        Assert.Contains("stream var worldPosition: float3", source, StringComparison.Ordinal);
        Assert.Contains("stream var worldNormal: float3", source, StringComparison.Ordinal);
        Assert.DoesNotContain("stream var uv:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_graph_with_no_master_says_so_rather_than_emitting_nothing() {
        var graph = new NodeGraphModel { Name = "Headless" };

        graph.Add("Math/Add");

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SG0003");
    }

    [Fact]
    public void A_graph_with_two_masters_says_which_two() {
        var graph = new NodeGraphModel { Name = "Twins" };

        graph.Add("Master/Unlit");

        var second = graph.Add("Master/Sprite");

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);

        var diagnostic = Assert.Single(result.Diagnostics, entry => entry.Id == "SG0002");

        Assert.Equal(second.Id, diagnostic.Node);
    }

    /// <summary>A node type the library does not have is named, not skipped.</summary>
    [Fact]
    public void A_missing_node_type_is_reported_against_the_node() {
        var graph = new NodeGraphModel { Name = "Plugin" };

        graph.Add("Master/Unlit");

        var missing = graph.Add("ThirdParty/Kaleidoscope");

        var result = new ShaderGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);

        var diagnostic = Assert.Single(result.Diagnostics, entry => entry.Id == "NG0001");

        Assert.Equal(missing.Id, diagnostic.Node);
        Assert.Contains("ThirdParty/Kaleidoscope", diagnostic.Message, StringComparison.Ordinal);
    }
}
