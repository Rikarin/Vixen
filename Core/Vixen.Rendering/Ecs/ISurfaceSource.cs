// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering.Materials;

namespace Vixen.Rendering.Ecs;

/// <summary>Where the look of the material a renderable names comes from, for a preview.</summary>
/// <remarks>
///     <para>
///         <b><see cref="IMaterialSource" />'s cheaper sibling, and the two are not the same question.</b>
///         That one hands back a <see cref="Material" /> — a compiled variant, a constant buffer and a
///         descriptor set — which is what a frame graph draws with and what a caller with no compositor
///         cannot use for anything. This one hands back a <see cref="MaterialSurface" />: the same
///         material read for its colour, its metalness, its roughness and what it emits.
///     </para>
///     <para>
///         <b>It exists because the editor viewport is not a frame graph.</b> The viewport draws a scene
///         through one instanced pipeline with a key light and no descriptor sets at all — see
///         <see cref="MeshInstanceRenderer" />, whose own remarks call materials the boundary it stops
///         at. Reaching the material system from there would mean giving the viewport a compositor;
///         reading four numbers off the material does not, and is most of what looking at a level needs.
///     </para>
///     <para>
///         ⚠ <b>False is "no material", not "not yet".</b> Unlike <see cref="IMeshSource" />, a miss here
///         is not something to wait for and re-ask about: an entity that names no material, or names one
///         that cannot be found, is drawn with <see cref="MaterialSurface.Default" /> and looks like a
///         plain surface. A mesh that has not loaded draws nothing because the alternative is an entity
///         whose shape depends on disk speed; a material that has not loaded draws grey, because the
///         alternative is a level that disappears while its materials are read.
///     </para>
/// </remarks>
public interface ISurfaceSource {
    /// <summary>The surface a material reference names.</summary>
    /// <param name="reference">Which material.</param>
    /// <param name="surface">Its surface, when this returns true.</param>
    /// <returns>Whether this source has one.</returns>
    bool TryGet(AssetReference reference, out MaterialSurface surface);
}
