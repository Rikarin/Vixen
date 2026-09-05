// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The half of a plan's validation that is about its <em>numbers</em>: what a bake clips, and what
///     a chain emitted for one resolution does at another.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything <c>TexturePlanTests</c> asserts is structural</b> — indices, formats,
///         write-once, liveness — and a plan can pass all of it and bake a picture nothing describes.
///         <a href="https://github.com/Rikarin/Vixen/issues/692">#692</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/689">#689</a> are the two ways that
///         happens, and both are properties of a <em>resolved</em> number, so neither can be asserted
///         without a bake resolution.
///     </para>
///     <para>
///         <b>No device, deliberately.</b> The failure both issues describe is that the picture comes
///         out plausible; a device test would have to know what the right picture was, and the whole
///         point is that nothing did.
///     </para>
/// </remarks>
public class TexturePlanCheckTests {
    /// <summary>A plan of one sharpen, whose radius the kernel loops to 8.</summary>
    /// <remarks>
    ///     <c>Sharpen</c> rather than <c>Blur</c> because <c>Blur</c> is deliberately <em>not</em> in
    ///     the ceiling table — its constant is a budget on taps, so its width is never clipped and
    ///     there is nothing to report. Picking it here would have written a test that passes because
    ///     the thing it covers cannot happen.
    /// </remarks>
    static TexturePlan Sharpen(float radius, int bake) =>
        new() {
            BaseWidth = 256,
            BaseHeight = 256,
            BakeLevelOffset = bake,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Sharpen",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("radius", radius, TextureParameterUnit.TexelsAtBase)]
                }
            ],
            Outputs = [1]
        };

    /// <summary>A plan of one jump-flood chain over a mask, emitted for the extent it is given.</summary>
    static TexturePlan Distance(int emittedFor, int bake) {
        var scratch = TextureAnalysis.FloodDispatches(emittedFor, emittedFor);

        return new() {
            BaseWidth = 64,
            BaseHeight = 64,
            BakeLevelOffset = bake,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.R16Float),
                .. Enumerable.Repeat(new TextureImage(TextureFormat.Rgba16Float), scratch)
            ],
            Ops = TextureAnalysis.Distance(1, 0, [.. Enumerable.Range(2, scratch)], emittedFor, emittedFor),
            Outputs = [1]
        };
    }

    /// <summary>
    ///     ⚠ A radius past its kernel's loop is a <em>warning</em>: the plan still bakes, and the
    ///     picture is not the one the graph describes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a> said out
    ///         loud.</b> 4 texels at a 256 base is 16 at a 4× bake, and <c>Sharpen</c> loops to 8 —
    ///         so the same graph is a different material at the larger size, and before #692 nothing
    ///         anywhere said so. Refusing it would be wrong: the bake is what the artist asked for
    ///         and the clip may be acceptable. There has to be a third state, and this is the
    ///         assertion that there is one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The message carries the resolved number and not the authored one.</b> "A radius
    ///         of 4 is too big" is a sentence nobody can act on when 4 is what is in the field; 16 is
    ///         the number that is actually being clipped, and it exists only once a bake resolution
    ///         has been chosen.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_radius_past_its_kernels_loop_is_a_warning_rather_than_a_refusal() {
        var plan = Sharpen(4f, -2);
        var problem = Assert.Single(plan.Check());

        Assert.Equal(TextureProblemSeverity.Warning, problem.Severity);
        Assert.Contains("Sharpen", problem.Message, StringComparison.Ordinal);
        Assert.Contains("16", problem.Message, StringComparison.Ordinal);

        Assert.Empty(plan.Validate());
        Assert.Equal(problem.Message, Assert.Single(plan.Warnings()));
    }

    /// <summary>
    ///     Verify the instrument: the same graph at the resolution it was authored for says nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What this file prints on the day the check does not run is "no problems", which is
    ///     also what a sound plan prints.</b> The pair above and here is what separates the two: one
    ///     plan, two bake offsets, a warning at one and silence at the other — so a
    ///     <see cref="TexturePlan.Check" /> that returned nothing at all would fail the first test,
    ///     and one that warned about everything would fail this.
    /// </remarks>
    [Fact]
    public void The_same_graph_at_the_resolution_it_was_authored_for_is_clean() =>
        Assert.Empty(Sharpen(4f, 0).Check());

    /// <summary>
    ///     ⚠ A plan whose <em>shape</em> is broken is refused without the number check throwing out
    ///     of the method.
    /// </summary>
    /// <remarks>
    ///     <b>Resolving a parameter reads the image the op writes.</b> An op whose output index is
    ///     past the table would make that an <see cref="IndexOutOfRangeException" /> thrown from the
    ///     one method in the assembly whose contract is to report rather than throw — and the caller
    ///     would see a crash instead of the list naming the actual mistake.
    /// </remarks>
    [Fact]
    public void A_plan_whose_shape_is_broken_is_reported_rather_than_thrown_from() {
        var plan = new TexturePlan {
            BaseWidth = 256,
            BaseHeight = 256,
            BakeLevelOffset = -2,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Sharpen",
                    Output = 7,
                    Inputs = [0],
                    Parameters = [new("radius", 4f, TextureParameterUnit.TexelsAtBase)]
                }
            ],
            Outputs = [1]
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("the table holds", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ Doc 48 § D8's promise, tested where it is not kept: a chain emitted for one extent is
    ///     refused at another.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/689">#689</a>.</b> A jump flood is
    ///         <c>log2(n)</c> dispatches in the <em>baked</em> extent, so its op count is part of the
    ///         answer rather than a parameter of it. Re-baking is expressed by building a plan with
    ///         the same <see cref="TexturePlan.Ops" /> and a different
    ///         <see cref="TexturePlan.BakeLevelOffset" /> — which is what
    ///         <c>TexturePlanDeviceTests.The_same_plan_baked_at_four_times_the_resolution_agrees_with_the_smaller_bake</c>
    ///         does and what § D8 promises works — and for this chain it produced a distance field
    ///         wrong at long range, which looks like a soft field rather than like a bug.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The issue's own reading of the mechanism was half wrong and it does not
    ///         matter.</b> It says <see cref="TexturePlan.BakeLevelOffset" /> "reads as a field a
    ///         caller may change on an existing plan"; it is <c>init</c>-only on a class with no
    ///         <c>with</c>, so nobody mutates a plan. The hazard is the copy, not the mutation, and
    ///         the copy is the documented way to re-bake.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_chain_emitted_for_one_extent_is_refused_at_another() {
        var problems = Distance(64, -1).Validate();

        // Every refusal is this one: each of the six halvings the chain was emitted for is an op
        // whose existence the bake resolution decides.
        Assert.NotEmpty(problems);
        Assert.All(problems, message => Assert.Contains("emitted for", message, StringComparison.Ordinal));
        Assert.Contains("64 texels", problems[0], StringComparison.Ordinal);
        Assert.Contains("128×128", problems[0], StringComparison.Ordinal);
    }

    /// <summary>Verify the instrument: the same chain, emitted for the bake it is run at, validates.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this the test above passes against a <see cref="TexturePlan.Check" /> that
    ///     refuses every plan holding a jump flood</b>, which would be a check nobody could satisfy
    ///     and which would take the § 4.5 device suite with it. The pair is one plan shape at two
    ///     extents: refused where the list is stale, clean where it was emitted for this bake.
    /// </remarks>
    [Fact]
    public void A_chain_re_emitted_for_this_bake_validates() => Assert.Empty(Distance(128, -1).Check());

    /// <summary>An op that is one dispatch at any resolution carries no extent and re-bakes.</summary>
    [Fact]
    public void An_ordinary_op_re_bakes_at_any_offset() {
        Assert.Empty(Sharpen(1f, 0).Check());
        Assert.Empty(Sharpen(1f, -2).Check());
        Assert.Empty(Sharpen(1f, 2).Check());
    }

    /// <summary>A CPU op is pooled and freed by the op order like any other.</summary>
    /// <remarks>
    ///     ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/688">#688</a>'s seam is only worth
    ///     having if the plan's shape does not change around it.</b> A CPU op writes one image, reads
    ///     by index and is written once, so <see cref="TexturePoolSchedule" /> needs to know nothing
    ///     about it — three ops threaded through two live images allocate two textures whether the
    ///     middle one is a dispatch or a solve.
    /// </remarks>
    [Fact]
    public void A_cpu_op_is_scheduled_like_a_dispatch() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "Invert", Output = 1, Inputs = [0] },
                new() { Kernel = "Transpose", Output = 2, Inputs = [1], Cpu = new TransposeRgba8() },
                new() { Kernel = "Invert", Output = 3, Inputs = [2] }
            ],
            Outputs = [3]
        };

        Assert.Empty(plan.Check());

        var schedule = TexturePoolSchedule.For(plan);

        Assert.Equal(2, schedule.Allocations);
        Assert.Equal(2, schedule.Peak);

        // The last image lands back in the first image's texture, which is the aliasing the device
        // test exercises: an upload that went to the wrong slot would be read as the answer.
        Assert.Equal(schedule.SlotOf[1], schedule.SlotOf[3]);
    }
}

/// <summary>An RGBA8 transpose, as a <see cref="ITextureCpuOperation" />.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a node, and not a CPU twin of one.</b> Doc 48 § D3 bans a C# implementation of
///         anything a kernel does, and nothing in <c>Shaders/</c> transposes. What is being tested is
///         the <em>seam</em> — that a plan can hold an op that is not a dispatch and that the
///         evaluator reads back, computes and uploads around it — so the operation wants to be
///         something no kernel in the chain could have produced by accident. A transpose is that: it
///         moves every texel, it is exactly invertible, and it commutes with the inverts either side
///         of it, so the whole chain has a closed form.
///     </para>
///     <para>
///         § 4.6's <c>Normal → Height</c> is what the seam exists for and a later slice writes it.
///     </para>
/// </remarks>
sealed class TransposeRgba8 : ITextureCpuOperation {
    /// <inheritdoc />
    public string Name => "Transpose";

    /// <inheritdoc />
    public void Run(in TextureCpuInvocation invocation) {
        var source = invocation.Inputs[0];
        var target = invocation.Output;

        Assert.Equal(TextureFormat.Rgba8, source.Format);
        Assert.Equal(TextureFormat.Rgba8, target.Format);
        Assert.Equal(source.Width, target.Height);
        Assert.Equal(source.Height, target.Width);

        for (var y = 0; y < target.Height; y++) {
            for (var x = 0; x < target.Width; x++) {
                var from = ((x * source.Width) + y) * 4;
                var to = ((y * target.Width) + x) * 4;

                source.Bytes.AsSpan(from, 4).CopyTo(target.Bytes.AsSpan(to, 4));
            }
        }
    }
}
