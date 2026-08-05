// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Physics.Ecs;

namespace Vixen.Water.Physics;

/// <summary>Floats every <see cref="BuoyancyBody" /> on the one surface everything else reads.</summary>
/// <remarks>
///     <para>
///         <b>[35 § D10](../../docs/plan/35-water.md#d10-buoyancy-is-pontoons-over-jolt-evaluated-at-the-fixed-steps-water-time),
///         and the whole of what this assembly is for.</b> Per fixed step, per pontoon: ask the
///         surface where it is, work out how much of the sphere is under it, and apply the force at
///         the pontoon's own world position — which is what makes a hull pitch when somebody stands at
///         the bow rather than bob level.
///     </para>
///     <para>
///         ⚠ <b>Before <see cref="PhysicsStepSystem" /> and after <see cref="PhysicsSyncSystem" />,
///         and both halves of that matter.</b> Jolt accumulates forces and clears them at the step, so
///         a force applied after the step is a force that is thrown away — a boat that sinks with the
///         system visibly running. And a force applied before the sync is a force on a body the sync
///         is about to create, which is the first frame of every boat lost.
///     </para>
///     <para>
///         ⚠ <b>It reads the simulation's water time off <see cref="IWaterSurface" /> and never its
///         own <c>GameTime</c>.</b> A force computed from a frame time changes when the frame rate
///         does, which in a networked game is a client and a server disagreeing about where a boat is
///         — [16](../../docs/plan/16-networking.md)'s determinism requirement applied to a force.
///         <c>WaterClockSystem</c> is what advances that clock, and it does so in
///         <see cref="SystemPhase.EarlyUpdate" /> precisely so that this can read it.
///     </para>
///     <para>
///         ⚠ <b>Jolt has a buoyancy impulse of its own and it is deliberately not used.</b> It takes a
///         <em>plane</em>, which is exactly the approximation a wave surface is not — and using it
///         would put a second definition of the water surface inside the physics engine, where § D2's
///         seam test cannot reach it.
///     </para>
///     <para>
///         ⚠ <b>Ripples are not passed, and the omission is the design.</b> The closed-form sum is
///         exact and answerable at any time; a ripple field is a simulation whose state <em>is</em> its
///         history, and a rollback re-simulating six ticks cannot ask it where the surface was. So the
///         force a server computes and the force a client predicts are the same function of the same
///         arguments. A wake that pushed a boat around would be a wake that desynced it.
///     </para>
/// </remarks>
/// <param name="scene">The physics world the forces go into.</param>
/// <param name="surface">Where the water is, and what time it is there.</param>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(PhysicsSyncSystem))]
[UpdateBefore(typeof(PhysicsStepSystem))]
public sealed class BuoyancySystem(PhysicsScene scene, IWaterSurface surface) : SystemBase, IDeclaredAccess {
    readonly PhysicsScene scene = scene ?? throw new ArgumentNullException(nameof(scene));

    readonly QueryDescription bodies = new QueryDescription().WithAll<BuoyancyBody, PhysicsBody>();

    readonly List<Entity> asked = [];

    BuoyancyForce[] forces = new BuoyancyForce[8];

    /// <summary>Where this step's wakes and splashes go, or null to produce none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>[35 § D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry)'s
    ///         wake and splash hooks, produced where the facts are.</b> A hull's speed, how much of it
    ///         is under, and the step it first touched water are all here and nowhere else — a system
    ///         that wanted to make spray would otherwise have to re-derive them from a transform,
    ///         which is a second opinion about whether a boat is moving.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One event with two consumers, which is § D2's rule applied once more.</b> A ripple
    ///         field turns a disturbance into an injection and <c>Vixen.Vfx</c> turns it into a burst
    ///         of spray; two producers would be a wake whose spray is not where the ripple is, and the
    ///         frame they stop agreeing on is the frame something changed in only one of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Producing them changes no force.</b> The solver reads no ripples — see the class
    ///         remarks — so a scene with the hooks wired and a scene without them simulate identically,
    ///         which is what keeps a predicted body predictable.
    ///     </para>
    /// </remarks>
    public WaterDisturbances? Disturbances { get; set; }

    /// <summary>How fast a pontoon has to move through water before it makes a wake, in m/s.</summary>
    /// <remarks>
    ///     ⚠ <b>Not zero.</b> A hull at rest still has a velocity of a few millimetres a second as the
    ///     solver settles it, and a wake emitted from that is a boat moored in a harbour throwing
    ///     spray for ever.
    /// </remarks>
    public float WakeSpeed { get; set; } = 0.75f;

    /// <summary>How fast a pontoon has to enter the water before it splashes, in m/s.</summary>
    public float SplashSpeed { get; set; } = 1.5f;

    /// <summary>Where the water is, and the clock it is at.</summary>
    /// <remarks>
    ///     ⚠ <b>Settable, because a dedicated server's answer is not a renderer.</b> On a client this
    ///     is <c>WaterZoneSystem</c>; on a headless build it is whatever folded the zones there. The
    ///     interface is the kernel's for exactly that reason — see <see cref="IWaterSurface" />.
    /// </remarks>
    public IWaterSurface Surface { get; set; } = surface ?? throw new ArgumentNullException(nameof(surface));

    /// <inheritdoc />
    /// <remarks>
    ///     Declared at construction rather than with attributes, for the reason <c>TransformSystem</c>
    ///     gives: naming a component type in a generic call is what assigns it an id, and on the first
    ///     frame an attribute would have nothing to look up.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>It declares no transform at all, and the absence is the point.</b> A pontoon is placed
    ///     from the <em>body's</em> pose, read out of the simulation — see <see cref="Apply" />.
    ///     Reading <c>WorldTransform</c> instead would be a pose one <c>TransformSystem</c> run old,
    ///     which in this phase is last frame's, and a boat would be floated where it was rather than
    ///     where it is.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PhysicsBody>()
        .Write<BuoyancyState>()
        .Build();

    /// <summary>How many bodies the last step floated — those with at least one wet pontoon.</summary>
    public int Floating { get; private set; }

    /// <summary>How many pontoons it evaluated, across every body.</summary>
    public int Pontoons { get; private set; }

    /// <summary>How many of those touched water.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero while <see cref="Pontoons" /> is not is the reading that says the water is
    ///     somewhere else.</b> It is the buoyancy equivalent of <c>ZonelessBodies</c>: a boat outside
    ///     every zone's window falls, and nothing about the falling says why.
    /// </remarks>
    public int WetPontoons { get; private set; }

    /// <summary>The forces the last body evaluated produced, for a debug draw.</summary>
    /// <remarks>
    ///     ⚠ <b>The <em>last</em> one, and this is scratch rather than a record.</b> It is what
    ///     <c>water.showBuoyancy</c> reads while it is stepping one selected body; keeping every
    ///     body's would be an array per body per step for a picture nobody is usually looking at, and
    ///     <see cref="BuoyancyState" /> is the five numbers that are worth keeping for all of them.
    /// </remarks>
    public ReadOnlySpan<BuoyancyForce> Forces => forces.AsSpan(0, LastCount);

    /// <summary>How many of <see cref="Forces" /> are the last body's.</summary>
    public int LastCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // The sync has just created bodies and pushed authored velocities in, and applying a force
        // is a native call the ECS cannot see into. Nothing scheduled may still be reading what it
        // writes.
        dependency.Complete();

        Step(context.World);

        return dependency;
    }

    /// <summary>Applies one step's worth of buoyancy.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test can step without standing up a runner.</remarks>
    public void Step(World world) {
        ArgumentNullException.ThrowIfNull(world);

        Floating = 0;
        Pontoons = 0;
        WetPontoons = 0;
        LastCount = 0;

        var gravity = scene.World.Gravity.Y;

        // ⚠ One entity at a time, and not a span. BuoyancyBody holds an array of pontoons, which
        // makes it a managed component: its values live in the world's store and the chunk holds
        // handles, so ReadValues would throw. The transforms and velocities beside it are unmanaged
        // and are read per entity here anyway, because the loop is already one at a time.
        asked.Clear();

        foreach (var chunk in world.Chunks(bodies)) {
            asked.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in asked) {
            Apply(world, entity, gravity);
        }
    }

    void Apply(World world, Entity entity, float gravity) {
        var authored = world.Read<BuoyancyBody>(entity);

        if (authored.Pontoons is not { Length: > 0 } pontoons) {
            return;
        }

        var body = world.Read<PhysicsBody>(entity).Handle;

        // ⚠ Out of the simulation and not out of a component. `WorldTransform` is written by
        // `TransformSystem`, which runs in LateUpdate — so in this phase it holds *last frame's*
        // pose, and a boat would be floated where it was rather than where it is. The one-frame lag
        // that produces is exactly the class of bug § D2's whole seam exists to prevent, and the
        // simulation already has the answer to hand.
        scene.World.GetTransform(body, out var position, out var rotation);

        // ⚠ Unit scale, and it is not an oversight: Jolt has no notion of a scaled body — a scaled
        // shape is baked into the shape itself — so a placement built with the entity's authored
        // scale would move the pontoons somewhere the collider is not.
        var placement = Matrix4x4.Compose(Vector3.One, rotation, position);
        var velocity = scene.World.GetLinearVelocity(body);

        Pontoons += pontoons.Length;

        // ⚠ The centre of the body, not of a pontoon — QueryAt picks a *zone*, and a hull is smaller
        // than a window by orders of magnitude. Asking per pontoon would be four containment walks
        // per step for an answer that differs only for a boat straddling two zones, which is the
        // authoring mistake QueryAt's own remarks refuse to resolve.
        var origin = position;

        if (Surface.QueryAt(new(origin.X, origin.Z)) is not { } query) {
            Dry(world, entity, pontoons.Length);

            return;
        }

        if (forces.Length < pontoons.Length) {
            forces = new BuoyancyForce[pontoons.Length];
        }

        var evaluator = query.Evaluator();

        // ⚠ No ripples. See the class remarks: the closed form is what a rollback can re-ask.
        var wet = Buoyancy.Solve(
            in evaluator,
            pontoons,
            in placement,
            velocity,
            gravity,
            authored.Settings,
            Surface.WaterTime,
            forces.AsSpan(0, pontoons.Length)
        );

        LastCount = pontoons.Length;
        WetPontoons += wet;

        var lift = 0f;
        var submerged = 0f;
        var was = world.Has<BuoyancyState>(entity) ? world.Read<BuoyancyState>(entity).Wet : 0;

        for (var index = 0; index < pontoons.Length; index++) {
            var force = forces[index];

            submerged += force.Submerged;

            if (force.Submerged <= 0f) {
                continue;
            }

            lift += force.Force.Y;
            Disturb(in force, velocity, entered: was == 0);

            // At the pontoon's own world position, which is what makes the hull pitch. A force at
            // the centre of mass would be a boat that bobs and never rolls.
            scene.World.ApplyForce(body, force.Force, force.Position);
        }

        if (wet > 0) {
            Floating++;
        }

        world.Set(
            entity,
            new BuoyancyState {
                Wet = wet,
                Total = pontoons.Length,
                Submerged = submerged / pontoons.Length,
                Lift = lift,
                SurfaceHeight = query.Height(new(origin.X, origin.Z), Surface.WaterTime)
            }
        );
    }

    /// <summary>Queues whatever this pontoon is doing to the surface, if anything.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Scaled by the submerged fraction, so a pontoon skimming the surface makes less
    ///         than one driving through it.</b> Without that, the loudest wake in a scene is the one
    ///         from a hull that is barely touching the water — because it is the one whose pontoon is
    ///         crossing the surface, and crossing is what a wake looks like from the outside.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A splash is the frame a body <em>arrives</em>, which is why the previous state is
    ///         read.</b> A rule on the vertical speed alone fires again every time a bobbing hull
    ///         crosses the surface, which is a crate dropped in a lake splashing four times before it
    ///         settles.
    ///     </para>
    /// </remarks>
    void Disturb(in BuoyancyForce force, Vector3 velocity, bool entered) {
        if (Disturbances is not { } queue) {
            return;
        }

        var lateral = new Vector2(velocity.X, velocity.Z).Length();
        var falling = -velocity.Y;

        if (entered && falling >= SplashSpeed) {
            queue.Add(
                new(
                    new(force.Position.X, force.Position.Z),
                    1f,
                    -falling * force.Submerged,
                    WaterDisturbanceKind.Splash,
                    force.SurfaceHeight
                )
            );

            return;
        }

        if (lateral >= WakeSpeed) {
            queue.Add(
                new(
                    new(force.Position.X, force.Position.Z),
                    0.75f,
                    -lateral * 0.2f * force.Submerged,
                    WaterDisturbanceKind.Wake,
                    force.SurfaceHeight
                )
            );
        }
    }

    /// <summary>Records a body no zone reaches, so the readout says "dry" rather than going stale.</summary>
    /// <remarks>
    ///     ⚠ <b>Written rather than left alone.</b> A boat that drifts out of every zone's window
    ///     keeps whatever it was floating at last, and a debug draw showing four wet pontoons on a
    ///     body in mid-air is worse than no draw at all.
    /// </remarks>
    static void Dry(World world, Entity entity, int total) =>
        world.Set(entity, new BuoyancyState { Wet = 0, Total = total, Submerged = 0f, Lift = 0f });
}
