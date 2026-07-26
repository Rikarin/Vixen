// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Raven.Parsing;
using Vixen.Core.Syntax.Parsing;
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

        var blender = new Blender(MemberCandidates(root), changes);
        return ParseText(newText.ToString(), Options, FilePath, Encoding, blender);
    }

    /// <summary>
    ///     The nodes offered for reuse: every member declaration, at every nesting
    ///     level. A type whose whole span is untouched is reused wholesale; one that
    ///     is not still offers its unaffected members.
    /// </summary>
    static IEnumerable<SyntaxNode> MemberCandidates(SyntaxNode node) {
        foreach (var child in node.ChildNodesAndTokens()) {
            if (child is MemberDeclarationSyntax member) {
                yield return member;
                foreach (var nested in MemberCandidates(member)) {
                    yield return nested;
                }
            } else if (child is CompilationUnitSyntax or SyntaxListNode) {
                foreach (var nested in MemberCandidates(child)) {
                    yield return nested;
                }
            }
        }
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
        var tokens = RavenLexer.Lex(text, bag, sourceText, filePath);
        syntaxTree.root = RavenParser.Parse(tokens, bag, sourceText, filePath, blender);
        syntaxTree.root.SyntaxTree = syntaxTree;

        syntaxTree.diagnostics = bag.ToArray();

        return syntaxTree;
    }
}
