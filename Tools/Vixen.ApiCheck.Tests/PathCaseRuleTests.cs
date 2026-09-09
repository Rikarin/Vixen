// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ That a reference naming a committed path in the wrong case is reported, on the machine
///     least able to notice it.
/// </summary>
/// <remarks>
///     <para>
///         doc 10 § Cross-platform discipline promises <i>"a CI check on Linux catches
///         <c>Texture.PNG</c> vs <c>texture.png</c> before a user does"</i> and there was no such
///         check (<a href="https://github.com/Rikarin/Vixen/issues/329">#329</a>). <c>PathCaseRule</c>
///         is the rule and <c>CheckPathCase</c> is the gate; this is the other caller, for the reason
///         <c>ComponentGeneratorRuleTests</c> and <c>DataContractGeneratorRuleTests</c> exist — a
///         rule whose only answer comes from a gate is a rule nobody has watched produce a positive.
///     </para>
///     <para>
///         ⚠ <b>And here the argument is sharper than usual, because this rule's true answer on this
///         tree is "nothing".</b> A check that reports no violations is indistinguishable from a
///         check that stopped matching, so the synthetic fixtures below are not decoration: they are
///         the only place anything watches the scan say yes.
///     </para>
///     <para>
///         ⚠ <b>The half that is genuinely weaker here than on CI.</b> Two committed paths differing
///         only in case cannot both be checked out on macOS, so
///         <see cref="TheTreeHasNoPathsDifferingOnlyInCase" /> is answered from git's index rather
///         than from the disk. That is the same answer on every platform; what a Mac cannot do is
///         observe the consequence.
///     </para>
/// </remarks>
public sealed class PathCaseRuleTests {
    /// <summary>A committed tree, in the spelling this repository uses.</summary>
    static readonly string[] Tree = [
        "Assets/Textures/Crate.png",
        "Assets/Scenes/Level1.vxscene",
        "Raven/Library/Pipeline/DepthOnly.rvn",
        "Core/Vixen.Ecs/Vixen.Ecs.csproj"
    ];

    /// <summary>
    ///     ⚠ The positive: the exact defect doc 10 names, reported.
    /// </summary>
    /// <remarks>
    ///     <c>Crate.PNG</c> opens <c>Crate.png</c> on macOS and Windows and is a 404 on Linux and
    ///     over HTTP. Nothing else in this repository can see it — it is not a compiler diagnostic,
    ///     not an analyzer finding, and not a failing test, because on the machine that wrote it the
    ///     file opens.
    /// </remarks>
    [Fact]
    public void AMisCasedReferenceIsReported() {
        var violations = PathCaseRule
            .Scan("Samples/Demo/Demo.cs", "var texture = \"Assets/Textures/Crate.PNG\";", PathCaseRule.Index(Tree))
            .ToList();

        var violation = Assert.Single(violations);

        Assert.Equal("Assets/Textures/Crate.PNG", violation.Reference);
        Assert.Equal("Assets/Textures/Crate.png", violation.CommittedAs);
        Assert.Equal(1, violation.Line);
    }

    /// <summary>A mis-cased <em>directory</em> is the same defect and is reported too.</summary>
    /// <remarks>
    ///     The ancestor directories are in the index for this: <c>assets/Textures/Crate.png</c> names
    ///     a file that exists under a directory that does not, and without the directories in the
    ///     index the fold would find the file and call the reference correct.
    /// </remarks>
    [Fact]
    public void AMisCasedDirectoryIsReported() {
        var violations = PathCaseRule
            .Scan("Samples/Demo/Demo.cs", "\"assets/Textures/Crate.png\"", PathCaseRule.Index(Tree))
            .ToList();

        Assert.Equal("Assets/Textures/Crate.png", Assert.Single(violations).CommittedAs);
    }

    /// <summary>A reference relative to the referencing file resolves against that file's directory.</summary>
    /// <remarks>
    ///     This is how every <c>ProjectReference</c> in the tree is spelled, and a rule that only
    ///     rooted at the repository would judge none of them.
    /// </remarks>
    [Fact]
    public void ARelativeReferenceIsResolvedAgainstItsOwnDirectory() {
        var index = PathCaseRule.Index(Tree);

        Assert.Empty(PathCaseRule.Scan("Core/Vixen.Ecs.Tests/x.csproj", @"..\Vixen.Ecs\Vixen.Ecs.csproj", index));

        Assert.Equal(
            "Core/Vixen.Ecs/Vixen.Ecs.csproj",
            Assert.Single(PathCaseRule.Scan("Core/Vixen.Ecs.Tests/x.csproj", @"..\vixen.ecs\Vixen.Ecs.csproj", index))
                .CommittedAs
        );
    }

    /// <summary>
    ///     ⚠ The negative that decides whether this rule is usable at all: a path naming nothing is
    ///     not judged.
    /// </summary>
    /// <remarks>
    ///     Most <c>"Assets/Walk.vxanim"</c> in this tree are synthetic in-memory paths in a test
    ///     fixture and name no committed file. A rule stated as "every path-shaped string must
    ///     resolve" would report hundreds of them, and a gate whose output is hundreds of
    ///     false positives is a gate that gets turned off. "Folds onto a committed path and is not
    ///     one" is the whole rule, and this is the case it excludes.
    /// </remarks>
    [Fact]
    public void APathThatNamesNothingIsNotJudged() {
        Assert.Empty(PathCaseRule.Scan("x.cs", "\"Assets/Nowhere/Missing.PNG\"", PathCaseRule.Index(Tree)));
    }

    /// <summary>A reference in the committed spelling is not a violation.</summary>
    [Fact]
    public void TheCorrectSpellingIsNotAViolation() {
        Assert.Empty(PathCaseRule.Scan("x.cs", "\"Assets/Textures/Crate.png\"", PathCaseRule.Index(Tree)));
    }

    /// <summary>
    ///     ⚠ A literal that is right relative to one base is right, whatever it would mean relative
    ///     to the other.
    /// </summary>
    /// <remarks>
    ///     The rooted form <c>Raven/Library/Pipeline/DepthOnly.rvn</c> written inside
    ///     <c>Raven/README.md</c> also reads as <c>Raven/Raven/Library/…</c> against that file's own
    ///     directory. The first candidate that resolves exactly ends the search; a rule that took the
    ///     first candidate that <em>folded</em> would report every rooted path in a nested file.
    /// </remarks>
    [Fact]
    public void AnExactMatchOnEitherBaseWins() {
        Assert.Empty(
            PathCaseRule.Scan("Raven/README.md", "Raven/Library/Pipeline/DepthOnly.rvn", PathCaseRule.Index(Tree))
        );
    }

    /// <summary>Two committed spellings of one path are found, and one spelling is not.</summary>
    /// <remarks>
    ///     Both halves, because a detector that reports every path is as useless as one that reports
    ///     none — and because <c>Collisions</c> is asserted to be empty over the real tree, where an
    ///     empty answer proves nothing on its own.
    /// </remarks>
    [Fact]
    public void ACaseCollisionIsFoundAndACleanTreeIsNot() {
        Assert.Empty(PathCaseRule.Collisions(Tree));

        var collision = Assert.Single(PathCaseRule.Collisions([.. Tree, "Assets/Textures/CRATE.PNG"]));

        Assert.Equal(["Assets/Textures/CRATE.PNG", "Assets/Textures/Crate.png"], collision);
    }

    /// <summary>
    ///     ⚠ The exemption file is not scanned, and a binary one is not either.
    /// </summary>
    /// <remarks>
    ///     Every exemption line quotes the literal it exempts, so scanning that file reports each of
    ///     them a second time at a line number inside the exemption file — which no exemption covers,
    ///     and which cannot be exempted without quoting the literal a third time. Found by running the
    ///     gate over its own first commit, once the exemption file had been staged and
    ///     <c>git ls-files</c> could see it.
    /// </remarks>
    [Fact]
    public void TheExemptionFileAndBinariesAreNotScanned() {
        Assert.False(PathCaseRule.IsScannable(PathCaseRule.ExemptionsFile));
        Assert.False(PathCaseRule.IsScannable("Samples/03-Lighting/Assets/Crate.png"));
        Assert.True(PathCaseRule.IsScannable("docs/WhitespaceExempt.txt"));
        Assert.True(PathCaseRule.IsScannable("Core/Vixen.Ecs/World.cs"));
    }

    /// <summary>Comment and blank lines in the exemption file are not exemptions.</summary>
    [Fact]
    public void ExemptionsSkipCommentsAndBlanks() {
        Assert.Equal(["a.cs: Some/Path"], PathCaseRule.ReadExemptions("# a comment\n\n  a.cs: Some/Path  \n"));
    }

    /// <summary>
    ///     ⚠ The real tree, and its three exemptions, which are what say the scan ran.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is <c>CheckPathCase</c>'s own body against this checkout, so the gate's answer
    ///         can be watched without a Release solution build. It asserts three things and the
    ///         middle one is the load-bearing one: no unexempted violation, <b>every exemption still
    ///         reported</b>, and enough files read to be this repository.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>git ls-files</c> rather than a directory walk, for the reason
    ///         <c>CheckLicenceHeaders</c> settled on and one more: inside an agent's worktree it
    ///         lists that worktree, so this cannot read the whole checkout each agent keeps under
    ///         <c>.claude/worktrees/</c> — the trap the golden walk and <c>CheckStrings</c> each hit.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheRepositoryHasNoMisCasedReferences() {
        var root = RepositoryRoot();
        var committed = CommittedPaths(root);

        Assert.True(committed.Count > 3000, $"`git ls-files` returned {committed.Count} paths, which is not this tree.");

        var index = PathCaseRule.Index(committed);
        var exempt = PathCaseRule.ReadExemptions(
            File.ReadAllText(Path.Combine(root, PathCaseRule.ExemptionsFile.Replace('/', Path.DirectorySeparatorChar)))
        );
        var violations = new List<PathCaseRule.Violation>();
        var scanned = 0;

        foreach (var path in committed.Where(PathCaseRule.IsScannable)) {
            var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(file)) {
                continue;
            }

            scanned++;
            violations.AddRange(PathCaseRule.Scan(path, File.ReadAllText(file), index));
        }

        Assert.True(scanned > 3000, $"Read {scanned} committed text files, which is not this tree.");

        Assert.Equal(
            string.Empty,
            string.Join('\n', violations.Where(violation => !exempt.Contains(violation.Key())).Select(v => v.ToString()))
        );

        Assert.Equal(
            string.Empty,
            string.Join(
                '\n',
                exempt
                    .Where(key => violations.All(violation => violation.Key() != key))
                    .Order(StringComparer.Ordinal)
                    .Select(key => $"{key} is exempt and no longer reported — delete the line, or the scan has stopped matching.")
            )
        );
    }

    /// <summary>
    ///     ⚠ The one platform consequence a Mac cannot observe, asserted from the index instead.
    /// </summary>
    /// <remarks>
    ///     git can hold <c>Foo.cs</c> and <c>foo.cs</c>; a case-folding checkout gets one of them and
    ///     the other is missing from the working tree with no error anywhere. This tree has been bitten
    ///     by the same fold from the other side already — <c>.gitignore</c>'s own comment records a
    ///     <c>Build/</c> line that made the whole Nuke <c>build/</c> project invisible to git on macOS
    ///     and Windows.
    /// </remarks>
    [Fact]
    public void TheTreeHasNoPathsDifferingOnlyInCase() {
        var collisions = PathCaseRule.Collisions(CommittedPaths(RepositoryRoot()));

        Assert.Equal(string.Empty, string.Join('\n', collisions.Select(group => string.Join(" and ", group))));
    }

    /// <summary>Every path git tracks in this checkout, relative to its root.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The paths, <c>/</c>-separated as git spells them.</returns>
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
    /// <returns>The absolute path.</returns>
    /// <remarks>
    ///     ⚠ Walked from <see cref="AppContext.BaseDirectory" /> rather than anchored on a
    ///     <c>[CallerFilePath]</c>: CI sets <c>ContinuousIntegrationBuild</c>, which turns on
    ///     <c>DeterministicSourcePaths</c> and rewrites every compiled source path to <c>/_/…</c>, so
    ///     a file-anchored root passes here and fails on all three runners at once.
    /// </remarks>
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
