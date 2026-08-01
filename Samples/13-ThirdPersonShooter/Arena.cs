// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Frames;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Physics;
using Vixen.Physics.Ecs;
using Vixen.Physics.Shapes;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>The level: what was loaded, what it collides with, and what lights it.</summary>
/// <remarks>
///     <para>
///         Three things that have to happen in one place because they are one decision. The scene
///         asset says where everything is; the boxes it carries become collision; and the frame the
///         project named needs field objects only a host can own before its global-illumination
///         nodes do anything at all.
///     </para>
/// </remarks>
public sealed class Arena : IDisposable {
    readonly ILogger logger;

    Arena(PhysicsScene physics, SceneHandle scene, ILogger logger) {
        Physics = physics;
        Scene = scene;
        this.logger = logger;
    }

    /// <summary>The bodies, the shapes and the characters.</summary>
    public PhysicsScene Physics { get; }

    /// <summary>What the level was loaded as, and what would unload it.</summary>
    public SceneHandle Scene { get; }

    /// <summary>How many entities got a collider out of their authored box.</summary>
    public int ColliderCount { get; private set; }

    /// <summary>How many point lights the level placed, each now breathing.</summary>
    public int LampCount { get; private set; }

    /// <summary>The camera-following clipmap every distance-field trace marches, or null.</summary>
    public GlobalDistanceField? DistanceField { get; private set; }

    /// <summary>The probe field carrying the bounced light, or null.</summary>
    public IrradianceField? Irradiance { get; private set; }

    /// <summary>The virtualized path's page pool and traversal, or null.</summary>
    public VirtualGeometrySystem? Geometry { get; private set; }

    /// <summary>Loads the level and stands up everything that reads it.</summary>
    /// <param name="services">What the host built.</param>
    /// <returns>The level.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <exception cref="InvalidOperationException">There is no engine to load into.</exception>
    public static Arena Load(AppServices services) {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Engine is not { } loop || services.Scenes is not { } scenes) {
            throw new InvalidOperationException(
                "Arena.Load needs an engine and a scene manager. AppConfig.UseEngine is off, which for this "
                + "project would mean a game with no world to put a level in."
            );
        }

        var logger = services.LoggerFactory.CreateLogger("ThirdPersonShooter.Arena");
        var physics = new PhysicsScene(loop.World);
        var handle = SceneHandle.None;

        if (services.Assets is { } assets) {
            // Blocking, and deliberately: there is nothing to show until the level is there, and a
            // frame drawn over a half-loaded scene is a worse answer than a slower start.
            if (assets.Load<SceneAsset>(ThirdPersonShooterGame.SceneAddress).Result is { } asset) {
                handle = asset.Load(scenes);
                SampleLog.SceneLoaded(logger, asset.Name, asset.Content.Count);
            } else {
                SampleLog.NoScene(logger, ThirdPersonShooterGame.SceneAddress);
            }
        } else {
            SampleLog.NoContent(logger);
        }

        var arena = new Arena(physics, handle, logger);

        arena.BuildCollision(loop.World);
        arena.SupplyFrame(services);

        return arena;
    }

    /// <summary>Adds the level's systems to the loop.</summary>
    /// <param name="loop">The frame loop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    public void Register(EngineLoop loop) {
        ArgumentNullException.ThrowIfNull(loop);

        // Sync, step, characters, writeback — the four in AddPhysics, in the phases their attributes
        // put them in. A game never orders them by hand, which is the point of them having phases.
        loop.AddPhysics(Physics);

        LightTheLamps(loop);
    }

    /// <summary>Puts a <see cref="LampFlicker" /> on every point light the level placed.</summary>
    /// <remarks>
    ///     <b>The second of the two ways a behaviour is attached.</b> <c>PlayerRig</c> hangs its
    ///     behaviours off entities it made and holds the handles of; this one has no handles at all —
    ///     the level made these lights and the game has never seen them before. A query is the only
    ///     thing that can find them, which is what makes "every point light in the level" expressible
    ///     without the level knowing that a game exists.
    /// </remarks>
    void LightTheLamps(EngineLoop loop) {
        var query = new QueryDescription().WithAll<Light, LocalTransform>();
        var lamps = new List<Entity>();

        foreach (var chunk in loop.World.Chunks(query)) {
            var lights = chunk.ReadValues<Light>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (lights[index].Kind is LightKind.Point) {
                    lamps.Add(entities[index]);
                }
            }
        }

        for (var index = 0; index < lamps.Count; index++) {
            // A different phase each, so seven lamps read as seven filaments rather than as one
            // switch being flipped. Derived from the index because a sample must not be random: two
            // runs of `--vixen-frames 8` have to produce the same frames.
            loop.Behaviors.Add(lamps[index], new LampFlicker { Offset = index * 0.9f });
        }

        LampCount = lamps.Count;
    }

    /// <summary>Turns every authored <see cref="BoxCollision" /> into a registered shape and a collider.</summary>
    /// <remarks>
    ///     ⚠ <b>Collected and then applied.</b> Adding a component is a structural change, and a
    ///     structural change during a chunk walk moves entities between archetypes underneath the walk
    ///     — so the query runs to completion first and the world is written afterwards. This is the
    ///     same rule <c>PossessionSystem</c> follows and the same one it explains.
    /// </remarks>
    void BuildCollision(World world) {
        var query = new QueryDescription().WithAll<BoxCollision>().WithNone<Collider>();
        var pending = new List<(Entity Entity, BoxCollision Box)>();

        foreach (var chunk in world.Chunks(query)) {
            var boxes = chunk.ReadValues<BoxCollision>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                pending.Add((entities[index], boxes[index]));
            }
        }

        foreach (var (entity, box) in pending) {
            var half = Vector3.Max(box.HalfExtents, new(0.01f));
            var solid = Physics.Shapes.Box(half);

            // A compound of one, which is how a box gets an offset: Collider has no local transform
            // of its own, because a shape's placement is a property of the shape and duplicating it
            // in the component would give two answers whenever they disagreed.
            var shape = box.Centre == Vector3.Zero
                ? solid
                : Physics.Shapes.Compound([new CompoundChild(solid, box.Centre, Quaternion.Identity)]);

            world.Add(entity, Collider.Of(shape) with { Friction = box.Friction > 0f ? box.Friction : 0.4f });
            ColliderCount++;
        }

        SampleLog.CollisionBuilt(logger, ColliderCount, Physics.Shapes.Count);
    }

    /// <summary>
    ///     Hands the frame the objects a document cannot create, then rebuilds it against them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The reload is the whole method.</b> <c>AppGraphics</c> builds the compositor in its
    ///         own constructor, which runs before <c>Game.OnInitialise</c> — so at the moment the
    ///         document's <c>!GlobalDistanceField</c> and <c>!IrradianceField</c> nodes were built,
    ///         the builder's field slots were empty and each node captured a null. A node with no
    ///         field does nothing rather than throwing, which is the right behaviour for a shared
    ///         document and exactly what makes this failure invisible: the frame draws, unlit by
    ///         anything indirect, and nothing says why.
    ///     </para>
    ///     <para>
    ///         <c>SceneRenderHost.Load</c> documents itself as callable again, and the swapchain image
    ///         is imported per frame in <c>AppGraphics.Begin</c> rather than once at start-up — so
    ///         rebuilding here costs one graph build and loses nothing.
    ///     </para>
    /// </remarks>
    void SupplyFrame(AppServices services) {
        if (services.Graphics is not { } graphics) {
            return;
        }

        var builder = graphics.Renderer.Host.Builder;

        // Doc 19. The clipmap follows the camera and is composited from the distance fields the model
        // importer already wrote beside each mesh; the probe field covers the arena and a little air
        // above it, which is as much as a 64 m room needs.
        DistanceField = new();
        Irradiance = new(new BoundingBox(new(-34f, -2f, -34f), new(34f, 14f, 34f)), new Int3(17, 4, 17));

        builder.DistanceField = DistanceField;
        builder.IrradianceField = Irradiance;

        // Doc 22. The traversal, the page pool and the visibility buffer, all owned here because a
        // document can name a pass and cannot own a device resource.
        Geometry = new(graphics.Device);
        Geometry.Supply(builder);

        // And the screen passes, which CompositorBuilder cannot switch on itself: Vixen.Rendering.PostFx
        // is downstream of it, so a case there would be a reference cycle.
        builder.Factories.Add(new Rendering.PostFx.PostEffectFactory());

        if (services.Assets?.Load<GraphicsCompositorAsset>(ThirdPersonShooterGame.CompositorAddress).Result is
            { } document) {
            graphics.Renderer.Host.Load(document);
            SampleLog.FrameRebuilt(logger, ThirdPersonShooterGame.CompositorAddress);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        // ⚠ Only two of the four own anything disposable. A GlobalDistanceField and an
        // IrradianceField hold device resources through the render graph rather than directly, which
        // is why neither is IDisposable and why writing one here was a compiler error rather than a
        // leak nobody noticed.
        Geometry?.Dispose();
        Physics.Dispose();
    }
}
