// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>A box measured in whole grid cells, which is what the cube-grid tool selects.</summary>
/// <param name="X">Its near corner along the work plane's first axis, in cells.</param>
/// <param name="Y">Ditto out of the plane.</param>
/// <param name="Z">Ditto along its second axis.</param>
/// <param name="Width">How many cells across.</param>
/// <param name="Height">How many up.</param>
/// <param name="Depth">How many along.</param>
/// <remarks>
///     <para>
///         <b>Doc 24 files the cube grid as "its own tool because it has its own selection model", and
///         this is that model.</b> Everything else in the toolset selects vertices, edges and faces;
///         this selects a <i>region of the lattice</i>, in integers, and the only thing you can do to
///         it is grow it, shrink it and push its faces — which is why it cannot be expressed as an
///         element selection and why it needs a type.
///     </para>
///     <para>
///         ⚠ <b>Integers, and that is the whole point rather than an implementation choice.</b> A box
///         whose extents are cell counts cannot drift off the grid, cannot be a quarter of a cell wide
///         after nine pushes, and lines up with the box beside it exactly — which is what makes a room
///         built out of these join up without anybody snapping anything. Floating-point extents that
///         happen to be rounded are the version of this tool that everybody has used and nobody
///         trusts.
///     </para>
/// </remarks>
public readonly record struct GridBox(int X, int Y, int Z, int Width, int Height, int Depth) {
    /// <summary>A one-cell box at a cell.</summary>
    /// <param name="x">Its cell along the plane's first axis.</param>
    /// <param name="y">Ditto out of the plane.</param>
    /// <param name="z">Ditto along its second.</param>
    /// <returns>The box.</returns>
    public static GridBox At(int x, int y, int z) => new(x, y, z, 1, 1, 1);

    /// <summary>Whether it encloses anything at all.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0 || Depth <= 0;

    /// <summary>The same box with an extent brought back to at least one cell on every axis.</summary>
    /// <returns>The box.</returns>
    /// <remarks>
    ///     ⚠ <b>Clamped rather than allowed to invert.</b> Pushing a face of a one-cell box inwards
    ///     twice would otherwise give a box of negative width, whose mesh is inside out and whose next
    ///     push makes it worse — where what the designer meant was "it will not get any smaller".
    /// </remarks>
    public GridBox Sound() => this with { Width = Math.Max(Width, 1), Height = Math.Max(Height, 1), Depth = Math.Max(Depth, 1) };

    /// <summary>The box grown or shrunk on one side, in cells.</summary>
    /// <param name="axis">0 for the plane's first axis, 1 for out of it, 2 for its second.</param>
    /// <param name="positive">Which of the two sides moves.</param>
    /// <param name="cells">How many cells, negative to pull the side inwards.</param>
    /// <returns>The box.</returns>
    /// <remarks>
    ///     <b>The cube grid's whole verb.</b> Pushing the far side out grows the box; pushing the near
    ///     side out moves its origin as well as its extent, which is what makes "pull this wall
    ///     towards me" work rather than making the box grow the other way.
    /// </remarks>
    public GridBox Push(int axis, bool positive, int cells) {
        if (positive) {
            return axis switch {
                0 => this with { Width = Width + cells },
                1 => this with { Height = Height + cells },
                _ => this with { Depth = Depth + cells }
            };
        }

        return axis switch {
            0 => this with { X = X - cells, Width = Width + cells },
            1 => this with { Y = Y - cells, Height = Height + cells },
            _ => this with { Z = Z - cells, Depth = Depth + cells }
        };
    }
}

/// <summary>Doc 24's cube-grid tool: boxes on the lattice, pushed a cell at a time.</summary>
/// <remarks>
///     <para>
///         <b>What Unreal's CubeGrid is for, and it is the fastest way to block out a building that
///         anybody has found.</b> A box on the grid, pushed and pulled by whole cells, at a step you
///         double and halve as you go from the shape of the level to the shape of a room to the shape
///         of a doorway. The step is <c>WorkPlane.Step</c>, so the grid you can see, the grid you snap
///         to and the grid this counts in are one number — D5's complaint about editors where they are
///         two or three.
///     </para>
///     <para>
///         ⚠ <b>It makes ordinary parametric boxes rather than a kind of its own.</b> A cube-grid box
///         is a <see cref="ShapeKind.Box" /> whose size happens to be a whole number of cells and whose
///         origin happens to be on a cell corner — so everything else in the toolset works on it
///         unchanged, it inspects like any other shape, and a designer who wants one that is not on the
///         grid any more just drags it. A shape kind of its own would have bought a badge in the
///         inspector and cost every other verb a special case.
///     </para>
///     <para>
///         ⚠ <b>Corner mode is <see cref="Corner" /> and it is where the tool stops being parametric.</b>
///         Pulling one corner of a box down a cell is what makes a ramp, a wedge or a buttress out of a
///         box, and it is not a box any more — so it demotes, exactly as any other edit to a face does.
///         Doc 24 asks for corner mode by name and this is the honest shape of it: the cells stay the
///         unit of movement, and what moves is geometry rather than a parameter.
///     </para>
///     <para>
///         ⚠ <b>What is not here is the hover preview.</b> Unreal draws the candidate cell under the
///         pointer before you commit to it, which is most of what makes the tool feel like a tool
///         rather than like a dialog — and it is a drawing job on <c>SceneLines</c>' overlay rather than
///         a modelling one. It is called out in <c>docs/plan/24</c> as owed rather than quietly
///         dropped.
///     </para>
/// </remarks>
public static class BlockoutCubeGrid {
    /// <summary>The step the grid counts in when nothing has chosen one.</summary>
    /// <remarks>A metre, which is <c>SceneGrid</c>'s own default and the size of the squares the
    ///     block-out checker draws — see <c>SceneMeshes.Checker</c>.</remarks>
    public const float DefaultStep = 1f;

    /// <summary>Creates a box covering a region of the lattice, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="box">Which cells.</param>
    /// <param name="plane">The work plane the lattice is on, or null for the ground.</param>
    /// <returns>The entity, or <see cref="Entity.Null" /> for an empty region.</returns>
    public static Entity Create(SceneDocument document, GridBox box, WorkPlane? plane = null) {
        ArgumentNullException.ThrowIfNull(document);

        var sound = box.Sound();

        if (sound.IsEmpty) {
            return Entity.Null;
        }

        var step = Step(plane);

        var parameters = new ShapeParameters {
            Kind = ShapeKind.Box,
            Size = new(sound.Width * step, sound.Height * step, sound.Depth * step),
            Sides = 16,
            Steps = 1
        };

        // ⚠ The entity's origin is the middle of the region's footprint and its *floor*, because that
        // is where a `MeshShapes` box has its own origin — so the box on the grid and the box in the
        // world are the same box, and a designer who reads the transform sees a number that is on the
        // grid rather than half a cell off it.
        var centre = new Vector3(
            (sound.X + (sound.Width * 0.5f)) * step,
            sound.Y * step,
            (sound.Z + (sound.Depth * 0.5f)) * step
        );

        return BlockoutCreate.Shape(document, parameters, plane?.ToWorld(centre) ?? centre);
    }

    /// <summary>Which cells a box entity covers, if it is still on the lattice.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="plane">The work plane, or null for the ground.</param>
    /// <param name="box">The region.</param>
    /// <returns>Whether it is a parametric box whose size is a whole number of cells.</returns>
    /// <remarks>
    ///     ⚠ <b>Rounded rather than required to be exact, and the tolerance is a twentieth of a
    ///     cell.</b> A box built at a four-metre step and then looked at through a one-metre one is
    ///     exactly on the lattice; a box somebody has nudged by a centimetre is not, and refusing it
    ///     would make the tool stop working for a reason nobody can see. What is refused is a box that
    ///     is genuinely between cells, because pushing one of those would move it onto the grid without
    ///     being asked.
    /// </remarks>
    public static bool TryRead(SceneDocument document, Entity entity, WorkPlane? plane, out GridBox box) {
        ArgumentNullException.ThrowIfNull(document);

        box = default;

        if (document.ShapeOf(entity) is not { Kind: ShapeKind.Box } shape) {
            return false;
        }

        var world = document.World;

        if (!world.Has<Vixen.Engine.Transforms.LocalTransform>(entity)) {
            return false;
        }

        var step = Step(plane);
        var at = world.Read<Vixen.Engine.Transforms.LocalTransform>(entity).Position;
        var local = plane?.ToLocal(at) ?? at;

        var width = shape.Size.X / step;
        var height = shape.Size.Y / step;
        var depth = shape.Size.Z / step;

        if (!Whole(width, out var cellsX) || !Whole(height, out var cellsY) || !Whole(depth, out var cellsZ)) {
            return false;
        }

        if (!Whole((local.X / step) - (cellsX * 0.5f), out var x)
            || !Whole(local.Y / step, out var y)
            || !Whole((local.Z / step) - (cellsZ * 0.5f), out var z)) {
            return false;
        }

        box = new(x, y, z, cellsX, cellsY, cellsZ);
        return true;
    }

    /// <summary>Pushes one side of a box entity out or in by whole cells, undoably.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="axis">0 for the plane's first axis, 1 for out of it, 2 for its second.</param>
    /// <param name="positive">Which of the two sides moves.</param>
    /// <param name="cells">How many cells, negative to pull the side inwards.</param>
    /// <param name="plane">The work plane, or null for the ground.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     ⚠ <b>Still parametric afterwards, which is what separates this from an extrude.</b> Pushing
    ///     a face of a cube-grid box is a change to its size and its origin, both of which the shape
    ///     already has — so the box stays live, its width is still a number in the inspector, and the
    ///     push is one merged undo entry per gesture rather than one topology change per cell.
    /// </remarks>
    public static bool Push(
        SceneDocument document,
        Entity entity,
        int axis,
        bool positive,
        int cells = 1,
        WorkPlane? plane = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        if (cells == 0 || !TryRead(document, entity, plane, out var box)) {
            return false;
        }

        var pushed = box.Push(axis, positive, cells).Sound();

        if (pushed == box) {
            return false;
        }

        var step = Step(plane);
        var world = document.World;

        var parameters = document.ShapeOf(entity)!.Value with {
            Size = new(pushed.Width * step, pushed.Height * step, pushed.Depth * step)
        };

        var centre = new Vector3(
            (pushed.X + (pushed.Width * 0.5f)) * step,
            pushed.Y * step,
            (pushed.Z + (pushed.Depth * 0.5f)) * step
        );

        using (document.Stack.BeginTransaction("Push Cells")) {
            world.Get<Vixen.Engine.Transforms.LocalTransform>(entity).Position = plane?.ToWorld(centre) ?? centre;
            BlockoutCreate.Resize(document, entity, parameters);
        }

        return true;
    }

    /// <summary>Moves the corners the selection covers by whole cells, undoably.</summary>
    /// <param name="editing">What is being edited.</param>
    /// <param name="offset">Which way and how far, in cells, in the work plane's space.</param>
    /// <param name="plane">The work plane, or null for the ground.</param>
    /// <returns>Whether anything moved.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 24's corner mode, and it is what turns a box into a ramp without leaving the
    ///         tool.</b> Select a corner — or an edge, or a face — and move it a cell; the result is a
    ///         wedge whose slope is exactly one cell in one cell, which is the thing a designer is
    ///         actually trying to build and the thing a free drag never quite gives them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the point at which a cube-grid box stops being one.</b> A box with one
    ///         corner pulled down is not a box's three extents any more, so it demotes — see
    ///         <c>MeshEdit.Demote</c>, which is where the confirmation is asked. Everything about the
    ///         cell quantisation survives; only the parameters go.
    ///     </para>
    /// </remarks>
    public static bool Corner(MeshEdit editing, Vector3 offset, WorkPlane? plane = null) {
        ArgumentNullException.ThrowIfNull(editing);

        if (!editing.IsActive || editing.Mesh is not { } mesh || editing.Selection.IsEmpty || !editing.Demote()) {
            return false;
        }

        List<int> positions = [];

        editing.Positions(positions);

        if (positions.Count == 0) {
            return false;
        }

        var step = Step(plane);
        var world = Quaternion.Transform(offset * step, plane?.Rotation ?? Quaternion.Identity);
        var local = BlockoutGeometry.Local(editing, world);

        var was = new EditMesh(mesh);

        foreach (var position in positions) {
            mesh.MovePosition(position, mesh.Positions[position] + local);
        }

        var document = editing.Document;

        document.TouchMesh(editing.Target);
        document.Stack.Execute(EditMeshCommand.Rebuilt(document, editing.Target, was, "Move Corners"));

        editing.Reconcile();
        return true;
    }

    /// <summary>Which cell a world point is in.</summary>
    /// <param name="point">The point, in world space.</param>
    /// <param name="plane">The work plane, or null for the ground.</param>
    /// <returns>The cell, floored — so a point anywhere inside a cell names that cell.</returns>
    public static (int X, int Y, int Z) CellOf(Vector3 point, WorkPlane? plane = null) {
        var step = Step(plane);
        var local = plane?.ToLocal(point) ?? point;

        return (
            (int) MathF.Floor(local.X / step),
            (int) MathF.Floor(local.Y / step),
            (int) MathF.Floor(local.Z / step)
        );
    }

    static float Step(WorkPlane? plane) {
        var step = plane?.Step ?? DefaultStep;

        return step > WorkPlane.MinimumStep ? step : DefaultStep;
    }

    static bool Whole(float value, out int cells) {
        cells = (int) MathF.Round(value);

        return MathF.Abs(value - cells) < 0.05f;
    }
}
