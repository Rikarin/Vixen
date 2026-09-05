// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Xunit;

namespace Tests;

/// <summary>What the harness's own test patterns can and cannot measure.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The instrument, tested. <a href="https://github.com/Rikarin/Vixen/issues/694">#694</a>.</b>
///         <c>TextureKernelHarness.Unique</c> reads as the strongest pattern in the file — 4 096
///         distinct colours, so "this is a copy" is a claim about all of them — and for a copy it is.
///         For an <em>averaging</em> assertion it is the weakest one available: every channel is an
///         affine function of x and y, the mean of an affine function over a symmetric window is its
///         value at the centre, and so a blur returns it unchanged at any strength. Two of § 4.4's
///         tests were written over it and passed with the parameter they were about hard-coded.
///     </para>
///     <para>
///         <b>A property of the harness rather than of a kernel, so it belongs to the harness.</b>
///         The docstring on <c>Unique</c> now says all this, and a docstring is what a reader skips.
///         This is the half that goes red if somebody "fixes" the pattern into something a blur does
///         move — which would be a fine change and would invalidate every remark written about it.
///     </para>
/// </remarks>
public class TextureHarnessPatternTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>Away from every clamped edge, at the integer radius below.</summary>
    const int Inside = 32;

    /// <summary>Two texels each side, so the taps are exactly five and the spacing exactly one.</summary>
    const int Radius = 2;

    /// <summary>One axis of a box blur over an uploaded picture.</summary>
    static Bitmap Blur(IGraphicsDevice device, byte[] source) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Blur",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [
                        new("radius", Radius, TextureParameterUnit.TexelsAtBase),
                        new("stepX", 1f),
                        new("stepY", 0f)
                    ]
                }
            ],
            Outputs = [1]
        };

        using var uploads = new TextureUploads(device);

        uploads.Add(plan, 0, Side, Side, source);

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        return bake.Read(1);
    }

    /// <summary>⚠ A box blur returns <c>Unique</c> unchanged, which is why it cannot measure one.</summary>
    /// <remarks>
    ///     <b>The trap, made permanent.</b> This is not a defect in <c>Blur</c> — it is the correct
    ///     answer, and it is exactly why an "is this texel untouched" assertion written over this
    ///     pattern is a claim about the pattern. The comparison is over the interior, because the
    ///     clamped edges are the one place an affine field is <em>not</em> a fixed point: the taps
    ///     that fall outside repeat the edge, which pulls the mean toward it.
    /// </remarks>
    [Fact]
    public void A_symmetric_blur_returns_the_unique_pattern_unchanged() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"pattern invariance on {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var before = new Bitmap(Side, Side, source);
        var after = Blur(device, source);

        var largest = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = Radius; x < Side - Radius; x++) {
                for (var channel = 0; channel < 4; channel++) {
                    largest = Math.Max(
                        largest,
                        Math.Abs(
                            TextureKernelHarness.At(after, x, y, channel)
                            - TextureKernelHarness.At(before, x, y, channel)
                        )
                    );
                }
            }
        }

        Assert.True(
            largest <= 1,
            $"a radius-{Radius} box moved the affine pattern by {largest} on "
            + $"{TextureKernelHarness.Adapter(device)}, so the remark on Unique is out of date."
        );
    }

    /// <summary>The pattern that does measure a blur, and by how much it separates.</summary>
    /// <remarks>
    ///     A one-texel column checkerboard under a five-tap box is 102 or 153 depending on the parity
    ///     of the column — 0 and 255 going in. That is the order of magnitude an averaging assertion
    ///     wants, against the zero the affine pattern gives.
    /// </remarks>
    [Fact]
    public void The_checkerboard_moves_under_the_same_blur() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"pattern separation on {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Columns(Side);
        var before = new Bitmap(Side, Side, source);
        var after = Blur(device, source);

        Assert.True(
            TextureKernelHarness.LargestMove(before, after, 0) > 100,
            $"the checkerboard moved by only {TextureKernelHarness.LargestMove(before, after, 0)} on "
            + $"{TextureKernelHarness.Adapter(device)}."
        );

        // ⚠ And the affine pattern moved by nothing under the identical op — which is what makes
        // #694's guard necessary rather than pedantic. Both numbers come from the same dispatch of
        // the same kernel with the same radius, on the same machine, in the same test run.
        var affine = TextureKernelHarness.Unique(Side);

        Assert.Equal(0, TextureKernelHarness.LargestMove(new(Side, Side, affine), Blur(device, affine), 1));
    }

    /// <summary>⚠ The guard refuses a "held still" claim whose op moved nothing.</summary>
    /// <remarks>
    ///     <b>The instrument's own instrument.</b> <c>AssertHeldStill</c> exists to make the vacuous
    ///     half of a fixed-point assertion impossible; a guard nobody has seen fail is a guard nobody
    ///     knows works. So both directions are exercised — it passes on real evidence and fails on the
    ///     evidence a blurred affine pattern would have supplied.
    /// </remarks>
    [Fact]
    public void The_guard_wants_evidence_that_the_op_moved_something() {
        using var device = TextureKernelHarness.Open();

        var affine = TextureKernelHarness.Unique(Side);
        var checkers = TextureKernelHarness.Columns(Side);
        var affineBefore = new Bitmap(Side, Side, affine);
        var affineAfter = Blur(device, affine);
        var checkerPair = (new Bitmap(Side, Side, checkers), Blur(device, checkers));

        // The honest form: the claim is about the affine pattern, the evidence comes from the
        // checkerboard, and both are the same op.
        TextureKernelHarness.AssertHeldStill(
            affineBefore,
            affineAfter,
            Inside,
            Inside,
            0,
            1,
            checkerPair,
            100,
            $"blur on {TextureKernelHarness.Adapter(device)}"
        );

        // The form #694 was written about: the evidence is the affine pattern itself, which the op
        // does not move — so there is nothing to stand the claim on, and the guard says so instead of
        // passing.
        var refusal = Assert.Throws<Xunit.Sdk.FailException>(
            () => TextureKernelHarness.AssertHeldStill(
                affineBefore,
                affineAfter,
                Inside,
                Inside,
                0,
                1,
                (affineBefore, affineAfter),
                100,
                "the vacuous form"
            )
        );

        Assert.Contains("moved nothing", refusal.Message, StringComparison.Ordinal);
    }
}
