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
///         ⚠ <b>The values are the shader's declared defaults, not a material's.</b> What a graph
///         reports is the list of names it needs from outside; what each one *is* comes from the
///         <c>.vxmat</c>, and until an author has set one the honest answer is the default the
///         declaration carries. This produces the feature with every value at its type's zero, which
///         a caller then overwrites from the material it is importing — see
///         <see cref="Values" /> for why the zero is not left to mean something.
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
            Maps = [.. source.Maps.Select(map => new GraphSurfaceMap(map.Texture, map.Slot))]
        };
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
