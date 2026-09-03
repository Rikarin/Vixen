// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Physics.Bodies;
using Vixen.Physics.Constraints;
using Vixen.Physics.Interop;
using JoltMathUtil = JoltPhysicsSharp.MathUtil;
using Quaternion = Vixen.Core.Mathematics.Quaternion;
using Vector3 = Vixen.Core.Mathematics.Vector3;

namespace Vixen.Physics;

public sealed partial class PhysicsWorld {
    BodyHandle worldAnchor = BodyHandle.None;
    Shape? worldAnchorShape;

    /// <summary>Creates a constraint between two bodies and adds it to the simulation.</summary>
    /// <param name="description">What the joint is.</param>
    /// <returns>Its handle.</returns>
    /// <remarks>
    ///     Both bodies must already be where they belong: anchors are given in world space and Jolt
    ///     converts them to body-local at this moment, so a constraint created before its bodies are
    ///     positioned records the offset between where they were and where they should have been, and
    ///     then holds it for ever.
    /// </remarks>
    public ConstraintHandle CreateConstraint(in ConstraintDescription description) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var first = Id(description.First);
        var anchorBody = description.Second.IsNone ? WorldAnchor() : description.Second;
        var second = Id(anchorBody);

        // Before the lock, and that is not an ordering preference. Reading a body's transform goes
        // through the *locking* body interface, so doing it inside the write lock below is this
        // thread waiting on a mutex it already holds — a hang with no stack in it, which is exactly
        // what it was before this line moved.
        var localFirstAnchor = ToLocal(description.FirstAnchor, description.First);
        var localSecondAnchor = ToLocal(description.SecondAnchor, anchorBody);
        var localAxis = ToLocalDirection(description.Axis, description.First);

        Constraint native;

        // One multi-lock rather than two single ones: it takes them in a canonical order, so two
        // threads building constraints over an overlapping pair cannot deadlock against each other.
        // Nothing in the engine does that today, and paying for it here costs nothing.
        //
        // ⚠ An explicit block rather than a `using var` over the whole method, so the lock is given
        // back before the suppression below asks the *locking* body interface for anything — which
        // would be this thread waiting on a mutex it already holds, the hang the anchor conversions
        // above were moved out of the lock to avoid.
        {
            Span<BodyID> both = [first, second];
            using var locked = system.BodyLockInterface.LockMultiWrite(both);

            var bodyOne = locked.GetBody(0);
            var bodyTwo = locked.GetBody(1);

            if (bodyOne is null || bodyTwo is null) {
                throw new PhysicsHandleException(
                    $"Could not lock both bodies of a {description.Kind} constraint: "
                    + $"{description.First} and {description.Second}."
                );
            }

            native = Build(description, in bodyOne, in bodyTwo);
            native.Enabled = true;
            system.AddConstraint(native);
        }

        var handle = new ConstraintHandle(nextConstraintId++);

        // The world anchor is a shapeless static body, so there is no contact between it and anything
        // to suppress — and giving it a sub-group would put every world-pinned joint's other body in
        // a group for nothing.
        var suppress = description.SuppressPairCollision && !description.Second.IsNone;

        // The anchors and the axis were converted to body-local above, the same way Jolt does it
        // internally. Keeping the local form is what lets the debug overlay draw the joint where it
        // is *now* rather than where it was when the level loaded — and the gap between the two
        // anchors, once they are transformed back out, is the constraint's error.
        constraints.Add(
            handle.Value,
            new(
                native,
                description.First,
                description.Second,
                description.Kind,
                localFirstAnchor,
                localSecondAnchor,
                localAxis,
                suppress
            )
        );

        constraintOrder.Add(handle);

        if (suppress) {
            SuppressForConstraint(description.First, description.Second);
        }

        return handle;
    }

    Vector3 ToLocal(Vector3 world, BodyHandle body) {
        GetTransform(body, out var position, out var rotation);
        return Quaternion.Transform(world - position, Quaternion.Conjugate(rotation));
    }

    Vector3 ToLocalDirection(Vector3 world, BodyHandle body) =>
        Quaternion.Transform(world, Quaternion.Conjugate(GetRotation(body)));

    Vector3 ToWorld(Vector3 local, BodyHandle body) {
        GetTransform(body, out var position, out var rotation);
        return position + Quaternion.Transform(local, rotation);
    }

    /// <summary>Removes a constraint and frees it.</summary>
    /// <param name="handle">The constraint.</param>
    /// <remarks>Doing this twice, or to a handle from another world, does nothing.</remarks>
    public void DestroyConstraint(ConstraintHandle handle) {
        if (IsDisposed || !constraints.Remove(handle.Value, out var constraint)) {
            return;
        }

        constraintOrder.Remove(handle);
        system.RemoveConstraint(constraint.Native);
        constraint.Native.Dispose();

        // The pair goes back to whatever else still has an opinion about it — another joint over the
        // same two bodies, or an explicit SetPairCollision. Leaving it suppressed would be a state
        // outliving the only thing that asked for it, which is how a pair of bodies ends up passing
        // through one another for the rest of a level with nothing left in the world to blame.
        if (constraint.Suppresses) {
            ReleaseForConstraint(constraint.First, constraint.Second);
        }
    }

    /// <summary>Whether a handle still names a constraint in this world.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="true" /> if the constraint exists.</returns>
    public bool IsAlive(ConstraintHandle handle) => constraints.ContainsKey(handle.Value);

    /// <summary>Turns a constraint off without destroying it, or back on.</summary>
    /// <param name="handle">The constraint.</param>
    /// <param name="enabled">Whether it acts.</param>
    /// <remarks>
    ///     A disabled constraint costs nothing to solve and keeps its anchors, which is what a
    ///     breakable joint or a temporarily released door wants — destroying and recreating one would
    ///     re-derive the anchors from wherever the bodies happen to be at that moment.
    /// </remarks>
    public void SetConstraintEnabled(ConstraintHandle handle, bool enabled) =>
        Constraint(handle).Native.Enabled = enabled;

    /// <summary>
    ///     Every constraint in the world, in the order it was created.
    /// </summary>
    /// <remarks>
    ///     Ordered rather than a dictionary's key set, so a debug overlay draws the same joints in the
    ///     same order every frame and two runs of a tool produce the same output. Valid until the next
    ///     create or destroy.
    /// </remarks>
    public ReadOnlySpan<ConstraintHandle> ConstraintHandles =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(constraintOrder);

    /// <summary>Which joint a constraint is.</summary>
    /// <param name="handle">The constraint.</param>
    /// <returns>Its kind.</returns>
    public ConstraintKind GetConstraintKind(ConstraintHandle handle) => Constraint(handle).Kind;

    /// <summary>
    ///     Where a constraint's two anchors are now, in world space.
    /// </summary>
    /// <param name="handle">The constraint.</param>
    /// <param name="first">The anchor on the first body.</param>
    /// <param name="second">The anchor on the second, or on the world.</param>
    /// <remarks>
    ///     The two coincide when the constraint is satisfied, so the distance between them is the
    ///     error the solver has not worked off — which is the single most useful number when a joint
    ///     looks wrong, and the reason the debug overlay draws the segment between them.
    /// </remarks>
    public void GetConstraintAnchors(ConstraintHandle handle, out Vector3 first, out Vector3 second) {
        var constraint = Constraint(handle);
        var anchorBody = constraint.Second.IsNone ? worldAnchor : constraint.Second;

        first = IsAlive(constraint.First) ? ToWorld(constraint.LocalFirstAnchor, constraint.First) : Vector3.Zero;
        second = IsAlive(anchorBody) ? ToWorld(constraint.LocalSecondAnchor, anchorBody) : first;
    }

    /// <summary>
    ///     The axis a constraint turns about or slides along, now, in world space.
    /// </summary>
    /// <param name="handle">The constraint.</param>
    /// <returns>The axis, or zero for a kind that has none.</returns>
    public Vector3 GetConstraintAxis(ConstraintHandle handle) {
        var constraint = Constraint(handle);

        return constraint.Kind is ConstraintKind.Hinge or ConstraintKind.Slider or ConstraintKind.Cone
            && IsAlive(constraint.First)
                ? Quaternion.Transform(constraint.LocalAxis, GetRotation(constraint.First))
                : Vector3.Zero;
    }

    /// <summary>The two bodies a constraint holds.</summary>
    /// <param name="handle">The constraint.</param>
    /// <param name="first">One body.</param>
    /// <param name="second">The other, or <see cref="BodyHandle.None" /> if it is pinned to the world.</param>
    public void GetConstraintBodies(ConstraintHandle handle, out BodyHandle first, out BodyHandle second) {
        var constraint = Constraint(handle);
        first = constraint.First;
        second = constraint.Second;
    }

    /// <summary>Changes a motorised constraint's target.</summary>
    /// <param name="handle">The constraint.</param>
    /// <param name="motor">How it should drive.</param>
    /// <param name="target">
    ///     What it drives towards: a velocity or a position, depending on <paramref name="motor" />.
    /// </param>
    /// <exception cref="PhysicsHandleException">The constraint has no motor.</exception>
    /// <remarks>
    ///     Only a hinge and a slider have motors. Everything else raises, rather than quietly doing
    ///     nothing — a servo that silently ignores its target is a bug that presents as a door which
    ///     will not open.
    /// </remarks>
    public void SetConstraintMotor(ConstraintHandle handle, ConstraintMotor motor, float target) {
        var native = Constraint(handle).Native;

        switch (native) {
            case HingeConstraint hinge:
                hinge.MotorState = ToJolt(motor);

                if (motor == ConstraintMotor.Velocity) {
                    hinge.TargetAngularVelocity = target;
                } else {
                    hinge.TargetAngle = target;
                }

                break;

            case SliderConstraint slider:
                slider.MotorState = ToJolt(motor);

                if (motor == ConstraintMotor.Velocity) {
                    slider.TargetVelocity = target;
                } else {
                    slider.TargetPosition = target;
                }

                break;

            default:
                throw new PhysicsHandleException($"{handle} is a {native.SubType} and has no motor.");
        }
    }

    JoltConstraint Constraint(ConstraintHandle handle) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        return constraints.TryGetValue(handle.Value, out var constraint)
            ? constraint
            : throw new PhysicsHandleException($"{handle} does not name a constraint in this world.");
    }

    /// <summary>
    ///     The invisible static body a constraint with no second body is pinned to, created on the
    ///     first ask.
    /// </summary>
    /// <remarks>
    ///     Jolt pins to the world with a sentinel body the binding does not expose, so this is the
    ///     next best thing: a static body with an <c>EmptyShape</c>, which has no geometry and
    ///     therefore no broad-phase footprint and no contacts. It is never destroyed while the world
    ///     lives, which costs one body slot for a world that uses any world-pinned constraint at all.
    /// </remarks>
    BodyHandle WorldAnchor() {
        if (IsAlive(worldAnchor)) {
            return worldAnchor;
        }

        var centre = System.Numerics.Vector3.Zero;
        using var shapeSettings = new EmptyShapeSettings(in centre);
        var shape = shapeSettings.Create();

        var position = System.Numerics.Vector3.Zero;
        var rotation = System.Numerics.Quaternion.Identity;

        using var settings = new BodyCreationSettings(
            shape,
            in position,
            in rotation,
            MotionType.Static,
            new ObjectLayer(0)
        );

        var id = system.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate);
        worldAnchor = new(id.ID);
        Store(worldAnchor, BodyDescription.Create() with { Motion = BodyMotion.Static });

        // Not in PhysicsShapes: that registry interns by description, and an empty shape has no
        // description worth exposing. It is owned here and freed with the world.
        worldAnchorShape = shape;
        return worldAnchor;
    }

    static TwoBodyConstraint Build(in ConstraintDescription description, in Body first, in Body second) =>
        description.Kind switch {
            ConstraintKind.Fixed => BuildFixed(description, in first, in second),
            ConstraintKind.Point => BuildPoint(description, in first, in second),
            ConstraintKind.Hinge => BuildHinge(description, in first, in second),
            ConstraintKind.Slider => BuildSlider(description, in first, in second),
            ConstraintKind.Distance => BuildDistance(description, in first, in second),
            ConstraintKind.Cone => BuildCone(description, in first, in second),
            _ => throw new ArgumentOutOfRangeException(
                nameof(description),
                description.Kind,
                "Unknown constraint kind."
            )
        };

    static TwoBodyConstraint BuildFixed(in ConstraintDescription description, in Body first, in Body second) {
        var settings = new FixedConstraintSettings {
            Space = ConstraintSpace.WorldSpace,
            AutoDetectPoint = false,
            Point1 = JoltMath.ToJolt(description.FirstAnchor),
            Point2 = JoltMath.ToJolt(description.SecondAnchor),
            AxisX1 = System.Numerics.Vector3.UnitX,
            AxisY1 = System.Numerics.Vector3.UnitY,
            AxisX2 = System.Numerics.Vector3.UnitX,
            AxisY2 = System.Numerics.Vector3.UnitY
        };

        return settings.CreateConstraint(in first, in second);
    }

    static TwoBodyConstraint BuildPoint(in ConstraintDescription description, in Body first, in Body second) {
        var settings = new PointConstraintSettings {
            Space = ConstraintSpace.WorldSpace,
            Point1 = JoltMath.ToJolt(description.FirstAnchor),
            Point2 = JoltMath.ToJolt(description.SecondAnchor)
        };

        return settings.CreateConstraint(in first, in second);
    }

    static HingeConstraint BuildHinge(in ConstraintDescription description, in Body first, in Body second) {
        var axis = Normalize(description.Axis, nameof(ConstraintDescription.Axis));
        var perpendicular = JoltMathUtil.GetNormalizedPerpendicular(axis);

        var settings = new HingeConstraintSettings {
            Space = ConstraintSpace.WorldSpace,
            Point1 = JoltMath.ToJolt(description.FirstAnchor),
            Point2 = JoltMath.ToJolt(description.SecondAnchor),
            HingeAxis1 = axis,
            HingeAxis2 = axis,

            // The normal axis is the zero-angle reference. Both bodies get the same one, so the
            // constraint's current angle at creation is whatever the bodies' relative rotation
            // already is rather than an arbitrary offset.
            NormalAxis1 = perpendicular,
            NormalAxis2 = perpendicular,
            LimitsMin = description.LimitMinimum,
            LimitsMax = description.LimitMaximum,
            LimitsSpringSettings = ToJolt(description.Spring),
            MotorSettings = ToJolt(description.Motor, description.MotorMaximum, torque: true)
        };

        var constraint = (HingeConstraint)settings.CreateConstraint(in first, in second);
        ApplyMotor(constraint, description);
        return constraint;
    }

    static SliderConstraint BuildSlider(in ConstraintDescription description, in Body first, in Body second) {
        var axis = Normalize(description.Axis, nameof(ConstraintDescription.Axis));
        var perpendicular = JoltMathUtil.GetNormalizedPerpendicular(axis);

        var settings = new SliderConstraintSettings {
            Space = ConstraintSpace.WorldSpace,
            AutoDetectPoint = false,
            Point1 = JoltMath.ToJolt(description.FirstAnchor),
            Point2 = JoltMath.ToJolt(description.SecondAnchor),
            SliderAxis1 = axis,
            SliderAxis2 = axis,
            NormalAxis1 = perpendicular,
            NormalAxis2 = perpendicular,
            LimitsMin = description.LimitMinimum,
            LimitsMax = description.LimitMaximum,
            LimitsSpringSettings = ToJolt(description.Spring),
            MotorSettings = ToJolt(description.Motor, description.MotorMaximum, torque: false)
        };

        var constraint = (SliderConstraint)settings.CreateConstraint(in first, in second);
        ApplyMotor(constraint, description);
        return constraint;
    }

    static TwoBodyConstraint BuildDistance(in ConstraintDescription description, in Body first, in Body second) {
        var settings = new DistanceConstraintSettings {
            Space = ConstraintSpace.WorldSpace,
            Point1 = JoltMath.ToJolt(description.FirstAnchor),
            Point2 = JoltMath.ToJolt(description.SecondAnchor),
            MinDistance = description.LimitMinimum,
            MaxDistance = description.LimitMaximum,
            LimitsSpringSettings = ToJolt(description.Spring)
        };

        return settings.CreateConstraint(in first, in second);
    }

    static TwoBodyConstraint BuildCone(in ConstraintDescription description, in Body first, in Body second) {
        var axis = Normalize(description.Axis, nameof(ConstraintDescription.Axis));

        var settings = new ConeConstraintSettings {
            Space = ConstraintSpace.WorldSpace,
            Point1 = JoltMath.ToJolt(description.FirstAnchor),
            Point2 = JoltMath.ToJolt(description.SecondAnchor),
            TwistAxis1 = axis,
            TwistAxis2 = axis,
            HalfConeAngle = description.HalfConeAngle
        };

        return settings.CreateConstraint(in first, in second);
    }

    static void ApplyMotor(HingeConstraint constraint, in ConstraintDescription description) {
        constraint.MotorState = ToJolt(description.Motor);

        if (description.Motor == ConstraintMotor.Velocity) {
            constraint.TargetAngularVelocity = description.MotorTarget;
        } else if (description.Motor == ConstraintMotor.Position) {
            constraint.TargetAngle = description.MotorTarget;
        }
    }

    static void ApplyMotor(SliderConstraint constraint, in ConstraintDescription description) {
        constraint.MotorState = ToJolt(description.Motor);

        if (description.Motor == ConstraintMotor.Velocity) {
            constraint.TargetVelocity = description.MotorTarget;
        } else if (description.Motor == ConstraintMotor.Position) {
            constraint.TargetPosition = description.MotorTarget;
        }
    }

    static System.Numerics.Vector3 Normalize(Vector3 value, string what) {
        var length = value.Length();

        return length > 1e-9f
            ? JoltMath.ToJolt(value / length)
            : throw new ArgumentException($"A constraint's {what} must not be zero-length.", what);
    }

    static SpringSettings ToJolt(ConstraintSpring spring) =>
        new(SpringMode.FrequencyAndDamping, spring.Frequency, spring.Damping);

    static MotorState ToJolt(ConstraintMotor motor) =>
        motor switch {
            ConstraintMotor.Velocity => MotorState.Velocity,
            ConstraintMotor.Position => MotorState.Position,
            _ => MotorState.Off
        };

    static MotorSettings ToJolt(ConstraintMotor motor, float maximum, bool torque) {
        var settings = new MotorSettings();

        if (motor == ConstraintMotor.Off || !(maximum > 0f)) {
            return settings;
        }

        if (torque) {
            settings.SetTorqueLimit(maximum);
        } else {
            settings.SetForceLimit(maximum);
        }

        return settings;
    }

    /// <summary>A constraint, which bodies it holds, and where it holds them.</summary>
    /// <param name="Native">Jolt's constraint.</param>
    /// <param name="First">One body.</param>
    /// <param name="Second">The other, as the caller gave it — possibly none.</param>
    /// <param name="Kind">Which joint it is.</param>
    /// <param name="LocalFirstAnchor">The first anchor, in the first body's space.</param>
    /// <param name="LocalSecondAnchor">The second anchor, in the second body's space.</param>
    /// <param name="LocalAxis">The axis, in the first body's space. Zero for a kind with none.</param>
    /// <param name="Suppresses">Whether this constraint is holding a pair suppression open.</param>
    sealed record JoltConstraint(
        Constraint Native,
        BodyHandle First,
        BodyHandle Second,
        ConstraintKind Kind,
        Vector3 LocalFirstAnchor,
        Vector3 LocalSecondAnchor,
        Vector3 LocalAxis,
        bool Suppresses
    );
}
