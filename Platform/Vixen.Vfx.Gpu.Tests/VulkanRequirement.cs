// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>Whether a missing driver is a reason to skip or a reason to fail.</summary>
/// <remarks>
///     <para>
///         Skipping is right on a machine with no Vulkan installed: the rest of the suite should
///         still run, and the CPU half of everything asserted here is already covered without a
///         device. It is exactly wrong on the CI leg that installs lavapipe precisely so that this
///         project can run — there, a runner that lost its driver would report a green build having
///         proved nothing, which is the most expensive kind of green there is.
///     </para>
///     <para>
///         So <c>VIXEN_REQUIRE_VULKAN=1</c> turns every skip here into a failure naming what was
///         missing. Copied rather than shared with <c>Vixen.Graphics.Vulkan.Tests</c>, which is what
///         the golden-image project does too: it is eight lines of policy, and a test assembly that
///         reached into another test assembly to get them would be a stranger arrangement than the
///         duplication.
///     </para>
/// </remarks>
static class VulkanRequirement {
    /// <summary>Skips, or fails, depending on whether a driver was promised.</summary>
    /// <param name="available">Whether a device was created.</param>
    /// <param name="reason">Why it was not, when it was not.</param>
    public static void Available(bool available, string? reason) {
        if (available) {
            return;
        }

        var message = reason ?? "no Vulkan";

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the GPU agreement tests may not skip: {message}");
        }

        Assert.Skip(message);
    }
}
