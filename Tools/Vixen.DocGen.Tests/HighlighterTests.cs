// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.DocGen.Guide;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>Fence highlighting — docs/plan/25 § 3.4, from the compiler rather than from a grammar.</summary>
public class HighlighterTests {
    /// <summary>A compilation to bind against, so the semantic half of the classification is real.</summary>
    static Compilation Host() =>
        CSharpCompilation.Create(
            "Fixtures",
            [
                CSharpSyntaxTree.ParseText(
                    """
                    namespace Fixtures {
                        public struct Position { public float X; }
                        public sealed class Mover { public void Step(int frames) { } }
                    }
                    """)
            ],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    static IReadOnlyList<IReadOnlyList<DocSpan>> Highlight(string code, bool fragment = false, bool withHost = true) {
        var example = new Example("docs/guide/x.md", 1, "csharp", code, Compile: true, Fragment: fragment, Reason: null);
        var lines = Highlighter.Highlight(example, withHost ? Host() : null, null, CancellationToken.None);

        Assert.NotNull(lines);

        return lines;
    }

    static string Text(IReadOnlyList<IReadOnlyList<DocSpan>> lines) =>
        string.Join('\n', lines.Select(line => string.Concat(line.Select(span => span.Text))));

    static IEnumerable<string> KindsOf(IReadOnlyList<IReadOnlyList<DocSpan>> lines, string text) =>
        lines.SelectMany(line => line).Where(span => span.Text.Contains(text, StringComparison.Ordinal))
            .Select(span => span.Kind);

    /// <summary>
    ///     The runs have to reproduce the fence exactly, or the reader is shown code that is not the
    ///     code the build compiled.
    /// </summary>
    [Fact]
    public void TheRunsSpellTheFenceBackOut() {
        const string Code =
            """
            var mover = new Fixtures.Mover();

            mover.Step(3);
            """;

        Assert.Equal(Code, Text(Highlight(Code, fragment: true)));
    }

    [Fact]
    public void KeywordsAreKeywords() {
        Assert.Contains("keyword", KindsOf(Highlight("public sealed class Thing { }"), "sealed"));
    }

    [Fact]
    public void StringsAndNumbersAreThemselves() {
        var lines = Highlight("""var text = "hello"; var count = 42;""", fragment: true);

        Assert.Contains("string", KindsOf(lines, "hello"));
        Assert.Contains("number", KindsOf(lines, "42"));
    }

    [Fact]
    public void CommentsSurvive() {
        var lines = Highlight("// what this does\npublic class Thing { }");

        Assert.Contains("comment", KindsOf(lines, "what this does"));
    }

    /// <summary>
    ///     The point of binding rather than pattern-matching: `Position` is a struct because Roslyn
    ///     resolved it, and a grammar could only have guessed from its case.
    /// </summary>
    [Fact]
    public void ATypeIsWhatTheCompilerSaysItIs() {
        var lines = Highlight("Fixtures.Position at = default;\nFixtures.Mover mover = null!;", fragment: true);

        Assert.Contains("struct", KindsOf(lines, "Position"));
        Assert.Contains("class", KindsOf(lines, "Mover"));
    }

    [Fact]
    public void AMethodIsAFunctionAndAParameterIsAVariable() {
        var lines = Highlight("public void Step(int frames) { }", fragment: false);

        Assert.Contains("method", KindsOf(lines, "Step"));
        Assert.Contains("parameter", KindsOf(lines, "frames"));
    }

    /// <summary>A fence that does not bind keeps its keywords, which is most of what colour is for.</summary>
    [Fact]
    public void AFenceWithNoHostIsStillClassified() {
        var lines = Highlight("public sealed class Thing { }", withHost: false);

        Assert.Contains("keyword", KindsOf(lines, "sealed"));
        Assert.Equal("public sealed class Thing { }", Text(lines));
    }

    [Fact]
    public void LinesAreLines() {
        var lines = Highlight("public class A { }\n\npublic class B { }");

        Assert.Equal(3, lines.Count);
        Assert.Empty(lines[1]);
    }

    [Fact]
    public void NothingButCSharpIsClassified() {
        var example = new Example("docs/guide/x.md", 1, "rvn", "shader Lit { }", Compile: false, Fragment: false, Reason: "not C#");

        Assert.Null(Highlighter.Highlight(example, Host(), null, CancellationToken.None));
    }
}
