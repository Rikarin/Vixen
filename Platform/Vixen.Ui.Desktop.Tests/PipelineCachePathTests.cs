// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>Where this head asks the driver to keep its pipeline cache.</summary>
/// <remarks>
///     <para>
///         <b>The across-run half of the warm start</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1228">#1228</a>). Every head that boots
///         through <c>Tools/Vixen.App</c>'s <c>GraphicsHost</c> is handed a path by
///         <c>AppBuilder</c>; this one opened its device with nothing but the surface and so
///         recompiled every pipeline it touched on every launch.
///     </para>
///     <para>
///         ⚠ <b>What this asserts is the derivation and not the call site, and that gap is real.</b>
///         A headless run never reaches <c>EnsureDevice</c> — there is no presentable surface and no
///         driver — so no test in this repository can watch the path being handed to
///         <c>VulkanDevice.Create</c>. The evidence that it is comes from running a head with a real
///         device and looking for the blob; the differential form is
///         <c>VulkanPipelineCacheTests</c>, and it is measured as
///         <c>VulkanDevice.PipelineCacheSeedBytes</c> rather than as a clock.
///     </para>
/// </remarks>
public class PipelineCachePathTests {
    /// <summary>It is a file under the platform's own cache directory.</summary>
    /// <remarks>
    ///     ⚠ <b>The cache directory and not the data one, and the difference is not tidiness.</b>
    ///     On macOS the data directory is <c>~/Library/Application Support</c>, which Time Machine
    ///     backs up and iCloud syncs — so a driver blob that is derivable, device-specific and thrown
    ///     away on any driver update would be copied off the machine for ever. See
    ///     <c>StandardFileSystemHost</c>, whose own comment records that this was got wrong once.
    /// </remarks>
    [Fact]
    public void The_pipeline_cache_goes_under_the_platforms_cache_directory() {
        using var platform = new HeadlessPlatform();

        var path = UiApplication.PipelineCacheFile(platform.FileSystem);

        Assert.NotNull(path);
        Assert.StartsWith(platform.FileSystem.CacheDirectory, path, StringComparison.Ordinal);
        Assert.NotEqual(platform.FileSystem.DataDirectory, Path.GetDirectoryName(path));

        // ⚠ The same file name `AppBuilder` writes, so that "delete your pipeline cache" is one
        // sentence for every head rather than one per head.
        Assert.Equal("pipelines.vkcache", Path.GetFileName(path));
    }

    /// <summary>A platform that names no cache directory gets no file, rather than a bare name.</summary>
    /// <remarks>
    ///     ⚠ <b>Null and not <see cref="string.Empty" />, and not a relative path.</b>
    ///     <c>VulkanDeviceOptions.PipelineCachePath</c> reads null as "keep the cache in memory for
    ///     the life of the device", which is the honest answer for a platform with nowhere to put it;
    ///     a bare <c>pipelines.vkcache</c> would land in whatever the process's working directory
    ///     happened to be, which for an application launched from a bundle is not a place it may
    ///     write.
    /// </remarks>
    [Fact]
    public void A_platform_with_no_cache_directory_asks_for_no_file() {
        Assert.Null(UiApplication.PipelineCacheFile(new Nowhere()));
    }

    /// <summary>A file-system host that names no directories at all.</summary>
    sealed class Nowhere : IFileSystemHost {
        public string ApplicationDirectory => string.Empty;

        public string DataDirectory => string.Empty;

        public string CacheDirectory => string.Empty;

        public string TemporaryDirectory => string.Empty;

        public bool IsSandboxed => false;

        public void MountStandardLocations(VirtualFileSystem fileSystem) { }

        public ValueTask<bool> RequestPermissionAsync(
            PermissionKind permission,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(true);
    }
}
