// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>The blockout verbs over the scene a mode is editing, at that mode's dials.</summary>
/// <remarks>
///     <para>
///         <b>Moved out of <see cref="BlockoutMode" />, not rewritten (#1307).</b> Every body here was a
///         closure in the mode's <c>Register</c> or one of the four helpers beside it —
///         <c>Created</c>, <c>Pushed</c>, <c>Repeated</c>, <c>Exported</c> — and says what it said
///         there. What changed is who can reach it: the mode's commands call these, the module
///         publishes them as <see cref="IBlockout" />, and a test can call one with no shell at all.
///     </para>
///     <para>
///         ⚠ <b>The guards moved in with the verbs.</b> The mode used to check for a scene, or for an
///         active mesh, before running a closure; a method a plugin calls directly has to check for
///         itself, so each one does, and the mode's commands now call them unconditionally.
///     </para>
///     <para>
///         ⚠ <b>The mode keeps the pointer.</b> Arming the shape tool or the knife, choosing a shape
///         kind and which lattice cell is under the cursor are statements about a gesture in a pane,
///         so they stayed where the pointer handling is; <see cref="CubeGrid" /> takes the hovered
///         cell as an argument for that reason.
///     </para>
/// </remarks>
/// <param name="mode">The mode whose editing state and dials the verbs read.</param>
public sealed class BlockoutVerbs(BlockoutMode mode) : IBlockout {
    readonly BlockoutMode mode = mode ?? throw new ArgumentNullException(nameof(mode));

    /// <summary>The scene the creation verbs act on, or null while the mode drives none.</summary>
    SceneDocument? Scene => mode.Editing?.Document;

    /// <summary>Runs an element verb against the editing state, if a mesh is being edited.</summary>
    bool Run(Func<MeshEdit, bool> verb) =>
        mode.Editing is { IsActive: true } editing && verb(editing);

    /// <inheritdoc />
    public bool SelectLoop() => Run(editing => BlockoutSelection.Loop(editing));

    /// <inheritdoc />
    public bool SelectRing() => Run(editing => BlockoutSelection.Ring(editing));

    /// <inheritdoc />
    public bool Grow() => Run(BlockoutSelection.Grow);

    /// <inheritdoc />
    public bool Shrink() => Run(BlockoutSelection.Shrink);

    /// <inheritdoc />
    public bool SelectGroup() => Run(editing => BlockoutSelection.Group(editing));

    /// <inheritdoc />
    public bool SelectCoplanar() => Run(editing => BlockoutSelection.Coplanar(editing));

    /// <inheritdoc />
    public bool SelectLinked() => Run(editing => BlockoutSelection.Linked(editing));

    /// <inheritdoc />
    public bool SelectAll() => Run(BlockoutSelection.All);

    /// <inheritdoc />
    public bool SelectNone() => Run(BlockoutSelection.None);

    /// <inheritdoc />
    public bool Invert() => Run(BlockoutSelection.Invert);

    /// <inheritdoc />
    public bool Extrude(bool individually = false) =>
        Run(editing => BlockoutGeometry.Extrude(editing, mode.Step, individually));

    /// <inheritdoc />
    public bool Inset(bool individually = false) =>
        Run(editing => BlockoutGeometry.Inset(editing, mode.Step * 0.25f, individually));

    /// <inheritdoc />
    public bool Bevel() => Run(editing => BlockoutGeometry.Bevel(editing, mode.Step * 0.25f, mode.BevelSegments, out _));

    /// <inheritdoc />
    public bool LoopCut() => Run(editing => BlockoutGeometry.LoopCut(editing));

    /// <inheritdoc />
    public bool Subdivide() => Run(editing => BlockoutGeometry.Subdivide(editing));

    /// <inheritdoc />
    public bool Bridge() => Run(BlockoutGeometry.Bridge);

    /// <inheritdoc />
    public bool FillHole() => Run(BlockoutGeometry.FillHole);

    /// <inheritdoc />
    public bool Flip() => Run(BlockoutGeometry.Flip);

    /// <inheritdoc />
    public bool Weld() => Run(editing => BlockoutGeometry.Weld(editing));

    /// <inheritdoc />
    public bool Dissolve() => Run(BlockoutGeometry.Dissolve);

    /// <inheritdoc />
    public bool Delete() => Run(BlockoutGeometry.Delete);

    /// <inheritdoc />
    public bool Detach() => Run(editing => BlockoutGeometry.Detach(editing) is not null);

    /// <inheritdoc />
    public bool Project(UvProjection projection) =>
        Run(editing => BlockoutSurfaces.Project(editing, projection, mode.UvScale));

    /// <inheritdoc />
    public bool FitUvs() => Run(BlockoutSurfaces.Fit);

    /// <inheritdoc />
    public bool Smooth(bool smooth = true) => Run(editing => BlockoutSurfaces.Smooth(editing, smooth));

    /// <inheritdoc />
    public bool AutoSmooth() => Run(editing => BlockoutSurfaces.AutoSmooth(editing));

    /// <inheritdoc />
    public bool NewGroup() => Run(BlockoutSurfaces.Regroup);

    /// <inheritdoc />
    public Entity CreateShape() =>
        Scene is { } scene ? Created(scene, BlockoutCreate.Shape(scene, mode.Shape, Where())) : Entity.Null;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The hover wins, and the fallback is what the verb used to do unconditionally.</b> A key
    ///     pressed with the pointer outside the pane — or in a test, or from the palette — still has to
    ///     mean something, and the plane's origin is the answer every other menu-run creation verb
    ///     gives. What changes is that with the pointer on the plane the box lands where the preview
    ///     said it would.
    /// </remarks>
    public Entity CubeGrid(GridBox? at = null) {
        if (Scene is not { } scene) {
            return Entity.Null;
        }

        if (at is not { } box) {
            var cell = BlockoutCubeGrid.CellOf(Where(), mode.Plane);

            box = GridBox.At(cell.X, cell.Y, cell.Z);
        }

        return Created(scene, BlockoutCubeGrid.Create(scene, box, mode.Plane));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One axis and one side from the keyboard, which is the honest shape of a keyboard
    ///     verb.</b> Unreal's cube grid pushes whichever face the pointer is over; picking that face is
    ///     a pick against the <i>box</i>, and what <see cref="BlockoutMode.HoverCell" /> answers is a
    ///     pick against the work <i>plane</i> — so the candidate-cell preview that arrived with doc 24
    ///     § P4 does not settle this one. The keys still push upwards, which is the direction a
    ///     block-out grows nine times in ten, and the other five sides are a drag of the gizmo.
    /// </remarks>
    public bool Push(int cells) {
        if (Scene is not { } scene) {
            return false;
        }

        var pushed = false;

        foreach (var entity in scene.Selection.Items.ToArray()) {
            pushed |= BlockoutCubeGrid.Push(scene, entity, axis: 1, positive: true, cells, mode.Plane);
        }

        return pushed;
    }

    /// <inheritdoc />
    public int Duplicate() => Scene is { } scene ? BlockoutCreate.Duplicate(scene, Vector3.Zero) : 0;

    /// <inheritdoc />
    public int Mirror() =>
        Scene is { } scene ? BlockoutCreate.Mirror(scene, (mode.Plane ?? BlockoutMode.Ground).AsPlane()) : 0;

    /// <inheritdoc />
    public int Repeat(bool radial) {
        if (Scene is not { } scene || scene.Selection.Count == 0) {
            return 0;
        }

        var entity = scene.Selection.Items[0];
        var step = mode.Step;

        return radial
            ? BlockoutCreate.Radial(scene, entity, Where(), mode.Plane?.Normal ?? Vector3.UnitY, mode.Copies)
            : BlockoutCreate.Array(scene, entity, new(step, 0f, 0f), mode.Copies);
    }

    /// <inheritdoc />
    public Entity Union() => Scene is { } scene ? BlockoutBoolean.Union(scene) : Entity.Null;

    /// <inheritdoc />
    public Entity Subtract() => Scene is { } scene ? BlockoutBoolean.Subtract(scene) : Entity.Null;

    /// <inheritdoc />
    public Entity Intersect() => Scene is { } scene ? BlockoutBoolean.Intersect(scene) : Entity.Null;

    /// <inheritdoc />
    public bool ApplyBoolean() => Scene is { } scene && BlockoutBoolean.Collapse(scene);

    /// <inheritdoc />
    public int PlaneCut() =>
        Scene is { } scene ? BlockoutBoolean.PlaneCut(scene, (mode.Plane ?? BlockoutMode.Ground).AsPlane()) : 0;

    /// <inheritdoc />
    public bool Trim() => Scene is { } scene && BlockoutBoolean.Trim(scene);

    /// <inheritdoc />
    public int Retopologize() =>
        Scene is { } scene ? BlockoutRetopology.Run(scene, mode.Retopology.ToRemeshSettings()) : 0;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ docs/plan/41 § R7's debug overlays, and the settings it captures with are the <i>same</i>
    ///     ones Retopologize runs with. A dump taken at other numbers would be a picture of a remesh that
    ///     is not the one the next click is going to produce, which is worse than no picture.
    /// </remarks>
    public bool ToggleRemeshDebug() {
        if (Scene is not { } scene) {
            return mode.RemeshDebug.Dump is not null;
        }

        if (mode.RemeshDebug.Dump is not null) {
            mode.RemeshDebug.Clear();
        } else {
            mode.RemeshDebug.Capture(scene, mode.Retopology.ToRemeshSettings());
        }

        return mode.RemeshDebug.Dump is not null;
    }

    /// <inheritdoc />
    public int Bake() => Scene is { } scene && mode.Baker is { } baker ? BlockoutHandoff.Bake(scene, baker) : 0;

    /// <inheritdoc />
    public int MakeEditable() =>
        Scene is { } scene && mode.Meshes is { } source ? BlockoutHandoff.Editable(scene, source) : 0;

    /// <inheritdoc />
    public bool Export(string extension) {
        if (Scene is not { } scene || mode.Export is not { } write) {
            return false;
        }

        var text = BlockoutHandoff.Export(scene, extension);

        if (text.Length == 0) {
            return false;
        }

        write(text, extension);
        return true;
    }

    /// <summary>Where a menu-run creation verb puts what it makes: the work plane's origin.</summary>
    /// <remarks>⚠ <b>Not the world origin and not the camera.</b> The work plane is where the designer
    ///     said they are building — D5's whole argument — so a shape made from a menu lands there and
    ///     a shape made from a drag lands where the drag was.</remarks>
    Vector3 Where() => mode.Plane?.Origin ?? Vector3.Zero;

    static Entity Created(SceneDocument scene, Entity entity) {
        if (!entity.IsNull) {
            scene.Selection.Set(entity);
        }

        return entity;
    }
}
