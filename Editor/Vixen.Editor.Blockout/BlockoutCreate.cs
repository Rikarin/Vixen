// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>Doc 24's Creation table: what makes geometry, rather than what changes it.</summary>
/// <remarks>
///     <para>
///         <b>The shape tool, the poly shape, and duplicate, mirror and array.</b> Everything here
///         produces an entity or entities; nothing here needs an element selection, which is why it is
///         a separate type from <see cref="BlockoutGeometry" /> and why its commands are reachable in
///         Object mode.
///     </para>
///     <para>
///         ⚠ <b>A created shape keeps its parameters, and that is the whole of D6's first half.</b> A
///         corridor that should be a metre wider is <see cref="Resize" /> — one number, one undo entry
///         — rather than a face selection and a drag. The door closes the first time somebody edits a
///         face of it; see <c>MeshEdit.Demote</c>.
///     </para>
///     <para>
///         ⚠ <b>Sizes are in world units and the transform stays uniform.</b> A shape carries its own
///         extent — see <see cref="ShapeParameters.Size" /> — so nothing here ever writes a non-uniform
///         scale, which is what keeps a later bevel the same width on every axis and a later projection
///         unstretched.
///     </para>
/// </remarks>
public static class BlockoutCreate {
    /// <summary>Creates a shape with live parameters, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="parameters">Which shape and how big.</param>
    /// <param name="at">Where to put it, in world units.</param>
    /// <param name="parent">What to hang it from, or <see cref="Entity.Null" /> for a root.</param>
    /// <returns>The entity.</returns>
    /// <remarks>
    ///     Named after the shape, which is what every editor does and what makes a hierarchy of
    ///     block-out geometry readable without clicking each row — the same rule
    ///     <c>SceneDocument.CreateShape</c> follows for a primitive.
    /// </remarks>
    public static Entity Shape(
        SceneDocument document,
        ShapeParameters parameters,
        Vector3 at = default,
        Entity parent = default
    ) {
        ArgumentNullException.ThrowIfNull(document);

        var placement = LocalTransform.Identity with { Position = at };

        using (document.Stack.BeginTransaction("Create " + parameters.Kind)) {
            var entity = document.Create(parameters.Kind.ToString(), placement, parent);

            document.Stack.Execute(ShapeCommand.Set(document, entity, parameters, "Create " + parameters.Kind));

            return entity;
        }
    }

    /// <summary>Creates a shape of a kind at its default size.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="kind">Which shape.</param>
    /// <param name="at">Where to put it.</param>
    /// <param name="parent">What to hang it from.</param>
    /// <returns>The entity.</returns>
    public static Entity Shape(SceneDocument document, ShapeKind kind, Vector3 at = default, Entity parent = default) =>
        Shape(document, ShapeParameters.Default(kind), at, parent);

    /// <summary>Changes a shape's live parameters, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">Whose.</param>
    /// <param name="parameters">What they should be.</param>
    /// <returns>Whether it had parameters to change.</returns>
    /// <remarks>
    ///     ⚠ <b>Consecutive changes to one shape merge into one history entry.</b> Dragging a width
    ///     field is one decision made over forty frames — see <see cref="ShapeCommand.TryMergeWith" />.
    /// </remarks>
    public static bool Resize(SceneDocument document, Entity entity, ShapeParameters parameters) {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.IsParametric(entity)) {
            return false;
        }

        document.Stack.Execute(ShapeCommand.Set(document, entity, parameters, "Shape Parameters"));
        return true;
    }

    /// <summary>Pulls a polygon clicked on the work plane up into a solid.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="footprint">The outline, in world units, in the plane's own space.</param>
    /// <param name="height">How far up to pull it.</param>
    /// <param name="plane">The plane it was clicked on.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The entity, or <see cref="Entity.Null" /> for an outline with fewer than three points.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's poly shape, and its own words for what it is for: how every irregular room
    ///         gets made.</b> Click a polygon, drag the height. The arithmetic is
    ///         <see cref="MeshShapes.Sweep" />, which is also what a staircase and a ramp are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A poly shape is a plain mesh from birth rather than a parametric one, and that is
    ///         a decision rather than an omission.</b> Its parameters would be a polygon of arbitrary
    ///         length and a height, which is not six numbers — so it would need a record of its own in
    ///         the scene format, a drawer of its own in the inspector, and an editing gesture of its own
    ///         to be worth having. What a designer actually does to one afterwards is move its corners,
    ///         which is what the element modes are for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The entity's origin is the outline's first point, not its centroid.</b> A room
    ///         whose origin is somewhere in the middle of its floor is one that snaps from a corner
    ///         nobody clicked; the first click is the one thing about the gesture the designer chose.
    ///     </para>
    /// </remarks>
    public static Entity Poly(
        SceneDocument document,
        IReadOnlyList<Vector2> footprint,
        float height,
        WorkPlane? plane = null,
        string name = "Room"
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(footprint);

        if (footprint.Count < 3 || MathF.Abs(height) < 1e-4f) {
            return Entity.Null;
        }

        var outline = new Vector2[footprint.Count];
        var origin = footprint[0];

        for (var point = 0; point < footprint.Count; point++) {
            outline[point] = footprint[point] - origin;
        }

        // ⚠ Wound anticlockwise whichever way it was clicked, because `Sweep` produces an inside-out
        // solid from a clockwise one — and which way round a designer drags a room is not something
        // they should have to know.
        if (Area(outline) < 0f) {
            System.Array.Reverse(outline);
        }

        var up = height < 0f ? -Vector3.UnitY : Vector3.UnitY;
        var mesh = MeshShapes.Sweep(outline, Vector3.Zero, Vector3.UnitZ, Vector3.UnitX, up * MathF.Abs(height));

        var at = plane?.ToWorld(new(origin.X, 0f, origin.Y)) ?? new Vector3(origin.X, 0f, origin.Y);
        var rotation = plane?.Rotation ?? Quaternion.Identity;

        using (document.Stack.BeginTransaction("Poly Shape")) {
            var entity = document.Create(name, LocalTransform.Identity with { Position = at, Rotation = rotation });

            document.SetMesh(entity, mesh);
            document.Stack.Execute(EditMeshCommand.Rebuilt(document, entity, null, "Poly Shape"));

            return entity;
        }
    }

    /// <summary>Copies the selected entities, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="offset">How far to move each copy.</param>
    /// <param name="into">The copies. Cleared first, or null to not report them.</param>
    /// <returns>How many were copied.</returns>
    /// <remarks>Doc 24's <c>Ctrl+D</c>. The copy is selected afterwards, because the next thing
    ///     anybody does to a duplicate is move it.</remarks>
    public static int Duplicate(SceneDocument document, Vector3 offset = default, List<Entity>? into = null) {
        ArgumentNullException.ThrowIfNull(document);

        List<Entity> made = into ?? [];

        if (SceneClone.Duplicate(document, document.Selection.Items.ToArray(), offset, made) == 0) {
            return 0;
        }

        document.Selection.Set(made);
        return made.Count;
    }

    /// <summary>Copies the selected entities and reflects the copies across a plane.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="plane">The plane to reflect across, in world space.</param>
    /// <param name="copy">Whether the originals stay.</param>
    /// <returns>How many entities were mirrored.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's <c>Ctrl+M</c>, across the work plane.</b> A mirrored wall is what makes the
    ///         second half of a symmetrical room free, and it is the verb people reach for immediately
    ///         after realising the first half took ten minutes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The geometry is reflected and the winding is flipped, rather than the transform
    ///         being given a negative scale.</b> A negative scale is the cheap answer and it is wrong in
    ///         two places at once: every face of the object is then inside out as far as the renderer's
    ///         culling is concerned, and every later extrude on it goes the wrong way. Reflecting the
    ///         positions and flipping the faces produces geometry that is right on its own terms.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Copy only, and never an instance.</b> Doc 24's table offers both; an <i>instance</i>
    ///         is a link that survives editing, which is a second kind of entity reference, a rule for
    ///         what happens when one side is edited, and a thing to draw in the outliner. That is a
    ///         feature with its own design and it is not this one — a copy is what a block-out pass
    ///         actually uses, because the two halves stop being symmetrical about ten minutes later.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Mirroring demotes.</b> A reflected staircase is not a staircase's parameters, and a
    ///         shape whose mesh had been mirrored while its parameters had not would regenerate itself
    ///         back the moment anybody nudged its width.
    ///     </para>
    /// </remarks>
    public static int Mirror(SceneDocument document, Plane plane, bool copy = true) {
        ArgumentNullException.ThrowIfNull(document);

        var world = document.World;
        var normal = plane.Normal.IsZero ? Vector3.UnitX : Vector3.Normalize(plane.Normal);

        List<Entity> targets = [];

        using (document.Stack.BeginTransaction(copy ? "Mirror" : "Mirror In Place")) {
            if (copy) {
                SceneClone.Duplicate(document, document.Selection.Items.ToArray(), Vector3.Zero, targets);
            } else {
                targets.AddRange(document.Selection.Items.Where(world.IsAlive));
            }

            foreach (var entity in targets) {
                Reflect(document, entity, normal, plane.D);
            }

            if (targets.Count > 0) {
                document.Selection.Set(targets);
            }
        }

        return targets.Count;
    }

    /// <summary>Copies an entity along a line, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">What to repeat.</param>
    /// <param name="step">How far apart, in the parent's space.</param>
    /// <param name="count">How many copies, beyond the original.</param>
    /// <param name="into">The copies. Cleared first, or null.</param>
    /// <returns>How many were made.</returns>
    /// <remarks>Doc 24's linear array. The count is copies rather than total, because "give me five
    ///     more columns" is what somebody looking at one column means.</remarks>
    public static int Array(
        SceneDocument document,
        Entity entity,
        Vector3 step,
        int count,
        List<Entity>? into = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        List<Entity> made = into ?? [];

        made.Clear();

        if (count < 1 || !document.World.IsAlive(entity)) {
            return 0;
        }

        using (document.Stack.BeginTransaction("Array")) {
            for (var index = 1; index <= count; index++) {
                var copy = SceneClone.Duplicate(document, entity, offset: step * index);

                if (!copy.IsNull) {
                    made.Add(copy);
                }
            }
        }

        return made.Count;
    }

    /// <summary>Copies an entity round a circle, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">What to repeat.</param>
    /// <param name="centre">What to turn about, in the parent's space.</param>
    /// <param name="axis">Which way the axis of rotation points.</param>
    /// <param name="count">How many copies, beyond the original.</param>
    /// <param name="sweep">How far round to go in radians, or zero for a full circle.</param>
    /// <param name="into">The copies. Cleared first, or null.</param>
    /// <returns>How many were made.</returns>
    /// <remarks>
    ///     ⚠ <b>A full circle divides by the total and a partial one by the gaps.</b> Eight columns
    ///     round a rotunda should be forty-five degrees apart, and eight along a ninety-degree arc
    ///     should have one at each end — which are two different divisions, and getting it wrong is
    ///     the way every radial array tool is wrong the first time.
    /// </remarks>
    public static int Radial(
        SceneDocument document,
        Entity entity,
        Vector3 centre,
        Vector3 axis,
        int count,
        float sweep = 0f,
        List<Entity>? into = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        List<Entity> made = into ?? [];

        made.Clear();

        if (count < 1 || !document.World.IsAlive(entity)) {
            return 0;
        }

        var around = axis.IsZero ? Vector3.UnitY : Vector3.Normalize(axis);
        var full = MathF.Abs(sweep) < 1e-5f;
        var arc = full ? MathF.Tau : sweep;
        var step = arc / (full ? count + 1 : count);

        var world = document.World;
        var local = world.Has<LocalTransform>(entity) ? world.Read<LocalTransform>(entity) : LocalTransform.Identity;

        using (document.Stack.BeginTransaction("Radial Array")) {
            for (var index = 1; index <= count; index++) {
                var turn = Quaternion.FromAxisAngle(around, step * index);
                var placed = centre + Quaternion.Transform(local.Position - centre, turn);

                var copy = SceneClone.Duplicate(document, entity, offset: placed - local.Position);

                if (copy.IsNull) {
                    continue;
                }

                world.Get<LocalTransform>(copy).Rotation = turn * local.Rotation;
                made.Add(copy);
            }
        }

        return made.Count;
    }

    /// <summary>Reflects one entity's geometry and position across a plane.</summary>
    static void Reflect(SceneDocument document, Entity entity, Vector3 normal, float offset) {
        var world = document.World;

        if (document.IsParametric(entity)) {
            document.Stack.Execute(ShapeCommand.Demote(document, entity, "Mirror"));
        }

        if (world.Has<LocalTransform>(entity)) {
            ref var local = ref world.Get<LocalTransform>(entity);

            local.Position -= normal * (2f * (Vector3.Dot(normal, local.Position) + offset));
        }

        if (document.MeshOf(entity) is not { } mesh) {
            return;
        }

        // ⚠ The plane taken into the entity's own space as a *direction*, because the entity has
        // already been moved onto the far side of it — so what is left to do in the mesh is a
        // reflection through its own origin along that direction.
        var axis = normal;

        if (world.Has<WorldTransform>(entity)
            && Matrix4x4.Invert(world.Read<WorldTransform>(entity).Value, out var inverse)) {
            axis = Matrix4x4.TransformDirection(normal, inverse);
        }

        if (axis.IsZero) {
            return;
        }

        axis = Vector3.Normalize(axis);

        var was = new EditMesh(mesh);

        for (var position = 0; position < mesh.PositionCount; position++) {
            var point = mesh.Positions[position];

            mesh.MovePosition(position, point - (axis * (2f * Vector3.Dot(axis, point))));
        }

        // Reflecting the positions turns every face inside out, so the faces are turned back.
        MeshOperations.Flip(mesh);

        document.TouchMesh(entity);
        document.Stack.Execute(EditMeshCommand.Rebuilt(document, entity, was, "Mirror"));
    }

    /// <summary>Twice the signed area of an outline, which is what says which way round it goes.</summary>
    static float Area(ReadOnlySpan<Vector2> outline) {
        var total = 0f;

        for (var point = 0; point < outline.Length; point++) {
            var next = outline[(point + 1) % outline.Length];

            total += (outline[point].X * next.Y) - (next.X * outline[point].Y);
        }

        return total;
    }
}
