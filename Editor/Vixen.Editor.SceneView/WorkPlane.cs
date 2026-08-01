// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>The plane everything is placed, dragged and snapped in: an origin, a rotation and a step.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D5, and it is CubeGrid's repositionable grid and Blender's 3D cursor in one
///         object.</b> The floor grid is a <i>view</i> of this rather than a thing of its own, which
///         is what makes the three commonest blockout gestures possible at all: put the grid on a
///         wall and everything afterwards is in the wall's plane; double and halve the step to build
///         at four metres, then one, then a quarter; offset along the normal to build the second floor
///         without doing arithmetic.
///     </para>
///     <para>
///         ⚠ <b>The rotation takes +Y onto the plane's normal, which is the same convention
///         <c>ScenePlacement.Upright</c> uses.</b> A plane is a floor whichever way it is facing —
///         its local X and Z are the two directions you build along, and its local Y is out of it —
///         so the ground plane is the identity and nothing about the default changes.
///     </para>
///     <para>
///         ⚠ <b><see cref="Step" /> is nullable and null is not "one".</b> Null means the grid picks
///         its own spacing from how far away the camera is, which is what it has always done and what
///         is right until somebody says otherwise. A number means the designer chose it — with
///         <c>]</c> and <c>[</c> — and then the grid they can see and the grid they snap to are
///         literally the same number, which is doc 24's D5's complaint about editors where they are
///         two.
///     </para>
///     <para>
///         ⚠ <b>Doubling and halving from wherever it is, rather than a fixed ladder.</b> Every level
///         is then a sub-lattice of the last, so a 0.25 m object is still on the 4 m grid's lines. A
///         step of a third would never be on one again, which is why the verb is a factor of two and
///         not a text field.
///     </para>
/// </remarks>
public sealed class WorkPlane {
    /// <summary>The smallest step the halve verb will produce, in world units.</summary>
    /// <remarks>
    ///     A tenth of a millimetre. Not a limit anybody meets while building; a floor, because halving
    ///     without one reaches denormals and the grid becomes a solid sheet nothing can be aimed at.
    /// </remarks>
    public const float MinimumStep = 1e-4f;

    /// <summary>And the largest, so the grid cannot be doubled out of the world.</summary>
    public const float MaximumStep = 1e6f;

    /// <summary>Where the plane's own origin is, in world space.</summary>
    public Vector3 Origin { get; set; }

    /// <summary>How it is turned: the rotation taking +Y onto its normal.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>The step the designer chose, or <see langword="null" /> to let the grid choose.</summary>
    public float? Step { get; set; }

    /// <summary>Which way it faces, in world space.</summary>
    public Vector3 Normal => Quaternion.Transform(Vector3.UnitY, Rotation);

    /// <summary>Whether it is still the ground plane through the world origin.</summary>
    /// <remarks>What a "Work Plane to World" command greys itself out on, and what a status readout
    ///     stays quiet about.</remarks>
    public bool IsGround =>
        Origin.IsZero && Step is null && Vector3.NearEqual(Normal, Vector3.UnitY, 1e-5f);

    /// <summary>Raised when anything about it changes.</summary>
    /// <remarks>What a viewport redraws its grid from, and what a readout listens to.</remarks>
    public event Action<WorkPlane>? Changed;

    /// <summary>Puts the plane on a surface.</summary>
    /// <param name="point">A point on it, in world space.</param>
    /// <param name="normal">Which way that surface faces.</param>
    /// <remarks>
    ///     ⚠ <b>The step survives.</b> Moving the grid onto a wall is not a statement about how big the
    ///     squares should be, and re-choosing it after every move is the thing that makes a
    ///     repositionable grid tiring to use.
    /// </remarks>
    public void SetTo(Vector3 point, Vector3 normal) {
        var up = Vector3.Normalize(normal);

        Origin = point;
        Rotation = up.IsZero ? Quaternion.Identity : Quaternion.FromToRotation(Vector3.UnitY, up);

        Changed?.Invoke(this);
    }

    /// <summary>Moves the plane along its own normal.</summary>
    /// <param name="distance">How far, in world units. Negative goes the other way.</param>
    /// <remarks>Building the second floor at three metres without doing arithmetic — D5's third
    ///     gesture, and the reason the offset is along the <i>normal</i> rather than along world Y.</remarks>
    public void Offset(float distance) {
        if (distance == 0f) {
            return;
        }

        Origin += Normal * distance;
        Changed?.Invoke(this);
    }

    /// <summary>Doubles the step, from whatever it is now.</summary>
    /// <param name="current">What the grid is drawing at, for the first press when nothing is chosen.</param>
    /// <returns>The step now in force.</returns>
    /// <remarks>
    ///     ⚠ <b>The first press has to be told what the grid was doing.</b> Until somebody chooses a
    ///     step there is not one — the spacing is a function of the camera — so doubling "the step"
    ///     with nothing chosen would either invent a number or double a one that is not what is on
    ///     screen. The caller reads <c>SceneGrid.Spacing</c> and passes it.
    /// </remarks>
    public float Coarsen(float current) => Chosen(Effective(current) * 2f);

    /// <summary>Halves it.</summary>
    /// <param name="current">Ditto.</param>
    /// <returns>The step now in force.</returns>
    public float Refine(float current) => Chosen(Effective(current) * 0.5f);

    /// <summary>Gives the spacing back to the camera.</summary>
    public void Auto() {
        if (Step is null) {
            return;
        }

        Step = null;
        Changed?.Invoke(this);
    }

    /// <summary>Puts it back on the ground through the world origin, with an automatic step.</summary>
    public void Reset() {
        Origin = Vector3.Zero;
        Rotation = Quaternion.Identity;
        Step = null;

        Changed?.Invoke(this);
    }

    /// <summary>The step in force, given what the grid would otherwise draw.</summary>
    /// <param name="current">The grid's own choice.</param>
    /// <returns>The chosen step, or <paramref name="current" />.</returns>
    public float Effective(float current) => Step ?? current;

    /// <summary>Takes a point from the plane's own space into the world.</summary>
    /// <param name="local">The point, with its Y out of the plane.</param>
    /// <returns>The world point.</returns>
    public Vector3 ToWorld(Vector3 local) => Origin + Quaternion.Transform(local, Rotation);

    /// <summary>And back.</summary>
    /// <param name="world">The world point.</param>
    /// <returns>The point in the plane's space, whose Y is its distance out of the plane.</returns>
    public Vector3 ToLocal(Vector3 world) =>
        Quaternion.Transform(world - Origin, Quaternion.Inverse(Rotation));

    /// <summary>The nearest point on the plane to a world point.</summary>
    /// <param name="world">The point.</param>
    /// <returns>The point on the plane.</returns>
    public Vector3 Project(Vector3 world) {
        var local = ToLocal(world);

        return ToWorld(new Vector3(local.X, 0f, local.Z));
    }

    /// <summary>The plane itself, for a ray test.</summary>
    /// <returns>The plane, facing along <see cref="Normal" />.</returns>
    public Plane AsPlane() {
        var normal = Normal;

        return new Plane(normal, -Vector3.Dot(normal, Origin));
    }

    float Chosen(float step) {
        Step = Math.Clamp(step, MinimumStep, MaximumStep);
        Changed?.Invoke(this);

        return Step.Value;
    }
}
