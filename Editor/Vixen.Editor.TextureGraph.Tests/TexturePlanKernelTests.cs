// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A plan carrying a kernel a graph wrote, rather than one this assembly shipped — #729.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The failure this closes is worse than a missing feature, and doc 48 § D6 walked into
///         it.</b> The Pixel Processor's compile-time half was finished — a whole <c>.rvn</c>
///         generated, put through the real Raven front end, every complaint mapped back to the line
///         the author's expression is on — and the op it emitted named a shader
///         <c>TexturePlanEvaluator</c> resolved through this assembly's embedded resources. So the
///         graph looked complete, the compilation was clean, the plan validated, and the bake threw
///         about a manifest resource nobody could have added.
///     </para>
///     <para>
///         <b>What is asserted here is the source, the rewrite and the refusals — not a picture.</b>
///         The picture needs a device; what a plan owes the evaluator is text that the format rewrite
///         applies to and the front end accepts, and that is checkable everywhere.
///     </para>
/// </remarks>
public class TexturePlanKernelTests {
    /// <summary>A Pixel Processor's plan carries its generated source under its op's own name.</summary>
    [Fact]
    public void A_generated_kernels_source_travels_on_the_plan() {
        var (compiler, graph) = Processing("float4(a.x * x, a.y, a.z, 1f)");
        var plan = compiler.Compile(graph).Value;
        var op = plan.Ops[^1];
        var declared = Assert.Single(compiler.Kernels);

        Assert.Equal(declared.Kernel, op.Kernel);

        // The point of the whole issue: the name the op gives resolves, and to the text the node
        // generated. Before this it threw — the plan had nowhere to put a kernel.
        Assert.Equal(declared.Source, plan.Source(op.Kernel));
        Assert.Equal(declared.Source, Assert.Single(plan.Kernels).Value);
    }

    /// <summary>
    ///     ⚠ And the text the plan carries is what the evaluator would compile: the format rewrite
    ///     applies to it, and the result goes through the real front end.
    /// </summary>
    /// <remarks>
    ///     <b>This is the assertion that the plan's copy is <em>usable</em> rather than merely
    ///     present.</b> A field holding the source and a <c>VariantFor</c> that never consulted it
    ///     would satisfy every equality above; what could not be faked is asking
    ///     <see cref="TextureKernels.Variant(string,string,TextureFormat)" /> — the one the evaluator
    ///     calls — for the same three variants a plan can ask of any kernel, and compiling each.
    /// </remarks>
    [Theory]
    [InlineData(TextureFormat.Rgba8, "rgba8")]
    [InlineData(TextureFormat.R16Float, "r16f")]
    [InlineData(TextureFormat.Rgba16Float, "rgba16f")]
    public void The_plans_own_kernel_takes_the_format_rewrite_and_compiles(TextureFormat format, string spelling) {
        var (compiler, graph) = Processing("float4(a.x * x + y, uv.x, 0f, 1f)");
        var plan = compiler.Compile(graph).Value;
        var kernel = plan.Ops[^1].Kernel;
        var source = TextureKernels.Variant(kernel, plan.Source(kernel), format);

        Assert.Contains($"[Format(\"{spelling}\")]", source, StringComparison.Ordinal);

        var data = RavenEffectCompiler
            .FromSources([(TextureKernels.VariantName(kernel, format), source)])
            .TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);
        Assert.Single(data.Stages, stage => stage.Stage == ShaderStage.Compute);
    }

    /// <summary>An embedded kernel still resolves through the assembly, plan or no plan.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument.</b> Every other assertion in this file would also pass if
    ///     <see cref="TexturePlan.Source" /> had stopped answering for the forty-five kernels this
    ///     assembly ships, which is every op in every graph anybody has authored.
    /// </remarks>
    [Fact]
    public void An_embedded_kernel_resolves_through_a_plan_that_carries_none() {
        var plan = new TexturePlan {
            BaseWidth = 8,
            BaseHeight = 8,
            Images = [new(TextureFormat.Rgba16Float)],
            Ops = [new() { Kernel = "Uniform", Output = 0 }],
            Outputs = [0]
        };

        Assert.Empty(plan.Kernels);
        Assert.Equal(TextureKernels.Source("Uniform"), plan.Source("Uniform"));
        Assert.Empty(plan.Validate());
    }

    /// <summary>A plan may not redefine a kernel this assembly ships.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than shadowed, and the reason is two layers down.</b>
    ///     <c>TexturePlanEvaluator</c> caches a compiled module on <c>(kernel name, output format)</c>
    ///     across every plan it runs — so a plan redefining <c>Blur</c> would either take the module
    ///     already compiled from the embedded source or leave its own behind for the next plan. The
    ///     same op would draw two different pictures depending on what ran before it, which is the
    ///     class of defect nobody reproduces.
    /// </remarks>
    [Fact]
    public void A_plan_may_not_redefine_a_kernel_the_assembly_ships() {
        var plan = Carrying("Blur", "shader Blur { }");
        var refusal = Assert.Single(plan.Validate());

        Assert.Contains("Blur", refusal, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => plan.Source("Blur"));

        // The instrument: the identical plan under a name the assembly does not ship is fine, so the
        // refusal is about the collision and not about carrying a kernel at all.
        Assert.Empty(Carrying("Blur$4c1d", "shader Blur$4c1d { }").Validate());
    }

    /// <summary>A carried kernel with no source is refused before a bake rather than during one.</summary>
    /// <remarks>
    ///     An empty entry is what a serialiser that dropped a field leaves behind, and the evaluator's
    ///     answer to it is a Raven complaint about an empty file three frames inside a background
    ///     task.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_carried_kernel_with_no_source_is_refused(string source) {
        var refusal = Assert.Single(Carrying("Written$0", source).Validate());

        Assert.Contains("no source", refusal, StringComparison.Ordinal);
    }

    /// <summary>A plan whose one op names the kernel it carries.</summary>
    static TexturePlan Carrying(string kernel, string source) =>
        new() {
            BaseWidth = 8,
            BaseHeight = 8,
            Images = [new(TextureFormat.Rgba16Float)],
            Ops = [new() { Kernel = kernel, Output = 0 }],
            Outputs = [0],
            Kernels = ImmutableDictionary<string, string>.Empty.Add(kernel, source)
        };

    /// <summary>A graph whose one processor is fed by noise, out through an Output.</summary>
    static (TextureGraphCompiler Compiler, NodeGraphModel Graph) Processing(string expression) {
        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var processor = graph.Add("Filters/Pixel Processor");
        var output = graph.Add("Output/Output");

        processor.SetText("Expression", expression);
        graph.Connect(new(noise.Id, "Out"), new(processor.Id, "A"));
        graph.Connect(new(processor.Id, "Out"), new(output.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return (new(registry) { BaseWidth = 64, BaseHeight = 64, Seed = 3 }, graph);
    }
}
