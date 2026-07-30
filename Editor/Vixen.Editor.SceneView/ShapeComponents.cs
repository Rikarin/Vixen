// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>The asset an entity is an instance of.</summary>
/// <remarks>
///     <para>
///         <b>What a drag from the content browser into the scene produces.</b> An entity needs
///         somewhere to hold "this is the crate", and a drop that made an entity merely <i>named</i>
///         after a file would have been the editor pretending it had done something.
///     </para>
///     <para>
///         ⚠ <b>Deliberately not the same thing as <see cref="MeshRenderable" />.</b> That says "draw
///         this mesh" and is the runtime's; this says "this entity stands for this asset", which is
///         still the honest answer for a drop of a texture, a clip or a prefab — anything whose meaning
///         on an entity nobody has decided yet. A drop that knows it is a mesh should produce a
///         <see cref="MeshRenderable" />; this is what is left over.
///     </para>
///     <para>
///         ⚠ <b>Editor-side, and therefore <c>[DataContract]</c> without <c>[Component]</c>.</b> The
///         contract makes it describable and so inspectable; the pair would declare it to
///         <c>SceneComponentRegistry</c>, and a compiled scene naming a type only the editor declares is
///         what a content compile refuses. If this earns a runtime meaning it earns the second
///         attribute at the same time, which is the whole of what <see cref="Light" /> and
///         <see cref="PrimitiveShape" /> needed to become runtime components.
///     </para>
/// </remarks>
[DataContract]
public struct AssetInstance {
    /// <summary>Which asset.</summary>
    /// <remarks>
    ///     No <c>[AssetPicker]</c>, and not for want of trying: that attribute is
    ///     <c>Vixen.Editor.Inspector</c>'s and this assembly deliberately does not reference it — a
    ///     scene view that knew what a property drawer was would be the coupling doc 11's layering
    ///     exists to prevent. The drawer is registered for <c>AssetId</c> by type as well as by
    ///     attribute, so the field gets a picker anyway.
    /// </remarks>
    public AssetId Asset;
}

/// <summary>Reading and writing an entity's asset.</summary>
public static class AssetInstances {
    /// <summary>Puts an asset reference on an entity, replacing whatever was there.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="asset">Which asset.</param>
    public static void Attach(World world, Entity entity, AssetId asset) {
        ArgumentNullException.ThrowIfNull(world);

        var instance = new AssetInstance { Asset = asset };

        if (world.Has<AssetInstance>(entity)) {
            world.Set(entity, in instance);
        } else {
            world.Add(entity, in instance);
        }
    }

    /// <summary>What asset an entity stands for, if any.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="asset">The asset.</param>
    /// <returns>Whether it has one.</returns>
    public static bool TryGet(World world, Entity entity, out AssetId asset) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.IsAlive(entity) && world.Has<AssetInstance>(entity)) {
            asset = world.Read<AssetInstance>(entity).Asset;
            return !asset.IsEmpty;
        }

        asset = AssetId.Empty;
        return false;
    }
}
