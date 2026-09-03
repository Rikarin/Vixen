// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Interop;
using Vixen.Physics.Shapes;
using BoundingBox = Vixen.Core.Mathematics.BoundingBox;

namespace Vixen.Physics;

public sealed partial class PhysicsWorld {
    /// <summary>Creates a body and adds it to the simulation.</summary>
    /// <param name="description">What the body is.</param>
    /// <returns>Its handle.</returns>
    /// <exception cref="PhysicsShapeException">
    ///     The description has no shape, or has one its motion type cannot use.
    /// </exception>
    /// <remarks>
    ///     The body is added awake unless it is static, because a dynamic body created asleep does
    ///     not fall — which is correct, surprising, and the source of the first bug everybody writes.
    /// </remarks>
    public BodyHandle CreateBody(in BodyDescription description) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (description.Shape.IsNone) {
            throw new PhysicsShapeException("A body needs a shape.", nameof(description));
        }

        var shapeDescription = Shapes.Describe(description.Shape);

        if (description.Motion == BodyMotion.Dynamic && !shapeDescription.CanBeDynamic) {
            throw new PhysicsShapeException(
                $"A dynamic body cannot use a {shapeDescription.Kind} shape: it has no inertia tensor "
                + "the solver can integrate. Make the body static or kinematic, or give it a convex shape.",
                nameof(description)
            );
        }

        if (description.Layer.Index >= Layers.Count) {
            throw new ArgumentOutOfRangeException(
                nameof(description),
                description.Layer.Index,
                $"The world's layer table declares {Layers.Count} layers."
            );
        }

        var shape = Shapes.Resolve(description.Shape);
        var position = JoltMath.ToJolt(description.Position);
        var rotation = JoltMath.ToJolt(description.Rotation);

        using var settings = new BodyCreationSettings(
            shape,
            in position,
            in rotation,
            ToJolt(description.Motion),
            new ObjectLayer(description.Layer.Index)
        ) {
            LinearVelocity = JoltMath.ToJolt(description.LinearVelocity),
            AngularVelocity = JoltMath.ToJolt(description.AngularVelocity),
            AllowedDOFs = ToJolt(description.DegreesOfFreedom),
            IsSensor = description.IsSensor,
            AllowSleeping = description.AllowSleeping,
            Friction = description.Friction,
            Restitution = description.Restitution,
            LinearDamping = description.LinearDamping,
            AngularDamping = description.AngularDamping,
            GravityFactor = description.GravityFactor,
            UserData = description.UserData
        };

        if (description.Mass > 0f) {
            // CalculateMass keeps the shape's inertia distribution and scales the whole thing to the
            // mass asked for, which is what a number typed into an inspector means. The alternative —
            // MassAndInertiaProvided — needs a full tensor, and a caller who has one is not using
            // this overload.
            settings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;

            var mass = settings.MassPropertiesOverride;
            mass.Mass = description.Mass;
            settings.MassPropertiesOverride = mass;
        }

        var activation = description.Motion == BodyMotion.Static ? Activation.DontActivate : Activation.Activate;
        var id = system.BodyInterface.CreateAndAddBody(settings, activation);
        var handle = new BodyHandle(id.ID);

        // Set afterwards, not in the creation settings. JoltPhysicsSharp 2.22.0's
        // BodyCreationSettings.MotionQuality does not reach the native settings object — reading it
        // back gives an uninitialised value, and a body asked for continuous detection at creation
        // gets discrete. Going through the body interface sets it correctly, and a bullet that
        // tunnels through a 4 cm wall at 400 m/s stops in front of it. Covered by
        // PhysicsWorldTests.AFastBodyTunnelsThroughAThinWallUntilContinuousDetectionIsTurnedOn.
        if (description.MotionQuality == BodyMotionQuality.Continuous) {
            system.BodyInterface.SetMotionQuality(in id, MotionQuality.LinearCast);
        }

        Store(handle, description);
        return handle;
    }

    /// <summary>Removes a body from the simulation and frees it.</summary>
    /// <param name="handle">The body.</param>
    /// <remarks>
    ///     Destroying a body whose handle is already stale does nothing, rather than throwing.
    ///     Teardown order is the one place a caller genuinely cannot know — a scene unload destroys
    ///     entities in whatever order the world holds them — and making the second destroy an error
    ///     turns that into defensive code at every call site.
    /// </remarks>
    public void DestroyBody(BodyHandle handle) {
        if (IsDisposed || !IsAlive(handle)) {
            return;
        }

        var id = new BodyID(handle.Value);
        system.BodyInterface.RemoveAndDestroyBody(in id);
        slots[handle.Index].Handle = BodyHandle.None.Value;
    }

    /// <summary>Whether a handle still names a body in this world.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns><see langword="true" /> if the body exists.</returns>
    public bool IsAlive(BodyHandle handle) {
        var index = handle.Index;
        return !handle.IsNone && index < (uint)slots.Length && slots[index].Handle == handle.Value;
    }

    /// <summary>Whatever was associated with a body when it was created.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>The user data, or zero if the handle is stale.</returns>
    /// <remarks>
    ///     Read from the world's own table rather than from Jolt, so it is still answerable in a
    ///     contact-removed event raised after the body was destroyed.
    /// </remarks>
    public ulong UserDataOf(BodyHandle handle) {
        var index = handle.Index;

        return index < (uint)slots.Length && slots[index].Handle == handle.Value
            ? slots[index].UserData
            : 0ul;
    }

    /// <summary>The shape a body was created with.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>The shape id, or <see cref="ShapeId.None" /> if the handle is stale.</returns>
    public ShapeId ShapeOf(BodyHandle handle) {
        var index = handle.Index;

        return index < (uint)slots.Length && slots[index].Handle == handle.Value
            ? slots[index].Shape
            : ShapeId.None;
    }

    /// <summary>Which layer a body is on.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>The layer.</returns>
    public PhysicsLayer LayerOf(BodyHandle handle) => slots[Check(handle)].Layer;

    /// <summary>Whether a body is a sensor.</summary>
    /// <param name="handle">The body.</param>
    /// <returns><see langword="true" /> if it reports overlaps instead of resolving them.</returns>
    public bool IsSensor(BodyHandle handle) => slots[Check(handle)].IsSensor;

    /// <summary>Where a body is.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>Its position in world space.</returns>
    public Vector3 GetPosition(BodyHandle handle) {
        var id = Id(handle);
        return JoltMath.ToVixen(system.BodyInterface.GetPosition(in id));
    }

    /// <summary>Which way a body faces.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>Its rotation.</returns>
    public Quaternion GetRotation(BodyHandle handle) {
        var id = Id(handle);
        return JoltMath.ToVixen(system.BodyInterface.GetRotation(in id));
    }

    /// <summary>Where a body is and which way it faces, in one read.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="position">Where.</param>
    /// <param name="rotation">Which way.</param>
    public void GetTransform(BodyHandle handle, out Vector3 position, out Quaternion rotation) {
        var id = Id(handle);
        var body = system.BodyInterface;
        position = JoltMath.ToVixen(body.GetPosition(in id));
        rotation = JoltMath.ToVixen(body.GetRotation(in id));
    }

    /// <summary>Moves a body there at once, with no motion in between.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="position">Where to put it.</param>
    /// <param name="rotation">Which way to face it.</param>
    /// <param name="activate">Whether to wake it.</param>
    /// <remarks>
    ///     A teleport, and it reads as one: nothing is swept, so a body moved across a wall is on the
    ///     other side of it, and anything resting on a platform moved this way is left where it was.
    ///     For a platform, use <see cref="MoveKinematic" />.
    /// </remarks>
    public void SetTransform(BodyHandle handle, Vector3 position, Quaternion rotation, bool activate = true) {
        var id = Id(handle);
        var target = JoltMath.ToJolt(position);
        var facing = JoltMath.ToJolt(rotation);

        system.BodyInterface.SetPositionAndRotation(
            in id,
            in target,
            in facing,
            activate ? Activation.Activate : Activation.DontActivate
        );
    }

    /// <summary>
    ///     Drives a kinematic body towards a pose over one step, giving it the velocity to get there.
    /// </summary>
    /// <param name="handle">The body.</param>
    /// <param name="position">Where it should be at the end of the step.</param>
    /// <param name="rotation">Which way it should face.</param>
    /// <param name="deltaTime">How long the step is.</param>
    /// <remarks>
    ///     This is what makes a moving platform carry what stands on it: the body arrives with a
    ///     velocity, so friction at the contact does its job. Setting the position instead gives the
    ///     platform no velocity at all, and everything on it slides off as though the platform had
    ///     vanished and reappeared — which, to the solver, it has.
    /// </remarks>
    public void MoveKinematic(BodyHandle handle, Vector3 position, Quaternion rotation, float deltaTime) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deltaTime);

        var id = Id(handle);
        var target = JoltMath.ToJolt(position);
        var facing = JoltMath.ToJolt(rotation);
        system.BodyInterface.MoveKinematic(in id, in target, in facing, deltaTime);
    }

    /// <summary>A body's linear velocity, in metres a second.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>The velocity.</returns>
    public Vector3 GetLinearVelocity(BodyHandle handle) {
        var id = Id(handle);
        return JoltMath.ToVixen(system.BodyInterface.GetLinearVelocity(in id));
    }

    /// <summary>Sets a body's linear velocity.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="velocity">The velocity, in metres a second.</param>
    public void SetLinearVelocity(BodyHandle handle, Vector3 velocity) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(velocity);
        system.BodyInterface.SetLinearVelocity(in id, in value);
    }

    /// <summary>A body's angular velocity, in radians a second about each axis.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>The velocity.</returns>
    public Vector3 GetAngularVelocity(BodyHandle handle) {
        var id = Id(handle);
        return JoltMath.ToVixen(system.BodyInterface.GetAngularVelocity(in id));
    }

    /// <summary>Sets a body's angular velocity.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="velocity">The velocity, in radians a second about each axis.</param>
    public void SetAngularVelocity(BodyHandle handle, Vector3 velocity) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(velocity);
        system.BodyInterface.SetAngularVelocity(in id, in value);
    }

    /// <summary>Applies a force for the coming step, at the body's centre of mass.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="force">The force, in newtons.</param>
    /// <remarks>
    ///     Forces accumulate until the next step and are then cleared, so this must be called every
    ///     step for as long as the force is meant to act — which is what makes it the right tool for
    ///     thrust and the wrong one for a one-off shove. That is <see cref="ApplyImpulse(BodyHandle, Vector3)" />.
    /// </remarks>
    public void ApplyForce(BodyHandle handle, Vector3 force) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(force);
        system.BodyInterface.AddForce(in id, in value);
    }

    /// <summary>Applies a force for the coming step, at a point in world space.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="force">The force, in newtons.</param>
    /// <param name="point">Where it is applied, in world space.</param>
    public void ApplyForce(BodyHandle handle, Vector3 force, Vector3 point) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(force);
        var at = JoltMath.ToJolt(point);
        system.BodyInterface.AddForce(in id, in value, in at);
    }

    /// <summary>Applies a torque for the coming step.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="torque">The torque, in newton-metres.</param>
    public void ApplyTorque(BodyHandle handle, Vector3 torque) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(torque);
        system.BodyInterface.AddTorque(in id, in value);
    }

    /// <summary>Changes a body's momentum at once, at its centre of mass.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="impulse">The impulse, in newton-seconds.</param>
    public void ApplyImpulse(BodyHandle handle, Vector3 impulse) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(impulse);
        system.BodyInterface.AddImpulse(in id, in value);
    }

    /// <summary>Changes a body's momentum at once, at a point in world space.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="impulse">The impulse, in newton-seconds.</param>
    /// <param name="point">Where it lands, in world space. Off-centre, this also spins the body.</param>
    public void ApplyImpulse(BodyHandle handle, Vector3 impulse, Vector3 point) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(impulse);
        var at = JoltMath.ToJolt(point);
        system.BodyInterface.AddImpulse(in id, in value, in at);
    }

    /// <summary>Changes a body's angular momentum at once.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="impulse">The angular impulse.</param>
    public void ApplyAngularImpulse(BodyHandle handle, Vector3 impulse) {
        var id = Id(handle);
        var value = JoltMath.ToJolt(impulse);
        system.BodyInterface.AddAngularImpulse(in id, in value);
    }

    /// <summary>Whether a body is awake.</summary>
    /// <param name="handle">The body.</param>
    /// <returns><see langword="true" /> if the solver is still integrating it.</returns>
    public bool IsActive(BodyHandle handle) {
        var id = Id(handle);
        return system.BodyInterface.IsActive(in id);
    }

    /// <summary>Wakes a body.</summary>
    /// <param name="handle">The body.</param>
    public void Activate(BodyHandle handle) {
        var id = Id(handle);
        system.BodyInterface.ActivateBody(in id);
    }

    /// <summary>Puts a body to sleep.</summary>
    /// <param name="handle">The body.</param>
    public void Deactivate(BodyHandle handle) {
        var id = Id(handle);
        system.BodyInterface.DeactivateBody(in id);
    }

    /// <summary>How a body moves.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>Its motion type.</returns>
    public BodyMotion GetMotion(BodyHandle handle) {
        var id = Id(handle);

        return system.BodyInterface.GetMotionType(in id) switch {
            MotionType.Static => BodyMotion.Static,
            MotionType.Kinematic => BodyMotion.Kinematic,
            _ => BodyMotion.Dynamic
        };
    }

    /// <summary>Changes how a body moves.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="motion">The motion type it should have.</param>
    /// <param name="activate">Whether to wake it.</param>
    /// <remarks>
    ///     A body created static stays in the static broad-phase tree even after this, so a body that
    ///     is going to move at some point should be created kinematic and left asleep rather than
    ///     created static and promoted.
    /// </remarks>
    public void SetMotion(BodyHandle handle, BodyMotion motion, bool activate = true) {
        var id = Id(handle);

        system.BodyInterface.SetMotionType(
            in id,
            ToJolt(motion),
            activate ? Activation.Activate : Activation.DontActivate
        );
    }

    /// <summary>Changes how carefully a body's motion is swept.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="quality">The quality.</param>
    public void SetMotionQuality(BodyHandle handle, BodyMotionQuality quality) {
        var id = Id(handle);

        system.BodyInterface.SetMotionQuality(
            in id,
            quality == BodyMotionQuality.Continuous ? MotionQuality.LinearCast : MotionQuality.Discrete
        );
    }

    /// <summary>Changes a body's friction.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="friction">The new value.</param>
    public void SetFriction(BodyHandle handle, float friction) {
        var id = Id(handle);
        system.BodyInterface.SetFriction(in id, friction);
    }

    /// <summary>Changes a body's restitution.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="restitution">The new value.</param>
    public void SetRestitution(BodyHandle handle, float restitution) {
        var id = Id(handle);
        system.BodyInterface.SetRestitution(in id, restitution);
    }

    /// <summary>Changes how much of the world's gravity acts on a body.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="factor">One is all of it, zero is none, negative floats upwards.</param>
    public void SetGravityFactor(BodyHandle handle, float factor) {
        var id = Id(handle);
        system.BodyInterface.SetGravityFactor(in id, factor);
    }

    /// <summary>Gives a body a different shape.</summary>
    /// <param name="handle">The body.</param>
    /// <param name="shape">The new shape.</param>
    /// <param name="updateMass">
    ///     Whether to recompute the mass from the new shape. Off keeps the mass and changes only the
    ///     volume, which is what a character crouching wants.
    /// </param>
    public void SetShape(BodyHandle handle, ShapeId shape, bool updateMass = true) {
        var index = Check(handle);
        var id = Id(handle);
        var native = Shapes.Resolve(shape);
        system.BodyInterface.SetShape(in id, in native, updateMass, Activation.Activate);
        slots[index].Shape = shape;
    }

    /// <summary>The axis-aligned bounds a body currently occupies.</summary>
    /// <param name="handle">The body.</param>
    /// <returns>The bounds, in world space.</returns>
    public BoundingBox GetBounds(BodyHandle handle) {
        var id = Id(handle);

        // Through a body lock rather than BodyInterface.GetTransformedShape, whose TransformedShape
        // comes back with an identity transform in JoltPhysicsSharp 2.22.0 — so its "world space"
        // bounds are the shape's local ones, centred on the origin whatever the body is doing.
        // Covered by PhysicsWorldTests.TheBoundsOfABodyFollowIt.
        var lockInterface = system.BodyLockInterfaceNoLock;
        lockInterface.LockRead(in id, out var locked);

        try {
            var bounds = locked is { Succeeded: true, Body: { } body } ? body.WorldSpaceBounds : default;
            return new(JoltMath.ToVixen(bounds.Min), JoltMath.ToVixen(bounds.Max));
        } finally {
            lockInterface.UnlockRead(in locked);
        }
    }

    void Store(BodyHandle handle, in BodyDescription description) {
        var index = handle.Index;

        if (index >= (uint)slots.Length) {
            var grown = new BodySlot[Math.Max((int)index + 1, Math.Max(slots.Length * 2, 64))];
            Array.Copy(slots, grown, slots.Length);

            // Slots that have never held a body have to read as free, and BodyHandle.None is
            // uint.MaxValue rather than zero, so a zeroed array would claim body #0 everywhere.
            for (var slot = slots.Length; slot < grown.Length; slot++) {
                grown[slot].Handle = BodyHandle.None.Value;
            }

            slots = grown;
        }

        slots[index] = new() {
            Handle = handle.Value,
            IsSensor = description.IsSensor,
            Layer = description.Layer,
            Shape = description.Shape,
            UserData = description.UserData,

            // ⚠ Written explicitly because it is the one field whose zero is a valid value. A body
            // reusing a destroyed body's index inherits its slot, and a slot left holding the old
            // body's sub-group would make the new one silently a member of a group — and pass
            // through whatever the old body was suppressed against.
            SubGroup = NoSubGroup
        };
    }

    uint Check(BodyHandle handle) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        return IsAlive(handle)
            ? handle.Index
            : throw new PhysicsHandleException($"{handle} does not name a body in this world.");
    }

    BodyID Id(BodyHandle handle) {
        Check(handle);
        return new(handle.Value);
    }

    static MotionType ToJolt(BodyMotion motion) =>
        motion switch {
            BodyMotion.Static => MotionType.Static,
            BodyMotion.Kinematic => MotionType.Kinematic,
            _ => MotionType.Dynamic
        };

    static AllowedDOFs ToJolt(BodyDegreesOfFreedom degrees) {
        var result = (AllowedDOFs)0;

        if ((degrees & BodyDegreesOfFreedom.TranslationX) != 0) {
            result |= AllowedDOFs.TranslationX;
        }

        if ((degrees & BodyDegreesOfFreedom.TranslationY) != 0) {
            result |= AllowedDOFs.TranslationY;
        }

        if ((degrees & BodyDegreesOfFreedom.TranslationZ) != 0) {
            result |= AllowedDOFs.TranslationZ;
        }

        if ((degrees & BodyDegreesOfFreedom.RotationX) != 0) {
            result |= AllowedDOFs.RotationX;
        }

        if ((degrees & BodyDegreesOfFreedom.RotationY) != 0) {
            result |= AllowedDOFs.RotationY;
        }

        if ((degrees & BodyDegreesOfFreedom.RotationZ) != 0) {
            result |= AllowedDOFs.RotationZ;
        }

        return result;
    }
}
