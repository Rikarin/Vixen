// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Rendering.Vfx;
using Vixen.Vfx;

namespace Vixen.Rendering.Ecs;

/// <summary>An entity that emits particles.</summary>
/// <remarks>
///     <para>
///         <b>One reference and four numbers.</b> Which effect, whether it is running, what its
///         randomness is derived from, and how far its particles reach. Where it emits from is the
///         transform's, exactly as a mesh's placement is — <see cref="VfxExtractionSystem" /> reads
///         <c>WorldTransform</c> and writes it into <c>VfxSystem.Origin</c>, so the same
///         <c>.vxvfx</c> on twenty entities is one asset and twenty emitters.
///     </para>
///     <para>
///         <b>The whole point of the <c>[AssetType]</c> line.</b> It is what makes the row in the
///         inspector a picker for <em>effects</em> rather than for anything in the project, and it is
///         <c>Vixen.Core</c>'s annotation rather than the editor's for the reason
///         <see cref="MeshRenderable.Mesh" /> gives: a runtime assembly referencing an editor one is
///         the coupling doc 11's layering exists to prevent. Between that and
///         <c>[Component] [DataContract]</c>, the Add Component menu, the type-filtered picker, the
///         drag-and-drop target and the <c>.vxscene</c> line all follow with no editor code at all.
///     </para>
///     <para>
///         ⚠ <b>A zeroed emitter is stopped, and that is why <see cref="VfxEmitters" /> exists.</b>
///         <c>default</c> gives <see cref="Playing" /> false and a <see cref="Reach" /> of zero — an
///         effect that never runs and whose bound culls it from every view even if it did. A chunk row
///         is zeroed memory and no constructor runs on it, so the usable value cannot be a field
///         initialiser; it is declared through <see cref="IDefaultComponent{TSelf}" /> instead, which
///         is what an Add Component reaches. <c>MeshRenderables.Default</c> and <c>Lights.Default</c>
///         are the same asymmetry answered the same way.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct VfxEmitter : IDefaultComponent<VfxEmitter> {
    /// <summary>Which effect, as the reference a scene stores.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="AssetReference.Null" /> is a real state rather than a mistake</b>, and it is
    ///     what an emitter dropped onto an entity before anybody has chosen an effect holds. It emits
    ///     nothing and is not an error, on <see cref="MeshRenderable.Material" />'s terms.
    /// </remarks>
    [AssetType(typeof(VfxEffectContent))]
    public AssetReference Effect;

    /// <summary>What each particle is an instance of, for an effect authored as a mesh renderer.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The emitter's rather than the effect's, and that is the same split
    ///         <see cref="VfxExtractionSystem.Material" /> makes.</b> A <c>.vxvfx</c> says how particles
    ///         <em>move</em> and that they are drawn as meshes; it says nothing about <em>which</em>
    ///         mesh, because the node library has no asset picker in it and an effect that named one
    ///         could not be reused for two kinds of debris. So the choice sits on the entity, beside
    ///         the effect it goes with.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read only when the effect's renderer is <see cref="VfxRendererKind.Mesh" />.</b> A
    ///         billboard, ribbon or light effect ignores it entirely, and a mesh effect that names none
    ///         simulates and draws nothing — which is counted by
    ///         <see cref="VfxExtractionSystem.Meshless" /> rather than thrown for, because an emitter
    ///         dropped onto an entity before anybody has chosen a mesh is a real state on
    ///         <see cref="Effect" />'s terms.
    ///     </para>
    /// </remarks>
    [AssetType(typeof(MeshData))]
    public AssetReference Mesh;

    /// <summary>Whether the spawners are running.</summary>
    /// <remarks>
    ///     Stopping an effect and killing it are different things: the particles already alive keep
    ///     going, which is what lets a fire that is put out finish its embers. Removing the component
    ///     is the other one, and takes the whole system with it.
    /// </remarks>
    public bool Playing;

    /// <summary>What the effect's randomness is derived from.</summary>
    /// <remarks>
    ///     <para>
    ///         Two emitters of one effect with the same seed produce the same particles, which is
    ///         almost never wanted: fifteen lamps drifting identical embers reads as a repeated
    ///         texture rather than as fifteen fires. <see cref="VfxExtractionSystem" /> derives one
    ///         from the entity when this is zero, so the ordinary case needs nothing said.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Set it when the frames have to be reproducible.</b> The derived seed follows the
    ///         entity's identity, which is stable for a level loaded from a scene and is not stable
    ///         for one built by spawning — so a test that renders N frames twice wants this written
    ///         down.
    ///     </para>
    /// </remarks>
    public uint Seed;

    /// <summary>How far the particles reach from the emitter, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>The bound the frustum culls against, and nothing recomputes it.</b> A particle outside
    ///     it does not vanish on its own — the whole effect does, because culling is per render object
    ///     — so this has to cover where the drift can get to rather than where the particles are now.
    ///     Too large costs one frustum test that comes back true; too small is an effect that
    ///     disappears when the emitter leaves the screen and its particles have not.
    /// </remarks>
    public float Reach;

    /// <summary>How far above the emitter the bound is centred, in metres.</summary>
    /// <remarks>
    ///     Zero is a sphere centred on the emitter, which is right for a puff and wastes half its
    ///     volume under the floor for anything that rises. Rising is the common case — embers, smoke,
    ///     steam — so it is worth a number rather than a larger <see cref="Reach" />.
    /// </remarks>
    public float Rise;

    /// <summary>A playing emitter with a bound that is not zero, and no effect chosen yet.</summary>
    /// <remarks>
    ///     ⚠ <b>Explicit, so this does not become a second public name for
    ///     <see cref="VfxEmitters.Default(AssetReference)" />.</b> That one takes the effect, which an
    ///     Add Component has no answer to — the picker in the row is the answer, and
    ///     <see cref="Effect" /> being null is a real state rather than a mistake. What is worth
    ///     carrying across is the pair of fields whose zeroes stop the emitter dead.
    /// </remarks>
    static VfxEmitter IDefaultComponent<VfxEmitter>.DefaultValue => VfxEmitters.Default(AssetReference.Null);
}

/// <summary>What <see cref="VfxExtractionSystem" /> made for an emitter, so it can take it away again.</summary>
/// <remarks>
///     <para>
///         <see cref="RenderHandle" />'s counterpart, and deliberately a separate component rather
///         than more fields on that one: an entity is a mesh or an emitter, and a handle that could be
///         either would need a discriminator that the archetype already is.
///     </para>
///     <para>
///         ⚠ <b>The <c>VfxSystem</c> itself is not here.</b> It is a reference type holding
///         pooled native memory, and a component is a value in a chunk — so the system lives in a map
///         the extraction owns and this carries the key. That is the same split
///         <c>AudioClipRef</c> makes, arrived at from the other direction.
///     </para>
/// </remarks>
public struct VfxHandle {
    /// <summary>Which render object it is.</summary>
    public RenderObjectId Object;
}

/// <summary>Reading and writing an entity's effect.</summary>
public static class VfxEmitters {
    /// <summary>How far a particle is assumed to reach when a document says nothing.</summary>
    /// <remarks>
    ///     Four metres, which covers a spark that rises for a few seconds and is small enough that a
    ///     level of emitters does not become a level of always-visible bounds. An effect that throws
    ///     its particles further says so; one that does not gets a number that is wrong in the
    ///     forgiving direction.
    /// </remarks>
    public const float DefaultReach = 4f;

    /// <summary>An emitter for an effect, with the values a new one should have.</summary>
    /// <param name="effect">Which effect.</param>
    /// <returns>The emitter.</returns>
    /// <remarks>
    ///     Playing, and with a bound that is not zero — neither of which a zeroed struct can be. An
    ///     emitter dropped into a level and emitting nothing looks broken in a way that takes a while
    ///     to attribute to two unticked fields.
    /// </remarks>
    public static VfxEmitter Default(AssetReference effect) =>
        new() { Effect = effect, Playing = true, Reach = DefaultReach, Rise = DefaultReach * 0.4f };

    /// <summary>Puts an effect on an entity, replacing whatever was there.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="emitter">The emitter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public static void Attach(World world, Entity entity, VfxEmitter emitter) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Has<VfxEmitter>(entity)) {
            world.Set(entity, emitter);

            return;
        }

        world.Add(entity, emitter);
    }
}
