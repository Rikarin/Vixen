// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Platform;

namespace Vixen.App.Tests;

/// <summary>
///     Every standard location under one throwaway directory, so a test run does not write into the
///     developer's home directory and two runs cannot see each other's files.
/// </summary>
sealed class TemporaryFileSystemHost : IFileSystemHost, IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), $"vixen-headless-{Guid.NewGuid():N}");

    public TemporaryFileSystemHost() {
        ApplicationDirectory = Path.Combine(root, "app");
        DataDirectory = Path.Combine(root, "data");
        CacheDirectory = Path.Combine(root, "cache");
        TemporaryDirectory = Path.Combine(root, "temp");

        Directory.CreateDirectory(ApplicationDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
    }

    public string ApplicationDirectory { get; }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public string TemporaryDirectory { get; }

    public bool IsSandboxed => false;

    public void MountStandardLocations(VirtualFileSystem fileSystem) {
        fileSystem.Mount(MountPoints.App, new PhysicalFileProvider(ApplicationDirectory, isReadOnly: true));
        fileSystem.Mount(MountPoints.Data, new PhysicalFileProvider(DataDirectory));
        fileSystem.Mount(MountPoints.Cache, new PhysicalFileProvider(CacheDirectory));
        fileSystem.Mount(MountPoints.Temp, new PhysicalFileProvider(TemporaryDirectory));
    }

    public ValueTask<bool> RequestPermissionAsync(
        PermissionKind permission,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(true);

    public void Dispose() {
        try {
            if (Directory.Exists(root)) {
                Directory.Delete(root, recursive: true);
            }
        } catch (IOException) {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
