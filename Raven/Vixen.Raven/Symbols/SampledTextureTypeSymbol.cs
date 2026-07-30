// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     An integer-sampled texture: <c>Texture2D&lt;uint4&gt;</c> or <c>Texture2D&lt;int4&gt;</c> —
///     a sampled image whose texels are integers rather than floats.
/// </summary>
/// <remarks>
///     <para>
///         Written with angle brackets and, like <see cref="StorageImageTypeSymbol" />, <em>not</em>
///         a generic type: the binder constructs it structurally, so there is no declaration to
///         instantiate and no body to substitute through.
///     </para>
///     <para>
///         <strong>This exists because the component type is part of the descriptor.</strong> A
///         sampled image's <c>OpTypeImage</c> states what a read returns, and Vulkan checks it
///         against the bound view's format: an <c>R32_UINT</c> view behind a float-sampled
///         <c>Texture2D</c> is a validation error however the shader then bitcasts the value. The
///         plain <c>Texture2D</c> stays the float-sampled texture every material reads; this is the
///         one a shader declares when the host binds an integer view, such as a visibility buffer.
///     </para>
///     <para>
///         <strong>The element is a four-lane vector, not a scalar,</strong> for the reason a
///         storage image's is: <c>OpImageFetch</c> and <c>texelFetch</c> hand back four components
///         whatever the format stores, so an <c>R32_UINT</c> texel reads as <c>(r, 0, 0, 1)</c> and
///         the caller takes <c>.x</c>. Float elements are refused rather than accepted as a second
///         spelling of the plain <c>Texture2D</c> — see <c>RVN2136</c>.
///     </para>
///     <para>
///         <c>Load</c> and <c>GetDimensions</c> only. An integer texture has no filtering to do —
///         Vulkan formats with a <c>UINT</c> component are not filterable — so a <c>Sample</c> here
///         would be a member that cannot work on any view the type is for. No <c>[Format]</c>
///         either: a sampled image's SPIR-V format is <c>Unknown</c>, and what the descriptor
///         checks is the component type this symbol already states.
///     </para>
/// </remarks>
public sealed class SampledTextureTypeSymbol : TypeSymbol, IEquatable<SampledTextureTypeSymbol> {
    /// <summary>The source name, shared with the float-sampled built-in.</summary>
    public const string Texture2DName = "Texture2D";

    Symbol[]? members;

    /// <summary>What a load returns: a four-lane vector of <c>int</c> or <c>uint</c>.</summary>
    public PrimitiveTypeSymbol ElementType { get; }

    public override SymbolKind Kind => SymbolKind.NamedType;
    public override TypeKind TypeKind => TypeKind.Resource;
    public override ResourceKind ResourceKind => ResourceKind.Texture;
    public override string Name => Texture2DName;
    public override Symbol? ContainingSymbol => null;

    public SampledTextureTypeSymbol(PrimitiveTypeSymbol elementType) {
        ElementType = elementType;
    }

    /// <summary>
    ///     <c>Load</c> and <c>GetDimensions</c>, with the plain <c>Texture2D</c>'s signatures and
    ///     this element's return type.
    /// </summary>
    /// <remarks>
    ///     Built per instance rather than shared on a singleton, for the same reason
    ///     <see cref="StorageImageTypeSymbol" />'s are: <c>Load</c>'s return depends on the element.
    /// </remarks>
    public override IReadOnlyList<Symbol> GetMembers() =>
        members ??= [
            new SynthesizedMethodSymbol(this, "Load", ElementType, [("coordinate", BuiltInTypes.Int3)]),
            new SynthesizedMethodSymbol(this, "GetDimensions", BuiltInTypes.Int2, [("lod", BuiltInTypes.Int)])
        ];

    public override string ToDisplayString() => $"{Name}<{ElementType.ToDisplayString()}>";

    public bool Equals(SampledTextureTypeSymbol? other) =>
        other is not null && ElementType.Equals(other.ElementType);

    public override bool Equals(object? obj) => Equals(obj as SampledTextureTypeSymbol);

    public override int GetHashCode() => HashCode.Combine(Texture2DName, ElementType);
}
