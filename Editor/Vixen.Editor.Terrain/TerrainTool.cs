// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Terrain;

/// <summary>What a drag in the viewport does to the ground.</summary>
/// <remarks>
///     <para>
///         <b>Seven sculpt tools plus holes — [docs/plan/31 § The sculpt tools].</b> The list is the
///         intersection of Unreal's Landscape and Unity's Terrain, plus Ramp, which only Unreal has
///         and which is what a level designer reaches for between two playtests.
///     </para>
///     <para>
///         ⚠ <b>Holes is last and is not the eighth sculpt tool.</b> It paints a visibility bit
///         rather than a height, so it writes <see cref="Vixen.Terrain.TerrainHoles" /> rather than an
///         edit layer, and it is the one tool in the strip whose stroke a
///         <see cref="TerrainStrokeCommand" /> cannot record — see that type's remarks. Grouping it
///         with the seven is the references' layout and the honest ordering for an artist; treating it
///         as one of them in code is the mistake the two storage models make visible.
///     </para>
/// </remarks>
public enum TerrainTool {
    /// <summary>Raises the ground; <c>Shift</c> lowers it.</summary>
    Sculpt,

    /// <summary>Averages the ground under the brush with its neighbours.</summary>
    Smooth,

    /// <summary>Pulls the ground towards a height picked at the start of the stroke.</summary>
    Flatten,

    /// <summary>A straight ramp between two picked points.</summary>
    Ramp,

    /// <summary>Thermal erosion: material steeper than the talus angle slides downhill.</summary>
    Erosion,

    /// <summary>Hydraulic erosion: channels deepen and shoulders fill.</summary>
    Hydro,

    /// <summary>Adds fractal noise.</summary>
    Noise,

    /// <summary>Paints the visibility mask, so a quad can be removed for a cave mouth.</summary>
    Holes
}
