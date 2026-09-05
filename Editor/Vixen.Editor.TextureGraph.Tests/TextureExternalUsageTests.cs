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

    /// <summary>
    ///     An external only a CPU op reads is not asked for Sampled, because nothing binds it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A guard that demanded every usage of every external would be satisfied by the defect
    ///     it was written for</b> and refuse a legal plan besides. The requirement is computed from
    ///     what the plan does to each image, so an image that is only ever copied out of needs only
    ///     <see cref="TextureUsage.CopySource" /> — this passes a declaration with no
    ///     <see cref="TextureUsage.Sampled" /> in it at all.
    /// </remarks>
    [Fact]
    public void An_external_only_a_cpu_op_reads_needs_no_Sampled() {
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

        using var bake = evaluator.Evaluate(
            plan,
            new Dictionary<int, TextureExternal> { [0] = new(source, TextureUsage.CopySource) }
        );

        Assert.Equal(0, bake.Dispatches);

        device.Destroy(source);
    }
}
