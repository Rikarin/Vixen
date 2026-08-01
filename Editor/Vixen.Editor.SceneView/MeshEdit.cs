// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.SceneView;

/// <summary>Which mesh is being edited, in which element mode, and what is selected in it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's P2, editor side.</b> The kernel has the selection and the topology queries —
///         <see cref="MeshSelection" /> and <see cref="MeshTopology" /> — and what is here is the part
///         that needs a scene: which entity, which of its elements the pointer is over, and what a
///         click does about it.
///     </para>
///     <para>
///         ⚠ <b>One per editor and handed to every pane, exactly as <see cref="SnapContext" /> is.</b>
///         Selecting a face in the perspective view and seeing it highlighted in the top view is the
///         behaviour every reference toolset has; one of these per pane would be four selections of
///         one mesh with nothing reconciling them, and the first operation to read the wrong one would
///         be a bug nobody could reproduce.
///     </para>
///     <para>
///         ⚠ <b>One target rather than a set, and that is what every reference toolset does too.</b>
///         The element indices of two meshes are two numbering schemes, so a selection spanning both is
///         one that no single operation can act on. The target follows the entity selection: exactly
///         one entity selected is the mesh being edited, and anything else is none.
///     </para>
///     <para>
///         ⚠ <b>The selection survives a position change and is dropped by a topology change, which is
///         P2's exit criterion.</b> Undoing a drag puts the corners back and leaves every index meaning
///         what it did; an extrude renumbers the tables, so an index kept across one names a different
///         element. The table sizes recorded when the mesh was last seen are what tell the two apart —
///         cheaply, and without the document having to describe what changed.
///     </para>
/// </remarks>
public sealed class MeshEdit {
    readonly SceneDocument document;

    int positions;
    int edges;
    int faces;
    int version = -1;

    /// <summary>Watches a scene, so that a mesh changing under the selection is noticed.</summary>
    /// <param name="document">The scene.</param>
    public MeshEdit(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
    }

    /// <summary>The scene the mesh being edited belongs to.</summary>
    /// <remarks>What a geometry verb needs: the mesh to change, and the undo stack to record the
    ///     change on. Exposed rather than passed alongside, because the two must be the same scene.</remarks>
    public SceneDocument Document => document;

    /// <summary>Whether an element mode is in force at all.</summary>
    /// <remarks>
    ///     <b>What the mode bar's Object entry turns off.</b> Doc 24's inventory makes Object one of
    ///     four element modes rather than the absence of the other three — see <c>BlockoutElement</c> —
    ///     and this is that fourth state, held once rather than answered twice.
    /// </remarks>
    public bool IsEnabled {
        get;
        set {
            if (field == value) {
                return;
            }

            field = value;

            if (!value) {
                Target = Entity.Null;
            }
        }
    }

    /// <summary>Whose mesh is being edited, or <see cref="Entity.Null" /> for none.</summary>
    /// <remarks>Setting it to a different entity clears the selection, because an index into one
    ///     mesh's tables means nothing about another's.</remarks>
    public Entity Target {
        get;
        private set {
            if (field == value) {
                return;
            }

            field = value;
            version = -1;

            Selection.Clear();
            Hover = SubObject.None;
        }
    }

    /// <summary>Which kind of element a click selects.</summary>
    /// <remarks>
    ///     ⚠ <b>Setting it converts what is selected rather than dropping it.</b> Going from face mode
    ///     to vertex mode with a wall selected leaves its corners selected, which is what makes
    ///     "select the wall, then drag one of its corners" one gesture instead of two — see
    ///     <see cref="MeshSelection.Convert" /> for the rule in both directions.
    /// </remarks>
    public MeshElementKind Element {
        get => Selection.Kind;
        set {
            if (Mesh is { } mesh) {
                Selection.Convert(mesh, value);
            } else {
                Selection.SetKind(value);
            }
        }
    }

    /// <summary>What is selected.</summary>
    public MeshSelection Selection { get; } = new();

    /// <summary>Which element the pointer is over, or <see cref="SubObject.None" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept here rather than recomputed by whatever draws it.</b> Hover is one query per
    ///     pointer move — doc 24's B4 sets that as the bar — and a second consumer asking again would
    ///     double it and could disagree with the first about what is under the pointer on the frame
    ///     the pointer moved between the two.
    /// </remarks>
    public SubObject Hover { get; set; }

    /// <summary>How many divisions a curved primitive is made editable at.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="SceneMeshes.Segments" />' and <c>ScenePicker.Segments</c>' number, and it
    ///     has to stay theirs.</b> Making a sphere editable at a different tessellation from the one it
    ///     is drawn and picked at is a mesh whose faces are not the faces on screen — a click that
    ///     selects a face nobody can see, which is the same disagreement the picker's own remarks are
    ///     about in a second place.
    /// </remarks>
    public int Segments { get; set; } = 24;

    /// <summary>Whether the target has geometry to edit.</summary>
    public bool IsActive => IsEnabled && Mesh is not null;

    /// <summary>The mesh being edited, or <see langword="null" />.</summary>
    public EditMesh? Mesh => Target.IsNull ? null : document.MeshOf(Target);

    /// <summary>Which filter a pick should use for the current element mode.</summary>
    public SubObjectFilter Filter =>
        Element switch {
            MeshElementKind.Vertex => SubObjectFilter.Vertex,
            MeshElementKind.Edge => SubObjectFilter.Edge,
            _ => SubObjectFilter.Face
        };

    /// <summary>Brings the target and the selection up to date with the scene.</summary>
    /// <returns>Whether the selection was dropped because the mesh's structure changed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called before anything reads the selection, and it is cheap when nothing has
    ///         happened.</b> The version the document keeps per entity moves on every edit, so the
    ///         common case is one integer comparison — which is what lets a frame loop and a pointer
    ///         move both call it without either having to know whether the other did.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A topology change drops the selection rather than trimming it, and the distinction
    ///         is what P2's exit asks for.</b> If the tables are the same size the mesh was dragged, and
    ///         every index still names the element the designer chose. If they are not, the numbering
    ///         has moved under the selection — an extrude inserts positions, a dissolve removes faces —
    ///         and what is left is not a smaller version of what was selected, it is a set of indices
    ///         that now name other things.
    ///     </para>
    /// </remarks>
    public bool Reconcile() {
        Target = IsEnabled && document.Selection.Count == 1 ? document.Selection.Items[0] : Entity.Null;

        if (Mesh is not { } mesh) {
            var had = !Selection.IsEmpty;

            Selection.Clear();
            Hover = SubObject.None;

            return had;
        }

        var current = document.MeshVersion(Target);

        if (current == version) {
            return false;
        }

        var known = version >= 0;
        var restructured = mesh.PositionCount != positions || mesh.Edges.Count != edges || mesh.FaceCount != faces;

        version = current;
        positions = mesh.PositionCount;
        edges = mesh.Edges.Count;
        faces = mesh.FaceCount;

        if (!known || !restructured) {
            return false;
        }

        Selection.Clear();
        Hover = SubObject.None;

        return true;
    }

    /// <summary>Goes into an element mode on whatever is selected.</summary>
    /// <param name="kind">Which kind of element a click will select.</param>
    /// <returns>Whether there turned out to be a mesh to edit.</returns>
    /// <remarks>
    ///     ⚠ <b>The demotion happens here rather than at the first click, and that is what makes the
    ///     mode visible.</b> Pressing <c>3</c> and seeing nothing change — because the entity is still
    ///     a parametric shape and there is no cage to draw — is a mode that reads as broken. Entering
    ///     an element mode <i>is</i> the deliberate act D6 asks the door to be opened by; a click is a
    ///     selection, and selections are not undoable.
    /// </remarks>
    public bool Enter(MeshElementKind kind) {
        IsEnabled = true;

        Reconcile();
        MakeEditable();
        Reconcile();

        Element = kind;

        return IsActive;
    }

    /// <summary>Comes back out of the element modes.</summary>
    public void Exit() {
        IsEnabled = false;
    }

    /// <summary>Gives the target editable geometry, if it has none and can have some.</summary>
    /// <returns>The mesh, or <see langword="null" /> when there is nothing to make one from.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's D6 demotion: a shape becomes a plain mesh, and it is a one-way door.</b> The
    ///         primitive kind stays on the entity and stops being what draws it — see
    ///         <c>SceneMeshes.Drawn</c>, where an edited mesh wins over everything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>D6 asks for a confirmation the first time and there is none yet, deliberately.</b>
    ///         The door is one-way because it throws away <i>live parameters</i>, and a
    ///         <c>PrimitiveShape</c> has none — a kind and a material, both of which survive. The
    ///         confirmation and the badge arrive with the shape tool in P4, which is what creates the
    ///         parameters they protect; warning about the loss of something that does not exist yet is
    ///         a dialog people learn to dismiss before it ever means anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Undoable, and pushed rather than applied silently.</b> Making a wall editable is a
    ///         change to the document — it is what the file writes — so a designer who did it by
    ///         pressing <c>2</c> with the wrong thing selected has to be able to press <c>Ctrl+Z</c>.
    ///     </para>
    /// </remarks>
    public EditMesh? MakeEditable() {
        if (Target.IsNull) {
            return null;
        }

        if (document.MeshOf(Target) is { } already) {
            return already;
        }

        if (!PrimitiveShapes.TryGet(document.World, Target, out var kind)) {
            return null;
        }

        document.SetMesh(
            Target,
            EditMeshes.From(kind, Segments, Math.Max(MeshPrimitives.MinimumSegments, Segments / 2))
        );

        document.Stack.Execute(EditMeshCommand.Rebuilt(document, Target, null, "Make Editable"));

        return document.MeshOf(Target);
    }

    /// <summary>Turns a picked element into a change to the selection.</summary>
    /// <param name="hit">What was under the pointer.</param>
    /// <param name="additive">Whether the click extends the selection rather than replacing it.</param>
    /// <returns>Whether the selection changed.</returns>
    /// <remarks>
    ///     ⚠ <b>A miss clears, and only when the click was not additive</b> — the rule
    ///     <c>SceneViewport.Select</c> already applies to entities, and applying a different one to
    ///     elements would be a rule nobody could describe.
    /// </remarks>
    public bool Clicked(SubObject hit, bool additive) {
        var was = Selection.Version;

        if (!hit.IsHit) {
            if (!additive) {
                Selection.Clear();
            }

            return Selection.Version != was;
        }

        if (additive) {
            Selection.Toggle(hit.Index);
        } else {
            Selection.Set(hit.Index);
        }

        return Selection.Version != was;
    }

    /// <summary>Which shared positions the selection covers.</summary>
    /// <param name="into">The position indices. Cleared first.</param>
    public void Positions(List<int> into) {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (Mesh is { } mesh) {
            Selection.Positions(mesh, into);
        }
    }

    /// <summary>Where the selection is, in world space.</summary>
    /// <param name="transform">The entity's world matrix.</param>
    /// <returns>The centre, or null when nothing is selected.</returns>
    /// <remarks>What the gizmo sits on while an element mode is in force, which is the selection's
    ///     centre rather than the entity's origin — a drag of one wall pivots on that wall.</remarks>
    public Vector3? Centre(in Matrix4x4 transform) =>
        Mesh is { } mesh && Selection.Centre(mesh) is { } centre
            ? Matrix4x4.TransformPosition(centre, transform)
            : null;
}
