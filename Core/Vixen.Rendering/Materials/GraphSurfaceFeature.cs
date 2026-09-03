// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Materials;

/// <summary>One number a graph-authored surface asks its material for.</summary>
/// <param name="Name">What the generated shader declares it as.</param>
/// <param name="Value">Its value.</param>
/// <remarks>
///     ⚠ <b>A kind per width rather than one record with a width field</b>, which is the same
///     decision <c>IMaterialParameter</c>'s own remarks record and for the same reason: a single
///     value type carrying a <see cref="Vector4" /> and a lane count would put three dead floats in
///     every line of every graph material, and would give an inspector one editor to draw for two
///     different things. The shader graph declares exactly two widths today — <c>Input/Float
///     Property</c> and <c>Input/Colour Property</c> — so there are exactly two of these.
/// </remarks>
[DataContract("GraphNumber")]
public sealed record GraphSurfaceNumber(string Name, float Value) {
    /// <summary>For the binder, which builds a value and then fills it.</summary>
    public GraphSurfaceNumber()
        : this(string.Empty, 0f) { }

    /// <inheritdoc cref="GraphSurfaceNumber" />
    public string Name { get; init; } = Name;

    /// <inheritdoc cref="GraphSurfaceNumber" />
    public float Value { get; init; } = Value;
}

/// <summary>Four numbers a graph-authored surface asks its material for.</summary>
/// <param name="Name">What the generated shader declares it as.</param>
/// <param name="Value">Its value.</param>
/// <remarks>
///     <inheritdoc cref="GraphSurfaceNumber" path="/remarks" />
///     <para>
///         A <see cref="Vector4" /> and not a colour, because a graph's <c>float4</c> is as often a
///         packed mask or a set of thresholds as it is a tint — the same distinction
///         <c>VectorParameter</c> and <c>ColourParameter</c> keep in the authoring format, resolved
///         here in favour of the one that cannot lose information.
///     </para>
/// </remarks>
[DataContract("GraphVector")]
public sealed record GraphSurfaceVector(string Name, Vector4 Value) {
    /// <summary>For the binder, which builds a value and then fills it.</summary>
    public GraphSurfaceVector()
        : this(string.Empty, Vector4.Zero) { }

    /// <inheritdoc cref="GraphSurfaceVector" />
    public string Name { get; init; } = Name;

    /// <inheritdoc cref="GraphSurfaceVector" />
    public Vector4 Value { get; init; } = Value;
}

/// <summary>A texture a graph-authored surface samples, and the slot it reads it through.</summary>
/// <param name="Texture">
///     What the material calls it — the name a <see cref="MaterialTexture" /> assigns under.
/// </param>
/// <param name="Slot">The <c>uint</c> the generated shader declares, which a host writes an index into.</param>
/// <remarks>
///     ⚠ <b>Both names are carried, though one generator wrote both.</b> A convention could derive
///     <c>albedoIndex</c> from <c>albedo</c> — the shader graph is what spells it that way — and a
///     runtime that reads this file never ran the generator and has no way to know that. The engine's
///     rule is that a name-to-name join is explicit wherever the two names belong to different
///     things, because a guess there leaves the slot at zero, and zero is a valid index holding some
///     other material's texture.
/// </remarks>
[DataContract("GraphMap")]
public sealed record GraphSurfaceMap(string Texture, string Slot) {
    /// <summary>For the binder, which builds a value and then fills it.</summary>
    public GraphSurfaceMap()
        : this(string.Empty, string.Empty) { }

    /// <inheritdoc cref="GraphSurfaceMap" />
    public string Texture { get; init; } = Texture;

    /// <inheritdoc cref="GraphSurfaceMap" />
    public string Slot { get; init; } = Slot;
}

/// <summary>
///     A surface authored as a shader graph rather than chosen from the library.
/// </summary>
/// <remarks>
///     <para>
///         <b>The end of the graph story, and it needed no new machinery to be.</b> A shader graph
///         compiled to Raven and nothing consumed it, because the shape it emitted — a whole shader
///         with its own transforms and its own stages — is one nothing in this engine can put on a
///         mesh. <c>Master/Surface</c> emits an <c>IMaterialSurface</c> instead, and an
///         <c>IMaterialSurface</c> is precisely what a feature names. So a graph-authored material is
///         a material with one more feature in its list, composed into <c>CompositeSurface</c> beside
///         <see cref="MetalRoughnessFeature" />, and everything after that — the effect key, the
///         parameter block, the lighting, the shadows, the bindless table — is the path every other
///         material already takes.
///     </para>
///     <para>
///         ⚠ <b>The one thing this has that no other feature does is a <see cref="ShaderName" /> it
///         does not know at compile time.</b> Every hand-written feature returns a constant, because
///         the shader it names is a file somebody committed; this one names a shader a build
///         generated from a <c>.vxshadergraph</c>, so the name is data. That is also the only way
///         this can be wrong in a way nothing catches: a material naming a graph whose shader was
///         never compiled resolves to an effect miss, which is a draw that does not happen.
///     </para>
///     <para>
///         <b>It carries values and names, and no handle</b> — "materials are values, not resources",
///         as <see cref="IMaterialFeature" /> puts it. <see cref="Maps" /> is a pairing of names, and
///         what turns one into something a shader can index is a host with a
///         <see cref="Graphics.BindlessTable" />; see
///         <see cref="Features.MaterialRenderFeature.TextureIndices" />.
///     </para>
/// </remarks>
[DataContract("GraphSurface")]
public sealed record GraphSurfaceFeature : IMaterialFeature {
    /// <summary>The shader the graph compiled to, which is the graph's own name.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty is refused rather than composed.</b> A slot bound to an empty name is a
    ///     composition Raven cannot resolve and a message about a shader called nothing; the
    ///     diagnostic is <see cref="MaterialDiagnosticId.UnnamedShader" /> and it names the material
    ///     rather than the compilation. This is the zeroed field whose zero looks valid that this
    ///     renderer keeps meeting.
    /// </remarks>
    public string Shader { get; init; } = string.Empty;

    /// <summary>The graph's <c>float</c> properties.</summary>
    public GraphSurfaceNumber[] Numbers { get; init; } = [];

    /// <summary>The graph's <c>float4</c> properties.</summary>
    public GraphSurfaceVector[] Vectors { get; init; } = [];

    /// <summary>The textures it samples, paired with the slots it reads them through.</summary>
    public GraphSurfaceMap[] Maps { get; init; } = [];

    /// <inheritdoc />
    public string ShaderName => Shader;

    /// <summary>What the shader calls a texture's slot, under a composition path.</summary>
    /// <param name="path">
    ///     The qualified prefix the feature was composed under, as
    ///     <see cref="MaterialCompilationContext" /> builds it.
    /// </param>
    /// <param name="slot">The slot's own name, from <see cref="Maps" />.</param>
    /// <returns>The parameter a host writes the table index into.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="slot" /> is empty.</exception>
    /// <inheritdoc cref="TexturedMetalRoughnessFeature.BaseColorIndexParameter" path="/remarks" />
    public static string IndexParameter(string path, string slot) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrEmpty(slot);

        return path + slot;
    }

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var number in Numbers) {
            context.Set(number.Name, number.Value);
        }

        foreach (var vector in Vectors) {
            context.Set(vector.Name, vector.Value);
        }

        foreach (var map in Maps) {
            // Zero, and it stays zero unless a host with a table writes a slot over it — the same
            // bargain `TexturedMetalRoughnessFeature` makes, and for the same reason: slot zero is
            // the table's fallback view, so a material whose map never reached a table samples
            // something defined and visibly wrong rather than an unwritten descriptor.
            context.Set(map.Slot, 0u);
        }
    }
}
