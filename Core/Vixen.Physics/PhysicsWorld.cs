// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using JoltPhysicsSharp;
using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Characters;
using Vixen.Physics.Constraints;
using Vixen.Physics.Events;
using Vixen.Physics.Interop;
using Vixen.Physics.Queries;
using Vixen.Physics.Shapes;

namespace Vixen.Physics;

/// <summary>
///     A simulated world: bodies, constraints, characters and the queries that ask about them.
/// </summary>
/// <remarks>
///     <para>
///         This is the whole engine-facing surface of Jolt. Nothing above it names a Jolt type, which
///         is what makes the binding replaceable and what lets the API be shaped for Vixen's
///         conventions — right-handed Y-up mathematics, handles rather than native objects, a fixed
///         step it does not own.
///     </para>
///     <para>
///         <b>It does not own the clock.</b> <see cref="Step" /> takes a delta and does not accumulate,
///         because the accumulator is <c>Vixen.Engine.Frames.FixedStepAccumulator</c> and there must
///         be exactly one of it — a physics world that ran its own steps would drift from the
///         simulation phase it is meant to be part of, and a replay would stop reproducing.
///     </para>
///     <para>
///         <b>It is not thread-safe.</b> Bodies are created, moved and queried from whichever thread
///         drives the step, and Jolt's own parallelism lives inside <see cref="Step" />. The one
///         exception is the contact callbacks, which Jolt raises from its job threads; those are
///         buffered under a lock and handed back on the calling thread once the step is over — see
///         <see cref="Contacts" />.
///     </para>
/// </remarks>
public sealed partial class PhysicsWorld : IDisposable {
    readonly PhysicsSystem system;
    readonly JobSystemThreadPool jobs;
    readonly JoltLayerFilters filters;
    readonly LayerMaskFilter queryLayerFilter = new();
    readonly IgnoreBodyFilter queryBodyFilter = new();

    // Guards the three event lists only. Jolt raises contact callbacks from its job threads during
    // Step, so the lists are written from many threads and read from one, after the step. A lock and
    // not a concurrent collection: the write is an Add under contention that lasts nanoseconds, and
    // a lock-free queue would cost an allocation per event to gain nothing measurable.
    readonly Lock eventGate = new();
    readonly List<ContactEvent> contacts = [];
    readonly List<TriggerEvent> triggers = [];
    readonly List<BodyHandle> activations = [];
    readonly List<BodyHandle> deactivations = [];

    // Per-body side data, indexed by BodyHandle.Index. Jolt reuses indices and bumps a sequence
    // number each time, so a slot holds the whole handle and is only this body's while the two match
    // exactly — the same trick the ECS uses on entity slots, and for the same reason: without it a
    // contact reported for a destroyed body is attributed to whichever body took its place.
    BodySlot[] slots = [];

    readonly Dictionary<uint, JoltConstraint> constraints = [];
    readonly List<ConstraintHandle> constraintOrder = [];
    readonly List<CharacterController> characters = [];

    uint nextConstraintId = 1;

    /// <summary>The shapes bodies in this world may use.</summary>
    /// <remarks>
    ///     Owned by the world, so a shape cannot outlive the bodies referring to it or be freed while
    ///     one is being removed. A caller that wants shapes shared across worlds keeps its own
    ///     descriptions and registers them in each — descriptions are values and cost nothing to copy.
    /// </remarks>
    public PhysicsShapes Shapes { get; } = new();

    /// <summary>The layer table this world was built with.</summary>
    public PhysicsLayers Layers { get; }

    /// <summary>How the world was configured.</summary>
    public PhysicsWorldSettings Settings { get; }

    /// <summary>Gravity, in metres a second squared.</summary>
    public Vector3 Gravity {
        get => JoltMath.ToVixen(system.Gravity);
        set => system.Gravity = JoltMath.ToJolt(value);
    }

    /// <summary>How many bodies exist, awake or asleep.</summary>
    public int BodyCount => (int)system.BodiesCount;

    /// <summary>How many bodies are awake and being solved.</summary>
    public int ActiveBodyCount => (int)system.GetNumActiveBodies(BodyType.Rigid);

    /// <summary>How many constraints exist.</summary>
    public int ConstraintCount => constraints.Count;

    /// <summary>How many steps have been run.</summary>
    public long StepCount { get; private set; }

    /// <summary>What the last step ran into.</summary>
    public PhysicsStepResult LastStepResult { get; private set; }

    /// <summary>Whether <see cref="Dispose" /> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Everything that touched, went on touching or stopped during the last step.</summary>
    /// <remarks>
    ///     Valid until the next <see cref="Step" />, which clears it. Draining is the caller's job and
    ///     is what <c>PhysicsScene</c> does before it hands events to the ECS.
    /// </remarks>
    public ReadOnlySpan<ContactEvent> Contacts => CollectionsMarshal.AsSpan(contacts);

    /// <summary>Everything that entered or left a sensor during the last step.</summary>
    public ReadOnlySpan<TriggerEvent> Triggers => CollectionsMarshal.AsSpan(triggers);

    /// <summary>Bodies that woke during the last step.</summary>
    public ReadOnlySpan<BodyHandle> Activations => CollectionsMarshal.AsSpan(activations);

    /// <summary>Bodies that fell asleep during the last step.</summary>
    public ReadOnlySpan<BodyHandle> Deactivations => CollectionsMarshal.AsSpan(deactivations);

    /// <summary>Builds a world.</summary>
    /// <param name="settings">How it is configured, or <see langword="null" /> for the defaults.</param>
    /// <exception cref="PhysicsInitializationException">Jolt could not be brought up.</exception>
    public PhysicsWorld(PhysicsWorldSettings? settings = null) {
        Settings = settings ?? new();
        Layers = Settings.Layers;

        JoltRuntime.Acquire();

        try {
            filters = Layers.CreateFilters();

            var systemSettings = new PhysicsSystemSettings {
                MaxBodies = Settings.MaxBodies,
                MaxBodyPairs = Settings.MaxBodyPairs,
                MaxContactConstraints = Settings.MaxContactConstraints,
                NumBodyMutexes = Settings.BodyMutexCount,
                ObjectLayerPairFilter = filters.ObjectPairs,
                BroadPhaseLayerInterface = filters.BroadPhase,
                ObjectVsBroadPhaseLayerFilter = filters.ObjectVsBroadPhase
            };

            system = new(systemSettings);

            var tuning = system.Settings;
            tuning.NumVelocitySteps = (uint)Settings.VelocityIterations;
            tuning.NumPositionSteps = (uint)Settings.PositionIterations;
            tuning.DeterministicSimulation = Settings.Deterministic ? Bool8.True : Bool8.False;
            tuning.AllowSleeping = Settings.AllowSleeping ? Bool8.True : Bool8.False;
            system.Settings = tuning;

            system.Gravity = JoltMath.ToJolt(Settings.Gravity);

            // A thread count of zero means "one per hardware thread bar one", which is the pool's own
            // default and the value the determinism note in PhysicsWorldSettings warns about.
            jobs = Settings.ThreadCount > 0
                ? CreatePool(Settings.ThreadCount)
                : new JobSystemThreadPool();

            system.OnContactAdded += OnContactAdded;
            system.OnContactPersisted += OnContactPersisted;
            system.OnContactRemoved += OnContactRemoved;
            system.OnBodyActivated += OnBodyActivated;
            system.OnBodyDeactivated += OnBodyDeactivated;
        } catch {
            // Half a world is worse than none: the runtime reference would never be given back and
            // the next test in the process would find Jolt already up with no owner.
            Shapes.Dispose();
            filters?.Dispose();
            JoltRuntime.Release();
            throw;
        }
    }

    static JobSystemThreadPool CreatePool(int threadCount) {
        var config = new JobSystemThreadPoolConfig {
            maxJobs = (uint)Foundation.MaxPhysicsJobs,
            maxBarriers = (uint)Foundation.MaxPhysicsBarriers,
            numThreads = threadCount
        };

        return new(in config);
    }

    /// <summary>Advances the simulation by one step.</summary>
    /// <param name="deltaTime">How long the step is, in seconds. Must be positive.</param>
    /// <returns>What the step ran into.</returns>
    /// <remarks>
    ///     <para>
    ///         The delta must be the same every call for the simulation to be stable, let alone
    ///         reproducible. That is not enforced — a tool that wants to scrub a single long step is a
    ///         legitimate caller — but everything in the engine that steps this world does so from a
    ///         fixed-step accumulator.
    ///     </para>
    ///     <para>
    ///         Events raised during the step replace whatever the previous one left, so a caller who
    ///         does not drain <see cref="Contacts" /> before the next step loses them rather than
    ///         accumulating a list that grows for ever.
    ///     </para>
    /// </remarks>
    public PhysicsStepResult Step(float deltaTime) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deltaTime);

        lock (eventGate) {
            contacts.Clear();
            triggers.Clear();
            activations.Clear();
            deactivations.Clear();
        }

        var error = system.Update(deltaTime, Settings.CollisionStepsPerUpdate, jobs);
        StepCount++;

        LastStepResult = error switch {
            PhysicsUpdateError.None => PhysicsStepResult.Ok,
            PhysicsUpdateError.ManifoldCacheFull => PhysicsStepResult.ManifoldCacheFull,
            PhysicsUpdateError.BodyPairCacheFull => PhysicsStepResult.BodyPairCacheFull,
            PhysicsUpdateError.ContactConstraintsFull => PhysicsStepResult.ContactConstraintsFull,
            _ => PhysicsStepResult.Ok
        };

        return LastStepResult;
    }

    /// <summary>
    ///     Rebuilds the broad phase from scratch, which is worth doing once after a level is loaded
    ///     and never during play.
    /// </summary>
    /// <remarks>
    ///     Adding bodies one at a time leaves the bounding-volume tree in whatever shape the insertion
    ///     order produced. One call after the static geometry is in place typically halves broad-phase
    ///     time for the rest of the level's life; calling it per frame costs more than it saves by a
    ///     wide margin.
    /// </remarks>
    public void OptimizeBroadPhase() {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        system.OptimizeBroadPhase();
    }

    void OnContactAdded(
        PhysicsSystem physicsSystem,
        in Body body1,
        in Body body2,
        in ContactManifold manifold,
        ref ContactSettings settings
    ) =>
        RecordContact(ContactPhase.Began, body1, body2, in manifold);

    void OnContactPersisted(
        PhysicsSystem physicsSystem,
        in Body body1,
        in Body body2,
        in ContactManifold manifold,
        ref ContactSettings settings
    ) =>
        RecordContact(ContactPhase.Continued, body1, body2, in manifold);

    void RecordContact(ContactPhase phase, Body body1, Body body2, in ContactManifold manifold) {
        var first = new BodyHandle(body1.ID.ID);
        var second = new BodyHandle(body2.ID.ID);

        // Sensors never reach the solver, so a contact involving one is a trigger crossing and
        // nothing else. Reporting it as both would make every trigger fire two events.
        if (body1.IsSensor || body2.IsSensor) {
            var sensorIsFirst = body1.IsSensor;

            lock (eventGate) {
                // A sensor pair persists for as long as the overlap does; only the first step is an
                // entry. Continued is dropped rather than reported — see TriggerEvent.
                if (phase == ContactPhase.Began) {
                    triggers.Add(
                        new(
                            ContactPhase.Began,
                            sensorIsFirst ? first : second,
                            sensorIsFirst ? second : first
                        )
                    );
                }
            }

            return;
        }

        var position = manifold.PointCount > 0
            ? JoltMath.ToVixen(manifold.GetWorldSpaceContactPointOn1(0))
            : Vector3.Zero;

        var contact = new ContactEvent(
            phase,
            first,
            second,
            position,
            JoltMath.ToVixen(manifold.WorldSpaceNormal),
            manifold.PenetrationDepth
        );

        lock (eventGate) {
            contacts.Add(contact);
        }
    }

    void OnContactRemoved(PhysicsSystem physicsSystem, ref SubShapeIDPair pair) {
        var first = new BodyHandle(pair.Body1ID.ID);
        var second = new BodyHandle(pair.Body2ID.ID);

        // The bodies may already be gone — a contact is removed precisely because one of them was
        // destroyed as often as because they moved apart — so this reads the side table rather than
        // the body, which is why the table records IsSensor in the first place.
        var firstIsSensor = IsSensorSlot(first);
        var secondIsSensor = IsSensorSlot(second);

        lock (eventGate) {
            if (firstIsSensor || secondIsSensor) {
                triggers.Add(
                    new(
                        ContactPhase.Ended,
                        firstIsSensor ? first : second,
                        firstIsSensor ? second : first
                    )
                );
                return;
            }

            contacts.Add(new(ContactPhase.Ended, first, second, Vector3.Zero, Vector3.Zero, 0f));
        }
    }

    void OnBodyActivated(PhysicsSystem physicsSystem, in BodyID bodyId, ulong userData) {
        var handle = new BodyHandle(bodyId.ID);

        lock (eventGate) {
            activations.Add(handle);
        }
    }

    void OnBodyDeactivated(PhysicsSystem physicsSystem, in BodyID bodyId, ulong userData) {
        var handle = new BodyHandle(bodyId.ID);

        lock (eventGate) {
            deactivations.Add(handle);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Order is the whole content of this method. Characters hold bodies, bodies hold shapes, the
    ///     physics system holds all three, and the layer filters outlive the system that reads them
    ///     while it shuts down. Freeing any of them early is a native crash with no managed frame in
    ///     the stack.
    /// </remarks>
    public void Dispose() {
        if (IsDisposed) {
            return;
        }

        IsDisposed = true;

        foreach (var character in characters) {
            character.DisposeInternal();
        }

        characters.Clear();

        foreach (var constraint in constraints.Values) {
            system.RemoveConstraint(constraint.Native);
            constraint.Native.Dispose();
        }

        constraints.Clear();
        constraintOrder.Clear();

        system.OnContactAdded -= OnContactAdded;
        system.OnContactPersisted -= OnContactPersisted;
        system.OnContactRemoved -= OnContactRemoved;
        system.OnBodyActivated -= OnBodyActivated;
        system.OnBodyDeactivated -= OnBodyDeactivated;

        system.Dispose();
        jobs.Dispose();
        filters.Dispose();
        worldAnchorShape?.Dispose();

        // After the system, for the reason every line above it is where it is: a group filter is
        // referenced by every body that names one, and the bodies go with the system.
        DisposeFilterTables();

        // After the system: removing the last body reads its shape.
        Shapes.Dispose();

        queryLayerFilter.Dispose();
        queryBodyFilter.Dispose();

        JoltRuntime.Release();
    }

    /// <summary>What the world remembers about a body that the body itself cannot be asked for.</summary>
    /// <remarks>
    ///     Contact removal arrives after a body has been destroyed, so anything a removal event needs
    ///     has to be here rather than read back out of Jolt. The generation is what keeps a stale
    ///     index from reading a later body's data.
    /// </remarks>
    struct BodySlot {
        /// <summary>The whole handle that owns this slot, or <c>uint.MaxValue</c> when it is free.</summary>
        public uint Handle;

        public bool IsSensor;
        public PhysicsLayer Layer;
        public ShapeId Shape;
        public ulong UserData;

        /// <summary>This body's sub-group, or <see cref="NoSubGroup" /> if it is in no group.</summary>
        /// <remarks>
        ///     Handed out lazily by the first suppression that names the body, so a world that uses
        ///     none never builds a table and every body here reads the sentinel. ⚠ The sentinel is
        ///     <b>not</b> zero — see <see cref="NoSubGroup" />.
        /// </remarks>
        public int SubGroup;
    }

    bool IsSensorSlot(BodyHandle handle) {
        var index = handle.Index;
        return index < (uint)slots.Length && slots[index].Handle == handle.Value && slots[index].IsSensor;
    }
}
