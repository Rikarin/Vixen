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

    /// <summary>The mesh output makes an instanced renderer, turned the way its ports say.</summary>
    /// <remarks>
    ///     <b>The node the library did not have.</b> <c>VfxRendererKind.Mesh</c>, the expansion that
    ///     builds its instances and the feature that draws them all shipped, and the only way to reach
    ///     any of it was a graph built in code — so an author could not make one at all.
    /// </remarks>
    [Fact]
    public void The_mesh_output_makes_an_instanced_renderer() {
        Assert.Equal(VfxBillboardAlignment.Camera, Rendered(_ => { }).Alignment);
        Assert.Equal(VfxRendererKind.Mesh, Rendered(_ => { }).Kind);

        // Velocity wins over an axis, which is what the node's remarks promise.
        var both = Rendered(node => {
            node.SetValue("Align to Velocity", 1f);
            node.SetValue("Axis", 1f, 0f, 0f);
        });

        Assert.Equal(VfxBillboardAlignment.Velocity, both.Alignment);

        var axis = Rendered(node => node.SetValue("Axis", 0f, 0f, 1f));

        Assert.Equal(VfxBillboardAlignment.FixedAxis, axis.Alignment);
        Assert.Equal(new(0f, 0f, 1f), axis.Axis);

        VfxRenderer Rendered(Action<GraphNode> configure) {
            var graph = new NodeGraphModel { Name = "Debris" };

            graph.Add("Vfx/Spawn/Burst");

            var velocity = graph.Add("Vfx/Initialize/Set Velocity");

            velocity.SetValue("Velocity", 0f, 3f, 0f);
            configure(graph.Add("Vfx/Output/Mesh"));

            var result = new VfxGraphCompiler(Library()).Compile(graph);

            Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
            Assert.NotNull(result.Value.Graph.Renderer);

            return result.Value.Graph.Renderer.Value;
        }
    }

    /// <summary>The ribbon output resolves the attribute it names to the slot that attribute got.</summary>
    /// <remarks>
    ///     ⚠ And is sorted by age whatever anything else says, because that is the ribbon's own order
    ///     rather than a drawing preference.
    /// </remarks>
    [Fact]
    public void The_ribbon_output_resolves_the_attribute_it_names() {
        var graph = new NodeGraphModel { Name = "Trail" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Initialize/Lifetime");

        // Two attributes, so that the one the ribbon names is not the one a zero would have found.
        var seed = graph.Add("Vfx/Initialize/Random Custom");
        var strip = graph.Add("Vfx/Initialize/Random Custom");

        seed.SetText("Attribute", "spark");
        strip.SetText("Attribute", "strip");
        strip.SetValue("Maximum", 4f, 0f, 0f, 0f);

        graph.Add("Vfx/Output/Ribbon").SetText("Attribute", "strip");

        // Wired, so the two Random Custom blocks are declared in a decided order rather than in
        // whatever order the graph happens to enumerate.
        graph.Connect(new(seed.Id, "Out"), new(strip.Id, "In"));

        var compiled = new VfxGraphCompiler(Library()).Compile(graph).Value.Graph;

        Assert.NotNull(compiled.Renderer);
        Assert.Equal(VfxRendererKind.Ribbon, compiled.Renderer.Value.Kind);
        Assert.Equal(1, compiled.Renderer.Value.RibbonSlot);
        Assert.Equal(1, compiled.SlotOf("strip"));

        // A ribbon is ordered by its particles' ages, so drawing one is what makes the graph keep
        // them — the renderer declaring its reads, arriving through the node.
        Assert.True(compiled.Attributes.HasFlag(VfxAttribute.Age));
    }

    /// <summary>A ribbon naming an attribute nothing writes is refused, not drawn as one strip.</summary>
    /// <remarks>
    ///     The failure this replaces had no symptom: unwritten storage is zero for every particle, so
    ///     every particle shares a strip and the effect is a tangle with nothing to search for.
    /// </remarks>
    [Fact]
    public void A_ribbon_naming_an_attribute_nothing_writes_is_refused() {
        var graph = new NodeGraphModel { Name = "Tangle" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Output/Ribbon").SetText("Attribute", "strip");

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("strip", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The three custom-attribute blocks reach their opcodes, and share one slot per name.
    /// </summary>
    /// <remarks>
    ///     The gap these close is not a missing opcode — <c>SetCustom</c>, <c>RandomCustom</c> and
    ///     <c>CustomOverLife</c> all shipped — but a missing <i>name</i>: a port is lanes of float and
    ///     an attribute is an identifier, so until a node could hold a string these were reachable
    ///     only from a graph built in code.
    /// </remarks>
    [Fact]
    public void The_custom_attribute_blocks_share_a_slot_per_name() {
        var graph = new NodeGraphModel { Name = "Custom" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Initialize/Lifetime");

        var set = graph.Add("Vfx/Initialize/Set Custom");
        var random = graph.Add("Vfx/Initialize/Random Custom");
        var fade = graph.Add("Vfx/Update/Custom over Life");

        set.SetText("Attribute", "glow");
        set.SetValue("Lanes", 3f);
        set.SetValue("Value", 0.25f, 0.5f, 0.75f, 0f);

        random.SetText("Attribute", "spark");

        fade.SetText("Attribute", "glow");
        fade.SetValue("Lanes", 3f);

        graph.Connect(new(set.Id, "Out"), new(random.Id, "In"));
        graph.Connect(new(random.Id, "Out"), new(fade.Id, "In"));

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var compiled = result.Value.Graph;

        Assert.Equal(["glow", "spark"], compiled.Customs.Select(custom => custom.Name));
        Assert.Equal(VfxAttributeType.Float3, compiled.Customs[0].Type);
        Assert.Equal(VfxAttributeType.Float, compiled.Customs[1].Type);

        var written = Assert.Single(compiled.Initializers, entry => entry.Opcode == VfxOpcode.SetCustom);

        Assert.Equal(0, written.Slot);
        Assert.Equal(0.5f, written.A.Y);

        Assert.Equal(1, Assert.Single(compiled.Initializers, entry => entry.Opcode == VfxOpcode.RandomCustom).Slot);

        // The updater names the same attribute the initializer did, so it writes the slot that one
        // declared rather than declaring a second of its own.
        Assert.Equal(0, Assert.Single(compiled.Updaters, entry => entry.Opcode == VfxOpcode.CustomOverLife).Slot);

        // And the shader half, which declares one buffer per slot in that order.
        Compiles(result.Value.Shader.Source);
    }

    /// <summary>One name used at two widths is a problem rather than a quiet widening.</summary>
    [Fact]
    public void One_name_at_two_widths_is_refused() {
        var graph = new NodeGraphModel { Name = "Conflict" };

        graph.Add("Vfx/Spawn/Burst");

        var narrow = graph.Add("Vfx/Initialize/Set Custom");
        var wide = graph.Add("Vfx/Initialize/Random Custom");

        narrow.SetText("Attribute", "glow");
        wide.SetText("Attribute", "glow");
        wide.SetValue("Lanes", 4f);

        graph.Connect(new(narrow.Id, "Out"), new(wide.Id, "In"));

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Float4", StringComparison.Ordinal));
    }

    /// <summary>Two lanes is refused rather than rounded down to one.</summary>
    /// <remarks>
    ///     <c>VfxAttributeType</c> has no <c>Float2</c>, and <c>VfxAttributes.Lanes</c> answers one for
    ///     anything it does not know — so a node that accepted two would store half of what was typed
    ///     and never say which half.
    /// </remarks>
    [Fact]
    public void A_custom_attribute_cannot_have_two_lanes() {
        var graph = new NodeGraphModel { Name = "Pair" };

        graph.Add("Vfx/Spawn/Burst");

        var node = graph.Add("Vfx/Initialize/Set Custom");

        node.SetText("Attribute", "uv");
        node.SetValue("Lanes", 2f);

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("lanes", StringComparison.Ordinal));
    }

    /// <summary>A name the emitted shader already uses is refused, against the name that was typed.</summary>
    /// <remarks>
    ///     ⚠ <b>A defect that only became reachable when a graph could name an attribute at all.</b>
    ///     A custom attribute's name is a buffer in the emitted source, in the same scope as the push
    ///     constants — so <c>seed</c> produced a shader whose second declaration of <c>seed</c> did
    ///     not parse, reported against generated text nobody wrote.
    /// </remarks>
    [Theory]
    [InlineData("seed")]
    [InlineData("age")]
    [InlineData("identifierOut")]
    [InlineData("Noise")]
    public void A_name_the_shader_already_uses_is_refused(string name) {
        var graph = new NodeGraphModel { Name = "Collision" };

        graph.Add("Vfx/Spawn/Burst");

        var node = graph.Add("Vfx/Initialize/Set Custom");

        node.SetText("Attribute", name);

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>And an attribute nobody named at all is refused before it reaches the shader.</summary>
    [Fact]
    public void A_custom_attribute_needs_a_name() {
        var graph = new NodeGraphModel { Name = "Nameless" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Initialize/Set Custom");

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("needs a name", StringComparison.Ordinal));
    }

    /// <summary>The two field and collider nodes the library was missing reach their opcodes.</summary>
    /// <remarks>
    ///     Both opcodes shipped with the field set and neither had a node, so an author could reach
    ///     <c>Attract</c> and <c>CollidePlane</c> and not their siblings — which is the difference
    ///     between a whirl and a pile, and between a ball and a floor.
    /// </remarks>
    [Fact]
    public void The_vortex_and_the_sphere_reach_their_opcodes() {
        var graph = new NodeGraphModel { Name = "Whirl" };

        graph.Add("Vfx/Spawn/Burst");
        graph.Add("Vfx/Initialize/Position in Box");
        graph.Add("Vfx/Initialize/Set Velocity").SetValue("Velocity", 0f, 1f, 0f);

        var vortex = graph.Add("Vfx/Update/Vortex");

        vortex.SetValue("Centre", 1f, 2f, 3f);
        vortex.SetValue("Axis", 0f, 0f, 1f);
        vortex.SetValue("Strength", 7f);
        vortex.SetValue("Radius", 9f);

        var sphere = graph.Add("Vfx/Update/Collide Sphere");

        sphere.SetValue("Centre", 4f, 5f, 6f);
        sphere.SetValue("Radius", 2f);
        sphere.SetValue("Bounce", 0.25f);
        sphere.SetValue("Friction", 0.75f);

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var turning = Assert.Single(result.Value.Graph.Updaters, entry => entry.Opcode == VfxOpcode.Vortex);

        // A.xyz is a point on the axis and A.w the acceleration; B.xyz is the axis and B.w the reach.
        Assert.Equal(new(1f, 2f, 3f, 7f), turning.A);
        Assert.Equal(new(0f, 0f, 1f, 9f), turning.B);

        var ball = Assert.Single(result.Value.Graph.Updaters, entry => entry.Opcode == VfxOpcode.CollideSphere);

        Assert.Equal(new(4f, 5f, 6f, 2f), ball.A);
        Assert.Equal(0.25f, ball.B.X);
        Assert.Equal(0.75f, ball.B.Y);

        // And the emitted Raven still compiles with both in it, which is the half a runtime assertion
        // cannot see.
        Compiles(result.Value.Shader.Source);
    }

    /// <summary>The last five opcodes reach the graph, and the spin they set actually advances.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>All five shipped in <em>both</em> backends with nothing able to author them</b> —
    ///         <c>SetPosition</c>, <c>VelocityInCone</c>, <c>SetRotation</c>,
    ///         <c>SetAngularVelocity</c> and <c>Rotate</c> are each implemented in
    ///         <c>VfxSimulation</c> and in <c>VfxShaderEmitter</c>, and none had a node. The module
    ///         README claimed one was left; there were five.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rotation assertion is behavioural rather than structural, and that is the
    ///         point.</b> Three of these five write an attribute that only a fourth one moves and only
    ///         <c>VfxGeometry</c> reads, so a graph carrying all three and turning nothing would
    ///         satisfy every "the opcode is in the list" check there is — which is the shape of every
    ///         built-and-never-fed defect in this tree. Roll is asserted to have <em>moved</em>, in the
    ///         direction and roughly the amount a second of the authored rate gives.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_last_five_opcodes_reach_the_graph_and_the_spin_advances() {
        var graph = new NodeGraphModel { Name = "Spun" };

        graph.Add("Vfx/Spawn/Burst");
        var life = graph.Add("Vfx/Initialize/Lifetime");

        // ⚠ Long and fixed, because the default is a random one to two seconds and this steps for
        // more than one — a particle that died mid-run would leave a shorter live span and the
        // assertion below would be reading whatever the pool had not yet overwritten.
        life.SetValue("Minimum", 30f);
        life.SetValue("Maximum", 30f);

        graph.Add("Vfx/Initialize/Position").SetValue("Position", 1f, 2f, 3f);

        var cone = graph.Add("Vfx/Initialize/Velocity in Cone");

        cone.SetValue("Axis", 0f, 0f, 1f);
        cone.SetValue("Angle", 0.25f);
        cone.SetValue("Minimum", 4f);
        cone.SetValue("Maximum", 6f);

        var roll = graph.Add("Vfx/Initialize/Rotation");

        roll.SetValue("Minimum", 0.5f);
        roll.SetValue("Maximum", 0.5f);

        var spin = graph.Add("Vfx/Initialize/Angular Velocity");

        // ⚠ A degenerate range on purpose: `SetRotation` and `SetAngularVelocity` randomize per
        // particle, so a spread would make the assertion below a bound rather than a value, and a
        // bound wide enough to hold is one a broken integration would also satisfy.
        spin.SetValue("Minimum", 2f);
        spin.SetValue("Maximum", 2f);

        graph.Add("Vfx/Update/Rotate");
        graph.Add("Vfx/Update/Integrate");

        var result = new VfxGraphCompiler(Library()).Compile(graph);

        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var initializers = result.Value.Graph.Initializers;

        // A.xyz is the point.
        Assert.Equal(new(1f, 2f, 3f, 0f), Assert.Single(initializers, one => one.Opcode == VfxOpcode.SetPosition).A);

        // A.xyz is the axis and A.w the half-angle; B.x and B.y are the speed range.
        var aimed = Assert.Single(initializers, one => one.Opcode == VfxOpcode.VelocityInCone);

        Assert.Equal(new(0f, 0f, 1f, 0.25f), aimed.A);
        Assert.Equal(4f, aimed.B.X);
        Assert.Equal(6f, aimed.B.Y);

        Assert.Equal(2f, Assert.Single(initializers, one => one.Opcode == VfxOpcode.SetAngularVelocity).A.X);
        Assert.Single(result.Value.Graph.Updaters, one => one.Opcode == VfxOpcode.Rotate);

        using var system = new VfxSystem(result.Value.Graph);

        system.Step(1f / 60f);

        Assert.True(system.Count > 0, "the burst spawned nothing.");

        var spawned = system.Count;

        // ⚠ Exactly the authored roll on the step it was born, and this is a fact about the runtime
        // rather than a rounding allowance: the updaters run over the particles that were already
        // alive, so one spawned this step is initialized and not yet advanced.
        Assert.All(system.Particles.Rotation[..spawned].ToArray(), angle => Assert.Equal(0.5f, angle, 4));

        // A whole second of it, so what is asserted is the rate rather than one step of it.
        for (var step = 0; step < 60; step++) {
            system.Step(1f / 60f);
        }

        Assert.Equal(spawned, system.Count);
        Assert.All(system.Particles.Rotation[..spawned].ToArray(), angle => Assert.Equal(2.5f, angle, 3));

        Compiles(result.Value.Shader.Source);
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

            var node = graph.Add(path);

            // ⚠ The custom-attribute blocks and the ribbon hold a *name*, and a name has no useful
            // default — an attribute called nothing cannot be a binding in the emitted shader, which
            // is why the compiler refuses one rather than inventing it. One name for all four, so
            // they share a slot and the ribbon's lookup finds what the blocks declared.
            if (node.Type.Contains("Custom", StringComparison.Ordinal) || node.Type.EndsWith("Ribbon", StringComparison.Ordinal)) {
                node.SetText("Attribute", "strip");
            }
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
