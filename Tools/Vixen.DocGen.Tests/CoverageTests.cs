// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>The coverage gate — docs/plan/25 § Part 5, and the three ways it says no.</summary>
public class CoverageTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-coverage-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    static DocNode Node(string name, string? docs = null) => new() {
        Id = $"T:Fixtures.{name}",
        Kind = DocKind.Class,
        Name = name,
        QualifiedName = $"Fixtures.{name}",
        Namespace = "Fixtures",
        Assembly = "Fixtures",
        Area = "Core",
        Slug = Slugs.ForType($"T:Fixtures.{name}"),
        Signature = [new DocSpan($"public sealed class {name}", "text")],
        Docs = docs
    };

    void WriteExemptions(string contents) {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "DocsExempt.txt"), contents);
    }

    // ── The file ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommentsAndBlankLinesAreNotEntries() {
        WriteExemptions("# a comment\n\nT:Fixtures.Alpha sweep-pending\n");

        var (entries, errors) = Coverage.Read(root);

        Assert.Empty(errors);

        var entry = Assert.Single(entries);

        Assert.Equal("T:Fixtures.Alpha", entry.Id);
        Assert.Equal("sweep-pending", entry.Reason);
        Assert.Equal(3, entry.Line);
    }

    /// <summary>
    ///     The reason is the whole point of the file: a line nobody can review is a mute button.
    /// </summary>
    [Fact]
    public void AnEntryWithNoReasonIsReported() {
        WriteExemptions("T:Fixtures.Alpha\n");

        var (entries, errors) = Coverage.Read(root);

        Assert.Empty(entries);
        Assert.Contains(errors, error => error.Contains("gives no reason", StringComparison.Ordinal));
    }

    [Fact]
    public void ADuplicatedEntryIsReported() {
        WriteExemptions("T:Fixtures.Alpha sweep-pending\nT:Fixtures.Alpha still pending\n");

        Assert.Contains(Coverage.Read(root).Errors, error => error.Contains("twice", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReasonKeepsItsWholeSentence() {
        WriteExemptions("T:Fixtures.Alpha the store is not public yet\n");

        Assert.Equal("the store is not public yet", Coverage.Read(root).Entries[0].Reason);
    }

    // ── The gate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypeWithNeitherPageNorExemptionFails() {
        var problems = Coverage.Check([Node("Alpha")], []);

        Assert.Contains(problems, problem => problem.Contains("T:Fixtures.Alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void ATypeWithAPagePasses() {
        Assert.Empty(Coverage.Check([Node("Alpha", docs: "ecs/alpha")], []));
    }

    [Fact]
    public void ATypeWithAnExemptionPasses() {
        Assert.Empty(Coverage.Check([Node("Alpha")], [new Exemption("T:Fixtures.Alpha", "sweep-pending", 7)]));
    }

    /// <summary>
    ///     Without this the file never empties: the page lands, the line stays, and the next reader
    ///     of the file cannot tell which of three thousand lines still means anything.
    /// </summary>
    [Fact]
    public void AnExemptionForATypeThatNowHasAPageFails() {
        var problems = Coverage.Check(
            [Node("Alpha", docs: "ecs/alpha")],
            [new Exemption("T:Fixtures.Alpha", "sweep-pending", 7)]);

        Assert.Contains(problems, problem => problem.Contains("delete this line", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExemptionForATypeTheGraphDoesNotHaveFails() {
        var problems = Coverage.Check([Node("Alpha")], [
            new Exemption("T:Fixtures.Alpha", "sweep-pending", 7),
            new Exemption("T:Fixtures.Renamed", "sweep-pending", 8)
        ]);

        Assert.Contains(problems, problem => problem.Contains("T:Fixtures.Renamed", StringComparison.Ordinal));
    }

    /// <summary>Three thousand failures is not a report, so the tail is counted rather than listed.</summary>
    [Fact]
    public void TheUncoveredListIsCappedAndTheRestCounted() {
        var nodes = Enumerable.Range(0, 30).Select(index => Node("Type" + index)).ToList();
        var problems = Coverage.Check(nodes, [], limit: 5);

        Assert.Equal(6, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("…and 25 more", StringComparison.Ordinal));
    }

    // ── The update ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFileHoldsEveryUndocumentedTypeAndNothingElse() {
        Coverage.Write(root, [Node("Beta"), Node("Alpha"), Node("Gamma", docs: "ecs/gamma")]);

        var (entries, errors) = Coverage.Read(root);

        Assert.Empty(errors);
        Assert.Equal(["T:Fixtures.Alpha", "T:Fixtures.Beta"], entries.Select(entry => entry.Id));
        Assert.All(entries, entry => Assert.Equal(Coverage.SeedReason, entry.Reason));
    }

    /// <summary>A file the gate would then fail on would be a seed nobody could commit.</summary>
    [Fact]
    public void TheFileSatisfiesTheGateItWrites() {
        var nodes = new[] { Node("Alpha"), Node("Beta"), Node("Gamma", docs: "ecs/gamma") };

        Coverage.Write(root, nodes);

        Assert.Empty(Coverage.Check(nodes, Coverage.Read(root).Entries));
    }

    /// <summary>
    ///     The whole difference between updating and re-seeding: the sweep replaces `sweep-pending`
    ///     with reasons somebody wrote, and flattening those back would delete the only review anybody
    ///     had done.
    /// </summary>
    [Fact]
    public void AnExistingReasonSurvivesTheUpdate() {
        WriteExemptions("T:Fixtures.Alpha the store is not public yet\n");
        Coverage.Write(root, [Node("Alpha"), Node("Beta")]);

        var entries = Coverage.Read(root).Entries;

        Assert.Equal("the store is not public yet", entries.Single(entry => entry.Id.EndsWith("Alpha", StringComparison.Ordinal)).Reason);
        Assert.Equal(Coverage.SeedReason, entries.Single(entry => entry.Id.EndsWith("Beta", StringComparison.Ordinal)).Reason);
    }

    [Fact]
    public void TheUpdateCountsWhatItDid() {
        WriteExemptions("T:Fixtures.Alpha sweep-pending\nT:Fixtures.Renamed sweep-pending\nT:Fixtures.Gamma sweep-pending\n");

        var summary = Coverage.Write(root, [Node("Alpha"), Node("Beta"), Node("Gamma", docs: "ecs/gamma")]);

        Assert.Equal(1, summary.Added);       // Beta
        Assert.Equal(1, summary.Documented);  // Gamma has a page now
        Assert.Equal(1, summary.Gone);        // Renamed is not in the graph
        Assert.Equal(2, summary.Total);
    }

    /// <summary>On an empty tree it is the seed, which is how the file came to exist at all.</summary>
    [Fact]
    public void WithNoFileTheUpdateIsTheSeed() {
        var summary = Coverage.Write(root, [Node("Alpha"), Node("Beta")]);

        Assert.Equal(2, summary.Added);
        Assert.Equal(2, summary.Total);
    }
}
