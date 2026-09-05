// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>
///     The bake's command stream with the validation layers watching, asserting that they said
///     nothing.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The only witness in this suite for a resource layout, a barrier or a usage bit</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/712">#712</a>. Every other test here reads
///         a picture, and a picture is not a witness for any of the three on the adapter this is
///         developed on: an Apple M1 Max reads an image left in <c>TRANSFER_SRC_OPTIMAL</c> perfectly
///         well, and MoltenVK does not enforce usage bits at all. Deleting the restore barrier in
///         <c>TexturePlanEvaluator.OnCpu</c> leaves <c>TextureCpuOpDeviceTests</c> entirely green;
///         it turns this red.
///     </para>
///     <para>
///         <b>Why it can exist here now.</b> <c>VulkanDiagnostics</c> is process-wide, so attributing
///         a message to a test needs the tests not to overlap — see <c>AssemblyParallelism.cs</c>,
///         which serialises this assembly for exactly this one test. Before that, a validation
///         assertion in this suite would have failed for another test's reason, which is worse than no
///         test.
///     </para>
///     <para>
///         ⚠ <b>What it prints on the day it does not run.</b> Nothing: a device that came up without
///         the validation layer <em>skips</em>, loudly, rather than passing — an instrument that
///         reports success when it is absent is the failure mode this whole batch is about, and
///         <c>VIXEN_REQUIRE_VULKAN=1</c> turns both that skip and the missing-device skip into
///         failures.
///     </para>
/// </remarks>
public class TextureValidationDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    static TextureOp Invert(int target, int source) =>
        new() {
            Kernel = "Invert",
            Output = target,
            Inputs = [source],
            Parameters = [new("invertR", 1f), new("invertG", 1f), new("invertB", 1f), new("invertA", 1f)]
        };

    /// <summary>
    ///     A plan that copies out of the caller's own image and then dispatches over it again is
    ///     validation-clean.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The shape is <c>TextureCpuOpDeviceTests</c>'s second plan</b>, because that is the
    ///         one that moves an image the evaluator does not own: a <see cref="TextureOp.Cpu" /> op
    ///         reads external image 0, which takes it to <see cref="ResourceState.CopySource" /> and
    ///         back, and op 3 samples it afterwards. Four barriers and one copy, each of which the
    ///         layers have a rule about and the pictures do not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three separate messages were live here when this was written</b>, and every one of
    ///         them was invisible to the two pictures:
    ///         <c>VUID-vkCmdCopyImageToBuffer-srcImage-00186</c> and two of
    ///         <c>VUID-VkImageMemoryBarrier-oldLayout-01212</c>, all three saying the caller's texture
    ///         had no <c>TRANSFER_SRC</c> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/722">#722</a>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bake_that_reads_the_callers_own_image_produces_no_validation_messages() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");
        TextureKernelHarness.RequireValidation(device);

        var (texture, staging) = TextureKernelHarness.Upload(device, TextureKernelHarness.Unique(Side), Side, Side);

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                new() { Kernel = "Transpose", Output = 1, Inputs = [0], Cpu = new TransposeRgba8() },
                Invert(2, 1),
                Invert(3, 0)
            ],
            Outputs = [2, 3]
        };

        // After the device and the upload, because both are somebody else's claim, and before the
        // evaluation, which is this one's.
        VulkanDiagnostics.Reset();

        using (var evaluator = new TexturePlanEvaluator(device)) {
            using var bake = evaluator.Evaluate(plan, TextureKernelHarness.Externals(0, texture));

            bake.Read(2);
            bake.Read(3);
        }

        var errors = VulkanDiagnostics.ErrorCount;
        var warnings = VulkanDiagnostics.WarningCount;
        var messages = string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages);

        device.Destroy(staging);
        device.Destroy(texture);

        Assert.True(
            errors == 0 && warnings == 0,
            $"The validation layers reported {errors} error(s) and {warnings} warning(s) while a plan "
            + $"with a CPU op over the caller's image ran on {TextureKernelHarness.Adapter(device)}:"
            + Environment.NewLine
            + messages
        );
    }
}
