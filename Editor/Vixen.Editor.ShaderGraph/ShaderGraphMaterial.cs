// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Rendering.Materials;

namespace Vixen.Editor.ShaderGraph;

/// <summary>
///     A compiled graph, as the material feature a <c>.vxmat</c> composes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The join, and it is mechanical because the two halves were built to meet.</b> A
///         <see cref="ShaderGraphKind.Surface" /> graph emits <c>shader N : IMaterialSurface</c>, and
///         an <see cref="IMaterialFeature" /> is a name for exactly that plus the values behind it.
///         So this reads <see cref="ShaderGraphSource.Properties" /> and
///         <see cref="ShaderGraphSource.Maps" /> and hands back a feature — no naming rule of its
///         own, no second convention to keep in step.
///     </para>
///     <para>
///         ⚠ <b><see cref="Feature" /> has no production caller, and the sentence that used to be
///         here named one that does not exist</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1126">#1126</a>. It said the zeros are
///         "overwritten by a caller from the material it is importing"; there is no importer and no
///         other caller, in <c>.cs</c> or in <c>.vxml</c>. <c>MaterialDocument.SetGraphValue</c> is
///         the editor's only feature-writing path and cannot be it: a <c>GraphSurfaceNumber</c>
///         entry <em>overrides</em> the generated shader's declared default, so writing every
///         property out the moment a panel opened would replace every graph default with black.
///         That panel therefore leaves an untouched property <em>out</em>, and this deliberately
///         does not.
///     </para>
///     <para>
///         ⚠ <b>The "first composition" that would justify the zeros is not a moment this editor
///         has.</b> <c>MaterialHeaderEdits.Graph</c> is a bare <c>[Inspector]</c> property: linking
///         a graph to a material writes the link and no feature at all, and a material with no
///         <see cref="GraphSurfaceFeature" /> does not compose the graph's surface — nothing else
///         reads <c>MaterialAsset.Graph</c> at draw time. So the graph does nothing until an author
///         nudges a value, and the call that fixes that wants no numbers rather than zeroed ones.
///         ⚠ Neither half is covered: the one fixture that composes this — the golden
///         <c>GraphMaterialImageTests</c> — builds its surface from constants on the master node
///         rather than property nodes, so <see cref="ShaderGraphSource.Properties" /> is empty and
///         the picture cannot distinguish this projection from no projection at all.
///     </para>
///     <para>
///         <b>Here rather than in the importer</b>, because the names being joined are this
///         compiler's own. An assembly that read a file and reconstructed <c>albedo</c> ⇄
///         <c>albedoIndex</c> would be a second place the generator's convention is written down.
///     </para>
/// </remarks>
public static class ShaderGraphMaterial {
    /// <summary>The feature a material composes to draw with this graph.</summary>
    /// <param name="source">The compiled graph.</param>
    /// <param name="values">
    ///     What the material sets, by the name the graph declares. A name the graph does not declare
    ///     is ignored, and one the material does not set takes its type's zero.
    /// </param>
    /// <returns>The feature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The graph is a standalone shader, which is not a thing a material can compose.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>A standalone graph is refused rather than converted.</b> Its shader has stages,
    ///     transforms and a <c>return</c>, and binding it into a <c>compose</c> slot typed
    ///     <c>IMaterialSurface</c> is a Raven error about generated text — reported against a
    ///     material whose author never saw that text and cannot act on it. The graph has the wrong
    ///     master, and that is the sentence worth saying.
    /// </remarks>
    public static GraphSurfaceFeature Feature(
        ShaderGraphSource source,
        IReadOnlyDictionary<string, Vector4>? values = null
    ) {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Kind != ShaderGraphKind.Surface) {
            throw new ArgumentException(
                $"'{source.Name}' compiles to a standalone shader, which a material cannot compose. Give "
                + "the graph a Master/Surface node.",
                nameof(source)
            );
        }

        List<GraphSurfaceNumber> numbers = [];
        List<GraphSurfaceVector> vectors = [];

        foreach (var property in source.Properties) {
            // The index a texture is read through is not a value a material sets — a host writes it
            // from the bindless table, and `GraphSurfaceFeature.Compile` seeds it at the table's
            // fallback. Listing it here would offer an author a number to type that is overwritten
            // every frame.
            if (string.Equals(property.Type, "uint", StringComparison.Ordinal)) {
                continue;
            }

            var value = values is not null && values.TryGetValue(property.Name, out var set) ? set : Vector4.Zero;

            switch (property.Type) {
                case "float":
                    numbers.Add(new(property.Name, value.X));

                    break;

                case "float4":
                    vectors.Add(new(property.Name, value));

                    break;

                default:
                    // Every other declaration is the engine's rather than the material's — the clock a
                    // `Time` node reads, the light a standalone PBR master shades with. A surface graph
                    // declares none of them today, and one that starts to should reach the frame's own
                    // values rather than a number in a `.vxmat`.
                    break;
            }
        }

        return new() {
            Shader = source.Name,
            Numbers = [.. numbers],
            Vectors = [.. vectors],
            Maps = Maps(source)
        };
    }

    /// <summary>The texture pairing a material composing this graph carries.</summary>
    /// <param name="source">The compiled graph.</param>
    /// <returns>One entry per slot the shader declares, in the order it declares them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Public because <see cref="Feature" /> is not the only thing that has to build
    ///         one</b> — <c>MaterialDocument.SetGraphValue</c> writes a feature an author is editing
    ///         a value on, and that one cannot go through <see cref="Feature" />: it must leave a
    ///         property the author has not touched <em>out</em> of the feature, where
    ///         <see cref="Feature" /> writes every declared property at its type's zero and so would
    ///         replace every graph default with black the moment a panel opened.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The maps are the half those two do agree on, and they were two spellings of one
    ///         line</b> — the join from <c>ShaderGraphSource.Maps</c>' texture-and-slot pair to
    ///         <see cref="GraphSurfaceMap" />. <c>AssetMaterialSource.Pair</c> keys the bindless
    ///         table on <c>{shader}.{chain}.{graph}.{slot}</c>, so a copy that dropped or renamed a
    ///         slot writes nothing for it and every read of that texture falls back to the table's
    ///         placeholder view: a wrong picture with no error. One line is what this is for.
    ///     </para>
    /// </remarks>
    public static GraphSurfaceMap[] Maps(ShaderGraphSource source) {
        ArgumentNullException.ThrowIfNull(source);

        return [.. source.Maps.Select(map => new GraphSurfaceMap(map.Texture, map.Slot))];
    }

    /// <summary>Every property a material is expected to set, and the width each is.</summary>
    /// <param name="source">The compiled graph.</param>
    /// <returns>The names, in the order the shader declares them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <remarks>
    ///     What a material inspector offers an author, and it is deliberately narrower than
    ///     <see cref="ShaderGraphSource.Properties" />: that list is every name the shader declares,
    ///     including the texture slots a host owns. This is the ones a person fills in.
    /// </remarks>
    public static ImmutableArray<ShaderGraphProperty> Values(ShaderGraphSource source) {
        ArgumentNullException.ThrowIfNull(source);

        return [
            .. source.Properties.Where(property =>
                property.Type is "float" or "float4"
            )
        ];
    }
}
