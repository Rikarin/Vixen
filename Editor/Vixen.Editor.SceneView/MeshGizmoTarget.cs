// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Geometry;

namespace Vixen.Editor.SceneView;

/// <summary>A mesh's selected elements, as one thing the gizmo can move.</summary>
/// <remarks>
///     <para>
///         <b><see cref="EntityGizmoTarget" />'s counterpart for doc 24's P2, and it is deliberately
///         one target rather than one per position.</b> The gizmo's arithmetic recomputes every target
///         from where it was at mouse-down — see <c>TransformGizmo</c> — so a hundred selected
///         vertices as a hundred targets would work and would rotate each of them about its own
///         centre, which is not what rotating a face means. One target whose origin is the selection's
///         centre is what makes rotate and scale behave.
///     </para>
///     <para>
///         ⚠ <b>The whole transform is applied to the covered positions, in the mesh's own space.</b>
///         A gizmo hands back a world position, a world rotation and a scale; what a mesh wants is
///         where each of its corners went. So the delta is assembled here, taken into local space
///         through the entity's inverse, and applied about the centre the drag started from.
///     </para>
///     <para>
///         ⚠ <b>The entity does not move, and that is the difference from dragging it.</b> Moving a
///         wall in face mode moves the wall's geometry inside the entity; the entity's transform is
///         what object mode drags. Confusing the two is how a designer ends up with a corridor whose
///         pivot is nowhere near it.
///     </para>
/// </remarks>
public sealed class MeshGizmoTarget : IGizmoTarget {
    readonly SceneDocument document;
    readonly MeshEdit editing;
    readonly List<int> covered = [];
    readonly List<Vector3> started = [];

    Vector3 origin;
    Quaternion turn = Quaternion.Identity;
    Vector3 size = Vector3.One;

    /// <summary>Views the selected elements of the mesh being edited as a gizmo target.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="editing">What is being edited.</param>
    public MeshGizmoTarget(SceneDocument document, MeshEdit editing) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(editing);

        this.document = document;
        this.editing = editing;

        Capture();
    }

    /// <summary>Which positions this drag is moving, and where each of them started.</summary>
    /// <remarks>What <c>EditMeshCommand.Moved</c> records, and the reason it is captured at
    ///     construction: the gizmo's targets are built at mouse-down and read again at the end.</remarks>
    public IReadOnlyList<int> Positions => covered;

    /// <summary>Where each of them was when the drag began.</summary>
    public IReadOnlyList<Vector3> Before => started;

    /// <inheritdoc />
    public Vector3 Position {
        get => Matrix4x4.TransformPosition(origin, Placement);
        set {
            if (!Matrix4x4.Invert(Placement, out var inverse)) {
                return;
            }

            Apply(Matrix4x4.TransformPosition(value, inverse), turn, size);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Identity until the drag turns it, rather than the entity's rotation.</b> A face has
    ///     no rotation of its own — what a rotate gizmo does to one is turn its corners about the
    ///     selection's centre — so the value read back is the turn applied so far, which is what makes
    ///     the gizmo's recompute-from-mouse-down arithmetic land where the pointer says.
    /// </remarks>
    public Quaternion Rotation {
        get => turn;
        set => Apply(origin, value, size);
    }

    /// <inheritdoc />
    public Vector3 Scale {
        get => size;
        set => Apply(origin, turn, value);
    }

    /// <inheritdoc />
    /// <remarks>The entity's own matrix, so that <c>GizmoSpace.Parent</c> in an element mode means
    ///     the mesh's axes — which is what "along this wall" means while you are inside it.</remarks>
    public Matrix4x4 ParentToWorld => Placement;

    /// <summary>Whether there is anything to move.</summary>
    public bool IsEmpty => covered.Count == 0;

    /// <summary>The entity's local-to-world matrix.</summary>
    Matrix4x4 Placement =>
        document.World.Has<WorldTransform>(editing.Target)
            ? document.World.Read<WorldTransform>(editing.Target).Value
            : Matrix4x4.Identity;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>An element drag records the positions it moved rather than the target's pose.</b> Doc
    ///     24 § D3's two granularities, and this is the first one: a <c>TransformTargetsCommand</c>
    ///     over this target would record a transform on a thing that has none — the entity did not
    ///     move, its corners did.
    ///     <para>
    ///         This used to be a type test in <c>SceneViewport.EndManipulate</c>, which meant the
    ///         viewport held a list of which target kinds were exceptions to its own rule.
    ///     </para>
    /// </remarks>
    public GizmoEdit? Record(in GizmoDrag drag) {
        if (IsEmpty) {
            return null;
        }

        var command = EditMeshCommand.Moved(
            document,
            editing.Target,
            [.. covered],
            [.. started],
            editing.Element switch {
                MeshElementKind.Vertex => "Move Vertices",
                MeshElementKind.Edge => "Move Edges",
                _ => "Move Faces"
            }
        );

        return new(command, document.Stack);
    }

    /// <summary>Records which positions are moving and where they start.</summary>
    void Capture() {
        editing.Positions(covered);
        started.Clear();

        if (editing.Mesh is not { } mesh) {
            return;
        }

        foreach (var position in covered) {
            started.Add(mesh.Positions[position]);
        }

        origin = Centre();
    }

    /// <summary>Writes the drag's transform into the mesh, from where every position started.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>From the captured positions rather than from where the last frame left them.</b>
    ///         The gizmo recomputes from mouse-down and calls these setters with absolute values, so
    ///         composing onto the current positions would apply the drag once per frame — the same
    ///         accumulation <c>TransformGizmo</c>'s own design exists to avoid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Dragging a vertex is what closes doc 24's D6 door, and it closes here.</b> This is
    ///         the first frame of the drag at which anything has actually been edited, and
    ///         <see cref="MeshEdit.Demote" /> is a no-op on every frame after it — so the confirmation
    ///         is asked once, before the mesh moves, and the drag is abandoned if it is declined.
    ///     </para>
    /// </remarks>
    void Apply(Vector3 at, Quaternion rotation, Vector3 scale) {
        origin = at;
        turn = rotation;
        size = scale;

        if (editing.Mesh is not { } mesh || !editing.Demote()) {
            return;
        }

        var was = Centre(started);

        for (var index = 0; index < covered.Count; index++) {
            var arm = started[index] - was;

            mesh.MovePosition(covered[index], at + Quaternion.Transform(arm * scale, rotation));
        }

        document.TouchMesh(editing.Target);
    }

    Vector3 Centre() => Centre(started);

    static Vector3 Centre(List<Vector3> points) {
        if (points.Count == 0) {
            return Vector3.Zero;
        }

        var total = Vector3.Zero;

        foreach (var point in points) {
            total += point;
        }

        return total / points.Count;
    }
}
