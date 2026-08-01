// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     docs/plan/25 § 3.4 — code reaches the site already tokenised, so the browser ships no
///     highlighter and the prerendered HTML is coloured for readers without JavaScript.
/// </summary>
public class SignatureTests {
    static IReadOnlyList<DocSpan> Read(string source, string metadataName) {
        var compilation = Fixture.Compile(source);
        var links = new SourceLinks(Path.GetTempPath(), "https://github.com/rikarin/Vixen", "abc123");

        return new SymbolReader(links)
            .Read(compilation.Assembly, "Core", isPackable: true)
            .Single(node => node.QualifiedName == metadataName)
            .Signature;
    }

    [Fact]
    public void ASignatureArrivesAsClassifiedRunsRatherThanAString() {
        var spans = Read("namespace Fixtures { public sealed class World; }", "Fixtures.World");

        Assert.Contains(spans, span => span is { Kind: "keyword", Text: "public" });
        Assert.Contains(spans, span => span is { Kind: "keyword", Text: "sealed" });
        Assert.Contains(spans, span => span is { Kind: "class", Text: "World" });
    }

    /// <summary>The runs joined back are the signature, so nothing is lost by classifying it.</summary>
    [Fact]
    public void TheRunsReadBackAsTheSignature() {
        var spans = Read(
            """
            namespace Fixtures {
                public sealed class Bag {
                    public int Count { get; }
                }
            }
            """, "Fixtures.Bag");

        Assert.Equal("public sealed class Bag", Signatures.Text(spans));
    }

    [Fact]
    public void ParametersAndTheirTypesAreDistinguished() {
        var compilation = Fixture.Compile(
            """
            namespace Fixtures {
                public sealed class Bag {
                    public void Add(int value) { }
                }
            }
            """);

        var links = new SourceLinks(Path.GetTempPath(), "https://github.com/rikarin/Vixen", "abc123");

        var member = new SymbolReader(links)
            .Read(compilation.Assembly, "Core", isPackable: true)
            .Single(node => node.QualifiedName == "Fixtures.Bag")
            .Members
            .Single(candidate => candidate.Name == "Add");

        Assert.Contains(member.Signature, span => span is { Kind: "keyword", Text: "int" });
        Assert.Contains(member.Signature, span => span is { Kind: "parameter", Text: "value" });
        Assert.Contains(member.Signature, span => span is { Kind: "method", Text: "Add" });
    }

    [Fact]
    public void TypeParametersAreTheirOwnKind() {
        var spans = Read("namespace Fixtures { public sealed class Pool<TItem>; }", "Fixtures.Pool<TItem>");

        Assert.Contains(spans, span => span is { Kind: "type-parameter", Text: "TItem" });
        Assert.Contains(spans, span => span is { Kind: "punctuation" });
    }

    /// <summary>
    ///     `ToDisplayParts` emits one part per symbol, so a nested generic ends in two punctuation
    ///     parts that render as one run. Merging them keeps the JSON — and the DOM — smaller.
    /// </summary>
    [Fact]
    public void AdjacentRunsOfTheSameKindAreOneRun() {
        var spans = Read(
            "namespace Fixtures { public sealed class Nested<T> { } }",
            "Fixtures.Nested<T>");

        for (var index = 1; index < spans.Count; index++) {
            Assert.NotEqual(spans[index - 1].Kind, spans[index].Kind);
        }
    }

    // ── The link — § 8.3 ────────────────────────────────────────────────────────────────────────

    static IReadOnlyList<DocSpan> Member(string source, string type, string member) {
        var compilation = Fixture.Compile(source);
        var links = new SourceLinks(Path.GetTempPath(), "https://github.com/rikarin/Vixen", "abc123");

        return new SymbolReader(links)
            .Read(compilation.Assembly, "Core", isPackable: true)
            .Single(node => node.QualifiedName == type)
            .Members.Single(candidate => candidate.Name == member)
            .Signature;
    }

    /// <summary>
    ///     The run that names a type carries its id, which is what makes a parameter type a link
    ///     rather than a coloured word.
    /// </summary>
    [Fact]
    public void ARunThatNamesATypeCarriesItsId() {
        var spans = Member(
            """
            namespace Fixtures {
                public sealed class Chunk { }

                public sealed class World {
                    public Chunk Take(Chunk source) => source;
                }
            }
            """, "Fixtures.World", "Take");

        Assert.All(
            spans.Where(span => span.Text == "Chunk"),
            span => Assert.Equal("T:Fixtures.Chunk", span.Id));
    }

    /// <summary>
    ///     Punctuation and whitespace name nothing, so they link to nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ A keyword is not in that list, and the reason is worth keeping: `int` is a keyword
    ///     <em>and</em> names <c>System.Int32</c>, so the run carries that id. It becomes a link only
    ///     if the graph has a page for it — which is the graph pass's decision, not this one's, and
    ///     is why the filter is where it is.
    /// </remarks>
    [Fact]
    public void ARunThatNamesNoTypeCarriesNoId() {
        var spans = Member(
            "namespace Fixtures { public sealed class World { public int Count(int value) => value; } }",
            "Fixtures.World",
            "Count");

        Assert.All(spans.Where(span => span.Kind is "punctuation" or "space"), span => Assert.Null(span.Id));
        Assert.Contains(spans, span => span is { Text: "int", Id: "T:System.Int32" });
    }

    /// <summary>
    ///     Two type names side by side are two links; merging them by kind alone would make one of
    ///     them point at the other's page.
    /// </summary>
    [Fact]
    public void RunsNamingDifferentTypesAreNotMerged() {
        var spans = Member(
            """
            namespace Fixtures {
                public sealed class Left { }
                public sealed class Right { }

                public sealed class World {
                    public Left Swap(Right value) => null!;
                }
            }
            """, "Fixtures.World", "Swap");

        Assert.Contains(spans, span => span is { Text: "Left", Id: "T:Fixtures.Left" });
        Assert.Contains(spans, span => span is { Text: "Right", Id: "T:Fixtures.Right" });
    }
}
