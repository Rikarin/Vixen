using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>
/// The outermost scope: everything declared anywhere in the compilation, plus
/// the intrinsic types and functions.
/// </summary>
public sealed class GlobalBinder(BindingContext context) : Binder(null) {
    public override BindingContext Context { get; } = context;

    private protected override void LookupInScope(string name, List<Symbol> results) {
        results.AddRange(Context.Compilation.GlobalNamespace.GetMembers(name));

        if (BuiltInTypes.Lookup(name) is { } builtIn) {
            results.Add(builtIn);
        }

        results.AddRange(Intrinsics.Lookup(name));
    }
}

/// <summary>A type declaration's scope: its type parameters and its members, inherited ones included.</summary>
public sealed class NamedTypeBinder(Binder next, NamedTypeSymbol type) : Binder(next) {
    public NamedTypeSymbol Type { get; } = type;

    public override NamedTypeSymbol? ContainingType => Type;

    private protected override void LookupInScope(string name, List<Symbol> results) {
        foreach (var typeParameter in Type.TypeParameters) {
            if (typeParameter.Name == name) {
                results.Add(typeParameter);
            }
        }

        if (results.Count > 0) {
            return;
        }

        results.AddRange(LookupMembers(Type, name));
    }
}

/// <summary>
/// A member's scope: the parameters and type parameters of a method, indexer or
/// accessor, and the return type <c>return</c> is checked against.
/// </summary>
public sealed class MemberBinder : Binder {
    readonly ParameterSymbol[] parameters;
    readonly TypeParameterSymbol[] typeParameters;

    internal MemberBinder(
        Binder next,
        Symbol member,
        TypeSymbol? returnType,
        IReadOnlyList<ParameterSymbol>? parameters = null,
        IReadOnlyList<TypeParameterSymbol>? typeParameters = null
    ) : base(next) {
        Member = member;
        ReturnType = returnType;
        this.parameters = parameters?.ToArray() ?? [];
        this.typeParameters = typeParameters?.ToArray() ?? [];
    }

    public Symbol Member { get; }

    public override Symbol? ContainingMember => Member;
    public override TypeSymbol? ReturnType { get; }

    private protected override void LookupInScope(string name, List<Symbol> results) {
        foreach (var typeParameter in typeParameters) {
            if (typeParameter.Name == name) {
                results.Add(typeParameter);
            }
        }

        foreach (var parameter in parameters) {
            if (parameter.Name == name) {
                results.Add(parameter);
            }
        }
    }
}

/// <summary>
/// A statement scope. Locals are added as their declarations are bound, so a
/// name is only visible after the point it is declared.
/// </summary>
public sealed class BlockBinder(Binder next, bool isLoopBody = false) : Binder(next) {
    readonly List<Symbol> declared = [];

    public override bool IsInLoop => isLoopBody || base.IsInLoop;

    public IReadOnlyList<LocalSymbol> Locals => declared.OfType<LocalSymbol>().ToArray();

    /// <summary>Adds a local or local function; false when the name is already taken here.</summary>
    public bool TryDeclare(Symbol symbol) {
        foreach (var existing in declared) {
            // Local functions may overload each other; anything else may not.
            if (existing.Name == symbol.Name && (existing is not MethodSymbol || symbol is not MethodSymbol)) {
                return false;
            }
        }

        declared.Add(symbol);
        return true;
    }

    private protected override void LookupInScope(string name, List<Symbol> results) {
        foreach (var symbol in declared) {
            if (symbol.Name == name) {
                results.Add(symbol);
            }
        }
    }
}

/// <summary>
/// Swaps in a different <see cref="BindingContext"/> without changing the scope
/// chain. The <see cref="SemanticModel"/> uses this so diagnostics and bound
/// nodes from method bodies land in its own maps rather than the compilation's
/// declaration state.
/// </summary>
public sealed class ContextBinder(Binder next, BindingContext context) : Binder(next) {
    public override BindingContext Context { get; } = context;
}
