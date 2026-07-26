// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Artefacts;

namespace Vixen.Raven.Symbols.Metadata;

/// <summary>A field, enum member or <c>val</c> type parameter read out of a compiled library.</summary>
/// <remarks>
///     <para>
///         Every flag the binder and the lowerer branch on round-trips: <see cref="IsConst" /> and
///         <see cref="IsCompose" /> because they decide which fields a constructor-less struct is
///         built from and which become bindings, <see cref="ResourceKind" /> and
///         <see cref="ResourceSet" /> because they decide where a binding lands.
///     </para>
///     <para>
///         <see cref="ConstantValue" /> answers with the declared value, unconditionally — where a
///         source field distinguishes it from <see cref="DeclaredValue" /> so that reading a
///         permutation key records a use. A library's keys are not this compilation's: the
///         consuming shader declares its own, so there is no use to record and the two properties
///         coincide.
///     </para>
/// </remarks>
public sealed class MetadataFieldSymbol : FieldSymbol {
    readonly MetadataNamedTypeSymbol containingType;
    readonly LibraryField model;

    TypeSymbol? type;

    public override string Name => model.Name;
    public override Symbol? ContainingSymbol => containingType;
    public override TypeSymbol Type => type ??= containingType.Resolve(model.Type);
    public override bool IsStatic => model.IsStatic;
    public override bool IsReadOnly => model.IsReadOnly;
    public override bool IsConst => model.IsConst;
    public override bool IsPermutation => model.IsPermutation;
    public override bool IsValueParameter => model.IsValueParameter;
    public override bool IsCompose => model.IsCompose;
    public override object? DeclaredValue => model.DeclaredValue?.ToObject();
    public override object? ConstantValue => IsConst ? DeclaredValue : null;
    public override ResourceKind ResourceKind => model.ResourceKind;
    public override ResourceSet ResourceSet => model.ResourceSet;
    public override string? SemanticName => model.SemanticName;

    internal MetadataFieldSymbol(MetadataNamedTypeSymbol containingType, LibraryField model) {
        this.containingType = containingType;
        this.model = model;
    }
}

/// <summary>A callable read out of a compiled library.</summary>
public sealed class MetadataMethodSymbol : MethodSymbol {
    readonly MetadataNamedTypeSymbol containingType;
    readonly LibraryMethod model;
    readonly TypeParameterSymbol[] typeParameters;

    ParameterSymbol[]? parameters;
    TypeSymbol? returnType;

    public override string Name => model.Name;
    public override Symbol? ContainingSymbol => containingType;
    public override MethodKind MethodKind => model.MethodKind;
    public override bool IsStatic => model.IsStatic;
    public override ShaderStage Stage => model.Stage;
    public override string? SemanticName => model.SemanticName;
    public override TypeSymbol ReturnType => returnType ??= containingType.Resolve(model.ReturnType, this);
    public override IReadOnlyList<TypeParameterSymbol> TypeParameters => typeParameters;

    public override IReadOnlyList<ParameterSymbol> Parameters =>
        parameters ??= model.Parameters
            .OrderBy(p => p.Ordinal)
            .Select(p => (ParameterSymbol)new MetadataParameterSymbol(this, containingType, p))
            .ToArray();

    /// <summary>
    ///     The IR function this method's body lowered to, or null when there is nothing to link.
    /// </summary>
    /// <remarks>
    ///     Null is the ordinary case for a protocol's declaration, which is bodyless and is
    ///     exactly what a <c>compose</c> slot binds against — the implementation comes from the
    ///     shader the slot resolves to.
    /// </remarks>
    public string? IrFunctionName => model.IrFunction;

    internal MetadataMethodSymbol(MetadataNamedTypeSymbol containingType, LibraryMethod model) {
        this.containingType = containingType;
        this.model = model;

        typeParameters = model.TypeParameters
            .OrderBy(p => p.Ordinal)
            .Select(p => new TypeParameterSymbol(this, p.Name, p.Ordinal))
            .ToArray();

        foreach (var parameter in typeParameters) {
            var declared = model.TypeParameters.First(p => p.Ordinal == parameter.Ordinal);
            if (!declared.Constraints.IsDefaultOrEmpty) {
                parameter.SetConstraintTypes([.. declared.Constraints.Select(c => containingType.Resolve(c, this))]);
            }
        }
    }
}

/// <summary>A parameter of a library method.</summary>
public sealed class MetadataParameterSymbol : ParameterSymbol {
    readonly MetadataNamedTypeSymbol declaringType;
    readonly MetadataMethodSymbol method;
    readonly LibraryParameter model;

    TypeSymbol? type;

    public override string Name => model.Name;
    public override Symbol? ContainingSymbol => method;
    public override int Ordinal => model.Ordinal;
    public override bool HasDefaultValue => model.HasDefaultValue;
    public override object? DefaultValue => model.DefaultValue?.ToObject();
    public override string? SemanticName => model.SemanticName;

    // Through the method, so a parameter typed by the method's own `T` resolves against it.
    public override TypeSymbol Type => type ??= declaringType.Resolve(model.Type, method);

    internal MetadataParameterSymbol(
        MetadataMethodSymbol method,
        MetadataNamedTypeSymbol declaringType,
        LibraryParameter model
    ) {
        this.method = method;
        this.declaringType = declaringType;
        this.model = model;
    }
}

/// <summary>A property read out of a compiled library.</summary>
public sealed class MetadataPropertySymbol : PropertySymbol {
    readonly MetadataNamedTypeSymbol containingType;
    readonly LibraryProperty model;

    TypeSymbol? type;

    public override string Name => model.Name;
    public override Symbol? ContainingSymbol => containingType;
    public override TypeSymbol Type => type ??= containingType.Resolve(model.Type);
    public override bool HasGetter => model.HasGetter;
    public override bool HasSetter => model.HasSetter;
    public override bool IsStatic => model.IsStatic;

    /// <summary>The IR function the getter lowered to, when it has a body.</summary>
    public string? IrGetterName => model.IrGetter;

    /// <summary>The IR function the setter lowered to, when it has a body.</summary>
    public string? IrSetterName => model.IrSetter;

    internal MetadataPropertySymbol(MetadataNamedTypeSymbol containingType, LibraryProperty model) {
        this.containingType = containingType;
        this.model = model;
    }
}
