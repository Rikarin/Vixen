// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Terrain;

/// <summary>What a scene says about its painted foliage: which instances, of which types.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31]'s owed fifth seam — "a <c>FoliageVolumeComponent</c> naming a volume
///         asset".</b> The instances live beside the scene as a <c>.vxfol</c>, because fifty
///         thousand transforms in a <c>.vxscene</c> is a file nobody can merge; the palette is
///         names and numbers a review has to be able to read, and this component is the text that
///         declares it — each entry a <c>.vxfoliage</c> reference, in the order the volume's chunks
///         index.
///     </para>
///     <para>
///         ⚠ <b>The palette's <em>order</em> is load-bearing.</b> A stored chunk names its type by
///         index, so reordering these references re-dresses every painted stand — somebody's oaks
///         become pines with no error anywhere. Append; do not sort.
///     </para>
///     <para>
///         ⚠ <b>Placement is the entity's translation, and world-painted volumes sit at the
///         origin.</b> The editor paints instances in world space, so a volume authored there rides
///         an entity with no transform; the translation exists for a prefab-shaped volume placed
///         more than once. Rotation and scale are not consumed, on
///         <see cref="TerrainComponent" />'s no-rotation terms.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct FoliageVolumeComponent {
    /// <summary>Which <c>.vxfol</c> holds the instances.</summary>
    /// <remarks>A name rather than a handle, on <see cref="TerrainComponent.Terrain" />'s terms.</remarks>
    public string Volume;

    /// <summary>The <c>.vxfoliage</c> types the instances are of, in palette order.</summary>
    public string[] Palette;

    /// <summary>How far from the camera cells stay uploaded, in metres. Zero takes the default.</summary>
    /// <remarks>
    ///     Residency, not the cull distance — <see cref="TerrainGrassComponent.Range" />'s own
    ///     distinction: the types' <c>EndCullDistance</c> says where instances stop being drawn,
    ///     and this says where their cells stop being <em>uploaded</em>, which is bandwidth.
    /// </remarks>
    public float Range;

    /// <summary>A volume drawn with the usual settings.</summary>
    /// <param name="volume">Which instances file.</param>
    /// <param name="palette">Which types, in the order the file's chunks index.</param>
    /// <returns>The component.</returns>
    public static FoliageVolumeComponent Of(string volume, params string[] palette) =>
        new() { Volume = volume, Palette = palette, Range = 0f };
}
