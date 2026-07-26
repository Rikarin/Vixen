// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;

namespace Vixen.Platform;

/// <summary>
///     The desktop answer to "where does this platform keep things": the OS's own conventions,
///     resolved through <see cref="Environment.GetFolderPath(Environment.SpecialFolder)" />.
/// </summary>
/// <remarks>
///     <para>
///         Shared by every head that runs on a desktop operating system — the windowed one and the
///         headless one both — because a dedicated server writes its saves and caches to the same
///         places a game does, and having two implementations of that would mean two places for it
///         to be wrong.
///     </para>
///     <para>
///         The per-OS conventions are the BCL's rather than ours:
///         <see cref="Environment.SpecialFolder.ApplicationData" /> is <c>%APPDATA%</c> on Windows,
///         <c>$XDG_DATA_HOME</c> (falling back to <c>~/.local/share</c>) on Linux, and
///         <c>~/Library/Application Support</c> on macOS, and the same three-way split holds for the
///         local, roaming and cache distinctions. Reimplementing that would be reimplementing it
///         slightly differently.
///     </para>
///     <para>
///         Directories are created when this is constructed rather than on first write, so a
///         permissions problem surfaces at boot with a path in the message instead of halfway
///         through a save.
///     </para>
/// </remarks>
public sealed class StandardFileSystemHost : IFileSystemHost {
    /// <summary>Resolves the standard locations for one application.</summary>
    /// <param name="organisation">
    ///     The publisher's name, used as the outer directory. Conventional on Windows and macOS and
    ///     harmless on Linux.
    /// </param>
    /// <param name="application">The application's name, used as the inner directory.</param>
    /// <param name="applicationDirectory">
    ///     Where shipped content lives, or <see langword="null" /> for the directory the entry
    ///     assembly was loaded from — which is right for every desktop layout and for a published
    ///     single file.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="organisation" /> or
    /// <paramref name="application" /> is empty.</exception>
    public StandardFileSystemHost(string organisation, string application, string? applicationDirectory = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(organisation);
        ArgumentException.ThrowIfNullOrWhiteSpace(application);

        var qualifier = Path.Combine(organisation, application);

        ApplicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;

        DataDirectory = Path.Combine(Folder(Environment.SpecialFolder.ApplicationData, DataFallback), qualifier);

        // Cache is not data, and the OS has a separate place for it on all three desktops — one the
        // system may purge and none of which is backed up.
        //
        // Getting this wrong on macOS is not cosmetic: ~/Library/Application Support is backed up by
        // Time Machine and synced by iCloud, so a decoded-texture cache put there is copied off the
        // machine forever and never reclaimed under storage pressure.
        //
        // `InternetCache` maps to ~/Library/Caches on macOS, which is right; on Linux .NET does not
        // map it at all and returns empty, so the XDG location is spelled out. An earlier version of
        // this comment claimed .NET handled both, and the Linux CI leg's first run proved otherwise.
        CacheDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(
                Folder(Environment.SpecialFolder.LocalApplicationData, DataFallback),
                qualifier,
                "cache"
            )
            : Path.Combine(CacheRoot(), qualifier);

        TemporaryDirectory = Path.Combine(Path.GetTempPath(), qualifier);

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
    }

    /// <summary>Where a Unix home-relative directory goes when the OS will not say.</summary>
    static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.Create);

    static string DataFallback => Path.Combine(Home, ".local", "share");

    /// <summary>A standard folder, created if it is not there, and never an empty string.</summary>
    /// <param name="folder">Which one.</param>
    /// <param name="fallback">Where to put it when the OS does not name one.</param>
    /// <remarks>
    ///     Two failure modes, one of which cost a Linux CI leg its first green run.
    ///     <see cref="Environment.GetFolderPath(Environment.SpecialFolder)" /> returns
    ///     <em>the empty string</em> on Unix for a directory that does not exist yet — which is every
    ///     directory on a fresh user account, in a container, and on a CI runner. Combining that with
    ///     a relative qualifier yields a relative path, and the engine then writes its saves into
    ///     whatever the working directory happened to be. <c>SpecialFolderOption.Create</c> fixes the
    ///     common case; the fallback covers the folders .NET does not map on Unix at all.
    /// </remarks>
    static string Folder(Environment.SpecialFolder folder, string fallback) {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
        return string.IsNullOrEmpty(path) ? fallback : path;
    }

    /// <summary>Where caches go off Windows.</summary>
    static string CacheRoot() {
        if (OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg) {
            return xdg;
        }

        return OperatingSystem.IsLinux()
            ? Path.Combine(Home, ".cache")
            : Folder(Environment.SpecialFolder.InternetCache, Path.Combine(Home, ".cache"));
    }

    /// <inheritdoc />
    public string ApplicationDirectory { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string CacheDirectory { get; }

    /// <inheritdoc />
    public string TemporaryDirectory { get; }

    /// <summary>Always <see langword="false" />: a desktop process may read the whole disk.</summary>
    /// <remarks>
    ///     Still false inside a Flatpak, where the sandbox is real but the process cannot detect it
    ///     reliably — so the honest answer is the one that makes callers use the file dialog, which
    ///     is what grants access there. Revisit if a portal-aware Linux head lands.
    /// </remarks>
    public bool IsSandboxed => false;

    /// <inheritdoc />
    public void MountStandardLocations(VirtualFileSystem fileSystem) {
        ArgumentNullException.ThrowIfNull(fileSystem);

        fileSystem.Mount(MountPoints.App, new PhysicalFileProvider(ApplicationDirectory, isReadOnly: true));
        fileSystem.Mount(MountPoints.Data, new PhysicalFileProvider(DataDirectory));
        fileSystem.Mount(MountPoints.Cache, new PhysicalFileProvider(CacheDirectory));
        fileSystem.Mount(MountPoints.Temp, new PhysicalFileProvider(TemporaryDirectory));
    }

    /// <summary>Always granted: a desktop has no permission prompts to show.</summary>
    /// <param name="permission">Ignored.</param>
    /// <param name="cancellationToken">Ignored.</param>
    /// <returns><see langword="true" />.</returns>
    public ValueTask<bool> RequestPermissionAsync(
        PermissionKind permission,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(true);
}
