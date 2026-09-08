// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>A kernel a graph wrote, dispatched — the half of #729 no value can assert.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the test whose <em>absence</em> was the bug.</b> Doc 48 § D6's Pixel
///         Processor had a full compile-time suite — a generated <c>.rvn</c>, the real Raven front
///         end, diagnostics mapped back to the author's line — and every one of those passed against
///         a plan that <c>TexturePlanEvaluator</c> could not run, because the op named a shader it
///         resolved through this assembly's embedded resources. A graph that looks complete and
///         throws in a background task is worse than a missing feature, and only a bake says which
///         one you have.
///     </para>
///     <para>
///         <b>The oracle is a closed form and not an eyeball.</b> The expression is <c>uv.x</c>, so
///         the red channel of the baked image must rise with x and be flat down every column — a
///         kernel that had silently fallen back to something else could match neither.
///     </para>
///     <para>
///         ⚠ Names its adapter and skips loudly without one, through
///         <see cref="TextureKernelHarness.Open" />: without a real device a headless run falls back
///         to the Null device, and what this would then be comparing is two black images.
///     </para>
/// </remarks>
public class TexturePlanKernelDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>A plan carrying its own kernel bakes the picture that kernel describes.</summary>
    [Fact]
    public void A_plan_carrying_its_own_kernel_bakes_what_that_kernel_says() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        NodeGraphModel graph = new();
        var processor = graph.Add("Filters/Pixel Processor");
        var target = graph.Add("Output/Output");

        processor.SetText("Expression", "float4(uv.x, 0f, 0f, 1f)");
        graph.Connect(new(processor.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compiler = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side, Seed = 7 };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        var plan = compilation.Value;

        // The kernel is the graph's own and not one this assembly ships, which is what makes the
        // evaluation below a statement about #729 rather than about the Pixel Processor.
        Assert.Equal(plan.Ops[^1].Kernel, Assert.Single(plan.Kernels).Key);
        Assert.DoesNotContain(plan.Ops[^1].Kernel, TextureKernels.Names);
        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        var picture = bake.Read(plan.Outputs[0]);

        output.WriteLine($"{adapter}: {picture.Width}×{picture.Height} from '{plan.Ops[^1].Kernel}'");

        // uv.x, so: the first column is dark, the last is bright, every column is flat, and the
        // whole row rises. A fallback to any other kernel fails at least one of the four.
        Assert.True(TextureKernelHarness.At(picture, 0, 0, 0) < 8);
        Assert.True(TextureKernelHarness.At(picture, Side - 1, 0, 0) > 247);

        for (var y = 1; y < Side; y++) {
            Assert.Equal(TextureKernelHarness.At(picture, Side / 2, 0, 0), TextureKernelHarness.At(picture, Side / 2, y, 0));
        }

        for (var x = 1; x < Side; x++) {
            Assert.True(TextureKernelHarness.At(picture, x, 0, 0) > TextureKernelHarness.At(picture, x - 1, 0, 0));
        }
    }

    /// <summary>
    ///     ⚠ Two plans spelling one authored kernel name two ways are refused, rather than the second
    ///     being served the first one's module.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The hole in <c>(kernel, output)</c>, and it is on the device because a cache of
    ///         compiled modules only exists there</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1080">#1080</a>. An embedded kernel's
    ///         source is a function of its name and <c>TexturePixelProcessor</c> hashes its own, so
    ///         both of those identify a module; <see cref="TexturePlan.Kernels" /> is a public
    ///         property and nothing enforces the convention on whatever fills it next.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second plan is the first one's ops with a different body under the same
    ///         name</b>, which is the only arrangement that can reach the cache — a second plan whose
    ///         kernel had a different name would compile a second module and prove nothing. Before the
    ///         guard this baked <c>uv.x</c> from a plan whose source says <c>uv.y</c>, on a device,
    ///         with no complaint anywhere.
    ///     </para>
    ///     <para>
    ///         ⚠ Names its adapter and skips loudly without one, through
    ///         <see cref="TextureKernelHarness.Open" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void One_authored_kernel_name_spelled_two_ways_is_refused_rather_than_cached_over() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        var plan = Written("float4(uv.x, 0f, 0f, 1f)");
        var kernel = Assert.Single(plan.Kernels).Key;

        // The same name over a different body. `Written` gives the two expressions two names, which
        // is the convention working; this puts the second body under the first name, which is what a
        // front end that did not follow it would produce.
        var second = Written("float4(0f, uv.y, 0f, 1f)");
        var body = Assert.Single(second.Kernels);

        var collided = new TexturePlan {
            BaseWidth = plan.BaseWidth,
            BaseHeight = plan.BaseHeight,
            BakeLevelOffset = plan.BakeLevelOffset,
            Seed = plan.Seed,
            Images = plan.Images,
            Ops = plan.Ops,
            Outputs = plan.Outputs,
            Kernels = ImmutableDictionary<string, string>.Empty.Add(
                kernel,
                body.Value.Replace(body.Key, kernel, StringComparison.Ordinal)
            )
        };

        Assert.NotEqual(plan.Kernels[kernel], collided.Kernels[kernel]);

        using var evaluator = new TexturePlanEvaluator(device);

        using (var first = evaluator.Evaluate(plan)) {
            // ⚠ Read, so the first plan really compiled and really populated the cache. An evaluator
            // that had thrown before `VariantFor` would make the assertion below pass for the wrong
            // reason.
            output.WriteLine($"{adapter}: first bake is {first.Read(plan.Outputs[0]).Width} wide from '{kernel}'");
        }

        var refusal = Assert.Throws<ArgumentException>(() => evaluator.Evaluate(collided));

        Assert.Contains(kernel, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, evaluator.Compilations);
    }

    /// <summary>The same plan twice is not that, so the guard is about the source and not the name.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the guard could be "an evaluator runs one plan", which would break every
    ///     preview pane in the editor.</b> Re-evaluating one plan is the normal case — a pane keeps an
    ///     evaluator and hands it the same graph on every edit — and it has to reach the cache rather
    ///     than a refusal, which is what the second <c>Compilations</c> of 1 says.
    /// </remarks>
    [Fact]
    public void The_same_authored_kernel_twice_takes_the_cached_module() {
        using var device = TextureKernelHarness.Open();

        TextureKernelHarness.Adapter(device);

        var plan = Written("float4(uv.x, 0f, 0f, 1f)");

        using var evaluator = new TexturePlanEvaluator(device);
        using (evaluator.Evaluate(plan)) { }

        Assert.Equal(1, evaluator.Compilations);

        using (evaluator.Evaluate(Written("float4(uv.x, 0f, 0f, 1f)"))) { }

        Assert.Equal(1, evaluator.Compilations);
    }

    /// <summary>A plan whose one op is a Pixel Processor over the given expression.</summary>
    static TexturePlan Written(string expression) {
        NodeGraphModel graph = new();
        var processor = graph.Add("Filters/Pixel Processor");
        var target = graph.Add("Output/Output");

        processor.SetText("Expression", expression);
        graph.Connect(new(processor.Id, "Out"), new(target.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compilation = new TextureGraphCompiler(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 7
        }.Compile(graph);

        Assert.Empty(compilation.Diagnostics);

        return compilation.Value;
    }
}
