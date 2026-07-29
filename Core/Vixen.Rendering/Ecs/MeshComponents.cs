// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Rendering.Ecs;

/// <summary>An entity that draws a mesh asset.</summary>
/// <remarks>
///     <para>
///         <b>Two references and nothing else.</b> Which geometry and which material, both as the
///         <c>vx:</c> identity an asset keeps when its file is renamed. Where it is and how big comes
///         from the transform; what it looks like is the material's; how it is cut into meshlets and
///         packed into buffers is the model compiler's. This says only which asset the entity is an
///         instance of.
///     </para>
///     <para>
///         <b>An <see cref="AssetReference" /> rather than a bare <see cref="AssetId" />, because a
///         model is not one mesh.</b> An FBX imports to a main object and a sub-asset per mesh, and
///         what an entity draws is usually one of the parts — <c>characters/hero#Hero_Mesh</c>, whose
///         reference is the asset's id and that part's sub-asset id. A bare id could only ever name the
///         main object.
///     </para>
///     <para>
///         ⚠ <b>This is a reference, and drawing it is still owed.</b> Resolving it is done:
///         <c>ContentCatalog.TryGetAddress</c> turns the reference into an address and
///         <c>AssetManager.LoadAsync</c> turns that into a <see cref="MeshData" />. What does not exist
///         yet is the extraction system that would put a <see cref="RenderObject" /> in the store for
///         it, which needs a residency cache over <see cref="GeometryBuffer" /> — one slice per mesh,
///         shared by every entity drawing it — and a material asset resolved to a
///         <see cref="Material" />. Until then an entity carrying this is authored, saved, compiled,
///         loaded and invisible. <see cref="LightExtractionSystem" /> is the same shape of thing for
///         lights and is finished, so the pattern the mesh one follows is in the tree.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct MeshRenderable {
    /// <summary>Which mesh, as the reference a scene stores.</summary>
    public AssetReference Mesh;

    /// <summary>Which material, or <see cref="AssetReference.Null" /> for the renderer's default.</summary>
    /// <remarks>
    ///     Null is a usable value rather than a mistake: a block-out mesh dropped into a level before
    ///     anybody has made a material for it should draw in something neutral, not fail to load.
    /// </remarks>
    public AssetReference Material;

    /// <summary>Whether it is drawn into the shadow stages as well as the shading ones.</summary>
    /// <remarks>
    ///     Defaults to <see langword="false" /> because a component's default is a zeroed struct and
    ///     nothing can change that — so <see cref="MeshRenderables.Default" /> is what an editor and a
    ///     script should build one with, exactly as <c>Lights.Default</c> exists for the same reason.
    /// </remarks>
    public bool CastsShadows;
}

/// <summary>An entity drawn as one of the built-in shapes.</summary>
/// <remarks>
///     <para>
///         <b>The kind and nothing else.</b> The geometry is not stored per entity — a thousand cubes
///         are a thousand copies of the same twenty-four vertices, and <see cref="MeshPrimitives" />
///         builds one on demand from a value that is four bytes. Size, position and orientation are the
///         transform's, which is why every primitive comes out of the builder fitting the unit cube:
///         <c>scale: 2 1 2</c> then means what it looks like it means.
///     </para>
///     <para>
///         <b>Separate from <see cref="MeshRenderable" /> rather than a null mesh on one.</b> A
///         primitive needs no asset and no residency cache — the extraction that draws one can be
///         written before the extraction that draws an asset, and an entity is one or the other. Making
///         it a special case of a mesh reference would put a branch on "is this reference null" in the
///         middle of the path that resolves references.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PrimitiveShape {
    /// <summary>Which shape.</summary>
    public PrimitiveKind Kind;

    /// <summary>Which material, or <see cref="AssetReference.Null" /> for the renderer's default.</summary>
    public AssetReference Material;
}

/// <summary>Reading and writing an entity's mesh.</summary>
public static class MeshRenderables {
    /// <summary>A renderable pointing at a mesh, with the values a new one should have.</summary>
    /// <param name="mesh">Which mesh.</param>
    /// <returns>The renderable.</returns>
    /// <remarks>
    ///     Shadow casting is on here and off in a zeroed struct, which is the asymmetry
    ///     <c>Lights.Default</c> also has: a mesh dropped into a level and casting no shadow looks
    ///     broken in a way that takes a while to attribute to a checkbox.
    /// </remarks>
    public static MeshRenderable Default(AssetReference mesh) => new() { Mesh = mesh, CastsShadows = true };

    /// <summary>Puts a mesh on an entity, replacing whatever was there.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="renderable">The renderable.</param>
    public static void Attach(World world, Entity entity, MeshRenderable renderable) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Has<MeshRenderable>(entity)) {
            world.Set(entity, in renderable);
            return;
        }

        world.Add(entity, in renderable);
    }

    /// <summary>What mesh an entity draws, if it draws one.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="renderable">The renderable.</param>
    /// <returns>Whether it has one.</returns>
    public static bool TryGet(World world, Entity entity, out MeshRenderable renderable) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.IsAlive(entity) && world.Has<MeshRenderable>(entity)) {
            renderable = world.Read<MeshRenderable>(entity);
            return true;
        }

        renderable = default;
        return false;
    }
}

/// <summary>Reading and writing an entity's shape, and what the shapes are called.</summary>
public static class PrimitiveShapes {
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
    ///     One name for both, on purpose: a file that says <c>Cylinder</c> and a menu item that says
    ///     "Cylinder" being the same string is what makes a hand-edited scene guessable.
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

        if (world.Has<PrimitiveShape>(entity)) {
            world.Get<PrimitiveShape>(entity).Kind = kind;
            return;
        }

        world.Add(entity, new PrimitiveShape { Kind = kind });
    }

    /// <summary>What shape an entity is drawn as, if it is drawn as one.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="kind">The shape.</param>
    /// <returns>Whether it has one.</returns>
    public static bool TryGet(World world, Entity entity, out PrimitiveKind kind) {
        ArgumentNullException.ThrowIfNull(world);

        if (world.IsAlive(entity) && world.Has<PrimitiveShape>(entity)) {
            kind = world.Read<PrimitiveShape>(entity).Kind;
            return true;
        }

        kind = default;
        return false;
    }
}
