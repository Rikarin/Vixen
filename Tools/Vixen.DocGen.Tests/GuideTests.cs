// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>The written half — docs/plan/25 § 4, and the contract that makes it a build failure.</summary>
public class GuideTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-guide-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    const string Front =
        """
        ---
        title: Entity queries
        slug: ecs/queries
        kind: guide
        area: ECS
        summary: Iterating the entities that have a given set of components.
        api: [T:Vixen.Ecs.World]
        tags: [ecs, iteration]
        status: stable
        ---
        """;

    const string Body =
        """

        ## What it is

        A description of a set of components.

        ## What it is for

        Reading component data in bulk.

        ## Using it

        Describe the set, then iterate.

        ## Examples

        Nothing yet.

        ## See also

        - Nothing yet.
        """;

    void Write(string relativePath, string contents) {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    (IReadOnlyList<GuidePage> Pages, IReadOnlyList<string> Errors) Read() =>
        GuideReader.Read(root, new SourceLinks(root, "https://github.com/rikarin/Vixen", "abc123"));

    // ── Front matter ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWholePageIsRead() {
        Write("docs/guide/ecs/queries.md", Front + Body);

        var (pages, errors) = Read();

        Assert.Empty(errors);

        var page = Assert.Single(pages);

        Assert.Equal("ecs/queries", page.Front.Slug);
        Assert.Equal(["T:Vixen.Ecs.World"], page.Front.Api);
        Assert.Equal(["ecs", "iteration"], page.Front.Tags);
        Assert.Equal(5, page.Headings.Count);
        Assert.Equal("what-it-is", page.Headings[0].Id);
    }

    /// <summary>
    ///     A page with no `api:` is prose the graph cannot join to a symbol, and a page with no
    ///     `summary` has no search result — both are what the schema exists to make loud.
    /// </summary>
    [Theory]
    [InlineData("summary: Iterating the entities that have a given set of components.\n", "summary")]
    [InlineData("api: [T:Vixen.Ecs.World]\n", "api")]
    [InlineData("title: Entity queries\n", "title")]
    public void AMissingRequiredFieldIsReported(string line, string field) {
        Write("docs/guide/ecs/queries.md", (Front + Body).Replace(line, string.Empty, StringComparison.Ordinal));

        var (pages, errors) = Read();

        Assert.Empty(pages);
        Assert.Contains(errors, error => error.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownKindIsReported() {
        Write("docs/guide/ecs/queries.md", (Front + Body).Replace("kind: guide", "kind: essay", StringComparison.Ordinal));

        Assert.Contains(Read().Errors, error => error.Contains("essay", StringComparison.Ordinal));
    }

    // ── The contract ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMissingContractHeadingIsReported() {
        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("## What it is for\n\nReading component data in bulk.\n", string.Empty, StringComparison.Ordinal));

        Assert.Contains(Read().Errors, error => error.Contains("What it is for", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyContractSectionIsReported() {
        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("Reading component data in bulk.", string.Empty, StringComparison.Ordinal));

        Assert.Contains(Read().Errors, error => error.Contains("nothing under it", StringComparison.Ordinal));
    }

    [Fact]
    public void HeadingsOutOfOrderAreReported() {
        Write("docs/guide/ecs/queries.md",
            Front + """

            ## What it is for

            Reading component data in bulk.

            ## What it is

            A description of a set of components.

            ## Using it

            Describe the set.

            ## Examples

            Nothing yet.

            ## See also

            - Nothing yet.
            """);

        Assert.Contains(Read().Errors, error => error.Contains("out of order", StringComparison.Ordinal));
    }

    // ── Snippets ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASnippetIsReplacedByTheRegionItNames() {
        Write("Samples/Thing/Program.cs",
            """
            public static class Program {
                public static void Main() {
                    // #region docs:query
                    var world = 1;
                    // #endregion
                }
            }
            """);

        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("Nothing yet.\n\n## See also",
                "{{ snippet Samples/Thing/Program.cs#docs:query }}\n\n## See also", StringComparison.Ordinal));

        var page = Assert.Single(Read().Pages);

        Assert.Contains("var world = 1;", page.Body, StringComparison.Ordinal);
        Assert.Contains("```csharp snippet title=Samples/Thing/Program.cs", page.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ASnippetNamingAMissingRegionIsReported() {
        Write("Samples/Thing/Program.cs", "public static class Program { }");

        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("Nothing yet.\n\n## See also",
                "{{ snippet Samples/Thing/Program.cs#docs:absent }}\n\n## See also", StringComparison.Ordinal));

        Assert.Contains(Read().Errors, error => error.Contains("docs:absent", StringComparison.Ordinal));
    }

    // ── Fences ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ A reason is a sentence. Split on spaces it becomes its first word, which is a reason
    ///     nobody can act on — and § 4.3 prints these so the exemptions stay visible.
    /// </summary>
    [Fact]
    public void ANoCompileReasonSurvivesItsSpaces() {
        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("Nothing yet.\n\n## See also",
                "```csharp no-compile=\"the managed store is not public yet\"\nvar x = 1;\n```\n\n## See also",
                StringComparison.Ordinal));

        var page = Assert.Single(Read().Pages);
        var example = Assert.Single(page.Examples);

        Assert.Equal("the managed store is not public yet", example.Reason);
    }

    [Fact]
    public void ACSharpFenceThatSaysNeitherIsReported() {
        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("Nothing yet.\n\n## See also",
                "```csharp\nvar x = 1;\n```\n\n## See also", StringComparison.Ordinal));

        Assert.Contains(Read().Errors, error => error.Contains("neither `compile` nor", StringComparison.Ordinal));
    }

    [Fact]
    public void ACompileFenceIsMarkedForTheBuild() {
        Write("docs/guide/ecs/queries.md",
            (Front + Body).Replace("Nothing yet.\n\n## See also",
                "```csharp compile\npublic sealed class Thing;\n```\n\n## See also", StringComparison.Ordinal));

        var example = Assert.Single(Assert.Single(Read().Pages).Examples);

        Assert.True(example.Compile);
        Assert.False(example.Fragment);
    }

    [Fact]
    public void TwoPagesCannotClaimOneSlug() {
        Write("docs/guide/ecs/queries.md", Front + Body);
        Write("docs/guide/ecs/other.md", Front + Body);

        Assert.Contains(Read().Errors, error => error.Contains("claim the slug", StringComparison.Ordinal));
    }
}
