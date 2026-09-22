// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>What one pipeline did to the size of a driver's cache blob.</summary>
public enum PipelineCacheGrowth {
    /// <summary>The blob with one pipeline in it is longer than the blob with none: the claim holds.</summary>
    Grew,

    /// <summary>
    ///     Both blobs are the same length and at least a header: a working driver that keeps nothing
    ///     per pipeline, which the specification permits.
    /// </summary>
    StoresNothing,

    /// <summary>The blob with one pipeline in it is shorter than the blob with none.</summary>
    Shrank,

    /// <summary>One of the blobs is shorter than the header every blob must begin with.</summary>
    Garbage
}

/// <summary>
///     Decides what a driver's pipeline cache proved, separately from the device that produced the
///     numbers, so the decision can be tested without one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>"This driver does not cache" is not the same fact as "there is no driver", and
///         <c>VIXEN_REQUIRE_VULKAN</c> exists to catch the second.</b> lavapipe compiles every
///         pipeline the suite asks for and answers <c>vkGetPipelineCacheData</c> with the 32-byte
///         header whatever went in, because a software rasterizer has no compiled shader binaries to
///         keep — and the size of the blob is entirely the implementation's business. The first
///         version of the test routed that outcome through <see cref="VulkanRequirement" />, and on
///         the one CI leg with a driver it failed every run with a message naming three causes, none
///         of them the real one (#1274).
///     </para>
///     <para>
///         So <see cref="PipelineCacheGrowth.StoresNothing" /> is a skip everywhere, with the adapter's name in it, and the
///         two outcomes the flag is <em>for</em> stay hard failures: a cache that shrinks when a
///         pipeline is added, and a blob too short to be one at all. The caller also has to show the
///         driver takes its own empty blob back before skipping — a file that is the right length and
///         not this driver's is garbage of a shape this decision cannot see.
///     </para>
/// </remarks>
static class PipelineCacheGrowthDecision {
    /// <summary>What the two blob sizes say.</summary>
    /// <param name="nothing">The blob a device that compiled no pipeline wrote, in bytes.</param>
    /// <param name="learned">The blob a device that compiled one pipeline wrote, in bytes.</param>
    public static PipelineCacheGrowth Decide(long nothing, long learned) {
        if (nothing < VulkanPipelineCacheBlob.HeaderSize || learned < VulkanPipelineCacheBlob.HeaderSize) {
            return PipelineCacheGrowth.Garbage;
        }

        if (learned > nothing) {
            return PipelineCacheGrowth.Grew;
        }

        return learned == nothing ? PipelineCacheGrowth.StoresNothing : PipelineCacheGrowth.Shrank;
    }

    /// <summary>The sentence a skip or a failure carries, naming the adapter it is true of.</summary>
    public static string Describe(PipelineCacheGrowth growth, string adapter, long nothing, long learned) =>
        growth switch {
            PipelineCacheGrowth.Grew => $"'{adapter}' grew its cache from {nothing} to {learned} bytes for one pipeline",
            PipelineCacheGrowth.StoresNothing =>
                $"'{adapter}' stores nothing in a pipeline cache: one pipeline made it {learned} bytes and no "
                + $"pipelines made it {nothing}, which the specification permits, so whether a cache seeds the next "
                + "device cannot be shown on this driver",
            PipelineCacheGrowth.Shrank =>
                $"'{adapter}' shrank its cache from {nothing} to {learned} bytes when a pipeline was added",
            PipelineCacheGrowth.Garbage =>
                $"'{adapter}' wrote {nothing} and {learned} bytes, and a pipeline cache blob begins with a "
                + $"{VulkanPipelineCacheBlob.HeaderSize}-byte header",
            _ => throw new ArgumentOutOfRangeException(nameof(growth), growth, null)
        };
}
