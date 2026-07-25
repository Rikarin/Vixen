// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Symbols.Source;

/// <summary>
///     A type declared in source. Everything beyond its name and arity is computed
///     lazily: members are created without their signatures, and each signature
///     resolves the first time it is read. That ordering is what lets a type refer to
///     its own members while its members refer back to the type.
/// </summary>
public sealed class SourceNamedTypeSymbol : NamedTypeSymbol {
    readonly Binder outerBinder;
    readonly List<SourceNamedTypeSymbol> nestedTypes = [];

    NamedTypeSymbol? baseType;
    bool basesResolved;
    Dictionary<string, Symbol[]>? membersByName;
    Symbol[]? members;
    NamedTypeSymbol[] interfaces = [];
    bool resolvingBases;
    Binder? typeBinder;
    bool typeParameterConstraintsResolved;
    TypeParameterSymbol[]? typeParameters;

    public TypeDeclarationInfo Declaration { get; }

    public override string Name => Declaration.Name;
    public override Symbol? ContainingSymbol { get; }
    public override TypeKind TypeKind => Declaration.Kind;
    public override SyntaxNode DeclaringSyntax => Declaration.Syntax;
    public override bool IsAbstract => DeclarationFacts.Has(Declaration.Modifiers, SyntaxKind.AbstractKeyword);
    public override bool IsStatic => DeclarationFacts.Has(Declaration.Modifiers, SyntaxKind.StaticKeyword);

    public override Accessibility DeclaredAccessibility =>
        DeclarationFacts.GetAccessibility(Declaration.Modifiers, Accessibility.Internal);

    public override IReadOnlyList<TypeParameterSymbol> TypeParameters {
        get {
            EnsureTypeParameters();
            return typeParameters!;
        }
    }

    public override NamedTypeSymbol? BaseType {
        get {
            EnsureBases();
            return baseType;
        }
    }

    public override IReadOnlyList<NamedTypeSymbol> Interfaces {
        get {
            EnsureBases();
            return interfaces;
        }
    }

    /// <summary>Types declared inside this one.</summary>
    public IReadOnlyList<SourceNamedTypeSymbol> NestedTypes {
        get {
            EnsureMembers();
            return nestedTypes;
        }
    }

    /// <summary>Entry-point methods declared on this type, keyed by the stage they target.</summary>
    public IReadOnlyList<MethodSymbol> EntryPoints =>
        GetMembers().OfType<MethodSymbol>().Where(m => m.Stage != ShaderStage.None).ToArray();

    /// <summary>The scope member bodies are bound in: this type's parameters and members.</summary>
    internal Binder TypeBinder => typeBinder ??= new NamedTypeBinder(outerBinder, this);

    internal SourceNamedTypeSymbol(Symbol container, TypeDeclarationInfo declaration, Binder outerBinder) {
        ContainingSymbol = container;
        Declaration = declaration;
        this.outerBinder = outerBinder;
    }

    public override IReadOnlyList<Symbol> GetMembers() {
        EnsureMembers();
        return members!;
    }

    public override IReadOnlyList<Symbol> GetMembers(string name) {
        EnsureMembers();
        return membersByName!.GetValueOrDefault(name) ?? (IReadOnlyList<Symbol>)[];
    }

    void EnsureTypeParameters() {
        if (typeParameters is null) {
            List<TypeParameterSymbol> parameters = [];
            if (Declaration.TypeParameterList is { } list) {
                var ordinal = 0;
                foreach (var parameter in list.Parameters) {
                    // A `val` parameter is a constant, not a type: it becomes a member (see
                    // BuildMembers) and must not count towards arity, or `Blur<val N: int>`
                    // would look like it takes a type argument.
                    if (parameter.ValKeyword is not null) {
                        continue;
                    }

                    parameters.Add(new(this, parameter.Identifier.ValueText, ordinal++, parameter));
                }
            }

            typeParameters = parameters.ToArray();
        }

        if (typeParameterConstraintsResolved || typeParameters.Length == 0) {
            return;
        }

        typeParameterConstraintsResolved = true;
        ConstraintResolution.Apply(typeParameters, Declaration.ConstraintClauses, TypeBinder);
    }

    void EnsureBases() {
        if (basesResolved || resolvingBases) {
            return;
        }

        resolvingBases = true;
        try {
            List<NamedTypeSymbol> protocols = [];

            if (Declaration.BaseList is { } list) {
                foreach (var entry in list.Types) {
                    if (TypeBinder.BindType(entry.Type) is not NamedTypeSymbol resolved || resolved.IsErrorType) {
                        continue;
                    }

                    if (ReferenceEquals(resolved.OriginalDefinition, this)) {
                        outerBinder.Diagnostics.Add(
                            SemanticDiagnostics.CyclicBaseType,
                            entry.Type.GetLocation(),
                            Name
                        );
                        continue;
                    }

                    if (resolved.TypeKind == TypeKind.Protocol) {
                        protocols.Add(resolved);
                    } else if (baseType is null) {
                        baseType = resolved;
                    } else {
                        protocols.Add(resolved);
                    }
                }
            }

            interfaces = protocols.ToArray();

            // A base list that ends up pointing back at this type would make
            // member lookup loop; drop it and report once.
            if (baseType is not null && ContainsSelf(baseType)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.CyclicBaseType,
                    Declaration.Identifier.GetLocation(),
                    Name
                );
                baseType = null;
            }
        } finally {
            resolvingBases = false;
            basesResolved = true;
        }
    }

    bool ContainsSelf(NamedTypeSymbol candidate) {
        for (var current = candidate; current is not null; current = current.BaseType) {
            if (ReferenceEquals(current.OriginalDefinition, this)) {
                return true;
            }
        }

        return false;
    }

    void EnsureMembers() {
        if (members is not null) {
            return;
        }

        var built = BuildMembers();

        // Publish before validating: the checks below read member signatures,
        // which resolve types through this type's own scope.
        members = built;
        membersByName = built
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        ReportDuplicates();
        ReportShaderIssues();
    }

    Symbol[] BuildMembers() {
        List<Symbol> result = [];

        // `val` type parameters first: they are declared in the signature, so they are in
        // scope for every member below.
        if (Declaration.TypeParameterList is { } typeParameterList) {
            var ordinal = 0;
            foreach (var parameter in typeParameterList.Parameters) {
                if (parameter.ValKeyword is not null) {
                    result.Add(new SourceValueParameterSymbol(this, parameter, ordinal++, TypeBinder));
                }
            }
        }

        foreach (var member in Declaration.Members) {
            switch (member) {
                case FieldDeclarationSyntax field:
                    result.Add(new SourceFieldSymbol(this, field, TypeBinder));
                    break;

                case PropertyDeclarationSyntax property:
                    result.Add(new SourcePropertySymbol(this, property, TypeBinder));
                    break;

                case IndexerDeclarationSyntax indexer:
                    result.Add(new SourcePropertySymbol(this, indexer, TypeBinder));
                    break;

                case EnumMemberDeclarationSyntax enumMember:
                    result.Add(new SourceEnumMemberSymbol(this, enumMember, result.Count));
                    break;

                case MethodDeclarationSyntax
                    or ConstructorDeclarationSyntax
                    or DestructorDeclarationSyntax
                    or OperatorDeclarationSyntax
                    or ConversionOperatorDeclarationSyntax:
                    result.Add(new SourceMethodSymbol(this, member, TypeBinder));
                    break;

                default: {
                    if (TypeDeclarationInfo.From(member) is { } nested) {
                        var symbol = new SourceNamedTypeSymbol(this, nested, TypeBinder);
                        nestedTypes.Add(symbol);
                        result.Add(symbol);
                    }

                    break;
                }
            }
        }

        return result.ToArray();
    }

    void ReportDuplicates() {
        foreach (var group in membersByName!) {
            if (group.Value.Length < 2) {
                continue;
            }

            // Methods may share a name; anything else may not, and a method
            // cannot share a name with a non-method.
            var seenSignatures = new List<MethodSymbol>();
            var seenNonMethod = false;

            foreach (var symbol in group.Value) {
                var isDuplicate = symbol is MethodSymbol method
                    ? seenNonMethod || seenSignatures.Any(m => HaveSameSignature(m, method))
                    : seenNonMethod || seenSignatures.Count > 0;

                if (isDuplicate) {
                    ReportDuplicate(symbol);
                }

                if (symbol is MethodSymbol declared) {
                    seenSignatures.Add(declared);
                } else {
                    seenNonMethod = true;
                }
            }
        }
    }

    void ReportDuplicate(Symbol symbol) {
        var location = symbol.DeclaringSyntax?.GetLocation() ?? Location.None;
        outerBinder.Diagnostics.Add(SemanticDiagnostics.DuplicateDeclaration, location, symbol.Name);
    }

    static bool HaveSameSignature(MethodSymbol left, MethodSymbol right) {
        if (left.Parameters.Count != right.Parameters.Count || left.Arity != right.Arity) {
            return false;
        }

        for (var i = 0; i < left.Parameters.Count; i++) {
            if (!left.Parameters[i].Type.Equals(right.Parameters[i].Type)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Checks that every <c>[Permutation]</c> field can actually behave as a
    ///     compile-time constant: declared on a shader, never reassigned, of a type a
    ///     define can carry, and with a default for when no value is supplied.
    /// </summary>
    void ReportPermutationIssues() {
        foreach (var member in members!) {
            if (member is not SourceFieldSymbol { IsPermutation: true } field) {
                continue;
            }

            var location = field.DeclaringSyntax?.GetLocation() ?? Location.None;

            if (TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.PermutationMustBeShaderField,
                    location,
                    field.Name
                );
                continue;
            }

            // IsDeclaredReadOnly, not IsReadOnly: the [Permutation] marker forces the
            // latter true, so it would never report a `var` key.
            if (!field.IsDeclaredReadOnly) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.PermutationMustBeReadOnly, location, field.Name);
            }

            // bool for flags, int/uint for counts (tap counts, cascade counts, light
            // limits). Floats are deliberately excluded: they make poor cache keys and a
            // shader wanting one should take a uniform.
            var special = (field.Type as PrimitiveTypeSymbol)?.SpecialType;
            if (special is not (SpecialType.Bool or SpecialType.Int or SpecialType.UInt)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.PermutationTypeNotSupported,
                    location,
                    field.Name,
                    field.Type.ToDisplayString()
                );
                continue;
            }

            if (field.Declaration.Initializer?.Value is not LiteralExpressionSyntax) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.PermutationNeedsDefault, location, field.Name);
            }

            if (outerBinder.Compilation.PermutationValues.GetValueOrDefault(field.Name) is { } supplied
                && !field.MatchesDeclaredType(supplied)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.PermutationValueTypeMismatch,
                    location,
                    field.Name,
                    supplied.GetType() == typeof(uint) ? "uint" : supplied.GetType() == typeof(int) ? "int" : "bool",
                    field.Type.ToDisplayString()
                );
            }
        }
    }

    /// <summary>
    ///     Checks that every <c>compose</c> slot can be resolved to a concrete shader before
    ///     codegen: declared on a shader, typed against a protocol, and filled by a shader
    ///     that actually implements it.
    /// </summary>
    void ReportComposeIssues() {
        foreach (var member in members!) {
            if (member is not SourceFieldSymbol { IsCompose: true } slot) {
                continue;
            }

            var location = slot.DeclaringSyntax?.GetLocation() ?? Location.None;

            if (TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.ComposeMustBeShaderField, location, slot.Name);
                continue;
            }

            if (slot.Declaration.Initializer is not null) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.ComposeCannotHaveInitializer, location, slot.Name);
            }

            // The slot's declared type has to be a protocol: that is what lets one shader be
            // written against a feature rather than against a particular implementation.
            if (slot.Type is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                if (!slot.Type.IsErrorType) {
                    outerBinder.Diagnostics.Add(
                        SemanticDiagnostics.ComposeMustBeProtocolTyped,
                        location,
                        slot.Name,
                        slot.Type.ToDisplayString()
                    );
                }

                continue;
            }

            var boundName = outerBinder.Compilation.ComposeBindings.Resolve(Name, slot.Name);
            if (boundName is null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComposeNotBound,
                    location,
                    slot.Name,
                    protocol.ToDisplayString()
                );
                continue;
            }

            if (slot.ComposedType is not { } bound) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComposeBindingNotFound,
                    location,
                    slot.Name,
                    boundName
                );
                continue;
            }

            if (bound.TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComposeBindingMustBeShader,
                    location,
                    slot.Name,
                    bound.Name,
                    bound.TypeKind.ToString().ToLowerInvariant()
                );
                continue;
            }

            if (!Implements(bound, protocol)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComposeBindingDoesNotImplement,
                    location,
                    slot.Name,
                    bound.Name,
                    protocol.ToDisplayString()
                );
            }
        }
    }

    /// <summary>Whether <paramref name="candidate" /> lists <paramref name="protocol" />, directly or through a base.</summary>
    static bool Implements(NamedTypeSymbol candidate, NamedTypeSymbol protocol) {
        for (var current = candidate; current is not null; current = current.BaseType) {
            foreach (var declared in current.Interfaces) {
                if (declared.Equals(protocol) || Implements(declared, protocol)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Checks that every <c>val</c> type parameter can be a compile-time constant: declared
    ///     on a shader, of a type a value can carry, and actually supplied.
    /// </summary>
    void ReportValueParameterIssues() {
        foreach (var member in members!) {
            if (member is not SourceValueParameterSymbol parameter) {
                continue;
            }

            var location = parameter.DeclaringSyntax?.GetLocation() ?? Location.None;

            if (TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ValueParameterMustBeOnShader,
                    location,
                    parameter.Name
                );
                continue;
            }

            // Same restriction as a permutation key, for the same reason: these are cache
            // keys, and a float makes a poor one.
            var special = (parameter.Type as PrimitiveTypeSymbol)?.SpecialType;
            if (special is not (SpecialType.Bool or SpecialType.Int or SpecialType.UInt)) {
                if (!parameter.Type.IsErrorType) {
                    outerBinder.Diagnostics.Add(
                        SemanticDiagnostics.ValueParameterTypeNotSupported,
                        location,
                        parameter.Name,
                        parameter.Type.ToDisplayString()
                    );
                }

                continue;
            }

            var values = outerBinder.Compilation.PermutationValues;
            var supplied = values.GetValueOrDefault($"{Name}.{parameter.Name}")
                ?? values.GetValueOrDefault(parameter.Name);

            if (supplied is null) {
                // No default to fall back on: a value parameter is part of the signature.
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ValueParameterNotSupplied,
                    location,
                    parameter.Name,
                    Name
                );
                continue;
            }

            if (!parameter.MatchesDeclaredType(supplied)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ValueParameterTypeMismatch,
                    location,
                    parameter.Name,
                    supplied.GetType() == typeof(uint) ? "uint" : supplied.GetType() == typeof(int) ? "int" : "bool",
                    parameter.Type.ToDisplayString()
                );
            }
        }
    }

    void ReportShaderIssues() {
        Dictionary<ShaderStage, MethodSymbol> stages = [];

        ReportPermutationIssues();
        ReportComposeIssues();
        ReportValueParameterIssues();

        foreach (var member in members!) {
            // Textures, samplers and the like bind to the pipeline, so they only
            // make sense as shader state.
            if (member is FieldSymbol field
                && field.Type is BuiltInNamedTypeSymbol { ResourceKind: not ResourceKind.None } resource
                && TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ResourceMustBeShaderField,
                    field.DeclaringSyntax?.GetLocation() ?? Location.None,
                    resource.Name
                );
                continue;
            }

            if (member is not MethodSymbol { Stage: not ShaderStage.None } method) {
                continue;
            }

            var location = method.DeclaringSyntax?.GetLocation() ?? Location.None;

            if (TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.StageAttributeOutsideShader,
                    location,
                    method.Stage + "Shader"
                );
                continue;
            }

            if (method.Arity > 0) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.EntryPointCannotBeGeneric, location, method.Name);
            }

            if (!stages.TryAdd(method.Stage, method)) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.DuplicateEntryPoint, location, Name, method.Stage);
            }
        }
    }

    /// <summary>
    ///     Resolves everything about the declaration that is computed lazily, so its
    ///     diagnostics appear even when nothing in the program refers to it.
    /// </summary>
    internal void EnsureSignatureResolved() {
        EnsureTypeParameters();
        EnsureBases();
        EnsureMembers();
    }
}
