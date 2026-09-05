// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § D6's Pixel Processor: a Raven expression, the real compiler, and the diagnostics
///     mapped back — with no device anywhere.
/// </summary>
/// <remarks>
///     ⚠ <b>The strongest assertion here is not that a graph compiled; it is that the generated text
///     goes through the same front end every other kernel in this assembly does</b> —
///     <see cref="The_generated_kernel_compiles_to_a_compute_stage" /> asks
///     <c>RavenEffectCompiler</c> for SPIR-V. A generator that emitted plausible-looking nonsense
///     would pass every plan-shaped assertion in this file and fail that one, which is the same
///     bargain <c>TextureKernelTests</c> makes for the forty-five committed kernels.
/// </remarks>
public class TexturePixelProcessorTests {
    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>A graph whose one processor is fed by noise, out through an Output.</summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph, GraphNode Node) Processing(
        string expression,
        bool wired = true
    ) {
        NodeGraphModel graph = new();
        var processor = graph.Add("Filters/Pixel Processor");
        var output = graph.Add("Output/Output");

        processor.SetText("Expression", expression);
        graph.Connect(new(processor.Id, "Out"), new(output.Id, "Input"));

        if (wired) {
            var noise = graph.Add("Source/Noise");

            graph.Connect(new(noise.Id, "Out"), new(processor.Id, "A"));
        }

        return (new(Registry()) { BaseWidth = 64, BaseHeight = 64, Seed = 3 }, graph, processor);
    }

    /// <summary>An expression becomes an op naming a kernel the compiler carries the source of.</summary>
    [Fact]
    public void An_expression_becomes_an_op_naming_a_generated_kernel() {
        var (compiler, graph, _) = Processing("float4(a.x * x, a.y, a.z, 1f)");
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var op = compilation.Value.Ops.Last();
        var kernel = Assert.Single(compiler.Kernels);

        Assert.Equal(kernel.Kernel, op.Kernel);
        Assert.StartsWith(TexturePixelProcessor.Prefix, op.Kernel, StringComparison.Ordinal);

        // Every declared uniform is carried, because the evaluator refuses an op that leaves one out
        // and a parameter nobody carried is written as zero.
        Assert.Equal(["x", "y", "z", "w"], op.Parameters.Select(parameter => parameter.Name).ToArray());

        // One input, because one is wired.
        Assert.Single(op.Inputs);
    }

    /// <summary>
    ///     ⚠ The generated text is a real kernel: it compiles to SPIR-V through the same front end
    ///     every committed one goes through.
    /// </summary>
    [Fact]
    public void The_generated_kernel_compiles_to_a_compute_stage() {
        var (compiler, graph, _) = Processing("float4(a.x * x + y, b.y, uv.x, 1f)");

        compiler.Compile(graph);

        var kernel = Assert.Single(compiler.Kernels);
        var data = RavenEffectCompiler
            .FromSources([(kernel.Kernel + ".rvn", kernel.Source)])
            .TryGet(EffectKey.Of(kernel.Kernel));

        Assert.NotNull(data);

        var compute = Assert.Single(data.Stages, stage => stage.Stage == ShaderStage.Compute);

        Assert.NotEmpty(compute.Bytecode);
        Assert.Single(data.Stages);

        // The shape every kernel in this assembly has: exactly one storage image, and the
        // workgroup the evaluator dispatches in.
        Assert.Single(
            data.Bindings,
            binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.StorageTexture }
        );

        Assert.Contains(
            $"[ComputeShader({TexturePlanEvaluator.GroupSize}, {TexturePlanEvaluator.GroupSize}, 1)]",
            kernel.Source,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     The textures it declares are in the order the evaluator binds an op's inputs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing in the C# would notice if <c>b</c> were declared first.</b> The evaluator
    ///     binds an op's images positionally over the sampled textures sorted by binding number, so
    ///     swapping them would composite the wrong way round — a plausible picture, from a correct
    ///     plan.
    /// </remarks>
    [Fact]
    public void The_generated_kernel_declares_its_inputs_in_binding_order() {
        NodeGraphModel graph = new();
        var first = graph.Add("Source/Noise");
        var second = graph.Add("Source/Uniform");
        var processor = graph.Add("Filters/Pixel Processor");
        var output = graph.Add("Output/Output");

        processor.SetText("Expression", "a * b");
        graph.Connect(new(first.Id, "Out"), new(processor.Id, "A"));
        graph.Connect(new(second.Id, "Out"), new(processor.Id, "B"));
        graph.Connect(new(processor.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64 };
        var plan = compiler.Compile(graph).Value;
        var kernel = Assert.Single(compiler.Kernels);

        var data = RavenEffectCompiler
            .FromSources([(kernel.Kernel + ".rvn", kernel.Source)])
            .TryGet(EffectKey.Of(kernel.Kernel))!;

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(["sourceA", "sourceB"], textures);

        // ⚠ And the op's inputs are in the same order — asserted rather than said. The evaluator
        // binds them positionally over those two declarations, so an op that listed B's image first
        // would composite the wrong way round with a kernel that is perfectly correct.
        var op = plan.Ops.Last();
        var images = compiler.NodeImages.ToDictionary(written => written.Node, written => written.Image);

        Assert.Equal(images[second.Id], op.Inputs[1]);

        // ⚠ A's image is *not* the noise node's own. The processor resolved to colour — the Uniform
        // arrives at B — so the grey noise is splatted by an inserted ChannelShuffle and what reaches
        // the op is the splat's output. Doc 48 § Part 4's promotion rule, seen from a node that had
        // no idea it happened.
        var promotion = Assert.Single(plan.Ops, one => one.Output == op.Inputs[0]);

        Assert.Equal("ChannelShuffle", promotion.Kernel);
        Assert.Equal(images[first.Id], promotion.Inputs[0]);
    }

    /// <summary>An unwired input declares no texture and asks the op for no image.</summary>
    [Fact]
    public void An_unwired_input_is_opaque_black_rather_than_an_unbound_texture() {
        var (compiler, graph, _) = Processing("float4(uv.x, uv.y, 0f, 1f)", wired: false);
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var op = Assert.Single(compilation.Value.Ops);
        var kernel = Assert.Single(compiler.Kernels);

        Assert.Empty(op.Inputs);
        // ⚠ `": Texture2D"` and not `"Texture2D"`: the storage image is an `RWTexture2D`, so the bare
        // substring is in every kernel this node could ever write and the assertion would be one
        // that cannot fail — which is exactly how it was first written here.
        Assert.DoesNotContain(": Texture2D", kernel.Source, StringComparison.Ordinal);
        Assert.Contains("val a = float4(0f, 0f, 0f, 1f)", kernel.Source, StringComparison.Ordinal);

        // ⚠ And it writes colour rather than the grey a node with no image input resolves to, or a
        // generator's every expression would keep only its red.
        Assert.Equal(TextureFormat.Rgba16Float, compilation.Value.Images[op.Output].Format);
    }

    /// <summary>An expression that does not type-check names the node and the setting.</summary>
    [Fact]
    public void An_expression_that_does_not_compile_names_the_node_and_the_setting() {
        var (compiler, graph, processor) = Processing("a.nonsense");
        var compilation = compiler.Compile(graph);

        var diagnostic = Assert.Single(compilation.Diagnostics, one => one.Id == "TG0021");

        Assert.Equal(processor.Id, diagnostic.Node);
        Assert.Equal("Expression", diagnostic.Port);

        // The span is the line the expression is on, so a pane showing the generated kernel can put
        // the squiggle where Raven put it.
        Assert.False(diagnostic.Span.IsNone);
        Assert.Null(compilation.Artefact);
    }

    /// <summary>
    ///     ⚠ The complaint is Raven's own, phrased Raven's way, rather than a message this node
    ///     invented.
    /// </summary>
    /// <remarks>
    ///     <b>This is what "the real Raven compiler" buys and the only way to see it.</b> A
    ///     hand-rolled checker would produce a message of its own, and doc 48 § D6's whole argument is
    ///     that there is no second set of diagnostics here to keep in step with the first.
    /// </remarks>
    [Fact]
    public void The_complaint_carries_ravens_own_diagnostic_id() {
        var (compiler, graph, _) = Processing("nothing * 2f");

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0021");

        Assert.Contains("RVN", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>Two nodes with one expression are one kernel; two expressions are two.</summary>
    [Fact]
    public void Identical_expressions_share_one_kernel() {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var first = graph.Add("Filters/Pixel Processor");
        var second = graph.Add("Filters/Pixel Processor");
        var third = graph.Add("Filters/Pixel Processor");
        var output = graph.Add("Output/Output");

        foreach (var node in new[] { first, second }) {
            node.SetText("Expression", "a * 2f");
            graph.Connect(new(noise.Id, "Out"), new(node.Id, "A"));
        }

        third.SetText("Expression", "a * 3f");
        graph.Connect(new(noise.Id, "Out"), new(third.Id, "A"));
        graph.Connect(new(first.Id, "Out"), new(output.Id, "Input"));

        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 64, BaseHeight = 64 };

        compiler.Compile(graph);

        Assert.Equal(2, compiler.Kernels.Length);
    }

    /// <summary>The kernel's name is the same in two compilations of one graph.</summary>
    /// <remarks>
    ///     ⚠ <b>A name from <c>string.GetHashCode</c> would pass every other test in this file and
    ///     fail this one</b>, because that hash is randomised per process — and a plan whose kernel
    ///     names changed per run could not be diffed between two saved versions of a graph.
    /// </remarks>
    [Fact]
    public void The_kernel_name_is_derived_from_the_expression_and_is_stable() {
        var (first, graph, _) = Processing("a * 2f");
        var (second, other, _) = Processing("a * 2f");

        first.Compile(graph);
        second.Compile(other);

        Assert.Equal(first.Kernels[0].Kernel, second.Kernels[0].Kernel);

        // And it is derived: a different expression is a different kernel.
        var (third, another, _) = Processing("a * 3f");

        third.Compile(another);

        Assert.NotEqual(first.Kernels[0].Kernel, third.Kernels[0].Kernel);
    }

    /// <summary>An expression over more than one line is refused where it is typed.</summary>
    [Fact]
    public void An_expression_over_two_lines_is_refused() {
        var (compiler, graph, _) = Processing("a\n * 2f");

        var diagnostic = Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0020");

        Assert.Contains("newline ends a statement", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty expression is refused: there is no picture it could mean instead.</summary>
    [Fact]
    public void An_empty_expression_is_refused() {
        var (compiler, graph, _) = Processing("   ");

        Assert.Single(compiler.Compile(graph).Diagnostics, one => one.Id == "TG0020");
    }

    /// <summary>
    ///     Doc 48 § D6's two halves in one graph: the number the expression reads is itself an
    ///     expression over an exposed parameter.
    /// </summary>
    [Fact]
    public void A_number_the_expression_reads_may_itself_be_a_parameter_expression() {
        var (compiler, graph, processor) = Processing("a * x");

        processor.SetText(TextureGraphExpressions.KeyOf("X"), "amount * 4f");
        compiler.Parameters.Add(new("amount", Default: 0.25f, Minimum: 0f, Maximum: 1f));

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var op = compilation.Value.Ops.Last();

        Assert.Equal(1f, op.Find("x")!.Value.Value);
    }
}
