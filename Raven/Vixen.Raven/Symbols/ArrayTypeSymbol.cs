// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
using System.Globalization;

namespace Vixen.Raven.Symbols;

/// <summary>An array type: <c>T[4]</c>, <c>T[]</c>, <c>T[,]</c>, <c>T[][]</c>.</summary>
/// <remarks>
///     <para>
///         A sized array and an unsized one are <em>different types</em>: <c>float[4]</c> is not
///         <c>float[]</c>, and neither converts to the other. That is not pedantry — the length is
///         part of the memory layout every backend needs, so a type that has one and a type that
///         does not cannot share a representation.
///     </para>
///     <para>
///         Only rank 1 can be sized. A multi-dimensional array is unsized because neither target
///         has one: GLSL and SPIR-V both spell <c>T[a][b]</c> as an array of arrays, which is what
///         two rank specifiers already build.
///     </para>
/// </remarks>
public sealed class ArrayTypeSymbol : TypeSymbol, IEquatable<ArrayTypeSymbol> {
    public TypeSymbol ElementType { get; }

    /// <summary>Number of dimensions; <c>T[,]</c> has rank 2.</summary>
    public int Rank { get; }

    /// <summary>The element count, or null when the array is unsized.</summary>
    public int? Length { get; }

    public override SymbolKind Kind => SymbolKind.ArrayType;
    public override TypeKind TypeKind => TypeKind.Array;
    public override string Name => string.Empty;
    public override Symbol? ContainingSymbol => null;

    /// <summary>
    ///     The element's, because an array of resources is a resource — one binding with a count.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without this an array of textures answered "not a resource", and every rule that asks
    ///         got the wrong answer at once. The worst of them was silent: the lowerer's binding kind
    ///         falls through to <c>Uniform</c> for anything that is not a declared resource, so
    ///         <c>var probes: TextureCube[4]</c> became a <em>member of the uniform block</em> — which
    ///         no backend can express, and which both of them emitted anyway. <c>glslc</c> rejects the
    ///         GLSL with "member of block cannot be or contain a sampler"; the SPIR-V put
    ///         <c>OpTypeImage</c> inside a <c>Block</c>-decorated struct, which passes
    ///         <c>spirv-val</c> and no driver.
    ///     </para>
    ///     <para>
    ///         The rest of the pipeline was already expecting this shape:
    ///         <c>ReflectionBuilder</c> unwraps an array into <c>(type, count)</c> and says so in a
    ///         comment. It was only the front end that did not agree.
    ///     </para>
    /// </remarks>
    public override ResourceKind ResourceKind => ElementType.ResourceKind;

    public ArrayTypeSymbol(TypeSymbol elementType, int rank = 1, int? length = null) {
        ElementType = elementType;
        Rank = rank;
        Length = rank == 1 ? length : null;
    }

    /// <summary>
    ///     Arrays expose <c>Length</c>; indexing is handled by the binder. On a sized array the
    ///     member is a compile-time constant, so <c>xs.Length</c> folds and can size another array.
    /// </summary>
    public override IReadOnlyList<Symbol> GetMembers() =>
        [new SynthesizedFieldSymbol(this, "Length", BuiltInTypes.Int, true, Length)];

    public override string ToDisplayString() =>
        ElementType.ToDisplayString()
        + "["
        + (Length?.ToString(CultureInfo.InvariantCulture) ?? new string(',', Rank - 1))
        + "]";

    public bool Equals(ArrayTypeSymbol? other) =>
        other is not null && Rank == other.Rank && Length == other.Length && ElementType.Equals(other.ElementType);

    public override bool Equals(object? obj) => Equals(obj as ArrayTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(ElementType, Rank, Length);
}
