// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>Which half of the shape tool's gesture is in flight.</summary>
public enum ShapeStage : byte {
    /// <summary>Nothing. The next press starts a footprint.</summary>
    Idle,

    /// <summary>A corner is down and the pointer is dragging the other one across the work plane.</summary>
    Footprint,

    /// <summary>The footprint is settled and the pointer is now setting the height.</summary>
    Height
}

/// <summary>Doc 24's shape tool, as a two-stage gesture over the work plane.</summary>
/// <remarks>
///     <para>
///         <b>"Drag a footprint on the work plane, then drag the height", which is the table's own
///         description and is the gesture every reference toolset uses.</b> Press and drag gives two
///         corners on the plane; release settles them; moving the pointer then sets the height without
///         a button held; a click commits. The shape that arrives has live parameters, so the numbers
///         a designer dragged are still numbers afterwards.
///     </para>
///     <para>
///         ⚠ <b>World points in, entity out, and no viewport anywhere in the type.</b> Turning a
///         pointer into a point on a plane is the pane's job and turning two points into a shape is
///         this one's, and keeping the seam there is what makes the whole gesture a unit test — "drag
///         from here to there and then up by three" is three method calls and an assertion about a
///         mesh, which is exactly the bargain doc 24's testing table asks for.
///     </para>
///     <para>
///         ⚠ <b>The second stage has no button held, and that is not an oversight.</b> A press-drag
///         for the footprint and a second press-drag for the height would need the designer to find
///         the object again between the two; every tool that does this — Unreal's, ProBuilder's,
///         Blender's box-add — moves the pointer freely for the height and takes a click as the
///         commit. <see cref="Cancel" /> is what <c>Escape</c> calls, and it removes the entity rather
///         than leaving a flat one behind.
///     </para>
///     <para>
///         ⚠ <b>The entity exists from the moment the footprint does.</b> Drawing a preview and then
///         creating something at the end would be two representations of one shape and a preview that
///         can disagree with the result; making the real thing early means what is on screen while
///         dragging <i>is</i> what will be there, at the price of one create in the history that a
///         cancel takes away again.
///     </para>
/// </remarks>
public sealed class ShapeDrag {
    readonly SceneDocument document;

    Vector3 anchor;
    Vector3 opposite;
    float height;

    /// <summary>Drags shapes into one scene.</summary>
    /// <param name="document">The scene.</param>
    public ShapeDrag(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
    }

    /// <summary>Which half of the gesture is in flight.</summary>
    public ShapeStage Stage { get; private set; }

    /// <summary>Which shape the next drag makes.</summary>
    public ShapeKind Kind { get; set; } = ShapeKind.Box;

    /// <summary>The entity being dragged, or <see cref="Entity.Null" /> between gestures.</summary>
    public Entity Entity { get; private set; }

    /// <summary>The plane the footprint is dragged on, or null for the ground.</summary>
    public WorkPlane? Plane { get; set; }

    /// <summary>How tall a shape is before the height stage has moved, in world units.</summary>
    /// <remarks>Not zero, because a shape with no height is one with no faces to see and no handle to
    ///     grab — so the moment the footprint settles there is already something on screen.</remarks>
    public float MinimumHeight { get; set; } = 0.1f;

    /// <summary>Starts a footprint at a point on the work plane.</summary>
    /// <param name="corner">Where, in world space.</param>
    public void Begin(Vector3 corner) {
        Cancel();

        anchor = corner;
        opposite = corner;
        height = MinimumHeight;

        Stage = ShapeStage.Footprint;
    }

    /// <summary>Drags the footprint's other corner.</summary>
    /// <param name="corner">Where, in world space.</param>
    /// <returns>Whether the shape changed.</returns>
    public bool Drag(Vector3 corner) {
        if (Stage != ShapeStage.Footprint) {
            return false;
        }

        opposite = corner;
        return Apply();
    }

    /// <summary>Settles the footprint and moves on to the height.</summary>
    /// <returns>Whether there is a shape to raise.</returns>
    /// <remarks>
    ///     ⚠ <b>A press and release with no drag between them is not a shape.</b> That gesture is a
    ///     click, which in the viewport means "select what is under the pointer" — so a tool that
    ///     turned it into a shape of no size would make it impossible to click anything while the tool
    ///     was armed.
    /// </remarks>
    public bool Settle() {
        if (Stage != ShapeStage.Footprint) {
            return false;
        }

        if (Entity.IsNull) {
            Cancel();
            return false;
        }

        Stage = ShapeStage.Height;
        return true;
    }

    /// <summary>Sets how tall the shape is.</summary>
    /// <param name="raised">How far above the work plane, in world units.</param>
    /// <returns>Whether the shape changed.</returns>
    public bool Raise(float raised) {
        if (Stage != ShapeStage.Height) {
            return false;
        }

        height = MathF.Max(MathF.Abs(raised), MinimumHeight);
        return Apply();
    }

    /// <summary>Finishes the gesture and leaves the shape where it is.</summary>
    /// <returns>The entity, or <see cref="Entity.Null" /> if there was no gesture.</returns>
    public Entity Commit() {
        var made = Entity;

        Entity = Entity.Null;
        Stage = ShapeStage.Idle;

        if (!made.IsNull) {
            document.Selection.Set(made);
        }

        return made;
    }

    /// <summary>Abandons the gesture and takes the entity away again.</summary>
    /// <returns>Whether there was one to take away.</returns>
    public bool Cancel() {
        var made = Entity;

        Entity = Entity.Null;
        Stage = ShapeStage.Idle;

        if (made.IsNull) {
            return false;
        }

        // The create and every parameter change since it are on the stack, so undoing them is what
        // "cancel" has to mean — a delete would leave the history claiming a shape was made.
        while (document.Stack.CanUndo.Value && document.World.IsAlive(made)) {
            document.Stack.Undo();
        }

        return true;
    }

    /// <summary>The parameters the gesture has arrived at so far.</summary>
    /// <returns>The shape.</returns>
    public ShapeParameters Parameters() {
        var plane = Plane;

        var start = plane?.ToLocal(anchor) ?? anchor;
        var end = plane?.ToLocal(opposite) ?? opposite;

        var size = new Vector3(MathF.Abs(end.X - start.X), height, MathF.Abs(end.Z - start.Z));

        return ShapeParameters.Default(Kind) with { Size = size };
    }

    /// <summary>Where the shape's origin is, which is the middle of the footprint on the plane.</summary>
    /// <returns>The point, in world space.</returns>
    public Vector3 Origin() {
        var plane = Plane;

        var start = plane?.ToLocal(anchor) ?? anchor;
        var end = plane?.ToLocal(opposite) ?? opposite;

        var centre = new Vector3((start.X + end.X) * 0.5f, MathF.Min(start.Y, end.Y), (start.Z + end.Z) * 0.5f);

        return plane?.ToWorld(centre) ?? centre;
    }

    bool Apply() {
        var parameters = Parameters();

        if (parameters.Size.X < Smallest || parameters.Size.Z < Smallest) {
            return false;
        }

        if (Entity.IsNull) {
            Entity = BlockoutCreate.Shape(document, parameters, Origin());
            document.Selection.Set(Entity);

            return true;
        }

        var world = document.World;

        if (world.Has<Vixen.Engine.Transforms.LocalTransform>(Entity)) {
            world.Get<Vixen.Engine.Transforms.LocalTransform>(Entity).Position = Origin();
        }

        return BlockoutCreate.Resize(document, Entity, parameters);
    }

    /// <summary>The smallest footprint that counts as a drag rather than as a click.</summary>
    const float Smallest = 1e-3f;
}
