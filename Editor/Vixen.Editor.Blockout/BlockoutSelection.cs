// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>The selection verbs of doc 24's inventory, as functions over one editing state.</summary>
/// <remarks>
///     <para>
///         <b>Between the mode and the kernel, and it holds nothing.</b> The walks are
///         <see cref="MeshTopology" />'s and the set is <see cref="MeshSelection" />'s; what is here is
///         the small amount of arranging each verb needs — which element the walk starts from, what to
///         do when the mode and the verb disagree about a kind, and whether a verb applies at all.
///     </para>
///     <para>
///         ⚠ <b>Every one of them is a no-op rather than an error when it does not apply.</b> "Select
///         loop" with nothing selected, "select coplanar" in vertex mode, "grow" on an empty selection
///         — all are things a key press produces, none is a mistake, and a command that threw would
///         take the editor down over a keystroke.
///     </para>
/// </remarks>
public static class BlockoutSelection {
    /// <summary>Selects the edge loop through the active edge.</summary>
    /// <param name="editing">The editing state.</param>
    /// <param name="additive">Whether it extends what is selected.</param>
    /// <returns>Whether anything was selected.</returns>
    /// <remarks>
    ///     ⚠ <b>Through the <i>active</i> edge, which is the one chosen last.</b> A loop through "the
    ///     selection" is undefined once the selection is more than one edge; Blender, ProBuilder and
    ///     Maya all walk from the last one, and <see cref="MeshSelection.Active" /> is why the set is
    ///     ordered.
    /// </remarks>
    public static bool Loop(MeshEdit editing, bool additive = false) => Walk(editing, MeshTopology.EdgeLoop, additive);

    /// <summary>Selects the edge ring through the active edge.</summary>
    /// <param name="editing">The editing state.</param>
    /// <param name="additive">Whether it extends what is selected.</param>
    /// <returns>Whether anything was selected.</returns>
    public static bool Ring(MeshEdit editing, bool additive = false) => Walk(editing, MeshTopology.EdgeRing, additive);

    /// <summary>Takes in everything touching what is selected.</summary>
    /// <param name="editing">The editing state.</param>
    /// <returns>Whether there was a mesh to grow in.</returns>
    public static bool Grow(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Selection.Grow(mesh);
        return true;
    }

    /// <summary>Gives back everything on the edge of what is selected.</summary>
    /// <param name="editing">The editing state.</param>
    /// <returns>Whether there was a mesh to shrink in.</returns>
    public static bool Shrink(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Selection.Shrink(mesh);
        return true;
    }

    /// <summary>Selects every face in the same group as the active one.</summary>
    /// <param name="editing">The editing state.</param>
    /// <param name="additive">Whether it extends what is selected.</param>
    /// <returns>Whether anything was selected.</returns>
    /// <remarks>Unreal's "select PolyGroup", and the blockout-specific one: a group is what makes a
    ///     cube's side one wall rather than two triangles.</remarks>
    public static bool Group(MeshEdit editing, bool additive = false) =>
        Faces(editing, additive, (mesh, face, into) => MeshTopology.Group(mesh, mesh.Faces[face].Group, into));

    /// <summary>Selects every face coplanar with and joined to the active one.</summary>
    /// <param name="editing">The editing state.</param>
    /// <param name="additive">Whether it extends what is selected.</param>
    /// <returns>Whether anything was selected.</returns>
    /// <remarks>The blockout-specific selection doc 24's inventory calls out by name: a wall that has
    ///     been cut into a dozen faces is still one flat surface, and this is how you get all of it.</remarks>
    public static bool Coplanar(MeshEdit editing, bool additive = false) =>
        Faces(editing, additive, static (mesh, face, into) => MeshTopology.Coplanar(mesh, face, into));

    /// <summary>Selects every face joined to the active one, however the surface turns.</summary>
    /// <param name="editing">The editing state.</param>
    /// <param name="additive">Whether it extends what is selected.</param>
    /// <returns>Whether anything was selected.</returns>
    public static bool Linked(MeshEdit editing, bool additive = false) =>
        Faces(editing, additive, static (mesh, face, into) => MeshTopology.Shell(mesh, face, into));

    /// <summary>Selects every element of the current mode.</summary>
    /// <param name="editing">The editing state.</param>
    /// <returns>Whether there was a mesh.</returns>
    public static bool All(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Selection.All(mesh);
        return true;
    }

    /// <summary>Selects what is not selected.</summary>
    /// <param name="editing">The editing state.</param>
    /// <returns>Whether there was a mesh.</returns>
    public static bool Invert(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Selection.Invert(mesh);
        return true;
    }

    /// <summary>Deselects everything.</summary>
    /// <param name="editing">The editing state.</param>
    /// <returns>Whether there was anything to deselect.</returns>
    public static bool None(MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(editing);

        var was = editing.Selection.Count;

        editing.Selection.Clear();

        return was > 0;
    }

    /// <summary>Runs an edge walk from the active edge, converting the mode if it has to.</summary>
    /// <remarks>
    ///     ⚠ <b>A loop asked for in face mode converts to edges rather than declining.</b> "Select
    ///     loop" is a statement about edges whatever mode you are in, and the alternative — a key that
    ///     does nothing in three of the four modes — is a key people conclude is broken.
    /// </remarks>
    static bool Walk(MeshEdit editing, Action<EditMesh, int, List<int>> walk, bool additive) {
        ArgumentNullException.ThrowIfNull(editing);

        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Element = MeshElementKind.Edge;

        if (editing.Selection.Active < 0) {
            return false;
        }

        List<int> taken = [];

        walk(mesh, editing.Selection.Active, taken);

        if (taken.Count == 0) {
            return false;
        }

        if (additive) {
            editing.Selection.Union(taken);
        } else {
            editing.Selection.Set(taken);
        }

        return true;
    }

    /// <summary>Runs a face query from the active face, converting the mode if it has to.</summary>
    static bool Faces(MeshEdit editing, bool additive, Action<EditMesh, int, List<int>> query) {
        ArgumentNullException.ThrowIfNull(editing);

        if (editing.Mesh is not { } mesh) {
            return false;
        }

        editing.Element = MeshElementKind.Face;

        var active = editing.Selection.Active;

        if ((uint) active >= (uint) mesh.FaceCount) {
            return false;
        }

        List<int> taken = [];

        query(mesh, active, taken);

        if (taken.Count == 0) {
            return false;
        }

        if (additive) {
            editing.Selection.Union(taken);
        } else {
            editing.Selection.Set(taken);
        }

        return true;
    }
}
