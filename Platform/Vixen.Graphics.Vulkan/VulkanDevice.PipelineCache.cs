// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>The driver's own pipeline cache: created with the device, kept beside the user's data.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every <c>vkCreate*Pipelines</c> call used to pass <c>VK_NULL_HANDLE</c> here, which
///         turns the driver's cache off rather than defaulting it on.</b> There is no implicit
///         cache: two pipelines that differ only in blend state hand the driver the same shader
///         twice and it compiles it twice, and nothing at all survives the process. That is one
///         level below the pre-warm doc 05 asks for (#301) and independent of it — it decides how
///         much work <em>the driver</em> has to redo, and it is what makes a pre-warm affordable
///         rather than a hitch moved to the loading screen.
///     </para>
///     <para>
///         The in-memory half needs no configuration and is always on. The on-disk half is
///         <see cref="VulkanDeviceOptions.PipelineCachePath" />: unset, the cache lives and dies
///         with the device, which is what a test and a one-shot tool want.
///     </para>
/// </remarks>
public sealed unsafe partial class VulkanDevice {
    PipelineCache pipelineCache;
    string? pipelineCachePath;

    /// <summary>How many bytes of a previous run's cache this device started from.</summary>
    /// <remarks>
    ///     The honest measurement of whether the feature works, and the reason it is a count rather
    ///     than a frame time: a warm cache is a blob the driver accepted, and a wall-clock budget
    ///     calibrated on an idle machine is this repository's largest flake source. Zero on the
    ///     first run of a machine, on a blob a driver update invalidated, and whenever no path was
    ///     given.
    /// </remarks>
    internal int PipelineCacheSeedBytes { get; private set; }

    /// <summary>Creates the cache, seeded from disk when a previous run left something usable.</summary>
    /// <param name="path">Where the blob lives, or null to keep the cache in memory only.</param>
    void CreatePipelineCache(string? path) {
        pipelineCachePath = path;

        var seed = ReadSeed(path);
        var create = new PipelineCacheCreateInfo {
            SType = StructureType.PipelineCacheCreateInfo,
            InitialDataSize = (nuint)seed.Length
        };

        fixed (byte* data = seed) {
            create.PInitialData = data;

            PipelineCache handle;
            Check(Api.CreatePipelineCache(device, &create, null, &handle), "vkCreatePipelineCache");
            pipelineCache = handle;
        }

        PipelineCacheSeedBytes = seed.Length;

        if (seed.Length > 0 && logger is { } log) {
            VulkanLog.PipelineCacheSeeded(log, seed.Length, path!);
        }
    }

    /// <summary>What a previous run left, or an empty span when there is nothing to trust.</summary>
    byte[] ReadSeed(string? path) {
        if (path is not { Length: > 0 } || !File.Exists(path)) {
            return [];
        }

        byte[] blob;

        try {
            blob = File.ReadAllBytes(path);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            if (logger is { } log) {
                VulkanLog.PipelineCacheUnreadable(log, path, error.Message);
            }

            return [];
        }

        var properties = adapter.Properties;
        var uuid = new ReadOnlySpan<byte>(properties.PipelineCacheUuid, VulkanPipelineCacheBlob.UuidSize);

        if (VulkanPipelineCacheBlob.Matches(blob, properties.VendorID, properties.DeviceID, uuid, out var reason)) {
            return blob;
        }

        // ⚠ Discarded rather than repaired, and this is the ordinary case rather than an error: a
        // driver update changes the UUID, and every blob written before it is then a file that
        // would be undefined behaviour to hand back. The next Dispose overwrites it.
        if (logger is { } discarded) {
            VulkanLog.PipelineCacheDiscarded(discarded, path, reason);
        }

        return [];
    }

    /// <summary>Writes what the driver learned this run, and destroys the cache.</summary>
    /// <remarks>
    ///     ⚠ <b>Written through a temporary and moved into place.</b> Two processes of the same game
    ///     may exit at once, and a half-written blob is not a corrupt cache that gets rebuilt — it is
    ///     a file whose header still matches, handed to the driver on the next boot.
    /// </remarks>
    void SavePipelineCache() {
        if (pipelineCache.Handle == 0) {
            return;
        }

        if (pipelineCachePath is { Length: > 0 } path) {
            try {
                nuint size = 0;
                Check(Api.GetPipelineCacheData(device, pipelineCache, &size, null), "vkGetPipelineCacheData");

                if (size >= VulkanPipelineCacheBlob.HeaderSize) {
                    var blob = new byte[(int)size];

                    fixed (byte* data = blob) {
                        Check(
                            Api.GetPipelineCacheData(device, pipelineCache, &size, data),
                            "vkGetPipelineCacheData"
                        );
                    }

                    Write(path, blob);
                }
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                or VulkanException) {
                if (logger is { } log) {
                    VulkanLog.PipelineCacheUnwritable(log, path, error.Message);
                }
            }
        }

        Api.DestroyPipelineCache(device, pipelineCache, null);
        pipelineCache = default;
    }

    static void Write(string path, byte[] blob) {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, blob);
        File.Move(temporary, path, true);
    }
}
