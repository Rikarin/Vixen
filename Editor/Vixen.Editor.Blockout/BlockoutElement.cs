// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Blockout;

/// <summary>What a click in the viewport selects while the blockout mode is active.</summary>
/// <remarks>
///     <para>
///         <b>The universal binding, and it is universal for a reason worth stating.</b> Blender,
///         ProBuilder, Unreal's PolyGroup editing and every DCC since the nineties put object,
///         vertex, edge and face on the digits in that order, so a level designer arrives already
///         knowing them — which is exactly why doc 24's B2 needs a mode: doc 20 gives <c>1..9</c> to
///         view-bookmark recall, both claims are right, and only a mode can hold one of them at a time.
///     </para>
///     <para>
///         ⚠ <b><see cref="Object" /> is one of the four rather than the absence of the other
///         three.</b> Moving a whole wall is the commonest blockout edit there is, and an
///         implementation where "no sub-object mode" is a null has to answer "what is selected" twice.
///     </para>
/// </remarks>
public enum BlockoutElement : byte {
    /// <summary>Whole entities, which is what the rest of the editor means by a selection.</summary>
    Object,

    /// <summary>Shared positions — what a vertex snap, a weld and "drag this corner" run on.</summary>
    Vertex,

    /// <summary>Edges, which is what a loop, a ring and a bevel are selections of.</summary>
    Edge,

    /// <summary>Faces, which is what an extrude, an inset and a per-face material act on.</summary>
    Face
}
