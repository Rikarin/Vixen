// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ A test that walks the repository from its own <c>[CallerFilePath]</c> reads nothing at all
///     under CI's default source-path mapping, and this holds the one property that stops it.
/// </summary>
/// <remarks>
///     <para>
///         <c>Directory.Build.props</c> sets <c>ContinuousIntegrationBuild</c> from the <c>CI</c>
///         environment variable, and the SDK defaults <c>DeterministicSourcePaths</c> to it. That
///         switches on a <c>PathMap</c> rewriting the repository root to <c>/_/</c> in everything the
///         compiler bakes into an assembly — <c>[CallerFilePath]</c> among it. So a roll call anchored
///         on the file it lives in looks for <c>/_/Editor/Vixen.Editor.TextureGraph</c>, which exists
///         on no machine.
///     </para>
///     <para>
///         ⚠ <b>It fails on the runner having passed on every developer box</b>, which is what made it
///         expensive: four such tests went red on ubuntu, macOS and Windows at once in run
///         <c>34003375702</c>, a shape that reads like a shared runtime defect and is really one
///         MSBuild default. Nothing on a developer machine can reproduce it, because nothing on a
///         developer machine sets <c>CI</c>.
///     </para>
///     <para>
///         ⚠ <b>So the two cases below are deliberately not the same check.</b>
///         <see cref="TheCompiledPathOfThisFileIsAPathThatExists" /> is the real property and is the
///         half that goes red on the runner; on a developer machine it is green whether or not the
///         property is set, so on its own it would be an instrument that only works where the defect
///         cannot occur. <see cref="TheTestProfileTurnsDeterministicSourcePathsOff" /> reads the
///         committed arrangement instead and therefore holds on both. Deleting either leaves one
///         machine unable to see the regression.
///     </para>
/// </remarks>
public sealed class SourceAnchoredTestTests {
    /// <summary>Where this file was compiled from — the thing CI's path mapping rewrites.</summary>
    /// <param name="path">Filled in by the compiler; never passed.</param>
    /// <returns>The compiled path of this file.</returns>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>
    ///     The property as the build actually resolved it: this file's compiled path names a file that
    ///     is on the machine. ⚠ This is the case that goes red on a CI runner and cannot go red
    ///     anywhere else.
    /// </summary>
    [Fact]
    public void TheCompiledPathOfThisFileIsAPathThatExists() {
        var here = Here();

        Assert.True(
            File.Exists(here),
            $"[CallerFilePath] in this assembly is '{here}', which is not a file on this machine. "
            + "Under a deterministic build that is the repository root rewritten to '/_/', and every "
            + "test in the tree that anchors a repository walk on its own compiled path reads an empty "
            + "directory. Directory.Build.props's TEST profile sets DeterministicSourcePaths false to "
            + "stop exactly this."
        );
    }

    /// <summary>
    ///     The committed arrangement that produces it, which is the half a developer machine can see:
    ///     the TEST profile turns <c>DeterministicSourcePaths</c> off, in the same file that turns
    ///     <c>ContinuousIntegrationBuild</c> on.
    /// </summary>
    [Fact]
    public void TheTestProfileTurnsDeterministicSourcePathsOff() {
        var properties = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));

        // The instrument first: if this stopped matching, the assertion below would be looking for a
        // property in a file whose CI switch had been moved or renamed, and would be reporting on an
        // arrangement that no longer exists.
        Assert.Matches(
            new Regex(@"<ContinuousIntegrationBuild\s+Condition=""'\$\(CI\)'\s*==\s*'true'"">true<"),
            properties
        );

        var profile = Regex.Match(
            properties,
            @"<PropertyGroup Condition=""\$\(MSBuildProjectName\.EndsWith\('\.Tests'\)\).*?</PropertyGroup>",
            RegexOptions.Singleline
        );

        Assert.True(profile.Success, "No *.Tests PropertyGroup in Directory.Build.props to read.");

        Assert.Contains(
            "<DeterministicSourcePaths>false</DeterministicSourcePaths>",
            profile.Value,
            StringComparison.Ordinal
        );
    }

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
