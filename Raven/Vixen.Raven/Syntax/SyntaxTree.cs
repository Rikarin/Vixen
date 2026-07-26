// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Raven.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Syntax;

public sealed class SyntaxTree : ISyntaxTree {
    SyntaxNode? root;
    Diagnostic[] diagnostics = [];

    public Encoding? Encoding { get; private init; }
    public string FilePath { get; private init; } = string.Empty;
    public int Length { get; private init; }
    public ParseOptions Options { get; private init; } = new();

    /// <summary>The source text this tree was parsed from, or null when created from a node.</summary>
    public SourceText? Text { get; private init; }

    /// <summary>Lexer/parser diagnostics produced while parsing (empty for a valid parse).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public SyntaxNode GetRoot() => root!;

    public bool TryGetRoot(out SyntaxNode? root) {
        root = this.root;
        return root != null;
    }

    public static SyntaxTree Create(
        SyntaxNode root,
        ParseOptions? options = default,
        string? path = "",
        Encoding? encoding = default
    ) =>
        new() {
            Encoding = encoding, FilePath = path ?? string.Empty, Options = options ?? new ParseOptions(), root = root
        };

    /// <summary>
    ///     Reparses this tree against edited text, keeping the path, options and encoding.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The entry point a hot reload calls: an editor holds the previous
    ///         <see cref="SourceText" />, applies <c>WithChanges</c>, and hands the result here.
    ///     </para>
    ///     <para>
    ///         <b>The reparse is currently full-file.</b> ANTLR owns parsing and has no notion of
    ///         reusing an existing tree, so the changed ranges are computed and then not used yet.
    ///         The shape is what matters: callers already pass edits rather than whole documents,
    ///         so making this incremental later changes no call site. A shader is small enough
    ///         that a full reparse fits the &lt; 500 ms reload budget with room to spare; a
    ///         2 000-line <c>.vxml</c> is the case that will need the real thing.
    ///     </para>
    /// </remarks>
    public SyntaxTree WithChangedText(SourceText newText) {
        ArgumentNullException.ThrowIfNull(newText);

        // Computed, and deliberately unused: it is what an incremental parser consumes, and
        // asking for it here keeps the contract honest about what callers must supply.
        _ = Text is { } oldText ? newText.GetChangeRanges(oldText) : [];

        return ParseText(newText.ToString(), Options, FilePath, Encoding);
    }

    public static SyntaxTree ParseText(
        string text,
        ParseOptions? options = default,
        string? path = "",
        Encoding? encoding = default
    ) {
        var sourceText = SourceText.From(text);
        var filePath = path ?? string.Empty;
        var bag = new DiagnosticBag();

        var syntaxTree = new SyntaxTree {
            Encoding = encoding,
            FilePath = filePath,
            Options = options ?? new ParseOptions(),
            Length = text.Length,
            Text = sourceText
        };

        // The hand-written front end (docs/plan/18). Recovery is explicit — missing
        // tokens are zero-width, skipped source travels as trivia — so even an
        // erroneous parse yields a tree that reproduces the file byte-for-byte.
        var tokens = RavenLexer.Lex(text, bag, sourceText, filePath);
        syntaxTree.root = RavenParser.Parse(tokens, bag, sourceText, filePath);
        syntaxTree.root.SyntaxTree = syntaxTree;

        syntaxTree.diagnostics = bag.ToArray();

        return syntaxTree;
    }
}
