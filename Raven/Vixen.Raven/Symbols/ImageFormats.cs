// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     One storage-image format: how the texels are stored, and what a shader reads them as.
/// </summary>
/// <param name="Name">
///     The GLSL layout qualifier, which is also the string a <c>[Format("…")]</c> carries.
/// </param>
/// <param name="SpirvValue">
///     The matching SPIR-V <c>ImageFormat</c> enumerant. Carried here rather than mapped in the
///     backend for the reason <c>BindingPlan</c> exists: a format's spelling in each target is one
///     decision, and two tables would be two chances to disagree.
/// </param>
/// <param name="Component">
///     What the shader sees. Always a four-lane vector, because <c>imageLoad</c> and
///     <c>OpImageRead</c> both hand back four components whatever the format stores — an
///     <c>r32f</c> image reads as <c>(r, 0, 0, 1)</c>. Only the component <em>class</em> is part of
///     the contract, which is why <c>rgba8</c> (eight bits, normalised) and <c>rgba32f</c> are both
///     <see cref="SpecialType.Float" />.
/// </param>
public sealed record ImageFormat(string Name, uint SpirvValue, SpecialType Component);

/// <summary>
///     The storage-image formats Raven admits.
/// </summary>
/// <remarks>
///     <para>
///         A format is <strong>required</strong> on a storage image rather than optional, and that
///         is the decision worth recording. GLSL needs the layout qualifier on any image that is
///         read, and SPIR-V needs a known <c>ImageFormat</c> or the module has to declare
///         <c>StorageImageReadWithoutFormat</c> — a capability not every device offers. Requiring
///         it means the same declaration compiles everywhere and the host knows exactly what to
///         create the view as.
///     </para>
///     <para>
///         A subset rather than all forty: these are the ones a post-process or a VFX dispatch
///         actually writes, and every one of them is in Vulkan's list of formats that
///         <em>must</em> support storage. Adding one is a line here and nothing else.
///     </para>
/// </remarks>
public static class ImageFormats {
    static readonly ImageFormat[] Known = [
        new("rgba32f", 1, SpecialType.Float),
        new("rgba16f", 2, SpecialType.Float),
        new("r32f", 3, SpecialType.Float),
        new("rgba8", 4, SpecialType.Float),
        new("rgba8_snorm", 5, SpecialType.Float),
        new("rg32f", 6, SpecialType.Float),
        new("rg16f", 7, SpecialType.Float),
        new("r16f", 9, SpecialType.Float),

        new("rgba32i", 21, SpecialType.Int),
        new("rgba16i", 22, SpecialType.Int),
        new("rgba8i", 23, SpecialType.Int),
        new("r32i", 24, SpecialType.Int),

        new("rgba32ui", 30, SpecialType.UInt),
        new("rgba16ui", 31, SpecialType.UInt),
        new("rgba8ui", 32, SpecialType.UInt),
        new("r32ui", 33, SpecialType.UInt)
    ];

    static readonly Dictionary<string, ImageFormat> ByName =
        Known.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The recognised format names, in the order a diagnostic should list them.</summary>
    /// <remarks>
    ///     An array rather than the dictionary's keys, for the reason <c>StageBuiltIns.Names</c>
    ///     is: this ends up in diagnostic text, and a message that varies between runs is a golden
    ///     test that fails for no reason.
    /// </remarks>
    public static string Names => string.Join(", ", Known.Select(f => f.Name));

    /// <summary>The format a <c>[Format("…")]</c> string names, or null.</summary>
    public static ImageFormat? Lookup(string? name) =>
        name is not null && ByName.TryGetValue(name, out var format) ? format : null;

    /// <summary>
    ///     The element type a format's images are read and written as: the four-lane vector of its
    ///     component class.
    /// </summary>
    public static PrimitiveTypeSymbol ElementType(ImageFormat format) {
        ArgumentNullException.ThrowIfNull(format);

        return format.Component switch {
            SpecialType.Int => BuiltInTypes.Int4,
            SpecialType.UInt => BuiltInTypes.UInt4,
            _ => BuiltInTypes.Float4
        };
    }
}
