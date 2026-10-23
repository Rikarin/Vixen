// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ <c>CheckAot</c> finds <c>vswhere.exe</c> where the Visual Studio installer puts it, and says
///     it is the machine — not the code — when it is nowhere (#1354).
/// </summary>
/// <remarks>
///     The file system is a set, so this runs on every platform; the gate only consults it on Windows.
///     The machine-side half — that the installer directory on <c>PATH</c> is what removes the
///     "'vswhere.exe' is not recognized" line ILC folds into the linker path — was reproduced by running
///     ILC's own <c>findvcvarsall.bat</c> both ways, and is recorded on <c>VswhereLocation</c>.
/// </remarks>
public sealed class VswhereLocationTests {
    const string ProgramFilesX86 = @"C:\Program Files (x86)";
    const string Installed = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";

    [Fact]
    public void The_installer_directory_is_prepended_when_vswhere_is_only_where_the_installer_put_it() {
        var path = VswhereLocation.PathFor(@"C:\Windows;C:\Tools", ProgramFilesX86, file => file == Installed);

        Assert.Equal(@"C:\Program Files (x86)\Microsoft Visual Studio\Installer;C:\Windows;C:\Tools", path);
    }

    [Fact]
    public void A_path_that_already_finds_vswhere_is_left_alone() {
        const string path = @"C:\Windows;C:\Tools\";

        Assert.Equal(path, VswhereLocation.PathFor(path, ProgramFilesX86, file => file == @"C:\Tools\vswhere.exe"));
    }

    /// <summary>The fail-early half: nowhere is null, which the gate turns into <see cref="VswhereLocation.Missing" />.</summary>
    [Theory]
    [InlineData(@"C:\Windows", ProgramFilesX86)]
    [InlineData(@"C:\Windows", null)]
    [InlineData(null, "")]
    public void Nowhere_is_null(string? path, string? programFilesX86) {
        Assert.Null(VswhereLocation.PathFor(path, programFilesX86, _ => false));
    }

    [Fact]
    public void An_empty_path_becomes_the_installer_directory_alone() {
        Assert.Equal(
            @"C:\Program Files (x86)\Microsoft Visual Studio\Installer",
            VswhereLocation.PathFor(null, ProgramFilesX86 + @"\", file => file == Installed)
        );
    }

    /// <summary>⚠ The message names the environment and says what a reader gets wrong.</summary>
    [Fact]
    public void The_message_says_it_is_not_an_ILC_finding() {
        Assert.Contains("not the code", VswhereLocation.Missing, StringComparison.Ordinal);
        Assert.Contains("MSB3073", VswhereLocation.Missing, StringComparison.Ordinal);
        Assert.Contains(@"Microsoft Visual Studio\Installer", VswhereLocation.Missing, StringComparison.Ordinal);
    }
}
