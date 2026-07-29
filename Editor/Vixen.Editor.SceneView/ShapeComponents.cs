// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>An entity that is drawn as one of the built-in shapes.</summary>
/// <remarks>
///     <para>
///         <b>The kind and nothing else.</b> The geometry is not stored per entity — a thousand cubes
///         are a thousand copies of the same twenty-four vertices, and <see cref="MeshPrimitives" />
///         builds one on demand from a value that is four bytes. Size, position and orientation are
///         the transform's, which is why every primitive comes out of the builder fitting the unit
///         cube: <c>scale: 2 1 2</c> then means what it looks like it means.
///     </para>
///     <para>
///         ⚠ <b>It lives in the editor, and that is a statement about what exists rather than about
///         where it belongs.</b> "Which mesh does this entity draw" is a runtime question and its
///         answer is a component in the engine — but the engine has no rendering assembly to name
///         one in today (<c>Vixen.Engine</c> does not reference <c>Vixen.Rendering</c>, deliberately),
///         and the only thing that can draw a mesh at all right now is the scene view. When the
///         runtime grows a mesh component this becomes an editor-side alias for it, and the scene
///         format below is what makes that migration a reader change rather than a data loss.
///     </para>
///     <para>
///         ⚠ <b><c>[DataContract]</c> and <i>not</i> <c>SceneComponentRegistry.Register</c></b>, for
///         the reason <see cref="Light" /> gives: the attribute is what makes it describable and so
///         inspectable, and registering it would be the separate claim that a compiled scene may
///         name it.
///     </para>
/// </remarks>
[DataContract]
public struct MeshShape {
    /// <summary>Which shape.</summary>
    public PrimitiveKind Kind;
}

/// <summary>The asset an entity is an instance of.</summary>
/// <remarks>
///     <para>
///         <b>What a drag from the content browser into the scene produces, and the reason it could
///         not before.</b> Nothing in the runtime carries an <c>AssetId</c>, so an entity had nowhere
///         to hold "this is the crate" — and a drop that made an entity <i>named</i> after a file
///         would have been the editor pretending it had done something.
///     </para>
///     <para>
///         ⚠ <b>A reference, not a renderer.</b> This says which asset the entity stands for; it does
///         not draw it. Turning a mesh asset into geometry needs a runtime mesh component and a
///         loader, which is where <see cref="MeshShape" />'s own remarks say this all ends up. What
///         it does buy today is real: the reference is authored, saved, shown in the inspector by the
///         existing asset drawer, and — because the scene file writes it in <c>vx:</c> form —
///         <c>ReferenceIndex</c> finds it, so "what breaks if I delete this" counts it.
///     </para>
///     <para>
///         ⚠ <b>Editor-side, like the two above.</b> <c>[DataContract]</c> so the inspector can draw
///         it; deliberately <i>not</i> registered with <c>SceneComponentRegistry</c>, because a scene
///         naming a type no build declares is what a content compile refuses.
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

/// <summary>Reading and writing an entity's asset, on the terms <see cref="MeshShapes" /> uses.</summary>
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

/// <summary>Reading and writing an entity's shape, and what the shapes are called.</summary>
public static class MeshShapes {
    /// <summary>Every shape, in the order a menu should offer them.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than taken from <c>Enum.GetValues</c>.</b> The enum's order is a
    ///     file format and must not change; a menu's order is what somebody reaches for most, and the
    ///     two being the same list is a coincidence that would not survive the first shape added on
    ///     the end for compatibility.
    /// </remarks>
    public static IReadOnlyList<PrimitiveKind> All { get; } = [
        PrimitiveKind.Cube,
        PrimitiveKind.Sphere,
        PrimitiveKind.Capsule,
        PrimitiveKind.Cylinder,
        PrimitiveKind.Cone,
        PrimitiveKind.Plane,
        PrimitiveKind.Quad,
        PrimitiveKind.Torus
    ];

    /// <summary>What a shape is called, in a menu and in a scene file.</summary>
    /// <param name="kind">The shape.</param>
    /// <returns>Its name.</returns>
    /// <remarks>
    ///     One name for both, on purpose: a file that says <c>shape: Cylinder</c> and a menu item
    ///     that says "Cylinder" being the same string is what makes a hand-edited scene guessable.
    /// </remarks>
    public static string NameOf(PrimitiveKind kind) => kind.ToString();

    /// <summary>Reads a shape's name back, tolerating one that is not a shape.</summary>
    /// <param name="text">The name, or null or empty for "no shape".</param>
    /// <param name="kind">The shape.</param>
    /// <returns>Whether it named one.</returns>
    /// <remarks>
    ///     ⚠ <b>An unrecognised name is <see langword="false" /> and not an exception.</b> A scene
    ///     written by a newer editor with a shape this one has never heard of should open, minus that
    ///     entity's geometry — refusing the whole file would make every shape added a flag day for
    ///     everyone who has not updated. The entity itself survives, so the loss is recoverable by
    ///     opening it in the editor that wrote it.
    /// </remarks>
    public static bool TryParse(string? text, out PrimitiveKind kind) {
        if (!string.IsNullOrWhiteSpace(text)) {
            return Enum.TryParse(text.Trim(), ignoreCase: true, out kind) && All.Contains(kind);
        }

        kind = default;
        return false;
    }

    /// <summary>Gives an entity a shape, or changes the one it has.</summary>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="kind">The shape.</param>
    public static void Attach(World world, Entity entity, PrimitiveKind kind) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Has<MeshShape>(entity)) {
            world.Get<MeshShape>(entity).Kind = kind;
            return;
        }

        world.Add(entity, new MeshShape { Kind = kind });
    }

    /// <summary>What shape an entity is drawn as, if it is drawn as one.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="kind">The shape.</param>
    /// <returns>Whether it has one.</returns>
    public static bool TryGet(World world, Entity entity, out PrimitiveKind kind) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.IsAlive(entity) && world.Has<MeshShape>(entity)) {
            kind = world.Read<MeshShape>(entity).Kind;
            return true;
        }

        kind = default;
        return false;
    }
}
