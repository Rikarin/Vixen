// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>What a caller's own texture has to have been created with, and the refusal when it was not.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/722">#722</a>, and the reason it is a
///         refusal rather than a comment.</b> A <see cref="TextureOp.Cpu" /> op reading an external
///         image issues <c>vkCmdCopyImageToBuffer</c> on the caller's texture and transitions it
///         through <see cref="ResourceState.CopySource" /> either side of that copy. All three of
///         those require an image created with <c>TRANSFER_SRC</c>, and for a whole batch the
///         requirement was stated nowhere, checked nowhere and violated by the suite's own harness.
///     </para>
///     <para>
///         ⚠ <b>On the Null device on purpose, and it is a check about the plan rather than about a
///         picture.</b> The declaration is compared against what the plan does to each image, which
///         needs no adapter — and putting it on the Null device is what makes it run everywhere,
///         including the machines where a violation would otherwise be caught by nothing at all.
///         <c>TextureValidationDeviceTests</c> is the other half: it puts the layers themselves behind
///         the same requirement, on a device, and it is the half that a wrong declaration slips past.
///     </para>
///     <para>
///         ⚠ <b>And what a Null-device test cannot do is decide what the requirement <em>is</em></b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/745">#745</a>. A case here asserted that an
///         image only a CPU op reads needs no <c>Sampled</c>; it passed, because the Null device makes
///         no image views and issues no barriers, and on a real adapter the layers reported three
///         errors for the same plan. A rule this file states has to come from the specification and
///         from what the evaluator's own code does, and the accepted cases below are only evidence
///         that the rule is not "refuse everything".
///     </para>
/// </remarks>
public class TextureExternalUsageTests {
    static TexturePlan CopiedThenSampled() =>
        new() {
            BaseWidth = 16,
            BaseHeight = 16,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "Transpose", Output = 1, Inputs = [0], Cpu = new TransposeRgba8() },
                new() {
                    Kernel = "Invert",
                    Output = 2,
                    Inputs = [0],
                    Parameters = [new("invertR", 1f), new("invertG", 1f), new("invertB", 1f), new("invertA", 1f)]
                }
            ],
            Outputs = [1, 2]
        };

    static TexturePlan Sampled() =>
        new() {
            BaseWidth = 16,
            BaseHeight = 16,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Invert",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("invertR", 1f), new("invertG", 1f), new("invertB", 1f), new("invertA", 1f)]
                }
            ],
            Outputs = [1]
        };

    static TextureHandle Source(NullDevice device, TextureUsage usage) =>
        device.CreateTexture(new(PixelFormat.Rgba8UNorm, 16, 16, usage, Name: "source"));

    /// <summary>
    ///     ⚠ A CPU op over an image declared as a plain handle is refused, because a plain handle
    ///     declares only that it can be sampled.
    /// </summary>
    /// <remarks>
    ///     <b>The overload that takes bare handles is the one every other suite uses</b>, and it is
    ///     right for them: a dispatch needs nothing but <see cref="TextureUsage.Sampled" />. The
    ///     refusal is what stops that convenience from becoming the silent path for the one plan shape
    ///     it cannot express.
    /// </remarks>
    [Fact]
    public void A_cpu_op_over_an_external_supplied_as_a_bare_handle_is_refused() {
        using var device = new NullDevice(new());

        var source = Source(device, TextureUsage.Sampled | TextureUsage.CopySource);

        using var evaluator = new TexturePlanEvaluator(device);

        var refusal = Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(CopiedThenSampled(), new Dictionary<int, TextureHandle> { [0] = source })
        );

        Assert.Contains("CopySource", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Image 0", refusal.Message, StringComparison.Ordinal);

        device.Destroy(source);
    }

    /// <summary>The same plan runs once the caller says what the texture was created with.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that stops the refusal from being "no plan with a CPU op may run".</b> A
    ///     guard with no accepted case is indistinguishable from a feature that was removed, and this
    ///     is the same plan, the same texture and the same evaluator — only the declaration differs.
    /// </remarks>
    [Fact]
    public void The_same_plan_runs_when_the_caller_declares_the_usage() {
        using var device = new NullDevice(new());

        var source = Source(device, TextureUsage.Sampled | TextureUsage.CopySource);

        using var evaluator = new TexturePlanEvaluator(device);

        using var bake = evaluator.Evaluate(
            CopiedThenSampled(),
            new Dictionary<int, TextureExternal> {
                [0] = new(source, TextureUsage.Sampled | TextureUsage.CopySource)
            }
        );

        Assert.Equal(1, bake.Dispatches);

        device.Destroy(source);
    }

    /// <summary>An external a dispatch samples is refused when the declaration has no Sampled in it.</summary>
    /// <remarks>
    ///     The other half of the requirement, and the one a caller who reaches for the declaring
    ///     overload can now get wrong on its own: a texture created for transfers only cannot be bound
    ///     to a sampler, whatever the plan says.
    /// </remarks>
    [Fact]
    public void A_dispatch_over_an_external_declared_without_Sampled_is_refused() {
        using var device = new NullDevice(new());

        var source = Source(device, TextureUsage.CopySource | TextureUsage.CopyDestination);

        using var evaluator = new TexturePlanEvaluator(device);

        var refusal = Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(
                Sampled(),
                new Dictionary<int, TextureExternal> {
                    [0] = new(source, TextureUsage.CopySource | TextureUsage.CopyDestination)
                }
            )
        );

        Assert.Contains("Sampled", refusal.Message, StringComparison.Ordinal);

        device.Destroy(source);
    }

    /// <summary>A plan with an external image and no externals at all is still refused by image.</summary>
    /// <remarks>
    ///     ⚠ <b>The path the two overloads share, and the one a null could have fallen through.</b>
    ///     Omitting the argument reaches the declaring overload as an empty map rather than as a null,
    ///     so the refusal has to come from the plan's own external images and not from the map being
    ///     absent — a check written the other way round would pass a plan whose bitmap input was
    ///     simply never supplied.
    /// </remarks>
    [Fact]
    public void A_plan_with_an_external_image_and_no_externals_is_refused() {
        using var device = new NullDevice(new());
        using var evaluator = new TexturePlanEvaluator(device);

        var refusal = Assert.Throws<ArgumentException>(() => evaluator.Evaluate(Sampled()));

        Assert.Contains("Image 0 is external", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ An external only a CPU op reads is asked for Sampled anyway, because the evaluator views
    ///     it and holds it readable whatever the plan does with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/745">#745</a>, and this test used
    ///         to assert the opposite.</b> It was called
    ///         <c>An_external_only_a_cpu_op_reads_needs_no_Sampled</c>, it passed, and what it proved
    ///         was that the Null device validates nothing: <c>ExternalViews</c> creates a view over
    ///         every external before any op runs, and <c>OnCpu</c> names
    ///         <c>SHADER_READ_ONLY_OPTIMAL</c> on both sides of its copy. All three of those are
    ///         invalid on an image created for transfers alone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured rather than reasoned, on an Apple M1 Max with the validation layers
    ///         on</b>: the plan below, run against a <c>CopySource | CopyDestination</c> texture
    ///         through the permissive rule, produced <c>VUID-VkImageViewCreateInfo-image-04441</c>
    ///         once and <c>VUID-VkImageMemoryBarrier-oldLayout-01211</c> twice — and a correct
    ///         picture, which is why nothing in this suite could see it. This assertion is what the
    ///         specification says; the previous one was what MoltenVK tolerates.
    ///     </para>
    ///     <para>
    ///         <b>The narrower requirement is still available and still worth having</b> — it is why
    ///         the base is <em>Sampled</em> rather than <em>everything</em>. A dispatch adds nothing,
    ///         and only a <see cref="TextureOp.Cpu" /> op adds <see cref="TextureUsage.CopySource" />,
    ///         so a caller who never copies is never asked for <c>TransferSrc</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_external_only_a_cpu_op_reads_is_refused_without_Sampled() {
        using var device = new NullDevice(new());

        var source = Source(device, TextureUsage.CopySource);

        var plan = new TexturePlan {
            BaseWidth = 16,
            BaseHeight = 16,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Transpose", Output = 1, Inputs = [0], Cpu = new TransposeRgba8() }],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);

        var refusal = Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(
                plan,
                new Dictionary<int, TextureExternal> { [0] = new(source, TextureUsage.CopySource) }
            )
        );

        Assert.Contains("Sampled", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("04441", refusal.Message, StringComparison.Ordinal);

        // And the same plan runs the moment the image is one a view may be made over.
        var viewable = Source(device, TextureUsage.Sampled | TextureUsage.CopySource);

        using (var bake = evaluator.Evaluate(
            plan,
            new Dictionary<int, TextureExternal> {
                [0] = new(viewable, TextureUsage.Sampled | TextureUsage.CopySource)
            }
        )) {
            Assert.Equal(0, bake.Dispatches);
        }

        device.Destroy(viewable);
        device.Destroy(source);
    }
}
