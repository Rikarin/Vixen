// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;

namespace Vixen.DocGen;

/// <summary>Signatures, already classified — docs/plan/25 § 3.4.</summary>
/// <remarks>
///     <para>
///         § 3.4 says code arrives at the site already tokenised, so the browser ships no highlighter
///         and the prerendered HTML is coloured for readers without JavaScript. For <em>quoted</em>
///         code — a guide's fence, a doc comment's <c>&lt;code&gt;</c> — that means Roslyn's
///         classifier over the real text.
///     </para>
///     <para>
///         A signature is not quoted code. It is synthesised from the symbol, so there is no source
///         span to classify — and Roslyn hands the classification out with the text:
///         <c>ToDisplayParts</c> returns the same runs <c>ToDisplayString</c> concatenates, each
///         tagged with what it is. The classifier would be a second, weaker answer to a question
///         already answered.
///     </para>
/// </remarks>
static class Signatures {
    /// <summary>
    ///     A type's signature: <c>public sealed class World</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>`SymbolDisplayFormat` cannot produce the left-hand half of this.</b> Accessibility
    ///     and modifiers are member options; for a type it emits `class World` at best and `World`
    ///     by default. `Vixen.ApiCheck` composes its baseline lines by hand for the same reason —
    ///     and the two agreeing about what a type's declaration reads as is what makes them
    ///     comparable.
    /// </remarks>
    public static IReadOnlyList<DocSpan> OfType(INamedTypeSymbol type, SymbolDisplayFormat format) {
        var spans = new List<DocSpan>();

        void Keyword(string text) {
            spans.Add(new DocSpan(text, "keyword"));
            spans.Add(new DocSpan(" ", "space"));
        }

        Keyword(type.DeclaredAccessibility switch {
            Accessibility.Public => "public",
            Accessibility.Protected => "protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "internal"
        });

        if (type is { IsStatic: true }) {
            Keyword("static");
        }

        if (type is { IsAbstract: true, TypeKind: TypeKind.Class }) {
            Keyword("abstract");
        }

        if (type is { IsSealed: true, TypeKind: TypeKind.Class, IsRecord: false }) {
            Keyword("sealed");
        }

        if (type is { IsReadOnly: true, TypeKind: TypeKind.Struct }) {
            Keyword("readonly");
        }

        if (type is { IsRefLikeType: true }) {
            Keyword("ref");
        }

        if (type.IsRecord) {
            Keyword("record");
        }

        Keyword(type.TypeKind switch {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Struct => "struct",
            _ => "class"
        });

        spans.AddRange(Of(type, format));

        return Merge(spans);
    }

    public static IReadOnlyList<DocSpan> Of(ISymbol symbol, SymbolDisplayFormat format) {
        var spans = new List<DocSpan>();

        foreach (var part in symbol.ToDisplayParts(format)) {
            spans.Add(new DocSpan(part.ToString(), Kind(part.Kind), TypeId(part)));
        }

        return Merge(spans);
    }

    /// <summary>
    ///     The type a run names, as a documentation id — or null when it names none.
    /// </summary>
    /// <remarks>
    ///     Asked of the part rather than parsed out of the text: <c>ToDisplayParts</c> carries the
    ///     symbol it wrote each run for, so <c>Vector3</c> in a parameter list is the same symbol the
    ///     graph has a page for, generic arguments included — <c>List&lt;World&gt;</c> is four parts
    ///     and the third of them is <c>World</c>.
    /// </remarks>
    static string? TypeId(SymbolDisplayPart part) =>
        part.Symbol is INamedTypeSymbol type ? type.OriginalDefinition.GetDocumentationCommentId() : null;

    /// <summary>
    ///     Adjacent runs of one kind are one run: `ToDisplayParts` emits a part per symbol, and the
    ///     `>` `>` closing a nested generic is two punctuation parts that render as one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Runs that name different types are never merged, however alike their kinds: `Vector3`
    ///     and `Quaternion` beside each other are two links, and merging them would make one wrong
    ///     one.
    /// </remarks>
    static List<DocSpan> Merge(List<DocSpan> spans) {
        var merged = new List<DocSpan>(spans.Count);

        foreach (var span in spans) {
            if (merged.Count > 0
                && string.Equals(merged[^1].Kind, span.Kind, StringComparison.Ordinal)
                && string.Equals(merged[^1].Id, span.Id, StringComparison.Ordinal)) {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };

                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    /// <summary>The plain text a classified signature reads as — what search indexes.</summary>
    public static string Text(IReadOnlyList<DocSpan> spans) =>
        string.Concat(spans.Select(span => span.Text));

    /// <summary>
    ///     Roslyn's part kinds, collapsed to the ones a page styles differently.
    /// </summary>
    /// <remarks>
    ///     Deliberately fewer than Roslyn has. A documentation site distinguishes a type name from a
    ///     keyword from a parameter; it does not need `RangeVariableName` to look different from
    ///     `LocalName`, and every distinct kind is a class the stylesheet has to define and the
    ///     theme has to get right in both modes.
    /// </remarks>
    static string Kind(SymbolDisplayPartKind kind) => kind switch {
        SymbolDisplayPartKind.Keyword => "keyword",
        SymbolDisplayPartKind.ClassName or SymbolDisplayPartKind.RecordClassName => "class",
        SymbolDisplayPartKind.StructName or SymbolDisplayPartKind.RecordStructName => "struct",
        SymbolDisplayPartKind.InterfaceName => "interface",
        SymbolDisplayPartKind.EnumName => "enum",
        SymbolDisplayPartKind.DelegateName => "delegate",
        SymbolDisplayPartKind.TypeParameterName => "type-parameter",
        SymbolDisplayPartKind.ParameterName => "parameter",
        SymbolDisplayPartKind.MethodName or SymbolDisplayPartKind.ExtensionMethodName => "method",
        SymbolDisplayPartKind.PropertyName => "property",
        SymbolDisplayPartKind.FieldName or SymbolDisplayPartKind.ConstantName => "field",
        SymbolDisplayPartKind.EventName => "event",
        SymbolDisplayPartKind.NamespaceName => "namespace",
        SymbolDisplayPartKind.Punctuation => "punctuation",
        SymbolDisplayPartKind.Operator => "operator",
        SymbolDisplayPartKind.NumericLiteral => "number",
        SymbolDisplayPartKind.StringLiteral => "string",
        SymbolDisplayPartKind.Space or SymbolDisplayPartKind.LineBreak => "space",
        _ => "text"
    };
}
