// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Spot and point light shadows — docs/plan/06 § Lighting.
/// </summary>
/// <remarks>
///     Where a directional light has no position and a cascade has to be <em>invented</em> from the
///     camera, a punctual light already is a volume: a spot's shadow frustum is its cone and a
///     point's is six of them. So the tests here are about coverage and cost rather than about
///     stability — there is nothing to stabilise, because nothing moves when the camera does.
/// </remarks>
public class PunctualShadowTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- The projections ----------------------------------------------------

    /// <summary>
    ///     The six cube faces cover every direction, with no gap.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The property that makes a cube map a cube map, and the one thing about point-light
    ///         shadows that can be wrong in a way no single-face test would catch: a wrong up vector
    ///         or a field of view that is not exactly 90° leaves a seam, and a seam in a shadow cube
    ///         is light through a wall along one line.
    ///     </para>
    ///     <para>
    ///         Ten thousand random directions, each of which must be inside at least one face's
    ///         frustum.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_direction_lands_in_some_cube_face() {
        var faces = Enum.GetValues<CubeFace>()
            .Select(face => new BoundingFrustum(ShadowProjections.Cube(Vector3.Zero, face, 100f)))
            .ToArray();

        Gen.Select(Gen.Float[-1f, 1f], Gen.Float[-1f, 1f], Gen.Float[-1f, 1f])
            // Long enough to normalise. A vector whose length underflows to zero normalises to the
            // origin, which is in no frustum and is a fact about float, not about cube maps.
            .Where(
                direction =>
                    (direction.Item1 * direction.Item1)
                    + (direction.Item2 * direction.Item2)
                    + (direction.Item3 * direction.Item3) > 0.01f
            )
            .Sample(
                direction => {
                    var point = Vector3.Normalize(new(direction.Item1, direction.Item2, direction.Item3)) * 10f;

                    Assert.True(
                        faces.Any(face => face.Contains(point)),
                        $"{point} is in none of the six faces"
                    );
                }
            );
    }

    /// <summary>A spot light's frustum is its cone, and points outside the cone are outside it.</summary>
    /// <remarks>
    ///     The field of view is twice the outer half-angle exactly. Fitting a wider frustum "to be
    ///     safe" costs resolution everywhere for a margin that lights nothing.
    /// </remarks>
    [Fact]
    public void A_spot_frustum_is_its_cone() {
        var direction = new Vector3(0f, 0f, -1f);
        var frustum = new BoundingFrustum(ShadowProjections.Spot(Vector3.Zero, direction, 0.3f, 100f));

        // Ten metres away, the cone's radius is 10·tan(0.3) ≈ 3.09.
        Assert.True(frustum.Contains(new Vector3(0f, 0f, -10f)), "the cone's axis is outside its own frustum");
        Assert.True(frustum.Contains(new Vector3(2.5f, 0f, -10f)), "a point well inside the cone is outside");
        Assert.False(frustum.Contains(new Vector3(6f, 0f, -10f)), "a point well outside the cone is inside");
        Assert.False(frustum.Contains(new Vector3(0f, 0f, -200f)), "a point past the range is inside");
        Assert.False(frustum.Contains(new Vector3(0f, 0f, 10f)), "a point behind the light is inside");
    }

    /// <summary>A light pointing straight down still gets a usable frustum.</summary>
    /// <remarks>
    ///     The degenerate case for any look-at: an up vector parallel to the view direction produces
    ///     a matrix full of NaNs, and a spot light aimed at the floor is the most ordinary thing in
    ///     any scene.
    /// </remarks>
    [Fact]
    public void A_light_pointing_along_the_up_axis_is_not_degenerate() {
        var frustum = new BoundingFrustum(
            ShadowProjections.Spot(new(0f, 10f, 0f), new(0f, -1f, 0f), 0.4f, 50f)
        );

        Assert.True(frustum.Contains(new Vector3(0f, 0f, 0f)));
        Assert.False(frustum.Contains(new Vector3(0f, 20f, 0f)));
    }

    /// <summary>A point light costs six times a spot light, and the number says so.</summary>
    [Fact]
    public void A_point_light_is_six_tiles_and_a_spot_is_one() {
        Assert.Equal(6, ShadowProjections.TileCount(LightKind.Point));
        Assert.Equal(1, ShadowProjections.TileCount(LightKind.Spot));
        Assert.Equal(0, ShadowProjections.TileCount(LightKind.Directional));
    }

    // --- The renderer -------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() { Key = key, Stages = [new(ShaderStage.Vertex, [1, 2, 3, 4], "main")] };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Caster { get; init; }
        public required PunctualShadowRenderer Shadows { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    Harness Build(int tilesPerSide = 4) {
        var system = new RenderSystem();

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        meshes.Add(materials);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles());

        var caster = system.AddStage(new("ShadowCaster"));

        var shadows = new PunctualShadowRenderer {
            Name = "Punctual",
            CasterStage = caster,
            Atlas = "PunctualAtlas",
            Resolution = 256,
            TilesPerSide = tilesPerSide
        };

        var size = shadows.AtlasSize;

        var description = new TextureDescription(
            PixelFormat.Depth32Float,
            size.X,
            size.Y,
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            Name: "PunctualAtlas"
        );

        var texture = device.CreateTexture(description);
        var compositor = new GraphicsCompositor(system) { Game = shadows, FrameSize = size };

        compositor.Imports["PunctualAtlas"] = new(texture, device.CreateTextureView(texture), description);

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Caster = caster,
            Shadows = shadows,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static void AddCaster(Harness h, Vector3 at, float radius = 1f) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(at, radius), Stages = h.Caster.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("DepthOnly"));
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        // Reset at the top of a frame rather than the bottom, so the graph a frame produced is still
        // there to be asked about afterwards — which is how a test sees what was culled.
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_spot_light_takes_one_tile_and_a_point_light_six() {
        using var h = Build();

        h.Shadows.Lights.Add(RenderLight.Spot(Vector3.Zero, new(0f, 0f, -1f), 20f, 0.2f, 0.4f, new Color3(1f)));
        h.Shadows.Lights.Add(RenderLight.Point(new(5f, 0f, 0f), 20f, new(1f)));

        h.Compositor.Collect();

        Assert.Equal(7, h.Shadows.Tiles.Count);
        Assert.Single(h.Shadows.Tiles, tile => tile.Face is null);
        Assert.Equal(6, h.Shadows.Tiles.Count(tile => tile.Face is not null));
    }

    /// <summary>A directional light in the list is skipped — it belongs to the cascade renderer.</summary>
    [Fact]
    public void A_directional_light_is_not_a_punctual_shadow() {
        using var h = Build();
        h.Shadows.Lights.Add(RenderLight.Directional(new(0f, -1f, 0f), new(1f)));

        h.Compositor.Collect();

        Assert.Empty(h.Shadows.Tiles);
    }

    /// <summary>Every tile is its own view over the caster stage, and they tile the atlas.</summary>
    [Fact]
    public void Each_tile_is_a_view_and_a_viewport() {
        using var h = Build();
        h.Shadows.Lights.Add(RenderLight.Point(Vector3.Zero, 20f, new(1f)));
        AddCaster(h, new(0f, 0f, -5f));

        Frame(h);

        Assert.Equal(6, h.Shadows.Tiles.Count);
        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));
        Assert.Equal(6, device.Recorder.CountOf(RecordedCommandKind.SetViewport));

        var corners = device.Recorder
            .OfKind(RecordedCommandKind.SetScissor)
            .Select(command => (command.C, command.D))
            .ToArray();

        Assert.Equal(6, corners.Distinct().Count());
    }

    /// <summary>A shadow pass has no colour attachment.</summary>
    [Fact]
    public void The_atlas_pass_writes_depth_only() {
        using var h = Build();
        h.Shadows.Lights.Add(RenderLight.Spot(Vector3.Zero, new(0f, 0f, -1f), 20f, 0.2f, 0.4f, new Color3(1f)));
        AddCaster(h, new(0f, 0f, -5f));

        Frame(h);

        var begin = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BeginRenderPass));

        Assert.Equal(0, begin.A);
        Assert.Equal(1, begin.B);
    }

    /// <summary>
    ///     A point light that does not fit is dropped whole, and counted.
    /// </summary>
    /// <remarks>
    ///     All six faces or none: a point light with four faces rendered is worse than one with none,
    ///     because the two missing directions are lit as though nothing occludes them — which reads
    ///     as light coming through a wall rather than as a missing shadow.
    /// </remarks>
    [Fact]
    public void A_light_that_does_not_fit_is_dropped_whole_and_counted() {
        // Four tiles in total: one point light fits, the second cannot.
        using var h = Build(tilesPerSide: 2);

        h.Shadows.Lights.Add(RenderLight.Point(Vector3.Zero, 20f, new(1f)));
        h.Shadows.Lights.Add(RenderLight.Point(new(5f, 0f, 0f), 20f, new(1f)));

        h.Compositor.Collect();

        Assert.Equal(4, h.Shadows.Capacity);
        Assert.Empty(h.Shadows.Tiles);
        Assert.Equal(2, h.Shadows.DroppedLights);
    }

    /// <summary>A spot light still fits where a point light did not.</summary>
    /// <remarks>
    ///     Tiles are allocated in tile units rather than per light, so the cheap light gets in behind
    ///     the expensive one. Reserving a cube's worth per light would have dropped this one too.
    /// </remarks>
    [Fact]
    public void A_spot_light_fits_where_a_point_light_did_not() {
        using var h = Build(tilesPerSide: 2);

        h.Shadows.Lights.Add(RenderLight.Point(Vector3.Zero, 20f, new(1f)));
        h.Shadows.Lights.Add(RenderLight.Spot(new(5f, 0f, 0f), new(0f, -1f, 0f), 20f, 0.2f, 0.4f, new Color3(1f)));

        h.Compositor.Collect();

        Assert.Single(h.Shadows.Tiles);
        Assert.Equal(1, h.Shadows.DroppedLights);
    }

    /// <summary>A caster only one face can see is drawn once, not six times.</summary>
    [Fact]
    public void A_caster_is_drawn_only_in_the_faces_that_see_it() {
        using var h = Build();
        h.Shadows.Lights.Add(RenderLight.Point(Vector3.Zero, 40f, new(1f)));

        // Well down −Z and small enough to be inside one face's frustum only.
        AddCaster(h, new(0f, 0f, -20f), radius: 0.5f);

        Frame(h);

        Assert.Equal(1, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }
}
