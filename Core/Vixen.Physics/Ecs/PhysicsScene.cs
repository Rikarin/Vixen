// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Physics.Bodies;
using Vixen.Physics.Events;
using Vixen.Physics.Shapes;
using EcsWorld = Vixen.Ecs.World;

namespace Vixen.Physics.Ecs;

/// <summary>Two entities touched, went on touching, or stopped.</summary>
/// <param name="Phase">Which of the three.</param>
/// <param name="First">One entity.</param>
/// <param name="Second">The other.</param>
/// <param name="Position">A point on the contact manifold, in world space.</param>
/// <param name="Normal">The contact normal, from the first towards the second.</param>
/// <param name="PenetrationDepth">How far the two overlap, in metres.</param>
/// <remarks>
///     An entity here may already be dead: a contact ends because a body was destroyed as often as
///     because it moved away, and the entity that owned it usually went with it. Check
///     <c>World.IsAlive</c> before touching one.
/// </remarks>
public readonly record struct EntityContactEvent(
    ContactPhase Phase,
    Entity First,
    Entity Second,
    Vector3 Position,
    Vector3 Normal,
    float PenetrationDepth
);

/// <summary>Something entered or left a trigger volume.</summary>
/// <param name="Phase">Entry or exit.</param>
/// <param name="Sensor">The trigger.</param>
/// <param name="Other">What entered or left it.</param>
public readonly record struct EntityTriggerEvent(ContactPhase Phase, Entity Sensor, Entity Other);

/// <summary>
///     The bridge: an ECS world on one side, a <see cref="PhysicsWorld" /> on the other, and one
///     fixed step joining them.
/// </summary>
/// <remarks>
///     <para>
///         Three passes, in this order and no other. <see cref="Synchronize" /> makes the physics
///         world match what the components say — bodies created for new colliders, destroyed for
///         removed ones, kinematic bodies driven towards their authored transforms.
///         <see cref="Step" /> advances the simulation. <see cref="Writeback" /> copies the results
///         back into <c>LocalTransform</c> and the velocity components. Doing them in any other order
///         costs a step of latency at best and drives the simulation from stale poses at worst.
///     </para>
///     <para>
///         <b>A body's entity should be a root.</b> Physics works in world space and the bridge reads
///         and writes <c>LocalTransform</c>, so an entity with a <c>Parent</c> has its local
///         transform treated as though it were a world one — physics and the transform hierarchy then
///         write the same component with different meanings, and the entity ends up wherever the last
///         writer put it. Attach things to a body with a joint, or hang a child <i>under</i> the body
///         rather than making the body a child.
///     </para>
///     <para>
///         The bridge holds no reference to any system. <see cref="PhysicsSystems" /> registers three
///         thin systems that call these three methods, so a test, a tool or a headless server can
///         drive the same passes without a <c>SystemRunner</c>.
///     </para>
/// </remarks>
public sealed class PhysicsScene : IDisposable {
    static readonly QueryDescription NeedBody = new QueryDescription()
        .WithAll<Collider, LocalTransform>()
        .WithNone<PhysicsBody>();

    static readonly QueryDescription HaveBody = new QueryDescription()
        .WithAll<PhysicsBody, LocalTransform>();

    static readonly QueryDescription OrphanedBody = new QueryDescription()
        .WithAll<PhysicsBody>()
        .WithNone<Collider>();

    readonly Dictionary<uint, Entity> entitiesByBody = [];
    readonly List<Entity> pendingCreate = [];
    readonly List<Entity> pendingDestroy = [];
    readonly List<Entity> pendingRebuild = [];
    readonly List<Entity> pendingUntag = [];
    readonly List<EntityContactEvent> contactEvents = [];
    readonly List<EntityTriggerEvent> triggerEvents = [];
    readonly bool ownsWorld;

    /// <summary>The ECS world this bridges.</summary>
    public EcsWorld Entities { get; }

    /// <summary>The simulation.</summary>
    public PhysicsWorld World { get; }

    /// <summary>The shapes bodies in this scene may use.</summary>
    public PhysicsShapes Shapes => World.Shapes;

    /// <summary>How many entities have bodies.</summary>
    public int BodyCount => entitiesByBody.Count;

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Contacts from the last <see cref="Step" />, in entity terms.</summary>
    public ReadOnlySpan<EntityContactEvent> Contacts => CollectionsMarshal.AsSpan(contactEvents);

    /// <summary>Trigger crossings from the last <see cref="Step" />, in entity terms.</summary>
    public ReadOnlySpan<EntityTriggerEvent> Triggers => CollectionsMarshal.AsSpan(triggerEvents);

    /// <summary>Builds a bridge over an ECS world.</summary>
    /// <param name="entities">The ECS world.</param>
    /// <param name="settings">How the simulation is configured, or <see langword="null" /> for the defaults.</param>
    public PhysicsScene(EcsWorld entities, PhysicsWorldSettings? settings = null) {
        ArgumentNullException.ThrowIfNull(entities);

        Entities = entities;
        World = new(settings);
        ownsWorld = true;
    }

    /// <summary>Builds a bridge over an ECS world and a simulation somebody else owns.</summary>
    /// <param name="entities">The ECS world.</param>
    /// <param name="world">The simulation, which this bridge will not dispose.</param>
    /// <remarks>For a host that already has a world — a replay, a server that shares one across scenes.</remarks>
    public PhysicsScene(EcsWorld entities, PhysicsWorld world) {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(world);

        Entities = entities;
        World = world;
        ownsWorld = false;
    }

    /// <summary>The entity a body belongs to, if it still has one.</summary>
    /// <param name="body">The body.</param>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="true" /> if the body is one of this scene's.</returns>
    public bool TryGetEntity(BodyHandle body, out Entity entity) =>
        entitiesByBody.TryGetValue(body.Value, out entity);

    /// <summary>The body an entity has, if it has one.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="body">The body.</param>
    /// <returns><see langword="true" /> if the entity has a body.</returns>
    public bool TryGetBody(Entity entity, out BodyHandle body) {
        if (Entities.IsAlive(entity) && Entities.TryGet<PhysicsBody>(entity, out var component)) {
            body = component.Handle;
            return true;
        }

        body = BodyHandle.None;
        return false;
    }

    /// <summary>
    ///     Makes the simulation match the components: creates, destroys and rebuilds bodies, and
    ///     pushes authored transforms and velocities in.
    /// </summary>
    /// <param name="deltaTime">The fixed step, which kinematic motion is expressed over.</param>
    public void Synchronize(float deltaTime) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        CollectStructuralChanges();
        ApplyStructuralChanges();
        PushAuthoredState(deltaTime);
    }

    /// <summary>Advances the simulation and translates its events into entity terms.</summary>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <returns>What the step ran into.</returns>
    public PhysicsStepResult Step(float deltaTime) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var result = World.Step(deltaTime);

        contactEvents.Clear();
        triggerEvents.Clear();

        foreach (var contact in World.Contacts) {
            contactEvents.Add(
                new(
                    contact.Phase,
                    EntityOf(contact.First),
                    EntityOf(contact.Second),
                    contact.Position,
                    contact.Normal,
                    contact.PenetrationDepth
                )
            );
        }

        foreach (var trigger in World.Triggers) {
            triggerEvents.Add(new(trigger.Phase, EntityOf(trigger.Sensor), EntityOf(trigger.Other)));
        }

        return result;
    }

    /// <summary>Copies the simulation's results back into components.</summary>
    /// <remarks>
    ///     Static bodies are skipped: their transforms are the authored ones and writing them back
    ///     would dirty <c>LocalTransform</c> for a hundred thousand pieces of level geometry every
    ///     step, which is precisely the change-version traffic <c>TransformSystem</c> exists to avoid.
    /// </remarks>
    public void Writeback() {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        foreach (var chunk in Entities.Chunks(HaveBody)) {
            var bodies = chunk.ReadValues<PhysicsBody>();
            var transforms = chunk.Values<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var body = bodies[index];

                if (body.BuiltMotion == BodyMotion.Static || !World.IsAlive(body.Handle)) {
                    continue;
                }

                World.GetTransform(body.Handle, out var position, out var rotation);

                transforms[index].Position = position;
                transforms[index].Rotation = rotation;

                var entity = entities[index];

                if (Entities.Has<LinearVelocity>(entity)) {
                    Entities.Get<LinearVelocity>(entity).Value = World.GetLinearVelocity(body.Handle);
                }

                if (Entities.Has<AngularVelocity>(entity)) {
                    Entities.Get<AngularVelocity>(entity).Value = World.GetAngularVelocity(body.Handle);
                }

                if (Entities.Has<PhysicsInterpolation>(entity)) {
                    ref var interpolation = ref Entities.Get<PhysicsInterpolation>(entity);
                    interpolation.PreviousPosition = interpolation.CurrentPosition;
                    interpolation.PreviousRotation = interpolation.CurrentRotation;
                    interpolation.CurrentPosition = position;
                    interpolation.CurrentRotation = rotation;
                }
            }
        }
    }

    void CollectStructuralChanges() {
        pendingCreate.Clear();
        pendingDestroy.Clear();
        pendingRebuild.Clear();

        foreach (var chunk in Entities.Chunks(NeedBody)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                pendingCreate.Add(entities[index]);
            }
        }

        foreach (var chunk in Entities.Chunks(OrphanedBody)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                pendingDestroy.Add(entities[index]);
            }
        }

        // A shape, motion type or sensor flag that changed cannot be edited into a live body without
        // recreating it: Jolt fixes the motion type's broad-phase tree and the sensor flag's contact
        // path at creation. Anything else — mass, damping, friction — is a setter, and is applied in
        // PushAuthoredState without the body ever going away.
        foreach (var chunk in Entities.Chunks(HaveBody)) {
            var bodies = chunk.ReadValues<PhysicsBody>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];

                if (!Entities.TryGet<Collider>(entity, out var collider)) {
                    continue;
                }

                var motion = Entities.TryGet<RigidBody>(entity, out var rigid)
                    ? rigid.Motion
                    : BodyMotion.Static;

                var body = bodies[index];

                if (body.BuiltShape != collider.Shape
                    || body.BuiltMotion != motion
                    || body.BuiltSensor != collider.IsSensor) {
                    pendingRebuild.Add(entity);
                }
            }
        }
    }

    void ApplyStructuralChanges() {
        foreach (var entity in pendingDestroy) {
            Remove(entity);
            Entities.Remove<PhysicsBody>(entity);
        }

        foreach (var entity in pendingRebuild) {
            Remove(entity);
            Entities.Get<PhysicsBody>(entity) = Create(entity);
        }

        foreach (var entity in pendingCreate) {
            Entities.Add(entity, Create(entity));
        }
    }

    PhysicsBody Create(Entity entity) {
        var collider = Entities.Read<Collider>(entity);
        var transform = Entities.Read<LocalTransform>(entity);
        var hasRigid = Entities.TryGet<RigidBody>(entity, out var rigid);

        var description = BodyDescription.Create() with {
            Shape = collider.Shape,
            Position = transform.Position,
            Rotation = transform.Rotation,
            Motion = hasRigid ? rigid.Motion : BodyMotion.Static,
            MotionQuality = hasRigid ? rigid.MotionQuality : BodyMotionQuality.Discrete,
            Layer = collider.Layer,
            Friction = collider.Friction,
            Restitution = collider.Restitution,
            IsSensor = collider.IsSensor,
            Mass = hasRigid ? rigid.Mass : 0f,
            GravityFactor = hasRigid ? rigid.GravityFactor : 1f,
            LinearDamping = hasRigid ? rigid.LinearDamping : 0f,
            AngularDamping = hasRigid ? rigid.AngularDamping : 0f,
            DegreesOfFreedom = hasRigid ? rigid.DegreesOfFreedom : BodyDegreesOfFreedom.All,
            AllowSleeping = !hasRigid || rigid.AllowSleeping,
            LinearVelocity = Entities.TryGet<LinearVelocity>(entity, out var linear) ? linear.Value : Vector3.Zero,
            AngularVelocity = Entities.TryGet<AngularVelocity>(entity, out var angular)
                ? angular.Value
                : Vector3.Zero,

            // The entity handle, packed. This is what turns a contact between two Jolt bodies back
            // into a contact between two entities without a dictionary lookup on the hot path.
            UserData = entity.Packed
        };

        var handle = World.CreateBody(description);
        entitiesByBody[handle.Value] = entity;

        if (Entities.Has<PhysicsInterpolation>(entity)) {
            Entities.Get<PhysicsInterpolation>(entity) = new() {
                PreviousPosition = transform.Position,
                PreviousRotation = transform.Rotation,
                CurrentPosition = transform.Position,
                CurrentRotation = transform.Rotation
            };
        }

        return new() {
            Handle = handle,
            BuiltShape = collider.Shape,
            BuiltMotion = description.Motion,
            BuiltSensor = collider.IsSensor
        };
    }

    void Remove(Entity entity) {
        if (!Entities.TryGet<PhysicsBody>(entity, out var body)) {
            return;
        }

        entitiesByBody.Remove(body.Handle.Value);
        World.DestroyBody(body.Handle);
    }

    void PushAuthoredState(float deltaTime) {
        pendingUntag.Clear();

        foreach (var chunk in Entities.Chunks(HaveBody)) {
            var bodies = chunk.ReadValues<PhysicsBody>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var body = bodies[index];

                if (!World.IsAlive(body.Handle)) {
                    continue;
                }

                var entity = entities[index];
                var transform = transforms[index];

                if (body.BuiltMotion == BodyMotion.Kinematic) {
                    // Driven, not teleported: the body arrives with the velocity that got it there,
                    // which is what carries whatever is standing on it. See PhysicsWorld.MoveKinematic.
                    World.MoveKinematic(body.Handle, transform.Position, transform.Rotation, deltaTime);
                } else if (Entities.Has<PhysicsTeleport>(entity)) {
                    World.SetTransform(body.Handle, transform.Position, transform.Rotation);

                    // Taking the tag off moves the entity to a different archetype, which invalidates
                    // the very spans this loop is walking. Recorded and done after, which is the same
                    // reason CommandBuffer exists.
                    pendingUntag.Add(entity);
                }

                if (body.BuiltMotion != BodyMotion.Dynamic) {
                    continue;
                }

                if (Entities.TryGet<LinearVelocity>(entity, out var linear)
                    && linear.Value != World.GetLinearVelocity(body.Handle)) {
                    World.SetLinearVelocity(body.Handle, linear.Value);
                }

                if (Entities.TryGet<AngularVelocity>(entity, out var angular)
                    && angular.Value != World.GetAngularVelocity(body.Handle)) {
                    World.SetAngularVelocity(body.Handle, angular.Value);
                }
            }
        }

        foreach (var entity in pendingUntag) {
            Entities.Remove<PhysicsTeleport>(entity);
        }
    }

    Entity EntityOf(BodyHandle body) {
        var packed = World.UserDataOf(body);

        // Zero is the world anchor and anything created outside the bridge. Unpacking it would give
        // entity 0, which is never allocated but is not obviously "no entity" at a call site either.
        return packed == 0ul ? Entity.Null : Entity.FromPacked(packed, Entities.Id);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;
        entitiesByBody.Clear();

        if (ownsWorld) {
            World.Dispose();
        }
    }
}
