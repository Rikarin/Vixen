// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Cameras;
using Vixen.Engine.Frames;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Vixen.Rendering.Ecs;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>One player: a controller, a body, a camera, an input map and a set of behaviours.</summary>
/// <remarks>
///     <para>
///         <b>The order matters and the reason is doc 29's.</b> The controller is made first and
///         outlives everything else here — it holds the aim, the seat and the camera binding, so a
///         death destroys the pawn and the camera keeps looking where the player was looking. The
///         pawn is second, the possession third, and the camera is bound to the <i>controller</i>
///         rather than to the body it happens to be watching.
///     </para>
///     <para>
///         ⚠ <b>The visuals are children of the pawn and the collision is not.</b> The capsule is
///         what physics sweeps; the boxes hanging off it are what a person sees. Turning the capsule
///         to face where the player is running would change what the sweep hits, which is a physical
///         consequence of a cosmetic decision — so <see cref="CharacterVisuals" /> carries the facing
///         and the body never rotates.
///     </para>
/// </remarks>
public sealed class PlayerRig : IDisposable {
    readonly ILogger logger;
    readonly World world;
    readonly PhysicsScene physics;

    PlayerRig(World world, PhysicsScene physics, Entity controller, Entity pawn, PlayerCamera camera, ILogger logger) {
        this.world = world;
        this.physics = physics;
        this.logger = logger;

        Controller = controller;
        Pawn = pawn;
        Camera = camera;
    }

    /// <summary>The thing that is the player. It outlives the pawn.</summary>
    public Entity Controller { get; }

    /// <summary>The body being driven.</summary>
    public Entity Pawn { get; }

    /// <summary>The real camera and the shot driving it.</summary>
    public PlayerCamera Camera { get; }

    /// <summary>Reads the devices into the controller's intent.</summary>
    public PlayerInputSystem Input { get; } = new();

    /// <summary>Forwards that intent to the possessed pawn, and the aim to the camera.</summary>
    public PossessionSystem Possession { get; } = new();

    /// <summary>The action asset, kept because <c>InputService</c> reads it once a frame.</summary>
    public InputActions? Actions { get; private set; }

    /// <summary>Holds the pointer while the game has focus, so a look is not clamped by the desk.</summary>
    public MouseCaptureSystem? Capture { get; private set; }

    /// <summary>Where the game's sounds came from.</summary>
    public GameSounds Sounds { get; private set; } = GameSounds.Silent;

    /// <summary>Builds a player into a loaded level.</summary>
    /// <param name="services">What the host built.</param>
    /// <param name="arena">The level, for its shape registry and its spawn points.</param>
    /// <returns>The rig.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">There is no engine to build into.</exception>
    public static PlayerRig Spawn(AppServices services, Arena arena) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(arena);

        if (services.Engine is not { } loop) {
            throw new InvalidOperationException("PlayerRig.Spawn needs an engine to build into.");
        }

        var logger = services.LoggerFactory.CreateLogger("ThirdPersonShooter.Player");
        var world = loop.World;
        var start = ThirdPersonShooterGame.SpawnPointAt(world, 0);

        // Seat zero. The slot is what a second local player would differ by, and the channel that
        // follows from it is what decides which camera the renderer's one view is filled from.
        var controller = Player.Create(world, slot: 0);
        var pawn = CreatePawn(world, arena, start);

        // ⚠ The spawn point's *rotation* goes here and nowhere else, and it took a bug to find the
        // "nowhere else". Which way a player faces when they come in is a fact about the player, so
        // it belongs on the thing that holds their aim — and the aim is what the movement basis, the
        // camera's heading and the character's facing are all derived from, so one write turns all
        // three. Spawn0 asks for 45°, and until this line the level said so and nothing listened.
        world.Get<ControlRotation>(controller).Yaw = YawOf(start.Rotation);

        Player.Possess(world, controller, pawn);

        // 4 m back, over the right shoulder. Bound to the controller, so a respawn re-aims it at the
        // new body with no code — which is the property the whole arrangement exists for.
        var camera = PlayerCameras.ThirdPerson(world, controller, distance: 4.5f, shoulderHeight: 1.5f);

        var rig = new PlayerRig(world, arena.Physics, controller, pawn, camera, logger);

        rig.BindInput(services);
        rig.Sounds = GameSounds.Load(services, logger);
        rig.Dress(loop, arena, services);

        // After BindInput, because the action it releases on comes off the map that loaded.
        rig.Capture = new(
            services.Window,
            rig.Actions?["Player"] is { } map && map.TryFind("Menu", out var menu) ? menu : null
        );

        SampleLog.PlayerSpawned(logger, 0, start.Position);
        return rig;
    }

    /// <summary>Adds the systems this player needs to the loop.</summary>
    /// <param name="loop">The frame loop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    public void Register(EngineLoop loop) {
        ArgumentNullException.ThrowIfNull(loop);

        loop.Add(Input)
            .Add(Possession)

            // Before the camera stages and after the devices are read, which is where its own phase
            // puts it. A headless run gets one with no window and it does nothing.
            .Add(Capture ?? new MouseCaptureSystem(null, null))

            // The two halves of the camera rig: the shot's body and aim solve in PreRender, and the
            // director picks which shot the real camera is at. Both are ordinary systems and neither
            // knows a player exists.
            //
            // `Occlusion` is the one line the engine cannot write: the shot already carries a
            // `CameraOcclusion`, and until something can answer "is this line blocked" that component
            // does nothing and the camera goes through the floor whenever the player looks up. See
            // ArenaCameraOcclusion — the seam exists because Vixen.Engine may not reference physics.
            .Add(new VirtualCameraSystem { Occlusion = new ArenaCameraOcclusion(physics) })
            .Add(new CameraDirectorSystem());
    }

    /// <summary>A capsule that walks, at a spawn point.</summary>
    static Entity CreatePawn(World world, Arena arena, LocalTransform start) {
        // 1.8 m tall over a 0.3 m radius: half height is measured between the cap centres, so the
        // total is 2 × (0.6 + 0.3). ShapeOffset lifts the capsule's centre off the entity's origin,
        // which is at the character's feet — CharacterController.Position is the centre.
        var standing = arena.Physics.Shapes.Capsule(0.6f, 0.3f);
        var crouched = arena.Physics.Shapes.Capsule(0.25f, 0.3f);

        // ⚠ The spawn point's position and *not* its rotation, which is the whole of a bug that read
        // as a character walking sideways. A capsule has no facing worth having — the class remark
        // above says the body never turns — so a yaw put here is never corrected by anything, and
        // the visuals hanging off it write a *local* rotation computed in *world* space. Spawn0 is
        // at 45°, so the avatar rendered exactly 45° round from wherever it was running, for as long
        // as it ran. See Spawn above for where that rotation goes instead.
        var pawn = Hierarchy.CreateTransform(world, LocalTransform.At(start.Position));

        world.Add(
            pawn,
            CharacterMovement.Default with {
                Shape = standing,
                CrouchShape = crouched,
                ShapeOffset = new(0f, 0.9f, 0f),
                CrouchShapeOffset = new(0f, 0.55f, 0f),
                WalkSpeed = 4.5f,
                SprintSpeed = 7.5f,

                // 1.1 m of clearance, which is a crate and a half. Written as the height a designer
                // measured rather than as the speed it implies, so the two cannot drift apart.
                JumpSpeed = CharacterMovement.JumpSpeedForHeight(1.1f, -19.62f)
            }
        );

        // What input, physics and — in a networked build — the wire all agree on. Nothing else is
        // shared between them, which is the whole of doc 29's argument for this component.
        world.Add(pawn, default(MoveIntent));

        // ⚠ **Without this the camera judders, and the character does not.** The character steps in
        // `SystemPhase.FixedUpdate` at a fixed rate and the frame is drawn at whatever rate the
        // display runs at, so a pawn with no `PhysicsInterpolation` holds one step's position for
        // however many frames fall inside that step and then jumps. Everything parented to it jumps
        // with it — including the camera, which is bolted to the pawn through `CameraTargets` — so
        // the whole world steps while the character stands still relative to it. That reads as a
        // jittery camera rather than as a stepping character, which is why it survived.
        //
        // `PhysicsScene` fills the two poses on every step and `PhysicsInterpolationSystem` draws
        // between them; the cost is that what is rendered is up to one step behind what the
        // simulation last decided, which is the correct trade for anything a person is looking at
        // and the wrong one for a hit test. Nothing here does a hit test against the pawn's
        // transform — `WeaponFire` casts from the muzzle through the physics world.
        // ⚠ Seeded, and `default` is a trap of exactly the kind this repo keeps finding. A zeroed
        // `PhysicsInterpolation` holds two poses at the world origin with two zero quaternions, and
        // the interpolation lerps between them and writes the result over the transform — so a pawn
        // given one at spawn is dragged to (0, 0, 0) and stays there until a step fills both poses.
        // The whole level's spawn points are 28 m from the origin, so it is not subtle; it is also
        // not a crash, and the character keeps walking.
        world.Add(
            pawn,
            new PhysicsInterpolation {
                PreviousPosition = start.Position,
                CurrentPosition = start.Position,
                PreviousRotation = Quaternion.Identity,
                CurrentRotation = Quaternion.Identity
            }
        );

        return pawn;
    }

    /// <summary>Which way a rotation faces, about the world's up axis.</summary>
    /// <remarks>
    ///     Through the forward vector rather than by unpacking the quaternion, because the answer has
    ///     to be in <see cref="ControlRotation" />'s convention — a yaw of zero looks down −Z — and
    ///     that convention lives in one place: <c>ControlRotation.Forward</c> builds this basis and
    ///     <c>MoveIntent.WorldDirection</c> builds it again. A rotation with pitch or roll in it
    ///     flattens rather than throwing, which is what a designer's slightly-off gizmo deserves.
    /// </remarks>
    static float YawOf(Quaternion rotation) {
        var forward = Quaternion.Transform(Vector3.Forward, rotation);

        return MathF.Abs(forward.X) + MathF.Abs(forward.Z) > MathUtil.ZeroTolerance
            ? MathF.Atan2(-forward.X, -forward.Z)
            : 0f;
    }

    /// <summary>Loads the action asset and points the controller at it.</summary>
    /// <remarks>
    ///     ⚠ <b>A missing map is a warning and not a throw.</b> A controller with no source has its
    ///     intent cleared and its aim preserved every frame, so the game runs and the player stands
    ///     still — which is a state somebody can look at, unlike a stack trace during boot.
    /// </remarks>
    void BindInput(AppServices services) {
        if (services.Assets is not { } assets) {
            SampleLog.NoInput(logger, ThirdPersonShooterGame.InputAddress, "this build shipped no content");
            return;
        }

        try {
            using var stream = assets.Open(ThirdPersonShooterGame.InputAddress);
            using var reader = new StreamReader(stream);

            var actions = InputActions.Load(reader.ReadToEnd(), "GameInput");

            actions.Enable();
            services.Input.Add(actions);

            Actions = actions;
            Input.Bind(Controller, new ActionPlayerInput(actions["Player"]));
        } catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException)) {
            SampleLog.NoInput(logger, ThirdPersonShooterGame.InputAddress, failure.Message);
        }
    }

    /// <summary>Hangs the meshes and the behaviours off the body.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Segmented rather than skinned, because <c>.obj</c> carries no rig.</b> Each part is
    ///         its own entity with its own <c>MeshRenderable</c>, parented into a small hierarchy that
    ///         a behaviour turns. That is how Quake 1 and every Lego character work, and swapping in a
    ///         rigged glTF later changes these lines and nothing else.
    ///     </para>
    /// </remarks>
    void Dress(EngineLoop loop, Arena arena, AppServices services) {
        var visuals = Hierarchy.CreateTransform(world, LocalTransform.Identity);

        Hierarchy.SetParent(world, visuals, Pawn);
        world.Add(visuals, default(CharacterVisuals));

        var assets = services.Assets;

        var hips = Part(assets, visuals, "player-torso", "PlayerTorso", new(0f, 0.98f, 0f), "player-body");
        Part(assets, visuals, "player-head", "PlayerHead", new(0f, 1.62f, 0f), "player-head");

        var armLeft = Part(assets, visuals, "player-arm", "PlayerArm", new(-0.29f, 1.55f, 0f), "player-body");
        var armRight = Part(assets, visuals, "player-arm", "PlayerArm", new(0.29f, 1.55f, 0f), "player-body");

        var legLeft = Part(assets, visuals, "player-leg", "PlayerLeg", new(-0.11f, 0.86f, 0f), "player-body");
        var legRight = Part(assets, visuals, "player-leg", "PlayerLeg", new(0.11f, 0.86f, 0f), "player-body");

        // The weapon hangs off the right arm, so aiming the arm aims the gun and nothing has to keep
        // two transforms in step.
        // ⚠ Negative Z, which is where the character is looking. Every offset above is symmetric about
        // the body's axis and so cannot tell a forward from a backward; this one can, and it had the
        // gun hanging out of the character's back for exactly as long as the facing was half a turn
        // out and the two errors agreed with each other.
        var weapon = Part(assets, armRight, "player-weapon", "PlayerWeapon", new(0f, -0.52f, -0.22f), "weapon");

        // The behaviours. Each is attached under its own static type, which is what gives the store a
        // bucket of exactly that type and a monomorphic loop over it.
        loop.Behaviors.Add(Pawn, new CharacterAnimation {
            Visuals = visuals,
            Hips = hips,
            ArmLeft = armLeft,
            ArmRight = armRight,
            LegLeft = legLeft,
            LegRight = legRight,
            Sounds = Sounds
        });

        Weapon = loop.Behaviors.Add(Pawn, new WeaponFire { Muzzle = weapon, Physics = arena.Physics, Sounds = Sounds });
        Respawn = loop.Behaviors.Add(Pawn, new RespawnWhenBelow { Floor = -8f, Controller = Controller });
    }

    /// <summary>One box of the body, parented, placed and painted.</summary>
    /// <remarks>
    ///     The material is named per part rather than left null, because null is the renderer's one
    ///     fallback and a character in it is the same grey as the wall behind them. Three materials
    ///     across seven parts — body, head, weapon — is what makes the pawn readable at the distance a
    ///     third-person camera actually sits at.
    /// </remarks>
    Entity Part(AssetManager? assets, Entity parent, string model, string mesh, Vector3 offset, string material) {
        var part = Hierarchy.CreateTransform(world, LocalTransform.At(offset));

        Hierarchy.SetParent(world, part, parent);

        world.Add(
            part,
            MeshRenderables.Default(GameModels.Reference(assets, GameModels.Address(model, mesh))) with {
                Material = GameModels.Reference(assets, GameModels.MaterialAddress(material))
            }
        );

        return part;
    }

    /// <summary>What the run did, for a headless leg that has to assert on something.</summary>
    /// <param name="frames">How many frames were drawn.</param>
    /// <remarks>
    ///     ⚠ <b>A frame count alone proves nothing.</b> "It started and stopped" is true of a game
    ///     that loaded no level, spawned nobody and simulated nothing — which is exactly the state
    ///     every failure in this project's history produced. The position is the useful number: the
    ///     player spawns at y = 0.2 and the floor's top is y = 0, so a character that is
    ///     <c>Walking</c> at roughly zero has been stepped, has found the collision the level
    ///     authored, and has settled on it.
    /// </remarks>
    public void Report(int frames) {
        var position = world.IsAlive(Pawn) && world.Has<LocalTransform>(Pawn)
            ? world.Read<LocalTransform>(Pawn).Position
            : default;

        var mode = world.IsAlive(Pawn) && world.Has<CharacterState>(Pawn)
            ? world.Read<CharacterState>(Pawn).Mode
            : CharacterMoveMode.Falling;

        SampleLog.RunSummary(
            logger,
            frames,
            position,
            mode,
            world.Has<CharacterState>(Pawn) && Weapon is { } weapon ? weapon.Shots : 0,
            Respawn?.Respawns ?? 0
        );
    }

    /// <summary>The weapon behaviour, kept so the run summary can read its counter.</summary>
    public WeaponFire? Weapon { get; private set; }

    /// <summary>The respawn behaviour, kept for the same reason.</summary>
    public RespawnWhenBelow? Respawn { get; private set; }

    /// <inheritdoc />
    public void Dispose() {
        Actions?.Disable();
        Sounds.Dispose();
    }
}
