// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>Whether a missing driver is a reason to skip or a reason to fail.</summary>
/// <remarks>
///     <para>
///         Skipping is right on a developer's machine with no Vulkan installed: the rest of the suite
///         should still run. It is exactly wrong on a CI leg whose entire purpose is to exercise the
///         backend — there, a runner that lost its driver would report a green build having proved
///         nothing at all, which is the most expensive kind of green there is.
///     </para>
///     <para>
///         So the leg that installs lavapipe sets <c>VIXEN_REQUIRE_VULKAN=1</c>, and every skip in
///         this project becomes a failure that names what was missing. The same guard is why the SDL
///         install steps exist in the workflow: <c>docs/plan/10</c> already records that a leg which
///         silently skips its subject is worse than no leg.
///     </para>
/// </remarks>
static class VulkanRequirement {
    /// <summary>Whether the environment insists a driver be present.</summary>
    public static bool Demanded =>
        Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE";

    /// <summary>Skips, or fails, depending on whether a driver was promised.</summary>
    /// <param name="available">Whether the thing under test is available.</param>
    /// <param name="reason">Why it is not, when it is not.</param>
    public static void Available(bool available, string? reason) {
        if (available) {
            return;
        }

        var message = reason ?? "no Vulkan";

        if (Demanded) {
            Assert.Fail(
                $"VIXEN_REQUIRE_VULKAN is set, so this test may not skip: {message}. Either the "
                + "runner's driver or validation layers are missing, or the backend declined a device "
                + "it should have accepted."
            );
        }

        Assert.Skip(message);
    }
}
