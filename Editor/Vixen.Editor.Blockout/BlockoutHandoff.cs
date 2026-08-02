// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.SceneView;
using Vixen.Geometry;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.Blockout;

/// <summary>Doc 24's P7: a block-out becoming an asset, and coming back as one.</summary>
/// <remarks>
///     <para>
///         <b>"Where the block-out becomes something an artist replaces."</b> The exit criterion is
///         that a block-out becomes an asset, an artist opens it in a DCC, replaces it, and the level
///         does not change shape — so everything here is about one piece of geometry crossing a
///         boundary without moving.
///     </para>
///     <para>
///         ⚠ <b>The bake and the export write the same file, and that is the decision.</b> The plan
///         asks for a bake "through the existing importer machinery" and for OBJ and glTF export as
///         separate rows; the existing importer machinery reads OBJ. Writing the artist's file and
///         pointing the entity at <i>that</i> makes the two rows one artefact — so the thing in the
///         level and the thing on the artist's disk are the same bytes rather than two things that
///         have to be kept in step.
///     </para>
/// </remarks>
public static class BlockoutHandoff {
    /// <summary>Bakes the selected block-out geometry into a mesh asset and points the entity at it.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="baker">What puts the file into the project.</param>
    /// <param name="name">What to call the asset, or null for the entity's own name.</param>
    /// <returns>How many entities were baked.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The geometry is written in the entity's own space, not the world's.</b> A baked
    ///         asset is something the entity is an <i>instance</i> of, so its vertices have to be
    ///         where the entity's transform expects them — an export centred on the world would give a
    ///         mesh that arrives offset by wherever in the level it was standing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The edit mesh goes and the entity keeps its transform.</b> That is what makes the
    ///         bake worth doing: the entity stops carrying a few thousand numbers in the scene file and
    ///         starts carrying two references, and every other instance of the same asset shares one
    ///         upload. An undo puts the geometry back, because <c>EditMeshCommand</c> recorded it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An import that fails leaves the entity alone.</b> A bake that removed the geometry
    ///         and then could not point the entity at anything would be a wall that vanished because a
    ///         file was locked.
    ///     </para>
    /// </remarks>
    public static int Bake(SceneDocument document, IMeshBaker baker, string? name = null) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(baker);

        var baked = 0;

        using (document.Stack.BeginTransaction("Bake To Asset")) {
            foreach (var entity in document.Selection.Items.ToArray()) {
                if (document.MeshOf(entity) is null) {
                    continue;
                }

                var label = name ?? document.NameOf(entity);
                var pieces = MeshExport.Pieces(document, [entity], centre: entity);

                var reference = baker.Bake(label, ".obj", MeshExport.Obj(pieces, label));

                if (reference.IsNull) {
                    continue;
                }

                // ⚠ A parametric shape demotes first, because its parameters describe geometry the
                // entity is about to stop carrying — and an undo has to put the parameters back after
                // it has put the mesh back, which is the order two commands in a transaction give.
                if (document.IsParametric(entity)) {
                    document.Stack.Execute(ShapeCommand.Demote(document, entity, "Bake To Asset"));
                }

                document.Stack.Execute(EditMeshCommand.Removed(document, entity, "Bake To Asset"));

                MeshRenderables.Attach(document.World, entity, MeshRenderables.Default(reference));

                baked++;
            }
        }

        return baked;
    }

    /// <summary>Writes the selected geometry out as a file for somebody else's tool.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="format">Which format — <c>.obj</c> or <c>.gltf</c>.</param>
    /// <param name="entities">Which entities, or null for the selection.</param>
    /// <param name="name">What to call the scene inside the file.</param>
    /// <returns>The file's text, or an empty string when there was nothing to write.</returns>
    /// <remarks>
    ///     ⚠ <b>Text back rather than a path.</b> Where a file goes is a dialog's answer and this
    ///     assembly has no dialogs — the same reason <c>SceneDocument.Writer</c> is an interface. It
    ///     also makes the export a unit test rather than a temporary directory.
    /// </remarks>
    public static string Export(
        SceneDocument document,
        string format = ".obj",
        IEnumerable<Entity>? entities = null,
        string name = "Blockout"
    ) {
        ArgumentNullException.ThrowIfNull(document);

        var chosen = entities ?? document.Selection.Items.ToArray();
        var pieces = MeshExport.Pieces(document, chosen);

        if (pieces.Count == 0) {
            return string.Empty;
        }

        return format.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
            ? MeshExport.Gltf(pieces, name)
            : MeshExport.Obj(pieces, name);
    }

    /// <summary>Makes an entity's mesh asset editable again, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="meshes">Where a mesh reference's geometry comes from.</param>
    /// <returns>How many entities were made editable.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's "import back as editable", which ProBuilder calls ProBuilderize.</b> An
    ///         artist hands back a corridor and a designer decides it is thirty centimetres too
    ///         narrow; this is what lets them fix it rather than file a request.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The reference stays on the entity and stops being what draws it.</b> That is the
    ///         same rule the parametric demotion follows — see <c>SceneMeshes.Drawn</c>, where an
    ///         edited mesh wins over everything — and it means an undo of the whole session leaves the
    ///         entity pointing at the asset it came from rather than at nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What comes back is welded and n-gonned as far as the tolerance allows, which is not
    ///         the mesh the artist authored.</b> A <c>MeshData</c> is one vertex per corner and
    ///         triangles only; <c>EditMesh.FromTriangles</c> welds the positions back into a graph and
    ///         <c>Regroup</c> puts coplanar neighbours in a group, but two triangles that were a quad
    ///         are two triangles. That is stated rather than hidden: an imported mesh made editable is
    ///         a mesh you can move the corners of, not the modelling history it had.
    ///     </para>
    /// </remarks>
    public static int Editable(SceneDocument document, IMeshSource meshes) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(meshes);

        var made = 0;

        using (document.Stack.BeginTransaction("Make Editable")) {
            foreach (var entity in document.Selection.Items.ToArray()) {
                if (document.MeshOf(entity) is not null
                    || !MeshRenderables.TryGet(document.World, entity, out var renderable)
                    || renderable.Mesh.IsNull
                    || !meshes.TryGet(renderable.Mesh, out var data)) {
                    continue;
                }

                var mesh = EditMeshes.From(data);

                mesh.Regroup();

                document.SetMesh(entity, mesh);
                document.Stack.Execute(EditMeshCommand.Rebuilt(document, entity, null, "Make Editable"));

                made++;
            }
        }

        return made;
    }

    /// <summary>The collision volumes a block-out mesh implies, in the entity's own space.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="into">A box per connected shell. Cleared first.</param>
    /// <returns>How many boxes there are.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's "box per convex piece, or the mesh itself".</b> A box per connected shell is
    ///         what a block-out actually wants: a room built out of six boxes gets six colliders, all
    ///         of them dynamic-capable, where one mesh collider would be static-only and slower to
    ///         query. <see cref="MeshCollision" /> says why a shell rather than a convex piece.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Boxes rather than <c>ShapeDescription</c>s, and that is a layering decision rather
    ///         than a missing step.</b> A hull, a mesh or a compound is registered <i>by its data</i>
    ///         with a live <c>PhysicsWorld</c> and the description holds the index it hands back — see
    ///         <c>ShapeDescription</c>'s own remarks — so the thing that can turn these into shapes is
    ///         the host that has a world. A blockout mode that referenced the physics engine to
    ///         describe a box would be the coupling doc 11's layering exists to prevent, for one
    ///         constructor call.
    ///     </para>
    /// </remarks>
    public static int Collision(SceneDocument document, Entity entity, List<BoundingBox> into) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        return document.MeshOf(entity) is { } mesh ? MeshCollision.Boxes(mesh, into) : 0;
    }

    /// <summary>The mesh itself as collision, for the geometry a box cannot stand in for.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The positions and triangle indices, or empty arrays for an entity with no mesh.</returns>
    /// <remarks>⚠ <b>Static only</b>, because a concave mesh has no usable inertia tensor — which is
    ///     what <c>ShapeDescription.CanBeDynamic</c> reports and is why the boxes are the default.</remarks>
    public static (Vector3[] Positions, int[] Indices) CollisionMesh(SceneDocument document, Entity entity) {
        ArgumentNullException.ThrowIfNull(document);

        return document.MeshOf(entity) is { } mesh ? MeshCollision.Triangles(mesh) : ([], []);
    }
}
