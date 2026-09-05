// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>A real adapter, or a loud skip.</summary>
/// <remarks>
///     ⚠ <b>Without a device a headless run falls back to the Null device on every platform and
///     exits 0</b>, and every picture this project compares would then be the claim that a black
///     image equals a black image. So the adapter is named in every message a device test writes,
///     and <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure.
/// </remarks>
static class TexturingDevice {
    /// <summary>A device, or a loud skip — or, when one was required, a failure.</summary>
    /// <returns>The device.</returns>
    public static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>What to put in a message so a reader knows which card produced it.</summary>
    /// <param name="device">The device.</param>
    /// <returns>Its name, kind and driver.</returns>
    public static string Adapter(VulkanDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        return $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";
    }
}
