// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ That <c>--since</c> maps a changed shader to the projects that read it, rather than
///     refusing or narrowing it to nothing.
/// </summary>
/// <remarks>
///     <para>
///         <c>AffectedOwnership</c> is the rule <c>AffectedProjects</c>, <c>AffectedTests</c> and
///         <c>CheckFormat --since</c> share, linked here because <c>build/_build.csproj</c> is outside
///         the solution. Before
///         <a href="https://github.com/Rikarin/Vixen/issues/1420">#1420</a> a comment-only edit to
///         <c>Raven/Library/Pipeline/ForwardPlus.rvn</c> made all three refuse outright: ownership was
///         the nearest <c>.csproj</c> up the tree, and there is none above the shader library.
///     </para>
///     <para>
///         ⚠ Both wrong answers are asserted against, because the tempting fix is the second one:
///         refusing is loud and costs a developer the inner loop, and exempting the library as
///         "projectless" would narrow a shader edit to no tests at all, silently.
///     </para>
/// </remarks>
public sealed class AffectedOwnershipTests {
    const string ForwardPlus = "Raven/Library/Pipeline/ForwardPlus.rvn";
    const string RavenTests = "Raven/Vixen.Raven.Tests/Vixen.Raven.Tests.csproj";

    /// <summary>No project contains any file, which is the shader library's real position.</summary>
    static string? NoContainer(string _) => null;

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoPatterns =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    ///     ⚠ The issue's exact case: a library shader, contained by nothing, is owned by the suite
    ///     that binds the whole library off disk.
    /// </summary>
    /// <remarks>
    ///     Red before the fix in the only shape it could be: the file comes back as an orphan, which
    ///     is the assertion <c>ProjectsOwning</c> fails the build on.
    /// </remarks>
    [Fact]
    public void ALibraryShaderIsOwnedByTheSuiteThatReadsTheLibrary() {
        var ownership = AffectedOwnership.Classify([ForwardPlus], NoContainer, NoPatterns);

        Assert.Empty(ownership.Orphans);
        Assert.Equal([RavenTests], ownership.Projects);
    }

    /// <summary>
    ///     ⚠ And the fix the issue warns against is not the one taken: the library is not
    ///     projectless.
    /// </summary>
    [Fact]
    public void TheLibraryIsNotExemptedAsProjectless() {
        Assert.False(AffectedOwnership.OwnedByNoProject(ForwardPlus));
        Assert.False(AffectedOwnership.OwnedByNoProject("Raven/Library/Pipeline/ForwardPlus.reflect.json"));

        // The control, so that the predicate is not simply false for everything.
        Assert.True(AffectedOwnership.OwnedByNoProject("docs/overview.md"));
        Assert.True(AffectedOwnership.OwnedByNoProject("Raven/README.md"));
    }

    /// <summary>A file nothing contains, reads or exempts is still an orphan.</summary>
    [Fact]
    public void AFileNothingReadsIsStillAnOrphan() {
        var ownership = AffectedOwnership.Classify(["Raven/Unknown/Stray.rvn"], NoContainer, NoPatterns);

        Assert.Equal(["Raven/Unknown/Stray.rvn"], ownership.Orphans);
        Assert.Empty(ownership.Projects);
    }

    /// <summary>
    ///     An item that names files outside its project's directory makes the project an owner of
    ///     them, with <c>**</c> spanning directories and <c>*</c> not.
    /// </summary>
    [Fact]
    public void AnItemIncludeOutsideTheProjectIsAPattern() {
        const string project = "Core/Vixen.Rendering.Tests/Vixen.Rendering.Tests.csproj";
        const string text = """
            <Project Sdk="Microsoft.NET.Sdk">
                <ItemGroup>
                    <ProjectReference Include="..\Vixen.Rendering\Vixen.Rendering.csproj" />
                    <Compile Include="Local\Inside.cs" />
                    <None Include="..\..\Raven\Library\**\*.rvn" LinkBase="Shaders" />
                    <AdditionalFiles Include="..\..\Raven\Library\Pipeline\*.reflect.json;..\..\Raven\Library\Vfx\One.reflect.json" />
                    <None Include="$(SomeProperty)\x.rvn" />
                </ItemGroup>
                <Import Project="..\..\Testing\Vixen.Testing.GoldenFile.props" />
            </Project>
            """;

        var patterns = AffectedOwnership.ItemPatterns(project, text);

        Assert.Equal(
            [
                "Raven/Library/**/*.rvn",
                "Raven/Library/Pipeline/*.reflect.json",
                "Raven/Library/Vfx/One.reflect.json",
                "Testing/Vixen.Testing.GoldenFile.props"
            ],
            patterns
        );

        Assert.True(AffectedOwnership.Matches("Raven/Library/**/*.rvn", ForwardPlus));
        Assert.True(AffectedOwnership.Matches("Raven/Library/**/*.rvn", "Raven/Library/Example1.rvn"));
        Assert.False(AffectedOwnership.Matches("Raven/Library/**/*.rvn", "Raven/Library/Pipeline/ForwardPlus.reflect.json"));
        Assert.True(AffectedOwnership.Matches("Raven/Library/Pipeline/*.reflect.json", "Raven/Library/Pipeline/ForwardPlus.reflect.json"));
        Assert.False(AffectedOwnership.Matches("Raven/Library/Pipeline/*.reflect.json", "Raven/Library/Pipeline/Deeper/X.reflect.json"));
    }

    /// <summary>
    ///     ⚠ A file that <em>has</em> a containing project is owned by its readers as well.
    /// </summary>
    /// <remarks>
    ///     <c>build/PathCaseRule.cs</c> is contained by <c>build/_build.csproj</c>, which is not in the
    ///     solution, and compiled into this very suite by a linked <c>Compile</c>. Owned by its
    ///     container alone, an edit to any rule this suite exists to fixture narrowed to no test.
    /// </remarks>
    [Fact]
    public void ALinkedBuildRuleIsOwnedByTheSuiteThatCompilesIt() {
        var root = RepositoryRoot();
        var patterns = RepositoryPatterns(root);

        var ownership = AffectedOwnership.Classify(
            ["build/PathCaseRule.cs"],
            _ => "build/_build.csproj",
            patterns
        );

        Assert.Contains("Tools/Vixen.ApiCheck.Tests/Vixen.ApiCheck.Tests.csproj", ownership.Projects);
        Assert.Contains("build/_build.csproj", ownership.Projects);
    }

    /// <summary>
    ///     ⚠ The real tree: every committed file under <c>Raven/Library/</c> has an owner, and a
    ///     library shader reaches the suites whose project files name the library.
    /// </summary>
    /// <remarks>
    ///     The derived half is what makes the declared list short. <c>Vixen.Rendering.Tests</c> and
    ///     <c>Vixen.Engine.Renderer.Tests</c> copy every library <c>.rvn</c> beside their binaries
    ///     through a <c>**</c> include, and nobody had to write that down for them to be reached.
    /// </remarks>
    [Fact]
    public void EveryCommittedLibraryFileHasAnOwnerOnThisTree() {
        var root = RepositoryRoot();
        var patterns = RepositoryPatterns(root);
        var library = CommittedPaths(root).Where(path => path.StartsWith("Raven/Library/", StringComparison.Ordinal)).ToList();

        Assert.True(library.Count > 100, $"`git ls-files` found {library.Count} library files, which is not this tree.");

        var ownership = AffectedOwnership.Classify(library, NoContainer, patterns);

        Assert.Empty(ownership.Orphans);

        var forwardPlus = AffectedOwnership.ReadersOf(ForwardPlus, patterns);

        Assert.Contains(RavenTests, forwardPlus);
        Assert.Contains("Core/Vixen.Rendering.Tests/Vixen.Rendering.Tests.csproj", forwardPlus);
        Assert.Contains("Core/Vixen.Engine.Renderer.Tests/Vixen.Engine.Renderer.Tests.csproj", forwardPlus);

        // The reflection a binding generator compiles is read by the project that generates from it.
        Assert.Contains(
            "Core/Vixen.Rendering/Vixen.Rendering.csproj",
            AffectedOwnership.ReadersOf("Raven/Library/Pipeline/ForwardPlus.reflect.json", patterns)
        );
    }

    /// <summary>
    ///     A declared reader still exists and still spells the directory it is declared to read.
    /// </summary>
    /// <remarks>
    ///     The only hand-kept part of the rule, so the part that can go stale: a reader that stopped
    ///     reading would keep owning the library, and a narrowed run would test the wrong thing.
    /// </remarks>
    [Fact]
    public void EveryDeclaredReaderStillReadsWhatItIsDeclaredToRead() {
        var root = RepositoryRoot();

        Assert.NotEmpty(AffectedOwnership.DeclaredReaders);

        foreach (var reader in AffectedOwnership.DeclaredReaders) {
            Assert.True(File.Exists(Path.Combine(root, reader.Project)), $"{reader.Project} does not exist.");
            Assert.StartsWith(Path.GetDirectoryName(reader.Project)!.Replace('\\', '/') + "/", reader.Evidence, StringComparison.Ordinal);

            var directory = reader.Prefix.TrimEnd('/').Split('/')[^1];
            var evidence = File.ReadAllText(Path.Combine(root, reader.Evidence));

            Assert.Contains($"\"{directory}\"", evidence, StringComparison.Ordinal);
        }
    }

    /// <summary>Every project file git tracks, with its out-of-directory item patterns.</summary>
    static Dictionary<string, IReadOnlyList<string>> RepositoryPatterns(string root) {
        var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var project in CommittedPaths(root).Where(path => path.EndsWith(".csproj", StringComparison.Ordinal))) {
            patterns[project] = AffectedOwnership.ItemPatterns(project, File.ReadAllText(Path.Combine(root, project)));
        }

        Assert.True(patterns.Count > 150, $"Read {patterns.Count} project files, which is not this tree.");

        return patterns;
    }

    /// <summary>Every path git tracks in this checkout, relative to its root.</summary>
    static List<string> CommittedPaths(string root) {
        using var git = Process.Start(
            new ProcessStartInfo("git", "ls-files") {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        );

        Assert.NotNull(git);

        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        Assert.Equal(0, git.ExitCode);

        return [.. output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
    }

    /// <summary>The repository root, found by walking up from the test binary.</summary>
    static string RepositoryRoot() {
        for (var directory = AppContext.BaseDirectory; directory is not null;) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
