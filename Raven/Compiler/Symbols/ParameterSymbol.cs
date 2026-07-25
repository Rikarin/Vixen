using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols;

/// <summary>A parameter of a method, constructor, indexer or lambda.</summary>
public abstract class ParameterSymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Parameter;

    public abstract TypeSymbol Type { get; }

    /// <summary>Position in the declaring signature.</summary>
    public abstract int Ordinal { get; }

    /// <summary>True when the declaration supplies a default (<c>count: int = 42</c>).</summary>
    public virtual bool HasDefaultValue => false;

    /// <summary>The default's constant value, when it is a literal.</summary>
    public virtual object? DefaultValue => null;

    /// <summary>The pipeline semantic from a <c>[Semantic("…")]</c> attribute, or null.</summary>
    public virtual string? SemanticName => null;

    public override string ToDisplayString() => $"{Name}: {Type.ToDisplayString()}";
}

/// <summary>A parameter of a built-in signature (intrinsic function, resource method).</summary>
public sealed class SynthesizedParameterSymbol : ParameterSymbol {
    internal SynthesizedParameterSymbol(Symbol container, string name, TypeSymbol type, int ordinal) {
        ContainingSymbol = container;
        Name = name;
        Type = type;
        Ordinal = ordinal;
    }

    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override TypeSymbol Type { get; }
    public override int Ordinal { get; }
}

/// <summary>A lambda parameter, whose type may be inferred from context.</summary>
public sealed class LambdaParameterSymbol : ParameterSymbol {
    internal LambdaParameterSymbol(Symbol? container, string name, TypeSymbol type, int ordinal, SyntaxNode? syntax) {
        ContainingSymbol = container;
        Name = name;
        Type = type;
        Ordinal = ordinal;
        DeclaringSyntax = syntax;
    }

    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override TypeSymbol Type { get; }
    public override int Ordinal { get; }
    public override SyntaxNode? DeclaringSyntax { get; }
}
