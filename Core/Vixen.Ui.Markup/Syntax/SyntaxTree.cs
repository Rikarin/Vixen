// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Parsing;

namespace Vixen.Ui.Markup.Syntax;

/// <summary>A parsed <c>.vxml</c> file: its tree, its text, and what went wrong reading it.</summary>
public sealed class SyntaxTree : ISyntaxTree {
    DocumentSyntax? root;
    Diagnostic[] diagnostics = [];

    /// <summary>The encoding the file was read with, when the caller knew it.</summary>
    public Encoding? Encoding { get; private init; }

    /// <summary>The path diagnostics name and <c>#line</c> directives point at.</summary>
    public string FilePath { get; private init; } = string.Empty;

    /// <summary>The source's length in characters.</summary>
    public int Length { get; private init; }

    /// <summary>The source text, kept for line mapping and for the next incremental reparse.</summary>
    public SourceText? Text { get; private init; }

    /// <summary>Lexer and parser diagnostics (empty for a clean parse).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    /// <summary>The document node.</summary>
    /// <returns>The root.</returns>
    public SyntaxNode GetRoot() => root!;

    /// <summary>The document node, typed.</summary>
    /// <returns>The root.</returns>
    public DocumentSyntax GetDocument() => root!;

    /// <summary>Gets the root if there is one.</summary>
    /// <param name="root">The root, or null.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryGetRoot(out SyntaxNode? root) {
        root = this.root;
        return root is not null;
    }

    /// <summary>Parses a <c>.vxml</c> file.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="path">The path diagnostics name.</param>
    /// <param name="encoding">The encoding it was read with, if known.</param>
    /// <returns>A tree. Always — a file that does not parse still produces one, with diagnostics.</returns>
    public static SyntaxTree ParseText(string text, string? path = "", Encoding? encoding = default) {
        ArgumentNullException.ThrowIfNull(text);

        var sourceText = SourceText.From(text);
        var filePath = path ?? string.Empty;
        var bag = new DiagnosticBag();

        var tree = new SyntaxTree {
            Encoding = encoding, FilePath = filePath, Length = text.Length, Text = sourceText
        };

        var tokens = VxmlLexer.Lex(text, bag, sourceText, filePath);
        tree.root = VxmlParser.Parse(tokens, bag, sourceText, filePath);
        tree.root.SyntaxTree = tree;
        tree.diagnostics = bag.ToArray();

        return tree;
    }

    /// <summary>Reparses this tree against edited text, keeping the path and encoding.</summary>
    /// <param name="newText">The file as it now reads.</param>
    /// <returns>A tree over <paramref name="newText" />.</returns>
    /// <remarks>
    ///     ⚠ <b>This reparses the whole file.</b> The shared <c>Blender</c> exists and Raven uses
    ///     it, but node reuse needs a unit of reuse — Raven offers member declarations — and VXML's
    ///     is not obvious: an element's green node is reusable only if nothing about its
    ///     <i>enclosing</i> content changed, because an unclosed tag anywhere above it changes what
    ///     it is. Reuse is owed, and until it lands this is honest rather than fast.
    /// </remarks>
    public SyntaxTree WithChangedText(SourceText newText) {
        ArgumentNullException.ThrowIfNull(newText);
        return ParseText(newText.ToString(), FilePath, Encoding);
    }
}
