// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;

namespace Vixen.Platform;

/// <summary>Something the user must agree to before the application may do it.</summary>
/// <remarks>
///     Granted implicitly on the desktop and asked for on mobile and in a browser. Requesting one
///     that is already granted is cheap and correct, so callers do not branch on the platform.
/// </remarks>
public enum PermissionKind : byte {
    /// <summary>Read files the user did not explicitly hand over.</summary>
    ReadExternalStorage = 1,

    /// <summary>Write outside the application's own sandbox.</summary>
    WriteExternalStorage = 2,

    /// <summary>Record audio.</summary>
    Microphone = 3,

    /// <summary>Use the camera.</summary>
    Camera = 4,

    /// <summary>Post notifications.</summary>
    Notifications = 5
}

/// <summary>Where this platform keeps things, and what it will let us do there.</summary>
/// <remarks>
///     <para>
///         The paths here are native, and they are the only native paths engine code ever sees.
///         <see cref="MountStandardLocations" /> turns them into the mounts from
///         <see cref="MountPoints" />, and from that moment everything above speaks
///         <c>/app</c>, <c>/data</c>, <c>/cache</c> and <c>/temp</c> — which is what lets the same
///         code read from a directory on a desktop, an APK on Android, a signed bundle on iOS and an
///         HTTP fetch in a browser.
///     </para>
///     <para>
///         Getting these four locations right is more of the platform's work than it looks. Data on
///         Windows is <c>%APPDATA%</c> but on Linux is <c>$XDG_DATA_HOME</c> with a fallback, on
///         macOS is <c>~/Library/Application Support</c>, and on iOS must be the directory the OS
///         backs up — while cache must be one it is allowed to delete, or the app is rejected for
///         storing reconstructible data in the wrong place.
///     </para>
/// </remarks>
public interface IFileSystemHost {
    /// <summary>The read-only location the application's shipped content lives in.</summary>
    /// <remarks>Empty where content is not a directory at all — inside an APK, or behind HTTP.</remarks>
    string ApplicationDirectory { get; }

    /// <summary>The read-write location for data that must survive a restart.</summary>
    string DataDirectory { get; }

    /// <summary>The read-write location for data the platform may delete at any time.</summary>
    string CacheDirectory { get; }

    /// <summary>The read-write location for scratch data that need not survive the session.</summary>
    string TemporaryDirectory { get; }

    /// <summary>Whether the platform confines the process to those directories.</summary>
    /// <remarks>
    ///     True on iOS, Android, the browser, a Flatpak and a sandboxed macOS build. Where it is
    ///     true, a native path the user picked in a dialog is readable and one assembled by hand is
    ///     not, however correct it looks.
    /// </remarks>
    bool IsSandboxed { get; }

    /// <summary>Mounts <c>/app</c>, <c>/data</c>, <c>/cache</c> and <c>/temp</c> onto this
    /// platform's providers.</summary>
    /// <param name="fileSystem">The file system to mount into.</param>
    /// <remarks>
    ///     The platform mounts rather than the caller, because only the platform knows what kind of
    ///     provider each mount needs: a directory on the desktop, an asset-manager provider on
    ///     Android, a fetch provider in a browser. Mounts already present are replaced.
    /// </remarks>
    void MountStandardLocations(VirtualFileSystem fileSystem);

    /// <summary>Asks the user for a permission, if this platform has one to ask for.</summary>
    /// <param name="permission">What is wanted.</param>
    /// <param name="cancellationToken">Abandons the request where the platform allows it.</param>
    /// <returns>
    ///     Whether the permission is granted. Platforms that do not have the concept return
    ///     <see langword="true" /> without prompting.
    /// </returns>
    ValueTask<bool> RequestPermissionAsync(
        PermissionKind permission,
        CancellationToken cancellationToken = default
    );
}
