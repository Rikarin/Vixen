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

        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            qualifier
        );

        // Cache is not data, and the OS has a separate place for it on two of the three desktops:
        // ~/Library/Caches on macOS and $XDG_CACHE_HOME on Linux, both of which the system is
        // entitled to purge and neither of which is backed up. `InternetCache` is what .NET maps
        // both of those to — the name is a legacy of the Win32 constant, where it means the
        // browser's cache and is emphatically not somewhere we should write. So: that folder off
        // Windows, and the local (non-roaming) data folder on Windows, where a `cache` subdirectory
        // is the convention and a roaming shader cache following a user across a corporate network
        // is neither wanted nor small.
        //
        // Getting this wrong on macOS is not cosmetic: ~/Library/Application Support is backed up by
        // Time Machine and synced by iCloud, so a decoded-texture cache put there is copied off the
        // machine forever and never reclaimed under storage pressure.
        CacheDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                qualifier,
                "cache"
            )
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.InternetCache), qualifier);

        TemporaryDirectory = Path.Combine(Path.GetTempPath(), qualifier);

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
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
