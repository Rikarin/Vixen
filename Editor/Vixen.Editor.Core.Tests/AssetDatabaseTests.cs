// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Editor.Core.Tests;

public sealed class AssetDatabaseTests {
    [Fact]
    public void EveryAssetIsFoundByItsGuidAndByItsPath() {
        using var project = new ProjectFixture();
        var hero = project.Add("Assets/Textures/hero.png");
        project.Add("Assets/Models/hero.fbx");

        var database = new AssetDatabase(project.Paths);
        var report = database.Scan();

        // Two files and the two folders they are in: a folder is an asset too, because that is where
        // an addressable group is inherited from.
        Assert.Equal(4, report.Assets);
        Assert.True(database.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Textures/hero.png", entry.Path);
        Assert.Equal("TextureImporter", entry.ImporterTag);
        Assert.True(database.TryGetByPath("Assets/Models", out var folder));
        Assert.True(folder.IsFolder);
    }

    [Fact]
    public void MovingAnAssetChangesItsPathAndNotItsIdentity() {
        using var project = new ProjectFixture();
        var hero = project.Add("Assets/Textures/hero.png");

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        Directory.CreateDirectory(project.Paths.Absolute("Assets/Ui"));
        File.Move(project.Paths.Absolute("Assets/Textures/hero.png"), project.Paths.Absolute("Assets/Ui/hero.png"));
        File.Move(
            project.Paths.Absolute("Assets/Textures/hero.png.meta"),
            project.Paths.Absolute("Assets/Ui/hero.png.meta")
        );

        database.Scan();

        Assert.True(database.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Ui/hero.png", entry.Path);
    }

    [Fact]
    public void AFileWithNoSidecarGetsOneAndIsReported() {
        using var project = new ProjectFixture();
        project.AddWithoutMeta("Assets/Textures/hero.png");

        var report = new AssetDatabase(project.Paths).Scan();

        Assert.True(File.Exists(project.Paths.Absolute("Assets/Textures/hero.png.meta")));
        // The folder gets one too: a folder is an asset, because that is where an addressable group
        // is inherited from.
        Assert.Contains(
            report.Issues,
            issue => issue is { Kind: AssetIssueKind.MetaCreated, Path: "Assets/Textures/hero.png" }
        );
    }

    /// <summary>
    ///     A sidecar whose asset has gone is moved aside, never deleted. A mis-ordered git operation
    ///     is recoverable if the GUID is still somewhere on disk, and is not if the editor helpfully
    ///     tidied it away.
    /// </summary>
    [Fact]
    public void AnOrphanedSidecarIsQuarantinedRatherThanDeleted() {
        using var project = new ProjectFixture();
        project.AddOrphanMeta("Assets/Textures/gone.png");

        var report = new AssetDatabase(project.Paths).Scan();

        Assert.False(File.Exists(project.Paths.Absolute("Assets/Textures/gone.png.meta")));
        Assert.True(File.Exists(Path.Combine(project.Paths.OrphanMeta, "Assets/Textures/gone.png.meta")));
        Assert.Single(report.Issues, issue => issue.Kind == AssetIssueKind.MetaOrphaned);
    }

    /// <summary>
    ///     The copy-pasted-folder disaster. Both files claim one GUID; the one whose recorded
    ///     <c>sourceHash</c> still describes its bytes is the original and keeps it.
    /// </summary>
    [Fact]
    public void TheAssetWhoseSourceHashStillMatchesKeepsTheGuid() {
        using var project = new ProjectFixture();
        var shared = AssetId.New();

        // The copy is at the alphabetically earlier path, so a rule based on path order alone would
        // pick the wrong one — which is what makes this test about the hash.
        project.Add("Assets/Copy/hero.png", "different bytes", shared, sourceHash: ProjectFixture.HashOf("original"));
        project.Add("Assets/Original/hero.png", "original", shared, sourceHash: ProjectFixture.HashOf("original"));

        var database = new AssetDatabase(project.Paths);
        var report = database.Scan();

        Assert.True(database.TryGetByGuid(shared, out var kept));
        Assert.Equal("Assets/Original/hero.png", kept.Path);

        var issue = Assert.Single(report.Issues, issue => issue.Kind == AssetIssueKind.DuplicateGuid);
        Assert.Contains("Assets/Copy/hero.png", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Assets/Original/hero.png", issue.Message, StringComparison.Ordinal);

        // And the loser really was re-GUIDed on disk, not just in memory.
        Assert.True(database.TryGetByPath("Assets/Copy/hero.png", out var loser));
        Assert.NotEqual(shared, loser.Guid);
        Assert.Contains(loser.Guid.ToString(), File.ReadAllText(project.Paths.Absolute("Assets/Copy/hero.png.meta")), StringComparison.Ordinal);
    }

    /// <summary>
    ///     When no hash settles it, the first path in order keeps the GUID — a rule rather than
    ///     whichever file the filesystem happened to hand over first, so two machines scanning one
    ///     checkout agree.
    /// </summary>
    [Fact]
    public void WhenNoHashSettlesItTheFirstPathInOrderKeepsTheGuid() {
        using var project = new ProjectFixture();
        var shared = AssetId.New();
        project.Add("Assets/b.png", "b", shared);
        project.Add("Assets/a.png", "a", shared);

        var database = new AssetDatabase(project.Paths);
        var report = database.Scan();

        Assert.True(database.TryGetByGuid(shared, out var kept));
        Assert.Equal("Assets/a.png", kept.Path);
        Assert.Contains("first path in order", Assert.Single(report.Issues).Message, StringComparison.Ordinal);
    }

    /// <summary>Re-GUIDing rewrites the identity and nothing else, comments included.</summary>
    [Fact]
    public void ReGuidingKeepsEverythingElseTheSidecarSaid() {
        using var project = new ProjectFixture();
        var shared = AssetId.New();
        project.Add("Assets/a.png", "a", shared);
        project.Add("Assets/b.png", "b", shared);

        File.WriteAllText(
            project.Paths.Absolute("Assets/b.png.meta"),
            $"# an artist's note\nguid: {shared}\nmetaVersion: 1\nimporter: !TextureImporter\n  maxSize: 512\n"
        );

        new AssetDatabase(project.Paths).Scan();

        var rewritten = File.ReadAllText(project.Paths.Absolute("Assets/b.png.meta"));
        Assert.StartsWith("# an artist's note\n", rewritten, StringComparison.Ordinal);
        Assert.Contains("maxSize: 512", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain(shared.ToString(), rewritten, StringComparison.Ordinal);
    }

    /// <summary>A build server asking "is this project clean?" wants the answer, not a working tree with edits in it.</summary>
    [Fact]
    public void AReadOnlyScanReportsEverythingAndChangesNothing() {
        using var project = new ProjectFixture();
        project.AddWithoutMeta("Assets/hero.png");
        project.AddOrphanMeta("Assets/gone.png");

        var report = new AssetDatabase(project.Paths).Scan(ScanOptions.ReadOnly);

        Assert.False(File.Exists(project.Paths.Absolute("Assets/hero.png.meta")));
        Assert.True(File.Exists(project.Paths.Absolute("Assets/gone.png.meta")));
        Assert.Equal(2, report.Issues.Count);
    }

    /// <summary>
    ///     A sidecar with no readable GUID is left alone rather than re-created: minting a new one
    ///     would break every reference to the asset, which is a worse outcome than an asset the
    ///     editor refuses to touch until a person looks at it.
    /// </summary>
    [Fact]
    public void AnUnreadableSidecarIsReportedRatherThanReplaced() {
        using var project = new ProjectFixture();
        project.AddWithoutMeta("Assets/hero.png");
        File.WriteAllText(project.Paths.Absolute("Assets/hero.png.meta"), "this is not a sidecar\n");

        var database = new AssetDatabase(project.Paths);
        var report = database.Scan();

        Assert.Equal("this is not a sidecar\n", File.ReadAllText(project.Paths.Absolute("Assets/hero.png.meta")));
        Assert.Single(report.Issues, issue => issue.Kind == AssetIssueKind.MetaUnreadable);
        Assert.False(database.TryGetByPath("Assets/hero.png", out _));
    }

    [Fact]
    public void TheIndexSurvivesBeingWrittenAndReadBack() {
        using var project = new ProjectFixture();
        var hero = project.Add("Assets/Textures/hero.png");

        var written = new AssetDatabase(project.Paths);
        written.Scan();
        written.Save();

        var loaded = new AssetDatabase(project.Paths);

        Assert.True(loaded.TryLoad());
        Assert.Equal(written.Count, loaded.Count);
        Assert.True(loaded.TryGetByGuid(hero, out var entry));
        Assert.Equal("Assets/Textures/hero.png", entry.Path);
        Assert.False(loaded.IsStale());
    }

    [Fact]
    public void AddingAnAssetMakesTheSavedIndexStale() {
        using var project = new ProjectFixture();
        project.Add("Assets/a.png");

        var database = new AssetDatabase(project.Paths);
        database.Scan();
        database.Save();

        project.Add("Assets/b.png");

        Assert.True(database.IsStale());
    }

    [Fact]
    public void AProjectWithNoAssetsDirectoryScansToNothing() {
        using var project = new ProjectFixture();
        Directory.Delete(project.Paths.Assets);

        var report = new AssetDatabase(project.Paths).Scan();

        Assert.Equal(0, report.Assets);
        Assert.Empty(report.Issues);
    }

    /// <summary>
    ///     The budget [08](../../docs/plan/08-asset-pipeline-and-addressables.md) sets is a hundred
    ///     thousand assets in under ten seconds. Ten thousand is measured here — enough to catch an
    ///     algorithmic regression, few enough that the fixture's own file writes do not dominate the
    ///     test — and the bound is loose on purpose: this is here to fail when someone makes the scan
    ///     read whole documents again, not to police a machine's disk.
    /// </summary>
    [Fact]
    public void TenThousandAssetsScanWellInsideTheBudget() {
        using var project = new ProjectFixture();

        for (var index = 0; index < 10_000; index++) {
            project.Add($"Assets/Bulk/{index / 100}/asset{index}.png");
        }

        var database = new AssetDatabase(project.Paths);

        // The first scan mints sidecars for the hundred folders the fixture made. The second is the
        // one worth measuring: a project that is already in order, which is the case that has to be
        // fast because it is the one that happens every time the editor starts.
        database.Scan();
        var report = database.Scan();

        Assert.Equal(10_101, report.Assets);
        Assert.Empty(report.Issues);
        Assert.InRange(report.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }
}
