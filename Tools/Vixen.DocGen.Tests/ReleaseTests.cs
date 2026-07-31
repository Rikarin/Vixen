// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>The version store and the release table — docs/plan/25 § 6.</summary>
public class ReleaseTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-release-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    static DocNode Node(
        string name,
        string signature = "public class {0}",
        DocKind kind = DocKind.Class,
        string? obsolete = null,
        string? baseType = null,
        IReadOnlyList<string>? interfaces = null,
        DocFacets? facets = null,
        IReadOnlyList<DocMember>? members = null
    ) => new() {
        Id = $"T:Fixtures.{name}",
        Kind = kind,
        Name = name,
        QualifiedName = $"Fixtures.{name}",
        Namespace = "Fixtures",
        Assembly = "Fixtures",
        Area = "Core",
        Slug = Slugs.ForType($"T:Fixtures.{name}"),
        Signature = [.. string.Format(System.Globalization.CultureInfo.InvariantCulture, signature, name)
            .Split(' ')
            .SelectMany((word, index) => index == 0
                ? [new DocSpan(word, Keyword(word))]
                : new[] { new DocSpan(" ", "text"), new DocSpan(word, Keyword(word)) })],
        Obsolete = obsolete,
        BaseType = baseType,
        Interfaces = interfaces ?? [],
        Facets = facets,
        Members = members ?? []
    };

    static string Keyword(string word) =>
        word is "public" or "sealed" or "abstract" or "class" or "struct" or "ref" or "enum" or "readonly"
            ? "keyword"
            : "class";

    static DocMember Member(string name, string signature) => new() {
        Id = $"M:Fixtures.Holder.{name}",
        Name = name,
        MemberKind = "method",
        Signature = [new DocSpan(signature, "text")]
    };

    static DocGraph Graph(params DocNode[] nodes) => new() {
        Solution = "Vixen.slnx",
        Configuration = "Release",
        Commit = "abc123",
        ProjectCount = 1,
        GeneratedDocumentCount = 0,
        Nodes = nodes
    };

    static Change Single(IReadOnlyList<Change> changes, ChangeKind kind) =>
        Assert.Single(changes, change => change.Kind == kind);

    // ── The store ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnArchivedGraphReadsBackAsItself() {
        var graph = Graph(Node("Alpha"), Node("Beta", members: [Member("Run", "public void Run()")]));
        var record = History.Write(root, graph, "0.1.0", "2026-08-01");
        var read = History.ReadGraph(root, "0.1.0");

        Assert.NotNull(read);
        Assert.Equal(2, read.Nodes.Count);
        Assert.Equal("abc123", read.Commit);
        Assert.Equal("public void Run()", string.Concat(read.Nodes[1].Members[0].Signature.Select(span => span.Text)));
        Assert.Equal(2, record.Types);
        Assert.Equal(1, record.Members);
        Assert.True(record.Bytes > 0);
    }

    [Fact]
    public void TheIndexListsWhatWasArchived() {
        History.Write(root, Graph(Node("Alpha")), "0.1.0", "2026-08-01");
        History.Write(root, Graph(Node("Alpha"), Node("Beta")), "0.2.0", "2026-09-01");

        var releases = History.Read(root);

        Assert.Equal(["0.1.0", "0.2.0"], releases.Select(release => release.Version));
        Assert.Equal("2026-09-01", releases[1].Date);
    }

    [Fact]
    public void ReArchivingAVersionReplacesItsRow() {
        History.Write(root, Graph(Node("Alpha")), "0.1.0", "2026-08-01");
        History.Write(root, Graph(Node("Alpha"), Node("Beta")), "0.1.0", "2026-08-02");

        var release = Assert.Single(History.Read(root));

        Assert.Equal("2026-08-02", release.Date);
        Assert.Equal(2, release.Types);
    }

    /// <summary>
    ///     Ordinal order would put 0.10.0 before 0.2.0 and a release before its own release candidate,
    ///     and the second of those decides what a release is diffed against.
    /// </summary>
    [Fact]
    public void VersionsAreOrderedTheWayAReleaseTrainIs() {
        foreach (var version in new[] { "0.10.0", "0.2.0", "1.0.0", "1.0.0-rc.1" }) {
            History.Write(root, Graph(Node("Alpha")), version, "2026-08-01");
        }

        Assert.Equal(
            ["0.2.0", "0.10.0", "1.0.0-rc.1", "1.0.0"],
            History.Read(root).Select(release => release.Version));
    }

    [Fact]
    public void ThePreviousReleaseIsTheNewestOneBeforeIt() {
        foreach (var version in new[] { "0.1.0", "0.2.0", "0.10.0" }) {
            History.Write(root, Graph(Node("Alpha")), version, "2026-08-01");
        }

        var releases = History.Read(root);

        Assert.Equal("0.2.0", History.Previous(releases, "0.10.0")?.Version);
        Assert.Null(History.Previous(releases, "0.1.0"));
    }

    // ── Added, removed, deprecated ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnAddedTypeIsAdded() {
        var changes = ReleaseDiff.Between(Graph(Node("Alpha")), Graph(Node("Alpha"), Node("Beta")));

        Assert.Equal("T:Fixtures.Beta", Single(changes, ChangeKind.Added).Id);
    }

    [Fact]
    public void ARemovedTypeIsBreaking() {
        var changes = ReleaseDiff.Between(Graph(Node("Alpha"), Node("Beta")), Graph(Node("Alpha")));
        var removed = Single(changes, ChangeKind.Removed);

        Assert.Equal("T:Fixtures.Beta", removed.Id);
        Assert.True(removed.IsBreaking);
    }

    /// <summary>Forty rows saying a method is gone, under one saying the type is, is a table nobody finishes.</summary>
    [Fact]
    public void ARemovedTypeDoesNotAlsoRemoveItsMembers() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha"), Node("Holder", members: [Member("Run", "public void Run()")])),
            Graph(Node("Alpha")));

        Assert.Equal("T:Fixtures.Holder", Single(changes, ChangeKind.Removed).Id);
    }

    [Fact]
    public void AnAddedTypeDoesNotAlsoAddItsMembers() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha")),
            Graph(Node("Alpha"), Node("Holder", members: [Member("Run", "public void Run()")])));

        Assert.Equal("T:Fixtures.Holder", Single(changes, ChangeKind.Added).Id);
    }

    [Fact]
    public void AMemberAddedToAnExistingTypeIsItsOwnRow() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Holder")),
            Graph(Node("Holder", members: [Member("Run", "public void Run()")])));

        Assert.Equal("M:Fixtures.Holder.Run", Single(changes, ChangeKind.Added).Id);
    }

    [Fact]
    public void GainingObsoleteIsDeprecation() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha")),
            Graph(Node("Alpha", obsolete: "Use Beta instead.")));

        var deprecated = Single(changes, ChangeKind.Deprecated);

        Assert.Equal("Use Beta instead.", deprecated.Note);
        Assert.False(deprecated.IsBreaking);
    }

    // ── Breaking — signature and shape ──────────────────────────────────────────────────────────

    [Fact]
    public void AChangedMemberSignatureIsABreak() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Holder", members: [Member("Run", "public void Run()")])),
            Graph(Node("Holder", members: [Member("Run", "public void Run(int frames)")])));

        var broken = Single(changes, ChangeKind.SignatureBreak);

        Assert.Equal("public void Run()", broken.Before);
        Assert.Equal("public void Run(int frames)", broken.After);
    }

    [Theory]
    [InlineData("public class {0}", "public sealed class {0}", "sealed")]
    [InlineData("public class {0}", "public abstract class {0}", "abstract")]
    [InlineData("public struct {0}", "public ref struct {0}", "ref struct")]
    public void AShapeChangeSaysWhatItCosts(string before, string after, string expected) {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha", before)),
            Graph(Node("Alpha", after)));

        Assert.Contains(expected, Single(changes, ChangeKind.ShapeBreak).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ADroppedInterfaceIsAShapeChange() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha", interfaces: ["T:Vixen.Ecs.ISystem", "T:System.IDisposable"])),
            Graph(Node("Alpha", interfaces: ["T:System.IDisposable"])));

        Assert.Contains("ISystem", Single(changes, ChangeKind.ShapeBreak).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedBaseTypeIsAShapeChange() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha", baseType: "T:Vixen.Ecs.SystemBase")),
            Graph(Node("Alpha", baseType: "T:Vixen.Ecs.Behavior")));

        Assert.Contains("base type", Single(changes, ChangeKind.ShapeBreak).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ANarrowedEnumIsAShapeChange() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Flags", "public enum {0} : int", DocKind.Enum)),
            Graph(Node("Flags", "public enum {0} : byte", DocKind.Enum)));

        Assert.Contains("underlying type", Single(changes, ChangeKind.ShapeBreak).Note, StringComparison.Ordinal);
    }

    // ── Breaking — engine ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The row no generic tool would produce: the signature is identical and every scene saved
    ///     with the old layout loads wrong.
    /// </summary>
    [Fact]
    public void AComponentThatChangedSizeIsBreakingWithAnIdenticalSignature() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Position", kind: DocKind.Component, facets: new DocFacets { SizeBytes = 8 })),
            Graph(Node("Position", kind: DocKind.Component, facets: new DocFacets { SizeBytes = 12 })));

        var broken = Single(changes, ChangeKind.EngineBreak);

        Assert.Contains("8 b → 12 b", broken.Note, StringComparison.Ordinal);
        Assert.True(broken.IsBreaking);
        Assert.DoesNotContain(changes, change => change.Kind == ChangeKind.SignatureBreak);
    }

    [Fact]
    public void ASystemThatChangedPhaseIsBreaking() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Movement", kind: DocKind.System, facets: new DocFacets { Phase = "Update" })),
            Graph(Node("Movement", kind: DocKind.System, facets: new DocFacets { Phase = "LateUpdate" })));

        Assert.Contains("Update → LateUpdate", Single(changes, ChangeKind.EngineBreak).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AShaderThatChangedItsDescriptorLayoutIsBreaking() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Lit", kind: DocKind.Shader, facets: new DocFacets { DescriptorSets = 2 })),
            Graph(Node("Lit", kind: DocKind.Shader, facets: new DocFacets { DescriptorSets = 3 })));

        Assert.Contains("recompiled", Single(changes, ChangeKind.EngineBreak).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ASystemThatChangedItsDeclaredAccessIsBreaking() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Movement", kind: DocKind.System, facets: new DocFacets { Reads = ["T:Fixtures.Position"] })),
            Graph(Node("Movement", kind: DocKind.System, facets: new DocFacets { Writes = ["T:Fixtures.Position"] })));

        Assert.Contains("declared access", Single(changes, ChangeKind.EngineBreak).Note, StringComparison.Ordinal);
    }

    // ── Breaking — behaviour, and the rendering ─────────────────────────────────────────────────

    [Fact]
    public void ASemanticBreakComesFromAGuidePage() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha")),
            Graph(Node("Alpha")),
            [("rendering/colour", "Materials now default to linear space.")]);

        var broken = Single(changes, ChangeKind.SemanticBreak);

        Assert.Equal("Materials now default to linear space.", broken.Display);
        Assert.True(broken.IsBreaking);
    }

    [Fact]
    public void TheTableCountsWhatItShows() {
        var changes = ReleaseDiff.Between(
            Graph(Node("Alpha"), Node("Gone")),
            Graph(Node("Alpha"), Node("New")));

        var markdown = ReleaseDiff.Markdown("0.2.0", "0.1.0", "2026-09-01", changes);

        Assert.Contains("## 0.2.0 — 2026-09-01", markdown, StringComparison.Ordinal);
        Assert.Contains("**1 added**", markdown, StringComparison.Ordinal);
        Assert.Contains("**1 breaking**", markdown, StringComparison.Ordinal);
        Assert.Contains("### Removed (1)", markdown, StringComparison.Ordinal);
        Assert.Contains("`Fixtures.Gone`", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And does not also say "no public API changed", which is true of it and reads as a mistake.
    /// </summary>
    [Fact]
    public void TheFirstReleaseSaysThatIsWhatItIs() {
        var markdown = ReleaseDiff.Markdown("0.1.0", null, "2026-08-01", []);

        Assert.Contains("The first release", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("No public API changed", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithNothingToSaySaysThat() {
        var markdown = ReleaseDiff.Markdown("0.1.1", "0.1.0", "2026-08-08", []);

        Assert.Contains("No public API changed.", markdown, StringComparison.Ordinal);
    }

    // ── The store, the changelog and the site's copy ────────────────────────────────────────────

    [Fact]
    public void AReleaseWritesItsTableIntoTheStoreTheChangelogAndTheOutput() {
        var output = Path.Combine(root, "artifacts", "docs");
        var record = History.Write(root, Graph(Node("Alpha")), "0.2.0", "2026-09-01");
        var changes = ReleaseDiff.Between(Graph(Node("Alpha"), Node("Gone")), Graph(Node("Alpha")));

        Releases.Write(root, output, record, "0.1.0", changes);

        Assert.True(File.Exists(Path.Combine(root, "docs", "api-history", "0.2.0", "changes.json")));
        Assert.True(File.Exists(Path.Combine(output, "releases", "0.2.0.json")));
        Assert.True(File.Exists(Path.Combine(output, "releases", "index.json")));

        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));

        Assert.Contains("## 0.2.0 — 2026-09-01", changelog, StringComparison.Ordinal);
        Assert.Contains("Fixtures.Gone", changelog, StringComparison.Ordinal);
    }

    /// <summary>Re-running a release must not leave two tables for one tag.</summary>
    [Fact]
    public void ReReleasingRewritesTheChangelogSectionRatherThanAppendingIt() {
        var output = Path.Combine(root, "artifacts", "docs");
        var record = History.Write(root, Graph(Node("Alpha")), "0.2.0", "2026-09-01");

        Releases.Write(root, output, record, "0.1.0", ReleaseDiff.Between(Graph(Node("Alpha"), Node("Gone")), Graph(Node("Alpha"))));
        Releases.Write(root, output, record, "0.1.0", ReleaseDiff.Between(Graph(Node("Alpha")), Graph(Node("Alpha"))));

        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));

        Assert.Equal(1, changelog.Split("## 0.2.0").Length - 1);
        Assert.Contains("No public API changed.", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestReleaseIsAtTheTopOfTheChangelog() {
        var output = Path.Combine(root, "artifacts", "docs");
        var first = History.Write(root, Graph(Node("Alpha")), "0.1.0", "2026-08-01");

        Releases.Write(root, output, first, null, []);

        var second = History.Write(root, Graph(Node("Alpha"), Node("Beta")), "0.2.0", "2026-09-01");

        Releases.Write(root, output, second, "0.1.0", ReleaseDiff.Between(Graph(Node("Alpha")), Graph(Node("Alpha"), Node("Beta"))));

        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));

        Assert.True(changelog.IndexOf("## 0.2.0", StringComparison.Ordinal)
            < changelog.IndexOf("## 0.1.0", StringComparison.Ordinal));
    }
}
