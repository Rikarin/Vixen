// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>docs/plan/25 § 3.3 — the prose half, read off the symbol rather than an XML sidecar.</summary>
public class DocumentationCommentTests {
    static DocumentationComment Read(string source, string metadataName) =>
        DocumentationComment.For(Fixture.Compile(source).Type(metadataName));

    [Fact]
    public void SummaryIsRead() {
        var docs = Read(
            """
            /// <summary>An entity's position in the world.</summary>
            public struct Position { public float X; }
            """, "Position");

        Assert.Equal("An entity's position in the world.", docs.Summary);
    }

    /// <summary>
    ///     A doc comment is written as `///` lines whose indentation is an artefact of where the
    ///     declaration sits. Carrying that into JSON puts it into the search index and onto the page.
    /// </summary>
    [Fact]
    public void WhitespaceFromTheCommentSyntaxIsCollapsed() {
        var docs = Read(
            """
            /// <summary>
            ///     An entity's position
            ///     in the world.
            /// </summary>
            public struct Position { public float X; }
            """, "Position");

        Assert.Equal("An entity's position in the world.", docs.Summary);
    }

    [Fact]
    public void ParagraphsSurviveAsBlankLines() {
        var docs = Read(
            """
            /// <summary>One.</summary>
            /// <remarks>
            ///     <para>First.</para>
            ///     <para>Second.</para>
            /// </remarks>
            public struct Position { public float X; }
            """, "Position");

        Assert.Equal("First.\n\nSecond.", docs.Remarks);
    }

    /// <summary>
    ///     Roslyn resolves a `cref` to a documentation id, which is exactly the identifier the graph
    ///     is keyed by — so a link an engineer wrote for the IDE becomes a link on the site with no
    ///     URL written anywhere.
    /// </summary>
    [Fact]
    public void CrefsAreCollectedAsDocumentationIds() {
        var docs = Read(
            """
            public struct Velocity { public float X; }

            /// <summary>Moves a <see cref="Velocity"/> along.</summary>
            /// <seealso cref="Velocity"/>
            public struct Position { public float X; }
            """, "Position");

        Assert.Equal(["T:Velocity"], docs.SeeAlso);
    }

    [Fact]
    public void ACrefInsideProseReadsAsTheNameItNames() {
        var docs = Read(
            """
            public struct Velocity { public float X; }

            /// <summary>Moves a <see cref="Velocity"/> along.</summary>
            public struct Position { public float X; }
            """, "Position");

        Assert.Equal("Moves a Velocity along.", docs.Summary);
    }

    /// <summary>
    ///     ⚠ `GetDocumentationCommentXml` expands `&lt;include&gt;` and leaves `&lt;inheritdoc/&gt;`
    ///     exactly as written, so this walk is the tool's own — and without it every type in the
    ///     engine that inherits its documentation renders as a blank page.
    /// </summary>
    [Fact]
    public void InheritDocIsResolvedByWalkingTheBaseChain() {
        var docs = Read(
            """
            /// <summary>A thing that ticks.</summary>
            public interface ITickable { }

            /// <inheritdoc/>
            public sealed class Clock : ITickable { }
            """, "Clock");

        Assert.Equal("A thing that ticks.", docs.Summary);
    }

    /// <summary>A type with no comment has no comment: nothing is borrowed from its base type
    ///     unless the author asked for it with `&lt;inheritdoc/&gt;`.</summary>
    [Fact]
    public void DocumentationIsNotInheritedWithoutTheTag() {
        var docs = Read(
            """
            /// <summary>A thing that ticks.</summary>
            public interface ITickable { }

            public sealed class Clock : ITickable { }
            """, "Clock");

        Assert.Null(docs.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<member><summary>unterminated")]
    public void NothingUsableParsesToEmptyRatherThanThrowing(string? xml) =>
        Assert.Equal(DocumentationComment.Empty, DocumentationComment.Parse(xml));
}
