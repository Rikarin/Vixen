// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Xunit;

namespace Vixen.Platform.Tests;

public sealed class StandardFileSystemHostTests : IDisposable {
    const string Organisation = "Vixen.Tests";

    /// <summary>A different application name per instance, and it has to be.</summary>
    /// <remarks>
    ///     <para>
    ///         Three of this host's four locations are derived rather than passed: data, cache and
    ///         temporary come from the operating system's own folders, qualified by organisation and
    ///         application. Only <see cref="applicationDirectory" /> is handed in. So a constant name
    ///         here — which is what this was — put every process on the machine on the same three
    ///         directories, and <see cref="Dispose" /> deletes all three recursively.
    ///     </para>
    ///     <para>
    ///         That is not a statement about xunit's parallelism, which never runs one class's methods
    ///         at once. It is about two <i>processes</i>: a second checkout of the tree, an IDE running
    ///         the suite while a terminal does, or several agents each in their own worktree. Worktrees
    ///         isolate the source and nothing else, and none of these paths is under it. Measured on
    ///         macOS at 17 failures in 50 runs with two concurrent processes, in three different tests
    ///         — a write refused because the other process held the file, a read of a file the other
    ///         process had just deleted, and a directory asserted to exist that the other process
    ///         removed after this one's constructor made it — against none at all when either ran
    ///         alone.
    ///     </para>
    /// </remarks>
    readonly string application = $"StandardFileSystemHost-{Guid.NewGuid():N}";

    readonly string applicationDirectory =
        Path.Combine(Path.GetTempPath(), $"vixen-app-{Guid.NewGuid():N}");

    readonly StandardFileSystemHost host;

    public StandardFileSystemHostTests() {
        Directory.CreateDirectory(applicationDirectory);
        host = new(Organisation, application, applicationDirectory);
    }

    /// <summary>
    ///     Created up front rather than on first write, so a permissions problem shows up at boot
    ///     with a path in the message instead of halfway through a save.
    /// </summary>
    [Fact]
    public void EveryWritableLocationExistsBeforeAnybodyWritesToIt() {
        Assert.True(Directory.Exists(host.DataDirectory));
        Assert.True(Directory.Exists(host.CacheDirectory));
        Assert.True(Directory.Exists(host.TemporaryDirectory));
    }

    /// <summary>
    ///     A shader cache following a user onto another machine over a roaming profile is neither
    ///     wanted nor small, so cache must not live under the roaming data directory.
    /// </summary>
    [Fact]
    public void CacheIsNotUnderTheDirectoryThatRoams() {
        Assert.NotEqual(host.DataDirectory, host.CacheDirectory);
        Assert.False(host.CacheDirectory.StartsWith(host.DataDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public void TheFourLocationsAreDistinct() {
        var locations = new HashSet<string>(StringComparer.Ordinal) {
            host.ApplicationDirectory,
            host.DataDirectory,
            host.CacheDirectory,
            host.TemporaryDirectory
        };

        Assert.Equal(4, locations.Count);
    }

    [Fact]
    public void MountingGivesTheEngineTheFourNamesItSpeaks() {
        var fileSystem = new VirtualFileSystem();
        host.MountStandardLocations(fileSystem);

        Assert.True(fileSystem.TryResolve(MountPoints.App / "anything.txt", out _, out _));
        Assert.True(fileSystem.TryResolve(MountPoints.Data / "anything.txt", out _, out _));
        Assert.True(fileSystem.TryResolve(MountPoints.Cache / "anything.txt", out _, out _));
        Assert.True(fileSystem.TryResolve(MountPoints.Temp / "anything.txt", out _, out _));
    }

    /// <summary>
    ///     <c>/app</c> is shipped content. A build that writes there works on the developer's
    ///     desktop and fails on every device, so the mount has to refuse it here.
    /// </summary>
    [Fact]
    public async Task TheApplicationMountIsReadOnly() {
        var fileSystem = new VirtualFileSystem();
        host.MountStandardLocations(fileSystem);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await fileSystem.WriteAllTextAsync(MountPoints.App / "no.txt", "no", TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task DataRoundTripsThroughTheMount() {
        var fileSystem = new VirtualFileSystem();
        host.MountStandardLocations(fileSystem);

        var path = MountPoints.Data / "save.txt";
        await fileSystem.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);

        Assert.Equal("hello", await fileSystem.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ADesktopGrantsEveryPermissionWithoutAsking() {
        Assert.True(await host.RequestPermissionAsync(PermissionKind.Camera, TestContext.Current.CancellationToken));
        Assert.False(host.IsSandboxed);
    }

    [Fact]
    public void AnEmptyNameIsRejectedRatherThanProducingAStrangePath() {
        Assert.Throws<ArgumentException>(() => new StandardFileSystemHost(" ", application));
        Assert.Throws<ArgumentException>(() => new StandardFileSystemHost(Organisation, string.Empty));
    }

    public void Dispose() {
        Delete(applicationDirectory);
        Delete(host.DataDirectory);
        Delete(host.CacheDirectory);
        Delete(host.TemporaryDirectory);
    }

    static void Delete(string directory) {
        try {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        } catch (IOException) {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
    /// <summary>
    ///     Every location is absolute.
    /// </summary>
    /// <remarks>
    ///     The bug this pins is not hypothetical and is not visible from any of the assertions above.
    ///     <c>Environment.GetFolderPath</c> returns <em>the empty string</em> on Unix for a directory
    ///     that does not exist yet — which is every one of them on a fresh user account, in a
    ///     container, and on a CI runner. Combined with a relative qualifier that yields a relative
    ///     path, and the engine writes its saves into whatever the working directory happened to be:
    ///     the game's install directory if launched from a shortcut, `/` if launched by a service
    ///     manager. It survived every macOS and Windows run and was found the first time the suite ran
    ///     on Linux.
    /// </remarks>
    [Fact]
    public void EveryLocationIsAbsolute() {
        // The overload that derives the application directory too, so all four are the host's own
        // work. The same qualifier as the field, so the three directories it creates are the three
        // Dispose already removes rather than a fourth set nobody cleans up.
        var host = new StandardFileSystemHost(Organisation, application);

        Assert.True(Path.IsPathRooted(host.DataDirectory), $"Data is relative: {host.DataDirectory}");
        Assert.True(Path.IsPathRooted(host.CacheDirectory), $"Cache is relative: {host.CacheDirectory}");
        Assert.True(Path.IsPathRooted(host.TemporaryDirectory), $"Temp is relative: {host.TemporaryDirectory}");
        Assert.True(Path.IsPathRooted(host.ApplicationDirectory), "The application directory is relative.");
    }
}
