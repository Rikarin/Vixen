// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Vixen.DocGen.Guide;

/// <summary>
///     Classifies a guide's code fences — docs/plan/25 § 3.4.
/// </summary>
/// <remarks>
///     <para>
///         **The compiler that checks an example also colours it.** § 3.4's rule is that highlighting
///         comes from the engine's own front ends rather than from a grammar in a browser, and for
///         C# that front end is already here: the fence is added to the same compilation
///         <see cref="Examples" /> builds it in, so the identifier this calls a <c>struct</c> is a
///         struct because Roslyn bound it, not because a regular expression guessed from its case.
///     </para>
///     <para>
///         ⚠ <b>A fence that does not compile is still classified.</b> The semantic model answers what
///         it can and the syntax carries the rest — keywords, strings, comments, numbers — so a
///         `no-compile` fence loses the type colours and keeps everything else, which is a much better
///         page than an uncoloured one.
///     </para>
/// </remarks>
static class Highlighter {
    /// <summary>The fence, one entry per line, each a list of classified runs.</summary>
    public static IReadOnlyList<IReadOnlyList<DocSpan>>? Highlight(
        Example example,
        Compilation? host,
        CSharpParseOptions? parseOptions,
        CancellationToken cancellationToken
    ) {
        if (!string.Equals(example.Language, "csharp", StringComparison.Ordinal) || example.Code.Length == 0) {
            return null;
        }

        var (source, offset) = Examples.Wrap(example, 0);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: cancellationToken);

        // The model is a bonus rather than a requirement: a fence with errors still has keywords.
        var model = host is null ? null : SafeModel(host, tree);
        var region = new TextSpan(offset, example.Code.Length);
        var spans = new List<DocSpan>();
        var root = tree.GetRoot(cancellationToken);

        foreach (var token in root.DescendantTokens(descendIntoTrivia: false)) {
            foreach (var trivia in token.LeadingTrivia) {
                Add(spans, source, region, trivia.FullSpan, KindOfTrivia(trivia));
            }

            Add(spans, source, region, token.Span, KindOfToken(token, model, cancellationToken));

            foreach (var trivia in token.TrailingTrivia) {
                Add(spans, source, region, trivia.FullSpan, KindOfTrivia(trivia));
            }
        }

        return Lines(spans);
    }

    static SemanticModel? SafeModel(Compilation host, SyntaxTree tree) {
        try {
            return host.AddSyntaxTrees(tree).GetSemanticModel(tree);
        } catch (ArgumentException) {
            // A host whose parse options this tree cannot join. The syntax alone is still worth
            // having, and a page that renders uncoloured beats a build that stops.
            return null;
        }
    }

    /// <summary>Adds the part of <paramref name="span" /> that is inside the fence, if any.</summary>
    static void Add(List<DocSpan> spans, string source, TextSpan region, TextSpan span, string kind) {
        var start = Math.Max(span.Start, region.Start);
        var end = Math.Min(span.End, region.End);

        if (end <= start) {
            return;
        }

        var text = source[start..end];

        // Merged rather than appended when the kind repeats: `}` `}` `;` as three runs is three
        // times the JSON for one colour.
        if (spans.Count > 0 && string.Equals(spans[^1].Kind, kind, StringComparison.Ordinal)) {
            spans[^1] = spans[^1] with { Text = spans[^1].Text + text };

            return;
        }

        spans.Add(new DocSpan(text, kind));
    }

    static string KindOfTrivia(SyntaxTrivia trivia) =>
        trivia.Kind() switch {
            SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia => "comment",
            SyntaxKind.SingleLineDocumentationCommentTrivia
                or SyntaxKind.MultiLineDocumentationCommentTrivia => "comment",
            SyntaxKind.DisabledTextTrivia => "comment",
            _ => "space"
        };

    /// <summary>
    ///     What a token is, asking the compiler before guessing.
    /// </summary>
    /// <remarks>
    ///     The kinds are the same vocabulary <see cref="Signatures" /> emits, so a signature and an
    ///     example are coloured by one palette — and the site maps that vocabulary onto
    ///     <c>@xui/code-block</c>'s in one place.
    /// </remarks>
    static string KindOfToken(SyntaxToken token, SemanticModel? model, CancellationToken cancellationToken) {
        if (token.IsKeyword() || token.Kind() is SyntaxKind.VarKeyword) {
            return "keyword";
        }

        switch (token.Kind()) {
            case SyntaxKind.StringLiteralToken:
            case SyntaxKind.CharacterLiteralToken:
            case SyntaxKind.SingleLineRawStringLiteralToken:
            case SyntaxKind.MultiLineRawStringLiteralToken:
            case SyntaxKind.InterpolatedStringTextToken:
                return "string";

            case SyntaxKind.NumericLiteralToken:
                return "number";

            case SyntaxKind.IdentifierToken:
                return KindOfIdentifier(token, model, cancellationToken);
        }

        return SyntaxFacts.IsPunctuation(token.Kind()) ? "punctuation" : "text";
    }

    static string KindOfIdentifier(SyntaxToken token, SemanticModel? model, CancellationToken cancellationToken) {
        var parent = token.Parent;

        if (model is null || parent is null) {
            return "text";
        }

        // The declaration's own name binds to nothing on the way up, so it is asked for directly —
        // otherwise every `class Foo` would colour `Foo` as plain text.
        var symbol = model.GetDeclaredSymbol(parent, cancellationToken)
            ?? model.GetSymbolInfo(parent, cancellationToken).Symbol
            ?? model.GetSymbolInfo(parent, cancellationToken).CandidateSymbols.FirstOrDefault();

        return symbol switch {
            INamedTypeSymbol type => type.TypeKind switch {
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => "class"
            },
            ITypeParameterSymbol => "type-parameter",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            IParameterSymbol => "parameter",
            ILocalSymbol => "local",
            INamespaceSymbol => "namespace",
            _ => "text"
        };
    }

    /// <summary>Splits the runs on newlines, because a code block is rendered a line at a time.</summary>
    static List<IReadOnlyList<DocSpan>> Lines(List<DocSpan> spans) {
        var lines = new List<IReadOnlyList<DocSpan>>();
        var current = new List<DocSpan>();

        foreach (var span in spans) {
            var parts = span.Text.ReplaceLineEndings("\n").Split('\n');

            for (var index = 0; index < parts.Length; index++) {
                if (index > 0) {
                    lines.Add(current);
                    current = [];
                }

                if (parts[index].Length > 0) {
                    current.Add(new DocSpan(parts[index], span.Kind));
                }
            }
        }

        lines.Add(current);

        // A fence's last line is the newline before the closing ```, and an empty row at the bottom
        // of every sample is a row nobody wanted.
        while (lines.Count > 0 && lines[^1].Count == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
