// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
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
///         <b>Drawn through <see cref="MeshExtractionSystem.Meshes" />.</b> The reference resolves the
///         way every other one does — <c>ContentCatalog</c> turns it into an address and
///         <c>AssetManager</c> turns that into a <see cref="MeshData" /> — and the extraction system
///         <em>asks</em> for it rather than waiting, so a mesh that has not arrived leaves the entity
///         without a <see cref="RenderHandle" /> and is asked about again next frame. A host that sets
///         no source draws no referenced mesh at all, which is what a project with no content mounted
///         is.
///     </para>
///     <para>
///         <b>The material resolves the same way</b>, through
///         <see cref="MeshExtractionSystem.Materials" />: the compiled <c>MaterialContent</c> the build
///         wrote becomes a <see cref="Material" />, its textures reach the frame's table, and the
///         entity is drawn with it. An entity that names none is drawn with
///         <see cref="MeshExtractionSystem.Material" />, which is what <see cref="Material" />'s own
///         remarks say null means.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct MeshRenderable : IDefaultComponent<MeshRenderable> {
    /// <summary>Which mesh, as the reference a scene stores.</summary>
    /// <remarks>
    ///     ⚠ <b><c>[AssetType]</c> is what makes the row in the inspector a picker for <i>meshes</i>
    ///     rather than for anything in the project.</b> It is <c>Vixen.Core</c>'s annotation and not
    ///     the editor's, for the reason this whole component is drawn from its <c>[DataContract]</c>
    ///     description: a runtime assembly referencing an editor one is the coupling doc 11's
    ///     layering exists to prevent.
    /// </remarks>
    [AssetType(typeof(MeshData), AllowNull = false)]
    public AssetReference Mesh;

    /// <summary>Which material, or <see cref="AssetReference.Null" /> for the renderer's default.</summary>
    /// <remarks>
    ///     Null is a usable value rather than a mistake: a block-out mesh dropped into a level before
    ///     anybody has made a material for it should draw in something neutral, not fail to load.
    ///     Which is why this one, unlike <see cref="Mesh" />, keeps the picker's "None".
    /// </remarks>
    [AssetType(typeof(Material))]
    public AssetReference Material;

    /// <summary>Whether it is drawn into the shadow stages as well as the shading ones.</summary>
    /// <remarks>
    ///     <para>
    ///         Zero is <see langword="false" /> and a chunk's column is zeroed memory, which nothing
    ///         can change — so <see cref="MeshRenderables.Default" /> is what an editor and a script
    ///         build one with, exactly as <c>Lights.Default</c> exists for the same reason.
    ///     </para>
    ///     <para>
    ///         <b>Read by <see cref="MeshExtractionSystem" />, and only where
    ///         <see cref="MeshExtractionSystem.CasterStages" /> says which stages are the shadow
    ///         ones.</b> A render object carries a stage mask and nothing else a shadow pass could
    ///         consult, so "casts no shadow" is spelled as "not in those stages" — which means a host
    ///         that has not named them cannot honour the flag, and ignores it rather than dropping
    ///         every shadow in a scene whose zeroed columns all read <see langword="false" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read once, when the entity is first extracted.</b> A settled entity is never
    ///         re-extracted, so ticking this on a live scene reaches nothing already drawn until
    ///         <see cref="MeshExtractionSystem.Resettle" /> unsettles it — the same bargain
    ///         <see cref="StaticShadowCaster" /> makes, for the same reason, and it is why the flag is
    ///         a property of the level rather than a switch to animate.
    ///     </para>
    /// </remarks>
    public bool CastsShadows;

    /// <summary>A renderable with no mesh yet, casting shadows once it has one.</summary>
    /// <remarks>
    ///     ⚠ <b>Explicit, so this does not become a second public name for
    ///     <see cref="MeshRenderables.Default" />.</b> That one takes the mesh, which is the question an
    ///     Add Component has no answer to — the picker is the answer, and it is the next thing the
    ///     author touches. What is worth carrying across from it is the shadow flag, whose zero reads
    ///     as a lighting bug and takes a while to attribute to a checkbox.
    /// </remarks>
    static MeshRenderable IDefaultComponent<MeshRenderable>.DefaultValue => MeshRenderables.Default(AssetReference.Null);
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
    [AssetType(typeof(Material))]
    public AssetReference Material;
}

/// <summary>
///     Claims that an entity's geometry never moves, so its shadow can be cached across frames.
/// </summary>
/// <remarks>
///     <para>
///         <b>The scene's half of <c>ShadowMapRenderer.StaticCasterStage</c>.</b> That node draws one
///         stage into a cache only when something invalidates it and copies the cache into the working
///         atlas every frame, which needs the casters split into "level" and "everything that moves" —
///         and a stage is exactly the shape of that split, because "which objects, in what order" is
///         what a stage means. This is how an entity says which side it is on:
///         <see cref="MeshExtractionSystem.StaticStages" /> is the mask an entity carrying this is
///         stamped with instead of <see cref="MeshExtractionSystem.Stages" />.
///     </para>
///     <para>
///         ⚠ <b>A claim, not a detection, and the renderer cannot check it.</b> An entity that carries
///         this and then moves keeps the shadow it was first drawn with — the shadow stays where the
///         object used to be, in a frame where the object is visibly elsewhere. That is the bargain
///         the word "static" already implied; the escape hatch for a level that genuinely changes is
///         <c>ShadowMapRenderer.StaticVersion</c>, which redraws the cache once.
///     </para>
///     <para>
///         ⚠ <b>Read when the entity is extracted, and extraction happens once.</b> A settled entity
///         is never re-extracted, so adding or removing this afterwards does not restamp the mask it
///         already has. An entity whose staticness is decided at all should be decided before it first
///         draws.
///     </para>
///     <para>
///         <b><c>[Component]</c> and <c>[DataContract]</c>, deliberately both</b>, on
///         <c>PlayerPawn</c>'s terms: it carries no handle and no derived state, so it is an authored
///         fact a scene and a prefab must both be able to say.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct StaticShadowCaster : ITagComponent;

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

/// <summary>The render object an entity has, while it has one.</summary>
/// <remarks>
///     <para>
///         <b>Written by the bridge, never by game code</b> — the shape <c>PhysicsBody</c> established.
///         Present exactly while a render object exists, so "is this drawable extracted" is an archetype
///         question and the system finds what still needs one with <c>WithNone&lt;RenderHandle&gt;</c>
///         rather than by scanning for a sentinel.
///     </para>
///     <para>
///         ⚠ <b>Neither <c>[Component]</c> nor <c>[DataContract]</c>, and both omissions are
///         load-bearing.</b> A <see cref="RenderObjectId" /> is a dense index into arrays this process
///         allocated, so it means nothing in another one — saving it into a scene would restore a handle
///         to whatever object took the slot. That is the same reason <c>Entity</c> is never written to a
///         scene, and the absence of the attributes is the whole of what keeps this out of
///         <c>SceneComponentRegistry</c>.
///     </para>
/// </remarks>
public struct RenderHandle {
    /// <summary>The object in the store.</summary>
    public RenderObjectId Object;

    /// <summary>Which geometry it holds a residency claim on, so releasing it releases the right one.</summary>
    /// <remarks>
    ///     ⚠ <b>Recorded rather than re-derived on removal.</b> An entity whose mesh reference was
    ///     changed and then destroyed would otherwise release a claim on the mesh it points at *now* and
    ///     leak the one it was actually holding — a leak that only shows up in a level that reassigns
    ///     meshes at run time.
    /// </remarks>
    public GeometryKey Geometry;

    /// <summary>Its bounds in the mesh's own space, so a moved entity needs no mesh lookup.</summary>
    public BoundingSphere Local;
}
