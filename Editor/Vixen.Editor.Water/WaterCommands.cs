// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Rendering.Water;
using Vixen.Water;

namespace Vixen.Editor.Water;

// ⚠ There is no CreateWaterBodyCommand here, and its absence is the decision rather than an omission.
// `SceneDocument.Create(name, local, parent, initialise)` already makes an entity undoably, with the
// transform, the parenting and the outliner's name all handled — a command of water's own would be a
// second answer to "how does a tool add an entity", and the two would drift the first time the
// document's version learnt something this one had not. What is below is only what is genuinely new:
// changing a body's profile, and regenerating the terrain's reserved layer.

/// <summary>Changing a body's profile, as one undo entry.</summary>
/// <remarks>
///     <para>
///         What a handle drag commits to. One entry for the whole drag rather than one per pointer
///         move, which is <c>TerrainStrokeCommand</c>'s arrangement and its reason: a drag is one
///         thing an author did, and forty entries is an undo stack nobody can walk back through.
///     </para>
///     <para>
///         ⚠ <b>It holds the value before and the value after rather than a delta.</b> A delta
///         applied twice is a profile that drifts, and an undo stack is replayed in both directions —
///         which is exactly when a drift becomes visible and impossible to attribute.
///     </para>
/// </remarks>
/// <param name="world">The world the body lives in.</param>
/// <param name="entity">Which body.</param>
/// <param name="before">What its profile was.</param>
/// <param name="after">What it becomes.</param>
public sealed class WaterProfileCommand(
    World world,
    Entity entity,
    WaterBodyComponent before,
    WaterBodyComponent after
) : IEditorCommand {
    /// <inheritdoc />
    public string Name => "Edit Water Profile";

    /// <inheritdoc />
    public void Do(EditorContext context) => Apply(after);

    /// <inheritdoc />
    public void Undo(EditorContext context) => Apply(before);

    void Apply(in WaterBodyComponent value) {
        if (world.IsAlive(entity)) {
            world.Set(entity, value);
        }
    }
}

/// <summary>Regenerating the terrain's reserved water layer, as one undo entry.</summary>
/// <remarks>
///     <para>
///         <b>The whole layer, because that is what non-destructive means</b> —
///         [§ D5](../../docs/plan/35-water.md#d5-carving-is-a-reserved-edit-layer-and-the-machinery-exists).
///         Moving a body restores the ground it left and cuts the ground it arrived at in one
///         operation, because the layer is deltas over ground it never touched.
///     </para>
///     <para>
///         ⚠ <b>The undo is a regeneration from the <em>old</em> body list, not a stored copy of the
///         layer.</b> A layer's deltas are sparse chunks over a whole terrain, and copying them per
///         entry would put a heightfield's worth of memory on the undo stack for every drag of a
///         width handle. Regenerating is deterministic — that is what "regenerated wholesale" is
///         for — so the cheap thing and the correct thing are the same thing here.
///     </para>
/// </remarks>
/// <param name="terrain">The terrain being carved.</param>
/// <param name="before">The bodies that carved it before.</param>
/// <param name="after">The ones that carve it now.</param>
public sealed class WaterCarveCommand(
    Vixen.Terrain.Terrain terrain,
    IReadOnlyList<(WaterBody Body, WaterCarveProfile Profile)> before,
    IReadOnlyList<(WaterBody Body, WaterCarveProfile Profile)> after
) : IEditorCommand {
    /// <inheritdoc />
    public string Name => "Carve Water";

    /// <inheritdoc />
    public void Do(EditorContext context) => Regenerate(after);

    /// <inheritdoc />
    public void Undo(EditorContext context) => Regenerate(before);

    void Regenerate(IReadOnlyList<(WaterBody Body, WaterCarveProfile Profile)> bodies) =>
        WaterCarve.Regenerate(terrain, WaterCarve.LayerOf(terrain), bodies);
}
