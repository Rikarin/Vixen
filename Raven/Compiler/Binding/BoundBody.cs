using Vixen.Raven.Symbols;

namespace Vixen.Raven.Binding;

/// <summary>What a <see cref="BoundBody"/> came from.</summary>
public enum BoundBodyKind {
    Method,
    Constructor,
    PropertyGetter,
    PropertySetter,
    /// <summary>A field's <c>= expression</c>, modelled as a body that returns it.</summary>
    FieldInitializer
}

/// <summary>
/// A member's body, normalized. Expression bodies and field initializers are
/// wrapped so every form is a <see cref="BoundBlockStatement"/> with a known
/// parameter list and return type — which is what lowering wants, and what the
/// raw syntax does not give you (an expression-bodied method's return conversion
/// has no syntax node of its own to hang off).
/// </summary>
public sealed class BoundBody {
    internal BoundBody(
        Symbol member,
        BoundBodyKind kind,
        IReadOnlyList<ParameterSymbol> parameters,
        TypeSymbol returnType,
        BoundBlockStatement body
    ) {
        Member = member;
        Kind = kind;
        Parameters = parameters;
        ReturnType = returnType;
        Body = body;
    }

    /// <summary>The method, property or field this body belongs to.</summary>
    public Symbol Member { get; }

    public BoundBodyKind Kind { get; }

    /// <summary>Parameters in scope, including a setter's synthesized <c>value</c>.</summary>
    public IReadOnlyList<ParameterSymbol> Parameters { get; }

    public TypeSymbol ReturnType { get; }

    public BoundBlockStatement Body { get; }

    public override string ToString() => $"{Kind} {Member.ToDisplayString()}";
}
