// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;

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
    bool validated;

    public TypeDeclarationInfo Declaration { get; }

    public override string Name => Declaration.Name;
    public override Symbol? ContainingSymbol { get; }
    public override TypeKind TypeKind => Declaration.Kind;
    public override SyntaxNode DeclaringSyntax => Declaration.Syntax;
    public override bool IsStatic => DeclarationFacts.Has(Declaration.Modifiers, SyntaxKind.StaticKeyword);

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

        // Force every enum member's value, so a bad initializer (RVN2094) or a
        // cycle is reported even when nothing in the program reads the member.
        foreach (var member in built) {
            if (member is SourceEnumMemberSymbol enumMember) {
                _ = enumMember.ConstantValue;
            }
        }
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

                case EnumMemberDeclarationSyntax enumMember:
                    result.Add(new SourceEnumMemberSymbol(this, enumMember, result.Count, TypeBinder));
                    break;

                case MethodDeclarationSyntax
                    or ConstructorDeclarationSyntax
                    or OperatorDeclarationSyntax:
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

            // An initializer names this slot's default rather than giving it a value, so the only shape
            // it can take is a bare identifier. Anything else — a literal, a call, a qualified name —
            // would be an expression, and there is nothing here to evaluate one into.
            if (slot.Declaration.Initializer is { Value: not IdentifierNameSyntax }) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.ComposeDefaultMustBeShaderName, location, slot.Name);
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

            // The compilation's binding, then the slot's own default — the same order
            // SourceFieldSymbol.ComposedType resolves them in, and it has to be: a slot reported unbound
            // here while lowering happily composed its default would be a shader that cannot be compiled
            // and works.
            var boundName = outerBinder.Compilation.ComposeBindings.Resolve(Name, slot.Name)
                ?? slot.DefaultComposition;

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

    /// <summary>
    ///     Checks that every <c>stream</c> field can actually be threaded between stages: declared
    ///     on a shader, not also a constant or a slot, without an initializer, and of a type a
    ///     stage interface can carry.
    /// </summary>
    /// <remarks>
    ///     The type check is here rather than left to the backends' <c>RVN4001</c>, because the
    ///     declaration is what has to change and because both backends would otherwise report it
    ///     twice with no source span between them.
    /// </remarks>
    void ReportStreamIssues() {
        foreach (var member in members!) {
            if (member is not SourceFieldSymbol { IsStream: true } stream) {
                continue;
            }

            var location = stream.DeclaringSyntax?.GetLocation() ?? Location.None;

            if (TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.StreamMustBeShaderField, location, stream.Name);
                continue;
            }

            var conflict = stream switch {
                { IsPermutation: true } => "a [Permutation] key",
                { IsCompose: true } => "a compose slot",
                { IsConst: true } => "const",
                _ => null
            };

            if (conflict is not null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.StreamCannotBeConstant,
                    location,
                    stream.Name,
                    conflict
                );
                continue;
            }

            if (stream.Declaration.Initializer is not null) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.StreamCannotHaveInitializer, location, stream.Name);
            }

            // Vulkan has no boolean interface type, and an aggregate would need a location per
            // leaf. Exactly the restriction the existing stage inputs and outputs live under.
            if (!IsStageInterfaceType(stream.Type) && !stream.Type.IsErrorType) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.StreamTypeNotSupported,
                    location,
                    stream.Name,
                    stream.Type.ToDisplayString()
                );
            }
        }
    }

    /// <summary>
    ///     Checks that every <c>groupshared</c> field is storage a workgroup can actually have:
    ///     declared on a shader, not also something that decides where the value lives, writable,
    ///     without an initializer, and of a type that is a value rather than a descriptor.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shape mirrors <see cref="ReportStreamIssues" /> because the two declarations are
    ///         siblings: both are shader members that are deliberately <em>not</em> bindings, so
    ///         both need saying what they may be instead of leaving <c>IsBinding</c> to answer.
    ///     </para>
    ///     <para>
    ///         What is <em>not</em> checked here is the stage. Whether a group-shared variable is
    ///         reached by a compute entry point or a fragment one is a question about the call
    ///         graph, which the symbol table cannot see; lowering answers it once the bodies exist
    ///         (<c>RVN3012</c>), the same division <c>stream</c> already lives under.
    ///     </para>
    /// </remarks>
    void ReportGroupSharedIssues() {
        foreach (var member in members!) {
            if (member is not SourceFieldSymbol { IsGroupShared: true } shared) {
                continue;
            }

            var location = shared.DeclaringSyntax?.GetLocation() ?? Location.None;

            if (TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.GroupSharedMustBeShaderField,
                    location,
                    shared.Name
                );
                continue;
            }

            var conflict = shared switch {
                { IsPermutation: true } => "a [Permutation] key",
                { IsCompose: true } => "a compose slot",
                { IsStream: true } => "a stream",
                { IsConst: true } => "const",
                _ => null
            };

            if (conflict is not null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.GroupSharedConflict,
                    location,
                    shared.Name,
                    conflict
                );
                continue;
            }

            if (shared.IsDeclaredReadOnly) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.GroupSharedCannotBeReadOnly,
                    location,
                    shared.Name
                );
            }

            if (shared.Declaration.Initializer is not null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.GroupSharedCannotHaveInitializer,
                    location,
                    shared.Name
                );
            }

            // A descriptor is not a value, so there is no `Workgroup` variable either target could
            // declare for one. Everything else — a scalar, a vector, a matrix, a struct, an array
            // of those — is memory, which is exactly what this storage class holds.
            if (shared.Type.ResourceKind is not ResourceKind.None && !shared.Type.IsErrorType) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.GroupSharedTypeNotSupported,
                    location,
                    shared.Name,
                    shared.Type.ToDisplayString()
                );
            }
        }
    }

    /// <summary>Whether a type can cross a stage boundary: a non-boolean scalar or vector.</summary>
    static bool IsStageInterfaceType(TypeSymbol type) =>
        type is PrimitiveTypeSymbol { TypeKind: TypeKind.Scalar or TypeKind.Vector } primitive
        && primitive.ComponentSpecialType != SpecialType.Bool;

    /// <summary>
    ///     Checks the descriptor-set markers: at most one per field, and only on a field that
    ///     actually becomes a binding.
    /// </summary>
    /// <remarks>
    ///     A marker on a <c>const</c>, a <c>[Permutation]</c> key or a <c>compose</c> slot is a
    ///     warning rather than an error — the shader is still correct, but the author believes
    ///     something about where that value lives that is not true.
    /// </remarks>
    void ReportResourceSetIssues() {
        foreach (var member in members!) {
            if (member is not SourceFieldSymbol field) {
                continue;
            }

            var markers = DeclarationFacts.GetResourceSets(field.AttributeLists).ToArray();
            var location = field.DeclaringSyntax?.GetLocation() ?? Location.None;

            ReportImageFormatIssues(field, location);

            if (field.IsPushConstant) {
                ReportPushConstantIssues(field, markers, location);
                continue;
            }

            if (markers.Length == 0) {
                continue;
            }

            if (markers.Length > 1) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ResourceSetConflict,
                    location,
                    field.Name,
                    markers[0].Name,
                    markers[1].Name
                );
            }

            if (field.ResourceKind == ResourceKind.None) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ResourceSetOnNonBinding,
                    location,
                    field.Name,
                    markers[0].Name
                );
            }
        }
    }

    /// <summary>
    ///     Checks that no binding's type contains a <c>bool</c> — <c>RVN2137</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A binding lands in <c>Uniform</c>, <c>StorageBuffer</c> or <c>PushConstant</c>, and
    ///         SPIR-V allows <c>OpTypeBool</c> in none of the three. Nothing said so: the module
    ///         generated, the backend reported nothing, and <c>spirv-val</c> rejected it — which is
    ///         to say a driver would have.
    ///     </para>
    ///     <para>
    ///         ⚠ Into the type rather than at it. A <c>bool</c> reaches a uniform block as a member
    ///         of a struct or an element of an array just as easily as by being the field's own
    ///         type, and the validator's complaint is about the block either way.
    ///     </para>
    ///     <para>
    ///         A <c>[Permutation]</c> key is not a binding and never reaches a storage class at all —
    ///         it is folded at every use — so it is untouched here, which is what leaves the shipped
    ///         library's thirty-odd boolean keys alone.
    ///     </para>
    /// </remarks>
    void ReportBooleanBindingIssues() {
        foreach (var member in members!) {
            if (member is not SourceFieldSymbol { IsBinding: true } field || field.Type.IsErrorType) {
                continue;
            }

            if (ContainsBoolean(field.Type, 0)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.BindingCannotBeBoolean,
                    field.DeclaringSyntax?.GetLocation() ?? Location.None,
                    field.Name,
                    field.Type.ToDisplayString()
                );
            }
        }
    }

    /// <summary>Whether a boolean is anywhere inside a type.</summary>
    /// <remarks>
    ///     Depth-bounded because a struct can reach itself through a field the binder has already
    ///     reported (<c>RVN2073</c>), and this runs before that stops being possible.
    /// </remarks>
    static bool ContainsBoolean(TypeSymbol type, int depth) {
        if (depth > 16) {
            return false;
        }

        switch (type) {
            case PrimitiveTypeSymbol primitive:
                return primitive.ComponentSpecialType == SpecialType.Bool;

            case ArrayTypeSymbol array:
                return ContainsBoolean(array.ElementType, depth + 1);

            // A resource is opaque — a texture holds no members a host writes — and a shader or a
            // protocol is not a value. Only a struct has members that reach the block.
            case NamedTypeSymbol { TypeKind: TypeKind.Struct } structure:
                foreach (var member in structure.GetMembers()) {
                    if (member is FieldSymbol { IsStatic: false } field
                        && !field.Type.IsErrorType
                        && ContainsBoolean(field.Type, depth + 1)) {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    ///     Checks a storage image's <c>[Format]</c>: present, recognised, and agreeing with the
    ///     element type the image is declared with.
    /// </summary>
    /// <remarks>
    ///     All three are errors rather than guesses, because there is nothing honest to guess. The
    ///     host creates the view; only the author knows what it holds, and a format that disagrees
    ///     with the element type is a read that returns whatever the driver felt like converting.
    /// </remarks>
    void ReportImageFormatIssues(SourceFieldSymbol field, Location location) {
        if (field.Type is not StorageImageTypeSymbol image) {
            if (DeclarationFacts.HasFormat(field.AttributeLists)) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.FormatOnNonImage, location, field.Name);
            }

            return;
        }

        var declared = DeclarationFacts.GetImageFormat(field.AttributeLists);

        if (declared is null) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.StorageImageNeedsFormat,
                location,
                field.Name,
                ImageFormats.Names
            );

            return;
        }

        if (ImageFormats.Lookup(declared) is not { } format) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.StorageImageFormatMismatch,
                location,
                field.Name,
                declared,
                $"is not a format Raven admits. One of: {ImageFormats.Names}"
            );

            return;
        }

        if (!ImageFormats.ElementType(format).Equals(image.ElementType)) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.StorageImageFormatMismatch,
                location,
                field.Name,
                declared,
                $"is read as '{ImageFormats.ElementType(format).ToDisplayString()}' rather than the "
                + $"'{image.ElementType.ToDisplayString()}' it is declared with"
            );
        }
    }

    /// <summary>
    ///     Checks a <c>[PushConstant]</c> field: it has to be a value, and a descriptor-set marker
    ///     on it says something untrue.
    /// </summary>
    /// <remarks>
    ///     A marked field that is not a binding at all — a <c>const</c>, a <c>compose</c> slot, a
    ///     <c>stream</c> — takes the same <c>RVN2091</c> treatment every other misplaced marker
    ///     does, because the value does not reach the pipeline by any route.
    /// </remarks>
    void ReportPushConstantIssues(
        SourceFieldSymbol field,
        (string Name, ResourceSet Set)[] markers,
        Location location
    ) {
        if (markers.Length > 0) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.PushConstantHasNoSet,
                location,
                field.Name,
                markers[0].Name
            );
        }

        if (!field.IsBinding) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.ResourceSetOnNonBinding,
                location,
                field.Name,
                "PushConstant"
            );
            return;
        }

        if (field.ResourceKind is not (ResourceKind.None or ResourceKind.Uniform)) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.PushConstantMustBeAValue,
                location,
                field.Name,
                field.Type.ToDisplayString()
            );
        }
    }

    /// <summary>
    ///     Warns about modifiers that are legal syntax but change nothing where they
    ///     stand — <c>override</c> on a field, <c>compose</c> on a method, anything on
    ///     a type or an <c>init</c>. Same policy as a misplaced descriptor-set marker
    ///     (<c>RVN2091</c>): the code still compiles, but the author believes something
    ///     untrue, so it is named rather than silently ignored.
    /// </summary>
    void ReportModifierIssues() {
        ReportUselessModifiers(Declaration.Modifiers, [], "type", Name, Declaration.Syntax);

        foreach (var member in members!) {
            if (member.DeclaringSyntax is not MemberDeclarationSyntax declaration) {
                continue;
            }

            var (allowed, description) = member switch {
                FieldSymbol =>
                    (new[] { SyntaxKind.ConstKeyword, SyntaxKind.ReadOnlyKeyword, SyntaxKind.StaticKeyword,
                        SyntaxKind.ComposeKeyword, SyntaxKind.StreamKeyword,
                        SyntaxKind.GroupSharedKeyword }, "field"),
                MethodSymbol { MethodKind: MethodKind.Constructor } => ([], "constructor"),
                MethodSymbol { MethodKind: MethodKind.Operator } =>
                    (new[] { SyntaxKind.StaticKeyword }, "operator"),
                MethodSymbol => (new[] { SyntaxKind.StaticKeyword, SyntaxKind.OverrideKeyword }, "method"),
                PropertySymbol => (new[] { SyntaxKind.StaticKeyword, SyntaxKind.OverrideKeyword }, "property"),
                // A nested type reports its own modifiers when its own checks run.
                _ => (Array.Empty<SyntaxKind>(), (string?)null)
            };

            if (description is not null) {
                ReportUselessModifiers(declaration.Modifiers, allowed, description, member.Name, declaration);
            }

            if (member is MethodSymbol method) {
                ReportParameterDirectionIssues(method);
            }
        }
    }

    /// <summary>
    ///     Checks <c>inout</c> against the things it cannot combine with.
    /// </summary>
    /// <remarks>
    ///     Here rather than on the parameter symbol because two of the three rules are about the
    ///     *member* — an operator has no syntax for a by-reference argument, and an entry point is
    ///     called by the pipeline. The entry-point case is checked again for a shader in
    ///     <c>ReportShaderIssues</c>'s path; this catches the stage attribute wherever it sits.
    /// </remarks>
    void ReportParameterDirectionIssues(MethodSymbol method) {
        foreach (var parameter in method.Parameters) {
            if (parameter.RefKind != RefKind.InOut) {
                continue;
            }

            var location = parameter.DeclaringSyntax?.GetLocation()
                           ?? method.DeclaringSyntax?.GetLocation()
                           ?? Location.None;

            if (parameter.HasDefaultValue) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.InOutCannotHaveDefault,
                    location,
                    parameter.Name
                );
            }

            if (method.MethodKind == MethodKind.Operator) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.InOutOnOperator,
                    location,
                    parameter.Name,
                    method.Name
                );
            }

            if (method.Stage != ShaderStage.None) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.InOutOnEntryPoint,
                    location,
                    parameter.Name,
                    method.Name
                );
            }
        }
    }

    /// <summary>
    ///     Warns about an attribute whose name the compiler does not read — <c>RVN2138</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Same policy as a useless modifier, and the same shape of walk, because it is the same
    ///         mistake seen through the other half of the declaration's syntax. What makes it worth
    ///         more than the modifier case is that a dropped attribute can <em>change the meaning</em>
    ///         of the declaration rather than merely add nothing to it: <c>[D] val UseSoftKnee: bool</c>
    ///         is not <c>[Permutation] val UseSoftKnee: bool</c> with a note missing, it is a uniform.
    ///     </para>
    ///     <para>
    ///         ⚠ Parameters as well as members. A <c>[Semantic]</c> is written on a parameter far more
    ///         often than anywhere else — it is how every graphics entry point names its inputs — so a
    ///         sweep that missed them would miss the place the typo is most likely to be made.
    ///     </para>
    ///     <para>
    ///         A nested type reports its own attributes when its own checks run, exactly as it does
    ///         for its modifiers, so this walk does not descend into one.
    ///     </para>
    /// </remarks>
    void ReportAttributeIssues() {
        ReportUnknownAttributes(Declaration.AttributeLists);

        foreach (var member in members!) {
            if (member is NamedTypeSymbol) {
                continue;
            }

            if (member.DeclaringSyntax is MemberDeclarationSyntax declaration) {
                ReportUnknownAttributes(declaration.AttributeLists);
            }

            if (member is not MethodSymbol method) {
                continue;
            }

            foreach (var parameter in method.Parameters) {
                if (parameter.DeclaringSyntax is ParameterSyntax syntax) {
                    ReportUnknownAttributes(syntax.AttributeLists);
                }
            }
        }
    }

    void ReportUnknownAttributes(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in DeclarationFacts.GetAttributes(attributeLists)) {
            var name = DeclarationFacts.GetAttributeName(attribute);

            if (!DeclarationFacts.IsKnownAttributeName(name)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.AttributeNotRecognised,
                    attribute.GetLocation(),
                    name,
                    DeclarationFacts.KnownAttributeNamesText
                );
            }
        }
    }

    void ReportUselessModifiers(
        SyntaxList<SyntaxToken> modifiers,
        SyntaxKind[] allowed,
        string description,
        string name,
        SyntaxNode declaration
    ) {
        foreach (var modifier in modifiers) {
            if (!allowed.Contains(modifier.Kind)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ModifierHasNoEffect,
                    declaration.GetLocation(),
                    SyntaxFacts.GetText(modifier.Kind),
                    description,
                    name
                );
            }
        }
    }

    void ReportShaderIssues() {
        Dictionary<ShaderStage, MethodSymbol> stages = [];

        ReportPermutationIssues();
        ReportComposeIssues();
        ReportValueParameterIssues();
        ReportStreamIssues();
        ReportGroupSharedIssues();
        ReportResourceSetIssues();
        ReportBooleanBindingIssues();
        ReportModifierIssues();
        ReportAttributeIssues();

        foreach (var member in members!) {
            // A shader is the pipeline, not a value: nothing constructs one, so an `init`
            // would be lowered and then dropped without ever running.
            if (TypeKind == TypeKind.Shader && member is MethodSymbol { MethodKind: MethodKind.Constructor }) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ShaderCannotBeConstructed,
                    member.DeclaringSyntax?.GetLocation() ?? Location.None,
                    Name
                );
                continue;
            }

            // Textures, samplers and the like bind to the pipeline, so they only
            // make sense as shader state.
            if (member is FieldSymbol { Type.ResourceKind: not ResourceKind.None } field
                && TypeKind != TypeKind.Shader) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ResourceMustBeShaderField,
                    field.DeclaringSyntax?.GetLocation() ?? Location.None,

                    // ToDisplayString, not Name. An array type has no name — `Name` is deliberately
                    // empty for a symbol nobody declared — so a `Texture2D[]` in a struct reported
                    // "A resource of type '' can only be declared as a shader field", which names
                    // the rule and not the field that broke it.
                    field.Type.ToDisplayString()
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

            ReportWorkgroupSizeIssues(method, location);

            if (method.Stage == ShaderStage.Compute) {
                ReportComputeInterfaceIssues(method, location);
            } else {
                ReportStageBuiltInIssues(method);
            }

            if (!stages.TryAdd(method.Stage, method)) {
                outerBinder.Diagnostics.Add(SemanticDiagnostics.DuplicateEntryPoint, location, Name, method.Stage);
            }
        }
    }

    /// <summary>
    ///     Checks the workgroup size against the stage: a compute stage needs one, and no other
    ///     stage has anywhere to put one.
    /// </summary>
    void ReportWorkgroupSizeIssues(MethodSymbol method, Location location) {
        var size = method.WorkgroupSize;

        if (method.Stage != ShaderStage.Compute) {
            if (size is not null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.WorkgroupSizeOnGraphicsStage,
                    location,
                    method.Name,
                    method.Stage.ToString().ToLowerInvariant()
                );
            }

            return;
        }

        if (size is null) {
            outerBinder.Diagnostics.Add(SemanticDiagnostics.ComputeNeedsWorkgroupSize, location, method.Name);
            return;
        }

        if (size.Value.IsInvalid) {
            outerBinder.Diagnostics.Add(SemanticDiagnostics.WorkgroupSizeNotValid, location, method.Name);
        }
    }

    /// <summary>
    ///     Checks the type of a graphics-stage parameter that names a pipeline-supplied value.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the recognised ones, and that asymmetry with the compute check is the point: a
    ///         graphics stage's parameter list is mostly vertex attributes the host feeds, so an
    ///         unrecognised semantic is <c>POSITION</c> or <c>TEXCOORD0</c> rather than a mistake.
    ///         A compute stage has no attributes at all, so its table is closed and an unknown
    ///         semantic there is <c>RVN2108</c>.
    ///     </para>
    ///     <para>
    ///         The type is refused rather than converted, exactly as it is for a dispatch id:
    ///         <c>SV_VertexID</c> is signed in both targets, so declaring it <c>uint</c> would put
    ///         a conversion nobody wrote between the built-in and its first use.
    ///     </para>
    /// </remarks>
    void ReportStageBuiltInIssues(MethodSymbol method) {
        foreach (var parameter in method.Parameters) {
            if (StageBuiltIns.Of(parameter.SemanticName, method.Stage) is not { } builtIn) {
                continue;
            }

            if (!parameter.Type.IsErrorType && !ReferenceEquals(parameter.Type, builtIn.Type)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComputeSemanticTypeMismatch,
                    parameter.DeclaringSyntax?.GetLocation() ?? Location.None,
                    builtIn.Semantic,
                    builtIn.Type.ToDisplayString(),
                    parameter.Name,
                    parameter.Type.ToDisplayString()
                );
            }
        }
    }

    /// <summary>
    ///     Checks that a compute entry point asks for nothing the stage cannot give it.
    /// </summary>
    /// <remarks>
    ///     A compute stage has no pipeline interface at all — no attributes feeding its
    ///     parameters, no framebuffer taking its result — so every parameter has to be a dispatch
    ///     built-in and the return type has to be void. Reported at the declaration rather than
    ///     left to a backend, because what has to change is the signature.
    /// </remarks>
    void ReportComputeInterfaceIssues(MethodSymbol method, Location location) {
        if (!ReferenceEquals(method.ReturnType, BuiltInTypes.Void) && !method.ReturnType.IsErrorType) {
            outerBinder.Diagnostics.Add(
                SemanticDiagnostics.ComputeHasNoStageInterface,
                location,
                $"A return type of '{method.ReturnType.ToDisplayString()}'",
                method.Name,
                "framebuffer to write it to; write the result to a resource instead"
            );
        }

        foreach (var parameter in method.Parameters) {
            var parameterLocation = parameter.DeclaringSyntax?.GetLocation() ?? location;

            if (parameter.SemanticName is null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComputeHasNoStageInterface,
                    parameterLocation,
                    $"The parameter '{parameter.Name}'",
                    method.Name,
                    $"vertex attributes to feed it; mark it with a dispatch built-in "
                    + $"({StageBuiltIns.Names(ShaderStage.Compute)})"
                );
                continue;
            }

            var builtIn = StageBuiltIns.Of(parameter.SemanticName, ShaderStage.Compute);

            if (builtIn is null) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.UnknownComputeSemantic,
                    parameterLocation,
                    parameter.SemanticName,
                    StageBuiltIns.Names(ShaderStage.Compute)
                );
                continue;
            }

            var expected = builtIn.Type;

            // An error type was already reported as unresolved; a second diagnostic about its
            // shape would only send the author somewhere else.
            if (!parameter.Type.IsErrorType && !ReferenceEquals(parameter.Type, expected)) {
                outerBinder.Diagnostics.Add(
                    SemanticDiagnostics.ComputeSemanticTypeMismatch,
                    parameterLocation,
                    parameter.SemanticName,
                    expected.ToDisplayString(),
                    parameter.Name,
                    parameter.Type.ToDisplayString()
                );
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

    /// <summary>
    ///     Runs the checks that read <em>other</em> types, once every declaration is resolved.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="EnsureSignatureResolved" /> because validation must never be
    ///         a side effect of resolution. It was: <c>EnsureMembers</c> ran these, so resolving one
    ///         shader's base list could reach a second shader's compose check, which asked the first
    ///         for its interfaces while <c>EnsureBases</c> was still mid-flight. The reentrancy guard
    ///         answered with the empty list it had so far, and a shader that plainly implemented its
    ///         protocol was reported as not implementing it — <c>RVN2076</c> on correct source.
    ///     </para>
    ///     <para>
    ///         The invariant that fixes it is one line long: nothing reachable from resolution
    ///         validates. Then a check asking another type for its bases either finds them resolved
    ///         or resolves them, and neither can re-enter.
    ///     </para>
    /// </remarks>
    internal void EnsureValidated() {
        if (validated) {
            return;
        }

        validated = true;
        EnsureSignatureResolved();
        ReportShaderIssues();
    }
}
