// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Antlr4.Runtime;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Grammar;
using Vixen.Raven.Syntax;

namespace Tests;

/// <summary>
///     The retired ANTLR front end, preserved verbatim as the differential oracle
///     (docs/plan/18 § Keep the grammar): the <c>.g4</c> files stay executable
///     specification, and <see cref="ParserDifferentialTests" /> holds the
///     hand-written parser to byte-identical trees against them.
/// </summary>
public static class AntlrOracle {
    public static (SyntaxNode Root, IReadOnlyList<Diagnostic> Diagnostics) Parse(string text) {
        var sourceText = SourceText.From(text);
        var bag = new DiagnosticBag();

        var listener = new RavenSyntaxErrorListener(bag, sourceText, "oracle.rvn");
        var stream = new AntlrInputStream(text);
        var lexer = new RavenLexer(stream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(listener);

        var tokenStream = new CommonTokenStream(lexer);
        var parser = new RavenParser(tokenStream);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(listener);

        var tree = parser.compilation_unit();

        tokenStream.Fill();
        var visitor = new SyntaxAntlrVisitor(tokenStream);
        var root = tree.Accept(visitor);

        return (root, bag.ToArray());
    }
}
