// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Lowering;

/// <summary>
///     One instantiation to emit: a generic declaration with concrete arguments supplied.
/// </summary>
/// <param name="Type">
///     The constructed type, for a generic struct; null for a generic <em>method</em> on a
///     non-generic type.
/// </param>
/// <param name="Method">
///     The constructed method, for a generic method; null for a struct's instantiation, whose
///     methods come from <c>Type.GetMembers()</c>.
/// </param>
/// <param name="Map">The substitution to lower the declaration's bodies through.</param>
sealed record Instantiation(ConstructedNamedTypeSymbol? Type, SubstitutedMethodSymbol? Method, TypeMap Map);

/// <summary>
///     Finds every instantiation a compilation actually uses, so the lowerer can emit one concrete
///     copy of each.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Monomorphisation is the only way a generic reaches a GPU.</strong> Neither
///         target has a polymorphic function or an unsized type: SPIR-V's types are structural and
///         fully concrete, and GLSL has no templates at all. So <c>Box&lt;T&gt;</c> is never
///         emitted — <c>Box&lt;float4&gt;</c> is, as an ordinary struct named <c>Box_float4</c>,
///         and a shader that never mentions one costs nothing.
///     </para>
///     <para>
///         Discovery is a worklist rather than a single pass, because an instantiation can name
///         another: a field of <c>Box&lt;Pair&lt;float&gt;&gt;</c> reaches <c>Pair&lt;float&gt;</c>
///         only once <c>Box</c>'s own members are read through its map. It terminates because
///         Raven has no way to construct a type argument from a type parameter — there is no
///         <c>Box&lt;Box&lt;T&gt;&gt;</c> inside <c>Box&lt;T&gt;</c> to expand forever, since a
///         generic type may not reference itself through its own parameter. A depth ceiling
///         guards the case anyway, because a compiler that hangs is worse than one that reports.
///     </para>
///     <para>
///         Instantiations are canonicalised. Two call sites writing <c>Swap&lt;float&gt;(…)</c>
///         produce two distinct <see cref="SubstitutedMethodSymbol" /> objects for the same
///         instantiation — the class has reference identity — so they are keyed by definition and
///         argument types instead, and every later lookup goes through the canonical one. Without
///         that, one generic function would be emitted once per call site.
///     </para>
/// </remarks>
sealed class Monomorphiser {
    /// <summary>
    ///     How deep an instantiation may nest before the search gives up.
    /// </summary>
    /// <remarks>
    ///     Unreachable in a language that cannot write <c>Box&lt;Box&lt;T&gt;&gt;</c> inside
    ///     <c>Box&lt;T&gt;</c>. Here so that a future language change turns a hang into a
    ///     diagnostic, which is the failure mode worth having.
    /// </remarks>
    const int MaxDepth = 16;

    readonly Dictionary<string, ConstructedNamedTypeSymbol> types = new(StringComparer.Ordinal);
    readonly Dictionary<string, SubstitutedMethodSymbol> methods = new(StringComparer.Ordinal);
    readonly Dictionary<string, Symbol> canonicalMembers = new(StringComparer.Ordinal);
    readonly List<Instantiation> ordered = [];
    readonly Queue<(Instantiation Work, int Depth)> pending = new();
    readonly Func<Symbol, IEnumerable<BoundBody>> bodiesOf;

    /// <summary>Whether the search gave up before closing.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Every instantiation to emit, in discovery order.</summary>
    public IReadOnlyList<Instantiation> Instantiations => ordered;

    public Monomorphiser(Func<Symbol, IEnumerable<BoundBody>> bodiesOf) {
        this.bodiesOf = bodiesOf;
    }

    /// <summary>The canonical symbol for a constructed type, so two uses share one struct.</summary>
    public ConstructedNamedTypeSymbol Canonical(ConstructedNamedTypeSymbol type) =>
        types.TryGetValue(Key(type), out var canonical) ? canonical : type;

    /// <summary>
    ///     The canonical symbol for a member of an instantiation, so the declaration and every call
    ///     site key the function table by one object.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A substituted member has reference identity — <c>Box&lt;float4&gt;.GetMembers()</c>
    ///         builds fresh symbols each time it is asked — so a dictionary keyed by the symbol
    ///         would give the call site a miss where the declaration hit. Keying by
    ///         <em>declaration and arguments</em> instead makes the first symbol seen the canonical
    ///         one, and every equal symbol after it resolves to that.
    ///     </para>
    ///     <para>
    ///         First writer wins, and the order is what makes it right: instantiations are declared
    ///         before any body is lowered, so the canonical symbol is always the one the function
    ///         table was built with.
    ///     </para>
    /// </remarks>
    public Symbol Canonical(Symbol member) {
        if (MemberKey(member) is not { } key) {
            return member;
        }

        if (canonicalMembers.TryGetValue(key, out var canonical)) {
            return canonical;
        }

        canonicalMembers[key] = member;
        return member;
    }

    /// <summary>
    ///     The identity of a member of an instantiation, or null when the member belongs to an
    ///     ordinary declaration and is already its own key.
    /// </summary>
    static string? MemberKey(Symbol member) =>
        member switch {
            SubstitutedMethodSymbol { TypeArguments.Count: > 0 } method => Key(method),
            SubstitutedMethodSymbol { ContainingSymbol: ConstructedNamedTypeSymbol container } method =>
                $"{Key(container)}::{Signature(method)}",
            SubstitutedPropertySymbol { ContainingSymbol: ConstructedNamedTypeSymbol container } property =>
                $"{Key(container)}::{property.OriginalDefinition.Name}",
            _ => null
        };

    /// <summary>Name and parameter types, so two overloads of one name stay two members.</summary>
    static string Signature(SubstitutedMethodSymbol method) =>
        $"{method.OriginalDefinition.Name}({string.Join(",", method.OriginalDefinition.Parameters.Select(p => p.Type.ToDisplayString()))})";

    /// <summary>
    ///     Walks a declaration's signatures and bodies, adding whatever instantiations they reach.
    /// </summary>
    public void Seed(NamedTypeSymbol type) {
        ArgumentNullException.ThrowIfNull(type);

        foreach (var member in type.GetMembers()) {
            Signature(member, null, 0);
            Bodies(member, null, 0);
        }
    }

    /// <summary>Closes the worklist, so an instantiation reached by another is emitted too.</summary>
    public void Close() {
        while (pending.Count > 0) {
            var (work, depth) = pending.Dequeue();

            if (depth >= MaxDepth) {
                Overflowed = true;
                continue;
            }

            if (work.Type is { } constructed) {
                foreach (var member in constructed.GetMembers()) {
                    Signature(member, work.Map, depth + 1);
                    Bodies(member, work.Map, depth + 1);
                }

                continue;
            }

            Signature(work.Method!, work.Map, depth + 1);
            Bodies(work.Method!, work.Map, depth + 1);
        }
    }

    /// <summary>Adds the instantiations a member's declared types reach.</summary>
    void Signature(Symbol member, TypeMap? through, int depth) {
        switch (member) {
            case FieldSymbol field:
                Reach(field.Type, through, depth);
                break;

            case PropertySymbol property:
                Reach(property.Type, through, depth);
                break;

            case MethodSymbol method:
                Reach(method.ReturnType, through, depth);

                foreach (var parameter in method.Parameters) {
                    Reach(parameter.Type, through, depth);
                }

                break;
        }
    }

    /// <summary>Adds the instantiations a member's bound bodies reach.</summary>
    /// <remarks>
    ///     Bodies are keyed by the <em>definition</em>'s symbol, because that is what was bound —
    ///     an instantiation reuses the same bound tree and differs only in the map it is read
    ///     through. So the walk unwraps to the definition to find the tree, then reaches every
    ///     type in it through the map.
    /// </remarks>
    void Bodies(Symbol member, TypeMap? through, int depth) {
        foreach (var body in bodiesOf(Definition(member))) {
            foreach (var node in body.Body.DescendantsAndSelf()) {
                switch (node) {
                    case BoundExpression expression:
                        Reach(expression.Type, through, depth);

                        if (expression is BoundInvocationExpression { Method: SubstitutedMethodSymbol generic }) {
                            ReachMethod(generic, through, depth);
                        }

                        break;

                    case BoundLocalDeclarationStatement declaration:
                        Reach(declaration.Local.Type, through, depth);
                        break;
                }
            }
        }
    }

    /// <summary>Adds whatever instantiations <paramref name="type" /> is or contains.</summary>
    void Reach(TypeSymbol type, TypeMap? through, int depth) {
        switch (through?.Substitute(type) ?? type) {
            case ConstructedNamedTypeSymbol { TypeKind: TypeKind.Struct } constructed
                when !constructed.TypeArguments.Any(IsOpen):
                foreach (var argument in constructed.TypeArguments) {
                    Reach(argument, null, depth);
                }

                Add(constructed, depth);
                break;

            case ArrayTypeSymbol array:
                Reach(array.ElementType, null, depth);
                break;

            case BufferTypeSymbol buffer:
                Reach(buffer.ElementType, null, depth);
                break;

            case TupleTypeSymbol tuple:
                foreach (var element in tuple.ElementTypes) {
                    Reach(element, null, depth);
                }

                break;
        }
    }

    /// <summary>Adds a call to a generic method with its arguments supplied.</summary>
    /// <remarks>
    ///     A method of a constructed <em>type</em> is not one of these: it is emitted with its
    ///     type, from <c>GetMembers()</c>. Only a method with type parameters of its own —
    ///     <c>func Swap&lt;T&gt;(…)</c> — needs a function per instantiation.
    /// </remarks>
    void ReachMethod(SubstitutedMethodSymbol method, TypeMap? through, int depth) {
        if (method.TypeArguments.Count == 0) {
            return;
        }

        var arguments = method.TypeArguments.Select(a => through?.Substitute(a) ?? a).ToArray();

        if (arguments.Any(IsOpen)) {
            return;
        }

        foreach (var argument in arguments) {
            Reach(argument, null, depth);
        }

        var resolved = arguments.SequenceEqual(method.TypeArguments)
            ? method
            : new SubstitutedMethodSymbol(
                method.OriginalDefinition,
                method.ContainingSymbol,
                new(method.OriginalDefinition.TypeParameters, arguments),
                arguments
            );

        var key = Key(resolved);

        if (methods.ContainsKey(key)) {
            return;
        }

        methods[key] = resolved;
        Enqueue(new(null, resolved, new(resolved.OriginalDefinition.TypeParameters, arguments)), depth);
    }

    void Add(ConstructedNamedTypeSymbol constructed, int depth) {
        var key = Key(constructed);

        if (types.ContainsKey(key)) {
            return;
        }

        types[key] = constructed;
        Enqueue(
            new(constructed, null, new(constructed.OriginalDefinition.TypeParameters, constructed.TypeArguments)),
            depth
        );
    }

    void Enqueue(Instantiation work, int depth) {
        ordered.Add(work);
        pending.Enqueue((work, depth));
    }

    /// <summary>Whether a type still mentions a type parameter, and so is not yet an instantiation.</summary>
    static bool IsOpen(TypeSymbol type) =>
        type switch {
            TypeParameterSymbol => true,
            ArrayTypeSymbol array => IsOpen(array.ElementType),
            BufferTypeSymbol buffer => IsOpen(buffer.ElementType),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(IsOpen),
            NamedTypeSymbol { IsConstructed: true } constructed => constructed.TypeArguments.Any(IsOpen),
            _ => false
        };

    static Symbol Definition(Symbol member) =>
        member switch {
            SubstitutedMethodSymbol method => method.OriginalDefinition,
            SubstitutedFieldSymbol field => field.OriginalDefinition,
            SubstitutedPropertySymbol property => property.OriginalDefinition,
            _ => member
        };

    static string Key(ConstructedNamedTypeSymbol type) =>
        $"{type.OriginalDefinition.ToDisplayString()}<{string.Join(",", type.TypeArguments.Select(Name))}>";

    static string Key(SubstitutedMethodSymbol method) =>
        $"{method.OriginalDefinition.ToDisplayString()}<{string.Join(",", method.TypeArguments.Select(Name))}>";

    /// <summary>
    ///     The name an instantiation is keyed and mangled by.
    /// </summary>
    /// <remarks>
    ///     Display strings rather than symbol identity, because two structurally equal type
    ///     arguments may be different objects — a <c>float[4]</c> bound at two call sites is two
    ///     <see cref="ArrayTypeSymbol" />s — and emitting a second copy of a struct for that would
    ///     be a bug the module would carry silently.
    /// </remarks>
    static string Name(TypeSymbol type) => type.ToDisplayString();
}
