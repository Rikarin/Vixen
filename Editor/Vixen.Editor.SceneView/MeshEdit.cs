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
            var was = Selection.Kind;

            if (Mesh is { } mesh) {
                Selection.Convert(mesh, value);
            } else {
                Selection.SetKind(value);
            }

            if (Selection.Kind != was) {
                ElementChanged?.Invoke(Selection.Kind);
            }
        }
    }

    /// <summary>Raised when the element mode changes, however it changed.</summary>
    /// <remarks>
    ///     ⚠ <b>Because a <i>verb</i> can change it, and the mode bar has to follow.</b> Weld leaves a
    ///     vertex and bevel leaves faces, so a verb run in one mode legitimately ends in another —
    ///     and without this the segmented control still showed the old one, the keys still meant the
    ///     old one, and the way out was to click a different mode and come back. That is the shape of
    ///     "the tool stopped responding", and it was two states for one fact.
    /// </remarks>
    public event Action<MeshElementKind>? ElementChanged;

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

    /// <summary>What to ask before a shape's live parameters are thrown away, or null to just do it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>D6's confirmation, and it arrives with P4 because P4 is what creates the parameters
    ///         it protects.</b> Answering <see langword="false" /> leaves the shape parametric and the
    ///         edit undone, which is what a Cancel has to mean; answering <see langword="true" /> opens
    ///         the door and it does not close again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked once and then never again, which is what "the first time" means and is not
    ///         the same as "once per shape".</b> A designer who has understood what the door is does
    ///         not need to be told again on the second wall; a dialog that appeared every time is one
    ///         people learn to dismiss without reading, which is worse than not having it. What tells
    ///         them afterwards is the badge — <c>SceneDocument.IsPlainMesh</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null goes ahead rather than refusing.</b> Every headless host and every test has no
    ///         way to ask, and a mesh kernel that could not be driven without a dialog would be one
    ///         nothing can script.
    ///     </para>
    /// </remarks>
    public Func<Entity, bool>? Confirm { get; set; }

    /// <summary>Whether the one-way door has been explained yet, this session.</summary>
    bool warned;

    /// <summary>Makes sure the target's geometry is something an edit may change.</summary>
    /// <returns>Whether it may — false only when the confirmation was declined.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's D6, at the moment D6 says it happens: the first edit to a face.</b> A shape
    ///         with live parameters draws, picks and selects like any other mesh — see
    ///         <c>SceneDocument.SetShape</c> — so entering an element mode costs it nothing and there
    ///         is no reason to charge for it. What cannot survive is an <i>edit</i>, because the next
    ///         parameter change would regenerate the mesh over the top of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called by every verb that changes geometry, including the surface ones.</b> A UV
    ///         projection is not a change of shape and is still a change to the mesh, so a mapping
    ///         applied to a shape that stayed parametric would vanish the next time anybody nudged its
    ///         width — silently, and a long way from what caused it.
    ///     </para>
    /// </remarks>
    public bool Demote() {
        if (Target.IsNull) {
            return true;
        }

        // ⚠ A derived mesh collapses as well, and it is the same door for the same reason: the result
        // of a boolean is a function of its operands, so an edit to it is an edit the next
        // re-evaluation would overwrite without saying so. Collapsing first makes the edit stick and
        // makes the operands somebody's to delete rather than a graph quietly rebuilding over them.
        if (!document.IsParametric(Target) && !document.IsDerived(Target)) {
            return true;
        }

        if (!warned && Confirm is { } ask && !ask(Target)) {
            return false;
        }

        warned = true;

        if (document.IsDerived(Target)) {
            document.Stack.Execute(new BooleanCommand(document, Target, null, "Apply Boolean"));
        }

        if (document.IsParametric(Target)) {
            document.Stack.Execute(ShapeCommand.Demote(document, Target));
        }

        return true;
    }

    /// <summary>Goes into an element mode on whatever is selected.</summary>
    /// <param name="kind">Which kind of element a click will select.</param>
    /// <returns>Whether there turned out to be a mesh to edit.</returns>
    /// <remarks>
    ///     ⚠ <b>Entering a mode no longer demotes anything, and that changed when P4 gave a shape a
    ///     mesh of its own.</b> P2 put the demotion here because a parametric entity had no cage to
    ///     draw, so pressing <c>3</c> on one and seeing nothing happen read as the mode being broken.
    ///     A shape generated by <see cref="MeshShapes" /> has a real mesh in the document from the
    ///     moment it is created, so the cage is there, every element of it selects, and the parameters
    ///     survive until something actually edits it — which is what D6 asked for in the first place.
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
    ///         <b>A <c>PrimitiveShape</c> becoming a mesh, which is a promotion rather than a
    ///         demotion.</b> The primitive kind stays on the entity and stops being what draws it —
    ///         see <c>SceneMeshes.Drawn</c>, where an edited mesh wins over everything — and nothing is
    ///         lost, because a <c>PrimitiveShape</c> is a kind and a material and both survive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>D6's one-way door is <see cref="Demote" /> and is not this.</b> What that throws
    ///         away is <i>live parameters</i>, which only a shape built by <see cref="MeshShapes" />
    ///         has; a primitive has none, so there is nothing here to warn about and no confirmation is
    ///         asked for.
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
