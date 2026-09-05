// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>The intermediate pool, asserted with no device at all.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The bound is what this file exists for.</b> "Allocate on first write, free when the
///         last reader has run, reuse a freed slot" is a claim about a number, and the version that
///         does none of it works perfectly on a plan of six ops. At 2K, forty RGBA8 intermediates are
///         640 MB and two are 32 MB; a spike that only ever ran six ops would not have noticed.
///     </para>
///     <para>
///         <b>It runs on any machine.</b> A pool assertion behind a GPU skip is a pool assertion
///         nobody reads on the day the driver is missing — and this is the half of doc 48 § M0's
///         question that does not need a device to answer.
///     </para>
/// </remarks>
public class TexturePoolTests {
    /// <summary>
    ///     A chain of <paramref name="ops" /> filters, each reading what the one before it wrote.
    /// </summary>
    /// <remarks>
    ///     The shape almost every real graph has in the middle: a long thread of unary operations,
    ///     where nothing is live except what the next op is about to read.
    /// </remarks>
    static TexturePlan Chain(int ops, int baseSize = 2048) {
        var images = ImmutableArray.CreateBuilder<TextureImage>();
        var list = ImmutableArray.CreateBuilder<TextureOp>();

        images.Add(new(TextureFormat.Rgba8, External: true));

        for (var index = 0; index < ops; index++) {
            images.Add(new(TextureFormat.Rgba8));

            list.Add(
                new() {
                    Kernel = "Levels",
                    Output = index + 1,
                    Inputs = [index],
                    Parameters = [
                        new("inputBlack", 0f),
                        new("inputWhite", 1f),
                        new("gamma", 1f),
                        new("outputBlack", 0f),
                        new("outputWhite", 1f),
                        new("dither", 0f)
                    ]
                }
            );
        }

        return new() {
            BaseWidth = baseSize,
            BaseHeight = baseSize,
            Images = images.ToImmutable(),
            Ops = list.ToImmutable(),
            Outputs = [ops]
        };
    }

    /// <summary>Forty ops over two live images allocate two textures, not forty.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § M0's second question, and the whole reason the pool exists.</b> Only two of a
    ///     chain's images are ever live at once — what the current op reads and what it writes — so the
    ///     third op reuses the first's texture. The plan names the last one an output, so it is kept
    ///     rather than recycled, which is what makes the answer two and not one.
    /// </remarks>
    [Fact]
    public void A_chain_of_forty_ops_allocates_two_images() {
        var plan = Chain(40);

        Assert.Empty(plan.Validate());

        var schedule = TexturePoolSchedule.For(plan);

        Assert.Equal(2, schedule.Peak);
        Assert.Equal(2, schedule.Allocations);

        // And that is the number in bytes as well, which is the number that matters: two 2K RGBA8
        // images are 32 MB and forty are 640 MB.
        Assert.Equal(2L * 2048 * 2048 * 4, schedule.Bytes);
    }

    /// <summary>The bound does not grow with the chain, which is the property rather than the number.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(40)]
    [InlineData(200)]
    public void The_allocation_count_does_not_follow_the_op_count(int ops) {
        var schedule = TexturePoolSchedule.For(Chain(ops));

        Assert.True(
            schedule.Allocations <= 2,
            $"{ops} ops allocated {schedule.Allocations} textures; a chain is never more than two live at once"
        );
    }

    /// <summary>Every op writes into some slot, and no image is left without one.</summary>
    [Fact]
    public void Every_written_image_has_a_slot() {
        var plan = Chain(6);
        var schedule = TexturePoolSchedule.For(plan);

        for (var image = 0; image < plan.Images.Length; image++) {
            if (plan.Images[image].External) {
                Assert.Equal(-1, schedule.SlotOf[image]);
            } else {
                Assert.InRange(schedule.SlotOf[image], 0, schedule.Allocations - 1);
            }
        }
    }

    /// <summary>
    ///     ⚠ An op's output never lands in a slot one of its own inputs is still using.
    /// </summary>
    /// <remarks>
    ///     <b>The classic form of this bug, and it is a one-line ordering mistake.</b> Freeing an op's
    ///     dying inputs before taking its output hands the output the texture the kernel is about to
    ///     read — and a dispatch has no ordering between its own invocations, so what comes out is
    ///     half the old image and half the new one, on some drivers, some of the time. Taking first
    ///     and freeing afterwards is what <c>TexturePoolSchedule.For</c> does, and this is what says
    ///     so.
    /// </remarks>
    [Fact]
    public void An_ops_output_never_aliases_an_image_it_reads() {
        var plan = Chain(40);
        var schedule = TexturePoolSchedule.For(plan);

        foreach (var op in plan.Ops) {
            foreach (var input in op.Inputs) {
                if (schedule.SlotOf[input] >= 0) {
                    Assert.NotEqual(schedule.SlotOf[input], schedule.SlotOf[op.Output]);
                }
            }
        }
    }

    /// <summary>An image the plan keeps is never handed to a later op.</summary>
    [Fact]
    public void An_output_image_is_never_recycled() {
        // Two branches over one source, both kept: the second branch must not be given the first
        // output's texture even though nothing reads it any more.
        var plan = new TexturePlan {
            BaseWidth = 256,
            BaseHeight = 256,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "A", Output = 1, Inputs = [0] },
                new() { Kernel = "B", Output = 2, Inputs = [0] }
            ],
            Outputs = [1, 2]
        };

        var schedule = TexturePoolSchedule.For(plan);

        Assert.Equal(2, schedule.Allocations);
        Assert.NotEqual(schedule.SlotOf[1], schedule.SlotOf[2]);
    }

    /// <summary>
    ///     A slot is reused only by an image of the same shape, so a plan at two resolutions allocates
    ///     one texture of each.
    /// </summary>
    [Fact]
    public void A_slot_is_reused_only_at_the_same_shape() {
        var plan = new TexturePlan {
            BaseWidth = 512,
            BaseHeight = 512,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8, 1),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "A", Output = 1, Inputs = [0] },
                new() { Kernel = "B", Output = 2, Inputs = [1] },
                new() { Kernel = "C", Output = 3, Inputs = [2] }
            ],
            Outputs = [3]
        };

        var schedule = TexturePoolSchedule.For(plan);

        // Image 1 is 512², image 2 is 256², image 3 is 512² and reuses image 1's texture: two
        // allocations, and never three.
        Assert.Equal(2, schedule.Allocations);
        Assert.Equal(schedule.SlotOf[1], schedule.SlotOf[3]);
        Assert.NotEqual(schedule.SlotOf[1], schedule.SlotOf[2]);
    }

    /// <summary>A branch that stays live over several ops keeps its texture the whole time.</summary>
    [Fact]
    public void A_long_lived_branch_is_not_recycled_under_the_op_that_reads_it() {
        // 1 is written first and read last, so it is live across the whole plan and 2 and 3 have to
        // go somewhere else.
        var plan = new TexturePlan {
            BaseWidth = 128,
            BaseHeight = 128,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "A", Output = 1, Inputs = [0] },
                new() { Kernel = "B", Output = 2, Inputs = [0] },
                new() { Kernel = "C", Output = 3, Inputs = [2] },
                new() { Kernel = "D", Output = 4, Inputs = [1, 3] }
            ],
            Outputs = [4]
        };

        var schedule = TexturePoolSchedule.For(plan);

        Assert.Equal(3, schedule.Peak);
        Assert.NotEqual(schedule.SlotOf[1], schedule.SlotOf[2]);
        Assert.NotEqual(schedule.SlotOf[1], schedule.SlotOf[3]);
        Assert.NotEqual(schedule.SlotOf[1], schedule.SlotOf[4]);

        // 2 dies when 3 is written, so 4 gets its texture back.
        Assert.Equal(schedule.SlotOf[2], schedule.SlotOf[4]);
    }
}
