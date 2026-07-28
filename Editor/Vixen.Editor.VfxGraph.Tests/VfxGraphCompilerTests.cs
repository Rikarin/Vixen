// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.VfxGraph;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     One graph, two targets — doc 11 § VfxGraph, doc 06 § the dual-target requirement.
/// </summary>
/// <remarks>
///     <para>
///         The claim under test is not that the graph compiles. It is that <i>one</i> authored graph
///         produces both an effect the CPU can run and a shader a device can run, without a second
///         node library, a second lowering, or any way for the two to have understood the graph
///         differently. That is why the tests here run the compiled graph and compile the emitted
///         source, from the same call.
///     </para>
///     <para>
///         No UI. What is checked is the compilation.
///     </para>
/// </remarks>
public class VfxGraphCompilerTests {
    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        Vixen.Editor.VfxGraph.NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>A fountain, wired as an author would wire it.</summary>
    static NodeGraphModel Fountain() {
        var graph = new NodeGraphModel { Name = "Fountain" };

        var spawn = graph.Add("Vfx/Spawn/Rate");
        var position = graph.Add("Vfx/Initialize/Position in Sphere");
        var velocity = graph.Add("Vfx/Initialize/Random Velocity");
        var lifetime = graph.Add("Vfx/Initialize/Lifetime");
        var size = graph.Add("Vfx/Initialize/Size");
        var colour = graph.Add("Vfx/Initialize/Colour");
        var gravity = graph.Add("Vfx/Update/Gravity");
        var drag = graph.Add("Vfx/Update/Drag");
        var integrate = graph.Add("Vfx/Update/Integrate");
        var fade = graph.Add("Vfx/Update/Colour over Life");
        var output = graph.Add("Vfx/Output/Billboard");

        position.SetValue("Radius", 0.2f);
        velocity.SetValue("Minimum", 2f);
        velocity.SetValue("Maximum", 5f);

        // A chain, because the order blocks run in is the order an author drew them in.
        NodeId[] chain = [
            spawn.Id, position.Id, velocity.Id, lifetime.Id, size.Id, colour.Id,
            gravity.Id, drag.Id, integrate.Id, fade.Id, output.Id
        ];

        for (var index = 1; index < chain.Length; index++) {
            graph.Connect(new(chain[index - 1], "Out"), new(chain[index], "In"));
        }

        return graph;
    }

    /// <summary>Compiles the emitted Raven and asserts nothing objected.</summary>
    static void Compiles(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Effect.rvn");

        Assert.True(tree.Diagnostics.Count == 0, Report("Parsing", tree.Diagnostics, source));

        var compilation = Compilation.Create("VfxGraph", tree);
        var semantic = compilation.GetDiagnostics();

        Assert.True(semantic.Count == 0, Report("Binding", semantic, source));

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, Report("Lowering", bag.ToArray(), source));
    }

    static string Report(string phase, IReadOnlyList<Diagnostic> diagnostics, string source) =>
        $"{phase} failed:\n{string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString()))}\n\n{source}";

    /// <summary>The claim, in one test: one graph, an effect that runs and a shader that compiles.</summary>
    [Fact]
    public void One_graph_produces_an_effect_and_a_shader() {
        var result = new VfxGraphCompiler(Library()).Compile(Fountain());

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        // The CPU half, actually stepped. A graph that compiles and produces no particles would pass
        // every structural assertion there is.
        using var system = new VfxSystem(result.Value.Graph);

        for (var step = 0; step < 30; step++) {
            system.Step(1f / 60f);
        }

        Assert.True(system.Count > 0, "The compiled effect spawned nothing in half a second.");

        Assert.All(
            system.Particles.Position[..system.Count].ToArray(),
            position => Assert.True(float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z))
        );

        // And the GPU half, actually compiled.
        Assert.Equal("Fountain", result.Value.Shader.Name);
        Assert.True(result.Value.Shader.HasInitialize);
        Assert.True(result.Value.Shader.HasUpdate);
        Compiles(result.Value.Shader.Source);
    }

    /// <summary>The blocks come out in the order they were wired, not the order they were added.</summary>
    [Fact]
    public void The_wire_decides_the_order_the_blocks_run_in() {
        var graph = new NodeGraphModel { Name = "Ordered" };

        // Added last-first, so insertion order would give the opposite answer.
        var integrate = graph.Add("Vfx/Update/Integrate");
        var drag = graph.Add("Vfx/Update/Drag");
        var gravity = graph.Add("Vfx/Update/Gravity");
        var spawn = graph.Add("Vfx/Spawn/Burst");
        var velocity = graph.Add("Vfx/Initialize/Set Velocity");
        var lifetime = graph.Add("Vfx/Initialize/Lifetime");
        var position = graph.Add("Vfx/Initialize/Position in Box");

        graph.Connect(new(spawn.Id, "Out"), new(position.Id, "In"));
        graph.Connect(new(position.Id, "Out"), new(velocity.Id, "In"));
        graph.Connect(new(velocity.Id, "Out"), new(lifetime.Id, "In"));
        graph.Connect(new(lifetime.Id, "Out"), new(gravity.Id, "In"));
        graph.Connect(new(gravity.Id, "Out"), new(drag.Id, "In"));
        graph.Connect(new(drag.Id, "Out"), new(integrate.Id, "In"));

        var compiled = new VfxGraphCompiler(Library()).Compile(graph).Value.Graph;

        Assert.Equal(
            [VfxOpcode.Gravity, VfxOpcode.Drag, VfxOpcode.Integrate],
            compiled.Updaters.Select(operation => operation.Opcode)
        );
    }

    /// <summary>An author's typed value reaches the operation it parameterises.</summary>
    [Fact]
    public void A_value_typed_into_a_port_reaches_the_operation() {
        var graph = new NodeGraphModel { Name = "Heavy" };

        graph.Add("Vfx/Spawn/Burst");

        var gravity = graph.Add("Vfx/Update/Gravity");

        gravity.SetValue("Acceleration", 0f, -20f, 0f);

        var compiled = new VfxGraphCompiler(Library()).Compile(graph).Value.Graph;
        var operation = Assert.Single(compiled.Updaters, entry => entry.Opcode == VfxOpcode.Gravity);

        Assert.Equal(-20f, operation.A.Y);
    }

    /// <summary>And a port nobody touched takes the default its declaration gave it.</summary>
    [Fact]
    public void An_untouched_port_takes_its_declared_default() {
        var graph = new NodeGraphModel { Name = "Default" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Update/Gravity");

        var compiled = new VfxGraphCompiler(Library()).Compile(graph).Value.Graph;
        var operation = Assert.Single(compiled.Updaters, entry => entry.Opcode == VfxOpcode.Gravity);

        Assert.Equal(-9.81f, operation.A.Y, 5);
    }

    /// <summary>The output node decides the renderer, and with it what the graph allocates.</summary>
    [Fact]
    public void The_output_node_decides_the_renderer() {
        var graph = new NodeGraphModel { Name = "Sparks" };

        graph.Add("Vfx/Spawn/Burst");

        var light = graph.Add("Vfx/Output/Light");

        light.SetValue("Intensity", 3f);
        light.SetValue("Range", 6f);

        var compiled = new VfxGraphCompiler(Library()).Compile(graph).Value.Graph;

        Assert.NotNull(compiled.Renderer);
        Assert.Equal(VfxRendererKind.Light, compiled.Renderer.Value.Kind);
        Assert.Equal(3f, compiled.Renderer.Value.Intensity);
        Assert.Equal(6f, compiled.Renderer.Value.Range);
    }

    /// <summary>A graph with nothing to spawn particles is an effect that would never appear.</summary>
    [Fact]
    public void A_graph_with_no_spawner_says_so() {
        var graph = new NodeGraphModel { Name = "Silent" };

        graph.Add("Vfx/Update/Integrate");

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "VG0002");
    }

    /// <summary>
    ///     The runtime's own refusal reaches the author as a diagnostic rather than as an exception.
    /// </summary>
    /// <remarks>
    ///     <c>VfxCompiledGraph.Compile</c> refuses a graph whose updaters read an attribute no
    ///     initializer writes — an integration over a velocity nothing set. That is exactly the
    ///     mistake a node graph makes easy, so it is worth checking that it arrives as a message and
    ///     not as a crash.
    /// </remarks>
    [Fact]
    public void The_runtimes_own_refusal_arrives_as_a_diagnostic() {
        var graph = new NodeGraphModel { Name = "Unset" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Update/Integrate");

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);

        var diagnostic = Assert.Single(result.Diagnostics, entry => entry.Id == "VG0003");

        Assert.Contains("Velocity", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>A loop of blocks is refused where it is drawn, by the framework.</summary>
    [Fact]
    public void A_loop_of_blocks_cannot_be_drawn() {
        var graph = new NodeGraphModel { Name = "Loop" };

        var one = graph.Add("Vfx/Update/Gravity");
        var two = graph.Add("Vfx/Update/Drag");

        graph.Connect(new(one.Id, "Out"), new(two.Id, "In"));

        Assert.Throws<ArgumentException>(() => graph.Connect(new(two.Id, "Out"), new(one.Id, "In")));
    }

    /// <summary>Every block in the library, in one graph, through both halves.</summary>
    [Fact]
    public void Every_block_in_the_library_compiles_both_ways() {
        var graph = new NodeGraphModel { Name = "Everything" };

        foreach (var path in Library().Types.Select(type => type.Path).Order(StringComparer.Ordinal)) {
            // One effect node and one output; the rest are blocks and a graph may hold all of them.
            if (path == "Vfx/Output/Light") {
                continue;
            }

            graph.Add(path);
        }

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        using var system = new VfxSystem(result.Value.Graph);

        for (var step = 0; step < 10; step++) {
            system.Step(1f / 60f);
        }

        Compiles(result.Value.Shader.Source);
    }
}
