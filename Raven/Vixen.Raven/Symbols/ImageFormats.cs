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
/// <param name="RequiresExtendedFormats">
///     Whether a module naming this format has to declare SPIR-V's
///     <c>StorageImageExtendedFormats</c>, and so whether the device has to have
///     <c>shaderStorageImageExtendedFormats</c>.
/// </param>
public sealed record ImageFormat(
    string Name,
    uint SpirvValue,
    SpecialType Component,
    bool RequiresExtendedFormats = false
);

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
///         actually writes. Adding one is a line here and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Thirteen of the sixteen are in Vulkan's list of formats that <em>must</em> support
///         storage, and this paragraph used to claim all sixteen were.</b> The mandatory set is
///         <c>R32_{UINT,SINT,SFLOAT}</c>, <c>R32G32B32A32_{UINT,SINT,SFLOAT}</c>,
///         <c>R16G16B16A16_{UINT,SINT,SFLOAT}</c> and <c>R8G8B8A8_{UNORM,SNORM,UINT,SINT}</c> —
///         which does cover <c>rgba8_snorm</c>, so the exceptions are exactly <c>rg32f</c>,
///         <c>rg16f</c> and <c>r16f</c>. Those three are the <em>extended</em> list, and a device
///         offers them for storage only when <c>shaderStorageImageExtendedFormats</c> is set. SPIR-V
///         draws the same line at the same place, which is what
///         <see cref="ImageFormat.RequiresExtendedFormats" /> records: their <c>ImageFormat</c>
///         enumerants require the <c>StorageImageExtendedFormats</c> capability and the other
///         thirteen require only <c>Shader</c>.
///     </para>
///     <para>
///         It is not pedantry, because the three are reachable. <c>Vixen.Editor.TextureGraph</c>'s
///         <c>TextureFormats.RavenName</c> spells <c>R16Float</c> as <c>r16f</c> and rewrites a
///         kernel's <c>[Format("…")]</c> to it, and <c>R16Float</c> is the recommended format for a
///         height field. No committed <c>.rvn</c> names one of the three — <c>Ripples.rvn</c> says in
///         so many words that it takes <c>rgba16f</c> over <c>rg16f</c> for exactly this reason — so
///         the whole exposure is through kernels built at run time, which no shader gate compiles.
///     </para>
/// </remarks>
public static class ImageFormats {
    static readonly ImageFormat[] Known = [
        new("rgba32f", 1, SpecialType.Float),
        new("rgba16f", 2, SpecialType.Float),
        new("r32f", 3, SpecialType.Float),
        new("rgba8", 4, SpecialType.Float),
        new("rgba8_snorm", 5, SpecialType.Float),
        // ⚠ The three that are not mandatory. SPIR-V's Rg32f, Rg16f and R16f enumerants require the
        // StorageImageExtendedFormats capability; every other row here requires only Shader.
        new("rg32f", 6, SpecialType.Float, RequiresExtendedFormats: true),
        new("rg16f", 7, SpecialType.Float, RequiresExtendedFormats: true),
        new("r16f", 9, SpecialType.Float, RequiresExtendedFormats: true),

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
