// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Graphics.Vulkan;

/// <summary>One physical device, reduced to what choosing between them needs.</summary>
/// <remarks>
///     A plain record rather than a <c>VkPhysicalDevice</c>, so the policy below is a pure function
///     that can be tested on a machine with no Vulkan — which is where most of the interesting cases
///     live anyway: a laptop with two GPUs, a CI runner with only lavapipe, a driver that reports
///     1.0.
/// </remarks>
/// <param name="Name">The driver's name for it.</param>
/// <param name="Kind">What kind of device it is.</param>
/// <param name="DeviceMemory">Device-local memory in bytes, or <c>0</c> if it does not say.</param>
/// <param name="ApiVersion">The Vulkan version it supports, packed as Vulkan packs it.</param>
/// <param name="HasSwapchain">Whether it offers <c>VK_KHR_swapchain</c>.</param>
/// <param name="CanPresent">Whether one of its queues can present to the surface we care about.</param>
/// <param name="HasGraphicsQueue">Whether it has a queue family that can draw.</param>
readonly record struct AdapterCandidate(
    string Name,
    AdapterKind Kind,
    ulong DeviceMemory,
    uint ApiVersion,
    bool HasSwapchain,
    bool CanPresent,
    bool HasGraphicsQueue
);

/// <summary>Choosing which GPU to run on, and saying why when none will do.</summary>
static class AdapterSelection {
    /// <summary>Vulkan 1.1, the floor <c>docs/plan/05</c> states and this enforces.</summary>
    /// <remarks>
    ///     Packed the way Vulkan packs a version: major in bits 22–31, minor in 12–21. Written out
    ///     rather than taken from a constant so the arithmetic is visible — an off-by-one here
    ///     rejects every device on the machine.
    /// </remarks>
    public const uint MinimumApiVersion = (1u << 22) | (1u << 12);

    /// <summary>Whether a candidate meets the floor at all.</summary>
    /// <param name="candidate">The device.</param>
    /// <param name="presentRequired">Whether it has to be able to present — false for a headless
    /// device doing offscreen work.</param>
    /// <param name="reason">Why not, when it does not.</param>
    public static bool IsUsable(
        in AdapterCandidate candidate,
        bool presentRequired,
        [NotNullWhen(false)] out string? reason
    ) {
        if (candidate.ApiVersion < MinimumApiVersion) {
            reason = $"'{candidate.Name}' reports Vulkan {Describe(candidate.ApiVersion)}; Vixen needs 1.1.";
            return false;
        }

        if (!candidate.HasGraphicsQueue) {
            reason = $"'{candidate.Name}' has no queue family that can draw.";
            return false;
        }

        if (presentRequired && !candidate.HasSwapchain) {
            reason = $"'{candidate.Name}' does not offer VK_KHR_swapchain.";
            return false;
        }

        if (presentRequired && !candidate.CanPresent) {
            reason = $"'{candidate.Name}' cannot present to this surface.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>How much we want a device, higher being better.</summary>
    /// <param name="candidate">The device.</param>
    /// <remarks>
    ///     <para>
    ///         Discrete, then integrated, then software, then whatever the driver would not classify.
    ///         Memory breaks ties within a class, which picks the better of two discrete cards
    ///         without needing a vendor table.
    ///     </para>
    ///     <para>
    ///         <b>Software is scored, not skipped.</b> lavapipe is a conformant Vulkan 1.3 driver
    ///         with no GPU, and it is what makes the backend, the validation layers and the
    ///         golden-image suite testable on a standard CI runner (<c>docs/plan/10 § Linux</c>). A
    ///         selector that filtered it out would make the most valuable CI leg in the plan
    ///         impossible, so it ranks last and stays in the list.
    ///     </para>
    /// </remarks>
    public static long Score(in AdapterCandidate candidate) {
        var tier = candidate.Kind switch {
            AdapterKind.Discrete => 3L,
            AdapterKind.Integrated => 2L,
            AdapterKind.Software => 1L,
            _ => 0L
        };

        // Memory in megabytes, so a tier is always worth more than any amount of memory.
        return (tier << 40) + (long)Math.Min(candidate.DeviceMemory >> 20, (1L << 40) - 1);
    }

    /// <summary>Picks the best usable device.</summary>
    /// <param name="candidates">Everything the instance enumerated.</param>
    /// <param name="presentRequired">Whether the chosen device has to be able to present.</param>
    /// <param name="index">Which candidate won.</param>
    /// <param name="reason">Why none did, when none did.</param>
    /// <returns>Whether a device was chosen.</returns>
    /// <remarks>
    ///     The failure message names every device and why each was rejected. "No suitable GPU found"
    ///     is the least useful error a graphics engine can produce, and the information needed to do
    ///     better is right here at the moment it is thrown away.
    /// </remarks>
    public static bool TrySelect(
        ReadOnlySpan<AdapterCandidate> candidates,
        bool presentRequired,
        out int index,
        [NotNullWhen(false)] out string? reason
    ) {
        index = -1;

        if (candidates.IsEmpty) {
            reason = "The Vulkan instance enumerated no physical devices. On macOS this is usually a "
                + "missing or misconfigured ICD: check VK_ICD_FILENAMES and that the instance was "
                + "created with VK_KHR_portability_enumeration.";

            return false;
        }

        var best = long.MinValue;
        var rejections = new List<string>();

        for (var candidate = 0; candidate < candidates.Length; candidate++) {
            if (!IsUsable(candidates[candidate], presentRequired, out var why)) {
                rejections.Add(why);
                continue;
            }

            var score = Score(candidates[candidate]);

            if (score > best) {
                best = score;
                index = candidate;
            }
        }

        if (index >= 0) {
            reason = null;
            return true;
        }

        reason = $"No usable Vulkan device among {candidates.Length}: {string.Join("; ", rejections)}";
        return false;
    }

    /// <summary>A packed Vulkan version as <c>major.minor.patch</c>.</summary>
    /// <param name="version">The packed version.</param>
    public static string Describe(uint version) =>
        $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";
}
