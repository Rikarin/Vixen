// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Foundation;
using Vixen.Core.IO;

namespace Vixen.Platform.Ios;

/// <summary>The four directories an iOS application is allowed to use.</summary>
/// <remarks>
///     <para>
///         <b>The sandbox is not a detail to be abstracted over — it is the whole story.</b> There
///         is nowhere to write outside the container, the container's absolute path changes on every
///         install and on some updates, and the distinction between the three writable directories
///         is what decides whether the operating system deletes a file behind the application's back
///         or iCloud uploads it to the user's account.
///     </para>
///     <para>
///         <b>Documents is deliberately not <see cref="DataDirectory" />.</b> Documents is backed up
///         and, in an application that declares file sharing, visible to the user in the Files app —
///         which is right for a saved game and wrong for a shader cache. <c>Library/Application
///         Support</c> is backed up and invisible, which is what engine data wants; <c>Library/Caches</c>
///         is neither, and the system deletes it under storage pressure, which is exactly the
///         contract a downloaded-bundle cache should be written against.
///     </para>
/// </remarks>
internal sealed class IosFileSystemHost : IFileSystemHost {
    /// <inheritdoc />
    /// <remarks>The bundle: read-only, and where content shipped in the <c>.ipa</c> lives.</remarks>
    public string ApplicationDirectory => NSBundle.MainBundle.BundlePath;

    /// <inheritdoc />
    public string DataDirectory { get; } = Directory(NSSearchPathDirectory.ApplicationSupportDirectory);

    /// <inheritdoc />
    public string CacheDirectory { get; } = Directory(NSSearchPathDirectory.CachesDirectory);

    /// <inheritdoc />
    public string TemporaryDirectory => Path.GetTempPath();

    /// <inheritdoc />
    public bool IsSandboxed => true;

    /// <inheritdoc />
    public void MountStandardLocations(VirtualFileSystem fileSystem) {
        ArgumentNullException.ThrowIfNull(fileSystem);

        fileSystem.Mount(MountPoints.App, new PhysicalFileProvider(ApplicationDirectory, isReadOnly: true));
        fileSystem.Mount(MountPoints.Data, new PhysicalFileProvider(Ensure(DataDirectory)));
        fileSystem.Mount(MountPoints.Cache, new PhysicalFileProvider(Ensure(CacheDirectory)));
        fileSystem.Mount(MountPoints.Temp, new PhysicalFileProvider(TemporaryDirectory));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <see langword="true" /> for storage, which needs no permission inside the sandbox, and
    ///     <see langword="false" /> for everything else. The camera, microphone and notification
    ///     prompts are real and each needs its own <c>Info.plist</c> usage string, without which iOS
    ///     terminates the application rather than declining — so they are refused here rather than
    ///     asked for on behalf of a bundle that may not have declared them.
    /// </remarks>
    public ValueTask<bool> RequestPermissionAsync(
        PermissionKind permission,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(
            permission is PermissionKind.ReadExternalStorage or PermissionKind.WriteExternalStorage
        );

    static string Directory(NSSearchPathDirectory kind) =>
        NSSearchPath.GetDirectories(kind, NSSearchPathDomain.User).FirstOrDefault()
        ?? Path.GetTempPath();

    static string Ensure(string path) {
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
