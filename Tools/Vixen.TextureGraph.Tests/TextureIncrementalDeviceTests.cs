// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     What the incremental path promises, on hand-built plans small enough to say which op ran.
/// </summary>
/// <remarks>
///     <para>
///         <b>Counters rather than clocks, and closed forms rather than a second evaluator.</b> A
///         levels curve over a ramp has one answer whoever computed it, so these hold after an edit
///         as well as before it; <see cref="TextureBake.Ran" /> is the list of ops that ran, which is
///         the claim itself and not a proxy for it.
///     </para>
///     <para>
///         The plan: a caller's ramp, three levels in a chain and one on a branch.
///         <c>ramp → a → b → c</c> and <c>a → d</c>, with <c>c</c> and <c>d</c> the outputs.
///     </para>
/// </remarks>
public class TextureIncrementalDeviceTests {
    const int Side = TextureKernelHarness.Side;
    const int Ramp = 0;
    const int A = 1;
    const int B = 2;
    const int C = 3;
    const int D = 4;

    static TextureOp Levels(int output, int input, float white = 1f, float gamma = 1f) =>
        new() {
            Kernel = "Levels",
            Output = output,
            Inputs = [input],
            Parameters = [
                new("inputBlack", 0f),
                new("inputWhite", white),
                new("gamma", gamma),
                new("outputBlack", 0f),
                new("outputWhite", 1f),
                new("dither", 0f)
            ]
        };

    /// <summary>
    ///     ⚠ One copy recorded over another's bytes, or an offset handed to the wrong image, is a
    ///     picture of the neighbour: every image here differs from the others, and in size of texel.
    /// </summary>
    [Fact]
    public void Reading_images_together_gives_what_reading_them_apart_gives() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba8),
                new(TextureFormat.R16Float),
                new(TextureFormat.Rgba16Float, LevelOffset: 1)
            ],
            Ops = [
                Levels(A, Ramp),
                Levels(B, A, gamma: 0.5f),
                Levels(C, A, white: 0.5f),
                new() {
                    Kernel = "Resample",
                    Output = D,
                    Inputs = [A],
                    ReadsOtherExtents = true,
                    Parameters = [new("filter", 2f)]
                }
            ],
            Outputs = [A, B, C, D]
        };
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using var bake = evaluator.EvaluateIncremental(plan, externals);
        int[] images = [D, B, A, C, B];
        var together = bake.ReadTexels(images);

        Assert.Equal(images.Length, together.Count);

        for (var index = 0; index < images.Length; index++) {
            Assert.Equal(bake.ReadTexels(images[index]), together[index]);
        }

        // Not a comparison of four identical images, which any offset would pass.
        Assert.Equal(4, together.Select(Convert.ToHexString).Distinct().Count());
    }

    static TexturePlan Chain() =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float)
            ],
            Ops = [Levels(A, Ramp), Levels(B, A), Levels(C, B), Levels(D, A)],
            Outputs = [C, D]
        };

    static (TextureHandle Texture, Dictionary<int, TextureExternal> Externals) Supply(
        VulkanDevice device,
        TexturePlan plan,
        TextureUploads uploads,
        byte[] texels
    ) {
        var texture = uploads.Add(plan, Ramp, Side, Side, texels);

        return (texture, new(uploads.Externals));
    }

    [Fact]
    public void An_unchanged_plan_runs_nothing_and_reads_the_same_picture() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using var first = evaluator.EvaluateIncremental(plan, externals);

        Assert.Equal([0, 1, 2, 3], first.Ran);
        Assert.Equal(0, first.Reused);

        var dispatches = evaluator.Dispatches;

        using var second = evaluator.EvaluateIncremental(plan, externals);

        Assert.True(second.Ran.IsEmpty, $"{TextureKernelHarness.Adapter(device)} ran {second.Ran.Length} ops.");
        Assert.Equal(4, second.Reused);
        Assert.Equal(dispatches, evaluator.Dispatches);
        Assert.Equal(first.ReadTexels(C), second.ReadTexels(C));
        Assert.Equal(first.ReadTexels(D), second.ReadTexels(D));
    }

    [Fact]
    public void Editing_one_op_runs_it_and_what_depends_on_it_and_writes_only_uniforms() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using (evaluator.EvaluateIncremental(plan, externals)) { }

        var compilations = evaluator.Compilations;
        var edited = plan.WithValues([new(1, "inputWhite", 0.5f)]);

        using var bake = evaluator.EvaluateIncremental(edited, externals);

        // `b` and `c`, and not `a`, which it reads, or `d`, which it has nothing to do with.
        Assert.Equal([1, 2], bake.Ran);
        Assert.Equal(2, bake.Reused);
        Assert.Equal(compilations, evaluator.Compilations);

        // The closed form, after the edit: a white point of a half doubles the ramp and clips at 1.
        var picture = bake.Read(C);

        Assert.InRange(TextureKernelHarness.At(picture, Side / 4, 0, 0), 120, 135);
        Assert.Equal(255, TextureKernelHarness.At(picture, Side - 1, 0, 0));
    }

    [Fact]
    public void Going_back_to_an_earlier_number_is_a_hit() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using (evaluator.EvaluateIncremental(plan, externals)) { }
        using (evaluator.EvaluateIncremental(plan.WithValues([new(1, "gamma", 2f)]), externals)) { }

        using var back = evaluator.EvaluateIncremental(plan, externals);

        Assert.Empty(back.Ran);
    }

    [Fact]
    public void A_number_the_gpu_cannot_see_runs_nothing() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using (evaluator.EvaluateIncremental(plan, externals)) { }

        // A parameter the kernel does not declare reaches no uniform, so the key is what the GPU
        // would have received and not what the op happens to carry.
        var ignored = plan.Ops[1] with { Parameters = [.. plan.Ops[1].Parameters, new("nobody reads this", 7f)] };
        var carried = new TexturePlan {
            BaseWidth = plan.BaseWidth,
            BaseHeight = plan.BaseHeight,
            Images = plan.Images,
            Ops = plan.Ops.SetItem(1, ignored),
            Outputs = plan.Outputs
        };

        using var bake = evaluator.EvaluateIncremental(carried, externals);

        Assert.Empty(bake.Ran);
    }

    static TexturePlan Sources(uint seed) =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = seed,
            Images = [new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)],
            Ops = [
                Uniform(0, 0.5f),
                new() {
                    Kernel = "Levels",
                    Output = 1,
                    Inputs = [0],
                    Parameters = Levels(1, 0).Parameters
                }
            ],
            Outputs = [1]
        };

    static TextureOp Uniform(int output, float grey) =>
        new() {
            Kernel = "Uniform",
            Output = output,
            Parameters = [new("red", grey), new("green", grey), new("blue", grey), new("alpha", 1f)]
        };

    [Fact]
    public void A_new_seed_runs_what_declares_one_and_nothing_else() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var none = new Dictionary<int, TextureExternal>();

        using (evaluator.EvaluateIncremental(Sources(1), none)) { }

        using var bake = evaluator.EvaluateIncremental(Sources(2), none);

        // `Uniform` has no seed member, so nothing it writes can depend on one; `Levels` dithers.
        Assert.Equal([1], bake.Ran);
    }

    [Fact]
    public void A_picture_is_identified_by_its_bytes_and_not_by_the_texture_holding_it() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var plan = Chain();
        var ramp = TextureKernelHarness.Ramp(Side);

        using (var uploads = new TextureUploads(device)) {
            var (_, externals) = Supply(device, plan, uploads, ramp);

            using var first = evaluator.EvaluateIncremental(plan, externals);

            Assert.Equal(4, first.Ran.Length);
        }

        // A new texture, the same bytes.
        using (var uploads = new TextureUploads(device)) {
            var (_, externals) = Supply(device, plan, uploads, ramp);

            using var second = evaluator.EvaluateIncremental(plan, externals);

            Assert.Empty(second.Ran);
        }

        var other = (byte[])ramp.Clone();

        other[0] ^= 0xFF;

        // A new picture: everything reads it, directly or not.
        using (var uploads = new TextureUploads(device)) {
            var (_, externals) = Supply(device, plan, uploads, other);

            using var third = evaluator.EvaluateIncremental(plan, externals);

            Assert.Equal(4, third.Ran.Length);
        }
    }

    [Fact]
    public void A_picture_with_no_digest_is_never_trusted() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (texture, supplied) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));
        var unknown = new Dictionary<int, TextureExternal> {
            [Ramp] = supplied[Ramp] with { Content = default }
        };

        using (evaluator.EvaluateIncremental(plan, unknown)) { }

        using var second = evaluator.EvaluateIncremental(plan, unknown);

        // A handle says nothing about what was painted into it since.
        Assert.True(texture.IsValid);
        Assert.Equal(4, second.Ran.Length);
    }

    [Fact]
    public void A_budget_of_nothing_is_slower_and_not_wrong() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        evaluator.Results.Budget = 0;

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));
        byte[] first;

        using (var bake = evaluator.EvaluateIncremental(plan, externals)) {
            Assert.Equal(4, bake.Ran.Length);

            first = bake.ReadTexels(C);

            // The bake holds what it can read, whatever the budget.
            Assert.True(evaluator.Results.Bytes > 0);
        }

        Assert.Equal(0, evaluator.Results.Bytes);
        Assert.Equal(0, evaluator.Results.Count);

        using var whole = evaluator.Evaluate(plan, externals);

        Assert.Equal(whole.ReadTexels(C), first);
    }

    [Fact]
    public void The_budget_holds_once_no_bake_is_alive_and_the_oldest_result_is_what_goes() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var one = (long)Side * Side * TextureFormats.BytesPerTexel(TextureFormat.Rgba16Float);
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        evaluator.Results.Budget = 4 * one;

        using (evaluator.EvaluateIncremental(plan, externals)) { }

        Assert.Equal(4 * one, evaluator.Results.Bytes);
        Assert.Equal(0, evaluator.Results.Evictions);

        // Two new results and a budget for four: what goes is two of the old plan's that the new
        // plan has no use for, which are `b` and `c`.
        var edited = plan.WithValues([new(1, "gamma", 3f)]);

        using (var bake = evaluator.EvaluateIncremental(edited, externals)) {
            Assert.Equal([1, 2], bake.Ran);
        }

        Assert.Equal(4 * one, evaluator.Results.Bytes);
        Assert.Equal(2, evaluator.Results.Evictions);

        // ⚠ The false half: with the budget this holds, `a` and `d` are still here and a third
        // edit of `b` does not run them.
        using var again = evaluator.EvaluateIncremental(plan.WithValues([new(1, "gamma", 4f)]), externals);

        Assert.Equal([1, 2], again.Ran);
    }

    [Fact]
    public void A_live_bake_outlasts_an_eviction_and_its_evaluator() {
        using var device = TextureKernelHarness.Open();

        var evaluator = new TexturePlanEvaluator(device);

        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        evaluator.Results.Budget = 0;

        using var held = evaluator.EvaluateIncremental(plan, externals);

        var expected = held.ReadTexels(C);

        using (evaluator.EvaluateIncremental(plan.WithValues([new(2, "gamma", 2f)]), externals)) { }

        Assert.Equal(expected, held.ReadTexels(C));

        evaluator.Dispose();

        Assert.Equal(expected, held.ReadTexels(C));
    }

    [Fact]
    public void Two_ops_with_one_key_share_one_result() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float)
            ],
            Ops = [Uniform(1, 0.5f), Uniform(2, 0.5f)],
            Outputs = [1, 2]
        };

        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using var bake = evaluator.EvaluateIncremental(plan, externals);

        Assert.Equal(bake.KeyOf(1), bake.KeyOf(2));
        Assert.Equal(1, evaluator.Results.Count);
        Assert.Equal(bake.ReadTexels(1), bake.ReadTexels(2));
    }

    [Fact]
    public void A_cpu_op_is_cached_only_when_it_says_what_it_is() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.R16Float),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "Transposed", Cpu = new TransposeRgba8(), Output = 1, Inputs = [Ramp] },
                TextureCpuOperations.NormalToHeight(2, Ramp, 8f, 1f),
                Levels(3, 1)
            ],
            Outputs = [2, 3]
        };

        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using (evaluator.EvaluateIncremental(plan, externals)) { }

        using var second = evaluator.EvaluateIncremental(plan, externals);

        // `TransposeRgba8` makes no promise, so it and what reads it run; `NormalToHeight` does.
        Assert.Equal([0, 2], second.Ran);
        Assert.Equal(1, second.Reused);
    }

    [Fact]
    public void A_run_that_fails_files_nothing() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));
        var unsound = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = plan.Images,
            Ops = plan.Ops.SetItem(3, plan.Ops[3] with { Inputs = [A, B] }),
            Outputs = plan.Outputs
        };

        Assert.Throws<ArgumentException>(() => evaluator.EvaluateIncremental(unsound, externals));

        Assert.Equal(0, evaluator.Results.Count);
        device.WaitIdle();
    }

    /// <summary>What a run with cached inputs does to the barriers, said by the layers.</summary>
    /// <remarks>
    ///     ⚠ <b>A cached result is read the way a caller's image is, and that is a claim about layouts:</b>
    ///     it has to arrive in <c>ShaderRead</c>, a CPU op has to take it through <c>CopySource</c> and
    ///     hand it back, and a recycled texture has to be written from <c>Undefined</c>. None of that
    ///     is visible in a picture on a unified adapter.
    /// </remarks>
    [Fact]
    public void A_run_over_cached_inputs_is_clean_under_the_validation_layers() {
        using var device = TextureKernelHarness.Open();

        TextureKernelHarness.RequireValidation(device);

        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.R16Float),
                new(TextureFormat.Rgba16Float)
            ],
            Ops = [Levels(1, Ramp), TextureCpuOperations.NormalToHeight(2, 1, 8f, 1f), Levels(3, 1)],
            Outputs = [2, 3]
        };

        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using (evaluator.EvaluateIncremental(plan, externals)) { }

        VulkanDiagnostics.Reset();

        // The CPU op and the dispatch both re-run over a result that is now in the cache.
        var edited = plan.WithValues([new(1, "intensity", 2f), new(2, "gamma", 2f)]);

        using (var bake = evaluator.EvaluateIncremental(edited, externals)) {
            Assert.Equal([1, 2], bake.Ran);

            bake.Read(2);
            bake.Read(3);
        }

        var errors = VulkanDiagnostics.ErrorCount;
        var messages = string.Join(Environment.NewLine, VulkanDiagnostics.Messages);

        Assert.True(errors == 0, $"{TextureKernelHarness.Adapter(device)} reported {errors} error(s):\n{messages}");
    }

    [Fact]
    public void Clearing_makes_the_next_evaluation_run_everything() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);
        using var uploads = new TextureUploads(device);

        var plan = Chain();
        var (_, externals) = Supply(device, plan, uploads, TextureKernelHarness.Ramp(Side));

        using (evaluator.EvaluateIncremental(plan, externals)) { }

        Assert.True(evaluator.Results.Count > 0);

        evaluator.Results.Clear();

        Assert.Equal(0, evaluator.Results.Count);
        Assert.Equal(0, evaluator.Results.Bytes);

        using var again = evaluator.EvaluateIncremental(plan, externals);

        Assert.Equal(4, again.Ran.Length);
    }

    /// <summary>Format is the ingredient a random graph cannot exercise on its own.</summary>
    /// <remarks>
    ///     ⚠ A graph's format follows its channels, which arrive on a wire, so its input keys always
    ///     differ first. Only a source with no inputs can show the ingredient, and it takes a plan
    ///     to change one source's format and nothing else about it.
    /// </remarks>
    [Fact]
    public void Dropping_the_format_from_the_key_serves_the_wrong_format_and_is_refused() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        static TexturePlan Filled(TextureFormat format) =>
            new() {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [new(format)],
                Ops = [TextureIncrementalDeviceTests.Uniform(0, 0.5f)],
                Outputs = [0]
            };

        using (evaluator.EvaluateIncremental(Filled(TextureFormat.Rgba8), new Dictionary<int, TextureExternal>())) { }

        // The control: with the whole key this is a miss and a different texture.
        using (var whole = evaluator.EvaluateIncremental(
                   Filled(TextureFormat.Rgba16Float),
                   new Dictionary<int, TextureExternal>()
               )) {
            Assert.Equal([0], whole.Ran);
        }

        evaluator.Results.Clear();
        evaluator.Results.Parts = TextureKeyParts.All & ~TextureKeyParts.Format;

        using (evaluator.EvaluateIncremental(Filled(TextureFormat.Rgba8), new Dictionary<int, TextureExternal>())) { }

        Assert.Throws<InvalidOperationException>(
            () => evaluator.EvaluateIncremental(
                Filled(TextureFormat.Rgba16Float),
                new Dictionary<int, TextureExternal>()
            )
        );
    }

    /// <summary>The same reason: a graph's externals come from assets, so the bytes do not change in one.</summary>
    [Fact]
    public void Dropping_the_digest_from_the_key_serves_the_old_picture() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var plan = Chain();
        var ramp = TextureKernelHarness.Ramp(Side);
        var other = TextureKernelHarness.Solid(Side, 9, 9, 9, 255);

        byte[] Draw(byte[] texels) {
            using var uploads = new TextureUploads(device);

            var (_, externals) = Supply(device, plan, uploads, texels);

            using var bake = evaluator.EvaluateIncremental(plan, externals);

            return bake.ReadTexels(C);
        }

        var control = Draw(ramp);

        Assert.NotEqual(control, Draw(other));

        evaluator.Results.Clear();
        evaluator.Results.Parts = TextureKeyParts.All & ~TextureKeyParts.Content;

        var sabotaged = Draw(ramp);

        Assert.Equal(sabotaged, Draw(other));
    }
}
