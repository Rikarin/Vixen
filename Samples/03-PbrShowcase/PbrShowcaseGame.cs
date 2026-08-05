// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Ecs;
using Vixen.Shaders.Generated;

namespace Vixen.Samples.PbrShowcase;

/// <summary>A grid of materials under the standard frame: metallic up the grid, roughness across it.</summary>
/// <remarks>
///     <para>
///         Five rows of metallic against five columns of roughness, which is the arrangement that
///         makes a microfacet BRDF legible in one picture: along one axis a dielectric becomes a
///         metal, and along the other a mirror becomes a diffuse surface. Any mistake in the model
///         shows up as a row or a column behaving unlike its neighbours.
///     </para>
///     <para>
///         <b>This sample used to drive the RHI by hand, and its conversion is the point it makes
///         now.</b> The old form opened a <c>VulkanDevice</c>, declared two passes to the render
///         graph and owed docs/plan/14 Phase 5 its shadows and its post chain for as long as adding
///         them meant authoring a frame. The frame is now <c>Assets/Frame.vxcompositor</c> — seven
///         knobs, one up from the document <c>vixen new</c> writes — and the shadows, the
///         occlusion, the temporal resolve and the meter all arrived with it. What this
///         file keeps is only what a document cannot say: what exists (spheres, a floor, a sun, a
///         camera) and the host halves of the knobs, each marked ⚠ below. The RHI itself is still
///         worth reading about — that is <c>Samples/01</c>, on purpose.
///     </para>
///     <para>
///         <b>Headless is a supported way to run this.</b> <c>--vixen-frames 2</c> with no usable
///         device builds the whole frame from the document and stops; <see cref="OnInitialise" />
///         returning early with no engine is not a failure. That is what CI can hold, and the
///         pictures are what a person holds — the README says what to look for.
///     </para>
/// </remarks>
public sealed class PbrShowcaseGame : Game {
    const int Rows = 5;
    const int Columns = 5;

    /// <summary>Sphere spacing, centre to centre. Diameter is 0.9, so 0.3 of floor shows between.</summary>
    const float Spacing = 1.2f;

    /// <summary>What the frame is called in content, so the host loads it instead of its built-in.</summary>
    public const string CompositorAddress = "Assets/Frame.vxcompositor";

    /// <summary>Which way the sun's light travels — a mid-morning sun, high enough for short shadows.</summary>
    /// <remarks>
    ///     The one authored fact the whole of the lighting comes out of: the sky is baked from it,
    ///     the sun's illuminance and tint are read off that sky, and the light entity is aimed along
    ///     it — so the disc, the shadows and the ambient can never disagree about where the sun is.
    /// </remarks>
    static readonly Vector3 SunDirection = Vector3.Normalize(new(-0.35f, -0.65f, -0.4f));

    ILogger? log;
    GridMaterials? palette;
    DevelopmentEffects? effects;
    ShowcaseFrame? frame;
    World? world;
    Entity? camera;

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        config.Name = "PbrShowcase";
        config.Organisation = "Vixen";
        config.Window = new() { Title = "Vixen — PBR Showcase", Size = new(1280, 720), IsVisible = true };

        // The project's own frame — the document's seven knobs stand for everything this sample
        // used to lack. The host halves of those knobs are the lines below and GridMaterials.
        config.Graphics.Compositor = CompositorAddress;

        // ⚠ Extraction's half of `shadows:` and `antialiasing:`. A frame document decides where a
        // stage is drawn; it cannot decide what an object is extracted as — without these two lines
        // the cascades render nothing and the velocity pass writes zeroes, and every counter says
        // both passes ran.
        config.Graphics.CasterStages.Add("Shadow");
        config.Graphics.CasterStages.Add("Motion");

        // ⚠ Has to be here rather than in OnInitialise. The compositor is built inside AppGraphics'
        // own constructor, before OnInitialise runs, and a document naming !StandardFrame against a
        // builder that has never heard of it throws from inside that build. Constructing the factory
        // is also what registers the node aliases with the type registry — one line, two failures.
        config.Graphics.Factories.Add(new Rendering.PostFx.PostEffectFactory());

        // The document's `extensions: afterOpaque: [!Terrain]` splice, host half. Registering the
        // factory is the whole installation: the host wires the world renderer's terrain list to it,
        // the extraction bridge walks TerrainComponent entities into that list every frame, and the
        // asset source turns the component's references into a heightfield and a grass rule.
        // ⚠ Deliberately not in CasterStages — the terrain does not cast shadows yet, and adding the
        // stage would extract nothing for it while every counter said the pass ran.
        config.Graphics.Factories.Add(new Rendering.Terrain.TerrainFactory());
    }

    /// <inheritdoc />
    protected override void OnInitialise() {
        log = Services.LoggerFactory.CreateLogger("PbrShowcase");

        if (Services.Engine is not { } engine || Services.Graphics is not { } graphics) {
            // Headless with no engine is a legitimate way to run this — `--vixen-frames 2` on a
            // machine with no GPU — and it is not a failure to report.
            return;
        }

        // ⚠ Before anything draws, because without it the game runs, reports nothing wrong, and
        // draws a black screen. DevelopmentEffects says why at length.
        effects = DevelopmentEffects.Attach(graphics.Effects, graphics.Device);

        if (effects is null) {
            SampleLog.NoShaderLibrary(log);
        }

        palette = GridMaterials.Compile(Rows, Columns, log);

        if (palette is not null) {
            // The project's own source, in both seats: Painter is the property a host sets, and the
            // extraction holds its own reference from before this ran — AppGraphics mounted the
            // asset source in its constructor, so pointing only one of the two at the palette would
            // draw every sphere in the fallback grey.
            graphics.Renderer.Painter = palette;

            if (graphics.Renderer.Extraction is { } extraction) {
                extraction.Materials = palette;
                extraction.Material = palette.Fallback;
            }

            // ⚠ The permutation table is the other half of the palette's wiring: an effect key is
            // built from the keys registered under the shader's name, and a ForwardPlus material
            // with no entry resolves to the default variant — the unlit one.
            graphics.Renderer.Materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
        }

        SupplyFrame(graphics);

        world = engine.World;
        Spawn(engine.World);
    }

    /// <inheritdoc />
    protected override void OnUpdate(GameTime time) {
        if (world is not { } scene || camera is not { } eye) {
            return;
        }

        // A slow orbit, so the sample shows its point unattended: the highlight row sweeps the
        // grid, the sky's reflection slides across the mirror row, and the temporal resolve has
        // motion to resolve.
        var angle = (float)time.Total.TotalSeconds * 0.15f;
        var target = new Vector3(0f, 0.6f, 0f);
        var position = target + new Vector3(MathF.Sin(angle) * 7.5f, 3.6f, MathF.Cos(angle) * 7.5f);
        var forward = Vector3.Normalize(target - position);

        ref var transform = ref scene.Get<LocalTransform>(eye);

        transform.Position = position;
        transform.Rotation = Quaternion.FromYawPitchRoll(
            MathF.Atan2(-forward.X, -forward.Z),
            MathF.Asin(forward.Y),
            0f
        );
    }

    /// <inheritdoc />
    protected override void OnShutdown() {
        if (Services.Graphics is { } graphics) {
            SampleLog.FrameReport(
                log!,
                graphics.FrameCount,
                graphics.Renderer.Extraction?.ObjectCount ?? 0,
                effects?.CompiledCount ?? 0
            );

            // What the terrain splice did, so a headless run says whether the ground was real: a
            // frame that drew zero terrains with zero waiting is a scene problem, and one that drew
            // zero with one waiting is content that never arrived — different bugs, one line each.
            if (graphics.Renderer.Host.Builder.Nodes.Values
                    .OfType<Rendering.Terrain.TerrainSceneRenderer>()
                    .FirstOrDefault() is { } ground) {
                SampleLog.GroundReport(
                    log!,
                    ground.TerrainsDrawn,
                    ground.GrassFieldsDrawn,
                    graphics.Renderer.TerrainExtraction?.TerrainCount ?? 0,
                    graphics.Renderer.TerrainExtraction?.Waiting ?? 0,
                    graphics.Renderer.TerrainExtraction?.RefusedGrass ?? 0
                );

                // And the pines beside it, on the same terms: drawn zero with meshes missing is
                // content that never arrived, drawn zero with volumes refused is a broken palette —
                // different bugs, one line.
                SampleLog.FoliageReport(
                    log!,
                    ground.FoliageVolumesDrawn,
                    ground.FoliageMeshesMissing,
                    graphics.Renderer.TerrainExtraction?.FoliageCount ?? 0,
                    graphics.Renderer.TerrainExtraction?.RefusedFoliage ?? 0
                );
            }
        }

        frame?.Dispose();
        frame = null;
    }

    /// <summary>What exists: the grid, the floor it shadows, the ground around it, the sun, and the
    ///     camera.</summary>
    /// <remarks>
    ///     Spawned in code rather than loaded from a scene asset, deliberately: the grid <em>is</em>
    ///     arithmetic — metallic from the row, roughness from the column — and a scene file version
    ///     of it would be twenty-five hand-transcribed copies of two divisions. Sample 13 is where a
    ///     level comes out of a <c>.vxscene</c>.
    /// </remarks>
    void Spawn(World scene) {
        for (var row = 0; row < Rows; row++) {
            for (var column = 0; column < Columns; column++) {
                var entity = Hierarchy.CreateTransform(
                    scene,
                    new LocalTransform {
                        Position = new(
                            (column - ((Columns - 1) * 0.5f)) * Spacing,
                            0.45f,
                            (row - ((Rows - 1) * 0.5f)) * Spacing
                        ),
                        Rotation = Quaternion.Identity,
                        Scale = new(0.9f)
                    }
                );

                scene.Add(entity, new PrimitiveShape {
                    Kind = PrimitiveKind.Sphere,
                    Material = palette?.Cell(row, column) ?? default
                });
            }
        }

        // The floor is not decoration: it is what the cascades land on and what the occlusion
        // darkens under each sphere. A showcase with nothing to receive its shadows would
        // demonstrate the knobs by showing nothing.
        var floor = Hierarchy.CreateTransform(
            scene,
            new LocalTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = new(16f, 1f, 16f) }
        );

        scene.Add(floor, new PrimitiveShape { Kind = PrimitiveKind.Plane, Material = palette?.Floor ?? default });

        // The ground under and around all of it: a 2×2-tile terrain whose flat apron sits just
        // below the floor and whose hills fill the orbit camera's background — heights, painted
        // weight layers and one hole all authored by TerrainSeed, the grass rule by its .vxgrass.
        // Two components carry the whole thing: the terrain names its asset and the grass names its
        // rule, and the entity's transform is the placement — translated by half the terrain's span
        // so its sample grid is centred on the grid of spheres.
        var ground = Hierarchy.CreateTransform(
            scene,
            LocalTransform.At(new(-TerrainSeed.HalfExtent, 0f, -TerrainSeed.HalfExtent))
        );

        scene.Add(ground, Rendering.Terrain.TerrainComponent.Of(TerrainSeed.TerrainPath));
        scene.Add(ground, Rendering.Terrain.TerrainGrassComponent.Of("Assets/Terrain/Meadow.vxgrass"));

        // The pines on the hills: a foliage volume beside the terrain, drawn through the same
        // !Terrain node by the GPU cull — the instances world-space in the committed .vxfol, so the
        // volume's entity sits at the origin rather than at the terrain's corner.
        var pines = Hierarchy.CreateTransform(scene, LocalTransform.At(Vector3.Zero));

        scene.Add(
            pines,
            Rendering.Terrain.FoliageVolumeComponent.Of(TerrainSeed.FoliagePath, TerrainSeed.FoliageTypePath)
        );

        // One sun, aimed along the direction the sky was baked from, with its illuminance and tint
        // read off that same sky — so the shadows, the disc and the ambient are one account of one
        // sun. Photometric (lux), and `exposure: Automatic` is what makes the absolute number safe:
        // the meter brings whatever this is back to the middle of the curve.
        var sun = Hierarchy.CreateTransform(
            scene,
            new LocalTransform {
                Position = Vector3.Zero,
                Rotation = Quaternion.FromYawPitchRoll(
                    MathF.Atan2(-SunDirection.X, -SunDirection.Z),
                    MathF.Asin(SunDirection.Y),
                    0f
                ),
                Scale = Vector3.One
            }
        );

        scene.Add(sun, Lights.Default(LightKind.Directional) with {
            Colour = frame?.SunTint ?? new Color3(1f, 0.97f, 0.92f),
            Intensity = frame?.SunIlluminance ?? 90_000f,
            Unit = LightUnit.Lux
        });

        var lens = Hierarchy.CreateTransform(scene, LocalTransform.At(new(0f, 3.6f, 7.5f)));

        scene.Add(lens, Camera.Perspective);
        camera = lens;
    }

    /// <summary>Hands the frame what its document cannot create, then rebuilds it against them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The reload is the point.</b> <c>AppGraphics</c> builds the compositor in its own
    ///         constructor, before <c>OnInitialise</c> runs — so when `gi: Ambient`'s
    ///         <c>!GlobalDistanceField</c> node was first built, the builder's field slot was empty
    ///         and the node captured a null. A node with no field does nothing rather than throwing,
    ///         which is right for a shared document and exactly what makes the failure invisible:
    ///         the frame draws, un-occluded, and nothing says why.
    ///     </para>
    ///     <para>
    ///         The field's instances are exact analytic distances — a sphere's is one subtraction —
    ///         so the occlusion under each sphere is the real march, not a baked approximation.
    ///         Sample 13's arena builds the same list from its authored boxes.
    ///     </para>
    /// </remarks>
    void SupplyFrame(AppGraphics graphics) {
        var builder = graphics.Renderer.Host.Builder;

        // The sky, the probe fallback and the set-0 stand-ins — ShowcaseFrame's remarks are the
        // whole story. Applied before the reload, so the first frame the rebuilt document draws is
        // already lit by it; the upload itself is the renderer's, once per frame.
        frame = ShowcaseFrame.Bake(graphics.Device, SunDirection);
        frame.Apply(graphics.Renderer.SceneEnvironment, graphics.Renderer.SceneBlock.Parameters);
        graphics.Renderer.Environment = frame.Sky;

        builder.DistanceField = new GlobalDistanceField();

        if (Services.Assets?.Load<GraphicsCompositorAsset>(CompositorAddress).Result is { } document) {
            graphics.Renderer.Host.Load(document);
            SampleLog.FrameRebuilt(log!, CompositorAddress);
        }

        // After the reload, because a stage is one of the things the document builds — the object
        // this held before the reload is not the one the frame is drawing with. Same for the sky
        // node, whose cube no document can name.
        if (builder.Stages.TryGetValue("Shadow", out var caster)) {
            frame.ApplyCaster(caster);
        }

        frame.ApplySky(graphics.Renderer.Host);

        if (builder.Nodes.Values.OfType<GlobalDistanceFieldRenderer>().FirstOrDefault() is not { } node) {
            return;
        }

        node.Instances.Clear();

        var sphere = GridMaterials.SphereField(0.45f);

        for (var row = 0; row < Rows; row++) {
            for (var column = 0; column < Columns; column++) {
                node.Instances.Add(new(
                    sphere,
                    new(
                        (column - ((Columns - 1) * 0.5f)) * Spacing,
                        0.45f,
                        (row - ((Rows - 1) * 0.5f)) * Spacing
                    ),
                    Quaternion.Identity,
                    1f
                ));
            }
        }

        node.Instances.Add(new(GridMaterials.SlabField(new(8f, 0.05f, 8f)), Vector3.Zero, Quaternion.Identity, 1f));

        // What says the composite is out of date. The node compares this rather than the list,
        // because walking every instance every frame costs more than the comparison saves.
        node.InstancesVersion++;

        SampleLog.SceneReady(log!, Rows, Columns, node.Instances.Count);
    }
}
