// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Parsing;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 18 step 5, and the permanent oracle afterwards: the ANTLR front end and
///     the hand-written parser must produce byte-identical trees over the corpus.
///     The grammar stays as executable specification; divergence fails here rather
///     than shipping.
/// </summary>
public class ParserDifferentialTests {
    public static TheoryData<string> CorpusFiles() {
        var data = new TheoryData<string>();
        foreach (var file in CorpusLocator.All()) {
            data.Add(file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Trees_are_identical_over_the_corpus(string path) {
        var text = File.ReadAllText(path);
        AssertSameTree(text);
    }

    /// <summary>
    ///     Snippets aimed at the grammar's ambiguities — cast versus parenthesized,
    ///     generic name versus comparison, blank-line empty statements, attribute
    ///     versus collection — where whatever the grammar's resolution is, the
    ///     hand-written parser must reproduce it.
    /// </summary>
    [Theory]
    // The cast alternative outranks the binary loop: `(a) + b` is a cast of `+b`.
    [InlineData("val x = (a) + b\n")]
    // `*` cannot start an expression, so this stays arithmetic on a parenthesized name.
    [InlineData("val x = (a) * b\n")]
    [InlineData("val x = (int)-1\n")]
    // A generic name only wins when the angle brackets scan as a type list.
    [InlineData("val x = a < b\n")]
    [InlineData("val x = a < b > (c)\n")]
    [InlineData("val x = G<int>(y)\n")]
    // Blank lines and empty statements in every position.
    [InlineData("if (a) {\n}\n")]
    [InlineData("if (a) {\n}\n\n\nreturn\n")]
    [InlineData("x = 1\n\n\ny = 2\n")]
    [InlineData("while (a) {\n    x = 1\n}\n")]
    // `else if` chains: the clause takes the nested `if` directly, so both parsers have to
    // nest rather than wrap it in a block, and the last `else` has to stay a block.
    [InlineData("if (a) {\n} else {\n}\n")]
    [InlineData("if (a) {\n} else if (b) {\n}\n")]
    [InlineData("if (a) {\n} else if (b) {\n} else if (c) {\n} else {\n}\n")]
    // Attributes on statements, on their own line and inline.
    [InlineData("[Unroll] for (i in 0 .. 4) {\n}\n")]
    [InlineData("[Unroll]\nfor (i in 0 .. 4) {\n}\n")]
    // Collection versus attribute at statement position.
    [InlineData("val c = [1, 2, 3]\n")]
    // Conditional, range and assignment nesting.
    [InlineData("val x = a ? b : c ? d : e\n")]
    [InlineData("x = y = 1\n")]
    [InlineData("val r = 0 .. 4 .. 8\n")]
    // Chained calls and accesses mixing qualified names with member access.
    [InlineData("val x = a.b.c(d).e.f[0].g\n")]
    [InlineData("val x = float4(1, 2, 3, 4).rgb\n")]
    // `default` in both of its expression forms.
    [InlineData("val x = default\n")]
    [InlineData("val x = default(float4)\n")]
    // Sized arrays against element access — the one ambiguity a size introduces, and the
    // reason both parsers decide it by *position* rather than by what is in the brackets.
    // In an expression `[…]` always indexes; in a type it always sizes.
    [InlineData("val x = a[4]\n")]
    [InlineData("val x = a[b[0]]\n")]
    [InlineData("val x = a[i] + b[0]\n")]
    // Not a cast of `-1` to some type `a[4]`: the cast scan refuses to read a size.
    [InlineData("val x = (a[4]) - 1\n")]
    [InlineData("var y: float[4]\n")]
    [InlineData("var y: float[N]\n")]
    [InlineData("var y: float[N * 2 + 1]\n")]
    [InlineData("var y: float[]\n")]
    [InlineData("var y: float[,]\n")]
    [InlineData("var y: float[2][3]\n")]
    [InlineData("var y: Foo.Bar[8]\n")]
    [InlineData("val x = default(float[4])\n")]
    public void Trees_are_identical_for_the_ambiguity_probes(string body) {
        var text = $"package A\n\nshader S {{\n    func M() {{\n        {body.Replace("\n", "\n        ").TrimEnd(' ')}    }}\n}}\n";
        AssertSameTree(text);
    }

    static void AssertSameTree(string text) {
        // The retired ANTLR front end is the oracle; the grammar files remain the
        // executable specification of the syntax.
        var (oracleRoot, oracleDiagnostics) = AntlrOracle.Parse(text);
        Assert.Empty(oracleDiagnostics);

        var bag = new DiagnosticBag();
        var source = SourceText.From(text);
        var tokens = RavenLexer.Lex(text, bag, source, "diff.rvn");
        var root = RavenParser.Parse(tokens, bag, source, "diff.rvn");

        Assert.True(bag.IsEmpty, "Hand-written parser diagnostics:\n" + string.Join("\n", bag.Select(d => d.ToString())));
        Assert.Equal(text, root.ToFullString());
        Assert.Equal(SyntaxDumper.Dump(oracleRoot), SyntaxDumper.Dump(root));
    }
}
