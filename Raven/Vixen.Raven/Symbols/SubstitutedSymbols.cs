using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>
///     Views of a member seen through a <see cref="TypeMap" />: same declaration,
///     signature types substituted. Produced when reading members of a constructed
///     generic type or calling a generic method with explicit type arguments.
/// </summary>
public static class SubstitutedSymbols {
    /// <summary>Wraps <paramref name="member" /> so its signature reads through <paramref name="map" />.</summary>
    public static Symbol Substitute(Symbol member, Symbol container, TypeMap map) =>
        member switch {
            FieldSymbol field => new SubstitutedFieldSymbol(field, container, map),
            PropertySymbol property => new SubstitutedPropertySymbol(property, container, map),
            MethodSymbol method => new SubstitutedMethodSymbol(method, container, map, []),
            _ => member
        };
}

/// <summary>A field of a constructed generic type.</summary>
public sealed class SubstitutedFieldSymbol(FieldSymbol definition, Symbol container, TypeMap map) : FieldSymbol {
    public FieldSymbol OriginalDefinition { get; } = definition;
    public override string Name => OriginalDefinition.Name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override TypeSymbol Type { get; } = map.Substitute(definition.Type);
    public override bool IsReadOnly => OriginalDefinition.IsReadOnly;
    public override bool IsConst => OriginalDefinition.IsConst;
    public override object? ConstantValue => OriginalDefinition.ConstantValue;
    public override ResourceKind ResourceKind => OriginalDefinition.ResourceKind;
    public override string? SemanticName => OriginalDefinition.SemanticName;
    public override bool IsStatic => OriginalDefinition.IsStatic;
    public override Accessibility DeclaredAccessibility => OriginalDefinition.DeclaredAccessibility;
    public override SyntaxNode? DeclaringSyntax => OriginalDefinition.DeclaringSyntax;
}

/// <summary>A property of a constructed generic type.</summary>
public sealed class SubstitutedPropertySymbol : PropertySymbol {
    readonly ParameterSymbol[] parameters;

    public PropertySymbol OriginalDefinition { get; }
    public override string Name => OriginalDefinition.Name;
    public override Symbol? ContainingSymbol { get; }
    public override TypeSymbol Type { get; }
    public override bool HasGetter => OriginalDefinition.HasGetter;
    public override bool HasSetter => OriginalDefinition.HasSetter;
    public override IReadOnlyList<ParameterSymbol> Parameters => parameters;
    public override bool IsStatic => OriginalDefinition.IsStatic;
    public override Accessibility DeclaredAccessibility => OriginalDefinition.DeclaredAccessibility;
    public override SyntaxNode? DeclaringSyntax => OriginalDefinition.DeclaringSyntax;

    internal SubstitutedPropertySymbol(PropertySymbol definition, Symbol container, TypeMap map) {
        OriginalDefinition = definition;
        ContainingSymbol = container;
        Type = map.Substitute(definition.Type);
        parameters = definition.Parameters
            .Select(p => (ParameterSymbol)new SubstitutedParameterSymbol(p, this, map))
            .ToArray();
    }
}

/// <summary>
///     A method read through a type map: a member of a constructed generic type, or
///     a generic method supplied with explicit type arguments.
/// </summary>
public sealed class SubstitutedMethodSymbol : MethodSymbol {
    readonly ParameterSymbol[] parameters;

    public MethodSymbol OriginalDefinition { get; }

    /// <summary>Arguments supplied for the method's own type parameters, if any.</summary>
    public IReadOnlyList<TypeSymbol> TypeArguments { get; }

    public override string Name => OriginalDefinition.Name;
    public override Symbol? ContainingSymbol { get; }
    public override MethodKind MethodKind => OriginalDefinition.MethodKind;
    public override TypeSymbol ReturnType { get; }
    public override IReadOnlyList<ParameterSymbol> Parameters => parameters;
    public override IReadOnlyList<TypeParameterSymbol> TypeParameters => OriginalDefinition.TypeParameters;
    public override ShaderStage Stage => OriginalDefinition.Stage;
    public override string? SemanticName => OriginalDefinition.SemanticName;
    public override bool IsStatic => OriginalDefinition.IsStatic;
    public override bool IsAbstract => OriginalDefinition.IsAbstract;
    public override Accessibility DeclaredAccessibility => OriginalDefinition.DeclaredAccessibility;
    public override SyntaxNode? DeclaringSyntax => OriginalDefinition.DeclaringSyntax;

    internal SubstitutedMethodSymbol(
        MethodSymbol definition,
        Symbol? container,
        TypeMap map,
        IReadOnlyList<TypeSymbol> typeArguments
    ) {
        OriginalDefinition = definition;
        ContainingSymbol = container;
        ReturnType = map.Substitute(definition.ReturnType);
        TypeArguments = typeArguments;
        parameters = definition.Parameters
            .Select(p => (ParameterSymbol)new SubstitutedParameterSymbol(p, this, map))
            .ToArray();
    }
}

/// <summary>A parameter whose type is read through a type map.</summary>
public sealed class SubstitutedParameterSymbol(ParameterSymbol definition, Symbol container, TypeMap map)
    : ParameterSymbol {
    public ParameterSymbol OriginalDefinition { get; } = definition;
    public override string Name => OriginalDefinition.Name;
    public override Symbol? ContainingSymbol { get; } = container;
    public override TypeSymbol Type { get; } = map.Substitute(definition.Type);
    public override int Ordinal => OriginalDefinition.Ordinal;
    public override bool HasDefaultValue => OriginalDefinition.HasDefaultValue;
    public override object? DefaultValue => OriginalDefinition.DefaultValue;
    public override string? SemanticName => OriginalDefinition.SemanticName;
    public override SyntaxNode? DeclaringSyntax => OriginalDefinition.DeclaringSyntax;
}
