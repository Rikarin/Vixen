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

    PlayerRig(World world, Entity controller, Entity pawn, PlayerCamera camera, ILogger logger) {
        this.world = world;
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

        Player.Possess(world, controller, pawn);

        // 4 m back, over the right shoulder. Bound to the controller, so a respawn re-aims it at the
        // new body with no code — which is the property the whole arrangement exists for.
        var camera = PlayerCameras.ThirdPerson(world, controller, distance: 4.5f, shoulderHeight: 1.5f);

        var rig = new PlayerRig(world, controller, pawn, camera, logger);

        rig.BindInput(services);
        rig.Sounds = GameSounds.Load(services, logger);
        rig.Dress(loop, arena, services);

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

            // The two halves of the camera rig: the shot's body and aim solve in PreRender, and the
            // director picks which shot the real camera is at. Both are ordinary systems and neither
            // knows a player exists.
            .Add(new VirtualCameraSystem())
            .Add(new CameraDirectorSystem());
    }

    /// <summary>A capsule that walks, at a spawn point.</summary>
    static Entity CreatePawn(World world, Arena arena, LocalTransform start) {
        // 1.8 m tall over a 0.3 m radius: half height is measured between the cap centres, so the
        // total is 2 × (0.6 + 0.3). ShapeOffset lifts the capsule's centre off the entity's origin,
        // which is at the character's feet — CharacterController.Position is the centre.
        var standing = arena.Physics.Shapes.Capsule(0.6f, 0.3f);
        var crouched = arena.Physics.Shapes.Capsule(0.25f, 0.3f);

        var pawn = Hierarchy.CreateTransform(world, start);

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

        return pawn;
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

        var hips = Part(assets, visuals, "player-torso", "PlayerTorso", new(0f, 0.98f, 0f));
        Part(assets, visuals, "player-head", "PlayerHead", new(0f, 1.62f, 0f));

        var armLeft = Part(assets, visuals, "player-arm", "PlayerArm", new(-0.29f, 1.55f, 0f));
        var armRight = Part(assets, visuals, "player-arm", "PlayerArm", new(0.29f, 1.55f, 0f));

        var legLeft = Part(assets, visuals, "player-leg", "PlayerLeg", new(-0.11f, 0.86f, 0f));
        var legRight = Part(assets, visuals, "player-leg", "PlayerLeg", new(0.11f, 0.86f, 0f));

        // The weapon hangs off the right arm, so aiming the arm aims the gun and nothing has to keep
        // two transforms in step.
        var weapon = Part(assets, armRight, "player-weapon", "PlayerWeapon", new(0f, -0.52f, 0.22f));

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

        loop.Behaviors.Add(Pawn, new WeaponFire { Muzzle = weapon, Physics = arena.Physics, Sounds = Sounds });
        loop.Behaviors.Add(Pawn, new RespawnWhenBelow { Floor = -8f, Controller = Controller });
    }

    /// <summary>One box of the body, parented and placed.</summary>
    Entity Part(AssetManager? assets, Entity parent, string model, string mesh, Vector3 offset) {
        var part = Hierarchy.CreateTransform(world, LocalTransform.At(offset));

        Hierarchy.SetParent(world, part, parent);
        world.Add(part, MeshRenderables.Default(GameModels.Reference(assets, GameModels.Address(model, mesh))));

        return part;
    }

    /// <inheritdoc />
    public void Dispose() {
        Actions?.Disable();
        Sounds.Dispose();
    }
}
