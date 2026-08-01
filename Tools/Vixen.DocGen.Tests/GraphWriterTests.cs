// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>The two tiers, and the two things that would silently lose a page.</summary>
public class GraphWriterTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-docgen-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    static DocNode Node(string name, string @namespace = "Fixtures", int members = 0) => new() {
        Id = $"T:{@namespace}.{name}",
        Kind = DocKind.Class,
        Name = name,
        QualifiedName = $"{@namespace}.{name}",
        Namespace = @namespace,
        Assembly = "Fixtures",
        Area = "Core",
        Slug = Slugs.ForType($"T:{@namespace}.{name}"),
        Signature = [new DocSpan($"public sealed class {name}", "text")],
        Summary = new string('x', 512),
        Members = [
            .. Enumerable.Range(0, members).Select(index => new DocMember {
                Id = $"M:{@namespace}.{name}.Method{index}",
                Name = "Method" + index,
                MemberKind = "method",
                Signature = [new DocSpan($"public void Method{index}()", "text")],
                Summary = new string('y', 512)
            })
        ]
    };

    static DocGraph Graph(params DocNode[] nodes) => new() {
        Solution = "Vixen.slnx",
        Configuration = "Release",
        ProjectCount = 1,
        GeneratedDocumentCount = 0,
        Nodes = nodes
    };

    [Fact]
    public void TheIndexCarriesEveryNodeAndThePagesCarryTheDetail() {
        var written = new GraphWriter().Write(Graph(Node("Alpha"), Node("Beta")), directory);

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "graph.json")));

        Assert.Equal(2, index.RootElement.GetProperty("Nodes").GetArrayLength());
        Assert.Equal("Release", index.RootElement.GetProperty("Configuration").GetString());
        Assert.Equal(1, written.Chunks);
        Assert.True(File.Exists(Path.Combine(directory, "pages", "fixtures.json")));
    }

    /// <summary>
    ///     A classified run is two strings, not an object with two names. There are 29 000 members
    ///     in the graph, and `"Text"`/`"Kind"` repeated per run is 4 MB of the 30 MB page tier.
    /// </summary>
    [Fact]
    public void ClassifiedRunsAreWrittenAsPairs() {
        var node = Node("Alpha") with {
            Signature = [new DocSpan("public", "keyword"), new DocSpan(" Alpha", "class")]
        };

        new GraphWriter().Write(Graph(node), directory);

        var page = File.ReadAllText(Path.Combine(directory, "pages", "fixtures.json"));

        Assert.Contains("[\"public\",\"keyword\"]", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Text\":", page, StringComparison.Ordinal);
    }

    /// <summary>Kinds are kebab-cased in the JSON because that is what the site filters on.</summary>
    [Fact]
    public void KindsAreWrittenAsTheirSlugs() {
        var node = Node("Velocity") with { Kind = DocKind.SceneComponent };

        new GraphWriter().Write(Graph(node), directory);

        var index = File.ReadAllText(Path.Combine(directory, "graph.json"));

        Assert.Contains("\"Kind\":\"scene-component\"", index, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Per namespace, because per type the median chunk is 428 bytes — but the largest namespace
    ///     is 92 kB in the index tier alone, so a group past the budget splits.
    /// </summary>
    [Fact]
    public void ANamespacePastTheBudgetIsSplit() {
        var nodes = Enumerable.Range(0, 8).Select(index => Node("Type" + index, members: 4)).ToArray();
        var written = new GraphWriter(chunkBudgetBytes: 4096).Write(Graph(nodes), directory);

        Assert.True(written.Chunks > 1, "the budget should have split this namespace");
        Assert.Equal(1, written.SplitChunks);
        Assert.True(File.Exists(Path.Combine(directory, "pages", "fixtures.1.json")));
    }

    [Fact]
    public void ASmallNamespaceIsOneChunk() {
        var written = new GraphWriter(chunkBudgetBytes: 256 * 1024).Write(Graph(Node("Alpha")), directory);

        Assert.Equal(1, written.Chunks);
        Assert.Equal(0, written.SplitChunks);
    }

    /// <summary>
    ///     Two nodes whose slugs collide would serve one page and hide the other, and the emitter has
    ///     to say so rather than pick — lowercasing makes this reachable from ids that never collided.
    /// </summary>
    [Fact]
    public void CollidingSlugsAreAnError() {
        var first = Node("IPin");
        var second = Node("IPIN");

        var failure = Assert.Throws<DocGenException>(() =>
            new GraphWriter().Write(Graph(first, second), directory));

        Assert.Contains("slug collisions", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A stale page from a previous run is a page the site still serves.</summary>
    [Fact]
    public void PagesFromAPreviousRunAreRemoved() {
        new GraphWriter().Write(Graph(Node("Alpha", "Old")), directory);

        Assert.True(File.Exists(Path.Combine(directory, "pages", "old.json")));

        new GraphWriter().Write(Graph(Node("Alpha", "New")), directory);

        Assert.False(File.Exists(Path.Combine(directory, "pages", "old.json")));
        Assert.True(File.Exists(Path.Combine(directory, "pages", "new.json")));
    }

    /// <summary>
    ///     A run with a link is a triple and a run without one is a pair — and both read back, which
    ///     is what an archived release depends on.
    /// </summary>
    [Fact]
    public void ALinkedRunRoundTripsAndAnUnlinkedOneStaysAPair() {
        var node = Node("Alpha") with {
            Signature = [
                new DocSpan("public", "keyword"),
                new DocSpan(" ", "space"),
                new DocSpan("World", "class", "T:Fixtures.World")
            ]
        };

        new GraphWriter().Write(Graph(node), directory);

        var written = File.ReadAllText(Path.Combine(directory, "pages", "fixtures.json"));

        Assert.Contains("""["public","keyword"]""", written, StringComparison.Ordinal);
        Assert.Contains("""["World","class","T:Fixtures.World"]""", written, StringComparison.Ordinal);

        var read = JsonSerializer.Deserialize<DocNode[]>(written, GraphWriter.Options);

        Assert.Equal("T:Fixtures.World", read![0].Signature[2].Id);
        Assert.Null(read[0].Signature[0].Id);
    }
}
