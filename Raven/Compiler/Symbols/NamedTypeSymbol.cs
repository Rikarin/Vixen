namespace Vixen.Raven.Symbols;

/// <summary>
/// A type referred to by name: a source <c>shader</c>/<c>struct</c>/<c>class</c>/
/// <c>protocol</c>/<c>enum</c>, or a compiler-supplied type such as <c>string</c>
/// or <c>Texture2D</c>.
/// </summary>
public abstract class NamedTypeSymbol : TypeSymbol {
    public override SymbolKind Kind => SymbolKind.NamedType;

    public virtual IReadOnlyList<TypeParameterSymbol> TypeParameters => [];

    /// <summary>
    /// The arguments this type was constructed with. For a generic definition
    /// these are its own type parameters.
    /// </summary>
    public virtual IReadOnlyList<TypeSymbol> TypeArguments => TypeParameters;

    public int Arity => TypeParameters.Count;

    /// <summary>True once type arguments have been substituted for the parameters.</summary>
    public virtual bool IsConstructed => false;

    /// <summary>The uninstantiated generic definition, or this type when not constructed.</summary>
    public virtual NamedTypeSymbol OriginalDefinition => this;

    /// <summary>Instance constructors declared on this type.</summary>
    public IReadOnlyList<MethodSymbol> Constructors {
        get {
            List<MethodSymbol>? found = null;
            foreach (var member in GetMembers()) {
                if (member is MethodSymbol { IsConstructor: true } method) {
                    (found ??= []).Add(method);
                }
            }

            return found ?? (IReadOnlyList<MethodSymbol>)[];
        }
    }

    public override string ToDisplayString() {
        var qualifier = ContainingSymbol switch {
            NamespaceSymbol { IsGlobalNamespace: false } ns => ns.QualifiedName + ".",
            NamedTypeSymbol type => type.ToDisplayString() + ".",
            _ => string.Empty
        };

        var name = qualifier + Name;
        return Arity == 0 ? name : $"{name}<{string.Join(", ", TypeArguments.Select(a => a.ToDisplayString()))}>";
    }
}
