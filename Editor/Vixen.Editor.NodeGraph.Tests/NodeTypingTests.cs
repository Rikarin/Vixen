// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Editor.NodeGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What the generator produced, and what the compiler makes of it: <c>DynamicVector</c> resolution
///     and the binding — doc 11 § node graphs' type-resolution tests.
/// </summary>
public class NodeTypingTests {
    /// <summary>A compiler that writes what it was given, so a test can read the resolution.</summary>
    /// <remarks>
    ///     The smallest possible subclass: it exists to expose what the base class decided, which is
    ///     the thing under test. A real one is <c>ShaderGraphCompiler</c>.
    /// </remarks>
    sealed class Recording(NodeTypeRegistry registry) : NodeGraphCompiler<List<string>>(registry) {
        readonly List<string> lines = [];

        /// <summary>What each node's dynamic ports resolved to, by identity.</summary>
        public Dictionary<NodeId, PortKind> Resolved { get; } = [];

        protected override void Begin(NodeGraphModel graph) {
            lines.Clear();
            Resolved.Clear();
        }

        protected override void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding) {
            Resolved[node.Id] = binding.Resolved;

            foreach (var port in definition.Ports) {
                if (port.Direction == PortDirection.Input) {
                    lines.Add($"{node.Id}.{port.Name} = {binding.Input(port.Name)}");
                }
            }
        }

        protected override List<string>? Finish(NodeGraphModel graph) => lines;

        protected override string Constant(ReadOnlySpan<float> value, PortKind kind) {
            var lanes = PortKinds.Lanes(kind);

            if (lanes <= 1) {
                return (value.Length > 0 ? value[0] : 0f).ToString("R", CultureInfo.InvariantCulture);
            }

            var text = new StringBuilder("[");

            for (var index = 0; index < lanes; index++) {
                var lane = value.Length == 0 ? 0f : value[Math.Min(index, value.Length - 1)];

                text.Append(index > 0 ? ", " : "").Append(lane.ToString("R", CultureInfo.InvariantCulture));
            }

            return text.Append(']').ToString();
        }

        protected override string Convert(string expression, PortKind from, PortKind target) =>
            $"({target}){expression}";
    }

    static NodeTypeRegistry Library() {
        var registry = new NodeTypeRegistry();

        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>The generator read the declaration, and this is what it made of it.</summary>
    [Fact]
    public void A_marked_class_becomes_a_node_type() {
        var definition = Library().Get("Test/Combine");

        Assert.Equal("Combine", definition.Title);
        Assert.Equal("Test", definition.Category);
        Assert.True(definition.Preview);
        Assert.Equal("Two values, one result.", definition.Summary);

        Assert.Equal(
            [("A", PortDirection.Input), ("B", PortDirection.Input), ("Out", PortDirection.Output)],
            definition.Ports.Select(port => (port.Name, port.Direction))
        );

        Assert.All(definition.Ports, port => Assert.Equal(PortKind.Dynamic, port.Kind));

        // The field initializer, read from the source rather than evaluated.
        Assert.Equal([0.25f], definition.Port("A", PortDirection.Input)!.Default);
        Assert.Empty(definition.Port("B", PortDirection.Input)!.Default);
    }

    /// <summary>A port may be called something its field cannot be, and carry a vector default.</summary>
    [Fact]
    public void A_port_can_be_named_and_defaulted_from_the_attribute() {
        var port = Library().Get("Test/Named").Port("Base Colour", PortDirection.Input);

        Assert.NotNull(port);
        Assert.Equal(PortKind.Float3, port.Kind);
        Assert.Equal([0.1f, 0.2f, 0.3f], port.Default);
        Assert.Equal("What it starts as.", port.Summary);
    }

    /// <summary>Nothing connected: a dynamic node is a float, which promotes into anything later.</summary>
    [Fact]
    public void An_unconnected_dynamic_node_is_a_float() {
        var graph = new NodeGraphModel();
        var node = graph.Add("Test/Combine");
        var compiler = new Recording(Library());

        compiler.Compile(graph);

        Assert.Equal(PortKind.Float, compiler.Resolved[node.Id]);
    }

    /// <summary>The widest connected input wins, and the narrow one is promoted to meet it.</summary>
    [Fact]
    public void The_widest_input_decides_what_a_dynamic_node_is() {
        var graph = new NodeGraphModel();
        var scalar = graph.Add("Test/Constant");
        var vector = graph.Add("Test/Vector");
        var node = graph.Add("Test/Combine");

        graph.Connect(new(scalar.Id, "Out"), new(node.Id, "A"));
        graph.Connect(new(vector.Id, "Out"), new(node.Id, "B"));

        var compiler = new Recording(Library());
        var lines = compiler.Compile(graph).Value;

        Assert.Equal(PortKind.Float3, compiler.Resolved[node.Id]);
        Assert.Contains($"{node.Id}.A = (Float3)n{scalar.Id.Value}_Out", lines);
        Assert.Contains($"{node.Id}.B = n{vector.Id.Value}_Out", lines);
    }

    /// <summary>And the resolution travels: a dynamic output is as wide as its own node became.</summary>
    [Fact]
    public void A_dynamic_output_carries_what_its_node_resolved_to() {
        var graph = new NodeGraphModel();
        var colour = graph.Add("Test/Colour");
        var first = graph.Add("Test/Combine");
        var second = graph.Add("Test/Combine");

        graph.Connect(new(colour.Id, "Out"), new(first.Id, "A"));
        graph.Connect(new(first.Id, "Out"), new(second.Id, "A"));

        var compiler = new Recording(Library());

        compiler.Compile(graph);

        Assert.Equal(PortKind.Float4, compiler.Resolved[first.Id]);
        Assert.Equal(PortKind.Float4, compiler.Resolved[second.Id]);
    }

    /// <summary>A texture is not a width, so a dynamic port refuses one rather than widening it.</summary>
    [Fact]
    public void A_texture_arriving_at_a_dynamic_port_is_a_type_error() {
        var graph = new NodeGraphModel();
        var texture = graph.Add("Test/Texture");
        var node = graph.Add("Test/Combine");

        graph.Connect(new(texture.Id, "Out"), new(node.Id, "A"));

        var result = new Recording(Library()).Compile(graph);

        Assert.False(result.Succeeded);

        var diagnostic = Assert.Single(result.Diagnostics, entry => entry.Id == "NG0003");

        Assert.Equal(node.Id, diagnostic.Node);
        Assert.Equal("A", diagnostic.Port);
    }

    /// <summary>An unconnected scalar default splats to whatever its node resolved to.</summary>
    /// <remarks>
    ///     What every shader language does for <c>v * s</c>, and what an author who typed one number
    ///     into a port that turned out to be a colour means.
    /// </remarks>
    [Fact]
    public void A_scalar_default_splats_to_the_resolved_width() {
        var graph = new NodeGraphModel();
        var vector = graph.Add("Test/Vector");
        var node = graph.Add("Test/Combine");

        graph.Connect(new(vector.Id, "Out"), new(node.Id, "B"));

        var lines = new Recording(Library()).Compile(graph).Value;

        Assert.Contains($"{node.Id}.A = [0.25, 0.25, 0.25]", lines);
    }

    /// <summary>An inline value the author typed beats the type's default.</summary>
    [Fact]
    public void An_inline_value_wins_over_the_types_default() {
        var graph = new NodeGraphModel();
        var node = graph.Add("Test/Combine");

        node.SetValue("A", 7f);

        var lines = new Recording(Library()).Compile(graph).Value;

        Assert.Contains($"{node.Id}.A = 7", lines);
    }

    /// <summary>Two different types cannot claim one path, because a saved graph names them by it.</summary>
    [Fact]
    public void One_path_means_one_node_type() {
        var registry = Library();
        var impostor = new NodeTypeDefinition("Test/Combine", [], static () => new TestConstantNode());

        Assert.Throws<ArgumentException>(() => registry.Add(impostor));
    }

    /// <summary>Registering the same definition twice is what two libraries sharing one node does.</summary>
    [Fact]
    public void Registering_the_same_type_twice_is_harmless() {
        var registry = Library();
        var before = registry.Count;

        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

        Assert.Equal(before, registry.Count);
    }

    /// <summary>A <c>[Setting]</c> becomes a declaration of its own, beside the ports and not in them.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a tenth <c>PortKind</c>.</b> A setting has no direction and no socket, so a
    ///     consumer that walks <c>Ports</c> must not find one — otherwise every such consumer has to
    ///     remember which kinds cannot be wired.
    /// </remarks>
    [Fact]
    public void A_setting_is_declared_beside_the_ports_rather_than_among_them() {
        var definition = Library().Get("Test/Named Thing");

        Assert.Equal(["Weight", "Out"], definition.Ports.Select(port => port.Name));
        Assert.Equal(["Label", "Target Name"], definition.Settings.Select(setting => setting.Name));

        // The initializer is read, not evaluated — the same rule a port's default follows.
        Assert.Equal("unnamed", definition.Setting("Label")!.Default);
        Assert.Equal("What the thing is called.", definition.Setting("Label")!.Summary);

        // A renamed setting is stored under the name, and its field's name means nothing.
        Assert.Equal("", definition.Setting("Target Name")!.Default);
        Assert.Null(definition.Setting("Target"));
    }

    /// <summary>A setting reaches the node it was typed on, and its default reaches one that was not.</summary>
    [Fact]
    public void A_setting_reaches_the_bound_node() {
        var graph = new NodeGraphModel();
        var typed = graph.Add("Test/Named Thing");
        var untouched = graph.Add("Test/Named Thing");

        typed.SetText("Label", "glow");

        Dictionary<NodeId, TestSettingNode> bound = [];
        var compiler = new Binding(Library(), bound);

        compiler.Compile(graph);

        Assert.Equal("glow", bound[typed.Id].Label);
        Assert.Equal("", bound[typed.Id].Target);

        // ⚠ The declared default, not an empty string: the compiler seeds it, so a node an author
        // never opened compiles to what its type says rather than to nothing.
        Assert.Equal("unnamed", bound[untouched.Id].Label);
    }

    /// <summary>A compiler that keeps the instances it bound, so a test can read their fields.</summary>
    sealed class Binding(NodeTypeRegistry registry, Dictionary<NodeId, TestSettingNode> bound)
        : NodeGraphCompiler<List<string>>(registry) {
        protected override void Visit(GraphNode node, NodeTypeDefinition definition, Node instance, NodeBinding binding) {
            if (instance is TestSettingNode setting) {
                bound[node.Id] = setting;
            }
        }

        protected override List<string> Finish(NodeGraphModel graph) => [];

        protected override string Constant(ReadOnlySpan<float> value, PortKind kind) => "";

        protected override string Convert(string expression, PortKind from, PortKind target) => expression;
    }
}
