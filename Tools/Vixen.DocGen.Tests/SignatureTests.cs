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
}
