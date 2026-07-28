// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Ui.Markup.Parsing;
using Green = Vixen.Core.Syntax.InternalSyntax;

namespace Vixen.Ui.Markup.Syntax;

/// <summary>A parsed <c>.vxml</c> file: its tree, its text, and what went wrong reading it.</summary>
public sealed class SyntaxTree : ISyntaxTree {
    DocumentSyntax? root;
    Diagnostic[] diagnostics = [];

    /// <summary>The token stream this tree was parsed from.</summary>
    /// <remarks>
    ///     Kept for the next incremental reparse, which has to know how the previous text
    ///     <i>lexed</i> and not merely what it said: the same characters read differently under a
    ///     different mode stack, and a candidate for reuse is only sound when the new stream reads
    ///     them the old way. The list is the one the lexer already built, so this is a reference
    ///     held rather than work done.
    /// </remarks>
    IReadOnlyList<LexedToken> tokens = [];

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

    /// <summary>How many nodes this parse took from the tree it was reparsed from.</summary>
    /// <remarks>
    ///     Zero for a fresh parse. Exposed because "a keystroke reparses one element and shifts the
    ///     rest" is a claim, and equal trees only prove reuse is <i>safe</i> — this is what says it
    ///     happened at all.
    /// </remarks>
    public int ReusedNodes { get; private set; }

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
    public static SyntaxTree ParseText(string text, string? path = "", Encoding? encoding = default) =>
        ParseText(text, path, encoding, blender: null);

    static SyntaxTree ParseText(string text, string? path, Encoding? encoding, Blender? blender) {
        ArgumentNullException.ThrowIfNull(text);

        var sourceText = SourceText.From(text);
        var filePath = path ?? string.Empty;
        var bag = new DiagnosticBag();

        var tree = new SyntaxTree {
            Encoding = encoding, FilePath = filePath, Length = text.Length, Text = sourceText
        };

        var lexed = VxmlLexer.Lex(text, bag, sourceText, filePath);
        tree.tokens = lexed;
        tree.root = VxmlParser.Parse(lexed, bag, sourceText, filePath, blender, out var reused);
        tree.root.SyntaxTree = tree;
        tree.diagnostics = bag.ToArray();
        tree.ReusedNodes = reused;

        return tree;
    }

    /// <summary>Reparses this tree against edited text, keeping the path and encoding.</summary>
    /// <param name="newText">The file as it now reads.</param>
    /// <returns>A tree over <paramref name="newText" />.</returns>
    /// <remarks>
    ///     <para>
    ///         Content nodes whose text a change did not touch are taken from this tree's green
    ///         nodes rather than reparsed — editing one attribute reparses that element and shifts
    ///         the rest. The file is always re-<i>lexed</i>, and a node is only lent out when the
    ///         new stream reads its characters as the old one did: this lexer carries a mode stack,
    ///         so an edit above a node decides what its text lexes <i>as</i>, and untouched
    ///         characters are no promise of unchanged tokens.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only subtrees that reported nothing are offered.</b> The parser's one piece of
    ///         enclosing state is the list of elements currently open, and every branch that reads it
    ///         reports a diagnostic — so a subtree that parsed cleanly never consulted it and parses
    ///         the same wherever it sits. It also has no diagnostics to lose, which matters because a
    ///         reused subtree's are not re-reported.
    ///     </para>
    ///     <para>
    ///         The change ranges answer exactly only for the immediate predecessor text; anything
    ///         else conservatively reports the whole document and parses fresh.
    ///     </para>
    /// </remarks>
    public SyntaxTree WithChangedText(SourceText newText) {
        ArgumentNullException.ThrowIfNull(newText);

        if (Text is not { } oldText || root is null) {
            return ParseText(newText.ToString(), FilePath, Encoding);
        }

        var changes = newText.GetChangeRanges(oldText);

        if (changes.Count == 0) {
            return this;
        }

        var blender = new Blender(Candidates(root, diagnostics), changes, tokens);
        return ParseText(newText.ToString(), FilePath, Encoding, blender);
    }

    /// <summary>
    ///     The nodes offered for reuse: every content node, at every nesting level, whose own
    ///     subtree reported nothing.
    /// </summary>
    /// <remarks>
    ///     An element that is itself clean is offered whole; one that is not still offers the clean
    ///     children inside it, which is what keeps a typo in one attribute from reparsing the file.
    /// </remarks>
    static IEnumerable<SyntaxNode> Candidates(SyntaxNode node, IReadOnlyList<Diagnostic> reported) {
        foreach (var child in node.ChildNodesAndTokens()) {
            if (child is not SyntaxNode inner || child is SyntaxToken) {
                continue;
            }

            if (inner is MarkupSyntax && Clean(inner, reported)) {
                yield return inner;
                continue;
            }

            foreach (var nested in Candidates(inner, reported)) {
                yield return nested;
            }
        }
    }

    /// <summary>Whether a node's parse ran to the end without complaining.</summary>
    /// <remarks>
    ///     ⚠ <b>A diagnostic does not always land inside the node whose parse produced it.</b>
    ///     <c>Expect</c> reports where the parser is <i>standing</i>, and recovery can have carried
    ///     that far past the node it was parsing — a <c>&lt;style</c> whose tag never closes skips
    ///     the rest of the file and then reports its missing <c>&gt;</c> at end of file, leaving a
    ///     node whose own span no diagnostic touches. The fabricated token, though, is always in the
    ///     node, so that is the second half of the question: nothing reported over these characters,
    ///     <em>and</em> nothing missing among them.
    /// </remarks>
    static bool Clean(SyntaxNode node, IReadOnlyList<Diagnostic> reported) {
        var start = node.Position;
        var end = start + node.Green.FullWidth;

        foreach (var diagnostic in reported) {
            var span = diagnostic.Location.SourceSpan;

            // A zero-width diagnostic — a missing token — still counts as inside, which is why this
            // is an overlap test rather than a containment one.
            if (span.Start < end && start <= span.End) {
                return false;
            }
        }

        return !AnythingMissing(node.Green);
    }

    /// <summary>Whether a green subtree holds a token the parser fabricated.</summary>
    static bool AnythingMissing(Green.GreenNode green) {
        if (green.IsToken) {
            return green is Green.SyntaxToken { IsMissing: true };
        }

        for (var i = 0; i < green.SlotCount; i++) {
            if (green.GetSlot(i) is { } child && AnythingMissing(child)) {
                return true;
            }
        }

        return false;
    }
}
