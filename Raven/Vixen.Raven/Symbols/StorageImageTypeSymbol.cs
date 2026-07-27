// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     A storage image: <c>RWTexture2D&lt;float4&gt;</c>, <c>RWTexture3D&lt;float4&gt;</c> — a
///     texture a shader stores into.
/// </summary>
/// <remarks>
///     <para>
///         Written with angle brackets and, like <see cref="BufferTypeSymbol" />, <em>not</em> a
///         generic type: the binder constructs it structurally, so there is no declaration to
///         instantiate and no body to substitute through.
///     </para>
///     <para>
///         <strong>The element is always a four-lane vector,</strong> because that is what both
///         targets do: <c>imageLoad</c> and <c>OpImageRead</c> hand back four components whatever
///         the format stores, so an <c>r32f</c> image reads as <c>(r, 0, 0, 1)</c>. Narrowing the
///         declared type to the format's channel count would be a shape neither target has.
///     </para>
///     <para>
///         Only the read-write form exists. A texture a shader merely reads is a
///         <c>Texture2D</c> — a sampled image, which filters and mips and is what a read almost
///         always wants. A storage image that is never written buys nothing over that, so there is
///         no <c>Texture2DStorage</c> to choose wrongly between.
///     </para>
///     <para>
///         Not sampled, and that is the part that surprises: a storage image has no sampler, no
///         filtering and no mip chain. Reads are by integer texel, exactly like <c>Load</c> on a
///         texture, which is why the members below are <c>Load</c>/<c>Store</c> rather than
///         <c>Sample</c>.
///     </para>
/// </remarks>
public sealed class StorageImageTypeSymbol : TypeSymbol, IEquatable<StorageImageTypeSymbol> {
    /// <summary>The source name of the two-dimensional form.</summary>
    public const string Texture2DName = "RWTexture2D";

    /// <summary>The source name of the three-dimensional form.</summary>
    public const string Texture3DName = "RWTexture3D";

    Symbol[]? members;

    /// <summary>What a load returns and a store takes: always a four-lane vector.</summary>
    public PrimitiveTypeSymbol ElementType { get; }

    /// <summary>Whether the image is addressed by two coordinates or three.</summary>
    public bool IsVolume { get; }

    /// <summary>
    ///     How the texels are stored, from the declaration's <c>[Format("…")]</c>. Null until a
    ///     declaration supplies one, which <c>RVN2123</c> requires it to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Part of the <em>type</em> rather than of the binding, because it is part of the type
    ///         in SPIR-V: <c>OpTypeImage</c> carries an <c>ImageFormat</c>, so two images with
    ///         different formats are different types and a function parameter has to say which one
    ///         it takes. That is why a parameter of image type carries its own <c>[Format]</c>.
    ///     </para>
    ///     <para>
    ///         It is deliberately <em>not</em> part of the element type. What the shader sees is
    ///         four lanes of <c>float</c>, <c>int</c> or <c>uint</c>, whether the storage is eight
    ///         bits normalised or thirty-two floating — so <c>rgba8</c> and <c>rgba32f</c> are both
    ///         read as <c>float4</c>, and the arithmetic a shader writes does not change with the
    ///         format.
    ///     </para>
    /// </remarks>
    public string? Format { get; private init; }

    /// <summary>The integer vector one texel is addressed by.</summary>
    public PrimitiveTypeSymbol CoordinateType => IsVolume ? BuiltInTypes.Int3 : BuiltInTypes.Int2;

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override TypeKind TypeKind => TypeKind.Resource;
    public override ResourceKind ResourceKind => ResourceKind.StorageImage;

    /// <summary>
    ///     Always true, but stored into through <c>Store</c> rather than by assignment.
    /// </summary>
    /// <remarks>
    ///     The image itself is never the target of an <c>=</c> — <c>RVN2119</c> still refuses that,
    ///     as it does for every binding. What this reports is that the descriptor is a writable
    ///     one, which is what the backends turn into the absence of a <c>readonly</c>/
    ///     <c>NonWritable</c> decoration.
    /// </remarks>
    public override bool IsWritableResource => true;

    public override string Name => IsVolume ? Texture3DName : Texture2DName;
    public override Symbol? ContainingSymbol => null;

    public StorageImageTypeSymbol(PrimitiveTypeSymbol elementType, bool isVolume) {
        ElementType = elementType;
        IsVolume = isVolume;
    }

    /// <summary>
    ///     <c>Load</c>, <c>Store</c> and <c>GetDimensions</c>, typed for this image's element and
    ///     coordinate.
    /// </summary>
    /// <remarks>
    ///     Built per instance rather than shared on a singleton, for the same reason
    ///     <see cref="BufferTypeSymbol" />'s <c>Length</c> is: the signatures depend on the type
    ///     arguments, so there is no one set of members a shared type could carry.
    /// </remarks>
    public override IReadOnlyList<Symbol> GetMembers() =>
        members ??= [
            new SynthesizedMethodSymbol(this, "Load", ElementType, [("coordinate", CoordinateType)]),
            new SynthesizedMethodSymbol(
                this,
                "Store",
                BuiltInTypes.Void,
                [("coordinate", CoordinateType), ("value", ElementType)]
            ),

            // No level: a storage image is a single mip, so there is nothing to ask about.
            new SynthesizedMethodSymbol(this, "GetDimensions", CoordinateType, [])
        ];

    /// <summary>The same image with its declared texel format attached.</summary>
    public StorageImageTypeSymbol WithFormat(string? format) => new(ElementType, IsVolume) { Format = format };

    public override string ToDisplayString() => $"{Name}<{ElementType.ToDisplayString()}>";

    public bool Equals(StorageImageTypeSymbol? other) =>
        other is not null
        && IsVolume == other.IsVolume
        && ElementType.Equals(other.ElementType)
        && string.Equals(Format, other.Format, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as StorageImageTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(ElementType, IsVolume, Format?.ToLowerInvariant());
}
