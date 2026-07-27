// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>A storage buffer: <c>Buffer&lt;T&gt;</c> read-only, <c>RWBuffer&lt;T&gt;</c> read-write.</summary>
/// <remarks>
///     <para>
///         Written with angle brackets but <em>not</em> a generic type. It is a structural type the
///         binder constructs directly, the same treatment <see cref="ArrayTypeSymbol" /> gets for
///         <c>T[4]</c>. That is why it needs no monomorphisation even now that Raven has some:
///         there is no declaration to instantiate and no body to substitute through.
///     </para>
///     <para>
///         One buffer concept, not HLSL's several. A typed (texel) buffer is a different descriptor
///         with no advantage on either target Raven emits for, and <c>ByteAddressBuffer</c> trades the
///         element type — the thing that makes an offset checkable — for manual byte arithmetic. So a
///         buffer has an element type, and the element type is the contract.
///     </para>
///     <para>
///         Read-only versus read-write is one bit rather than two descriptor types, because in Vulkan
///         both are <c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER</c>. The difference is an access decoration —
///         <c>NonWritable</c> in SPIR-V, <c>readonly</c> in GLSL — which is worth declaring: a driver
///         can hoist loads out of a loop from a read-only buffer and cannot from a writable one.
///     </para>
/// </remarks>
public sealed class BufferTypeSymbol : TypeSymbol, IEquatable<BufferTypeSymbol> {
    /// <summary>The source name of the read-only form.</summary>
    public const string ReadOnlyName = "Buffer";

    /// <summary>The source name of the read-write form.</summary>
    public const string ReadWriteName = "RWBuffer";

    public TypeSymbol ElementType { get; }

    /// <summary>Whether an element may be assigned to.</summary>
    public bool IsWritable { get; }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override TypeKind TypeKind => TypeKind.Resource;
    public override ResourceKind ResourceKind => ResourceKind.StorageBuffer;
    public override bool IsWritableResource => IsWritable;
    public override string Name => IsWritable ? ReadWriteName : ReadOnlyName;
    public override Symbol? ContainingSymbol => null;

    public BufferTypeSymbol(TypeSymbol elementType, bool isWritable) {
        ElementType = elementType;
        IsWritable = isWritable;
    }

    /// <summary>
    ///     A buffer exposes <c>Length</c>; indexing is handled by the binder.
    /// </summary>
    /// <remarks>
    ///     Not a constant, unlike a sized array's: the host decides how many elements it bound, and
    ///     both targets can ask at run time — <c>data.length()</c> in GLSL, <c>OpArrayLength</c> in
    ///     SPIR-V. That is the whole point of a buffer over an array, and it is why the
    ///     <c>ArrayLength</c> intrinsic exists.
    /// </remarks>
    public override IReadOnlyList<Symbol> GetMembers() =>
        [new SynthesizedFieldSymbol(this, "Length", BuiltInTypes.Int, true)];

    public override string ToDisplayString() => $"{Name}<{ElementType.ToDisplayString()}>";

    public bool Equals(BufferTypeSymbol? other) =>
        other is not null && IsWritable == other.IsWritable && ElementType.Equals(other.ElementType);

    public override bool Equals(object? obj) => Equals(obj as BufferTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(ElementType, IsWritable);
}
