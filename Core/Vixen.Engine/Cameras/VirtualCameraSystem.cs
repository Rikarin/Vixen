// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Cameras;

/// <summary>
///     Evaluates every virtual camera: where it would be, where it would look, and how it would
///     shake.
/// </summary>
/// <remarks>
///     <para>
///         <b>Which stage a shot uses is an archetype question, not a branch.</b> Each stage is a
///         sweep over the chunks that have that stage's component — the follow bodies are one
///         monomorphic pass over a contiguous column, the composers are another — and a shot with no
///         body at all is matched by a query with <c>WithNone</c> over all four of them and takes its
///         position from its own transform. That is the same shape <c>TransformSystem</c> uses for
///         its roots, and it is the reason this is not a <c>switch</c> over an enum with a union of
///         settings behind it.
///     </para>
///     <para>
///         <b>The stages are passes inside one system rather than systems of their own.</b> Their
///         order <i>is</i> the design — a body before an aim, because an aim needs somewhere to look
///         from; the confiner and the obstacle avoider between them, because a camera that has been
///         moved must still look at its subject from where it ended up; the shake last, because
///         nothing downstream may damp against it. Expressed as eleven systems with
///         <c>UpdateAfter</c> attributes, that order would be eleven separate things to get wrong,
///         and every one of them would conflict on <see cref="CameraShot" /> and serialise anyway.
///     </para>
///     <para>
///         <b>Every shot is evaluated every frame, live or not.</b> Cinemachine makes this a setting
///         per camera and pays for it with shots that lurch when they come on, because their damping
///         is resuming from wherever they were left. A shot is a few dozen floating-point operations
///         and a scene has tens of them; the setting is not worth the class of bug it opens.
///     </para>
///     <para>
///         It runs in <see cref="SystemPhase.LateUpdate" />, which is where that phase's own
///         documentation puts "cameras that follow" — after everything has moved, before anything is
///         culled. Targets are read through <see cref="Hierarchy.ResolveWorldMatrix" /> rather than
///         out of <c>WorldTransform</c>, because the transform pass has not run yet this frame and a
///         camera aimed at last frame's position of its subject is a camera that lags by a frame.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class VirtualCameraSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription unstaged = new QueryDescription().WithAll<VirtualCamera>().WithNone<CameraShot>();

    readonly QueryDescription untargeted = new QueryDescription()
        .WithAll<VirtualCamera>()
        .WithNone<CameraTargets>();

    readonly QueryDescription staged = new QueryDescription().WithAll<VirtualCamera, CameraShot>();

    readonly QueryDescription bodyless = new QueryDescription()
        .WithAll<VirtualCamera, CameraShot>()
        .WithNone<FollowBody, FramingBody, OrbitBody, HardLockBody>();

    readonly QueryDescription aimless = new QueryDescription()
        .WithAll<VirtualCamera, CameraShot>()
        .WithNone<ComposerAim, HardLookAim, PovAim, MatchTargetAim>();

    readonly QueryDescription follows = new QueryDescription()
        .WithAll<CameraShot, CameraTargets, FollowBody>();

    readonly QueryDescription framings = new QueryDescription()
        .WithAll<CameraShot, CameraTargets, FramingBody>();

    readonly QueryDescription orbits = new QueryDescription().WithAll<CameraShot, CameraTargets, OrbitBody>();

    readonly QueryDescription hardLocks = new QueryDescription()
        .WithAll<CameraShot, CameraTargets, HardLockBody>();

    readonly QueryDescription confiners = new QueryDescription().WithAll<CameraShot, CameraConfiner>();

    readonly QueryDescription occluders = new QueryDescription()
        .WithAll<CameraShot, CameraTargets, CameraOcclusion>();

    readonly QueryDescription composers = new QueryDescription()
        .WithAll<CameraShot, CameraTargets, ComposerAim>();

    readonly QueryDescription hardLooks = new QueryDescription()
        .WithAll<CameraShot, CameraTargets, HardLookAim>();

    readonly QueryDescription povs = new QueryDescription().WithAll<CameraShot, PovAim>();

    readonly QueryDescription matches = new QueryDescription().WithAll<CameraShot, CameraTargets, MatchTargetAim>();

    readonly QueryDescription noises = new QueryDescription().WithAll<CameraShot, CameraNoise>();

    readonly QueryDescription listeners = new QueryDescription().WithAll<CameraShot, CameraImpulseListener>();

    readonly List<Entity> pending = [];

    double unscaledSeconds;

    /// <summary>The impulses every listening shot feels. Aged once a frame by this system.</summary>
    public CameraImpulses Impulses { get; } = new();

    /// <summary>Width over height of the frame the shots are being composed for.</summary>
    /// <remarks>
    ///     Set by the host from its swapchain, once, and again when the window is resized. It is a
    ///     property of the surface being rendered to rather than of any one shot, which is why it is
    ///     not on <see cref="CameraLens" /> — a dead zone that meant a different part of the picture
    ///     on an ultrawide monitor than on a phone would be a framing decision made by the hardware.
    /// </remarks>
    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Which way is up, for every stage that has to keep a horizon level.</summary>
    public Vector3 WorldUp { get; set; } = Vector3.UnitY;

    /// <summary>What answers whether a shot's view of its subject is blocked, or nothing.</summary>
    /// <remarks>
    ///     Nothing by default, and a <see cref="CameraOcclusion" /> on a shot then does nothing at
    ///     all rather than throwing. See that component for why the engine cannot answer the question
    ///     itself.
    /// </remarks>
    public ICameraOcclusion? Occlusion { get; set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<VirtualCamera>()
        .Read<CameraTargets>()
        .Read<LocalTransform>()
        .Read<Parent>()
        .Read<WorldTransform>()
        .Read<FollowBody>()
        .Read<FramingBody>()
        .Read<OrbitBody>()
        .Read<HardLockBody>()
        .Read<CameraConfiner>()
        .Read<ComposerAim>()
        .Read<HardLookAim>()
        .Read<PovAim>()
        .Read<MatchTargetAim>()
        .Read<CameraNoise>()
        .Read<CameraImpulseListener>()
        .Write<CameraShot>()
        .Write<CameraOcclusion>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Evaluate(context.World, context.Time, context.Commands);
        return dependency;
    }

    /// <summary>Runs every stage over every shot in the world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <param name="commands">
    ///     Where the components the engine owns are attached, or <see langword="null" /> to attach
    ///     them at once.
    /// </param>
    /// <remarks>
    ///     Public so a test, a tool or an editor's preview can evaluate without standing up a runner
    ///     — the same reason <c>TransformSystem.Resolve</c> is.
    /// </remarks>
    public void Evaluate(World world, GameTime time, CommandBuffer? commands = null) {
        ArgumentNullException.ThrowIfNull(world);

        Attach(world, commands);

        var deltaTime = time.DeltaSeconds;
        unscaledSeconds += time.UnscaledDeltaSeconds;

        Impulses.Advance(deltaTime);

        Seed(world);
        Bodies(world, deltaTime);
        Confine(world, deltaTime);
        Avoid(world, deltaTime);
        Aims(world, deltaTime);
        Shake(world, time);
        Settle(world);
    }

    /// <summary>Gives every shot the components the engine writes, if it has not got them.</summary>
    /// <remarks>
    ///     A shot placed by a scene carries its settings and nothing else — <see cref="CameraShot" />
    ///     is derived state and <see cref="CameraTargets" /> holds entity handles, and neither can be
    ///     in a file (each says why). So they are attached here, and a shot created any way at all
    ///     works without its creator having remembered to.
    /// </remarks>
    void Attach(World world, CommandBuffer? commands) {
        Collect(world, unstaged);

        foreach (var entity in pending) {
            if (commands is null) {
                world.Add(entity, default(CameraShot));
            } else {
                commands.Add(entity, default(CameraShot));
            }
        }

        Collect(world, untargeted);

        foreach (var entity in pending) {
            if (commands is null) {
                world.Add(entity, default(CameraTargets));
            } else {
                commands.Add(entity, default(CameraTargets));
            }
        }
    }

    void Collect(World world, QueryDescription description) {
        pending.Clear();

        foreach (var chunk in world.Chunks(description)) {
            pending.AddRange(chunk.Entities);
        }
    }

    /// <summary>Clears last frame's shake, takes this frame's lens, and places what nothing else will.</summary>
    void Seed(World world) {
        foreach (var chunk in world.Chunks(staged)) {
            var cameras = chunk.ReadValues<VirtualCamera>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var shot = ref shots[index];
                shot.Lens = cameras[index].Lens;
                shot.ShakePosition = Vector3.Zero;
                shot.ShakeRotation = Quaternion.Identity;

                if (!shot.HasHistory) {
                    // A zeroed CameraShot has a zero quaternion, which is not a rotation. It happens
                    // to behave as the identity in the one formula that would read it before an aim
                    // stage overwrites it, and depending on that is how a stage added later finds a
                    // degenerate rotation nobody thought could reach it.
                    shot.Rotation = Quaternion.Identity;
                }
            }
        }

        // A shot with no body sits where its entity does — which is what makes a hand-placed
        // establishing shot work with no components on it beyond the one that says it is a shot.
        foreach (var chunk in world.Chunks(bodyless)) {
            var shots = chunk.Values<CameraShot>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                shots[index].Position = Hierarchy.ResolveWorldMatrix(world, entities[index]).Translation;
            }
        }

        foreach (var chunk in world.Chunks(aimless)) {
            var shots = chunk.Values<CameraShot>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var matrix = Hierarchy.ResolveWorldMatrix(world, entities[index]);

                shots[index].Rotation = Matrix4x4.Decompose(matrix, out _, out var rotation, out _)
                    ? rotation
                    : Quaternion.Identity;
            }
        }

        // A shot that has a body but no aim, or the other way about, has one half seeded from its
        // transform and the other half left holding last frame's damped value. Both are correct: the
        // seeded half is not damped against anything, and the other half is about to be overwritten.
    }

    void Bodies(World world, float deltaTime) {
        Follow(world, deltaTime);
        Frame(world, deltaTime);
        Orbit(world, deltaTime);
        HardLock(world);
    }

    void Follow(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(follows)) {
            var bodies = chunk.ReadValues<FollowBody>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!TryResolve(world, targets[index].Follow, out var position, out var rotation)) {
                    continue;
                }

                ref var shot = ref shots[index];
                var body = bodies[index];
                var basis = BindingBasis(body.Binding, rotation, position, shot.Position);
                var ideal = position + Quaternion.Transform(body.Offset, basis);

                shot.Position = DampTowards(in shot, ideal, basis, body.Damping, deltaTime);
            }
        }
    }

    /// <summary>The frame an offset and its damping times are measured in.</summary>
    Quaternion BindingBasis(CameraBinding binding, Quaternion targetRotation, Vector3 target, Vector3 camera) {
        switch (binding) {
            case CameraBinding.World:
                return Quaternion.Identity;

            case CameraBinding.TargetRotation:
                return targetRotation;

            case CameraBinding.TargetHeading: {
                // The target's facing, flattened onto the horizontal plane. LookRotation puts local
                // −Z on it, so the offset's +Z is the target's back — which is what "behind" means.
                var forward = Flatten(Quaternion.Transform(Vector3.Forward, targetRotation));
                return forward.LengthSquared() > MathUtil.ZeroTolerance
                    ? Transform.LookRotation(Vector3.Normalize(forward), WorldUp)
                    : targetRotation;
            }

            case CameraBinding.SimpleFollow: {
                // The direction the camera already lies in. Nothing the target does can turn the
                // camera, so a player orbiting with the stick is never fought by the follow.
                var behind = Flatten(camera - target);
                return behind.LengthSquared() > MathUtil.ZeroTolerance
                    ? Transform.LookRotation(-Vector3.Normalize(behind), WorldUp)
                    : Quaternion.Identity;
            }

            default:
                return Quaternion.Identity;
        }
    }

    Vector3 Flatten(Vector3 direction) => direction - (WorldUp * Vector3.Dot(direction, WorldUp));

    /// <summary>Damps a position towards an ideal, in the axes of a given frame.</summary>
    static Vector3 DampTowards(
        in CameraShot shot,
        Vector3 ideal,
        Quaternion basis,
        Vector3 damping,
        float deltaTime
    ) {
        if (!shot.HasHistory) {
            return ideal;
        }

        var error = Quaternion.Transform(shot.Position - ideal, Quaternion.Conjugate(basis));
        return ideal + Quaternion.Transform(CameraDamping.Decay(error, damping, deltaTime), basis);
    }

    void Frame(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(framings)) {
            var bodies = chunk.ReadValues<FramingBody>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!TryResolve(world, targets[index].Follow, out var position, out _)) {
                    continue;
                }

                ref var shot = ref shots[index];
                var body = bodies[index];
                var subject = position + body.TrackedOffset;

                // The rotation is the one the aim stage left last frame; see FramingBody's remarks
                // for why the body is allowed to be one frame behind the aim and the reverse is not.
                if (!CameraFraming.Project(
                        subject,
                        shot.Position,
                        shot.Rotation,
                        in shot.Lens,
                        AspectRatio,
                        out var screen,
                        out var depth
                    )) {
                    continue;
                }

                var extents = CameraFraming.Extents(in shot.Lens, AspectRatio);
                var scale = shot.Lens.Orthographic ? Vector3.One : new Vector3(depth, depth, 1f);

                // Moving the camera right by d takes the subject d to the left across the frame, so
                // the correction along each axis is the screen overshoot scaled back into metres.
                var lateral = CameraFraming.Overshoot(screen.X, body.ScreenPosition.X, body.DeadZone.X)
                    * extents.X
                    * scale.X;

                var vertical = CameraFraming.Overshoot(screen.Y, body.ScreenPosition.Y, body.DeadZone.Y)
                    * extents.Y
                    * scale.Y;

                var wanted = body.Distance;

                if (body.MinimumDistance > 0f) {
                    wanted = MathF.Max(wanted, body.MinimumDistance);
                }

                if (body.MaximumDistance > 0f) {
                    wanted = MathF.Min(wanted, body.MaximumDistance);
                }

                var along = depth - wanted;
                var take = shot.HasHistory
                    ? new Vector3(
                        CameraDamping.Fraction(body.Damping.X, deltaTime),
                        CameraDamping.Fraction(body.Damping.Y, deltaTime),
                        CameraDamping.Fraction(body.Damping.Z, deltaTime)
                    )
                    : Vector3.One;

                var move = new Vector3(lateral * take.X, vertical * take.Y, -along * take.Z);
                shot.Position += Quaternion.Transform(move, shot.Rotation);
            }
        }
    }

    void Orbit(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(orbits)) {
            var bodies = chunk.ReadValues<OrbitBody>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!TryResolve(world, targets[index].Follow, out var position, out _)) {
                    continue;
                }

                ref var shot = ref shots[index];
                var body = bodies[index];
                var pivot = position + body.PivotOffset;

                // Spherical coordinates written out rather than composed from quaternions, because
                // the two Euler orders that could produce this differ by exactly the case that
                // matters — a heading applied about the world's up rather than the tilted one.
                var cosPitch = MathF.Cos(body.Pitch);

                var direction = new Vector3(
                    cosPitch * MathF.Sin(body.Heading),
                    MathF.Sin(body.Pitch),
                    cosPitch * MathF.Cos(body.Heading)
                );

                var ideal = pivot + (direction * body.Radius);

                // The orbit's own frame: +Z outward along the radius, +X tangential, +Y vertical.
                var basis = Transform.LookRotation(-direction, WorldUp);

                shot.Position = DampTowards(in shot, ideal, basis, body.Damping, deltaTime);
            }
        }
    }

    void HardLock(World world) {
        foreach (var chunk in world.Chunks(hardLocks)) {
            var bodies = chunk.ReadValues<HardLockBody>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!TryResolve(world, targets[index].Follow, out var position, out var rotation)) {
                    continue;
                }

                var body = bodies[index];

                shots[index].Position = position
                    + (body.InTargetSpace ? Quaternion.Transform(body.Offset, rotation) : body.Offset);
            }
        }
    }

    void Confine(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(confiners)) {
            var bounds = chunk.ReadValues<CameraConfiner>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var shot = ref shots[index];
                var confiner = bounds[index];
                var clamped = Vector3.Clamp(shot.Position, confiner.Minimum, confiner.Maximum);

                shot.Position = shot.HasHistory
                    ? Vector3.Lerp(shot.Position, clamped, CameraDamping.Fraction(confiner.Damping, deltaTime))
                    : clamped;
            }
        }
    }

    void Avoid(World world, float deltaTime) {
        if (Occlusion is null) {
            return;
        }

        foreach (var chunk in world.Chunks(occluders)) {
            var settings = chunk.Values<CameraOcclusion>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                var target = targets[index].LookAt.IsNull ? targets[index].Follow : targets[index].LookAt;

                if (!TryResolve(world, target, out var subject, out _)) {
                    continue;
                }

                ref var shot = ref shots[index];
                ref var occluder = ref settings[index];
                var offset = shot.Position - subject;
                var distance = offset.Length();

                if (distance <= MathUtil.ZeroTolerance) {
                    continue;
                }

                var direction = offset / distance;
                var wanted = distance;

                if (Occlusion.Occluded(subject, shot.Position, occluder.Radius, out var hit)) {
                    wanted = MathF.Max(
                        MathF.Min(Vector3.Distance(subject, hit), distance),
                        occluder.MinimumDistance
                    );
                }

                // Pulling in and easing out are different speeds on purpose; see the component.
                var damping = wanted < occluder.Applied ? occluder.PullInDamping : occluder.PullOutDamping;

                occluder.Applied = shot.HasHistory
                    ? CameraDamping.Approach(occluder.Applied, wanted, damping, deltaTime)
                    : wanted;

                shot.Position = subject + (direction * MathF.Min(occluder.Applied, distance));
            }
        }
    }

    void Aims(World world, float deltaTime) {
        Compose(world, deltaTime);
        HardLook(world);
        Pov(world, deltaTime);
        Match(world, deltaTime);
    }

    void Compose(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(composers)) {
            var aims = chunk.ReadValues<ComposerAim>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!TryResolve(world, targets[index].LookAt, out var position, out _)) {
                    continue;
                }

                ref var shot = ref shots[index];
                var aim = aims[index];
                var subject = position + aim.TrackedOffset;

                // A subject behind the camera has no screen position at all, so there is nothing to
                // damp towards and the framing starts from a hard look instead. It happens on the
                // frame a target is assigned and after a teleport, and both want a snap.
                if (!shot.HasHistory
                    || !CameraFraming.Project(
                        subject,
                        shot.Position,
                        shot.Rotation,
                        in shot.Lens,
                        AspectRatio,
                        out var screen,
                        out _
                    )) {
                    shot.Rotation = Look(subject - shot.Position, shot.Rotation);
                    continue;
                }

                var extents = CameraFraming.Extents(in shot.Lens, AspectRatio);

                var yaw = -Turn(
                    screen.X,
                    aim.ScreenPosition.X,
                    aim.DeadZone.X,
                    aim.SoftZone.X,
                    extents.X,
                    aim.HorizontalDamping,
                    deltaTime
                );

                // A subject to the right of where it belongs is answered by turning the camera
                // right, which is a negative rotation about the up axis in a right-handed frame —
                // hence the sign above, and its absence here, where up is up.
                var pitch = Turn(
                    screen.Y,
                    aim.ScreenPosition.Y,
                    aim.DeadZone.Y,
                    aim.SoftZone.Y,
                    extents.Y,
                    aim.VerticalDamping,
                    deltaTime
                );

                // Yaw about the world's up and pitch about the camera's own right, so that framing
                // never accumulates roll. Roll is CameraLens.Dutch and is applied elsewhere.
                var turned = Quaternion.FromAxisAngle(Vector3.UnitX, pitch) * shot.Rotation;
                shot.Rotation = Quaternion.Normalize(turned * Quaternion.FromAxisAngle(WorldUp, yaw));
            }
        }
    }

    /// <summary>One axis of a composer: the angle to turn through this frame.</summary>
    static float Turn(
        float screen,
        float centre,
        float deadZone,
        float softZone,
        float tangent,
        float dampTime,
        float deltaTime
    ) {
        var error = CameraFraming.TurnToEdge(screen, centre, deadZone, tangent, out var edge);

        if (error == 0f) {
            return 0f;
        }

        // How much of the error may be left un-corrected: the angle between the dead zone's edge and
        // the soft zone's, which is the promise the damping is not allowed to break.
        var soft = MathF.Max(MathF.Abs(softZone), MathF.Abs(deadZone));
        _ = CameraFraming.TurnToEdge(screen, centre, soft, tangent, out var limit);
        var slack = MathF.Atan(limit * tangent) - MathF.Atan(edge * tangent);

        return CameraFraming.Correction(error, slack, dampTime, deltaTime);
    }

    void HardLook(World world) {
        foreach (var chunk in world.Chunks(hardLooks)) {
            var aims = chunk.ReadValues<HardLookAim>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                if (!TryResolve(world, targets[index].LookAt, out var position, out _)) {
                    continue;
                }

                ref var shot = ref shots[index];
                shot.Rotation = Look(position + aims[index].TrackedOffset - shot.Position, shot.Rotation);
            }
        }
    }

    void Pov(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(povs)) {
            var aims = chunk.ReadValues<PovAim>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var shot = ref shots[index];
                var aim = aims[index];
                var pitch = MathUtil.Clamp(aim.Pitch, aim.MinimumPitch, aim.MaximumPitch);
                var cosPitch = MathF.Cos(pitch);

                // The direction two angles name, built directly. Composing the library's yaw-pitch
                // helper would apply the pitch about the world's X rather than the turned one, which
                // is the same rotation only while the yaw is zero.
                var forward = new Vector3(
                    -cosPitch * MathF.Sin(aim.Yaw),
                    MathF.Sin(pitch),
                    -cosPitch * MathF.Cos(aim.Yaw)
                );

                var wanted = Transform.LookRotation(forward, WorldUp);

                shot.Rotation = shot.HasHistory
                    ? CameraDamping.Approach(shot.Rotation, wanted, aim.Damping, deltaTime)
                    : wanted;
            }
        }
    }

    void Match(World world, float deltaTime) {
        foreach (var chunk in world.Chunks(matches)) {
            var aims = chunk.ReadValues<MatchTargetAim>();
            var targets = chunk.ReadValues<CameraTargets>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                var target = targets[index].Follow.IsNull ? targets[index].LookAt : targets[index].Follow;

                if (!TryResolve(world, target, out _, out var rotation)) {
                    continue;
                }

                ref var shot = ref shots[index];

                shot.Rotation = shot.HasHistory
                    ? CameraDamping.Approach(shot.Rotation, rotation, aims[index].Damping, deltaTime)
                    : rotation;
            }
        }
    }

    void Shake(World world, GameTime time) {
        foreach (var chunk in world.Chunks(noises)) {
            var profiles = chunk.ReadValues<CameraNoise>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var shot = ref shots[index];
                var noise = profiles[index];

                if (noise.Gain == 0f) {
                    continue;
                }

                var seconds = noise.Unscaled ? unscaledSeconds : time.TotalSeconds;

                shot.ShakePosition += CameraNoiseSignal.Sample(seconds, noise.PositionFrequency, 0, noise.Seed)
                    * noise.PositionAmplitude
                    * noise.Gain;

                var angles = CameraNoiseSignal.Sample(seconds, noise.RotationFrequency, 3, noise.Seed)
                    * noise.RotationAmplitude
                    * noise.Gain;

                shot.ShakeRotation = Quaternion.FromYawPitchRoll(angles.Y, angles.X, angles.Z)
                    * shot.ShakeRotation;
            }
        }

        if (Impulses.Count == 0) {
            return;
        }

        foreach (var chunk in world.Chunks(listeners)) {
            var gains = chunk.ReadValues<CameraImpulseListener>();
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                ref var shot = ref shots[index];
                var displacement = Impulses.Sample(shot.Position);

                if (displacement.LengthSquared() <= 0f) {
                    continue;
                }

                var gain = gains[index];
                var local = CameraFraming.ToViewSpace(displacement, shot.Rotation);
                shot.ShakePosition += local * gain.PositionGain;

                if (gain.RotationGain == 0f) {
                    continue;
                }

                // The camera swings the way it was shoved: about the axis across the push and the
                // view direction. In view space the view direction is −Z, so the axis is a cross
                // product with a constant and the whole thing is three multiplies.
                var axis = Vector3.Cross(Vector3.Forward, local);

                if (axis.LengthSquared() <= MathUtil.ZeroTolerance) {
                    continue;
                }

                shot.ShakeRotation = Quaternion.FromAxisAngle(
                        Vector3.Normalize(axis),
                        local.Length() * gain.RotationGain
                    )
                    * shot.ShakeRotation;
            }
        }
    }

    /// <summary>Marks every shot as having a previous state, now that it has one.</summary>
    void Settle(World world) {
        foreach (var chunk in world.Chunks(staged)) {
            var shots = chunk.Values<CameraShot>();

            for (var index = 0; index < chunk.Count; index++) {
                shots[index].HasHistory = true;
            }
        }
    }

    /// <summary>Where a target is and which way it faces, as of now rather than as of the last pass.</summary>
    static bool TryResolve(World world, Entity entity, out Vector3 position, out Quaternion rotation) {
        if (entity.IsNull || !world.IsAlive(entity)) {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            return false;
        }

        var matrix = Hierarchy.ResolveWorldMatrix(world, entity);
        position = matrix.Translation;
        rotation = Matrix4x4.Decompose(matrix, out _, out var decomposed, out _) ? decomposed : Quaternion.Identity;
        return true;
    }

    /// <summary>The rotation that looks along a direction, or the current one if there is no direction.</summary>
    Quaternion Look(Vector3 direction, Quaternion current) =>
        direction.LengthSquared() > MathUtil.ZeroTolerance
            ? Transform.LookRotation(Vector3.Normalize(direction), WorldUp)
            : current;
}
