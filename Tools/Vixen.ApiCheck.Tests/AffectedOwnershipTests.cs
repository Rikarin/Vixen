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

    /// <summary>
    ///     ⚠ <c>Raven/README.md</c> is projectless by the area-README clause and still selects the
    ///     suite that reads it.
    /// </summary>
    /// <remarks>
    ///     #1319 made it projectless so that <c>--since</c> stopped refusing it, while
    ///     <c>ReadmeExampleTests</c> compiles its examples and <c>DiagnosticCoverageTests</c> checks its
    ///     table. On the real tree the suite was still reached, but only because two Raven projects
    ///     pack the file as their NuGet readme; with no patterns — the position it is in the day that
    ///     include moves — <c>Projects</c> came back empty before the declared reader.
    /// </remarks>
    [Fact]
    public void TheRavenReadmeIsOwnedByTheSuiteThatReadsIt() {
        var ownership = AffectedOwnership.Classify(["Raven/README.md"], NoContainer, NoPatterns);

        Assert.Empty(ownership.Orphans);

        // Not only the Raven suite since #1506: the two `.md` sweeps read every README as well.
        Assert.Equal(
            [
                "Core/Vixen.Ui.Styling.Utilities.Tests/Vixen.Ui.Styling.Utilities.Tests.csproj",
                RavenTests,
                "Tools/Vixen.DocGen.Tests/Vixen.DocGen.Tests.csproj"
            ],
            ownership.Projects
        );

        // A declared file is that file, not a prefix of a longer name.
        Assert.Empty(AffectedOwnership.ReadersOf("Raven/README.md.orig", NoPatterns));
    }

    /// <summary>
    ///     #1446's two groups that something does read select that reader, rather than refusing or
    ///     being exempted as projectless.
    /// </summary>
    [Theory]
    [InlineData("Tools/Vixen.BcnOracle/bcn-oracle.c", "Core/Vixen.Core.Imaging.Tests/Vixen.Core.Imaging.Tests.csproj")]
    [InlineData("Tools/Vixen.BcnOracle/build.sh", "Core/Vixen.Core.Imaging.Tests/Vixen.Core.Imaging.Tests.csproj")]
    [InlineData("Testing/ContentDeterminism/Assets/UiCore.vxgroup", "build/_build.csproj")]
    [InlineData("Testing/ContentDeterminism/README.md", "build/_build.csproj")]
    public void AFileReadOnlyByAToolOrATargetIsOwnedByIt(string file, string reader) {
        Assert.False(AffectedOwnership.OwnedByNoProject(file));

        var ownership = AffectedOwnership.Classify([file], NoContainer, NoPatterns);

        Assert.Empty(ownership.Orphans);

        // A README is also read by the `.md` sweeps (#1506), so it is this reader and theirs.
        Assert.Equal(
            file.EndsWith(".md", StringComparison.Ordinal)
                ? [.. AffectedOwnership.ReadersOf("x/README.md", NoPatterns).Append(reader).Order(StringComparer.Ordinal)]
                : [reader],
            ownership.Projects
        );
    }

    /// <summary>#1446's prose and markers: projectless, each by name.</summary>
    [Theory]
    [InlineData(".claude/skills/vixen/SKILL.md")]
    [InlineData("Core/Vixen.Generators.Shared/README.md")]
    [InlineData("Samples/14-Mmo/Mmo.vxproj")]
    [InlineData("Samples/14-Mmo/README.md")]
    [InlineData("Samples/14-Mmo/Assets/Default.vxgroup")]
    public void ProseAndMarkersNothingReadsAreProjectless(string file) {
        Assert.True(AffectedOwnership.OwnedByNoProject(file));
    }

    /// <summary>
    ///     And the Mmo exemption is those three files, not the sample: a source file at its root is
    ///     still an orphan.
    /// </summary>
    [Fact]
    public void TheMmoExemptionIsNotTheWholeSample() {
        Assert.False(AffectedOwnership.OwnedByNoProject("Samples/14-Mmo/Stray.cs"));
        Assert.False(AffectedOwnership.OwnedByNoProject("Samples/14-Mmo/Assets/Other.vxgroup"));
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
    ///     ⚠ The shared <c>Testing/</c> helpers are owned by the projects that import them, through
    ///     the props file's <c>$(MSBuildThisFileDirectory)</c>.
    /// </summary>
    /// <remarks>
    ///     No project contains <c>Testing/GoldenFile.cs</c>; four import
    ///     <c>Vixen.Testing.GoldenFile.props</c>, which compiles it into each. Reading only the
    ///     project file owned the props and left the source it links an orphan.
    /// </remarks>
    [Fact]
    public void ASharedTestingHelperIsOwnedByItsImporters() {
        var patterns = RepositoryPatterns(RepositoryRoot());
        var readers = AffectedOwnership.ReadersOf("Testing/GoldenFile.cs", patterns);

        Assert.Contains(RavenTests, readers);
        Assert.True(readers.Count >= 2, $"Testing/GoldenFile.cs is read by {readers.Count} project(s): {string.Join(", ", readers)}.");
    }

    /// <summary>
    ///     ⚠ A nested <c>Directory.Build.props</c> is owned by every project beneath it.
    /// </summary>
    [Fact]
    public void ANestedDirectoryBuildPropsIsOwnedByTheProjectsBeneathIt() {
        var patterns = RepositoryPatterns(RepositoryRoot());
        var readers = AffectedOwnership.ReadersOf("Raven/Directory.Build.props", patterns);

        Assert.Contains(RavenTests, readers);
        Assert.Contains("Raven/Vixen.Raven/Vixen.Raven.csproj", readers);
        Assert.All(readers, reader => Assert.StartsWith("Raven/", reader, StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ Every committed file on this tree has an owner, or is one of the orphans named here —
    ///     exactly, in both directions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The orphan assertion in <c>ProjectsOwning</c> only fires on the day somebody changes
    ///         the file, which is how <c>Raven/Library/</c> went unnoticed until two merge agents
    ///         hit it in one sweep. This asks the same question of every committed file ahead of
    ///         time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The list is what <c>--since</c> still refuses, and it is empty</b> since #1446
    ///         decided its nineteen rows: <c>Testing/ContentDeterminism/</c> and
    ///         <c>Tools/Vixen.BcnOracle/</c> have declared readers, and <c>.claude/</c>, the
    ///         generator README and the Mmo sample's root are projectless. It can only stay empty; a
    ///         new file in a place nothing owns reds this before it reds somebody's narrowed run, and
    ///         a row added here needs a written reason beside it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryCommittedFileHasAnOwnerOrIsANamedOrphan() {
        string[] named = [];

        var root = RepositoryRoot();
        var committed = CommittedPaths(root);
        var containers = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ownership = AffectedOwnership.Classify(committed, path => Containing(root, path, containers), RepositoryPatterns(root));

        Assert.True(committed.Count > 3000, $"`git ls-files` returned {committed.Count} paths, which is not this tree.");

        Assert.Equal(
            string.Empty,
            string.Join('\n', ownership.Orphans.Except(named).Select(path => $"{path} is owned by no project and read by none."))
        );

        Assert.Equal(
            string.Empty,
            string.Join('\n', named.Except(ownership.Orphans).Select(path => $"{path} is named as an orphan and is not one — delete its line."))
        );
    }

    /// <summary>The nearest project at or above a committed file, memoised by directory.</summary>
    static string? Containing(string root, string relative, Dictionary<string, string?> byDirectory) {
        var slash = relative.LastIndexOf('/');

        return slash < 0 ? null : Nearest(relative[..slash]);

        string? Nearest(string directory) {
            if (byDirectory.TryGetValue(directory, out var known)) {
                return known;
            }

            var absolute = Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar));
            var project = System.IO.Directory.Exists(absolute) ? System.IO.Directory.GetFiles(absolute, "*.csproj").FirstOrDefault() : null;
            var parent = directory.LastIndexOf('/');

            var answer = project is not null
                ? Path.GetRelativePath(root, project).Replace('\\', '/')
                : parent < 0 ? null : Nearest(directory[..parent]);

            byDirectory[directory] = answer;

            return answer;
        }
    }

    /// <summary>
    ///     A declared reader still exists and still spells the directory it is declared to read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The only hand-kept part of the rule, so the part that can go stale: a reader that
    ///         stopped reading would keep owning the library, and a narrowed run would test the wrong
    ///         thing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The walk is resolved, not the name found.</b> This used to pass whenever the
    ///         evidence file held the literal <c>"Library"</c> anywhere, and
    ///         <c>LibraryReflectionTests.cs</c> spells it twice more for other reasons, so a reader
    ///         that stopped walking to <c>Raven/Library</c> stayed declared. Now some
    ///         <c>Path.Combine(AppContext.BaseDirectory, …)</c> in the evidence has to land on the
    ///         declared prefix when started from the project's own <c>bin/&lt;config&gt;/&lt;tfm&gt;/</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryDeclaredReaderStillReadsWhatItIsDeclaredToRead() {
        var root = RepositoryRoot();

        var patterns = RepositoryPatterns(root);

        Assert.NotEmpty(AffectedOwnership.DeclaredReaders);

        foreach (var reader in AffectedOwnership.DeclaredReaders) {
            Assert.True(File.Exists(Path.Combine(root, reader.Project)), $"{reader.Project} does not exist.");

            var projectDirectory = Path.GetDirectoryName(reader.Project)!.Replace('\\', '/');

            // Its own source, or one it compiles through a link — `build/PathCaseRule.cs` names the
            // exemption list this suite runs the rule against.
            Assert.True(
                reader.Evidence.StartsWith(projectDirectory + "/", StringComparison.Ordinal)
                || (patterns.TryGetValue(reader.Project, out var linked) && linked.Any(pattern => AffectedOwnership.Matches(pattern, reader.Evidence))),
                $"{reader.Evidence} is neither inside {projectDirectory}/ nor linked into {reader.Project}."
            );

            var text = File.ReadAllText(Path.Combine(root, reader.Evidence));

            if (reader.Prefix.Contains('*', StringComparison.Ordinal)) {
                // A glob names no path to walk to; its literal fragments are what the sweep filters on.
                var missing = GlobFragments(reader.Prefix).Where(fragment => !text.Contains('"' + fragment + '"', StringComparison.Ordinal)).ToList();

                Assert.True(
                    missing.Count == 0,
                    $"{reader.Evidence} spells no literal {string.Join(", ", missing)}, so {reader.Project} is declared to sweep {reader.Prefix} and no longer filters on it."
                );

                continue;
            }

            var walks = Reaches(projectDirectory, text);

            Assert.True(
                walks.Contains(reader.Prefix.TrimEnd('/'))
                || (reader.Prefix.EndsWith('/') && walks.Any(walk => walk.StartsWith(reader.Prefix, StringComparison.Ordinal))),
                $"{reader.Evidence} walks to [{string.Join(", ", walks)}], not to {reader.Prefix} — "
                + $"so {reader.Project} is declared to read a path it no longer reads."
            );
        }
    }

    /// <summary>The literal parts of a glob: <c>**/*.md</c> is <c>.md</c>, <c>**/README.md</c> is <c>README.md</c>.</summary>
    static string[] GlobFragments(string glob) =>
        glob.Split(["**/", "*", "?"], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    ///     ⚠ A glob's evidence cannot pass on a different filter, and a directory's cannot pass on a
    ///     sibling with a shared prefix.
    /// </summary>
    [Fact]
    public void A_declared_readers_glob_and_directory_evidence_is_exact() {
        Assert.Equal([".md"], GlobFragments("**/*.md"));
        Assert.Equal(["README.md"], GlobFragments("**/README.md"));

        // A walk to `docs2/x` is not under `docs/`, and `docs/guide` is.
        Assert.DoesNotContain(Reaches("Tools/Vixen.DocGen.Tests", """Path.Combine(root, "docs2", "x")"""), walk => walk.StartsWith("docs/", StringComparison.Ordinal));
        Assert.Contains(Reaches("Tools/Vixen.DocGen.Tests", """Path.Combine(root, "docs", "guide")"""), walk => walk.StartsWith("docs/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ The globs select what they say across the tree, and a declared file is still only that
    ///     file.
    /// </summary>
    [Theory]
    [InlineData("docs/plan/43-web-styling-parity.md", "Core/Vixen.Ui.Styling.Utilities.Tests/Vixen.Ui.Styling.Utilities.Tests.csproj", true)]
    [InlineData("Editor/Vixen.Editor.Ui/README.md", "Tools/Vixen.DocGen.Tests/Vixen.DocGen.Tests.csproj", true)]
    [InlineData("README.md", "Tools/Vixen.DocGen.Tests/Vixen.DocGen.Tests.csproj", true)]
    [InlineData("docs/PlanCitationExempt.txt", "Tools/Vixen.DocGen.Tests/Vixen.DocGen.Tests.csproj", true)]
    [InlineData("docs/PlanCitationExempt.txt", "Core/Vixen.Ui.Styling.Utilities.Tests/Vixen.Ui.Styling.Utilities.Tests.csproj", false)]
    [InlineData("NOTICE.orig", "Core/Vixen.Ui.Layout.Tests/Vixen.Ui.Layout.Tests.csproj", false)]
    public void A_declared_glob_selects_what_it_says(string file, string reader, bool selected) {
        Assert.Equal(selected, AffectedOwnership.ReadersOf(file, NoPatterns).Contains(reader));
    }

    /// <summary>
    ///     ⚠ The issue's own table: each projectless file a test reads selects that test, with no
    ///     project patterns at all — the declared reader alone carries it.
    /// </summary>
    [Theory]
    [InlineData("NOTICE", "Core/Vixen.Ui.Layout.Tests/Vixen.Ui.Layout.Tests.csproj")]
    [InlineData(".github/workflows/ci.yml", "Tools/Vixen.ApiCheck.Tests/Vixen.ApiCheck.Tests.csproj")]
    [InlineData("docs/plan/08-asset-pipeline-and-addressables.md", "Editor/Vixen.Editor.Assets.Tests/Vixen.Editor.Assets.Tests.csproj")]
    [InlineData("docs/guide/ecs/queries.md", "Tools/Vixen.DocGen.Tests/Vixen.DocGen.Tests.csproj")]
    [InlineData("build/test-cost.txt", "Tools/Vixen.ApiCheck.Tests/Vixen.ApiCheck.Tests.csproj")]
    public void AProjectlessFileATestReadsSelectsThatTest(string file, string reader) {
        var ownership = AffectedOwnership.Classify([file], NoContainer, NoPatterns);

        Assert.Empty(ownership.Orphans);
        Assert.Contains(reader, ownership.Projects);
    }

    /// <summary>
    ///     Every repository-relative path <paramref name="source" /> reaches, in the three shapes a
    ///     declared reader's evidence takes.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>
    ///             A test binary walking up: <c>Path.Combine(AppContext.BaseDirectory, "..", …)</c>,
    ///             resolved from the project's own <c>bin/Debug/net10.0</c>.
    ///         </item>
    ///         <item>
    ///             A build target: <c>RootDirectory / "Testing" / "ContentDeterminism"</c>, resolved
    ///             from the root (#1446).
    ///         </item>
    ///         <item>
    ///             ⚠ The weakest, kept for the one reader nothing on disk connects it to: a string
    ///             literal spelling the whole prefix. <c>Tools/Vixen.BcnOracle/</c> is compiled by a
    ///             script into a cache outside the tree and the suite finds the binary there, so the
    ///             suite's own instruction to run that script is the only edge. It is the whole
    ///             repository-relative directory, not a word like <c>"Library"</c>, which is what
    ///             made the old name check pass for a reader that had stopped walking.
    ///         </item>
    ///     </list>
    /// </remarks>
    static List<string> Reaches(string projectDirectory, string source) => [
        .. BaseDirectoryWalk.Matches(source).Select(walk => Resolve(projectDirectory + "/bin/Debug/net10.0", walk.Groups["segments"].Value))
            .Concat(RootDirectoryWalk.Matches(source).Select(walk => Resolve(string.Empty, walk.Groups["segments"].Value.Replace('/', ','))))
            .Concat(ScriptInstruction.Matches(source).Select(match => match.Groups["directory"].Value))

            // #1506: the shapes the census reads — a root walk, and a path constant.
            .Concat(RepositoryReads(projectDirectory, source))
            .Distinct(StringComparer.Ordinal)
    ];

    /// <summary>
    ///     ⚠ The evidence check cannot pass for a reader in the wrong place, in any of its shapes.
    /// </summary>
    [Theory]
    [InlineData("Raven/Vixen.Raven.Tests", """Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "README.md")""", "Raven/README.md")]
    [InlineData("build", """AbsolutePath Fixture => RootDirectory / "Testing" / "ContentDeterminism";""", "Testing/ContentDeterminism")]
    [InlineData("build", """AbsolutePath Fixture => RootDirectory / "Testing";""", "Testing")]
    [InlineData("Core/Vixen.Core.Imaging.Tests", """Missing("x", "Build it: sh Tools/Vixen.BcnOracle/build.sh.");""", "Tools/Vixen.BcnOracle")]
    public void A_declared_readers_evidence_resolves_to_one_place(string project, string source, string expected) {
        Assert.Equal([expected], Reaches(project, source));
    }

    /// <summary>A Nuke path built from the repository root out of string literals.</summary>
    static readonly System.Text.RegularExpressions.Regex RootDirectoryWalk = new(
        """RootDirectory(?<segments>(?:\s*/\s*"[^"]*")+)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>A script under a repository directory, named as the command that builds what is read: <c>sh Dir/Sub/build.sh</c>.</summary>
    static readonly System.Text.RegularExpressions.Regex ScriptInstruction = new(
        """\bsh (?<directory>[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+)/[A-Za-z0-9_.-]+\.sh\b""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>
    ///     ⚠ The check above cannot pass by finding a word: a walk one level short, or to a sibling
    ///     directory, resolves elsewhere.
    /// </summary>
    [Theory]
    [InlineData("""Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library")""", "Raven/Library")]
    [InlineData("""Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Library")""", "Raven/Vixen.Raven.Tests/Library")]
    [InlineData("""Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Library2")""", "Raven/Library2")]
    public void A_declared_readers_walk_resolves_from_its_binary(string source, string expected) {
        var walk = Assert.Single(BaseDirectoryWalk.Matches(source));

        Assert.Equal(expected, Resolve("Raven/Vixen.Raven.Tests/bin/Debug/net10.0", walk.Groups["segments"].Value));
    }

    /// <summary>A path built from the test binary's directory out of string literals.</summary>
    static readonly System.Text.RegularExpressions.Regex BaseDirectoryWalk = new(
        """Path\.Combine\(\s*AppContext\.BaseDirectory(?<segments>(?:\s*,\s*"[^"]*")+)\s*\)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>Applies a walk's literal segments to a repository-relative start.</summary>
    static string Resolve(string start, string segments) {
        List<string> path = start.Length == 0 ? [] : [.. start.Split('/')];

        foreach (var segment in segments.Split(',').Select(part => part.Trim().Trim('"')).Where(part => part.Length > 0)) {
            if (segment == "..") {
                path.RemoveAt(path.Count - 1);
            } else {
                path.Add(segment);
            }
        }

        return string.Join('/', path);
    }

    /// <summary>
    ///     ⚠ Every repository file a test reads off disk, and that no solution project contains,
    ///     selects that test: an edit to it cannot narrow to a run that skips its reader (#1506).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>NOTICE</c>, <c>.github/workflows/ci.yml</c> and a dozen files under <c>docs/</c> are
    ///         <see cref="AffectedOwnership.OwnedByNoProject" />, and before #1506 nothing but
    ///         <c>Raven/README.md</c> had a declared reader, so an edit to any of them narrowed
    ///         <c>AffectedTests --since</c> to no tests and said nothing. The same is true one step over
    ///         for a file inside <c>build/</c>, whose container <c>_build.csproj</c> is outside the
    ///         solution: <c>build/test-cost.txt</c> is read by this suite and nothing selected it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A census and not a list, because a list is a floor.</b> The issue's table named five
    ///         readers; the sweep found twenty-odd. What it reads is the two shapes a test here
    ///         spells a repository path in: a <c>Path.Combine</c> whose trailing arguments are string
    ///         literals, resolved from the repository root (or from the project's
    ///         <c>bin/Debug/net10.0</c> when it starts at <c>AppContext.BaseDirectory</c>), and a string
    ///         literal holding a slash that is itself a committed path. A candidate counts only when
    ///         it lands on a committed file or directory, which is what keeps a fixture's scratch tree
    ///         out. A file contained by a solution project is not this test's question — the reverse
    ///         reference closure answers it, or the documented "a test that walks the repository"
    ///         blind spot does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it cannot see</b> is a sweep that never spells the path: a
    ///         <c>RepositoryFiles.Listed</c> filtered by extension. Those readers are declared by glob in
    ///         <see cref="AffectedOwnership.DeclaredReaders" /> and held to their evidence by
    ///         <see cref="EveryDeclaredReaderStillReadsWhatItIsDeclaredToRead" /> instead.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryRepositoryFileATestReadsSelectsThatTest() {
        var root = RepositoryRoot();
        var committed = CommittedPaths(root);
        var files = committed.ToHashSet(StringComparer.Ordinal);
        var solution = SolutionProjects(root).ToHashSet(StringComparer.Ordinal);
        var patterns = RepositoryPatterns(root);
        var containers = new Dictionary<string, string?>(StringComparer.Ordinal);
        var unselected = new List<string>();
        var seen = new HashSet<(string Source, string Path)>();
        var reads = 0;

        var sources = committed.Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".vxml", StringComparison.Ordinal)).ToList();
        var testProjects = solution.Where(project => project.EndsWith(".Tests.csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

        // What each test project compiles: its own directory, and every source it links from outside
        // — `build/ComponentGeneratorRule.cs` names `docs/ComponentGeneratorExempt.txt` in a constant,
        // and this suite runs that rule against the real tree.
        var compiled = sources
            .Select(source => (Source: source, Project: Containing(root, source, containers)))
            .Where(pair => pair.Project is not null && testProjects.Contains(pair.Project))
            .Select(pair => (pair.Source, Project: pair.Project!))
            .Concat(
                testProjects.SelectMany(project => patterns[project]
                    .Where(pattern => pattern.EndsWith(".cs", StringComparison.Ordinal) || pattern.EndsWith(".vxml", StringComparison.Ordinal))
                    .SelectMany(pattern => sources.Where(source => AffectedOwnership.Matches(pattern, source)))
                    .Select(source => (Source: source, Project: project)))
            )
            .Distinct()
            .ToList();

        foreach (var (source, project) in compiled) {
            // This file's own fixtures are repository paths by subject, and none of them is a read.
            if (source == "Tools/Vixen.ApiCheck.Tests/AffectedOwnershipTests.cs") {
                continue;
            }

            var text = File.ReadAllText(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)));

            foreach (var read in RepositoryReads(Path.GetDirectoryName(project)!.Replace('\\', '/'), text).Distinct(StringComparer.Ordinal)) {
                // A directory counts only when it is itself projectless — `docs/guide`, `.github/workflows`.
                // A walk to `Core` or `Raven/Library` is a repository sweep, the blind spot named above.
                List<string> targets = files.Contains(read)
                    ? [read]
                    : AffectedOwnership.OwnedByNoProject(read + "/")
                        ? [.. committed.Where(path => path.StartsWith(read + "/", StringComparison.Ordinal))]
                        : [];

                if (targets.Count == 0) {
                    continue;
                }

                if (NotReads.Any(entry => entry.Source == source && entry.Path == read)) {
                    seen.Add((source, read));

                    continue;
                }

                // Projectless, or contained by a project the solution does not hold (`build/`): the two
                // places a narrowed run cannot reach through the reverse reference closure.
                var outside = targets.Where(target => Containing(root, target, containers) is { } container
                        ? !solution.Contains(container)
                        : AffectedOwnership.OwnedByNoProject(target))
                    .ToList();

                if (outside.Count == 0) {
                    continue;
                }

                reads++;

                if (outside.FirstOrDefault(target => !AffectedOwnership.ReadersOf(target, patterns).Contains(project)) is { } missed) {
                    unselected.Add($"{source} reads {read}, and an edit to {missed} does not select {project}.");
                }
            }
        }

        // The instrument, before the verdict: a sweep that recognised no read agrees with every rule.
        Assert.True(reads >= 20, $"The sweep recognised only {reads} read(s) of a file outside every solution project, which is not this tree.");

        // Assert.True and not Assert.Equal on a joined string: Equal truncates the difference to its
        // first few dozen characters, and the whole list is the finding.
        Assert.True(unselected.Count == 0, string.Join('\n', unselected));

        var stale = NotReads.Where(entry => !seen.Contains((entry.Source, entry.Path))).ToList();

        Assert.True(stale.Count == 0, string.Join('\n', stale.Select(entry => $"{entry.Source} no longer spells {entry.Path} — delete its NotReads line.")));
    }

    /// <summary>
    ///     ⚠ The census cannot pass by recognising nothing: each shape it reads resolves to the one
    ///     repository path it spells, and a scratch path or a walk from elsewhere does not.
    /// </summary>
    [Theory]
    [InlineData("""File.ReadAllText(Path.Combine(RepositoryRoot(), "NOTICE"))""", "NOTICE")]
    [InlineData("""Path.Combine(root, ".github", "workflows", "ci.yml")""", ".github/workflows/ci.yml")]
    [InlineData("Path.Combine(dir.FullName,\n    \"docs\",\n    \"plan\", \"43-web-styling-parity.tsv\")", "docs/plan/43-web-styling-parity.tsv")]
    [InlineData("""Path.Combine(RavenDirectory(), "..", "docs", "overview.md")""", "docs/overview.md")]
    [InlineData("""Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "README.md")""", "Raven/README.md")]
    [InlineData("""const string Ledger = "docs/MarkupPending.txt";""", "docs/MarkupPending.txt")]
    [InlineData("""Path.Combine(root, "docs/guide")""", "docs/guide")]
    public void A_repository_read_resolves_to_the_path_it_spells(string source, string expected) {
        Assert.Equal([expected], RepositoryReads("Raven/Vixen.Raven.Tests", source));
    }

    /// <summary>The negative: prose with a slash, and a root-marker probe, are not reads.</summary>
    [Theory]
    [InlineData("""$"docs/plan/43 asks for {x}" """)]
    [InlineData("x = \"docs/plan/41 § D14 asks for the same bits.\";")]
    [InlineData("""File.Exists(Path.Combine(directory, "Vixen.slnx"))""")]
    [InlineData("""Path.Combine(AppContext.BaseDirectory, "InertProperties.txt")""")]
    public void A_non_read_is_not_recognised_as_one(string source) {
        Assert.DoesNotContain(
            RepositoryReads("Core/Vixen.Ui.Styling.Utilities.Tests", source),
            path => path is "Vixen.slnx" or "InertProperties.txt" || path.StartsWith("docs/", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     Spellings the census recognises that are not reads of the repository file they happen to
    ///     name, each with the reason. Held exactly, like the named orphans: a line whose spelling
    ///     is gone reds the census.
    /// </summary>
    static readonly (string Source, string Path, string Reason)[] NotReads = [
        // `RavenDirectory()` is `Raven/`, so this is `Raven/README.md`, which has its declared reader;
        // resolved from the root, the census would read it as the repository README.
        ("Raven/Vixen.Raven.Tests/DiagnosticCoverageTests.cs", "README.md", "a walk from Raven/, not the root"),
        // A scratch project the command under test writes; the name collides with the root file.
        ("Tools/Vixen.Cli.Tests/VixenCommandTests.cs", ".gitignore", "the scaffolded project's own .gitignore"),
        // `root` here is a temporary directory the release writer is pointed at.
        ("Tools/Vixen.DocGen.Tests/ReleaseTests.cs", "CHANGELOG.md", "a scratch store, not the repository changelog"),
        // A scratch tree's `docs/`, created to hold a fixture exemption list. The real lists these
        // suites read are declared through the linked rules' constants.
        ("Tools/Vixen.ApiCheck.Tests/ComponentGeneratorRuleTests.cs", "docs", "a scratch tree's docs/"),
        ("Tools/Vixen.ApiCheck.Tests/GeneratorPackagingRuleTests.cs", "docs", "a scratch tree's docs/")
    ];

    /// <summary>
    ///     Every repository-relative path <paramref name="source" /> spells as a read, in the two
    ///     shapes a test in this tree uses: a <c>Path.Combine</c> ending in literals, and a slashed
    ///     literal that is a path on its own.
    /// </summary>
    /// <param name="projectDirectory">The reading project's directory, for a walk from its binary.</param>
    /// <param name="source">A source file's text.</param>
    /// <returns>Candidate paths; the caller keeps those that are committed.</returns>
    /// <remarks>
    ///     A walk from anything but <c>AppContext.BaseDirectory</c> is resolved from the root, with a
    ///     leading <c>..</c> dropped, because the head is an expression this cannot evaluate and the
    ///     root is what every repository-reading test computes. Where that is wrong it is named in
    ///     <see cref="NotReads" />. A <c>File.Exists</c> or <c>Directory.Exists</c> probe for
    ///     <c>Vixen.slnx</c> is the marker every root search climbs to, not a read of the solution.
    /// </remarks>
    static List<string> RepositoryReads(string projectDirectory, string source) {
        var reads = new List<string>();

        foreach (System.Text.RegularExpressions.Match walk in CombineWalk.Matches(source)) {
            var fromBinary = walk.Groups["head"].Value.Trim() == "AppContext.BaseDirectory";
            List<string> path = fromBinary ? [.. (projectDirectory + "/bin/Debug/net10.0").Split('/')] : [];
            var escaped = false;

            foreach (var segment in System.Text.RegularExpressions.Regex.Matches(walk.Groups["segments"].Value, "\"([^\"]*)\"")
                         .SelectMany(literal => literal.Groups[1].Value.Split('/', StringSplitOptions.RemoveEmptyEntries))) {
                if (segment == "..") {
                    if (path.Count > 0) {
                        path.RemoveAt(path.Count - 1);
                    } else if (fromBinary) {
                        escaped = true;
                    }
                } else if (segment != ".") {
                    path.Add(segment);
                }
            }

            var resolved = string.Join('/', path);
            var probe = source[..walk.Index].TrimEnd().EndsWith("Exists(", StringComparison.Ordinal);

            if (!escaped && resolved.Length > 0 && !(probe && resolved == "Vixen.slnx")) {
                reads.Add(resolved);
            }
        }

        reads.AddRange(PathConstant.Matches(source).Select(constant => constant.Groups["path"].Value.TrimEnd('/')));

        return [.. reads.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A <c>Path.Combine</c> whose first argument is an expression and the rest are literals.</summary>
    static readonly System.Text.RegularExpressions.Regex CombineWalk = new(
        """Path\.Combine\(\s*(?<head>[\w.]+(?:\(\))?)(?<segments>(?:\s*,\s*"[^"]*")+)\s*\)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>
    ///     A <c>const string</c> whose whole value is a slashed path, which is how a suite that reads
    ///     one file names it once for its reads and its messages.
    /// </summary>
    /// <remarks>
    ///     ⚠ A constant, not any literal: a bare slashed literal is far more often a fixture's
    ///     argument — <c>Write("docs/manual/log-events.md", …)</c> into a scratch tree, or a path a
    ///     rule is asked to classify — and a census that counted those was mostly noise.
    /// </remarks>
    static readonly System.Text.RegularExpressions.Regex PathConstant = new(
        """const\s+string\s+\w+\s*=\s*"(?<path>[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+/?)"\s*;""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    /// <summary>Every project in <c>Vixen.slnx</c>, with its out-of-directory item patterns.</summary>
    /// <remarks>
    ///     ⚠ <b>The solution's projects, because that is the build's universe</b>
    ///     (<c>Build.Affected.cs</c>, <c>ItemPatternsBySolutionProject</c>). Read from every tracked
    ///     <c>.csproj</c> instead — 429 against the solution's 407 — a file read only by a mobile or
    ///     web head, a template, or <c>_build.csproj</c> itself would pass the census here and still
    ///     make <c>--since</c> refuse it as belonging to no project.
    /// </remarks>
    static Dictionary<string, IReadOnlyList<string>> RepositoryPatterns(string root) {
        var patterns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var project in SolutionProjects(root)) {
            patterns[project] = AffectedOwnership.ItemPatterns(
                project,
                File.ReadAllText(Path.Combine(root, project)),
                imported => File.Exists(Path.Combine(root, imported)) ? File.ReadAllText(Path.Combine(root, imported)) : null
            );
        }

        Assert.True(patterns.Count > 150, $"Read {patterns.Count} project files, which is not this tree.");

        return patterns;
    }

    /// <summary>The <c>.csproj</c> paths <c>Vixen.slnx</c> lists, repository-relative and <c>/</c>-separated.</summary>
    static List<string> SolutionProjects(string root) {
        var solution = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Vixen.slnx"));

        List<string> projects = [
            .. solution.Descendants("Project")
                .Select(project => ((string?)project.Attribute("Path"))?.Replace('\\', '/'))
                .OfType<string>()
                .Where(path => path.EndsWith(".csproj", StringComparison.Ordinal) && File.Exists(Path.Combine(root, path)))
                .Distinct(StringComparer.Ordinal)
        ];

        Assert.True(projects.Count > 150, $"Read {projects.Count} projects out of Vixen.slnx, which is not this solution.");

        return projects;
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
