// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Graphics.Vulkan;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Random edits to a random graph, each evaluated incrementally and compared byte for byte with
///     a clean whole-plan evaluation of the same graph on the same device.
/// </summary>
/// <remarks>
///     <para>
///         <b>The oracle is the whole-plan path, from a fresh compiler, on an evaluator whose result
///         cache has never been used.</b> It is not a second implementation: it is the same kernels
///         through the same dispatch code, asked without a past. So this says the only thing a cache
///         has to say: that having a past makes no difference. That path is the one the closed-form
///         suites hold to arithmetic.
///     </para>
///     <para>
///         ⚠ <b>Raw texels, not <see cref="TextureBake.Read(int)" />.</b> That rounds half-floats to eight
///         bits, and a stale result one 300th away from the right one would pass.
///     </para>
///     <para>
///         ⚠ <b>The edits are chosen so that each ingredient of a key can be the only thing that
///         moved</b>: a number with nothing else touched, a wire, the seed, the resolution, and a
///         Pixel Processor's expression, which changes a kernel and leaves its uniforms exactly as
///         they were. <see cref="Dropping_an_ingredient_from_the_key_fails_the_property" /> is what
///         says they do. Format and an external's digest cannot be reached from a graph alone and
///         are sabotaged by hand in <c>TextureIncrementalDeviceTests</c>, which says why.
///     </para>
/// </remarks>
public class TextureIncrementalPropertyTests(ITestOutputHelper output) {
    const int Side = 64;
    const int Steps = 14;
    const int MaximumNodes = 10;

    /// <summary>Every node type a graph with nothing but wires can hold.</summary>
    /// <remarks>
    ///     All of the library, less what needs a host: an asset, a mesh map, or an Output node, which
    ///     is left out because <c>PreviewEveryNode</c> already keeps every image and two of them
    ///     naming one usage is a refusal rather than a graph.
    /// </remarks>
    static string[] Pool(NodeTypeRegistry registry) =>
        [
            .. registry.Types
                .Select(type => type.Path)
                .Where(
                    path => path is not ("Source/Bitmap" or "Output/Output")
                        && !path.Contains("Mesh", StringComparison.Ordinal)
                )
                .Order(StringComparer.Ordinal)
        ];

    static readonly string[] Expressions = [
        "a",
        "a * 0.5",
        "a + float4(0.25, 0.0, 0.0, 0.0)",
        "float4(1.0, 1.0, 1.0, 1.0) - a",
        "float4(uv.x, uv.y, x, 1.0)"
    ];

    static int Iterations() {
        var multiplier = Environment.GetEnvironmentVariable("VIXEN_PROPERTY_ITER");

        return 48
            * (int.TryParse(multiplier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                    ? parsed
                    : 1);
    }

    [Fact]
    public void Any_edit_evaluated_incrementally_is_byte_identical_to_a_clean_evaluation() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var clean = new TexturePlanEvaluator(device);

        var registry = Registry();
        var totals = new Totals();

        Gen.Int.Sample(
            seed => {
                evaluator.Results.Budget = (seed & 3) switch {
                    0 => 0,
                    1 => 4L * Side * Side * 8,
                    _ => TextureResultCache.DefaultBudget
                };

                new Session(registry, device, evaluator, clean, seed).Run(totals);
            },
            iter: Iterations(),
            threads: 1
        );

        output.WriteLine(
            $"{TextureKernelHarness.Adapter(device)}: {totals.Compared} comparisons over {totals.Bytes} bytes; {totals.Refreshed} refreshed, "
            + $"{totals.Compiled} compiled, {totals.Refused} refused; {totals.Ran} ops ran of {totals.Ops} in the plans"
        );

        Assert.Equal(0, clean.Results.Hits + clean.Results.Misses);

        // ⚠ The guard on the guard: the generator drifting into graphs that never compile, or an
        // edit that never reuses anything, would leave every equality above passing on nothing.
        Assert.True(totals.Compared > 400, $"Only {totals.Compared} images were compared.");
        Assert.True(totals.Refreshed > 20, $"Only {totals.Refreshed} edits were answered without a compile.");
        Assert.True(totals.Ran < totals.Ops * 3 / 4, $"{totals.Ran} of {totals.Ops} ops ran, so nothing was reused.");
    }

    /// <summary>The property again, over graphs that are mostly compounds, with their knobs moving.</summary>
    /// <remarks>
    ///     ⚠ <b>Fewer of them, because one compound is a dozen ops</b>, and the guards are its own: an
    ///     edit that reaches inside a compound has to have been answered without a compile, or this
    ///     says nothing about a refresh.
    /// </remarks>
    [Fact]
    public void Any_edit_to_a_compound_evaluated_incrementally_is_byte_identical_to_a_clean_evaluation() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var clean = new TexturePlanEvaluator(device);

        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, null, out _);
        var totals = new Totals();

        Gen.Int.Sample(
            seed => new Session(registry, device, evaluator, clean, seed, library).Run(totals),
            iter: Iterations() / 4,
            threads: 1
        );

        output.WriteLine(
            $"{TextureKernelHarness.Adapter(device)}: {totals.Compared} comparisons over {totals.Bytes} bytes; {totals.Refreshed} refreshed "
            + $"({totals.RefreshedCompounds} with a compound, {totals.Knobs} knobs moved), {totals.Compiled} compiled, {totals.Refused} refused"
        );

        Assert.True(totals.Compared > 100, $"Only {totals.Compared} images were compared.");
        Assert.True(totals.RefreshedCompounds > 20, $"Only {totals.RefreshedCompounds} edits on compounds were answered without a compile.");
        Assert.True(totals.Knobs > 3, $"Only {totals.Knobs} knobs moved.");
    }

    /// <summary>Each ingredient the property reaches, which it has to notice going missing.</summary>
    public static TheoryData<int> Ingredients() =>
        new(
            (int)TextureKeyParts.Kernel,
            (int)TextureKeyParts.Parameters,
            (int)TextureKeyParts.Inputs,
            (int)TextureKeyParts.Resolution,
            (int)TextureKeyParts.Seed
        );

    /// <summary>The sabotage, kept: the property above, with one ingredient out of the hash.</summary>
    /// <remarks>
    ///     ⚠ <b>Seeds in a loop rather than <c>Sample</c></b>, because what is wanted here is the first
    ///     failure and CsCheck would spend the next minute shrinking it. Bounded, and the bound is
    ///     the test: a key that lost an ingredient for thirty sessions without a wrong picture means
    ///     the property cannot see that ingredient at all.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Ingredients))]
    public void Dropping_an_ingredient_from_the_key_fails_the_property(int ingredient) {
        var part = (TextureKeyParts)ingredient;

        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var clean = new TexturePlanEvaluator(device);

        var registry = Registry();

        evaluator.Results.Parts = TextureKeyParts.All & ~part;

        for (var seed = 1; seed <= 30; seed++) {
            evaluator.Results.Clear();

            try {
                new Session(registry, device, evaluator, clean, seed).Run(new());
            } catch (Exception failure) when (failure is Xunit.Sdk.EqualException or InvalidOperationException) {
                output.WriteLine($"Without {part}, session {seed} failed: {failure.Message[..Math.Min(160, failure.Message.Length)]}");

                return;
            }
        }

        Assert.Fail($"Thirty sessions with {part} out of the key drew no wrong picture, so the property cannot see it.");
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    sealed class Totals {
        public int Compared;
        public long Bytes;
        public int Refreshed;
        public int Compiled;
        public int Refused;
        public int RefreshedCompounds;
        public int Knobs;
        public int Ran;
        public int Ops;
    }

    /// <summary>One graph, edited step by step.</summary>
    sealed class Session(
        NodeTypeRegistry registry,
        VulkanDevice device,
        TexturePlanEvaluator evaluator,
        TexturePlanEvaluator clean,
        int seed,
        ISubGraphSource? library = null
    ) {
        readonly Random random = new(seed);

        // Mostly compounds when there is a library, and the rest of the pool is what feeds them.
        readonly string[] pool = Pool(registry);
        readonly string[] compounds = library is null
            ? []
            : [.. TextureCompoundLibrary.Shipped.Intersect(Pool(registry), StringComparer.Ordinal)];
        readonly NodeGraphModel graph = new();
        readonly List<GraphNode> nodes = [];
        int knobsMoved;
        readonly TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            PreviewEveryNode = true,
            SubGraphSource = library
        };

        public void Run(Totals totals) {
            TextureGraphSettings.Declare(graph, Side, Side, (uint)random.Next());

            var count = random.Next(3, MaximumNodes);

            for (var index = 0; index < count; index++) {
                AddNode();
            }

            for (var step = 0; step < Steps; step++) {
                Evaluate(totals);
                Edit();
            }

            Evaluate(totals);
        }

        /// <summary>Whether the graph as it stands compiles at all.</summary>
        /// <remarks>
        ///     ⚠ <b>An edit that breaks the graph is taken back, and without that nearly every graph
        ///     here is refused.</b> A random wire into a measuring port is a refusal, and one bad node
        ///     is every later step of the session. This suite's own guard is what found that: 681
        ///     refused steps against 39 evaluated, all of them green.
        /// </remarks>
        bool Holds() {
            var oracle = Oracle();

            // ⚠ And a compound holding a mesh map names an asset this suite has no way to supply.
            return !oracle.Compile(graph).HasErrors && oracle.Externals.All(static external => external.Asset.Length == 0);
        }

        TextureGraphCompiler Oracle() =>
            new(registry) {
                BaseWidth = Side,
                BaseHeight = Side,
                BakeLevelOffset = compiler.BakeLevelOffset,
                PreviewEveryNode = true,
                SubGraphSource = library
            };

        void AddNode() {
            for (var attempt = 0; attempt < 8; attempt++) {
                var type = compounds.Length > 0 && random.Next(3) > 0
                    ? compounds[random.Next(compounds.Length)]
                    : pool[random.Next(pool.Length)];
                var node = graph.Add(type);

                if (type == "Filters/Pixel Processor") {
                    node.SetText("Expression", Expressions[random.Next(Expressions.Length)]);
                }

                nodes.Add(node);

                foreach (var port in ImagePorts(node)) {
                    Wire(node, port);
                }

                if (Holds()) {
                    return;
                }

                graph.Remove(node.Id, out _);
                nodes.Remove(node);
            }
        }

        /// <summary>Feeds a port from any node added before this one, which cannot make a cycle.</summary>
        void Wire(GraphNode node, string port) {
            var position = nodes.IndexOf(node);

            if (position <= 0) {
                return;
            }

            var from = nodes[random.Next(position)];
            var outlets = registry.Get(from.Type).Ports
                .Where(candidate => candidate is { Direction: PortDirection.Output, Kind: PortKind.Image })
                .ToArray();

            if (outlets.Length > 0) {
                var outlet = outlets[random.Next(outlets.Length)];

                graph.TryConnect(new(from.Id, outlet.Name), new(node.Id, port), out _, out _);
            }
        }

        IEnumerable<string> ImagePorts(GraphNode node) =>
            registry.Get(node.Type).Ports
                .Where(port => port is { Direction: PortDirection.Input, Kind: PortKind.Image })
                .Select(port => port.Name);

        void Edit() {
            if (nodes.Count == 0) {
                AddNode();

                return;
            }

            var node = nodes[random.Next(nodes.Count)];
            var roll = random.Next(100);

            switch (roll) {
                case < 60:
                    Number(node);

                    break;

                case < 72: {
                    var ports = ImagePorts(node).ToArray();

                    if (ports.Length > 0) {
                        var port = ports[random.Next(ports.Length)];
                        var before = graph.Source(new(node.Id, port));

                        Wire(node, port);

                        if (!Holds() && before is { } restored) {
                            graph.Connect(restored, new(node.Id, port));
                        }
                    }

                    break;
                }

                case < 82:
                    TextureGraphSettings.Declare(graph, Side, Side, (uint)random.Next());

                    break;

                case < 90:
                    compiler.BakeLevelOffset = compiler.BakeLevelOffset == 0 ? 1 : 0;

                    if (!Holds()) {
                        compiler.BakeLevelOffset = compiler.BakeLevelOffset == 0 ? 1 : 0;
                    }

                    break;

                case < 95: {
                    var processor = nodes.Find(candidate => candidate.Type == "Filters/Pixel Processor");

                    if (processor is not null) {
                        var before = processor.TextOf("Expression");

                        processor.SetText("Expression", Expressions[random.Next(Expressions.Length)]);

                        if (!Holds()) {
                            processor.SetText("Expression", before);
                        }
                    }

                    break;
                }

                default:
                    if (nodes.Count < MaximumNodes) {
                        AddNode();
                    } else if (graph.Edges.All(edge => edge.From.Node != node.Id)) {
                        // Only a node nothing reads, or the edit is a hole in whatever did.
                        graph.Remove(node.Id, out _);
                        nodes.Remove(node);
                    }

                    break;
            }
        }

        /// <summary>Moves an exposed parameter on a compound, inside its range.</summary>
        bool Knob(GraphNode node) {
            var knobs = registry.Get(node.Type).Settings
                .Where(setting => setting.Kind is SettingKind.Float or SettingKind.Int)
                .ToArray();

            if (knobs.Length == 0 || random.Next(2) == 0) {
                return false;
            }

            var knob = knobs[random.Next(knobs.Length)];
            var low = float.IsFinite(knob.Minimum) ? knob.Minimum : 0f;
            var high = float.IsFinite(knob.Maximum) ? knob.Maximum : 1f;
            var before = node.TextOf(knob.Name);
            var value = low + ((high - low) * (float)random.NextDouble());

            node.SetText(knob.Name, value.ToString("R", CultureInfo.InvariantCulture));

            if (Holds()) {
                knobsMoved++;
            } else {
                node.SetText(knob.Name, before);
            }

            return true;
        }

        void Number(GraphNode node) {
            if (Knob(node)) {
                return;
            }

            var numbers = registry.Get(node.Type).Ports
                .Where(port => port is { Direction: PortDirection.Input, Kind: PortKind.Float or PortKind.Int or PortKind.Bool })
                .ToArray();

            if (numbers.Length == 0) {
                return;
            }

            var port = numbers[random.Next(numbers.Length)];
            var current = node.Values.TryGetValue(port.Name, out var lanes) && lanes.Length > 0
                ? lanes[0]
                : port.Default.IsDefaultOrEmpty ? 0f : port.Default[0];

            node.SetValue(
                port.Name,
                port.Kind switch {
                    PortKind.Bool => current == 0f ? 1f : 0f,
                    PortKind.Int => Math.Clamp(MathF.Round(current) + random.Next(-1, 2), 1f, 8f),
                    _ => current == 0f ? (float)random.NextDouble() : current * (0.5f + (float)random.NextDouble())
                }
            );

            if (!Holds()) {
                node.SetValue(port.Name, current);
            }
        }

        void Evaluate(Totals totals) {
            totals.Knobs += knobsMoved;
            knobsMoved = 0;

            var oracle = Oracle();
            var fresh = oracle.Compile(graph);

            if (fresh.HasErrors || fresh.Artefact is not { } expected || expected.Ops.Length == 0) {
                totals.Refused++;

                return;
            }

            var plan = compiler.Refresh(graph);

            if (plan is null) {
                plan = compiler.Compile(graph).Value;
                totals.Compiled++;
            } else {
                totals.Refreshed++;

                if (!compiler.Inlining.IsEmpty) {
                    totals.RefreshedCompounds++;
                }
            }

            Assert.Equal(expected.Images, plan.Images);
            Assert.Equal(expected.Outputs, plan.Outputs);

            using var uploads = new TextureUploads(device);
            using var cleanUploads = new TextureUploads(device);

            Assert.Empty(TextureGraphExternals.Upload(uploads, plan, compiler.Externals));
            Assert.Empty(TextureGraphExternals.Upload(cleanUploads, expected, oracle.Externals));

            using var incremental = evaluator.EvaluateIncremental(plan, uploads.Externals);
            using var whole = clean.Evaluate(expected, cleanUploads.Externals);

            foreach (var image in plan.Outputs) {
                if (plan.Images[image].External) {
                    continue;
                }

                var want = whole.ReadTexels(image);

                Assert.Equal(want, incremental.ReadTexels(image));

                totals.Compared++;
                totals.Bytes += want.Length;
            }

            totals.Ran += incremental.Ran.Length;
            totals.Ops += plan.Ops.Length;
        }
    }
}
