// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
    NamedTypeSymbol? composedType;
    bool composeResolved;

    public VariableDeclarationSyntax Declaration => syntax.Declaration;

    /// <summary>The declaration's attribute lists, for validation that reads them directly.</summary>
    internal SyntaxList<AttributeListSyntax> AttributeLists => syntax.AttributeLists;

    public override string Name => Declaration.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => syntax;

    public override bool IsPermutation => DeclarationFacts.IsPermutation(syntax.AttributeLists);

    public override bool IsCompose => DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ComposeKeyword);

    /// <summary>
    ///     The shader bound to this slot, or null when the field is not a slot or the
    ///     binding is missing or invalid.
    /// </summary>
    /// <remarks>
    ///     Resolution is by name against the compilation's own types. Whether it succeeded is
    ///     reported once, at the declaration, by <c>ReportComposeIssues</c>; this property
    ///     stays silent so that lowering can ask it freely.
    /// </remarks>
    public override NamedTypeSymbol? ComposedType {
        get {
            if (!IsCompose || composeResolved) {
                return composedType;
            }

            composeResolved = true;

            if (ContainingType is not { } containing
                || binder.Compilation.ComposeBindings.Resolve(containing.Name, Name) is not { } boundName) {
                return null;
            }

            if (binder.Compilation.GetAllTypes().FirstOrDefault(t => t.Name == boundName) is not { } bound) {
                return null;
            }

            // The kind and protocol checks are the validation pass's job; storing the
            // resolved type either way keeps its diagnostics specific.
            return composedType = bound;
        }
    }

    /// <summary>
    ///     A permutation key is const in every sense that matters downstream: its value is
    ///     known when the shader is compiled, so folding and dead-branch elimination pick
    ///     it up without a special case. The only difference from a <c>const</c> field is
    ///     where the value comes from.
    /// </summary>
    public override bool IsConst =>
        IsPermutation || DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ConstKeyword);

    public override bool IsStatic => IsConst || DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.StaticKeyword);

    /// <summary>
    ///     Whether the source itself says the field cannot be reassigned — <c>val</c>,
    ///     <c>const</c> or <c>readonly</c>.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="IsReadOnly" />, which a <c>[Permutation]</c> marker also
    ///     forces true. Validation has to ask this one: otherwise
    ///     <c>[Permutation] var Flag</c> would report itself read-only and the diagnostic
    ///     for a mutable permutation could never fire.
    /// </remarks>
    internal bool IsDeclaredReadOnly =>
        Declaration.Keyword.Kind == SyntaxKind.ValKeyword
        || DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ConstKeyword)
        || DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ReadOnlyKeyword);

    public override bool IsReadOnly => IsConst || IsDeclaredReadOnly;

    public override Accessibility DeclaredAccessibility =>
        DeclarationFacts.GetAccessibility(syntax.Modifiers, Accessibility.Private);

    public override TypeSymbol Type => type ??= ResolveType();

    public override string? SemanticName => DeclarationFacts.GetSemanticName(syntax.AttributeLists);

    public override object? DeclaredValue =>
        Declaration.Initializer?.Value is LiteralExpressionSyntax literal ? LiteralParser.Parse(literal).Value : null;

    public override object? ConstantValue {
        get {
            if (!IsConst) {
                return null;
            }

            var declared = DeclaredValue;

            if (!IsPermutation) {
                return declared;
            }

            // Reading a permutation key is the moment the output starts depending on it,
            // so that is where the dependency is recorded — see Compilation.UsedPermutationKeys.
            binder.Compilation.RecordPermutationUse(Name);

            var supplied = binder.Compilation.PermutationValues.GetValueOrDefault(Name);
            if (supplied is null) {
                return declared;
            }

            // A mismatch is reported once, at the declaration, by ReportPermutationIssues.
            // Here it just falls back, so binding sees a well-typed value either way.
            return MatchesDeclaredType(supplied) ? supplied : declared;
        }
    }

    /// <summary>
    ///     Whether a supplied permutation value has the field's declared type.
    ///     <see cref="PermutationValues" /> has already narrowed it to bool/int/uint.
    /// </summary>
    internal bool MatchesDeclaredType(object value) =>
        (Type as PrimitiveTypeSymbol)?.SpecialType switch {
            SpecialType.Bool => value is bool,
            SpecialType.Int => value is int,
            SpecialType.UInt => value is uint,
            _ => false
        };

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

    public override ResourceSet ResourceSet =>
        DeclarationFacts.GetResourceSet(syntax.AttributeLists) ?? base.ResourceSet;

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
