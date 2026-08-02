// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Terrain;

/// <summary>Which half of the terrain toolset a drag belongs to.</summary>
/// <remarks>
///     <para>
///         <b>Sculpt and Paint, inside one mode — [docs/plan/31 § Two modes, not three, and not
///         one].</b> Both require a terrain and both act on its texels, so they are one mode; what
///         differs is whether a stamp writes a height or a weight. Unreal spells the same split as
///         tabs within Landscape mode and it is right.
///     </para>
///     <para>
///         ⚠ <b>Foliage is not here and will not be.</b> It paints onto <em>any</em> surface — a
///         terrain, a blockout mesh, an imported cliff — so its filter set is the feature rather than
///         an accident, and folding it in would mean answering "what is the target surface" twice
///         with different answers.
///     </para>
/// </remarks>
public enum TerrainCategory {
    /// <summary>The heights: the seven sculpt tools plus holes.</summary>
    Sculpt,

    /// <summary>The layer weights: the four paint tools, over the selected target layer.</summary>
    Paint
}

/// <summary>What a drag does to the layer weights under it.</summary>
/// <remarks>
///     Four, over the selected target layer — [docs/plan/31 § The paint tools]. They are the sculpt
///     tools' shapes applied to a different target, which is [§ D12]'s argument for one brush: a soft
///     edge sculpted at strength 0.3 and a soft edge painted at strength 0.3 are the same shape.
/// </remarks>
public enum TerrainPaintTool {
    /// <summary>Raises the target layer's weight; <c>Shift</c> lowers it while the others rise.</summary>
    Paint,

    /// <summary>Averages the target layer's weight within the brush.</summary>
    Smooth,

    /// <summary>Sets it to the coverage asked for, so repeated strokes converge.</summary>
    Flatten,

    /// <summary>Scatters it, so a boundary between two grounds is not a clean arc.</summary>
    Noise
}
