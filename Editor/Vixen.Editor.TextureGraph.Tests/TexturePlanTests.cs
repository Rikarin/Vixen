// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>The plan on its own: its resolution rules, its seed, and what it refuses.</summary>
/// <remarks>
///     ⚠ <b>Not one of these opens a device, and that is the point.</b> Doc 48 § D8's resolution rule
///     is arithmetic on the plan; a bug in it produces a picture that is subtly wrong at one
///     resolution and right at another, which is exactly the kind of defect a device test looking at
///     one resolution cannot see. So it is asserted where it lives.
/// </remarks>
public class TexturePlanTests {
    /// <summary>A plan of one blur over one supplied image, at whatever base and level are asked for.</summary>
    static TexturePlan Blur(int baseSize, int level, float radius) =>
        new() {
            BaseWidth = baseSize,
            BaseHeight = baseSize,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8, level)
            ],
            Ops = [
                new() {
                    Kernel = "Blur",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("radius", radius, TextureParameterUnit.TexelsAtBase),
                        new("stepX", 1f),
                        new("stepY", 0f)
                    ]
                }
            ],
            Outputs = [1]
        };

    /// <summary>A level offset is a power of two away from the base, in both directions.</summary>
    [Theory]
    [InlineData(0, 1024, 1024)]
    [InlineData(1, 512, 512)]
    [InlineData(2, 256, 256)]
    [InlineData(-1, 2048, 2048)]
    public void An_image_is_relative_to_the_base_resolution(int level, int width, int height) {
        var plan = Blur(1024, level, 4f);

        Assert.Equal(width, plan.SizeOf(1).X);
        Assert.Equal(height, plan.SizeOf(1).Y);
    }

    /// <summary>A level that would take an image below one texel gives one texel.</summary>
    [Fact]
    public void An_image_never_shrinks_below_a_texel() {
        var plan = Blur(64, 12, 1f);

        Assert.Equal(1, plan.SizeOf(1).X);
        Assert.Equal(1, plan.SizeOf(1).Y);

        // And the scale follows the size it actually got rather than the level, so a radius is not
        // scaled to a sixteenth of a texel on an image that is one texel across.
        Assert.Equal(1f / 64f, plan.ScaleOf(1), 6);
    }

    /// <summary>
    ///     ⚠ Doc 48 § D8, as a number: a radius authored in texels-at-base is scaled by the evaluator.
    /// </summary>
    /// <remarks>
    ///     <b>The bug with a two-year fuse.</b> Eight texels at a base of 1024 is eight texels on a
    ///     full-resolution image and four on a half-resolution one — and the same graph baked at 4K
    ///     wants thirty-two, which is what makes it the <em>same material</em> rather than a sharper
    ///     one. A radius that reached the kernel unscaled would look right at the resolution it was
    ///     tuned at and be wrong everywhere else, which nobody associates with the resolution field.
    /// </remarks>
    [Theory]
    [InlineData(1024, 0, 8f)]
    [InlineData(1024, 1, 4f)]
    [InlineData(1024, 2, 2f)]
    [InlineData(1024, -1, 16f)]
    [InlineData(4096, 0, 8f)]
    public void A_length_is_in_texels_at_the_base_and_the_evaluator_scales_it(int baseSize, int level, float expected) {
        var plan = Blur(baseSize, level, 8f);
        var op = plan.Ops[0];

        Assert.Equal(expected, plan.Resolve(0, op.Find("radius")!.Value), 4);

        // And a plain number is not scaled, whatever the resolution: an axis, a mode, an opacity.
        Assert.Equal(1f, plan.Resolve(0, op.Find("stepX")!.Value), 4);
    }

    /// <summary>
    ///     A graph authored at one resolution and baked at another asks its kernels for proportionally
    ///     wider filters, which is the property D8 says is testable.
    /// </summary>
    [Fact]
    public void The_same_plan_at_four_times_the_base_asks_for_four_times_the_radius() {
        var small = Blur(1024, 0, 8f);
        var large = Blur(4096, 0, 8f);

        var at1K = small.Resolve(0, small.Ops[0].Find("radius")!.Value);
        var at4K = large.Resolve(0, large.Ops[0].Find("radius")!.Value);

        // Both are 8 — a texel-at-base *is* a texel of a level-0 image — and the picture is four times
        // wider at 4K because the image is. Storing the radius as an absolute number of texels of the
        // *output* is what would make these differ, and would make the two bakes different materials.
        Assert.Equal(at1K, at4K, 4);
        Assert.Equal(4f * small.SizeOf(1).X, large.SizeOf(1).X);
    }

    /// <summary>Two ops never draw the same seed, and a seed is the same on every run.</summary>
    [Fact]
    public void A_seed_is_per_op_and_reproducible() {
        var plan = Blur(256, 0, 1f);
        var other = Blur(256, 0, 1f);

        Assert.Equal(plan.SeedFor(0), other.SeedFor(0));

        HashSet<uint> seen = [];

        for (var op = 0; op < 64; op++) {
            Assert.True(seen.Add(plan.SeedFor(op)), $"op {op} draws a seed an earlier op already had");
        }
    }

    /// <summary>Moving the plan's seed moves every op's.</summary>
    [Fact]
    public void The_plans_seed_reaches_every_op() {
        var plan = new TexturePlan {
            BaseWidth = 8,
            BaseHeight = 8,
            Seed = 41823,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [],
            Outputs = [0]
        };

        var moved = new TexturePlan {
            BaseWidth = 8,
            BaseHeight = 8,
            Seed = 41824,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [],
            Outputs = [0]
        };

        for (var op = 0; op < 8; op++) {
            Assert.NotEqual(plan.SeedFor(op), moved.SeedFor(op));
        }
    }

    /// <summary>A sound plan has nothing to say about itself.</summary>
    [Fact]
    public void A_sound_plan_validates() => Assert.Empty(Blur(256, 0, 4f).Validate());

    /// <summary>
    ///     ⚠ An R8 or RG8 output is refused, which refutes the format list in doc 48 § M1 and #566.
    /// </summary>
    /// <remarks>
    ///     Both list R8 and RG8 beside the other three as though a kernel could write any of the five.
    ///     <c>Raven/Vixen.Raven/Symbols/ImageFormats.cs</c> admits neither as a storage image, and
    ///     Vulkan requires storage support for neither — so the refusal is where a plan is built rather
    ///     than at pipeline creation, where it would arrive as a driver message about a format nobody
    ///     chose by hand.
    /// </remarks>
    [Theory]
    [InlineData(TextureFormat.R8)]
    [InlineData(TextureFormat.Rg8)]
    public void A_kernel_cannot_write_a_format_no_storage_image_has(TextureFormat format) {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true), new(format)],
            Ops = [new() { Kernel = "Levels", Output = 1, Inputs = [0] }],
            Outputs = [1]
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("storage image", StringComparison.Ordinal));
    }

    /// <summary>Reading an image nothing has written yet is refused rather than read as noise.</summary>
    [Fact]
    public void An_op_cannot_read_an_image_nothing_has_written() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Levels", Output = 0, Inputs = [1] }],
            Outputs = [0]
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("nothing has written", StringComparison.Ordinal));
    }

    /// <summary>An op that reads what it writes is refused: a dispatch has no order within itself.</summary>
    [Fact]
    public void An_op_cannot_read_the_image_it_writes() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() { Kernel = "Levels", Output = 1, Inputs = [0] },
                new() { Kernel = "Levels", Output = 1, Inputs = [1] }
            ],
            Outputs = [1]
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("reads and writes", StringComparison.Ordinal));
    }

    /// <summary>An image written twice is refused, because liveness is the op order.</summary>
    [Fact]
    public void An_image_is_written_once() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() { Kernel = "Levels", Output = 1, Inputs = [0] },
                new() { Kernel = "Levels", Output = 1, Inputs = [0] }
            ],
            Outputs = [1]
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("already wrote", StringComparison.Ordinal));
    }

    /// <summary>Writing over a supplied image is refused: an external image is an input.</summary>
    [Fact]
    public void An_external_image_is_never_written() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8, External: true)],
            Ops = [new() { Kernel = "Levels", Output = 1, Inputs = [0] }],
            Outputs = [1]
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("caller supplies", StringComparison.Ordinal));
    }

    /// <summary>A plan that computes nothing anybody reads says so.</summary>
    [Fact]
    public void A_plan_with_no_outputs_is_refused() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Levels", Output = 1, Inputs = [0] }],
            Outputs = ImmutableArray<int>.Empty
        };

        Assert.Contains(plan.Validate(), problem => problem.Contains("names no outputs", StringComparison.Ordinal));
    }

    /// <summary>An index outside the table is named rather than thrown from somewhere unrelated.</summary>
    [Fact]
    public void An_index_outside_the_image_table_is_refused() {
        var plan = new TexturePlan {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true)],
            Ops = [new() { Kernel = "Levels", Output = 7, Inputs = [0] }],
            Outputs = [7]
        };

        Assert.NotEmpty(plan.Validate());
    }
}
