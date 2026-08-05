// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>
///     A <c>var</c> property or a <c>self[…]</c> indexer. Raven properties may carry
///     <c>willSet</c>/<c>didSet</c> observers in addition to <c>get</c>/<c>set</c>;
///     any of them makes the property writable.
/// </summary>
internal sealed class SourcePropertySymbol : PropertySymbol {
    readonly Binder binder;
    ParameterSymbol[]? parameters;
    bool resolving;
    TypeSymbol? type;

    public MemberDeclarationSyntax Syntax { get; }

    public override string Name =>
        Syntax switch {
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            _ => string.Empty
        };

    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => Syntax;
    public override bool IsStatic => DeclarationFacts.Has(Syntax.Modifiers, SyntaxKind.StaticKeyword);

    public override TypeSymbol Type => type ??= ResolveType();

    /// <summary>The accessor block, when the property declares one.</summary>
    public AccessorListSyntax? AccessorList =>
        Syntax switch {
            PropertyDeclarationSyntax property => property.AccessorList,
            _ => null
        };

    /// <summary>The <c>=&gt; expression</c> body, when the property is expression-bodied.</summary>
    public ArrowExpressionClauseSyntax? ExpressionBody =>
        Syntax switch {
            PropertyDeclarationSyntax property => property.ExpressionBody,
            _ => null
        };

    /// <summary>The <c>= value</c> initializer, when present.</summary>
    public EqualsValueClauseSyntax? Initializer =>
        Syntax is PropertyDeclarationSyntax property ? property.Initializer : null;

    public override bool HasGetter => ExpressionBody is not null || HasAccessor(SyntaxKind.GetAccessorDeclaration);

    public override bool HasSetter =>
        HasAccessor(SyntaxKind.SetAccessorDeclaration)
        || HasAccessor(SyntaxKind.WillSetAccessorDeclaration)
        || HasAccessor(SyntaxKind.DidSetAccessorDeclaration);

    public override IReadOnlyList<ParameterSymbol> Parameters => parameters ??= ResolveParameters();

    internal SourcePropertySymbol(NamedTypeSymbol containingType, MemberDeclarationSyntax syntax, Binder binder) {
        ContainingSymbol = containingType;
        Syntax = syntax;
        this.binder = binder;
    }

    bool HasAccessor(SyntaxKind kind) {
        if (AccessorList is not { } list) {
            return false;
        }

        foreach (var accessor in list.Accessors) {
            if (accessor.Kind == kind) {
                return true;
            }
        }

        return false;
    }

    /// <summary>A property takes no parameters: the indexer form that did is gone.</summary>
    static ParameterSymbol[] ResolveParameters() => [];

    TypeSymbol ResolveType() {
        if (resolving) {
            binder.Diagnostics.Add(SemanticDiagnostics.CircularDefinition, DeclaringSyntax.GetLocation(), Name);
            return ErrorTypeSymbol.Instance;
        }

        resolving = true;
        try {
            var annotation = Syntax switch {
                PropertyDeclarationSyntax property => property.Type,
                _ => null
            };

            if (annotation is not null) {
                return binder.BindType(annotation);
            }

            // No annotation: take the type from the getter body or the initializer.
            if (ExpressionBody?.Expression is { } expressionBody) {
                return binder.InferType(expressionBody);
            }

            if (Initializer?.Value is { } initializer) {
                return binder.InferType(initializer);
            }

            if (AccessorList is { } list) {
                foreach (var accessor in list.Accessors) {
                    if (accessor.Kind == SyntaxKind.GetAccessorDeclaration
                        && accessor.ExpressionBody?.Expression is { } getter) {
                        return binder.InferType(getter);
                    }
                }
            }

            binder.Diagnostics.Add(SemanticDiagnostics.MissingTypeOrInitializer, DeclaringSyntax.GetLocation(), Name);
            return ErrorTypeSymbol.Instance;
        } finally {
            resolving = false;
        }
    }

    /// <summary>Resolves the declared type and parameters, so their diagnostics appear unprompted.</summary>
    internal void EnsureSignatureResolved() {
        _ = Type;
        foreach (var parameter in Parameters) {
            _ = parameter.Type;
        }
    }
}
