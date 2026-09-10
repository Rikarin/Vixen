// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Graphics.Vulkan;

/// <summary>
///     The 32 bytes every <c>VkPipelineCache</c> blob starts with, and whether one found on disk may
///     be handed back to this driver.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A cache blob is driver- and device-specific, and feeding a foreign one to
///         <c>vkCreatePipelineCache</c> is undefined behaviour rather than an error code.</b> The
///         specification is explicit that an implementation need not validate what it is given; what
///         it promises instead is that the blob begins with <c>VkPipelineCacheHeaderVersionOne</c> —
///         a length, a version, the vendor and device ids and the driver's own 16-byte UUID —
///         precisely so that the application can decide. So this check is part of the feature rather
///         than a nicety: without it a driver update, or a laptop's second GPU, turns a cache file
///         into a crash on the next boot.
///     </para>
///     <para>
///         The UUID is the half that catches a driver update on the same GPU, which is the common
///         case; the vendor and device ids catch the other machine. All three are compared because
///         any one of them alone lets a real case through.
///     </para>
/// </remarks>
static class VulkanPipelineCacheBlob {
    /// <summary>How long <c>VkPipelineCacheHeaderVersionOne</c> is, in bytes.</summary>
    internal const int HeaderSize = 32;

    /// <summary>How long the driver's own identifier is, in bytes.</summary>
    internal const int UuidSize = 16;

    /// <summary><c>VK_PIPELINE_CACHE_HEADER_VERSION_ONE</c>, the only version there is.</summary>
    internal const uint HeaderVersionOne = 1;

    /// <summary>Whether a blob read from disk was written by this driver on this device.</summary>
    /// <param name="blob">What the file held.</param>
    /// <param name="vendorId">This adapter's <c>VkPhysicalDeviceProperties::vendorID</c>.</param>
    /// <param name="deviceId">This adapter's <c>VkPhysicalDeviceProperties::deviceID</c>.</param>
    /// <param name="uuid">This adapter's <c>pipelineCacheUUID</c>, sixteen bytes.</param>
    /// <param name="reason">Why it may not be used, when it may not; empty when it may.</param>
    /// <returns>Whether the blob may be handed to <c>vkCreatePipelineCache</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The header is read little-endian, and the specification does not promise it is.</b>
    ///     It says the fields are written in the format of the implementation — which is why a blob
    ///     is not portable in the first place. Reading it little-endian is therefore not a
    ///     portability claim: it is how a big-endian driver's blob comes to fail the vendor-id
    ///     comparison and be discarded, which is the outcome wanted there anyway.
    /// </remarks>
    internal static bool Matches(
        ReadOnlySpan<byte> blob,
        uint vendorId,
        uint deviceId,
        ReadOnlySpan<byte> uuid,
        out string reason
    ) {
        if (blob.Length < HeaderSize) {
            reason = $"it is {blob.Length} bytes and the header alone is {HeaderSize}";
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(blob);

        if (headerSize < HeaderSize || headerSize > blob.Length) {
            reason = $"its header claims to be {headerSize} bytes of a {blob.Length}-byte file";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);

        if (version != HeaderVersionOne) {
            reason = $"its header version is {version} rather than {HeaderVersionOne}";
            return false;
        }

        var blobVendor = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        var blobDevice = BinaryPrimitives.ReadUInt32LittleEndian(blob[12..]);

        if (blobVendor != vendorId || blobDevice != deviceId) {
            reason = $"it was written on vendor {blobVendor:x4} device {blobDevice:x4}, and this is "
                + $"vendor {vendorId:x4} device {deviceId:x4}";

            return false;
        }

        if (uuid.Length < UuidSize || !blob.Slice(16, UuidSize).SequenceEqual(uuid[..UuidSize])) {
            reason = "it was written by a different driver build on this device";
            return false;
        }

        reason = "";
        return true;
    }
}
