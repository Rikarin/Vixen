// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Vixen.Raven.Symbols;

namespace Vixen.Raven.Artefacts;

/// <summary>
///     A compiled Raven library: the declarations a consumer binds against, and the lowered IR
///     the bodies emit from. This is what a <c>.rvnlib</c> holds, and what a
///     <see cref="RavenReference" /> hands to a <see cref="Compilation" />.
/// </summary>
/// <remarks>
///     <para>
///         Two halves, and both are needed for the requirement — "referenced without reparsing
///         source" — to mean anything. The <see cref="Types" /> half becomes real
///         <c>NamedTypeSymbol</c>s that participate in binding, so a call into the library is
///         type-checked exactly as a call within the compilation is. The <see cref="Ir" /> half
///         is what makes the call <em>emit</em>: the lowerer links the library's functions into
///         the module it is building, so the backend sees a direct call and never learns the
///         callee came from elsewhere.
///     </para>
///     <para>
///         The link between the two halves is by name — <see cref="LibraryMethod.IrFunction" />
///         and <see cref="LibraryType.IrStruct" />. Names rather than indices so a
///         hand-inspected artefact is readable, and so adding a function to a library does not
///         renumber the rest of it.
///     </para>
/// </remarks>
public sealed record CompiledLibrary {
    /// <summary>The library's name, which is the assembly name of the compilation that produced it.</summary>
    public required string Name { get; init; }

    /// <summary>Every type the library declares, nested types flattened out.</summary>
    public ImmutableArray<LibraryType> Types { get; init; } = [];

    /// <summary>The lowered IR the exported bodies are made of.</summary>
    public LibraryIr Ir { get; init; } = new();

    /// <summary>
    ///     SHA-256 over the sources this was compiled from, so a stale library is detectable
    ///     without recompiling to compare. Same construction as
    ///     <see cref="CompiledEffect.SourceHash" />.
    /// </summary>
    public string SourceHash { get; init; } = string.Empty;

    /// <summary>The type with this qualified name, or null.</summary>
    public LibraryType? Find(string qualifiedName) =>
        Types.FirstOrDefault(t => string.Equals(t.QualifiedName, qualifiedName, StringComparison.Ordinal));
}

/// <summary>One type a library declares.</summary>
/// <remarks>
///     Nested types are recorded flat, with <see cref="ContainingType" /> naming the outer one,
///     because the reader has to resolve a type reference by qualified name before it knows
///     whether the target is nested.
/// </remarks>
public sealed record LibraryType {
    /// <summary>Dotted <c>package</c> name, empty for the global namespace.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Simple name, without the namespace or the declaring type.</summary>
    public required string Name { get; init; }

    /// <summary>The declaring type's qualified name, when this type is nested.</summary>
    public string? ContainingType { get; init; }

    public TypeKind Kind { get; init; }

    public LibraryTypeReference? BaseType { get; init; }

    /// <summary>Protocols the type conforms to.</summary>
    public ImmutableArray<LibraryTypeReference> Interfaces { get; init; } = [];

    public ImmutableArray<LibraryTypeParameter> TypeParameters { get; init; } = [];

    public ImmutableArray<LibraryField> Fields { get; init; } = [];

    public ImmutableArray<LibraryMethod> Methods { get; init; } = [];

    public ImmutableArray<LibraryProperty> Properties { get; init; } = [];

    /// <summary>
    ///     The IR struct this type lowered to, when it has storage. Null for a shader, a
    ///     protocol and an enum, none of which becomes an aggregate.
    /// </summary>
    public string? IrStruct { get; init; }

    /// <summary>
    ///     Namespace, declaring types and name, dotted. This is the key every type reference
    ///     resolves against.
    /// </summary>
    public string QualifiedName =>
        ContainingType is { Length: > 0 } outer
            ? outer + "." + Name
            : Namespace.Length > 0
                ? Namespace + "." + Name
                : Name;
}

/// <summary>A generic parameter of a library type or method.</summary>
public sealed record LibraryTypeParameter {
    public required string Name { get; init; }
    public int Ordinal { get; init; }

    /// <summary>Types named by the parameter's <c>where</c> clause, which are enforced (RVN2096).</summary>
    public ImmutableArray<LibraryTypeReference> Constraints { get; init; } = [];
}

/// <summary>A <c>val</c>/<c>var</c>/<c>const</c> member, an enum member, or a <c>val</c> type parameter.</summary>
public sealed record LibraryField {
    public required string Name { get; init; }
    public required LibraryTypeReference Type { get; init; }

    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsConst { get; init; }
    public bool IsPermutation { get; init; }
    public bool IsValueParameter { get; init; }
    public bool IsCompose { get; init; }
    public bool IsStream { get; init; }

    /// <summary>
    ///     The folded value of a <c>const</c>, or the literal a field was declared with.
    /// </summary>
    /// <remarks>
    ///     One field rather than the symbol layer's two. A permutation key's
    ///     <c>ConstantValue</c> is per-variant — it answers with what <em>this</em> compilation
    ///     was given — so it is not a property of the library; what travels is the declared
    ///     value, and a consumer's own <c>PermutationValues</c> take over from there.
    /// </remarks>
    public LibraryValue? DeclaredValue { get; init; }

    public ResourceKind ResourceKind { get; init; }
    public ResourceSet ResourceSet { get; init; } = ResourceSet.PerMaterial;
    public string? SemanticName { get; init; }
}

/// <summary>A callable member: <c>func</c>, <c>init</c> or an operator.</summary>
public sealed record LibraryMethod {
    public required string Name { get; init; }
    public MethodKind MethodKind { get; init; }
    public required LibraryTypeReference ReturnType { get; init; }
    public ImmutableArray<LibraryParameter> Parameters { get; init; } = [];
    public ImmutableArray<LibraryTypeParameter> TypeParameters { get; init; } = [];
    public bool IsStatic { get; init; }

    /// <summary>The stage this method is an entry point for, which a library does not export.</summary>
    public ShaderStage Stage { get; init; }

    public string? SemanticName { get; init; }

    /// <summary>
    ///     The IR function this method's body lowered to, or null when there is no body to link.
    /// </summary>
    /// <remarks>
    ///     Null for a protocol's declaration, which is bodyless by construction and is exactly
    ///     what a <c>compose</c> slot binds against — and for anything the writer declined to
    ///     export, which it reports rather than doing quietly (RVN5001).
    /// </remarks>
    public string? IrFunction { get; init; }
}

/// <summary>A parameter of a library method.</summary>
public sealed record LibraryParameter {
    public required string Name { get; init; }
    public required LibraryTypeReference Type { get; init; }
    public int Ordinal { get; init; }
    public bool HasDefaultValue { get; init; }
    public LibraryValue? DefaultValue { get; init; }
    public string? SemanticName { get; init; }

    /// <summary>
    ///     How the parameter passes its argument. Part of the exported signature rather than an
    ///     implementation detail: a consumer binding against this library has to know that the
    ///     argument must be an assignable place, and the IR it links has a by-reference parameter.
    /// </summary>
    public RefKind RefKind { get; init; }
}

/// <summary>A <c>var</c> member with accessors.</summary>
public sealed record LibraryProperty {
    public required string Name { get; init; }
    public required LibraryTypeReference Type { get; init; }
    public bool HasGetter { get; init; }
    public bool HasSetter { get; init; }
    public bool IsStatic { get; init; }

    /// <summary>The IR function the getter lowered to, when it has a body.</summary>
    public string? IrGetter { get; init; }

    /// <summary>The IR function the setter lowered to, when it has a body.</summary>
    public string? IrSetter { get; init; }
}

/// <summary>What shape a <see cref="LibraryTypeReference" /> takes.</summary>
public enum LibraryTypeKind {
    /// <summary>A type that was already an error when the library was written.</summary>
    Error,

    /// <summary>A scalar, vector or matrix, identified by its <see cref="SpecialType" />.</summary>
    Primitive,

    /// <summary>A compiler-supplied named type — a texture or a sampler.</summary>
    BuiltIn,

    /// <summary>A declared type, identified by qualified name.</summary>
    Named,

    Array,
    Tuple,

    /// <summary>A generic parameter of the declaring type or method, by name.</summary>
    TypeParameter,

    /// <summary>
    ///     A storage buffer. Structural like an array, and for the same reason: <c>Buffer&lt;T&gt;</c>
    ///     is spelled with angle brackets but has no declaration to resolve by name.
    /// </summary>
    Buffer
}

/// <summary>
///     A reference to a type from somewhere else in the artefact: a member's type, a base, a
///     constraint.
/// </summary>
/// <remarks>
///     Deliberately not a serialized <c>TypeSymbol</c>. A primitive travels as its
///     <see cref="SpecialType" />, which is the identity the whole binder keys off; a declared
///     type travels as a qualified name, which is stable across a recompilation of the library
///     and resolvable against another library's types. Only structural types — arrays and
///     tuples — carry their shape, because they have no name to be resolved by.
/// </remarks>
public sealed record LibraryTypeReference {
    public LibraryTypeKind Kind { get; init; }

    /// <summary>For <see cref="LibraryTypeKind.Primitive" /> and <see cref="LibraryTypeKind.BuiltIn" />.</summary>
    public SpecialType Special { get; init; }

    /// <summary>Qualified name for a declared type, simple name for a type parameter.</summary>
    public string? Name { get; init; }

    /// <summary>Element type of an array.</summary>
    public LibraryTypeReference? Element { get; init; }

    /// <summary>Array rank; <c>T[,]</c> is 2.</summary>
    public int Rank { get; init; }

    /// <summary>
    ///     Array length, or null when unsized. Part of the type rather than a detail of it:
    ///     <c>float[4]</c> and <c>float[]</c> are different types, so a signature that lost the
    ///     length would resolve to something the source never declared.
    /// </summary>
    public int? Length { get; init; }

    /// <summary>
    ///     For <see cref="LibraryTypeKind.Buffer" />: whether it is the <c>RWBuffer</c> form. Part of
    ///     the type, because a store into the read-only form is <c>RVN2119</c> and losing the
    ///     direction would silently make it legal.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>Element types of a tuple.</summary>
    public ImmutableArray<LibraryTypeReference> Elements { get; init; } = [];

    /// <summary>Element names of a tuple; an entry is null for an unnamed element.</summary>
    public ImmutableArray<string?> ElementNames { get; init; } = [];

    /// <summary>Type arguments, for a reference to a constructed generic type.</summary>
    public ImmutableArray<LibraryTypeReference> TypeArguments { get; init; } = [];

    public static LibraryTypeReference Primitive(SpecialType special) =>
        new() { Kind = LibraryTypeKind.Primitive, Special = special };

    public static LibraryTypeReference ErrorType => new() { Kind = LibraryTypeKind.Error };
}

/// <summary>
///     A compile-time value, rendered as text with the type that says how to read it back.
/// </summary>
/// <remarks>
///     Text rather than a boxed <c>object</c>, for the reason <c>.rvnfx</c>'s permutation key
///     already records: a boxed value survives <c>System.Text.Json</c> as a
///     <c>JsonElement</c> and stops comparing equal to what went in, which would make a
///     round-trip quietly lossy. The kind sits beside it, so nothing is lost by writing the
///     value the way source spells it.
/// </remarks>
/// <param name="Kind">How to parse <paramref name="Text" />: bool, int, uint, float or double.</param>
/// <param name="Text">The value, invariant-culture and round-trippable.</param>
public sealed record LibraryValue(string Kind, string Text) {
    /// <summary>Encodes a boxed constant, or null when there is nothing to encode.</summary>
    /// <remarks>
    ///     An unrepresentable value returns null rather than throwing: a library is written from
    ///     a compilation that has already bound cleanly, so the only values reaching here are
    ///     the five the IR can hold — but a null answer degrades to "no declared value", which
    ///     every consumer already handles.
    /// </remarks>
    public static LibraryValue? From(object? value) =>
        value switch {
            null => null,
            bool flag => new("bool", flag ? "true" : "false"),
            int number => new("int", number.ToString(CultureInfo.InvariantCulture)),
            uint number => new("uint", number.ToString(CultureInfo.InvariantCulture)),
            float number => new("float", number.ToString("R", CultureInfo.InvariantCulture)),
            double number => new("double", number.ToString("R", CultureInfo.InvariantCulture)),
            _ => null
        };

    /// <summary>Decodes back to the boxed constant, or null when the text does not parse.</summary>
    public object? ToObject() =>
        Kind switch {
            "bool" => bool.TryParse(Text, out var flag) ? flag : null,
            "int" => int.TryParse(Text, CultureInfo.InvariantCulture, out var number) ? number : null,
            "uint" => uint.TryParse(Text, CultureInfo.InvariantCulture, out var number) ? number : null,
            "float" => float.TryParse(Text, CultureInfo.InvariantCulture, out var number) ? number : null,
            "double" => double.TryParse(Text, CultureInfo.InvariantCulture, out var number) ? number : null,
            _ => null
        };
}
