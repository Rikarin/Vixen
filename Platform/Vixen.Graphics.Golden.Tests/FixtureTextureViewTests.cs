// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>What the fixture asks the driver for while it builds, and who reads the answer.</summary>
/// <remarks>
///     <para>
///         Two properties, and the second is the one that matters. The first is that
///         <c>Fixture.Owned</c> does not create a texture view for a texture whose usage makes one
///         illegal — <c>VUID-VkImageViewCreateInfo-image-04441</c>. The second is that a validation
///         error raised while a fixture is <em>constructing</em> its resources reaches the check in
///         <c>Fixture.Render</c> at all.
///     </para>
///     <para>
///         ⚠ <b>It did not, and that is the whole of #1174.</b> <c>Render</c> called
///         <c>VulkanDiagnostics.Reset()</c> on entry, and every resource a fixture owns is created
///         before it renders — so the counter the assertion three lines later reads had just been
///         zeroed by the assertion's own method. A whole golden run (274 tests, 271 passed, 3
///         skipped, 0 failed) carried exactly one <c>vkCreateImageView</c> the spec forbids and
///         reported a clean suite. The error was real, it was printed, and no assertion in the
///         repository could see it.
///     </para>
///     <para>
///         ⚠ <b>Ask what these print on the day the layers are not loaded.</b> A machine without the
///         validation package renders correct pictures and reports zero errors, which is
///         character-for-character what a clean run reports — the same trap #143's first leg
///         records. So both tests assert <c>ValidationEnabled</c> before believing a count, and the
///         second asserts the deliberate violation was actually noticed before asserting anything
///         about who noticed it.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide, so a second fixture holding a device at the same time would have its errors
///         attributed here.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class FixtureTextureViewTests {
    /// <summary>A texture that is only ever a transfer endpoint gets no view.</summary>
    /// <remarks>
    ///     The picture-free half of #1174: <c>CopyBetweenTextures</c>' <c>origin</c> is
    ///     <c>CopySource | CopyDestination</c>, never sampled and never an attachment, and it never
    ///     touches the view it was handed. Asking for one anyway was a spec violation the RHI
    ///     faithfully performed.
    /// </remarks>
    [Fact]
    public void ATransferOnlyTextureGetsNoView() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;

        Assert.True(
            owned.Device.ValidationEnabled,
            "the validation layers are not active on this device, so a count of zero errors below "
            + "would mean nothing was asked rather than nothing was wrong."
        );

        var transfer = owned.Owned(
            "transfer only",
            TextureUsage.CopySource | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm,
            4,
            4
        );

        Assert.False(
            transfer.View.IsValid,
            "the fixture created a view over a texture whose usage has no view-legal bit in it, "
            + "which is VUID-VkImageViewCreateInfo-image-04441."
        );

        Assert.Equal(0, VulkanDiagnostics.ErrorCount);
    }

    /// <summary>A texture the shader will read still gets one.</summary>
    /// <remarks>
    ///     ⚠ The other half, and without it the test above is satisfied by a fixture that stopped
    ///     creating views altogether — every sampled fixture in the suite would then fail, but this
    ///     file would be green and would be the file a reader consults to find out why.
    /// </remarks>
    [Fact]
    public void ASampledTextureStillGetsOne() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;

        var sampled = owned.Owned(
            "sampled",
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm,
            4,
            4
        );

        Assert.True(sampled.View.IsValid, "a sampled texture needs a view and the fixture withheld one.");
        Assert.Equal(0, VulkanDiagnostics.ErrorCount);
    }

    /// <summary>A validation error raised during setup fails the fixture's next frame.</summary>
    /// <remarks>
    ///     <para>
    ///         The instrument, tested rather than assumed. The violation is made on purpose — a view
    ///         over a transfer-only texture, the exact call #1174 found — and then a perfectly legal
    ///         frame is rendered. That frame must fail, because it was drawn on a device that had
    ///         already been told it was doing something the spec forbids.
    ///     </para>
    ///     <para>
    ///         The middle assertion is what stops this passing vacuously: if the layers said nothing
    ///         about a call that is unambiguously invalid, they are not listening, and every
    ///         validation assertion in this assembly is decoration.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AValidationErrorRaisedBeforeTheFrameFailsIt() {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;

        Assert.True(
            owned.Device.ValidationEnabled,
            "the validation layers are not active on this device, so nothing below can be observed."
        );

        var colour = owned.ColourTarget("setup error");

        var transfer = owned.Owned(
            "transfer only",
            TextureUsage.CopySource | TextureUsage.CopyDestination,
            PixelFormat.Rgba8UNorm,
            4,
            4
        );

        // The violation, deliberately, through the device rather than through the fixture — the
        // fixture no longer makes this call and the point is what happens when something does.
        var illegal = owned.Device.CreateTextureView(transfer.Texture);
        owned.Owns(() => owned.Device.Destroy(illegal));

        Assert.True(
            VulkanDiagnostics.ErrorCount > 0,
            "a view over a CopySource|CopyDestination texture drew no complaint, so the layers are "
            + "loaded and silent — which is worse than absent, because everything looks clean."
        );

        owned.Graph.AddPass("setup error", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        var thrown = Assert.Throws<InvalidOperationException>(() => owned.Render(colour));

        Assert.Contains("vkCreateImageView", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    /// <remarks>
    ///     The suite's five-line guard, carried by hand the way every other device opener here
    ///     carries it. <c>DeviceGuardTests</c> is what makes carrying it non-optional.
    /// </remarks>
    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the fixture's own device tests may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }
}
