// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The bindless table, against a driver with the validation layers on.
/// </summary>
/// <remarks>
///     <para>
///         <strong>This is the only test that can see any of it.</strong> Everything about an
///         unbounded binding is flags — partially-bound and update-after-bind on the binding, the
///         update-after-bind-pool bit on the layout, the matching bit on the pool, and four device
///         features that had to be enabled at <c>vkCreateDevice</c>, hours of frames earlier. Get any
///         one wrong and the Null backend agrees with you: it has no layout object, no pool and no
///         features to disagree with. The layers are what say no.
///     </para>
///     <para>
///         It found the reason <c>DescriptorBinding.IsUnbounded</c> asks about the kind. Raven's
///         reflection reports a storage buffer whose block ends in a runtime-sized array as
///         <c>Count == 0</c> — one descriptor, host-decided length — and reading that as an unbounded
///         descriptor array put an update-after-bind flag on every storage buffer in the culling
///         shaders. The layers named the rule and the feature nobody had enabled; nothing else in the
///         tree noticed at all.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests, for the reason
///         <see cref="GoldenImageTests" /> gives: <see cref="VulkanDiagnostics" /> is process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class BindlessTableDeviceTests {
    /// <summary>
    ///     A table is created, filled and destroyed without the layers saying anything.
    /// </summary>
    /// <remarks>
    ///     Written as one test rather than four because the flags are not independent: a pool without
    ///     the update-after-bind bit fails at allocation, a layout without it fails at creation, and a
    ///     write to a set the layout never declared fails at the write — so the sequence is the
    ///     assertion, and splitting it would produce three tests that each stop before the next one's
    ///     subject.
    /// </remarks>
    [Fact]
    public void A_table_is_created_filled_and_destroyed_cleanly() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        if (!BindlessTable.IsSupportedBy(device)) {
            // Not a failure. MoltenVK gates descriptor indexing behind Metal argument-buffer tier 2
            // (ADR-011), so a Mac is a legitimate "no" — and a capability check that reported yes
            // here is exactly what VulkanFeatures.Bindless exists to prevent.
            //
            // ⚠ Skipped and not returned. The judgement above is unchanged — an absent capability
            // must not redden a leg — but a bare return is recorded by xUnit as a *pass*, so this
            // read as a device test that had run and been satisfied on every runner whose device
            // says no, which is every runner but one. Skipping keeps the same verdict and stops it
            // being invisible. Same shape as VirtualGeometryGoldenTests' int64-atomics gate, which
            // docs/plan/22 § phase 6 settled the same way.
            Assert.Skip("The device offers no bindless descriptor indexing (ADR-011), which this test is gated on.");

            return;
        }

        VulkanDiagnostics.Reset();

        // Deliberately smaller than the device's ceiling, so what is exercised is a table sized by a
        // host rather than one that happens to match whatever the driver reported.
        using var table = new BindlessTable(device, capacity: 1024, name: "Golden.Bindless");

        Assert.Equal(1024, table.Capacity);

        var views = new TextureViewHandle[32];

        for (var index = 0; index < views.Length; index++) {
            var texture = device.CreateTexture(
                new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: $"bindless {index}")
            );

            views[index] = device.CreateTextureView(texture);
            Assert.Equal((uint)index, table.Add(views[index]));
        }

        // Every slot the table did not fill stays unwritten, and the set is still usable — which is
        // what partially-bound means and what an engine holding a thousand-slot table for thirty-two
        // textures depends on. A layout without the flag makes this the failure.
        Assert.Equal(32, table.Count);
        Assert.Equal(32, table.WriteCount);

        // The far end of the array, so a descriptorCount that came out as one — which is what the
        // backend built before it knew about MaxBindlessDescriptors — is a write past the end rather
        // than an assertion nobody made.
        device.UpdateDescriptorSet(table.Set, [DescriptorWrite.Texture(0, views[0], table.Capacity - 1)]);

        Clean();
    }

    /// <summary>
    ///     A device that reports the capability reports a usable ceiling with it.
    /// </summary>
    /// <remarks>
    ///     The pair, on real hardware. A driver offering <c>VK_EXT_descriptor_indexing</c> and a zero
    ///     update-after-bind ceiling would pass every capability check in the engine and refuse the
    ///     first layout — so the two are asserted together, where a real number exists to assert.
    /// </remarks>
    [Fact]
    public void The_capability_comes_with_a_ceiling() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var features = owned.Device.Features;

        Assert.Equal(features.HasBindless, features.MaxBindlessDescriptors > 0);
    }

    /// <summary>Refuses an answer produced alongside validation errors.</summary>
    static void Clean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            throw new InvalidOperationException(
                "The table produced validation errors, so nothing it did means anything: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
