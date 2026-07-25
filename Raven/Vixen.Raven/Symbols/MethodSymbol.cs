
namespace Vixen.Raven.Symbols;

/// <summary>What a <see cref="MethodSymbol" /> was declared as.</summary>
public enum MethodKind {
    Ordinary,
    Constructor,
    Destructor,
    PropertyGet,
    PropertySet,

    /// <summary>A <c>willSet</c>/<c>didSet</c> observer body.</summary>
    PropertyObserver,
    Operator,
    Conversion,
    LocalFunction,

    /// <summary>A compiler-supplied function such as <c>dot</c> or <c>normalize</c>.</summary>
    Intrinsic
}

/// <summary>A callable member: <c>func</c>, <c>init</c>, an accessor, or an intrinsic.</summary>
public abstract class MethodSymbol : Symbol {
    public override SymbolKind Kind => SymbolKind.Method;

    public abstract MethodKind MethodKind { get; }

    public abstract TypeSymbol ReturnType { get; }

    public abstract IReadOnlyList<ParameterSymbol> Parameters { get; }

    public virtual IReadOnlyList<TypeParameterSymbol> TypeParameters => [];

    public int Arity => TypeParameters.Count;

    public bool IsConstructor => MethodKind == MethodKind.Constructor;

    /// <summary>The pipeline stage this method is an entry point for, if any.</summary>
    public virtual ShaderStage Stage => ShaderStage.None;

    /// <summary>The semantic its return value carries, from <c>[Semantic("…")]</c>.</summary>
    public virtual string? SemanticName => null;

    /// <summary>Lowest parameter count a call may supply, honouring defaults.</summary>
    public int MinimumArgumentCount {
        get {
            var count = Parameters.Count;
            while (count > 0 && Parameters[count - 1].HasDefaultValue) {
                count--;
            }

            return count;
        }
    }

    public override string ToDisplayString() {
        var qualifier = ContainingType is { } type ? type.ToDisplayString() + "." : string.Empty;
        var typeArgs = Arity == 0 ? string.Empty : $"<{string.Join(", ", TypeParameters.Select(p => p.Name))}>";
        var parameters = string.Join(", ", Parameters.Select(p => p.ToDisplayString()));
        return $"{qualifier}{Name}{typeArgs}({parameters}): {ReturnType.ToDisplayString()}";
    }
}

/// <summary>
///     A method the compiler supplies: an intrinsic function, or a method on a
///     built-in type such as <c>Texture2D.Sample</c>.
/// </summary>
public sealed class SynthesizedMethodSymbol : MethodSymbol {
    readonly ParameterSymbol[] parameters;

    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }
    public override MethodKind MethodKind { get; }
    public override TypeSymbol ReturnType { get; }
    public override IReadOnlyList<ParameterSymbol> Parameters => parameters;
    public override Accessibility DeclaredAccessibility => Accessibility.Public;
    public override bool IsStatic => ContainingSymbol is null;

    internal SynthesizedMethodSymbol(
        Symbol? container,
        string name,
        TypeSymbol returnType,
        IReadOnlyList<(string Name, TypeSymbol Type)> parameters,
        MethodKind methodKind = MethodKind.Intrinsic
    ) {
        ContainingSymbol = container;
        Name = name;
        ReturnType = returnType;
        MethodKind = methodKind;
        this.parameters = parameters
            .Select((p, i) => (ParameterSymbol)new SynthesizedParameterSymbol(this, p.Name, p.Type, i))
            .ToArray();
    }
}
