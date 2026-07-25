using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven.Binding;

/// <summary>
///     One link in the scope chain. Each binder knows the names introduced by its own
///     scope; lookup walks outward through <see cref="Next" /> until a scope answers.
///     The chain is
///     <c>global → imports → type → method → block…</c>.
/// </summary>
public abstract partial class Binder {
    /// <summary>The enclosing scope, or null for the global binder.</summary>
    public Binder? Next { get; }

    public virtual BindingContext Context => Next!.Context;

    public Compilation Compilation => Context.Compilation;

    public DiagnosticBag Diagnostics => Context.Diagnostics;

    /// <summary>The type whose body we are inside, if any — what <c>self</c> means.</summary>
    public virtual NamedTypeSymbol? ContainingType => Next?.ContainingType;

    /// <summary>The member being bound (method, field initializer, accessor).</summary>
    public virtual Symbol? ContainingMember => Next?.ContainingMember;

    /// <summary>The return type <c>return</c> statements must satisfy.</summary>
    public virtual TypeSymbol? ReturnType => Next?.ReturnType;

    /// <summary>True inside a loop body, where <c>break</c>/<c>continue</c> are legal.</summary>
    public virtual bool IsInLoop => Next?.IsInLoop ?? false;

    protected Binder(Binder? next) {
        Next = next;
    }

    /// <summary>
    ///     Symbols named <paramref name="name" />, from the innermost scope that has
    ///     any. Outer scopes are shadowed, not merged.
    /// </summary>
    public IReadOnlyList<Symbol> Lookup(string name) {
        List<Symbol> results = [];
        for (var binder = this; binder is not null; binder = binder.Next) {
            binder.LookupInScope(name, results);
            if (results.Count > 0) {
                return results;
            }
        }

        return results;
    }

    /// <summary>
    ///     The first type named <paramref name="name" /> with this generic arity.
    ///     Unlike <see cref="Lookup" /> this keeps searching outward past scopes whose
    ///     match is not a type, so a local named <c>float</c> cannot hide the type.
    /// </summary>
    public TypeSymbol? LookupType(string name, int arity) {
        for (var binder = this; binder is not null; binder = binder.Next) {
            List<Symbol> results = [];
            binder.LookupInScope(name, results);

            foreach (var symbol in results) {
                if (symbol is NamedTypeSymbol named && named.Arity == arity) {
                    return named;
                }

                if (arity == 0 && symbol is TypeSymbol type and not NamedTypeSymbol) {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>The first namespace named <paramref name="name" /> in scope.</summary>
    public NamespaceSymbol? LookupNamespace(string name) {
        for (var binder = this; binder is not null; binder = binder.Next) {
            List<Symbol> results = [];
            binder.LookupInScope(name, results);

            foreach (var symbol in results) {
                if (symbol is NamespaceSymbol ns) {
                    return ns;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Members named <paramref name="name" /> reachable on <paramref name="type" />,
    ///     searching its bases and protocols. The first type in the chain that
    ///     declares the name wins.
    /// </summary>
    public static IReadOnlyList<Symbol> LookupMembers(TypeSymbol type, string name) {
        foreach (var current in type.TypeAndBases()) {
            var members = current.GetMembers(name);
            if (members.Count > 0) {
                return members;
            }
        }

        return [];
    }

    /// <summary>Adds symbols this scope declares under <paramref name="name" />.</summary>
    private protected virtual void LookupInScope(string name, List<Symbol> results) { }

    private protected void Report(DiagnosticDescriptor descriptor, SyntaxNode syntax, params object[] arguments) =>
        Diagnostics.Add(descriptor, syntax.GetLocation(), arguments);
}
