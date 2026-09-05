// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ Verifying an instrument that had none: the reader behind <c>CheckAot</c>'s and
///     <c>CheckAotIos</c>'s two pre-publish assertions.
/// </summary>
/// <remarks>
///     <para>
///         Those assertions used to read <c>Vixen.AotProbe.csproj</c> as a string, so
///         <c>&lt;!-- &lt;PublishAot&gt;true&lt;/PublishAot&gt; --&gt;</c> satisfied them exactly as
///         the live declaration did. The fixtures below are the three edits that were invisible: a
///         commented-out property, a commented-out root, and a group given a <c>Condition</c> that
///         never evaluates. Each one is asserted against the real probe rather than a toy, so the
///         subject is the file the gate actually reads.
///     </para>
///     <para>
///         Here rather than beside <c>build/_build.csproj</c> because there is nowhere beside it:
///         the build project is outside <c>Vixen.slnx</c> and no suite in the tree tests it. This
///         assembly already walks the repository to ask what the build reads, and the reader is
///         linked into it as source, so there is no second copy to drift.
///     </para>
/// </remarks>
public sealed class AotProbeProjectFileTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-aot-probe-tests", Guid.NewGuid().ToString("N"));

    public AotProbeProjectFileTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            if (Directory.Exists(directory)) {
                Directory.Delete(directory, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    /// <summary>
    ///     The floor, and the reason the other three tests mean anything: a reader that had stopped
    ///     recognising how the probe is written would answer "nothing declared" to every question
    ///     below and every one of them would still pass.
    /// </summary>
    [Fact]
    public void TheRealProbeReadsAsFullyDeclaredAndFullyRooted() {
        var probe = ProbeProject();

        Assert.True(AotProbeProjectFile.DeclaresProperty(probe, "PublishAot", "true"));
        Assert.True(AotProbeProjectFile.DeclaresProperty(probe, "TrimmerSingleWarn", "false"));

        var referenced = AotProbeProjectFile.ReferencedAssemblies(probe);

        Assert.True(referenced.Count >= 25, $"only {referenced.Count} references read out of the probe.");
        Assert.Equal(
            referenced.Order(StringComparer.Ordinal),
            AotProbeProjectFile.RootedAssemblies(probe).Order(StringComparer.Ordinal)
        );
    }

    /// <summary>
    ///     ⚠ The edit somebody actually makes while debugging a probe, and the one the substring
    ///     test could not see. On iOS these four properties are the only enforcement there is.
    /// </summary>
    [Fact]
    public void ACommentedOutPropertyIsNotDeclared() {
        var sabotaged = Fixture(
            "commented-property.csproj",
            "<PublishAot>true</PublishAot>",
            "<!-- <PublishAot>true</PublishAot> -->"
        );

        Assert.Contains("<!-- <PublishAot>true</PublishAot> -->", File.ReadAllText(sabotaged), StringComparison.Ordinal);
        Assert.False(AotProbeProjectFile.DeclaresProperty(sabotaged, "PublishAot", "true"));
        Assert.True(AotProbeProjectFile.DeclaresProperty(sabotaged, "TreatWarningsAsErrors", "true"));
    }

    /// <summary>
    ///     ⚠ Commenting out a reference and its root together stays symmetric and is harmless.
    ///     Commenting out only the root leaves that assembly covered by nothing but what
    ///     <c>Main</c> reaches, which is the case the rooting comparison exists for.
    /// </summary>
    [Fact]
    public void ACommentedOutRootIsNotARoot() {
        var sabotaged = Fixture(
            "commented-root.csproj",
            """<TrimmerRootAssembly Include="Vixen.Ecs" />""",
            """<!-- <TrimmerRootAssembly Include="Vixen.Ecs" /> -->"""
        );

        Assert.Contains("Vixen.Ecs", AotProbeProjectFile.ReferencedAssemblies(sabotaged));
        Assert.DoesNotContain("Vixen.Ecs", AotProbeProjectFile.RootedAssemblies(sabotaged));
    }

    /// <summary>
    ///     A property inside a group whose condition never evaluates reads identically to one in the
    ///     unconditional group — the same blindness one level out.
    /// </summary>
    [Fact]
    public void APropertyUnderAConditionedGroupIsNotDeclared() {
        var sabotaged = Fixture(
            "conditioned-group.csproj",
            "<PropertyGroup>",
            """<PropertyGroup Condition="'$(NeverTrue)' == 'yes'">""",
            once: true
        );

        Assert.False(AotProbeProjectFile.DeclaresProperty(sabotaged, "PublishAot", "true"));
    }

    static string ProbeProject() =>
        Path.Combine(RepositoryRoot(), "Tools", "Vixen.AotProbe", "Vixen.AotProbe.csproj");

    string Fixture(string name, string from, string to, bool once = false) {
        var text = File.ReadAllText(ProbeProject());

        Assert.Contains(from, text, StringComparison.Ordinal);

        var path = Path.Combine(directory, name);

        File.WriteAllText(path, once ? ReplaceFirst(text, from, to) : text.Replace(from, to, StringComparison.Ordinal));

        return path;
    }

    static string ReplaceFirst(string text, string from, string to) {
        var index = text.IndexOf(from, StringComparison.Ordinal);

        return text[..index] + to + text[(index + from.Length)..];
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
