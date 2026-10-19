// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

namespace Vixen.Editor.Terrain;

/// <summary>The three Create ▸ lines for the file kinds a terrain is painted and planted with.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Declared, and they used to be the application's.</b> Until #1287 these were three rows
///         of <c>EditorApplication.BuiltInAssetKinds</c> with their starter texts beside them — an
///         application spelling <c>.vxlayer</c>, <c>.vxfoliage</c> and <c>.vxgrass</c>, which doc 50
///         § F9 counts as a shell naming a feature. <c>[CreateAssetMenu]</c> is read by
///         <c>Vixen.Editor.Plugin.Generator</c> into <c>Declared.Register</c>, which
///         <see cref="TerrainModule.Activate" /> calls, so the lines arrive and leave with the module
///         that also contributes these kinds' icons.
///     </para>
///     <para>
///         ⚠ <b>The ids are the old ones, written out</b>, because a derived id would be
///         <c>assets.create.terrain-layer</c> and somebody's key binding for the old one would stop
///         working without a word.
///     </para>
///     <para>
///         ⚠ <b>None of them opens.</b> There is no asset editor for a layer, a foliage type or a grass
///         type — they are edited in the inspector and in the terrain panels — so opening one would
///         put "No editor claims that file" on screen every time somebody made one.
///     </para>
///     <para>
///         ⚠ <b><c>.vxterrain</c> is deliberately not here.</b> A terrain is a size, a tile count and
///         a height range before it is a file, and an empty one of those is eight gigabytes as easily
///         as eight megabytes — which is what the Terrain panel's create form and its derived-cost
///         readout exist for.
///     </para>
/// </remarks>
static class TerrainAssetKinds {
    /// <summary>A paint layer with a tiling somebody can see, and no texture yet.</summary>
    /// <returns>The new file's text.</returns>
    /// <remarks>
    ///     Four metres, because a ground texture tiled at one metre reads as noise from standing
    ///     height and one tiled at sixteen reads as a blur — and the number an author changes first is
    ///     the one they can already see the effect of.
    /// </remarks>
    [CreateAssetMenu("Terrain Layer", ".vxlayer", DefaultName = "New Terrain Layer", Opens = false, Id = "assets.create-terrain-layer")]
    public static string NewLayer() => """
        name: New Layer
        tilingMetres: 4
        """;

    /// <summary>A stored foliage type with a spacing, and no mesh yet.</summary>
    /// <returns>The new file's text.</returns>
    [CreateAssetMenu("Foliage Type", ".vxfoliage", DefaultName = "New Foliage Type", Opens = false, Id = "assets.create-foliage")]
    public static string NewFoliage() => """
        name: New Foliage
        radius: 2
        storage: Stored
        """;

    /// <summary>A derived grass type over a layer that has to be renamed to one that exists.</summary>
    /// <returns>The new file's text.</returns>
    /// <remarks>
    ///     ⚠ <b>The layer name is the one field with no defensible default</b>, so it names the
    ///     conventional one and the importer's own <c>Validate()</c> is what says when it does not
    ///     match the terrain — which is a message beside the file rather than an empty hillside.
    /// </remarks>
    [CreateAssetMenu("Grass Type", ".vxgrass", DefaultName = "New Grass Type", Opens = false, Id = "assets.create-grass")]
    public static string NewGrass() => """
        name: New Grass
        layer: Grass
        density: 8
        jitter: 0.8
        """;
}
