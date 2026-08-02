// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Frames;
using Vixen.Engine.Renderer;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Physics;
using Vixen.Physics.Ecs;
using Vixen.Physics.Shapes;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

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
    /// <summary>How many lights one object's list has room for.</summary>
    /// <remarks>
    ///     More than the level has, deliberately: nineteen lights against a budget of eighteen would
    ///     still drop one, and a per-object list that drops anything at all in a scene whose lamps
    ///     flicker is a list whose membership churns every frame. See <see cref="Paint" />.
    /// </remarks>
    const int LightsPerObject = 24;

    /// <summary>
    ///     Whether the shading pass marches the clipmap for ambient occlusion.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Off, and off is a question rather than a decision.</b> Something on the ground
    ///         still turns when the camera turns, and the sun's cascade is no longer a candidate —
    ///         <c>ShadowCascadeTests.Orbiting_the_camera_moves_the_cascade_without_turning_it</c> holds
    ///         its basis fixed across an orbit to seven places. What is left that follows the camera is
    ///         this: <see cref="GlobalDistanceField" /> is a clipmap of four nested cubes <em>centred
    ///         on the viewer</em>, so its level boundaries are axis-aligned squares that sweep across
    ///         the floor as the camera orbits, and a point crossing one takes its distance from a
    ///         coarser grid.
    ///     </para>
    ///     <para>
    ///         With the march compiled out, the ambient term is the sky alone and nothing in the frame
    ///         reads the clipmap. If the moving shadows and the square blocks both go with it, the
    ///         cause is the level transition and the fix is to blend across it rather than switch —
    ///         which is a shader change. If they survive, this is eliminated too, which is worth as
    ///         much and is why the flag is a named constant instead of an edit somebody has to
    ///         remember to undo.
    ///     </para>
    /// </remarks>
    const bool MarchOcclusion = false;

    /// <summary>
    ///     How many cascades the sun's atlas holds. Must equal the document's <c>cascadeCount:</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One, and one is the second question.</b> Four cascades share one texture, so every
    ///     lookup projects through a matrix with a *tile* folded into it — and which tile a fragment
    ///     reads is chosen per fragment from its own view depth, which is the one thing left in the
    ///     shadow path that changes as the camera turns. At one cascade the fold is exactly the
    ///     identity and the viewport is the whole texture, so the tiling is absent rather than
    ///     simplified. Put both this and the document back to four once it is answered.
    /// </remarks>
    const int Cascades = 1;

    readonly ILogger logger;
    AppServices? services;

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

    /// <summary>The baked sky, and the two set-0 bindings this project has no pass for.</summary>
    public ArenaFrame? Frame { get; private set; }

    /// <summary>The virtualized path's page pool and traversal, or null.</summary>
    public VirtualGeometrySystem? Geometry { get; private set; }

    /// <summary>What compiles the shader variants this frame asks for, or null in a baked build.</summary>
    public DevelopmentEffects? Effects { get; private set; }

    /// <summary>What every object in the level is drawn with, or null if it would not compile.</summary>
    public Material? Material { get; private set; }

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

        arena.services = services;

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

        // ⚠ Before anything else, because without it the game runs, complains about nothing, and
        // draws a black screen. DevelopmentEffects says why at length.
        Effects = DevelopmentEffects.Attach(graphics.Effects, graphics.Device);

        if (Effects is null) {
            SampleLog.NoShaderLibrary(logger);
        }

        // Before Paint, because the permutation it sets and this have to be the same number — see the
        // note beside `ForwardPlusKeys.MaxLights` there for what the two of them are between them.
        graphics.Renderer.Lighting.MaxLightsPerObject = LightsPerObject;

        Paint(graphics);

        var builder = graphics.Renderer.Host.Builder;

        // Doc 19. The clipmap follows the camera and is composited from the distance fields the model
        // importer already wrote beside each mesh; the probe field covers the arena and a little air
        // above it, which is as much as a 64 m room needs.
        DistanceField = new();
        Irradiance = new(new BoundingBox(new(-34f, -2f, -34f), new(34f, 14f, 34f)), new Int3(17, 4, 17));

        builder.DistanceField = DistanceField;
        builder.IrradianceField = Irradiance;

        // ⚠ Set 0's other bindings. The document's !IrradianceField node fills five volumes and a
        // sampler once it names ForwardPlus among its passes, and the !ShadowMap node and the pass
        // that reads its atlas fill the shadow pair; this fills the sky, the probe array that falls
        // back to it, and the cluster list the project has no dispatch for. Between them the pass's
        // per-frame set is complete, which is the difference between a picture and a window full of
        // clear colour — see ArenaFrame.
        //
        // Baked from the level's own sun, so the sky and the light are one fact. SunFromSky below is
        // the other half.
        Frame = ArenaFrame.Bake(graphics.Device, SunDirection());
        Frame.Apply(graphics.Renderer.SceneEnvironment, graphics.Renderer.SceneBlock.Parameters);
        SunFromSky();

        // Uploaded by WorldRenderer.Draw before the first pass, rather than from a game's OnRender
        // which runs after the scene is already recorded.
        graphics.Renderer.Environment = Frame.Sky;

        // Doc 22. The traversal, the page pool and the visibility buffer, all owned here because a
        // document can name a pass and cannot own a device resource.
        Geometry = new(graphics.Device);
        Geometry.Supply(builder);

        // The screen passes are not added here: OnConfigure put PostEffectFactory in
        // GraphicsOptions.Factories, because the first build of this document happened before this
        // method existed and would have thrown without it. The builder keeps them across a reload.

        if (services.Assets?.Load<GraphicsCompositorAsset>(ThirdPersonShooterGame.CompositorAddress).Result is
            { } document) {
            graphics.Renderer.Host.Load(document);
            SampleLog.FrameRebuilt(logger, ThirdPersonShooterGame.CompositorAddress);
        }

        // After the reload, because a stage is one of the things the document builds — the object
        // this held before it is not the one the frame is drawing with. ShadowCaster's opacity map,
        // its sampler and its bone palette live here rather than on a material because no material
        // has a name for any of them: see ArenaFrame.ApplyCaster.
        if (builder.Stages.TryGetValue("Shadow", out var caster)) {
            Frame.ApplyCaster(caster);
        }

        // And the sky node, for the same reason and on the same terms: the cube is baked before the
        // frame graph exists and shared by every frame, so no document can name it — the host hands
        // it over. Nothing else in the document needs it, because the shading passes read the same
        // cube out of set 0 where the scene's lighting already put it.
        Frame.ApplySky(graphics.Renderer.Host);

        // And the clipmap's contents, which are what turn the occlusion march from a walk over
        // nothing into ambient occlusion. After the reload for the same reason the two above are:
        // the node this fills is made by CompositorBuilder and is a different object every time.
        if (services.Engine is { } engine) {
            FillDistanceField(graphics.Renderer.Host, engine.World);
        }
    }

    /// <summary>Bakes a field for every box the level authored and hands them to the clipmap.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this the whole occlusion path is a walk over an empty clipmap that returns
    ///         "nothing is near" for every sample.</b> The composition is bound, the march is
    ///         compiled, the bindings are filled, every counter reports success and the picture is
    ///         exactly the one with no occlusion in it. <c>GlobalDistanceField</c> is a
    ///         <em>composite</em> — it holds no geometry of its own and is assembled from the
    ///         instances the node is given.
    ///     </para>
    ///     <para>
    ///         <b>Baked analytically from <see cref="BoxCollision" /> rather than loaded from the
    ///         model importer's fields, and that is a decision this level earns.</b> Every solid thing
    ///         here is a box whose exact half-extents the scene already states — so the signed
    ///         distance has a closed form, and a closed form has no bake error, no resolution
    ///         artefacts at the corners and no asset to resolve. A level of real meshes would take the
    ///         importer's fields instead, and <c>DistanceFieldInstance</c> is the same either way.
    ///     </para>
    ///     <para>
    ///         The same query <see cref="BuildCollision" /> uses, so what occludes light and what
    ///         stops a character are one list. A wall the physics knows about and the light does not
    ///         is the bug this shape rules out.
    ///     </para>
    /// </remarks>
    void FillDistanceField(SceneRenderHost host, World world) {
        if (host.Builder.Nodes.Values.OfType<GlobalDistanceFieldRenderer>().FirstOrDefault() is not { } node) {
            return;
        }

        node.Instances.Clear();

        var query = new QueryDescription().WithAll<BoxCollision, LocalTransform>();

        foreach (var chunk in world.Chunks(query)) {
            var boxes = chunk.ReadValues<BoxCollision>();
            var transforms = chunk.ReadValues<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                var box = boxes[index];
                var half = Vector3.Max(box.HalfExtents, new(0.05f));

                node.Instances.Add(
                    new(
                        BoxField(half),
                        transforms[index].Position + Quaternion.Transform(box.Centre, transforms[index].Rotation),
                        transforms[index].Rotation,
                        1f
                    )
                );
            }
        }

        // What says the composite is out of date. The node compares this rather than the list, because
        // walking every instance every frame costs more than the comparison saves.
        node.InstancesVersion++;
        DistanceFieldInstances = node.Instances.Count;

        SampleLog.DistanceFieldsBuilt(logger, DistanceFieldInstances);
    }

    /// <summary>How many boxes the clipmap is composited from.</summary>
    public int DistanceFieldInstances { get; private set; }

    /// <summary>An exact signed-distance field for an axis-aligned box, in the box's own space.</summary>
    /// <remarks>
    ///     <para>
    ///         The standard closed form: the distance to a box is the length of the positive part of
    ///         <c>|p| − half</c>, plus the negative part's largest component for points inside. Exact
    ///         everywhere including the corners, which a rasterised bake is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bounds are the box plus a margin, and the margin is the whole point.</b> A
    ///         field that stopped at the surface would answer only where the answer is zero — an
    ///         occlusion march starts <em>outside</em> a surface and walks toward it, so what it needs
    ///         is the positive distances around the box. Two metres is a little over half the march's
    ///         own radius.
    ///     </para>
    /// </remarks>
    static MeshDistanceField BoxField(Vector3 half) {
        const float Margin = 2f;
        const float Cell = 0.5f;

        var bounds = new BoundingBox(-half - new Vector3(Margin), half + new Vector3(Margin));
        var size = bounds.Maximum - bounds.Minimum;

        // Clamped at both ends: a thin wall would otherwise get two texels across its thickness, and
        // the floor would get a grid nothing has the memory for.
        var resolution = new Int3(
            Math.Clamp((int)MathF.Ceiling(size.X / Cell) + 1, 5, 48),
            Math.Clamp((int)MathF.Ceiling(size.Y / Cell) + 1, 5, 48),
            Math.Clamp((int)MathF.Ceiling(size.Z / Cell) + 1, 5, 48)
        );

        var distances = new float[resolution.X * resolution.Y * resolution.Z];
        var step = new Vector3(
            size.X / (resolution.X - 1),
            size.Y / (resolution.Y - 1),
            size.Z / (resolution.Z - 1)
        );

        for (var z = 0; z < resolution.Z; z++) {
            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    var at = bounds.Minimum + new Vector3(x * step.X, y * step.Y, z * step.Z);
                    var outside = Vector3.Abs(at) - half;

                    var beyond = new Vector3(
                        MathF.Max(outside.X, 0f),
                        MathF.Max(outside.Y, 0f),
                        MathF.Max(outside.Z, 0f)
                    ).Length();

                    var within = MathF.Min(MathF.Max(outside.X, MathF.Max(outside.Y, outside.Z)), 0f);

                    distances[(z * resolution.Y * resolution.X) + (y * resolution.X) + x] = beyond + within;
                }
            }
        }

        return new(bounds, resolution, distances);
    }

    /// <summary>Which way the level's sun sends its light, from the level's own transform.</summary>
    /// <remarks>
    ///     The scene decides where the sun <em>is</em> and the atmosphere decides everything else
    ///     about it — see <see cref="SunFromSky" />. A level with no directional light gets a
    ///     late-afternoon default rather than a sky with no sun in it, because a black environment
    ///     cube is a scene with no ambient at all.
    /// </remarks>
    Vector3 SunDirection() {
        if (services?.Engine is not { } loop) {
            return Vector3.Normalize(new(-0.57f, -0.14f, 0.81f));
        }

        var query = new QueryDescription().WithAll<Light, LocalTransform>();

        foreach (var chunk in loop.World.Chunks(query)) {
            var lights = chunk.ReadValues<Light>();
            var transforms = chunk.ReadValues<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                if (lights[index].Kind is LightKind.Directional) {
                    return Vector3.Normalize(
                        Quaternion.Transform(Vector3.Forward, transforms[index].Rotation)
                    );
                }
            }
        }

        return Vector3.Normalize(new(-0.57f, -0.14f, 0.81f));
    }

    /// <summary>Gives the level's sun the brightness and the colour its own elevation implies.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written back onto the light rather than left to the scene file, and that is the
    ///         whole demonstration.</b> A scene that names a sun direction <i>and</i> a sun brightness
    ///         can be a sunset sky over a noon sun, and nothing anywhere reports it — the two are only
    ///         related by whoever typed them. Here the direction is authored and the illuminance and
    ///         the tint come out of the same air mass the sky above was computed for, so moving the
    ///         sun down reddens the horizon, dims the disc and warms the key light in one edit.
    ///     </para>
    ///     <para>
    ///         The scene file carries plausible values anyway, so the level reads sensibly in an
    ///         editor that is not running the game. They are the ones this computes.
    ///     </para>
    /// </remarks>
    void SunFromSky() {
        if (services?.Engine is not { } loop || Frame is not { } frame) {
            return;
        }

        var query = new QueryDescription().WithAll<Light>();
        var illuminance = frame.SunIlluminance;
        var tint = frame.SunTint;

        foreach (var chunk in loop.World.Chunks(query)) {
            var lights = chunk.Values<Light>();

            for (var index = 0; index < chunk.Count; index++) {
                if (lights[index].Kind is not LightKind.Directional) {
                    continue;
                }

                lights[index].Unit = LightUnit.Lux;
                lights[index].Intensity = illuminance;
                lights[index].Colour = tint;

                // Zero rather than a temperature, because the tint above already *is* the atmosphere's
                // answer — extinction along the sun's path, per channel. Multiplying a black-body
                // colour into it would be two different accounts of why a low sun is orange.
                lights[index].Temperature = 0f;
            }
        }

        SampleLog.SunFromSky(logger, illuminance, tint.R, tint.G, tint.B);
    }

    /// <summary>Gives every object in the level something to be drawn with.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second half of the black screen, and the half that logs nothing at all.</b>
    ///         <c>MeshExtractionSystem.Painted</c> falls back to <c>Extraction.Material</c> whenever a
    ///         <c>MeshRenderable</c> names no material of its own — which every object here does,
    ///         because <c>.obj</c> carries no material a build could compile. Nothing sets that
    ///         fallback, so it is null, so every object extracts with no shader, asks for no variant
    ///         and draws nothing. The effect system reports zero misses, because a miss is a variant
    ///         that was <i>asked</i> for.
    ///     </para>
    ///     <para>
    ///         <b>The permutation table is the other half of the same wiring.</b> An effect key is
    ///         built from the keys registered under the shader's name, so a <c>ForwardPlus</c> material
    ///         with no entry resolves to the default variant — which is the unlit one. Registering the
    ///         generated <c>UsedPermutationKeys</c> is what makes the lights in the level reach the
    ///         shader that reads them.
    ///     </para>
    /// </remarks>
    void Paint(AppGraphics graphics) {
        // Which shader fills the forward pass's irradiance slot is the project's decision rather
        // than the material's — doc 19's probe field is what this project put there.
        var slots = new Dictionary<string, string>(StringComparer.Ordinal) {
            [MaterialCompiler.ForwardIrradianceSlot] = MaterialCompiler.IrradianceFieldShader,

            // And the clipmap behind the forward pass's field slot, which is what gives every
            // material an ambient occlusion term. The same shader the !DistanceFieldAo node names —
            // one field, marched by whoever needs it — and the reason it is marched *here* is that
            // occlusion belongs on indirect light alone, which is a distinction a forward pass has
            // already thrown away by the time any screen-space pass could run.
            [MaterialCompiler.ForwardDistanceFieldSlot] = "GlobalDistanceField",

            // And the shadow atlas behind the forward pass's punctual slot, which is what stops the
            // floodlights outside the houses lighting the inside of their far walls. The atlas has
            // been rendered every frame since this document had a !PunctualShadows node in it and
            // nothing in Raven/Library could sample one, so every spot and point light in the level
            // lit straight through geometry — a lamp two rooms away is a plausible amount of light,
            // which is why it reads as a lighting setup problem rather than as a missing feature.
            //
            // ⚠ One gate rather than the occlusion march's two: the neutral filler compiles to `1f`
            // and declares nothing, so naming the real one here is the whole switch. What it does
            // still need is the document's `!PunctualShadows passes:` line, which fills the three
            // bindings this composition brings into set 0.
            [MaterialCompiler.ForwardPunctualShadowSlot] = MaterialCompiler.PunctualShadowShader
        };

        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",

                // One grey metal-roughness surface for whatever names no material of its own. Nine
                // .vxmat files cover the level and the character, so in a normal run this is drawn
                // exactly never — it is what a renderable with a broken reference gets.
                Features = [new MetalRoughnessFeature { BaseColor = new(0.62f, 0.63f, 0.66f), Metalness = 0f, Roughness = 0.7f }]
            },
            slots
        );

        if (compilation.Failed || compilation.Material is not { } material) {
            SampleLog.NoMaterial(
                logger,
                string.Join("; ", compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
            );

            return;
        }

        // ⚠ The project's, not this material's, and that distinction was a level with no shadows in
        // it. Every one of these is a fact about the *frame* — is there a shadow atlas, is there an
        // environment cube, is the probe field filled — so it is true of the nine .vxmat files and
        // of this fallback at once. Set here only, it reached the one material a renderable with no
        // reference falls back to, which in this level is none of them: the frame rendered four
        // cascades into an atlas that every draw was compiled not to sample.
        //
        // AssetMaterialSource.Permutations is where a project says it once.
        var permutations = new ParameterCollection();

        permutations.Set(ForwardPlusKeys.UseImageBasedLighting, true);
        permutations.Set(ForwardPlusKeys.UseReflectionProbe, true);

        // Off. Nothing fills this project's field — there is no filler on the node — and `Ambient`
        // in ClusteredShading blends the field over the sky on the field's own coverage rather than
        // adding, so a full-coverage answer of zero would replace a sky that is working.
        permutations.Set(ForwardPlusKeys.UseIrradianceField, false);

        // On, and this is the ambient occlusion the level had none of. It marches the clipmap the
        // !GlobalDistanceField node composites, at the shading point, and multiplies the answer into
        // `d.occlusion` — which `Ambient` reads and which `Direct` and `Punctual` never see. So a
        // corner darkens because less sky reaches it and a lamp three metres away still lights it,
        // which is the difference between occlusion and dirt.
        //
        // ⚠ Three things and none of them works alone: this compiles the march, `slots` above binds
        // the field behind the slot, and the document's `!GlobalDistanceField passes:` line fills the
        // bindings that composition declares. Two out of three is either a march that reads nothing
        // or a set that is written short — and a set written short is every draw in the pass refused.
        permutations.Set(ForwardPlusKeys.UseDistanceFieldOcclusion, MarchOcclusion);

        // ⚠ **The other half of `cascadeCount:` in the document, and neither works alone.** This
        // sizes `cascades[]` in the shader's block and the node decides how many of them it fills, so
        // the two disagreeing is a fragment selecting a slot nobody wrote — a matrix of zeroes, which
        // projects every fragment to the same point and shadows the level with a single texel.
        //
        // They agreed at four by both defaulting to it, which is the kind of agreement that holds
        // until somebody changes one. It is written down on both sides now.
        permutations.Set(ForwardPlusKeys.CascadeCount, Cascades);

        // On. The last thing in the way was the caster pipeline: ShadowCaster's vertex stage declares
        // bone indices and weights whatever its skinning permutation says, and SurfaceVertex has
        // neither — so no vertex layout satisfied it and the driver refused every caster. Raven now
        // drops a stage input the folded variant never reads, and the vertex layout is matched to
        // each effect's reflection by name rather than to ForwardPlus' locations, so the caster
        // declares the three attributes a static mesh has and binds them where it reads them.
        permutations.Set(ForwardPlusKeys.UseShadows, true);

        // ⚠ **Twenty-four rather than the shader's sixteen and the feature's eight, and this is the
        // blinking.** The level has nineteen lights; a per-object list keeps the eight *brightest*
        // that reach an object and drops the rest, and the floor is one 64 m box — so every lamp is
        // inside its bounding sphere, `Score` collapses to intensity alone, and the ranking is fifteen
        // floodlights of 110–150 klm in a row. `LampFlicker` then swings each of them ±12% out of
        // phase, so which eight win changes *every frame*: a lamp's whole contribution to the floor
        // switches on and off while the wall beside it, whose nearest lamp always wins, stands still.
        //
        // With room for all of them nothing is dropped, so nothing can churn — which is a scene-sized
        // answer rather than a fix. `ForwardLightingRenderFeature.Select` says the real one is
        // clustered lighting, where a fragment finds its own lights in a grid and no per-object budget
        // exists to overflow. This project binds an empty froxel buffer and compiles the clustered
        // permutation off, so that door is not open here yet.
        //
        // Both numbers or neither: `MaxLights` sizes the array *in the shader's block* and
        // `MaxLightsPerObject` sizes the block the feature *writes*. The shader reading past what the
        // host filled is stale memory shaded as though it were a light.
        permutations.Set(ForwardPlusKeys.MaxLights, LightsPerObject);

        material.Parameters.Apply(permutations);
        Material = material;

        if (graphics.Renderer.Extraction is { } extraction) {
            extraction.Material = material;
        }

        if (graphics.Renderer.Painter is AssetMaterialSource painter) {
            painter.Slots = slots;
            painter.Permutations = permutations;
        }

        graphics.Renderer.Materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
    }

    /// <summary>What the frame actually drew, for a headless leg that has to assert on something.</summary>
    /// <remarks>
    ///     ⚠ <b>The counter that catches a black screen.</b> Everything else this project logs is
    ///     true of a game that renders nothing: the level loads, the collision is built, the player
    ///     walks. What a frame draws is objects × resolved shader variants, and a variant that does
    ///     not resolve is a miss which draws nothing at all — so a nonzero miss count, or a zero
    ///     object count, is the difference between a picture and a black window. Nothing else in the
    ///     run says so, which is exactly how it went unnoticed.
    /// </remarks>
    public void ReportFrame() {
        if (services?.Graphics is not { } graphics) {
            return;
        }

        var meshes = graphics.Renderer.Source as AssetMeshSource;

        SampleLog.FrameSummary(
            logger,
            graphics.Renderer.Extraction?.ObjectCount ?? 0,
            meshes?.Loaded ?? 0,
            meshes?.Failed ?? 0,
            graphics.Effects.Count,
            graphics.Effects.MissCount,
            graphics.Renderer.Materials.BoundCount
        );

        // ⚠ The counter that catches the failure the frame summary above cannot. Every number there
        // is true of a frame whose draws were all refused: the objects extracted, the meshes loaded,
        // the variant resolved, the material set written. What decides whether any of it reached a
        // pixel is whether set 0 bound — EffectSetWriter fills it whole or not at all — so a
        // WriteCount that never leaves zero is the black screen, stated.
        var scene = graphics.Renderer.SceneBlock;

        SampleLog.SceneSetSummary(logger, scene.WriteCount, scene.IsComplete ? "complete" : "short of a binding");

        if (scene.MissingBinding is { } binding) {
            SampleLog.SceneSetMissing(logger, binding);
        }

        // ⚠ The third thing a black frame can be, after "no variant" and "no set 0": a camera that
        // was never filled. Every counter above is true of a frame drawn through an identity
        // view-projection, in which the whole level sits outside the unit cube and clips away.
        SampleLog.CameraSummary(logger, graphics.View.Position, graphics.View.ViewProjection);

        var sent = graphics.Renderer.ViewBlock.BytesFor(graphics.View);

        SampleLog.ViewBlockSummary(
            logger,
            sent.Length,
            sent.Length >= 64
                ? System.Runtime.InteropServices.MemoryMarshal.Read<System.Numerics.Matrix4x4>(sent)
                : default
        );

        if (services.Engine is { } engine) {
            var renderables = Count(engine.World, new QueryDescription().WithAll<MeshRenderable>());
            var placed = Count(engine.World, new QueryDescription().WithAll<MeshRenderable, WorldTransform>());
            var lit = Count(engine.World, new QueryDescription().WithAll<Light>());
            var litPlaced = Count(engine.World, new QueryDescription().WithAll<Light, WorldTransform>());

            SampleLog.LevelSummary(logger, renderables, placed, lit, litPlaced);
        }

        var lighting = graphics.Renderer.SceneEnvironment;
        var sky = lighting.Environment;

        SampleLog.LightingSummary(
            logger,
            graphics.Renderer.Lighting.Lights.Count,
            graphics.Renderer.Lighting.Sun is { } sun ? sun.Radiance.ToString() : "none",
            sky?.Irradiance.L00 ?? default,
            sky?.Intensity ?? 0f,
            lighting.Bound,
            lighting.Slots
        );

        SampleLog.VolumeSummary(
            logger,
            graphics.Volumes.ContributingCount,
            graphics.Volumes.VolumeCount,
            graphics.Volumes.Overlay.IsEmpty ? "says nothing" : "has an opinion"
        );

        var geometry = graphics.Renderer.Geometry;

        SampleLog.GeometrySummary(
            logger,
            geometry.UsedVertices,
            geometry.UsedIndices,
            geometry.SliceCount,
            graphics.Renderer.UploadedBytes
        );

        var objects = graphics.Renderer.Host.System.Objects;
        var worlds = objects.Data.Data(graphics.Renderer.Transforms.World);

        SampleLog.TransformSummary(
            logger,
            objects.Count,
            objects.Count > 0 ? worlds[0].Translation : default,
            objects.Count > 1 ? worlds[1].Translation : default,
            graphics.Renderer.Meshes.DrawCount,
            graphics.Renderer.Meshes.IndexCount
        );
    }

    /// <summary>How many entities a query matches.</summary>
    static int Count(World world, in QueryDescription query) {
        var total = 0;

        foreach (var chunk in world.Chunks(query)) {
            total += chunk.Count;
        }

        return total;
    }

    /// <inheritdoc />
    public void Dispose() {
        // ⚠ Only two of the four own anything disposable. A GlobalDistanceField and an
        // IrradianceField hold device resources through the render graph rather than directly, which
        // is why neither is IDisposable and why writing one here was a compiler error rather than a
        // leak nobody noticed.
        // Before the sky goes, because the renderer would otherwise keep uploading a texture whose
        // handles have been destroyed.
        if (services?.Graphics is { } graphics) {
            graphics.Renderer.Environment = null;
        }

        Frame?.Dispose();
        Geometry?.Dispose();
        Physics.Dispose();
    }
}
