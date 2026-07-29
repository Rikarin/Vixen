// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Rendering;

/// <summary>
///     Which location a vertex stage reads each of its attributes from.
/// </summary>
/// <remarks>
///     <para>
///         <b>The number is the shader's, not the renderer's</b>, and that is the whole reason this
///         type exists. A renderer knows its vertex struct — the field order, the formats, the byte
///         offsets — and cannot know where the stage it was handed reads them, because that is a
///         property of the source. Raven's <c>StreamPlan</c> locates a stage's own parameters after
///         the shader's streams, so <c>position</c> is location 0 in a shader with no streams and
///         location 3 in one with three, and adding a stream renumbers every attribute under it.
///     </para>
///     <para>
///         ⚠ <b>Getting it wrong is not a validation error.</b> A pipeline whose vertex layout names
///         a location the stage does not declare simply binds nothing to that attribute, and the
///         stage reads whatever the driver left there — a mesh drawn with its normals in the colour
///         slot, on one driver, silently. Which is why this is passed rather than assumed, and why
///         the count is checked.
///     </para>
///     <para>
///         The default is 0, 1, 2, … in declaration order, which is what <c>glslc</c> output has and
///         therefore what every caller handing over hand-written GLSL wants. So a renderer that is
///         given nothing behaves exactly as it did before there was anything to give.
///     </para>
///     <para>
///         Where the numbers come from is <c>Vixen.Shaders.Generators</c>: a shader's
///         <c>.reflect.json</c> carries its vertex inputs, and the generator emits a
///         <c>…Location</c> constant per attribute beside the <c>…Set</c> and <c>…Binding</c> ones.
///     </para>
/// </remarks>
public readonly record struct VertexLocations {
    readonly ImmutableArray<uint> locations;

    /// <summary>Locations in the renderer's own attribute order.</summary>
    /// <param name="locations">
    ///     One per attribute. Empty means declaration order, which is the default.
    /// </param>
    public VertexLocations(params ReadOnlySpan<uint> locations) => this.locations = [.. locations];

    /// <summary>How many were supplied, or zero for declaration order.</summary>
    public int Count => locations.IsDefault ? 0 : locations.Length;

    /// <summary>The location of the attribute at <paramref name="index" /> in declaration order.</summary>
    /// <param name="index">Which attribute, counting from the first the vertex struct declares.</param>
    public uint this[int index] => Count == 0 ? (uint) index : locations[index];

    /// <summary>
    ///     Throws when a caller supplied a number of locations that is not this renderer's number of
    ///     attributes.
    /// </summary>
    /// <param name="expected">How many attributes the vertex struct has.</param>
    /// <param name="name">The renderer, for the message.</param>
    /// <remarks>
    ///     Checked at construction rather than left to indexing, because the failure this guards is a
    ///     caller who passed three of four: the fourth would fall back to its index and bind against
    ///     a location nothing declares, which is the silent case above rather than a throw.
    /// </remarks>
    public void Require(int expected, string name) {
        if (Count != 0 && Count != expected) {
            throw new ArgumentException(
                $"'{name}' takes {expected} vertex attributes, so {expected} locations or none — not {Count}.",
                nameof(expected)
            );
        }
    }
}
