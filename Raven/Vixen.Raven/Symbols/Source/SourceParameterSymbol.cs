// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>A parameter declared in source.</summary>
public sealed class SourceParameterSymbol : ParameterSymbol {
    readonly Binder binder;
    readonly ParameterSyntax syntax;
    TypeSymbol? type;

    public override string Name => syntax.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override int Ordinal { get; }
    public override SyntaxNode DeclaringSyntax => syntax;
    public override bool HasDefaultValue => syntax.Default is not null;

    public override object? DefaultValue =>
        syntax.Default?.Value is LiteralExpressionSyntax literal ? LiteralParser.Parse(literal).Value : null;

    public override TypeSymbol Type => type ??= ResolveType();

    public override string? SemanticName => DeclarationFacts.GetSemanticName(syntax.AttributeLists);

    internal SourceParameterSymbol(Symbol container, ParameterSyntax syntax, int ordinal, Binder binder) {
        ContainingSymbol = container;
        this.syntax = syntax;
        this.binder = binder;
        Ordinal = ordinal;
    }

    TypeSymbol ResolveType() {
        if (syntax.Type is { } annotation) {
            return binder.BindType(annotation);
        }

        // `count: int = 42` is the normal form; a default with no annotation still
        // determines the type.
        if (syntax.Default?.Value is { } @default) {
            return binder.InferType(@default);
        }

        binder.Diagnostics.Add(SemanticDiagnostics.MissingTypeOrInitializer, syntax.Identifier.GetLocation(), Name);
        return ErrorTypeSymbol.Instance;
    }
}
