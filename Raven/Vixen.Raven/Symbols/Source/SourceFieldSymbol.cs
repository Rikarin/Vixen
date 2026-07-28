// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>A <c>val</c>/<c>var</c> field declared in a type body.</summary>
public sealed class SourceFieldSymbol : FieldSymbol {
    readonly Binder binder;
    readonly FieldDeclarationSyntax syntax;

    bool resolving;
    TypeSymbol? type;
    NamedTypeSymbol? composedType;
    bool composeResolved;
    object? declaredValue;
    bool declaredValueComputed;
    bool evaluatingDeclaredValue;

    public VariableDeclarationSyntax Declaration => syntax.Declaration;

    /// <summary>The declaration's attribute lists, for validation that reads them directly.</summary>
    internal SyntaxList<AttributeListSyntax> AttributeLists => syntax.AttributeLists;

    public override string Name => Declaration.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => syntax;

    public override bool IsPermutation => DeclarationFacts.IsPermutation(syntax.AttributeLists);

    public override bool IsCompose => DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.ComposeKeyword);

    public override bool IsStream => DeclarationFacts.Has(syntax.Modifiers, SyntaxKind.StreamKeyword);

    /// <summary>
    ///     The shader bound to this slot, or null when the field is not a slot or the
    ///     binding is missing or invalid.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Resolution is by name against the compilation's own types, then against the types
    ///         its referenced libraries declare — a material feature is exactly the kind of thing
    ///         that ships in <c>Raven/Library</c>, so a slot filled by a library shader is the
    ///         ordinary case rather than an exotic one.
    ///     </para>
    ///     <para>
    ///         Whether it succeeded is reported once, at the declaration, by
    ///         <c>ReportComposeIssues</c>; this property stays silent so that lowering can ask it
    ///         freely.
    ///     </para>
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

            // Source before references: a compilation that declares its own implementation of a
            // feature means that one, which is the same precedence a shadowed name gets.
            var bound = Find(binder.Compilation.GetAllTypes(), boundName)
                ?? Find(binder.Compilation.GetReferencedTypes(), boundName);

            if (bound is null) {
                return null;
            }

            // The kind and protocol checks are the validation pass's job; storing the
            // resolved type either way keeps its diagnostics specific.
            return composedType = bound;
        }
    }

    static NamedTypeSymbol? Find(IEnumerable<NamedTypeSymbol> types, string name) {
        foreach (var type in types) {
            if (string.Equals(type.Name, name, StringComparison.Ordinal)) {
                return type;
            }
        }

        return null;
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

    public override TypeSymbol Type => type ??= ResolveType();

    public override string? SemanticName => DeclarationFacts.GetSemanticName(syntax.AttributeLists);

    public override object? DeclaredValue {
        get {
            if (Declaration.Initializer?.Value is not { } initializer) {
                return null;
            }

            // The literal fast path covers most fields, and it is all a non-const
            // field's host-side default supports anyway.
            if (initializer is LiteralExpressionSyntax literal) {
                return LiteralParser.Parse(literal).Value;
            }

            if (!IsConst) {
                return null;
            }

            // A const may be initialized by any constant expression — `-1`,
            // `TapCount * 2`, another const — evaluated once and cached.
            if (declaredValueComputed) {
                return declaredValue;
            }

            if (evaluatingDeclaredValue) {
                binder.Diagnostics.Add(
                    SemanticDiagnostics.CircularDefinition,
                    Declaration.Identifier.GetLocation(),
                    Name
                );
                return null;
            }

            evaluatingDeclaredValue = true;
            try {
                declaredValue = ConstantEvaluator.Evaluate(binder.BindValue(initializer));
                declaredValueComputed = true;
                return declaredValue;
            } finally {
                evaluatingDeclaredValue = false;
            }
        }
    }

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
            // A stream is per-invocation, so it is never a binding however numeric it looks —
            // checked before the resource types, so `stream var t: Texture2D` reports the type
            // restriction (RVN2103) rather than quietly becoming a texture binding.
            if (IsStream) {
                return ResourceKind.None;
            }

            if (Type.ResourceKind is not ResourceKind.None) {
                return Type.ResourceKind;
            }

            // Everything else a shader binds is a member of its uniform block, whatever shape it is.
            // This used to ask `IsNumericLike`, which is true of a scalar, a vector and a matrix and
            // of nothing else — so a struct and an array of structs reported "not a binding" while
            // the lowerer went on making them uniforms. The two answers being different is the exact
            // thing `IsBinding` documents itself as preventing, and it surfaced as `[PerDraw] var
            // lights: PunctualLight[MaxLights]` being told its marker had no effect.
            return IsBinding ? ResourceKind.Uniform : ResourceKind.None;
        }
    }

    public override ResourceSet ResourceSet =>
        DeclarationFacts.GetResourceSet(syntax.AttributeLists) ?? base.ResourceSet;

    public override bool IsPushConstant => DeclarationFacts.IsPushConstant(syntax.AttributeLists);

    public override ImageFormat? ImageFormat =>
        ImageFormats.Lookup(DeclarationFacts.GetImageFormat(syntax.AttributeLists));

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
                var bound = binder.BindType(annotation);

                // A storage image's texel format is part of its type in SPIR-V, and it is spelled
                // as an attribute on the declaration — so it is folded in here rather than left for
                // the backends to look it back up off the symbol.
                return bound is StorageImageTypeSymbol image ? image.WithFormat(ImageFormat?.Name) : bound;
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
    readonly Binder binder;
    readonly EnumMemberDeclarationSyntax syntax;

    object? constantValue;
    bool constantComputed;
    bool evaluating;

    /// <summary>Declaration order. The value continues from the previous member, C-style.</summary>
    public int Ordinal { get; }

    public override string Name => syntax.Identifier.ValueText;
    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => syntax;
    public override TypeSymbol Type => (TypeSymbol)ContainingSymbol!;
    public override bool IsConst => true;
    public override bool IsReadOnly => true;
    public override bool IsStatic => true;

    /// <summary>
    ///     The member's value: its initializer evaluated as a constant expression, or the
    ///     previous member's value plus one — <c>A, B = 5, C</c> makes <c>C</c> six. An
    ///     initializer that is not a compile-time integer is <c>RVN2094</c>; a cycle
    ///     (<c>A = B, B = A</c>) is a circular-definition error. Both fall back to 0 so
    ///     downstream phases still see a well-typed constant.
    /// </summary>
    public override object? ConstantValue {
        get {
            if (constantComputed) {
                return constantValue;
            }

            if (evaluating) {
                binder.Diagnostics.Add(
                    SemanticDiagnostics.CircularDefinition,
                    syntax.Identifier.GetLocation(),
                    Name
                );
                return 0;
            }

            evaluating = true;
            try {
                constantValue = Evaluate();
                constantComputed = true;
                return constantValue;
            } finally {
                evaluating = false;
            }
        }
    }

    internal SourceEnumMemberSymbol(
        NamedTypeSymbol containingType,
        EnumMemberDeclarationSyntax syntax,
        int ordinal,
        Binder binder
    ) {
        ContainingSymbol = containingType;
        this.syntax = syntax;
        Ordinal = ordinal;
        this.binder = binder;
    }

    object Evaluate() {
        if (syntax.Value?.Value is not { } initializer) {
            return Ordinal == 0 ? 0 : Successor(PreviousMember()?.ConstantValue);
        }

        var value = ConstantEvaluator.Evaluate(binder.BindValue(initializer));
        if (value is int or uint) {
            return value;
        }

        binder.Diagnostics.Add(
            SemanticDiagnostics.EnumMemberValueNotConstant,
            syntax.Identifier.GetLocation(),
            Name
        );
        return 0;
    }

    SourceEnumMemberSymbol? PreviousMember() {
        foreach (var member in ((NamedTypeSymbol)ContainingSymbol!).GetMembers()) {
            if (member is SourceEnumMemberSymbol candidate && candidate.Ordinal == Ordinal - 1) {
                return candidate;
            }
        }

        return null;
    }

    static object Successor(object? previous) =>
        previous switch {
            int value => value + 1,
            uint value => value + 1,
            _ => 0
        };
}
