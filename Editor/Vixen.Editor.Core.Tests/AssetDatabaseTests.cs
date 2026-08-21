// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
    }

    [Fact]
    public void AnIndexOverAProjectInOrderIsNotStale() {
        using var project = new ProjectFixture();
        project.Add("Assets/Textures/hero.png");

        var database = new AssetDatabase(project.Paths);

        // Twice: the first mints the folder's sidecar, and a scan does not trust a stamp it wrote
        // itself until a later scan has seen the file sitting there unchanged.
        database.Scan();
        database.Scan();
        database.Save();

        Assert.False(database.IsStale());

        var reopened = new AssetDatabase(project.Paths);

        Assert.True(reopened.TryLoad());
        Assert.False(reopened.IsStale());
    }

    /// <summary>
    ///     A scan does not trust the stamps of files it wrote during that same scan. A filesystem
    ///     whose write-time resolution is a whole second cannot tell an edit that landed while the
    ///     walk was running from one that landed before it, so those files are read once more — a
    ///     wasted read, which is the affordable half of being wrong.
    /// </summary>
    [Fact]
    public void AScanDoesNotTrustAStampItWroteItself() {
        using var project = new ProjectFixture();
        project.AddWithoutMeta("Assets/hero.png");

        var database = new AssetDatabase(project.Paths);
        var minting = database.Scan();

        Assert.Equal(0, minting.Reused);

        var settling = database.Scan();

        Assert.Equal(0, settling.Reused);
        Assert.Equal(1, database.Scan().Reused);
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

    /// <summary>
    ///     A file dropped in with no sidecar changes no sidecar, so the whole-database freshness check
    ///     — a count of <c>.meta</c> files and their newest write time — could not see it at all. The
    ///     asset was invisible to a warm start until something else happened to change a sidecar.
    /// </summary>
    [Fact]
    public void AnAssetWithNoSidecarMakesTheSavedIndexStale() {
        using var project = new ProjectFixture();
        project.Add("Assets/a.png");

        var database = new AssetDatabase(project.Paths);
        database.Scan();
        database.Scan();
        database.Save();

        Assert.False(database.IsStale());

        project.AddWithoutMeta("Assets/b.png");

        Assert.True(database.IsStale());
    }

    /// <summary>
    ///     The point of the whole exercise: an asset whose sidecar has not moved keeps its entry
    ///     without the file being opened, so what a rescan costs is what changed and not what exists.
    /// </summary>
    [Fact]
    public void OnlyTheAssetsWhoseSidecarsMovedAreRead() {
        using var project = new ProjectFixture();

        for (var index = 0; index < 20; index++) {
            project.Add($"Assets/asset{index}.png");
        }

        var database = new AssetDatabase(project.Paths);
        database.Scan();

        Assert.Equal(20, database.Scan().Reused);

        var minted = AssetId.New();
        project.WriteMetaFor("Assets/asset7.png", minted);

        var report = database.Scan();

        Assert.Equal(20, report.Assets);
        Assert.Equal(1, report.Rescanned);
        Assert.True(database.TryGetByGuid(minted, out var entry));
        Assert.Equal("Assets/asset7.png", entry.Path);
    }

    /// <summary>
    ///     Reuse is a claim about the disk, so it is checked against the disk. An index that names an
    ///     asset which is not there any more does not get to keep it, however fresh it says it is.
    /// </summary>
    [Fact]
    public void AnEntryWhoseAssetIsGoneIsDroppedRatherThanReused() {
        using var project = new ProjectFixture();
        var doomed = project.Add("Assets/a.png");
        project.Add("Assets/b.png");

        var database = new AssetDatabase(project.Paths);
        database.Scan();
        database.Scan();
        database.Save();

        File.Delete(project.Paths.Absolute("Assets/a.png"));

        var loaded = new AssetDatabase(project.Paths);
        Assert.True(loaded.TryLoad());
        var report = loaded.Scan();

        Assert.Equal(1, report.Assets);
        Assert.False(loaded.TryGetByGuid(doomed, out _));
        Assert.Single(report.Issues, issue => issue.Kind == AssetIssueKind.MetaOrphaned);
    }

    /// <summary>
    ///     The case to reason about is a crash halfway through writing the index. It is written beside
    ///     the real file and renamed over it, and it ends with a terminator naming its own entry
    ///     count — so a torn file is refused outright and costs a full rescan, rather than being read
    ///     as a short but plausible index whose missing assets the editor never notices.
    /// </summary>
    [Fact]
    public void AnIndexTruncatedByACrashIsRefusedRatherThanHalfBelieved() {
        using var project = new ProjectFixture();

        for (var index = 0; index < 5; index++) {
            project.Add($"Assets/asset{index}.png");
        }

        var database = new AssetDatabase(project.Paths);
        database.Scan();
        database.Save();

        var lines = File.ReadAllLines(project.Paths.GuidIndexFile);
        File.WriteAllText(project.Paths.GuidIndexFile, string.Join('\n', lines[..4]) + "\n");

        var loaded = new AssetDatabase(project.Paths);

        Assert.False(loaded.TryLoad());
        Assert.Equal(0, loaded.Count);
        Assert.Equal(5, loaded.Scan().Assets);
    }

    /// <summary>An index written by a version that knew a different format is refused, not guessed at.</summary>
    [Fact]
    public void AnIndexWrittenByAnOlderFormatIsRefused() {
        using var project = new ProjectFixture();
        var hero = project.Add("Assets/hero.png");

        Directory.CreateDirectory(project.Paths.Library);

        File.WriteAllText(
            project.Paths.GuidIndexFile,
            $"vixen-guid-index 1\n10\t{DateTime.UtcNow.Ticks}\n{hero}\t1\t0\tTextureImporter\tAssets/hero.png\n"
        );

        Assert.False(new AssetDatabase(project.Paths).TryLoad());
    }

    /// <summary>
    ///     Recorded as a size and an ISO-8601 instant rather than as tick counts, because the file is
    ///     kept as text for a person reading it at four in the morning and a tick count is not
    ///     something a person reads.
    /// </summary>
    [Fact]
    public void TheIndexIsStillTextAPersonCanRead() {
        using var project = new ProjectFixture();
        var hero = project.Add("Assets/hero.png");

        var database = new AssetDatabase(project.Paths);
        database.Scan();
        database.Save();

        var text = File.ReadAllText(project.Paths.GuidIndexFile);
        var entry = text.Split('\n').Single(line => line.Contains("Assets/hero.png", StringComparison.Ordinal));
        var columns = entry.Split('\t');

        Assert.StartsWith("vixen-guid-index 2\nscanned\t", text, StringComparison.Ordinal);
        Assert.Equal(hero.ToString(), columns[0]);
        Assert.Equal("TextureImporter", columns[3]);
        Assert.Equal(new FileInfo(project.Paths.Absolute("Assets/hero.png.meta")).Length, long.Parse(columns[4], CultureInfo.InvariantCulture));
        Assert.EndsWith("Z", columns[5], StringComparison.Ordinal);
        Assert.Equal("Assets/hero.png", columns[6]);
        Assert.Contains("\nend\t1\n", text, StringComparison.Ordinal);
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
    /// <remarks>
    ///     ⚠ <b>A wall-clock threshold under a parallel test run measures the machine as much as the
    ///     code</b>, which is why the assertion that carries the meaning is
    ///     <see cref="ScanReport.Rescanned" /> and not <see cref="ScanReport.Elapsed" />: how many
    ///     sidecars a scan opened is a property of the algorithm and is the same on every machine,
    ///     where the ten seconds is a smoke alarm with the batteries deliberately half out.
    /// </remarks>
    [Fact]
    public void TenThousandAssetsScanWellInsideTheBudget() {
        using var project = new ProjectFixture();

        for (var index = 0; index < 10_000; index++) {
            project.Add($"Assets/Bulk/{index / 100}/asset{index}.png");
        }

        var database = new AssetDatabase(project.Paths);

        // The first scan mints sidecars for the hundred folders the fixture made, and the second is
        // the first that can trust what the first wrote. The third is the one worth measuring: a
        // project already in order, which is the case that happens every time the editor starts.
        database.Scan();
        database.Scan();
        var report = database.Scan();

        Assert.Equal(10_101, report.Assets);
        Assert.Empty(report.Issues);
        Assert.InRange(report.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        // Not one sidecar was opened, because not one of them moved.
        Assert.Equal(0, report.Rescanned);
    }

    /// <summary>
    ///     Cold start with a warm index and one changed file — the case the whole-database freshness
    ///     check turned into a full rebuild of ten thousand entries.
    /// </summary>
    /// <remarks>
    ///     ⚠ The claim is the read count, not the clock. A wall-clock assertion here would measure
    ///     how loaded the machine running the suite is; <see cref="ScanReport.Rescanned" /> is the
    ///     same number on every machine and is what actually changed.
    /// </remarks>
    [Fact]
    public void ColdStartAfterOneFileChangedReadsOneFile() {
        using var project = new ProjectFixture();

        for (var index = 0; index < 10_000; index++) {
            project.Add($"Assets/Bulk/{index / 100}/asset{index}.png");
        }

        var warmed = new AssetDatabase(project.Paths);
        warmed.Scan();
        warmed.Scan();
        warmed.Save();

        var minted = AssetId.New();
        project.WriteMetaFor("Assets/Bulk/42/asset4242.png", minted, importer: "AudioImporter");

        var cold = new AssetDatabase(project.Paths);

        Assert.True(cold.TryLoad());

        var report = cold.Scan();

        Assert.Equal(10_101, report.Assets);
        Assert.Equal(1, report.Rescanned);
        Assert.Empty(report.Issues);
        Assert.True(cold.TryGetByGuid(minted, out var entry));
        Assert.Equal("AudioImporter", entry.ImporterTag);
    }
}
