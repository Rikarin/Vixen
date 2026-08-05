// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     The compositor node that draws the world's terrains: the surface, and each terrain's grass.
/// </summary>
/// <remarks>
///     <para>
///         <b>What <c>!Terrain</c> builds to — the runtime reachability the whole stack was
///         missing.</b> <see cref="TerrainRenderer" />, <see cref="GrassDispatch" /> and
///         <see cref="GrassDrawPass" /> are complete and tested and had no caller outside the
///         editor; this is the caller. It reads the frame's terrains from a
///         <see cref="TerrainSceneSource" /> the extraction system fills, owns one draw set per
///         heightfield, and declares two passes: an attachment-less pass for the uploads and the
///         scatter dispatch — a render graph pass with no attachments runs outside a render pass,
///         which is where a transfer and a compute dispatch are legal — and a raster pass that
///         loads the frame's colour and depth and draws.
///     </para>
///     <para>
///         ⚠ <b>The depth is the scene's, loaded and written.</b> Terrain is opaque geometry that
///         happens to be drawn after the Main pass, so it must occlude and be occluded — reverse-Z,
///         <c>CompareFunction.Greater</c>, which is <c>DepthStencilState.Default</c> and what every
///         pipeline in this stack already builds. A read-only depth here would draw the ground over
///         every wall that should hide it.
///     </para>
///     <para>
///         ⚠ <b>Preview-grade shading, stated rather than discovered.</b> <c>Terrain.rvn</c> lights
///         with its own hard-coded sun and the default permutation covers four weight-blended
///         layers; frame-lit shading rides <c>TerrainLit</c> when the frame provides its half, and
///         shadow casting is <see cref="TerrainCasterRenderer" />'s — a sibling node, because a
///         caster must run before the passes this node runs after. Motion vectors are
///         <see cref="TerrainVelocityRenderer" />'s, a sibling for the mirrored reason: the frame's
///         velocity pass clears the motion plane after this node's own passes have run, so the
///         reprojection is staged here and recorded there. What this node settles is placement and
///         reachability, which is the part a document can say.
///     </para>
///     <para>
///         ⚠ <b>A heightfield placed twice draws once.</b> The renderer's constants are per frame
///         and per placement, so a second entity naming the same terrain would overwrite the
///         first's placement before either draws. The editor shares the atlas and accepts the same
///         limit; <see cref="SharedPlacements" /> is where the second placement is counted rather
///         than silently lost.
///     </para>
/// </remarks>
public sealed class TerrainSceneRenderer : SceneRenderer, IDisposable {
    /// <summary>The cell grid every grass field scatters over, in metres.</summary>
    /// <remarks>The editor's foliage volume uses the same 32 m cell, and the two must agree the day
    ///     painted foliage and derived grass share a residency.</remarks>
    public const float GrassCellSize = 32f;

    /// <summary>How far grass cells stay resident when neither component nor document says.</summary>
    public const float DefaultGrassRange = 160f;

    /// <summary>How far foliage cells stay uploaded when neither component nor document says.</summary>
    /// <remarks>Wider than the grass's, because a tree is visible from further than a blade — the
    ///     default type's cull distance is 250 m and a residency inside it uploads cells only to
    ///     cull every instance in them.</remarks>
    public const float DefaultFoliageRange = 320f;

    readonly Dictionary<TerrainMap, TerrainDrawSet?> sets = [];
    readonly List<(TerrainDrawSet Set, TerrainSceneEntry Entry)> drawn = [];
    readonly Dictionary<FoliageVolume, FoliageStand?> stands = [];
    readonly List<(FoliageStand Stand, FoliageSceneEntry Entry)> foliageDrawn = [];
    readonly HashSet<TerrainMap> placed = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly TerrainFrameLighting lighting = new();

    TerrainShaders terrainShaders;
    TerrainShaders terrainVelocityShaders;
    TerrainShaders grassVelocityShaders;
    TerrainShaders foliageVelocityShaders;
    (bool Lit, bool Split, bool Clustered) shaderMode;
    (PixelFormat Motion, PixelFormat Depth) velocityFormats;
    float lastTime = -1f;
    bool disposed;

    /// <summary>The colour target the ground draws into.</summary>
    public required string Output { get; init; }

    /// <summary>The depth target it tests and writes.</summary>
    public required string Depth { get; init; }

    /// <summary>The frame's split albedo plane, bound beside <see cref="Output" /> when it exists.</summary>
    /// <remarks>
    ///     The canonical name is <c>!StandardFrame</c>'s and the presence of the resource is the
    ///     signal: a frame that splits declares the plane and the Main pass writes it, so the ground
    ///     joins with the same three targets — direct light, albedo, raw world normal — and the
    ///     ambient combine rebuilds its diffuse ambient like everything else's. A frame that does
    ///     not split declares no such resource and the ground draws one target, ambient included.
    /// </remarks>
    public string Albedo { get; set; } = "SceneAlbedo";

    /// <summary>And its normal plane, on the same terms.</summary>
    public string Normals { get; set; } = "SceneNormals";

    /// <summary>The frame's cascade atlas — half of what makes the ground frame-lit at all.</summary>
    /// <remarks>
    ///     Read for two reasons: its presence (with the published cascade constants) is the signal
    ///     that the frame lights, and the raster pass must declare the read so the graph fences the
    ///     shadow passes before the ground samples what they wrote.
    /// </remarks>
    public string ShadowAtlas { get; set; } = "ShadowAtlas";

    /// <summary>Which pass's names the frame's lighting is published under.</summary>
    /// <remarks>
    ///     <c>ShadowMapRenderer.Publish</c>, the lighting feature and the Main pass's
    ///     <c>sceneTextures:</c> lines all qualify their keys by the shading pass they serve, and
    ///     the ground consumes the same values — the sun, the cascades, the cluster buffers — so it
    ///     reads the same qualified names. The default is the standard frame's shading pass.
    /// </remarks>
    public string ScenePass { get; set; } = "ForwardPlus";

    /// <summary>The frame's set-0 state, whose parameters and scene lighting the lit path reads.</summary>
    /// <remarks>
    ///     Null is the editor's case and any host that wires no frame: the ground keeps the
    ///     preview shaders and asks the frame for nothing. <see cref="TerrainFactory" /> wires the
    ///     builder's own instance.
    /// </remarks>
    public SceneConstants? Frame { get; set; }

    /// <summary>The view whose camera places, culls and streams the ground.</summary>
    public RenderView? View { get; set; }

    /// <summary>Where the frame's terrains come from, or null while nothing supplies them.</summary>
    /// <remarks>
    ///     The stable object between this node — rebuilt per document load — and the extraction
    ///     system, registered once. Null draws nothing quietly, which is a document opened before
    ///     its host finished wiring.
    /// </remarks>
    public TerrainSceneSource? Scene { get; set; }

    /// <summary>The resolved vegetation numbers this node runs at.</summary>
    public TerrainVegetationQuality Vegetation { get; set; } = new();

    /// <summary>Whether grass fields scatter and draw at all.</summary>
    public bool Grass { get; set; } = true;

    /// <summary>The device, or null for a node that declines to draw.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>How many terrains the last frame drew.</summary>
    public int TerrainsDrawn { get; private set; }

    /// <summary>How many grass fields the last frame dispatched.</summary>
    public int GrassFieldsDrawn { get; private set; }

    /// <summary>How many placements were dropped because their heightfield was already placed.</summary>
    public int SharedPlacements { get; private set; }

    /// <summary>How many terrains the renderer refused — a shape problem, not a frame problem.</summary>
    public int RefusedTerrains { get; private set; }

    /// <summary>How many grass fields named a weight layer their terrain does not have.</summary>
    public int GrassLayersMissing { get; private set; }

    /// <summary>How many foliage volumes the last frame culled and drew.</summary>
    public int FoliageVolumesDrawn { get; private set; }

    /// <summary>How many palette types drew nothing because their mesh has not resolved.</summary>
    /// <remarks>
    ///     ⚠ <b>The difference between "not loaded yet" and "not in the level".</b> A number that
    ///     falls to zero over a level's first frames is content arriving; one that stays up is a
    ///     <c>.vxfoliage</c> whose mesh reference nothing can resolve — an unparseable name, a
    ///     deleted asset, or a mesh with no geometry — and only the first fixes itself.
    /// </remarks>
    public int FoliageMeshesMissing { get; private set; }

    /// <summary>Whether the last build was still waiting for a shader to resolve.</summary>
    /// <remarks>
    ///     True for the first frames of a development run while the compiler works, and for ever on
    ///     a shipped build whose bundle is missing the terrain variants — which is the state this
    ///     property exists to make legible, because both look like ground that is not there.
    /// </remarks>
    public bool WaitingForShaders { get; private set; }

    /// <summary>Whether the last build chose the frame-lit shaders over the preview.</summary>
    /// <remarks>
    ///     True exactly when the frame provided what lit shading needs: a <see cref="Frame" /> with
    ///     a camera, the cascade constants published under <see cref="ScenePass" />'s name, and the
    ///     <see cref="ShadowAtlas" /> resource to sample. Anything less is the preview — the honest
    ///     fallback, because a lit shader with nothing bound behind it draws a black world.
    /// </remarks>
    public bool Lit { get; private set; }

    /// <summary>Whether the ground wrote the frame's split planes as well as its colour.</summary>
    public bool Split { get; private set; }

    /// <summary>Whether the ground stack reprojected into the frame's motion target this frame.</summary>
    /// <remarks>
    ///     True exactly when the spliced <see cref="TerrainVelocityRenderer" /> exists, the frame
    ///     declares its motion plane, and the three velocity shaders have resolved. False on a
    ///     frame with no temporal resolve — the ordinary case, costing nothing — and for the first
    ///     frames of a development run while the compiler works, which is a ground that ghosts
    ///     briefly rather than a ground that is not there: the colour path never waits on this.
    /// </remarks>
    public bool MotionVectors { get; private set; }

    /// <summary>How many reprojection draws the sibling recorded last frame — terrains, grass
    ///     fields' indirect commands and foliage batches' together.</summary>
    public int VelocityDraws { get; private set; }

    /// <summary>The velocity sibling the factory paired, or null in a frame with no velocity pass.</summary>
    internal TerrainVelocityRenderer? VelocitySibling { get; set; }

    /// <summary>Whether the lit shaders read the frame's culled cluster lists.</summary>
    /// <remarks>
    ///     False on a frame that culls no lights — the ground is then lit by the sun and the sky
    ///     alone, with no per-object fallback on purpose: a terrain is the biggest object in any
    ///     frame, which is the exact shape the eight-light per-object list reorders worst on.
    /// </remarks>
    public bool ClusteredLights { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        // The names first and loudly, before any early-out: a document that names a target nothing
        // declared is wrong however empty the world is, and a refusal that only fired once a
        // terrain existed would land a long way from the typo.
        var colour = frame.Texture(ToString(), Output);
        var colourFormat = frame.FormatOf(ToString(), Output);
        var depth = frame.Texture(ToString(), Depth);
        var depthFormat = frame.FormatOf(ToString(), Depth);

        TerrainsDrawn = 0;
        GrassFieldsDrawn = 0;
        SharedPlacements = 0;
        FoliageVolumesDrawn = 0;
        FoliageMeshesMissing = 0;
        VelocityDraws = 0;
        WaitingForShaders = false;

        // A node built without a device declines to draw — an editor opening a document before it
        // has a window, and CompositorBuilder's own contract for every post node.
        if (Device is null || Modules is null || View is not { } view || Scene is not { } scene) {
            return;
        }

        if (scene.Terrains.Count == 0 && scene.Foliage.Count == 0) {
            // No terrain in the world is the ordinary case for most projects, and it costs nothing.
            return;
        }

        // What the frame provides decides which shaders draw — see Lit's remarks. Decided before
        // the shader resolve because the mode is the effect key, and re-decided every frame because
        // the cluster buffers arrive a frame after the first Main pass executes.
        var mode = DetectMode(frame);

        if (mode != shaderMode) {
            // A mode is a pipeline: different shaders, a different set layout, and under split a
            // different attachment count. The draw sets are rebuilt on the mode's first frame —
            // the same atlas re-upload the very first frame paid.
            foreach (var set in sets.Values) {
                set?.Dispose();
            }

            // The foliage stands with them, for the same reason — the draw pass's pipeline and set
            // layout are the mode's. The instance re-upload a rebuild costs is the first frame's.
            foreach (var stand in stands.Values) {
                stand?.Dispose();
            }

            sets.Clear();
            stands.Clear();
            terrainShaders = default;
            shaderMode = mode;
        }

        (Lit, Split, ClusteredLights) = mode;

        if (!ResolveTerrainShaders(frame)) {
            WaitingForShaders = true;
            return;
        }

        // The velocity path rides availability, the lit detection's own idiom: the transformer
        // spliced a sibling exactly when the frame draws a Motion stage, the frame declares the
        // plane that stage writes, and the three velocity shaders have resolved. Any of them
        // missing turns the path off for the frame without holding the colour draw hostage — a
        // ground that ghosts for the compiler's first frames rather than a ground that is not
        // there.
        MotionVectors = VelocitySibling is { Enabled: true } sibling
            && frame.Has(sibling.Motion)
            && ResolveVelocityShaders(frame);

        if (MotionVectors) {
            velocityFormats = (frame.FormatOf(ToString(), VelocitySibling!.Motion), depthFormat);
        }

        var output = Split
            ? new RenderOutput(
                [colourFormat, frame.FormatOf(ToString(), Albedo), frame.FormatOf(ToString(), Normals)],
                depthFormat
            )
            : new RenderOutput([colourFormat], depthFormat);

        if (Lit) {
            FillLighting();
        }

        drawn.Clear();
        placed.Clear();

        foreach (var entry in scene.Terrains) {
            if (!placed.Add(entry.Terrain)) {
                SharedPlacements++;
                continue;
            }

            if (SetFor(entry, output, frame) is not { } set) {
                continue;
            }

            drawn.Add((set, entry));
        }

        foliageDrawn.Clear();

        if (scene.Foliage.Count > 0) {
            if (ResolveFoliageShaders(frame) is { } foliageShaders) {
                foreach (var entry in scene.Foliage) {
                    if (StandFor(entry, output, foliageShaders) is { } stand) {
                        foliageDrawn.Add((stand, entry));
                    }
                }
            } else {
                WaitingForShaders = true;
            }
        }

        if (drawn.Count == 0 && foliageDrawn.Count == 0) {
            return;
        }

        var time = (float)clock.Elapsed.TotalSeconds;

        // The clock the grass velocity shader re-evaluates the wind at. This frame's value on the
        // very first frame, which is a sway delta of zero — the honest answer for a field nobody
        // has seen move yet.
        var previousTime = lastTime >= 0f ? lastTime : time;

        lastTime = time;

        var atlas = Lit ? frame.Texture(ToString(), ShadowAtlas) : default;

        // The uploads and the scatter, outside any render pass: buffer writes, texture copies and
        // the two compute dispatches are what a Vulkan render pass refuses. SideEffect because the
        // graph cannot see this pass's edges — the terrain's atlas and the grass ring are the
        // node's own resources, not the graph's.
        frame.Graph.AddPass(
            $"{this}.Upload",
            pass => {
                pass.SideEffect();

                if (Lit) {
                    // Declared read here as well as on the raster pass, because this is where the
                    // descriptors are written: a graph texture has no view until the graph places
                    // it, and the read is what makes the handle answerable in Execute.
                    pass.Reads(atlas);
                }

                pass.Execute(
                    context => {
                        if (Lit) {
                            // The handles are the frame's and arrive at execute: the atlas from the
                            // graph, the cluster buffers from what the shading pass last published.
                            lighting.ShadowAtlas = context.View(atlas);
                            lighting.LightBuffer = PublishedBuffer("lightBuffer");
                            lighting.Clusters = PublishedBuffer("clusters");
                        }

                        foreach (var (set, entry) in drawn) {
                            Upload(context.CommandList, set, entry, view, time, previousTime);
                        }

                        foreach (var (stand, entry) in foliageDrawn) {
                            UploadFoliage(context.CommandList, stand, entry, view);
                        }
                    }
                );
            }
        );

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // Loaded, never cleared: the Main pass's picture is what the ground joins. The
                // depth is read-write on purpose — see the class remarks.
                pass.ColourAttachment(colour, LoadAction.Load, default);

                if (Split) {
                    // The split planes the Main pass wrote, loaded for the same reason the colour
                    // is: the ground joins a frame, it does not start one.
                    pass.ColourAttachment(frame.Texture(ToString(), Albedo), LoadAction.Load, default);
                    pass.ColourAttachment(frame.Texture(ToString(), Normals), LoadAction.Load, default);
                }

                pass.DepthAttachment(depth, LoadAction.Load, 0f);

                if (Lit) {
                    // The barrier half: the shadow passes wrote the atlas, and the ground samples
                    // it inside this pass.
                    pass.Reads(atlas);
                }

                pass.Execute(
                    context => {
                        foreach (var (set, _) in drawn) {
                            set.Surface.Record(context.CommandList);
                            TerrainsDrawn++;

                            if (set.Field is { } field) {
                                field.Draw.Record(context.CommandList, field.Dispatch);
                                GrassFieldsDrawn++;
                            }
                        }

                        foreach (var (stand, _) in foliageDrawn) {
                            stand.Draw.Record(context.CommandList, stand.Cull, stand.Meshes);
                            FoliageVolumesDrawn++;
                        }
                    }
                );
            }
        );
    }

    /// <summary>The caster over one terrain's draw set, made when the caster node first asks.</summary>
    /// <remarks>
    ///     <para>
    ///         The seam <see cref="TerrainCasterRenderer" /> reaches through: the caster borrows
    ///         the surface's heightmap, holes and index buffer, so it lives and dies with the draw
    ///         set — a mode flip or a LOD change disposes both together, and a caster over a
    ///         disposed surface cannot exist.
    ///     </para>
    ///     <para>
    ///         ⚠ Null until the surface has uploaded once. The caster pass records <em>before</em>
    ///         this node's Upload pass runs, so a set born this frame holds a heightmap in the
    ///         <c>Undefined</c> state — sampled, that is a validation error wearing a flat shadow.
    ///         One frame of caster latency per new terrain is the cost, paid once.
    ///     </para>
    /// </remarks>
    internal TerrainCasterPass? CasterFor(TerrainMap terrain, in TerrainShaders shaders, PixelFormat depthFormat) {
        if (Device is null || !sets.TryGetValue(terrain, out var set) || set is null) {
            return null;
        }

        if (!set.Surface.Uploaded) {
            return null;
        }

        return set.Caster ??= new(Device, set.Surface, shaders, depthFormat);
    }

    /// <summary>A frame buffer the shading pass published, by the shader's bare name for it.</summary>
    BufferHandle PublishedBuffer(string binding) {
        if (Frame is not { } constants) {
            return default;
        }

        var key = ParameterKeys.New<BufferHandle>($"{ScenePass}.{binding}");

        return constants.Parameters.Has(key) ? constants.Parameters.Get(key) : default;
    }

    /// <summary>Whether the frame provides what each of the three lit decisions needs.</summary>
    /// <remarks>
    ///     Availability is the signal — no toggle on the node, because every one of these is a fact
    ///     the frame already states: the atlas and the split planes are declared resources, the
    ///     cascades and the cluster buffers are published parameters, and the camera is the scene
    ///     lighting's. A hand-authored frame that provides them differently names its own resources
    ///     through <see cref="ShadowAtlas" />, <see cref="Albedo" /> and <see cref="Normals" />.
    /// </remarks>
    (bool Lit, bool Split, bool Clustered) DetectMode(CompositorFrame frame) {
        if (Frame is not { Lighting.Camera: not null } constants) {
            return (false, false, false);
        }

        var cascades = ParameterKeys.New<Matrix4x4>($"{ScenePass}.cascades[0].viewProjection");

        if (!constants.Parameters.Has(cascades) || !frame.Has(ShadowAtlas)) {
            return (false, false, false);
        }

        var split = frame.Has(Albedo) && frame.Has(Normals);

        // Both halves of the clustered contract, because they publish separately: the light list
        // comes from the lighting feature and the lists from the pass's own sceneBuffers line. The
        // handles land a frame after the first Main pass executes, so a clustered frame's ground is
        // sun-lit for exactly one frame — the same frame everything else is still warming up in.
        var clustered = PublishedBuffer("lightBuffer").IsValid && PublishedBuffer("clusters").IsValid;

        return (true, split, clustered);
    }

    /// <summary>Copies the frame's lighting into the values the lit blocks are written from.</summary>
    void FillLighting() {
        var constants = Frame!;
        var parameters = constants.Parameters;
        var scene = constants.Lighting!;
        var camera = scene.Camera!.Value;
        var sun = scene.Sun?.Sun;

        lighting.LightDirection = sun?.Direction ?? Vector3.Zero;
        lighting.LightColor = sun?.Radiance ?? Vector3.Zero;
        lighting.ViewPosition = camera.Position;

        // The same derivations the frame's own consumers use: the view matrix ShadowMapRenderer's
        // cascades were fitted along, and the half-tangents ClusterGrid.Apply hands both the culler
        // and the shading pass — a second derivation of either is how the ground reads someone
        // else's slice.
        lighting.View = Matrix4x4.LookAt(camera.Position, camera.Position + camera.Forward, camera.Up);

        var vertical = MathF.Tan(camera.FieldOfView * 0.5f);

        lighting.TanHalfFov = new(vertical * camera.AspectRatio, vertical);
        lighting.NearPlane = camera.NearPlane;
        lighting.FarPlane = camera.FarPlane;

        lighting.EnvironmentSh = scene.Environment?.Irradiance ?? default;
        lighting.AmbientIntensity = scene.Environment?.Intensity ?? 0f;

        lighting.ShadowTexelSize = Value(parameters, "shadowTexelSize", new Vector2(1f / 1024f, 1f / 1024f));
        lighting.ShadowConstantBias = Value(parameters, "shadowConstantBias", 0.008f);
        lighting.ShadowSlopeBias = Value(parameters, "shadowSlopeBias", 0.01f);
        lighting.ShadowFadeRange = Value(parameters, "shadowFadeRange", 10f);

        var lastSplit = 0f;

        for (var cascade = 0; cascade < lighting.Cascades.Length; cascade++) {
            var slot = $"{ScenePass}.cascades[{cascade.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
            var matrix = ParameterKeys.New<Matrix4x4>($"{slot}.viewProjection");

            if (parameters.Has(matrix)) {
                lighting.Cascades[cascade] = new() {
                    ViewProjection = parameters.Get(matrix),
                    Split = Value(parameters, $"cascades[{cascade}].split", lastSplit),
                    DepthScale = Value(parameters, $"cascades[{cascade}].depthScale", 0f)
                };

                lastSplit = lighting.Cascades[cascade].Split;
            } else {
                // Padded degenerate — a zero matrix fails the containment test's `w > 0`, so the
                // slot can never be selected — and the last real split repeated, so the fade-out
                // distance stays the frame's own. FrameShadow's own contract.
                lighting.Cascades[cascade] = new() { Split = lastSplit };
            }
        }
    }

    /// <summary>One published scalar, under the shading pass's qualified name.</summary>
    float Value(ParameterCollection parameters, string name, float fallback) {
        var key = ParameterKeys.New<float>($"{ScenePass}.{name}");

        return parameters.Has(key) ? parameters.Get(key) : fallback;
    }

    /// <summary>And one published vector, on the same terms.</summary>
    Vector2 Value(ParameterCollection parameters, string name, Vector2 fallback) {
        var key = ParameterKeys.New<Vector2>($"{ScenePass}.{name}");

        return parameters.Has(key) ? parameters.Get(key) : fallback;
    }

    /// <summary>Stages one terrain's frame: the surface, its reprojection, then its grass field.</summary>
    void Upload(ICommandList commands, TerrainDrawSet set, in TerrainSceneEntry entry, RenderView view, float time, float previousTime) {
        // The placement rides the view matrix, because the shader has no field for one — the
        // editor's UploadTerrain states the whole argument. The frustum comes from the combined
        // matrix so culling happens in the terrain's own space.
        var placedMatrix = Matrix4x4.FromTranslation(entry.Origin) * view.ViewProjection;
        var terrainView = new TerrainView(placedMatrix, view.Position - entry.Origin, new(placedMatrix));

        // Re-assigned per frame rather than at construction, because the source appears when
        // content mounts — which may be after this node first built the set.
        set.Surface.Textures = Scene?.Textures;

        // The frame's lighting rides the same per-frame assignment: one shared instance, and the
        // origin that turns the shader's terrain-local positions back into the world the cascades
        // and the lamps live in.
        set.Surface.Frame = Lit ? lighting : null;
        set.Surface.FrameOrigin = entry.Origin;

        set.Surface.Upload(commands, terrainView);

        if (MotionVectors) {
            // Staged after the surface's own upload, so the node records the set points at are this
            // frame's camera selection — the same patches, the same morph, and therefore the same
            // depths the velocity node's read-only test compares. The sibling records the draw
            // after the frame's velocity pass has cleared the plane.
            set.Velocity ??= new(Device!, set.Surface, terrainVelocityShaders, velocityFormats.Motion, velocityFormats.Depth);

            set.Velocity.Upload(placedMatrix, Matrix4x4.FromTranslation(entry.Origin) * PreviousOf(view));
        }

        if (set.Field is not { } field) {
            return;
        }

        var range = entry.GrassRange > 0f ? entry.GrassRange : DefaultGrassRange;

        field.Residency.Update(view.Position, range);

        field.Resident.Clear();
        field.Resident.AddRange(field.Residency.Resident);

        var scale = MathF.Max(Vegetation.GrassCullDistanceScale, 0f);
        var source = set.Surface.GrassSource(field.Layer, entry.Origin);

        field.Draw.Frame = Lit ? lighting : null;

        field.Dispatch.Prepare(
            field.Type,
            new(GrassCellSize),
            field.Resident,
            in source,
            field.Draw.MeshTemplate,
            Math.Clamp(Vegetation.GrassDensityScale, 0f, 1f)
        );

        // The blades are scattered in world space, so the grass draws with the camera's own view
        // rather than the terrain's origin-shifted one.
        var worldView = new TerrainView(view.ViewProjection, view.Position, new(view.ViewProjection));
        var fade = new Vector2(field.Type.StartCullDistance * scale, field.Type.EndCullDistance * scale);

        field.Draw.Prepare(
            commands,
            field.Dispatch,
            in worldView,
            field.Type.Wind,
            time,
            fade: fade
        );

        if (MotionVectors) {
            // The same camera, the same wind, the same fade band — every value the stipple reads
            // is the colour pass's own, so the two passes discard the same pixels. What is this
            // pass's alone is the previous matrix and the previous clock, which is the sway's own
            // motion made real to the resolve.
            field.Velocity ??= new(Device!, grassVelocityShaders, velocityFormats.Motion, velocityFormats.Depth);

            field.Velocity.Prepare(
                field.Draw,
                field.Dispatch,
                in worldView,
                PreviousOf(view),
                field.Type.Wind,
                time,
                previousTime,
                fade
            );
        }

        field.Dispatch.Record(commands);
    }

    /// <summary>Last frame's matrix, or this frame's on the frame nobody has advanced yet.</summary>
    /// <remarks>The zero matrix a fresh view reports would project every vertex to the origin and
    ///     hand the whole ground a vector pointing at screen centre — the substitution
    ///     <c>MotionVectorRenderFeature</c> makes for an object's first sight, made here for the
    ///     view's.</remarks>
    static Matrix4x4 PreviousOf(RenderView view) =>
        view.PreviousViewProjection == default ? view.ViewProjection : view.PreviousViewProjection;

    /// <summary>The draw set for one terrain, made on the frame its heightfield first appears.</summary>
    TerrainDrawSet? SetFor(in TerrainSceneEntry entry, in RenderOutput output, CompositorFrame frame) {
        var nearRange = entry.NearRange > 0f ? entry.NearRange : Vegetation.TerrainNearRange;

        if (sets.TryGetValue(entry.Terrain, out var existing)) {
            if (existing is null) {
                // Refused once is refused always — the refusal was about the terrain's shape, and
                // retrying per frame would re-throw sixty times a second.
                return null;
            }

            if (Math.Abs(existing.NearRange - nearRange) > float.Epsilon) {
                // A LOD range is baked into the quadtree at construction, so a changed range is a
                // new renderer. Rare — a scene edit — and the atlas re-upload it costs is the same
                // one the first frame paid.
                existing.Dispose();
                sets.Remove(entry.Terrain);
            } else {
                SyncGrass(existing, entry, output, frame);
                return existing;
            }
        }

        TerrainDrawSet set;

        try {
            var surface = new TerrainRenderer(
                Device!,
                entry.Terrain,
                terrainShaders,
                output,
                Vixen.Terrain.TerrainLodRanges.Default with { NearRange = nearRange },
                lit: Lit
            );

            var description = entry.Terrain.Description;

            // A streamer only changes the first frame of a large terrain — a small one fits the
            // atlas by construction and the machinery would be a decision with one outcome. Sixteen
            // tiles is TerrainRenderer's own line for "fits by construction". Attached before the
            // first Upload, which is the streamer's stated requirement.
            if (description.TilesX * description.TilesZ > 16) {
                surface.Streaming = new(description, new TerrainTileSource(entry.Terrain));
            }

            set = new(surface, nearRange);
        } catch (ArgumentException) {
            // A terrain the renderer refuses is one shape of asset, not the whole frame — the
            // editor's viewport makes the same trade.
            sets[entry.Terrain] = null;
            RefusedTerrains++;

            return null;
        }

        sets[entry.Terrain] = set;
        SyncGrass(set, entry, output, frame);

        return set;
    }

    /// <summary>Brings a set's grass field into line with what the entry says this frame.</summary>
    void SyncGrass(TerrainDrawSet set, in TerrainSceneEntry entry, in RenderOutput output, CompositorFrame frame) {
        if (!Grass || entry.Grass is not { } type) {
            set.Field?.Dispose();
            set.Field = null;

            return;
        }

        if (set.Field is { } existing) {
            if (existing.Type == type) {
                return;
            }

            // The rule changed — grass is derived, so the whole field re-grows. The ring's buffers
            // are sized by quality rather than by the type, so a rebuild is the descriptor work
            // only.
            existing.Dispose();
            set.Field = null;
        }

        var layer = LayerOf(entry.Terrain, type.Layer);

        if (type.NeedsSurfaceWeight && layer < 0) {
            // A rule bound to a layer the terrain does not have grows nothing wherever it is
            // asked, so the field is not built — and the counter is where a person finds out.
            GrassLayersMissing++;

            return;
        }

        if (GrassShaders(frame, type) is not { } shaders) {
            WaitingForShaders = true;

            return;
        }

        var dispatch = new GrassDispatch(
            Device!,
            shaders.Scatter,
            shaders.Arguments,
            Math.Max(1, Vegetation.GrassResidentCells),
            Math.Max(1, Vegetation.GrassBladesPerCell)
        );

        var draw = new GrassDrawPass(Device!, shaders.Draw, output, lit: Lit);

        set.Field = new(
            type,
            layer,
            new(new(GrassCellSize), Math.Max(1, Vegetation.GrassResidentCells)),
            dispatch,
            draw
        );
    }

    /// <summary>How many cells one volume's streamer may keep uploaded, or zero for a volume that
    ///     fits its budget and streams nothing — the tier's number, for a test.</summary>
    internal int FoliageCellsOf(FoliageVolume volume) => stands.GetValueOrDefault(volume)?.Streamer?.Cells ?? 0;

    /// <summary>The device state for one volume, made on the frame the volume first appears.</summary>
    /// <remarks>
    ///     Keyed by the volume object, on <see cref="SetFor" />'s terms: the extraction bridge hands
    ///     back the same instance frame over frame, and the cull pass's instance buffer is the cost
    ///     a rebuild would re-pay.
    /// </remarks>
    FoliageStand? StandFor(in FoliageSceneEntry entry, in RenderOutput output, in FoliageShaders shaders) {
        if (stands.TryGetValue(entry.Volume, out var existing)) {
            // Refused once is refused always — the terrain path's own trade, for its reason.
            return existing;
        }

        FoliageStand stand;

        try {
            var cull = new FoliageCullPass(Device!, shaders.Count, shaders.Place);
            var budget = Math.Max(1, Vegetation.FoliageCellBudget);

            // ⚠ A streamer only where the tier's budget bites — the terrain surface's own line for
            // "fits by construction". A volume inside the budget uploads whole, at once, with no
            // first-frames hole while pages land; a bigger one uploads the cells around the camera
            // and the budget is what FoliageCellBudget means.
            FoliageStreamer? streamer = entry.Volume.CellCount > budget
                ? new FoliageStreamer(entry.Volume, budget)
                : null;

            cull.Streaming = streamer;

            stand = new(Device!, cull, new(Device!, shaders.Draw, output, lit: Lit), streamer);
        } catch (ArgumentException) {
            // A volume the pass refuses is one shape of asset, not the whole frame — the terrain
            // path's own trade. Extraction validates the palette, so this is belt over braces.
            stands[entry.Volume] = null;

            return null;
        }

        stands[entry.Volume] = stand;

        return stand;
    }

    /// <summary>Stages one volume's frame: residency, meshes, the cull's tables, and its dispatches.</summary>
    void UploadFoliage(ICommandList commands, FoliageStand stand, in FoliageSceneEntry entry, RenderView view) {
        var volume = entry.Volume;
        var range = entry.Range > 0f ? entry.Range : DefaultFoliageRange;

        // The residency is decided in volume space — the streamer's window was derived from the
        // volume's own cells — so the camera steps out of the world by the entry's origin.
        stand.Streamer?.Update([new StreamingSource(view.Position - entry.Origin, range)]);

        SyncFoliageMeshes(stand, volume);

        // Re-uploaded when something moved: a cell entered or left residency, a mesh arrived and
        // its type joined the draw list, or the volume itself grew. Everything else is the steady
        // state, and the steady state uploads nothing — which is the point of the whole pass.
        if (stand.Streamer is { Changed: true }
            || stand.UploadedDraws != stand.DrawList.Count
            || stand.UploadedInstances != volume.InstanceCount) {
            stand.Cull.Upload(volume, stand.DrawList, entry.Origin);
            stand.Streamer?.Accept();

            stand.UploadedDraws = stand.DrawList.Count;
            stand.UploadedInstances = volume.InstanceCount;
        }

        // The instances were uploaded in world space, so the cull answers the camera's own view —
        // no origin-shifted frustum, unlike the terrain's sample-space placement.
        stand.Cull.Prepare(
            view.Frustum,
            view.Position,
            Math.Clamp(Vegetation.FoliageDensityScale, 0f, 1f),
            default,
            MathF.Max(Vegetation.FoliageCullDistanceScale, 0f)
        );

        stand.Draw.Frame = Lit ? lighting : null;

        var worldView = new TerrainView(view.ViewProjection, view.Position, view.Frustum);

        stand.Draw.Prepare(commands, stand.Cull, in worldView);

        if (MotionVectors) {
            // A placed tree does not move, so the camera term is its whole motion — one previous
            // matrix, no previous clock. Prepared after the colour pass's own prepare, whose first
            // frame uploads the default albedo the velocity set borrows.
            stand.Velocity ??= new(Device!, foliageVelocityShaders, velocityFormats.Motion, velocityFormats.Depth);

            stand.Velocity.Prepare(stand.Draw, stand.Cull, in worldView, PreviousOf(view));
        }

        stand.Cull.Record(commands);

        FoliageMeshesMissing += stand.Missing.Count;
    }

    /// <summary>Records the stack's reprojection — every staged velocity pass, in draw order.</summary>
    /// <returns>How many draws were issued: terrains, grass fields' indirect commands, foliage batches'.</returns>
    /// <remarks>
    ///     Called by <see cref="TerrainVelocityRenderer" /> from inside its own pass, which the
    ///     transformer placed after the frame's velocity pass — recording these draws from this
    ///     node's own passes would put them before that pass's clear, which wipes them unwritten.
    ///     Every pass recorded here was staged by this frame's upload, so a set born without the
    ///     motion path — the sibling absent, the shaders pending — simply holds no velocity object
    ///     and contributes nothing.
    /// </remarks>
    internal int RecordVelocity(ICommandList commands) {
        var draws = 0;

        foreach (var (set, _) in drawn) {
            if (set.Velocity is { } surface) {
                surface.Record(commands);
                draws += surface.Draws;
            }

            if (set.Field is { Velocity: { } blades } field) {
                draws += blades.Record(commands, field.Draw, field.Dispatch);
            }
        }

        foreach (var (stand, _) in foliageDrawn) {
            if (stand.Velocity is { } plants) {
                draws += plants.Record(commands, stand.Cull, stand.Meshes);
            }
        }

        VelocityDraws = draws;

        return draws;
    }

    /// <summary>Turns arrived mesh assets into device buffers and draw templates, one type at a time.</summary>
    /// <remarks>
    ///     ⚠ <b>A type is drawn only once its mesh is real</b> — there is no honest stand-in for a
    ///     tree. Until then its chunks are not uploaded at all (<see cref="FoliageCullPass.Upload" />
    ///     skips types with no draw entry), and <see cref="FoliageMeshesMissing" /> is where the wait
    ///     is visible.
    ///     Asking is what starts the load — <c>IMeshSource</c>'s contract — so the miss is the ask.
    /// </remarks>
    void SyncFoliageMeshes(FoliageStand stand, FoliageVolume volume) {
        if (Scene?.Meshes is not { } meshes) {
            // No source is a host that has not mounted content; every stored type is missing and
            // says so, rather than a forest that is quietly not there.
            for (var type = 0; type < volume.Palette.Count; type++) {
                if (volume.Palette[type].Storage == FoliageStorage.Stored) {
                    stand.Missing.Add(type);
                }
            }

            return;
        }

        for (var type = 0; type < volume.Palette.Count; type++) {
            if (stand.Meshes.ContainsKey(type)) {
                continue;
            }

            var settings = volume.Palette[type];

            // Derived types are the grass path's — nothing about them is in any file, and a .vxfol
            // holds only stored chunks. A derived palette entry here simply has nothing to draw.
            if (settings.Storage != FoliageStorage.Stored) {
                continue;
            }

            if (settings.Mesh is not { Length: > 0 } name
                || !AssetReference.TryParse(name, null, out var reference)
                || reference.IsNull) {
                // An unparseable or absent reference never resolves; it stays in Missing for ever,
                // which is the counter's job — see FoliageMeshesMissing's remarks.
                stand.Missing.Add(type);
                continue;
            }

            if (!meshes.TryGet(reference, out var data)) {
                stand.Missing.Add(type);
                continue;
            }

            if (data.Positions.Length == 0 || data.Indices.Length == 0) {
                stand.Missing.Add(type);
                continue;
            }

            stand.Meshes[type] = FoliageDrawPass.UploadMesh(
                Device!,
                data.Positions,
                data.Normals,
                data.TexCoords,
                data.Indices,
                settings.Name
            );

            stand.Missing.Remove(type);

            // One level until the LOD-group seam exists: the whole index buffer, no distances. The
            // cull bins everything into level 0 and the other three commands stay zero-count.
            stand.DrawList.Add(new(type, [new() { IndexCount = (uint)stand.Meshes[type].IndexCount }], []));
        }
    }

    /// <summary>Resolves the cull's two compute phases and the draw pair the mode names.</summary>
    /// <remarks>
    ///     The non-occluding cull variants, deliberately: the frame publishes no depth pyramid for
    ///     the ground stack yet, and compiling the Hi-Z pair for a binding nothing fills would be a
    ///     variant no dispatch can run. The occlusion seam is
    ///     <see cref="FoliageCullPass.Prepare" />'s <c>occluders</c> parameter, waiting.
    /// </remarks>
    FoliageShaders? ResolveFoliageShaders(CompositorFrame frame) {
        var countKey = EffectKey.Of(
            "FoliageCull",
            [KeyValuePair.Create("Place", "false"), KeyValuePair.Create("Occlusion", "false")]
        );

        var placeKey = EffectKey.Of(
            "FoliageCull",
            [KeyValuePair.Create("Place", "true"), KeyValuePair.Create("Occlusion", "false")]
        );

        // The draw follows the surface's mode, on the grass's terms: the lit instances take the
        // split decision with them, and the cull is mode-blind.
        var drawKey = Lit
            ? EffectKey.Of("FoliageLit", [KeyValuePair.Create("SplitOutputs", Split ? "true" : "false")])
            : EffectKey.Of("Foliage");

        if (frame.Effects.Resolve(countKey) is not { } countEffect
            || frame.Effects.Resolve(placeKey) is not { } placeEffect
            || frame.Effects.Resolve(drawKey) is not { } drawEffect) {
            return null;
        }

        var count = Modules!.ModuleOf(countEffect, ShaderStage.Compute);
        var place = Modules.ModuleOf(placeEffect, ShaderStage.Compute);

        var draw = new TerrainShaders(
            Modules.ModuleOf(drawEffect, ShaderStage.Vertex),
            Modules.ModuleOf(drawEffect, ShaderStage.Fragment)
        );

        if (!count.IsValid || !place.IsValid || !draw.IsValid) {
            return null;
        }

        return new(count, place, draw);
    }

    readonly record struct FoliageShaders(ShaderHandle Count, ShaderHandle Place, TerrainShaders Draw);

    /// <summary>Everything one foliage volume draws with, kept across frames.</summary>
    /// <remarks>
    ///     Per volume rather than per frame because the cull pass owns the uploaded instance table
    ///     — <see cref="TerrainDrawSet" />'s argument, with megabytes of records for an atlas.
    /// </remarks>
    sealed class FoliageStand : IDisposable {
        readonly IGraphicsDevice device;

        public FoliageStand(IGraphicsDevice device, FoliageCullPass cull, FoliageDrawPass draw, FoliageStreamer? streamer) {
            this.device = device;
            Cull = cull;
            Draw = draw;
            Streamer = streamer;
        }

        public FoliageCullPass Cull { get; }

        public FoliageDrawPass Draw { get; }

        /// <summary>The cell streamer, or null for a volume that fits the tier's budget whole.</summary>
        public FoliageStreamer? Streamer { get; }

        /// <summary>The volume's reprojection, or null while the frame has no motion plane.</summary>
        public FoliageVelocityPass? Velocity { get; set; }

        /// <summary>Each resolved type's device mesh, by palette index.</summary>
        public Dictionary<int, FoliageMesh> Meshes { get; } = [];

        /// <summary>One draw template per resolved type, in arrival order.</summary>
        public List<FoliageDraw> DrawList { get; } = [];

        /// <summary>The types still waiting for a mesh, or holding one nothing can resolve.</summary>
        public HashSet<int> Missing { get; } = [];

        /// <summary>How many draw templates the last upload covered. Negative forces the first.</summary>
        public int UploadedDraws { get; set; } = -1;

        /// <summary>And how many instances the volume held then.</summary>
        public int UploadedInstances { get; set; } = -1;

        public void Dispose() {
            foreach (var mesh in Meshes.Values) {
                device.Destroy(mesh.Indices);
                device.Destroy(mesh.Vertices);
            }

            Meshes.Clear();
            Velocity?.Dispose();
            Draw.Dispose();
            Cull.Dispose();
            Streamer?.Dispose();
        }
    }

    /// <summary>The weight layer a name refers to, or negative for none.</summary>
    static int LayerOf(TerrainMap terrain, string? name) {
        if (name is not { Length: > 0 }) {
            return -1;
        }

        for (var layer = 0; layer < terrain.Weights.LayerCount; layer++) {
            if (string.Equals(terrain.Weights.LayerOf(layer).Name, name, StringComparison.Ordinal)) {
                return layer;
            }
        }

        return -1;
    }

    /// <summary>Resolves the surface's two stages, once per mode, through the frame's effect system.</summary>
    /// <remarks>
    ///     The default splat permutation — four layer slots, no height blend — in whichever shader
    ///     the detected mode names: the preview, or <c>TerrainLit</c> keyed by the split and the
    ///     cluster decisions. A terrain whose splat wants more compiles the same shader at other
    ///     values, and routing that through here is still owed.
    /// </remarks>
    bool ResolveTerrainShaders(CompositorFrame frame) {
        if (terrainShaders.IsValid) {
            return true;
        }

        var key = Lit
            ? EffectKey.Of(
                "TerrainLit",
                [
                    KeyValuePair.Create("SplitOutputs", Split ? "true" : "false"),
                    KeyValuePair.Create("UseClusteredLights", ClusteredLights ? "true" : "false")
                ]
            )
            : EffectKey.Of("Terrain");

        if (frame.Effects.Resolve(key) is not { } effect) {
            return false;
        }

        terrainShaders = new(
            Modules!.ModuleOf(effect, ShaderStage.Vertex),
            Modules.ModuleOf(effect, ShaderStage.Fragment)
        );

        return terrainShaders.IsValid;
    }

    /// <summary>Resolves the stack's three velocity shaders, once, through the frame's effect system.</summary>
    /// <remarks>
    ///     All three together rather than each on first need, because the answer gates one decision:
    ///     a frame whose grass reprojects while its terrain cannot yet would ghost the ground under
    ///     sharp blades, which reads as a worse bug than both ghosting for one more compile.
    /// </remarks>
    bool ResolveVelocityShaders(CompositorFrame frame) {
        if (terrainVelocityShaders.IsValid && grassVelocityShaders.IsValid && foliageVelocityShaders.IsValid) {
            return true;
        }

        if (frame.Effects.Resolve(EffectKey.Of("TerrainVelocity")) is not { } terrain
            || frame.Effects.Resolve(EffectKey.Of("GrassVelocity")) is not { } grass
            || frame.Effects.Resolve(EffectKey.Of("FoliageVelocity")) is not { } foliage) {
            return false;
        }

        terrainVelocityShaders = new(
            Modules!.ModuleOf(terrain, ShaderStage.Vertex),
            Modules.ModuleOf(terrain, ShaderStage.Fragment)
        );

        grassVelocityShaders = new(
            Modules.ModuleOf(grass, ShaderStage.Vertex),
            Modules.ModuleOf(grass, ShaderStage.Fragment)
        );

        foliageVelocityShaders = new(
            Modules.ModuleOf(foliage, ShaderStage.Vertex),
            Modules.ModuleOf(foliage, ShaderStage.Fragment)
        );

        return terrainVelocityShaders.IsValid && grassVelocityShaders.IsValid && foliageVelocityShaders.IsValid;
    }

    /// <summary>Resolves one grass rule's three shaders and the output its pipeline draws into.</summary>
    /// <remarks>
    ///     Two compute variants — the scatter and its <c>Arguments</c> phase — keyed by the rule's
    ///     own flags, because <c>LayerBound</c> and <c>RandomYaw</c> compile the sampling in or
    ///     out. The draw is one variant for every field.
    /// </remarks>
    GrassFieldShaders? GrassShaders(CompositorFrame frame, in GrassType type) {
        var bound = type.NeedsSurfaceWeight ? "true" : "false";
        var yaw = type.RandomYaw ? "true" : "false";

        var scatterKey = EffectKey.Of(
            "GrassScatter",
            [
                KeyValuePair.Create("Arguments", "false"),
                KeyValuePair.Create("LayerBound", bound),
                KeyValuePair.Create("RandomYaw", yaw)
            ]
        );

        var argumentKey = EffectKey.Of(
            "GrassScatter",
            [
                KeyValuePair.Create("Arguments", "true"),
                KeyValuePair.Create("LayerBound", bound),
                KeyValuePair.Create("RandomYaw", yaw)
            ]
        );

        // The draw follows the surface's mode: the lit blades take the split decision with them,
        // and the scatter is mode-blind — a blade's placement does not depend on what lights it.
        var drawKey = Lit
            ? EffectKey.Of("GrassLit", [KeyValuePair.Create("SplitOutputs", Split ? "true" : "false")])
            : EffectKey.Of("Grass");

        if (frame.Effects.Resolve(scatterKey) is not { } scatterEffect
            || frame.Effects.Resolve(argumentKey) is not { } argumentEffect
            || frame.Effects.Resolve(drawKey) is not { } drawEffect) {
            return null;
        }

        var scatter = Modules!.ModuleOf(scatterEffect, ShaderStage.Compute);
        var arguments = Modules.ModuleOf(argumentEffect, ShaderStage.Compute);

        var draw = new TerrainShaders(
            Modules.ModuleOf(drawEffect, ShaderStage.Vertex),
            Modules.ModuleOf(drawEffect, ShaderStage.Fragment)
        );

        if (!scatter.IsValid || !arguments.IsValid || !draw.IsValid) {
            return null;
        }

        return new(scatter, arguments, draw);
    }

    readonly record struct GrassFieldShaders(ShaderHandle Scatter, ShaderHandle Arguments, TerrainShaders Draw);

    /// <summary>Everything one heightfield draws with, kept across frames.</summary>
    /// <remarks>
    ///     Per terrain rather than per frame because a <see cref="TerrainRenderer" /> owns an atlas
    ///     — the editor's <c>ScenePresenter</c> states the cost of rebuilding one.
    /// </remarks>
    sealed class TerrainDrawSet : IDisposable {
        public TerrainDrawSet(TerrainRenderer surface, float nearRange) {
            Surface = surface;
            NearRange = nearRange;
        }

        public TerrainRenderer Surface { get; }

        public float NearRange { get; }

        public GrassField? Field { get; set; }

        /// <summary>The caster over this surface, or null while nothing casts. See <see cref="CasterFor" />.</summary>
        public TerrainCasterPass? Caster { get; set; }

        /// <summary>The reprojection over this surface, or null while the frame has no motion plane.</summary>
        public TerrainVelocityPass? Velocity { get; set; }

        public void Dispose() {
            Velocity?.Dispose();
            Caster?.Dispose();
            Field?.Dispose();
            Surface.Streaming?.Dispose();
            Surface.Dispose();
        }
    }

    /// <summary>One grass rule over one terrain: the ring, the dispatch and the draw.</summary>
    sealed class GrassField : IDisposable {
        public GrassField(GrassType type, int layer, GrassResidency residency, GrassDispatch dispatch, GrassDrawPass draw) {
            Type = type;
            Layer = layer;
            Residency = residency;
            Dispatch = dispatch;
            Draw = draw;
        }

        public GrassType Type { get; }

        public int Layer { get; }

        public GrassResidency Residency { get; }

        public GrassDispatch Dispatch { get; }

        public GrassDrawPass Draw { get; }

        /// <summary>The field's reprojection, or null while the frame has no motion plane.</summary>
        public GrassVelocityPass? Velocity { get; set; }

        /// <summary>This frame's resident cells, materialised once for the dispatch.</summary>
        public List<GrassSlot> Resident { get; } = [];

        public void Dispose() {
            Velocity?.Dispose();
            Draw.Dispose();
            Dispatch.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var set in sets.Values) {
            set?.Dispose();
        }

        foreach (var stand in stands.Values) {
            stand?.Dispose();
        }

        sets.Clear();
        stands.Clear();
        drawn.Clear();
        foliageDrawn.Clear();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "Terrain" : Name;
}
