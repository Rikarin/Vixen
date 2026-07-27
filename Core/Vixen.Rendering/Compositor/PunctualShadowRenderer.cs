// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>One tile of the punctual shadow atlas: what it renders, and where it lives in it.</summary>
/// <param name="Light">Which light in the renderer's list.</param>
/// <param name="Face">Which cube face, or <see langword="null" /> for a spot light's single tile.</param>
/// <param name="ViewProjection">World to this tile's clip space.</param>
/// <param name="Scale">How much of the atlas the tile is, as a fraction.</param>
/// <param name="Offset">Where it starts in the atlas, as a fraction.</param>
public readonly record struct ShadowTile(
    int Light,
    CubeFace? Face,
    Matrix4x4 ViewProjection,
    Vector2 Scale,
    Vector2 Offset
);

/// <summary>
///     Renders spot and point light shadows into one atlas.
/// </summary>
/// <remarks>
///     <para>
///         The same idea as <see cref="ShadowMapRenderer" /> — a shadow map is a view, and an atlas
///         is one pass with a viewport per tile — applied to lights that have a position. What
///         differs is that nothing has to be fitted or stabilised: a spot light's shadow frustum
///         <em>is</em> its cone and a point light's is six of them, so the whole projection question
///         that cascades spend two hundred lines on does not arise.
///     </para>
///     <para>
///         <strong>A point light is six tiles and a spot light is one.</strong> That ratio is the
///         real cost of the two and it is worth seeing: six times the culling, six times the draws,
///         six times the atlas. It is also why the atlas is allocated in tile units rather than per
///         light — the alternative reserves a cube's worth of space for every light in case it turns
///         out to be one.
///     </para>
///     <para>
///         <strong>When the atlas runs out, lights are dropped and counted.</strong> Not silently:
///         <see cref="DroppedLights" /> is what turns "some shadows disappeared in the big fight"
///         into a number a profiler can show. Dropping the ones furthest down the list is arbitrary
///         and deliberate — importance ordering belongs to whoever fills the list, which is the only
///         place that knows what the scene is about.
///     </para>
/// </remarks>
public sealed class PunctualShadowRenderer : SceneRenderer {
    readonly List<RenderView> views = [];
    readonly List<ShadowTile> tiles = [];

    /// <summary>The stage that draws depth-only casters.</summary>
    public required RenderStage CasterStage { get; init; }

    /// <summary>The name of the atlas to render into.</summary>
    public string Atlas { get; set; } = string.Empty;

    /// <summary>The lights to shadow. Directional lights are skipped — they are the cascades'.</summary>
    public IList<RenderLight> Lights { get; } = [];

    /// <summary>One tile's side in texels.</summary>
    public int Resolution { get; set; } = 512;

    /// <summary>How many tiles the atlas is across, so it holds this squared.</summary>
    public int TilesPerSide { get; set; } = 4;

    /// <summary>The near plane every punctual shadow projection uses.</summary>
    public float NearPlane { get; set; } = 0.05f;

    /// <summary>The tiles this frame allocated.</summary>
    public IReadOnlyList<ShadowTile> Tiles => tiles;

    /// <summary>The views the tiles are drawn from.</summary>
    public IReadOnlyList<RenderView> Views => views;

    /// <summary>How many lights did not fit in the atlas this frame.</summary>
    public int DroppedLights { get; private set; }

    /// <summary>How many tiles the atlas holds.</summary>
    public int Capacity => Math.Max(TilesPerSide, 1) * Math.Max(TilesPerSide, 1);

    /// <summary>The atlas's size in texels, for whoever creates the texture.</summary>
    public Int2 AtlasSize => new(Math.Max(TilesPerSide, 1) * Resolution, Math.Max(TilesPerSide, 1) * Resolution);

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        tiles.Clear();
        DroppedLights = 0;

        var side = Math.Max(TilesPerSide, 1);
        var scale = new Vector2(1f / side, 1f / side);

        for (var index = 0; index < Lights.Count; index++) {
            var light = Lights[index];
            var needed = ShadowProjections.TileCount(light.Kind);

            if (needed == 0) {
                continue;
            }

            // All of a light's faces or none of them. A point light with four of its six faces
            // rendered is worse than one with none: the two missing directions are lit as though
            // nothing occludes them, which reads as light leaking through walls.
            if (tiles.Count + needed > Capacity) {
                DroppedLights++;
                continue;
            }

            for (var face = 0; face < needed; face++) {
                var slot = tiles.Count;
                var offset = new Vector2(slot % side * scale.X, slot / side * scale.Y);

                var projection = light.Kind == LightKind.Point
                    ? ShadowProjections.Cube(light.Position, (CubeFace)face, light.Range, NearPlane)
                    : ShadowProjections.Spot(
                        light.Position,
                        light.Direction,
                        light.OuterAngle,
                        light.Range,
                        NearPlane
                    );

                tiles.Add(
                    new(index, light.Kind == LightKind.Point ? (CubeFace)face : null, projection, scale, offset)
                );

                while (views.Count <= slot) {
                    views.Add(new($"{Name}[{views.Count}]"));
                }

                var view = views[slot];
                view.Frustum = new(projection);
                view.Position = light.Position;
                view.MaximumDistance = 0f;

                compositor.Use(view, CasterStage);
            }
        }
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (tiles.Count == 0 || Atlas.Length == 0) {
            return;
        }

        var atlas = frame.Texture(ToString(), Atlas);
        var format = frame.FormatOf(ToString(), Atlas);
        var side = Math.Max(TilesPerSide, 1);

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.DepthAttachment(atlas);

                pass.Execute(
                    graphContext => {
                        var context = frame.Context(graphContext.CommandList);
                        var previous = context.Output;
                        context.Output = new([], format);

                        for (var slot = 0; slot < tiles.Count; slot++) {
                            var x = slot % side * Resolution;
                            var y = slot / side * Resolution;

                            graphContext.CommandList.SetViewport(new(x, y, Resolution, Resolution));
                            graphContext.CommandList.SetScissor(new(x, y, Resolution, Resolution));

                            compositor.System.Record(views[slot], CasterStage, context);
                        }

                        context.Output = previous;
                    }
                );
            }
        );
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "PunctualShadows" : Name;
}
