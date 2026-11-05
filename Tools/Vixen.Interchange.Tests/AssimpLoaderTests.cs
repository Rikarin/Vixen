// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Xunit;
using AssimpApi = Silk.NET.Assimp.Assimp;

namespace Vixen.Interchange.Tests;

/// <summary>Which Assimp a read opens: the application's, and the major the binding was generated from.</summary>
public sealed class AssimpLoaderTests {
    /// <summary>The binding's own name for a platform, read off the internal container that holds them.</summary>
    /// <param name="property">The platform's property.</param>
    /// <returns>Its names.</returns>
    static string[] BindingNames(string property) {
        var container = typeof(AssimpApi).Assembly.GetTypes().Single(type => type.Name == "AssimpLibraryNameContainer");
        var names = container.GetProperty(property)?.GetValue(Activator.CreateInstance(container, nonPublic: true));

        return Assert.IsType<string[]>(names);
    }

    [Theory]
    [InlineData("Linux", false, false, true)]
    [InlineData("MacOS", false, true, true)]
    [InlineData("Windows64", true, false, true)]
    [InlineData("Windows86", true, false, false)]
    public void The_file_name_is_the_one_the_binding_was_generated_for(
        string platform,
        bool windows,
        bool macOS,
        bool is64Bit
    ) =>
        Assert.Equal(Assert.Single(BindingNames(platform)), AssimpLoader.FileName(windows, macOS, is64Bit));

    /// <summary>
    ///     ⚠ <b>Both layouts, in order, on a machine that need not be Linux.</b> The build's folder, the
    ///     publish's folder, and the bare soname after both, so a system library cannot beat the file
    ///     the application was tested with.
    /// </summary>
    [Fact]
    public void A_linux_application_answers_before_the_machine_does() {
        var candidates = AssimpLoader.Candidates("app", ["linux-x64", "linux"], "libassimp.so.5").ToArray();

        string[] expected = [
            Path.Combine("app", "runtimes", "linux-x64", "native", "libassimp.so.5"),
            Path.Combine("app", "runtimes", "linux", "native", "libassimp.so.5"),
            Path.Combine("app", "libassimp.so.5"),
            "libassimp.so.5"
        ];

        Assert.Equal(expected, candidates);
    }

    /// <summary>
    ///     ⚠ <b>Held on the operating system this runs on, which is what Linux CI is for.</b> With no
    ///     system Assimp the read would fail outright, and with one the path would be the bare name, so
    ///     this is red both ways if the application's own copy stops answering.
    /// </summary>
    [Fact]
    public void A_read_opens_the_copy_the_application_carries() {
        var read = ModelReader.Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"u8, ".obj", "Shape", new ModelReadOptions());

        Assert.Single(read.Meshes);
        Assert.StartsWith(AppContext.BaseDirectory, ModelReader.NativeLibraryPath, StringComparison.Ordinal);
        Assert.Equal(
            AssimpLoader.FileName(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), Environment.Is64BitProcess),
            Path.GetFileName(ModelReader.NativeLibraryPath)
        );
    }
}
