// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The root <c>.runsettings</c>, held to the only thing that decides whether it is doing any
///     work: whether the variables it declares are in <em>this</em> process.
/// </summary>
/// <remarks>
///     <para>
///         Nothing discovers that file. <c>dotnet test</c> does not look for a <c>.runsettings</c>
///         beside the solution, and until <c>Directory.Build.props</c> gained a
///         <c>RunSettingsFilePath</c> it reached exactly four call sites, all inside <c>build/</c> —
///         so <c>dotnet test &lt;one project&gt;</c>, the command <c>CLAUDE.md</c> recommends and the
///         working agreement prefers, ran without it
///         (<a href="https://github.com/Rikarin/Vixen/issues/916">#916</a>). What that costs is not
///         cosmetic: the file sets <c>DYLD_LIBRARY_PATH</c> so the Homebrew Khronos validation layer
///         can load, and every Vulkan test in the tree otherwise runs unvalidated while
///         <c>VulkanInstanceTests.ValidationIsOnWhereTheLayerIsInstalled</c> goes red saying nothing
///         is wrong with the code.
///     </para>
///     <para>
///         ⚠ <b>And a developer's own shell hides it.</b> <c>DYLD_LIBRARY_PATH</c> exported from a
///         profile makes that suite pass whatever the settings say, so "are the validation layers
///         on?" answered yes in one terminal and no in an IDE, a <c>launchctl</c> session and a
///         runner — the exact drift <c>.runsettings</c>'s own comment says it exists to remove.
///         Which is why the assertion below is an <em>equality</em> against the file's declared value
///         rather than a "contains": an ambient export is a different string, and a check that
///         accepted it would be green in precisely the case it exists to catch.
///     </para>
///     <para>
///         Here for the reason <see cref="TestParallelismTests" /> is: this is the assembly that
///         already asks what the build actually reads, and this is the same failure one file over —
///         a committed settings file that changes nothing and reports success by looking present.
///     </para>
/// </remarks>
public sealed class RunSettingsTests {
    /// <summary>The environment variables the committed <c>.runsettings</c> declares.</summary>
    /// <remarks>
    ///     Read out of the file rather than written down twice, so editing the file moves the
    ///     assertion with it. VSTest applies <c>RunConfiguration/EnvironmentVariables</c> to the test
    ///     host on every platform, so this is not a macOS-only claim even though its subject is.
    /// </remarks>
    public static TheoryData<string, string> Declared {
        get {
            var path = Path.Combine(RepositoryRoot(), ".runsettings");
            var document = XDocument.Load(path);

            var variables = document
                .Descendants("EnvironmentVariables")
                .Elements()
                .Select(element => (Name: element.Name.LocalName, element.Value))
                .ToList();

            Assert.True(
                variables.Count > 0,
                $"{path} declares no environment variables under RunConfiguration, so passing it to a "
                + "run changes nothing and the theory below has no cases."
            );

            var data = new TheoryData<string, string>();

            foreach (var (name, value) in variables) {
                data.Add(name, value);
            }

            return data;
        }
    }

    /// <summary>
    ///     Every variable the file declares reached this test host, with the value the file declares.
    /// </summary>
    [Theory]
    [MemberData(nameof(Declared))]
    public void TheRunSettingsReachedThisProcess(string name, string value) {
        Assert.Equal(value, Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    ///     ⚠ And the property that makes that true is in the shared build file, not in one project.
    /// </summary>
    /// <remarks>
    ///     The theory above is the evidence and this is the diagnosis: without it, a run started some
    ///     other way — a <c>settings</c> switch on the command line, or an ambient export that
    ///     happens to match — would satisfy the theory and leave the tree's 178 test projects
    ///     unsettled. 178 projects cannot be relied on to remember, and the one that forgets is
    ///     invisible.
    /// </remarks>
    [Fact]
    public void DirectoryBuildPropsPointsEveryTestProjectAtIt() {
        var path = Path.Combine(RepositoryRoot(), "Directory.Build.props");

        var property = XDocument.Load(path)
            .Descendants("RunSettingsFilePath")
            .SingleOrDefault();

        Assert.True(
            property is not null,
            $"{path} declares no RunSettingsFilePath, so `dotnet test <one project>` runs with no "
            + "settings at all — .runsettings then reaches only the four call sites in build/ that "
            + "name it explicitly. That is #916."
        );

        Assert.Equal("$(MSBuildThisFileDirectory).runsettings", property!.Value.Trim());

        // The test profile and not the whole tree: the property is meaningless on a library, and a
        // sibling that also carries VSTestLogger is what identifies the group without re-parsing the
        // condition MSBuild spells it with.
        Assert.True(
            property.Parent?.Elements("VSTestLogger").Any() == true,
            "RunSettingsFilePath is not in the PropertyGroup that carries VSTestLogger, so it is no "
            + "longer scoped to test projects — check the condition it ended up under."
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
