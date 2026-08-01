// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Raven.Gpu.Tests;

/// <summary>Whether a missing driver is a reason to skip or a reason to fail.</summary>
/// <remarks>
///     <para>
///         Skipping is right on a machine with no Vulkan installed. It is exactly wrong on the CI
///         leg that installs lavapipe precisely so that this project can run — there, a runner that
///         lost its driver would report a green build having proved nothing, which is the most
///         expensive kind of green there is.
///     </para>
///     <para>
///         So <c>VIXEN_REQUIRE_VULKAN=1</c> turns every skip here into a failure naming what was
///         missing. Copied rather than shared with the other GPU test assemblies, which is what they
///         do to each other as well: it is eight lines of policy, and a test assembly reaching into
///         another test assembly to get them would be a stranger arrangement than the duplication.
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
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the Raven numeric gates may not skip: {message}");
        }

        Assert.Skip(message);
    }
}
