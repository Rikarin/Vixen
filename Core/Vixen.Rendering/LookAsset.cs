// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering.Ecs;

namespace Vixen.Rendering;

/// <summary>A project's <c>.vxlook</c>: the artistic base every scene shares.</summary>
/// <remarks>
///     <para>
///         Doc 39's look profile — the one named artifact where a game's exposure target, meter
///         clamps, grade, fog and lens character live together instead of scattered through an
///         eleven-hundred-line frame document. The Standard Frame emits neutral values on purpose;
///         this is where the art goes, and it reaches the frame at run time through the volume fold,
///         never through the expansion — same document, relightable.
///     </para>
///     <para>
///         <b>The payload is <see cref="PostProcessSettings" /> itself, and that is the design
///         rather than a shortcut.</b> The volume stack already has a vocabulary for "a layer of
///         per-parameter opinions", an inspector that draws it, a fold that blends it and nodes that
///         consume it — so the look is exactly a settings layer, folded under everything a scene
///         says, and needs no translation on its way in. A second field set here would drift from
///         the volumes' one field at a time, and every drifted field would be a look that a volume
///         could not override.
///     </para>
///     <para>
///         The wrapper exists at all — rather than serialising the bare struct — so the asset has a
///         document name (<c>!Look</c>), room for the sibling blocks later phases add (a default
///         LUT's address is the known candidate, being a reference rather than a value), and a place
///         for these remarks to live.
///     </para>
///     <para>
///         <b>Precedence is fixed and four layers deep</b>: engine defaults (the authored node
///         values), then this, then a scene's unbound volume, then local volumes and gameplay
///         overlays. The fold applies this first at full weight, so anything any volume says wins
///         over it per parameter — see <see cref="Ecs.PostProcessVolumeSystem.Look" /> for the seam
///         a host wires it into.
///     </para>
/// </remarks>
[DataContract("Look")]
public sealed record LookAsset {
    /// <summary>What the look has an opinion about. Unset fields stay the document's.</summary>
    public PostProcessSettings Settings { get; init; } = PostProcessSettings.None;
}
