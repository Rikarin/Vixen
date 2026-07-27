// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Native.Tests;

/// <summary>
///     Where a native library is looked for, and in what order.
/// </summary>
/// <remarks>
///     Every rule here is a pure function from a name to a list of candidates, which is deliberate:
///     the ordering is the whole design, and a test that had to touch a filesystem could only ever
///     assert the ordering that this machine's installed libraries happen to produce. A rule about
///     Windows that can only be checked on Windows is a rule that is checked once a release.
/// </remarks>
public sealed class NativeResolutionTests {
    /// <summary>
    ///     The application's own copy wins. A machine with an older system-wide build of the same
    ///     library is the shape of every "works on my machine" report filed about native
    ///     dependencies, and the fix is to prefer what shipped.
    /// </summary>
    [Fact]
    public void TheApplicationsOwnNativesComeBeforeAnythingElse() {
        var directories = NativeSearch.Directories("/app", ["osx-arm64", "osx"], "/opt/homebrew/lib").ToList();

        Assert.Equal(
            [
                Path.Combine("/app", "runtimes", "osx-arm64", "native"),
                Path.Combine("/app", "runtimes", "osx", "native"),
                "/app",
                "/opt/homebrew/lib"
            ],
            directories
        );
    }

    /// <summary>
    ///     Architecture-specific before architecture-neutral: a binary built for this machine beats
    ///     one built for its operating system in general.
    /// </summary>
    [Fact]
    public void TheRidChainIsMostSpecificFirst() {
        Assert.Equal(["win-x64", "win"], NativeRid.ChainFor("win-x64"));
        Assert.Equal(["osx-arm64", "osx"], NativeRid.ChainFor("osx-arm64"));
        Assert.Equal(["browser-wasm", "browser"], NativeRid.ChainFor("browser-wasm"));
    }

    /// <summary>An identifier with no architecture half is its own chain, not a crash.</summary>
    [Fact]
    public void ARidWithNoArchitectureIsItsOwnChain() => Assert.Equal(["osx"], NativeRid.ChainFor("osx"));

    /// <summary>And this process knows what it is.</summary>
    [Fact]
    public void TheCurrentRidIsTheOperatingSystemAndTheArchitecture() {
        Assert.Equal([NativeRid.Current, NativeRid.CurrentOperatingSystem], NativeRid.Chain);
        Assert.StartsWith($"{NativeRid.CurrentOperatingSystem}-", NativeRid.Current, StringComparison.Ordinal);

        if (OperatingSystem.IsMacOS()) {
            Assert.Equal("osx", NativeRid.CurrentOperatingSystem);
        }
    }

    /// <summary>
    ///     <b>The versioned soname is the file that actually exists.</b> <c>libvulkan.so</c> and
    ///     <c>libvulkan.dylib</c> are development symlinks shipped by the <i>-dev</i> package; a
    ///     runtime-only install has only <c>libvulkan.so.1</c> and <c>libvulkan.1.dylib</c>. Missing
    ///     them means failing to load a library that is sitting right there.
    /// </summary>
    [Theory]
    [InlineData(true, false, new[] { "vulkan.dll", "vulkan-1.dll" })]
    [InlineData(false, true, new[] { "libvulkan.dylib", "libvulkan.1.dylib" })]
    [InlineData(false, false, new[] { "libvulkan.so", "libvulkan.so.1" })]
    public void EachPlatformSpellsALibraryItsOwnWay(bool windows, bool macOS, string[] expected) =>
        Assert.Equal(expected, NativeLibraryNames.ForPlatform("vulkan", windows, macOS, ["1"]));

    /// <summary>A library with no versioned soname asks for one name and stops.</summary>
    [Fact]
    public void ALibraryWithNoVersionsGetsOneName() =>
        Assert.Equal(["libSDL2.dylib"], NativeLibraryNames.ForPlatform("SDL2", false, true, []));

    /// <summary>
    ///     Directory-major, not name-major. Every name is tried in the most specific directory
    ///     before the next directory is considered — otherwise a system copy under the exact file
    ///     name would beat the application's own copy under a versioned one, which is the preference
    ///     this inverts.
    /// </summary>
    [Fact]
    public void EveryNameIsTriedInADirectoryBeforeTheNextDirectory() {
        var paths = NativeSearch.Paths(["/first", "/second"], ["a.dylib", "b.dylib"]).ToList();

        Assert.Equal(
            [
                Path.Combine("/first", "a.dylib"),
                Path.Combine("/first", "b.dylib"),
                Path.Combine("/second", "a.dylib"),
                Path.Combine("/second", "b.dylib")
            ],
            paths
        );
    }

    /// <summary>What a described library resolves through, end to end and without touching a disk.</summary>
    [Fact]
    public void ADescribedLibraryIsLookedForWhereItWasSaidToBe() {
        var was = NativeLibraries.BaseDirectory;

        try {
            NativeLibraries.BaseDirectory = "/app";
            NativeLibraries.Describe(new("testonly", ["1"], ["/somewhere/else"]));

            var candidates = NativeLibraries.Candidates("testonly").ToList();

            Assert.StartsWith(
                Path.Combine("/app", "runtimes", NativeRid.Current, "native"),
                candidates[0],
                StringComparison.Ordinal
            );

            Assert.Contains(candidates, path => path.StartsWith("/somewhere/else", StringComparison.Ordinal));
            Assert.Contains(candidates, path => path.EndsWith("1.dylib", StringComparison.Ordinal)
                || path.EndsWith(".so.1", StringComparison.Ordinal)
                || path.EndsWith("-1.dll", StringComparison.Ordinal));
        } finally {
            NativeLibraries.BaseDirectory = was;
        }
    }

    /// <summary>
    ///     A library nobody described is still looked for, under its ordinary names. The engine does
    ///     not have to enumerate every native dependency it will ever have in order to resolve any
    ///     of them.
    /// </summary>
    [Fact]
    public void AnUndescribedLibraryStillGetsTheOrdinaryNames() {
        var candidates = NativeLibraries.Candidates("neverdescribed").ToList();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, path => Assert.Contains("neverdescribed", path, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Registering twice is not an error. <see cref="System.Runtime.InteropServices.NativeLibrary" />
    ///     throws on a second resolver for one assembly, and "which of my subsystems registered
    ///     first" is not a question a caller should have to answer.
    /// </summary>
    [Fact]
    public void RegisteringAnAssemblyTwiceIsHarmless() {
        NativeLibraries.Register(typeof(NativeResolutionTests).Assembly);
        NativeLibraries.Register(typeof(NativeResolutionTests).Assembly);
    }
}
