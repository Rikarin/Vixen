// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.IR;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Lowering;

/// <summary>
///     Flattening: a derived type takes its base's storage into its own layout and its own copy of
///     every inherited body.
/// </summary>
/// <remarks>
///     <para>
///         The symbol layer has always modelled inheritance correctly — member lookup walks the base
///         chain, nearest first — so the binder accepted <c>shader Derived : Base</c> and resolved
///         everything. Lowering was where it stopped: a type contributed only its <em>declared</em>
///         members, so a base's fields never became bindings or struct fields, and a base's method
///         body was lowered once against its own type rather than once per derived type. Three
///         silent miscompilations came out of that, which is why it was <c>RVN3002</c> rather than a
///         gap left open.
///     </para>
///     <para>
///         <strong>Flattening is monomorphisation over a different axis,</strong> and that is what
///         makes it affordable here: the same "emit this body in a context" machinery generics
///         needed does this too. A derived type is a context in which <c>self</c> has the derived
///         layout, a base's field resolves to the derived storage, and a call to an overridden
///         member reaches the override. Emitting a copy per derived type is what turns a language
///         with no dynamic dispatch into one where <c>override</c> means something.
///     </para>
///     <para>
///         <strong>Storage is merged rather than copied, for a shader.</strong> A shader's fields
///         are globals and a global is a name and a type, so the derived shader lists the same
///         <see cref="IrVariable" /> the base does — exactly what <c>compose</c> already does
///         through <c>MergeInterface</c>. A struct is the opposite: its fields are laid out by
///         index, so a derived struct genuinely holds the base's fields, base-first, and a body
///         reaching one resolves it against the derived layout.
///     </para>
/// </remarks>
public sealed partial class Lowerer {
    /// <summary>
    ///     The functions a derived type contributes for members it inherits, keyed by the type they
    ///     were emitted for.
    /// </summary>
    /// <remarks>
    ///     Keyed separately from <c>functions</c> rather than by a synthesized symbol, because there
    ///     is no symbol for "Base.Shade as Derived sees it" — the member is the base's, and only the
    ///     pair says which copy.
    /// </remarks>
    readonly Dictionary<(NamedTypeSymbol Owner, Symbol Member, BoundBodyKind Kind), IrFunction> inherited = [];

    readonly Dictionary<(NamedTypeSymbol Owner, Symbol Member, BoundBodyKind Kind), FunctionShell> inheritedShells = [];

    /// <summary>Every base of a type, nearest first, ignoring protocols and error types.</summary>
    /// <remarks>
    ///     A protocol base contributes no storage and no bodies, so it stops the walk — and it is
    ///     what <c>compose</c> slots are typed against, which is the composition that was already
    ///     working before flattening existed.
    /// </remarks>
    internal static IEnumerable<NamedTypeSymbol> BaseChain(NamedTypeSymbol type) {
        for (var current = type.BaseType; current is not null; current = current.BaseType) {
            if (current.IsErrorType || current.TypeKind == TypeKind.Protocol) {
                yield break;
            }

            yield return current;
        }
    }

    /// <summary>Whether <paramref name="candidate" /> is <paramref name="type" /> or one of its bases.</summary>
    static bool IsSelfOrBase(NamedTypeSymbol? candidate, NamedTypeSymbol type) =>
        candidate is not null
        && (candidate.Equals(type) || BaseChain(type).Any(candidate.Equals));

    /// <summary>
    ///     The fields a type's storage holds: its bases' first, in base-to-derived order, then its
    ///     own.
    /// </summary>
    /// <remarks>
    ///     Base-first so a derived value's prefix is the base's layout, which is the order every
    ///     other language with value inheritance uses and the one a host reading the reflection
    ///     would expect. A field the derived type redeclares by the same name would shadow rather
    ///     than replace, which the binder already refuses (<c>RVN2003</c>), so there is no collision
    ///     to resolve here.
    /// </remarks>
    static IEnumerable<FieldSymbol> LayoutFields(NamedTypeSymbol type) {
        foreach (var baseType in BaseChain(type).Reverse()) {
            foreach (var field in DeclaredStorage(baseType)) {
                yield return field;
            }
        }

        foreach (var field in DeclaredStorage(type)) {
            yield return field;
        }
    }

    static IEnumerable<FieldSymbol> DeclaredStorage(NamedTypeSymbol type) =>
        type.GetMembers().OfType<FieldSymbol>().Where(f => f is { IsConst: false, IsCompose: false, IsStream: false });

    /// <summary>
    ///     The bodies a type inherits and does not itself declare, with the name each copy gets.
    /// </summary>
    /// <remarks>
    ///     A member the derived type declares with the same signature <em>is</em> the override, and
    ///     is emitted from the type's own members like any other — so it is skipped here rather than
    ///     copied over. Nearest-first, so a chain of three types copies each member once.
    /// </remarks>
    IEnumerable<(string Name, Symbol Member, BoundBody Body)> InheritedBodies(NamedTypeSymbol type) {
        if (BaseChain(type).ToArray() is not { Length: > 0 } bases) {
            return [];
        }

        List<(string, Symbol, BoundBody)> result = [];
        HashSet<string> taken = [.. MemberBodies(type, report: false).Select(entry => entry.Name)];
        HashSet<string> seen = [];

        foreach (var baseType in bases) {
            foreach (var (_, member, body) in MemberBodies(baseType, report: false)) {
                // Signature identity rather than name: two overloads are two members, and the
                // derived type may override one of them.
                if (member is not MethodSymbol method || !seen.Add(Signature(method))) {
                    continue;
                }

                if (FindImplementation(type, method) is { } implementation
                    && !ReferenceEquals(implementation, method)) {
                    // Overridden: the derived type declares it, and its own pass emits it.
                    continue;
                }

                result.Add((Unique(taken, $"{type.Name}_{FunctionName(baseType, method)}"), member, body));
            }
        }

        return result;
    }

    static string Signature(MethodSymbol method) =>
        $"{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})";

    /// <summary>Creates the signature of every body a type inherits.</summary>
    void DeclareInheritedFunctions(NamedTypeSymbol type) {
        var selfType = type.TypeKind is TypeKind.Struct ? structs[type] : null;
        var previous = currentSelfType;
        currentSelfType = type;

        try {
            foreach (var (name, member, body) in InheritedBodies(type)) {
                DeclareFunction(name, body, member, type, SelfTypeFor(body, selfType));
            }
        } finally {
            currentSelfType = previous;
        }
    }

    /// <summary>Lowers every body a type inherits, in the derived type's context.</summary>
    void LowerInheritedFunctions(NamedTypeSymbol type, Action<IrFunction> add) {
        var selfType = type.TypeKind is TypeKind.Struct ? structs[type] : null;
        var previous = currentSelfType;
        currentSelfType = type;

        try {
            foreach (var (name, member, body) in InheritedBodies(type)) {
                add(LowerFunction(name, body, member, type, SelfTypeFor(body, selfType)));
            }
        } finally {
            currentSelfType = previous;
        }
    }

    /// <summary>Gives every shader the bindings and streams of the shaders it inherits from.</summary>
    /// <remarks>
    ///     <para>
    ///         The same merge <c>compose</c> uses, for the same reason and with the same effect: a
    ///         shader's storage is module-scope globals, so the derived shader lists the variables
    ///         the base declared rather than variables of its own.
    ///     </para>
    ///     <para>
    ///         <strong>The shader's own bindings come first and what it pulls in follows</strong> —
    ///         one rule for inheritance and <c>compose</c> alike, so a shader's own layout does not
    ///         move when a base gains a field. A <em>struct</em> is laid out the other way round,
    ///         base-first, and that is not an inconsistency: a struct's fields are reached by index
    ///         and putting the base's first makes a derived value's prefix the base's layout, which
    ///         is what every other language with value inheritance does.
    ///     </para>
    /// </remarks>
    void MergeInheritedInterfaces(IReadOnlyList<NamedTypeSymbol> types) {
        foreach (var type in types) {
            if (type.TypeKind != TypeKind.Shader || !shaders.TryGetValue(type, out var shader)) {
                continue;
            }

            foreach (var baseType in BaseChain(type).Reverse()) {
                if (shaders.TryGetValue(baseType, out var source)) {
                    MergeInterface(shader, source, qualify: false);
                }
            }
        }
    }
}
