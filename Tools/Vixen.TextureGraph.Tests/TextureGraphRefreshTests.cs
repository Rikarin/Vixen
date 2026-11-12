// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using CsCheck;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A value edit answered without a compile, held to what a compile would have said.</summary>
/// <remarks>
///     <para>
///         <b>No device, and that is why it can reach the whole library.</b> The compiler is its own
///         oracle: the plan <see cref="TextureGraphCompiler.Refresh(Vixen.NodeGraph.NodeGraphModel)" /> patches together
///         has to equal,
///         op for op, the plan a fresh compiler builds from the same graph. There is no second idea of
///         what a node emits, because the node's own <c>Compile</c> is what ran both times.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Null is a correct answer, so the property alone would pass for a method that always
///             returned it.
///         </b>
///         <see cref="Every_node_refreshes_to_what_a_compile_would_emit" /> counts how
///         often it did not.
///     </para>
/// </remarks>
public class TextureGraphRefreshTests(ITestOutputHelper output) {
    const int Side = 64;

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static TextureGraphCompiler Compiler(NodeTypeRegistry registry) =>
        new(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7, PreviewEveryNode = true };

    /// <summary>Everything about an op a picture can depend on, as text a failure can print.</summary>
    static string Describe(TexturePlan plan) =>
        string.Join(
            "\n",
            plan.Ops.Select(static op => $"{op.Kernel} {op.Cpu?.GetType().Name} {op.Identity} {op.Output} "
                + $"[{string.Join(",", op.Inputs)}] {op.EmittedForExtent} "
                + $"[{string.Join(",", op.Parameters.Select(static parameter => $"{parameter.Name}={parameter.Value:R}/{parameter.Unit}"))}]"
            )
        )
        + $"\nseed {plan.Seed}, kernels {string.Join(",", plan.Kernels.Keys.Order(StringComparer.Ordinal))}";

    /// <summary>A graph holding one node of a type, with every image port fed.</summary>
    static (NodeGraphModel Graph, GraphNode Node) Around(NodeTypeDefinition type) {
        NodeGraphModel graph = new();
        var node = graph.Add(type.Path);

        foreach (var port in type.Ports) {
            if (port is not { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                continue;
            }

            var source = graph.Add("Source/Noise");

            graph.Connect(new(source.Id, "Out"), new(node.Id, port.Name));
        }

        return (graph, node);
    }

    /// <summary>The ports an author has a number on.</summary>
    static PortDefinition[] Numbers(NodeTypeDefinition type) => [
        ..type.Ports.Where(static port => port is {
                Direction: PortDirection.Input, Kind: PortKind.Float or PortKind.Int or PortKind.Bool
            }
        )
    ];

    static float Jiggle(PortDefinition port, float current, Random random) =>
        port.Kind switch {
            PortKind.Bool => current == 0f ? 1f : 0f,
            PortKind.Int => Math.Max(1f, MathF.Round(current) + random.Next(-1, 3)),
            _ => current == 0f
                ? (float)random.NextDouble()
                : current * (0.25f + 1.5f * (float)random.NextDouble())
        };

    [Fact]
    public void Every_node_refreshes_to_what_a_compile_would_emit() {
        var registry = Registry();
        var answered = 0;
        var refused = new Dictionary<string, int>(StringComparer.Ordinal);

        void Check(NodeTypeDefinition type, int seed) {
            var (graph, node) = Around(type);
            var compiler = Compiler(registry);

            if (compiler.Compile(graph).HasErrors) {
                // A node that needs more than its image ports fed: an asset, a mesh map, a setting.
                // The refresh of a compile that failed is the null a later test asserts.
                return;
            }

            var random = new Random(seed);
            var numbers = Numbers(type);

            if (numbers.Length == 0) {
                return;
            }

            for (var step = 0; step < 6; step++) {
                var port = numbers[random.Next(numbers.Length)];
                var current = node.Values.TryGetValue(port.Name, out var lanes) && lanes.Length > 0
                    ? lanes[0]
                    : port.Default.IsDefaultOrEmpty
                        ? 0f
                        : port.Default[0];

                node.SetValue(port.Name, Jiggle(port, current, random));

                var refreshed = compiler.Refresh(graph);

                if (refreshed is null) {
                    refused[type.Path] = refused.GetValueOrDefault(type.Path) + 1;

                    // Null means compile, and the compile that follows has to leave this compiler
                    // able to answer the next edit. A compile that fails ends the walk.
                    if (compiler.Compile(graph).HasErrors) {
                        return;
                    }

                    continue;
                }

                var fresh = Compiler(registry).Compile(graph);

                Assert.False(fresh.HasErrors, $"{type.Path}: a refresh answered a graph a compile refuses.");
                Assert.Equal(Describe(fresh.Value), Describe(refreshed));
                Assert.Equal(fresh.Value.Images, refreshed.Images);
                Assert.Equal(fresh.Value.Outputs, refreshed.Outputs);
                answered++;
            }
        }

        foreach (var type in registry.Types) {
            Gen.Int.Sample(seed => Check(type, seed), iter: 4, threads: 1);
        }

        output.WriteLine(
            $"{answered} edits answered without a compile; refused: "
            + string.Join(
                ", ",
                refused.OrderBy(static entry => entry.Key).Select(static entry => $"{entry.Key} ×{entry.Value}")
            )
        );

        // ⚠ The false half of a property whose null is always legal: a refresh that gave up on
        // everything would pass every assertion above. The library has dozens of nodes with numbers.
        Assert.True(answered > 300, $"Only {answered} edits were answered, so this proved almost nothing.");
    }

    [Fact]
    public void A_refresh_shares_the_plan_and_runs_no_compile() {
        var registry = Registry();
        var compiler = Compiler(registry);
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var levels = graph.Add("Colour/Levels");

        graph.Connect(new(noise.Id, "Out"), new(levels.Id, "Input"));

        var plan = compiler.Compile(graph).Value;
        var ops = plan.Ops.Length;

        levels.SetValue("Gamma", 0.5f);

        var refreshed = compiler.Refresh(graph);

        Assert.NotNull(refreshed);
        Assert.NotSame(plan, refreshed);
        Assert.Equal(1, compiler.Refreshes);
        Assert.Equal(ops, refreshed.Ops.Length);
        Assert.Equal(0.5f, refreshed.Ops[^1].Find("gamma")!.Value.Value);
        Assert.Equal(plan.Ops[^1].Identity, refreshed.Ops[^1].Identity);

        // The op that was not edited is the same object, so nothing about it was rebuilt.
        Assert.Same(plan.Ops[0], refreshed.Ops[0]);
    }

    [Fact]
    public void Nothing_moved_is_the_same_plan() {
        var compiler = Compiler(Registry());
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");

        var plan = compiler.Compile(graph).Value;

        noise.Position = new(40f, 40f);

        Assert.Same(plan, compiler.Refresh(graph));
    }

    [Fact]
    public void The_seed_moves_without_a_compile() {
        var compiler = Compiler(Registry());
        NodeGraphModel graph = new();

        graph.Add("Source/Noise");
        TextureGraphSettings.Declare(graph, Side, Side, 1);

        var plan = compiler.Compile(graph).Value;

        TextureGraphSettings.Declare(graph, Side, Side, 2);

        var refreshed = compiler.Refresh(graph);

        Assert.NotNull(refreshed);
        Assert.Equal(1u, plan.Seed);
        Assert.Equal(2u, refreshed.Seed);
        Assert.Equal(plan.Ops, refreshed.Ops);
    }

    /// <summary>Everything that is not a number, each of which has to come back as "compile".</summary>
    public static TheoryData<string> Structure() =>
        ["wire", "new node", "removed node", "setting", "expression", "base resolution", "bake level", "argument"];

    [Theory]
    [MemberData(nameof(Structure))]
    public void Anything_but_a_number_is_a_compile(string edit) {
        var compiler = Compiler(Registry());
        NodeGraphModel graph = new();
        var first = graph.Add("Source/Noise");
        var second = graph.Add("Source/Checker");
        var blend = graph.Add("Colour/Blend");

        graph.Connect(new(first.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(first.Id, "Out"), new(blend.Id, "Foreground"));

        compiler.Compile(graph);

        // The instrument: untouched, this is an answer rather than a null.
        Assert.NotNull(compiler.Refresh(graph));

        switch (edit) {
            case "wire":
                graph.Connect(new(second.Id, "Out"), new(blend.Id, "Foreground"));

                break;

            case "new node":
                graph.Add("Source/Uniform");

                break;

            case "removed node":
                graph.Remove(second.Id, out _);

                break;

            case "setting":
                blend.SetText("Mode", "Multiply");

                break;

            case "expression":
                blend.SetText("=Opacity", "0.5");

                break;

            case "base resolution":
                graph.Settings[TextureGraphSettings.BaseWidth] = "128";

                break;

            case "bake level":
                compiler.BakeLevelOffset = 1;

                break;

            case "argument":
                compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1" };

                break;
        }

        Assert.Null(compiler.Refresh(graph));
    }

    /// <summary>The number the replay exists for: it arrives on a port and it is a list of images.</summary>
    [Fact]
    public void A_number_that_changes_what_a_node_emits_is_a_compile() {
        var compiler = Compiler(Registry());
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var flood = graph.Add("Analysis/Flood Fill");

        graph.Connect(new(noise.Id, "Out"), new(flood.Id, "Mask"));

        var plan = compiler.Compile(graph).Value;

        // One dispatch and one scratch image per iteration, so this number is structure.
        flood.SetValue("Iterations", 8f);

        Assert.Null(compiler.Refresh(graph));

        var recompiled = compiler.Compile(graph).Value;

        Assert.NotEqual(plan.Images.Length, recompiled.Images.Length);

        // The compile re-armed it, and the number beside it is only a uniform.
        flood.SetValue("Threshold", 0.25f);

        Assert.NotNull(compiler.Refresh(graph));
    }

    /// <summary>Everything a compound can have moved in it, around one published graph.</summary>
    /// <remarks>
    ///     <c>Compound/Tinted</c> takes one number on a port and two knobs, <c>amount</c> and
    ///     <c>Mode</c>: <c>Source/Uniform → Levels → Blend → out</c>, with an expression on the levels
    ///     and a <c>$Mode</c> on the blend.
    /// </remarks>
    sealed class Compound {
        public readonly TextureGraphCompiler Compiler;
        public readonly GraphNode Instance;
        public readonly NodeGraphModel Graph = new();
        public readonly NodeGraphModel Inner = new();
        public readonly GraphNode Levels;
        readonly NodeTypeRegistry registry = Registry();
        readonly TextureGraphLibrary library = new();

        public Compound() {
            var entry = Inner.Add(SubGraphs.InputType);
            var source = Inner.Add("Source/Noise");
            var outlet = Inner.Add(SubGraphs.OutputType);
            var blend = Inner.Add("Colour/Blend");

            Levels = Inner.Add("Colour/Levels");
            Inner.Interface.Add(new("Scale", PortDirection.Input, PortKind.Float, [4f]));
            Inner.Interface.Add(new("Out", PortDirection.Output, PortKind.Image));
            Inner.Connect(new(entry.Id, "Scale"), new(source.Id, "Scale"));
            Inner.Connect(new(source.Id, "Out"), new(Levels.Id, "Input"));
            Inner.Connect(new(Levels.Id, "Out"), new(blend.Id, "Background"));
            Inner.Connect(new(source.Id, "Out"), new(blend.Id, "Foreground"));
            Inner.Connect(new(blend.Id, "Out"), new(outlet.Id, "Out"));
            Levels.SetText("=Gamma", "amount * 2f");
            blend.SetText("Mode", "$Mode");

            library.Publish(
                "Compound/Tinted",
                Inner,
                [
                    new("amount", Default: 0.5f, Minimum: 0f, Maximum: 4f),
                    new("Mode", TextureGraphParameterKind.Name, Choice: "Multiply", Accepted: ["Multiply", "Add"])
                ],
                registry
            );

            Compiler = Fresh();
            Instance = Graph.Add("Compound/Tinted");
            Assert.Empty(Compiler.Compile(Graph).Diagnostics);
        }

        public TextureGraphCompiler Fresh() {
            var fresh = TextureGraphRefreshTests.Compiler(registry);

            fresh.SubGraphSource = library;

            return fresh;
        }

        /// <summary>A refresh has to happen, without a compile, and has to be what a compile says.</summary>
        public void AssertRefreshes() {
            var refreshes = Compiler.Refreshes;
            var refreshed = Compiler.Refresh(Graph);

            Assert.NotNull(refreshed);
            Assert.Equal(refreshes + 1, Compiler.Refreshes);

            var fresh = Fresh();

            fresh.Arguments = Compiler.Arguments;

            var expected = fresh.Compile(Graph);

            Assert.False(expected.HasErrors);
            Assert.Equal(Describe(expected.Value), Describe(refreshed));
        }
    }

    [Fact]
    public void A_number_beside_a_compound_refreshes() {
        var compound = new Compound();
        var uniform = compound.Graph.Add("Source/Uniform");

        Assert.Empty(compound.Compiler.Compile(compound.Graph).Diagnostics);
        uniform.SetValue("Colour", 0.5f);

        compound.AssertRefreshes();
    }

    [Fact]
    public void A_number_on_a_compound_reaches_the_node_inside_it_without_a_compile() {
        var compound = new Compound();
        var before = compound.Compiler.Refresh(compound.Graph)!;

        compound.Instance.SetValue("Scale", 9f);
        compound.AssertRefreshes();

        Assert.NotEqual(Describe(before), Describe(compound.Compiler.Refresh(compound.Graph)!));
    }

    /// <summary>⚠ A knob reaches an inner port by an expression, so moving one has to fold it again.</summary>
    [Fact]
    public void An_exposed_number_on_a_compound_folds_again_without_a_compile() {
        var compound = new Compound();
        var before = compound.Compiler.Refresh(compound.Graph)!;

        compound.Instance.SetText("amount", "1.5");
        compound.AssertRefreshes();

        Assert.NotEqual(Describe(before), Describe(compound.Compiler.Refresh(compound.Graph)!));
    }

    /// <summary>A name knob is a uniform on a blend, and asking the node again finds that out.</summary>
    [Fact]
    public void An_exposed_name_on_a_compound_is_answered_by_the_node_it_reaches() {
        var compound = new Compound();

        compound.Instance.SetText("Mode", "Add");

        compound.AssertRefreshes();
    }

    [Fact]
    public void A_number_inside_a_compound_is_read_on_the_next_refresh() {
        var compound = new Compound();

        compound.Levels.SetValue("Output White", 0.25f);

        compound.AssertRefreshes();
    }

    [Fact]
    public void An_argument_is_a_number_and_it_reaches_the_authors_own_expressions() {
        var registry = Registry();
        var compiler = Compiler(registry);
        NodeGraphModel graph = new();
        var levels = graph.Add("Colour/Levels");

        graph.Connect(new(graph.Add("Source/Noise").Id, "Out"), new(levels.Id, "Input"));
        levels.SetText("=Gamma", "amount * 2f");
        compiler.Parameters.Add(new("amount", Default: 1f, Minimum: 0f, Maximum: 4f));

        Assert.Empty(compiler.Compile(graph).Diagnostics);

        compiler.Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["amount"] = "2" };

        var refreshed = compiler.Refresh(graph);
        var fresh = Compiler(registry);

        fresh.Parameters.Add(new("amount", Default: 1f, Minimum: 0f, Maximum: 4f));
        fresh.Arguments = compiler.Arguments;

        Assert.NotNull(refreshed);
        Assert.Equal(Describe(fresh.Compile(graph).Value), Describe(refreshed));
    }

    /// <summary>Only a compile can say a value was refused, so a refused value has to be a compile.</summary>
    [Fact]
    public void A_knob_out_of_its_range_is_a_compile_and_the_compile_says_so() {
        var compound = new Compound();

        compound.Instance.SetText("amount", "40");

        Assert.Null(compound.Compiler.Refresh(compound.Graph));

        var compilation = compound.Compiler.Compile(compound.Graph);

        Assert.Contains(
            compilation.Diagnostics,
            static diagnostic => diagnostic.Message.Contains("outside its range", StringComparison.Ordinal)
        );
    }

    /// <summary>⚠ A name is only read during a replay, so a refused one is said there, and still has to compile.</summary>
    [Fact]
    public void A_name_knob_a_node_cannot_take_is_a_compile_and_the_compile_says_so() {
        var compound = new Compound();

        compound.Instance.SetText("Mode", "Nope");

        Assert.Null(compound.Compiler.Refresh(compound.Graph));
        Assert.Contains(
            compound.Compiler.Compile(compound.Graph).Diagnostics,
            static diagnostic => diagnostic.Id == TextureDiagnostics.NameKnobNotResolved
        );
    }

    /// <summary>A knob's text is the only text a refresh takes on a sub-graph node.</summary>
    [Fact]
    public void An_expression_on_a_compound_is_a_compile() {
        var compound = new Compound();

        compound.Instance.SetText("=Scale", "2f");

        Assert.Null(compound.Compiler.Refresh(compound.Graph));
    }

    [Fact]
    public void A_compounds_contents_changing_shape_is_a_compile() {
        var compound = new Compound();

        compound.Inner.Add("Source/Uniform");

        Assert.Null(compound.Compiler.Refresh(compound.Graph));
    }

    /// <summary>⚠ Null says "not this graph", and leaves the compilation able to answer the last one.</summary>
    [Fact]
    public void A_refresh_refused_inside_a_compound_leaves_the_compilation_standing() {
        var compound = new Compound();
        var before = compound.Compiler.Refresh(compound.Graph)!;

        compound.Instance.SetText("amount", "40");
        Assert.Null(compound.Compiler.Refresh(compound.Graph));

        compound.Instance.SetText("amount", "0.5");

        var refreshed = compound.Compiler.Refresh(compound.Graph);

        Assert.NotNull(refreshed);
        Assert.Equal(Describe(before), Describe(refreshed));
    }

    /// <summary>
    ///     ⚠ An inlining runs on every value edit, so an expression on a sub-graph node's port would
    ///     be a Raven compilation per slider position if nothing remembered it.
    /// </summary>
    [Fact]
    public void A_number_beside_an_expression_on_a_compound_compiles_no_expression() {
        var compound = new Compound();
        var uniform = compound.Graph.Add("Source/Uniform");

        compound.Instance.SetText("=Scale", "2f + 1f");
        Assert.Empty(compound.Compiler.Compile(compound.Graph).Diagnostics);

        var compilations = compound.Compiler.ExpressionCompilations;

        uniform.SetValue("Colour", 0.5f);
        compound.AssertRefreshes();

        Assert.Equal(compilations, compound.Compiler.ExpressionCompilations);
    }

    /// <summary>The library, held to a compile: every shipped compound, its numbers and its knobs.</summary>
    [Fact]
    public void Every_compound_refreshes_to_what_a_compile_would_emit() {
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, null, out _);
        var answered = 0;
        var knobs = 0;

        TextureGraphCompiler Make() {
            var compiler = Compiler(registry);

            compiler.SubGraphSource = library;

            return compiler;
        }

        void Check(NodeTypeDefinition type, int seed) {
            var (graph, node) = Around(type);
            var compiler = Make();

            if (compiler.Compile(graph).HasErrors) {
                return;
            }

            var random = new Random(seed);
            var numbers = Numbers(type);

            for (var step = 0; step < 4; step++) {
                var settings = type.Settings
                    .Where(static setting => setting.Kind is SettingKind.Float or SettingKind.Int)
                    .ToArray();
                var turnKnob = settings.Length > 0 && (numbers.Length == 0 || random.Next(2) == 0);

                if (turnKnob) {
                    var setting = settings[random.Next(settings.Length)];
                    var low = float.IsFinite(setting.Minimum) ? setting.Minimum : 0f;
                    var high = float.IsFinite(setting.Maximum) ? setting.Maximum : 1f;

                    node.SetText(
                        setting.Name,
                        ((float)(random.NextDouble() * (high - low) + low)).ToString(
                            "R",
                            CultureInfo.InvariantCulture
                        )
                    );
                    knobs++;
                } else if (numbers.Length > 0) {
                    var port = numbers[random.Next(numbers.Length)];
                    var current = node.Values.TryGetValue(port.Name, out var lanes) && lanes.Length > 0
                        ? lanes[0]
                        : port.Default.IsDefaultOrEmpty
                            ? 0f
                            : port.Default[0];

                    node.SetValue(port.Name, Jiggle(port, current, random));
                } else {
                    return;
                }

                var refreshed = compiler.Refresh(graph);

                if (refreshed is null) {
                    if (compiler.Compile(graph).HasErrors) {
                        return;
                    }

                    continue;
                }

                var fresh = Make().Compile(graph);

                Assert.False(fresh.HasErrors, $"{type.Path}: a refresh answered a graph a compile refuses.");
                Assert.Equal(Describe(fresh.Value), Describe(refreshed));
                Assert.Equal(fresh.Value.Images, refreshed.Images);
                Assert.Equal(fresh.Value.Outputs, refreshed.Outputs);
                answered++;
            }
        }

        foreach (var path in TextureCompoundLibrary.Shipped) {
            Gen.Int.Sample(seed => Check(registry.Get(path), seed), iter: 2, threads: 1);
        }

        output.WriteLine($"{answered} edits on compounds answered without a compile, {knobs} of them knobs.");

        Assert.True(answered > 100, $"Only {answered} edits were answered, so this proved almost nothing.");
        Assert.True(knobs > 20, $"Only {knobs} knobs were moved.");
    }

    [Fact]
    public void Another_graph_compiled_in_between_is_a_compile() {
        var compiler = Compiler(Registry());
        NodeGraphModel graph = new();
        NodeGraphModel other = new();
        var noise = graph.Add("Source/Noise");

        other.Add("Source/Checker");

        compiler.Compile(graph);
        compiler.Compile(other);
        noise.SetValue("Scale", 3f);

        Assert.Null(compiler.Refresh(graph));
        Assert.NotNull(compiler.Refresh(other));
    }

    [Fact]
    public void A_compile_that_failed_leaves_nothing_to_refresh() {
        var compiler = Compiler(Registry());
        NodeGraphModel graph = new();

        graph.Add("Colour/Levels");

        Assert.True(compiler.Compile(graph).HasErrors);
        Assert.Null(compiler.Refresh(graph));
    }

    [Fact]
    public void A_copy_of_a_plan_carries_everything_the_plan_has() {
        var plan = new TexturePlan {
            BaseWidth = 128,
            BaseHeight = 64,
            BakeLevelOffset = 1,
            Seed = 99,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Uniform", Output = 0, Parameters = [new("r", 0.5f)] }],
            Outputs = [0],
            Kernels = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("X", "source")
        };

        var copy = plan.WithValues([new(0, "r", 0.25f)]);

        Assert.Equal(0.25f, copy.Ops[0].Find("r")!.Value.Value);

        foreach (var property in typeof(TexturePlan).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.Name == nameof(TexturePlan.Ops)) {
                continue;
            }

            Assert.Equal(property.GetValue(plan), property.GetValue(copy));
        }
    }

    [Fact]
    public void A_value_the_plan_cannot_take_is_refused() {
        var plan = new TexturePlan {
            BaseWidth = 4,
            BaseHeight = 4,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Uniform", Output = 0, Parameters = [new("r", 0.5f)] }],
            Outputs = [0]
        };

        Assert.Throws<ArgumentException>(() => plan.WithValues([new(0, "nope", 1f)]));
        Assert.Throws<ArgumentException>(() => plan.WithValues([new(1, "r", 1f)]));
    }
}
