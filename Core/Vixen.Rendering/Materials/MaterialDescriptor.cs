// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Materials;

/// <summary>
///     A material as it is authored: which shader, which features, and how it is lit.
/// </summary>
/// <remarks>
///     <para>
///         The serialisable half of the material model, and deliberately a <em>parallel</em> model to
///         <see cref="Material" /> — the same split the compositor makes between
///         <c>GraphicsCompositorAsset</c> and the renderer it builds. This holds names and numbers;
///         the runtime material holds a parameter collection sized by a compiled layout and a
///         descriptor set full of device handles, and neither of those is a thing a file can hold.
///     </para>
///     <para>
///         <see cref="MaterialCompiler" /> is what turns one into the other, and everything it needs
///         is here: nothing about a descriptor requires a graphics device, a shader compiler or a
///         frame, so a material can be edited, validated and serialised in an editor that has not
///         opened a window yet.
///     </para>
/// </remarks>
[DataContract("Material")]
public sealed record MaterialDescriptor {
    /// <summary>The shading pass this material is authored against.</summary>
    /// <remarks>
    ///     A name rather than a reference, because it is the same name the effect key carries and
    ///     the compiler resolves. <c>ForwardPlus</c> is the pass doc 06 makes the default; a material
    ///     for a stage that draws with something else names that instead.
    /// </remarks>
    public string ShaderName { get; init; } = "ForwardPlus";

    /// <summary>The features, in the order they contribute.</summary>
    /// <remarks>
    ///     Order is load-bearing and cheap to get wrong: a feature reads the surface as the previous
    ///     one left it, so occlusion after a base workflow multiplies into what the workflow wrote,
    ///     and before it multiplies into a default nobody set. The list is authored top to bottom for
    ///     that reason rather than sorted into some canonical order here.
    /// </remarks>
    public IReadOnlyList<IMaterialFeature> Features { get; init; } = [];

    /// <summary>What the material does with the light that reaches it.</summary>
    public IMaterialShading Shading { get; init; } = new StandardShading();
}
