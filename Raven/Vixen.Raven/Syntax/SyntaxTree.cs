// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Core.Syntax.Parsing;
using Vixen.Core.Syntax.Text;
using Vixen.Raven.Parsing;

namespace Vixen.Raven.Syntax;

public sealed class SyntaxTree : ISyntaxTree {
    SyntaxNode? root;
    Diagnostic[] diagnostics = [];

    /// <summary>The token stream this tree was parsed from.</summary>
    /// <remarks>
    ///     Kept for the next incremental reparse, which has to know how the previous text
    ///     <i>lexed</i> rather than merely what it said: untouched characters are not unchanged
    ///     tokens, and a candidate for reuse is only sound when the new stream reads them the old
    ///     way. The list is the one the lexer already built, so this is a reference held rather than
    ///     work done.
    /// </remarks>
    IReadOnlyList<LexedToken> tokens = [];

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
    ///         <b>The reparse is incremental</b> (docs/plan/18 step 7): member declarations
    ///         whose text a change did not touch are taken from this tree's green nodes rather
    ///         than reparsed — editing one function body reparses that member and shifts the
    ///         rest. The change ranges answer exactly only for the immediate predecessor text;
    ///         anything else conservatively reports the whole document and parses fresh.
    ///     </para>
    /// </remarks>
    public SyntaxTree WithChangedText(SourceText newText) {
        ArgumentNullException.ThrowIfNull(newText);

        if (Text is not { } oldText || root is null) {
            return ParseText(newText.ToString(), Options, FilePath, Encoding);
        }

        var changes = newText.GetChangeRanges(oldText);
        if (changes.Count == 0) {
            return this;
        }

        var blender = new Blender(MemberCandidates(root, diagnostics, oldText), changes, tokens);
        return ParseText(newText.ToString(), Options, FilePath, Encoding, blender);
    }

    /// <summary>
    ///     The nodes offered for reuse: every member declaration, at every nesting
    ///     level, <b>whose own parse reported nothing</b>. A type whose whole span is
    ///     untouched and clean is reused wholesale; one that is not still offers the
    ///     clean members inside it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The cleanliness gate is not an optimisation, and leaving it out is silent.</b> A
    ///     reused subtree keeps its nodes and loses its <i>diagnostics</i>, because those were
    ///     produced by the parse that is not being run again. Offering a member that reported an
    ///     error therefore yields a tree which still contains the broken construct and a diagnostic
    ///     list which no longer mentions it — so an author editing one function watches the errors
    ///     in the rest of the file disappear, and a hot reload binds a tree with fabricated tokens in
    ///     it while reporting no syntax error to explain them. This is the entry point a hot reload
    ///     calls, so that is the live path.
    ///     <para>
    ///         Found by <c>Vixen.Net.Fuzz</c>'s <c>raven</c> target, which reported it thirty-two
    ///         ways in the first four hundred cases. <c>IncrementalParseTests</c> asserts the
    ///         diagnostic counts match and did not catch it, because every shader it edits parses
    ///         cleanly and zero equals zero. VXML's front end has had this gate from the start.
    ///     </para>
    /// </remarks>
    static IEnumerable<SyntaxNode> MemberCandidates(
        SyntaxNode node,
        IReadOnlyList<Diagnostic> reported,
        SourceText text
    ) {
        foreach (var child in node.ChildNodesAndTokens()) {
            if (child is MemberDeclarationSyntax member) {
                if (Clean(member, reported, text)) {
                    yield return member;
                }

                foreach (var nested in MemberCandidates(member, reported, text)) {
                    yield return nested;
                }
            } else if (child is CompilationUnitSyntax or SyntaxListNode) {
                foreach (var nested in MemberCandidates(child, reported, text)) {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>Whether a node's parse ran to the end without complaining.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two questions, because a diagnostic does not always land inside the node whose
    ///         parse produced it.</b> The parser reports where it is <i>standing</i>, and recovery can
    ///         have carried that well past the construct it was reading — leaving a node that is
    ///         plainly broken and whose own span no diagnostic touches. The fabricated token, though,
    ///         is always inside: so nothing reported over these characters, <em>and</em> nothing
    ///         missing among them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The window runs to the first token <i>after</i> the node, and that extra reach is
    ///         a second defect rather than caution.</b> A member declaration ends by requiring a line
    ///         break — <c>ExpectSingleNewLine</c>, the last thing <c>ParseMemberDeclaration</c> does —
    ///         and that check reports where the parser is standing, which is the next real token and
    ///         not anything the member owns. Reuse skips it, because <c>TryReuseMember</c> returns
    ///         before the parse that would run it, so the missing terminator goes unreported. The
    ///         member's own span cannot see that diagnostic: the whitespace between them is the
    ///         <i>next</i> token's leading trivia, so it is outside the member's full span too.
    ///     </para>
    ///     <para>
    ///         Skipping forward over whitespace is what closes that gap. It is an approximation of
    ///         "the diagnostic this member's parse would have produced", and it approximates in the
    ///         safe direction: a node wrongly refused is a node reparsed, which costs time, and a node
    ///         wrongly reused is a diagnostic that silently disappears from an editor.
    ///     </para>
    /// </remarks>
    static bool Clean(SyntaxNode node, IReadOnlyList<Diagnostic> reported, SourceText text) {
        var span = node.FullSpan;
        var end = span.End;

        while (end < text.Length && char.IsWhiteSpace(text[end])) {
            end++;
        }

        foreach (var diagnostic in reported) {
            var at = diagnostic.Location.SourceSpan;

            // An overlap test rather than a containment one, so that a zero-width diagnostic — which
            // is what a missing token reports — still counts as inside.
            if (at.Start <= end && span.Start <= at.End) {
                return false;
            }
        }

        return !AnythingMissing(node);
    }

    /// <summary>Whether a subtree holds a token the parser fabricated.</summary>
    static bool AnythingMissing(SyntaxNode node) {
        if (node is SyntaxToken token) {
            return token.IsMissing;
        }

        for (var i = 0; i < node.SlotCount; i++) {
            if (node.GetSlot(i) is { } child && AnythingMissing(child)) {
                return true;
            }
        }

        return false;
    }

    public static SyntaxTree ParseText(
        string text,
        ParseOptions? options = default,
        string? path = "",
        Encoding? encoding = default
    ) =>
        ParseText(text, options, path, encoding, blender: null);

    static SyntaxTree ParseText(
        string text,
        ParseOptions? options,
        string? path,
        Encoding? encoding,
        Blender? blender
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
        var lexed = RavenLexer.Lex(text, bag, sourceText, filePath);
        syntaxTree.tokens = lexed;
        syntaxTree.root = RavenParser.Parse(lexed, bag, sourceText, filePath, blender);
        syntaxTree.root.SyntaxTree = syntaxTree;

        syntaxTree.diagnostics = bag.ToArray();

        return syntaxTree;
    }
}
