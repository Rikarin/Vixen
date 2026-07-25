using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>A <c>val</c>/<c>var</c> field declared in a type body.</summary>
public sealed class SourceFieldSymbol : FieldSymbol {
    readonly Binder binder;
    readonly FieldDeclarationSyntax syntax;

    bool resolving;
    TypeSymbol? type;

    public VariableDeclarationSyntax Declaration => syntax.Declaration;

    public override string Name => Declaration.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => syntax;

    public override bool IsConst => DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ConstKeyword);

    public override bool IsStatic => IsConst || DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.StaticKeyword);

    public override bool IsReadOnly =>
        IsConst
        || Declaration.Keyword.Kind == SyntaxKind.ValKeyword
        || DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ReadOnlyKeyword);

    public override Accessibility DeclaredAccessibility =>
        DeclarationFacts.GetAccessibility(syntax.Modifiers, Accessibility.Private);

    public override TypeSymbol Type => type ??= ResolveType();

    public override string? SemanticName => DeclarationFacts.GetSemanticName(syntax.AttributeLists);

    public override object? ConstantValue =>
        IsConst && Declaration.Initializer?.Value is LiteralExpressionSyntax literal
            ? LiteralParser.Parse(literal).Value
            : null;

    public override ResourceKind ResourceKind {
        get {
            if (Type is BuiltInNamedTypeSymbol { ResourceKind: not ResourceKind.None } resource) {
                return resource.ResourceKind;
            }

            return ContainingType is { TypeKind: TypeKind.Shader } && Type.IsNumericLike && !IsConst
                ? ResourceKind.Uniform
                : ResourceKind.None;
        }
    }

    internal SourceFieldSymbol(NamedTypeSymbol containingType, FieldDeclarationSyntax syntax, Binder binder) {
        ContainingSymbol = containingType;
        this.syntax = syntax;
        this.binder = binder;
    }

    TypeSymbol ResolveType() {
        if (resolving) {
            binder.Diagnostics.Add(SemanticDiagnostics.CircularDefinition, Declaration.Identifier.GetLocation(), Name);
            return ErrorTypeSymbol.Instance;
        }

        resolving = true;
        try {
            if (Declaration.Type is { } annotation) {
                return binder.BindType(annotation);
            }

            if (Declaration.Initializer?.Value is { } initializer) {
                // The initializer is bound for real by the semantic model; here we
                // only need its type, so its diagnostics are discarded.
                return binder.InferType(initializer);
            }

            binder.Diagnostics.Add(
                SemanticDiagnostics.MissingTypeOrInitializer,
                Declaration.Identifier.GetLocation(),
                Name
            );
            return ErrorTypeSymbol.Instance;
        } finally {
            resolving = false;
        }
    }

    /// <summary>Resolves the declared type, so its diagnostics appear unprompted.</summary>
    internal void EnsureSignatureResolved() => _ = Type;
}

/// <summary>A member of an <c>enum</c>: a constant of the enum's own type.</summary>
public sealed class SourceEnumMemberSymbol : FieldSymbol {
    readonly EnumMemberDeclarationSyntax syntax;

    /// <summary>Declaration order, which is the implicit value when none is given.</summary>
    public int Ordinal { get; }

    public override string Name => syntax.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => syntax;
    public override TypeSymbol Type => (TypeSymbol)ContainingSymbol!;
    public override bool IsConst => true;
    public override bool IsReadOnly => true;
    public override bool IsStatic => true;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;

    public override object? ConstantValue =>
        syntax.Value?.Value is LiteralExpressionSyntax literal ? LiteralParser.Parse(literal).Value : Ordinal;

    internal SourceEnumMemberSymbol(NamedTypeSymbol containingType, EnumMemberDeclarationSyntax syntax, int ordinal) {
        ContainingSymbol = containingType;
        this.syntax = syntax;
        Ordinal = ordinal;
    }
}
