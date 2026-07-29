// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Video.Ecs;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;

namespace Vixen.Video.Rendering;

/// <summary>Gets every video in a world onto the GPU and into the renderer, once a frame.</summary>
/// <remarks>
///     <para>
///         <b>The step <c>VideoSystem</c> names and does not do.</b> That system advances the players
///         in <c>SystemPhase.Update</c> and its own remarks say the picture it chose "is uploaded in
///         <c>SystemPhase.PreRender</c> and drawn in <c>SystemPhase.Render</c>". This is the middle
///         one — and it is a plain class rather than a <c>SystemBase</c> because it needs a command
///         list, and an ECS system in this engine is handed a world and a time and nothing else.
///         Inventing a graphics-shaped system context to hide two method calls would be a worse
///         answer than two method calls.
///     </para>
///     <para>
///         ⚠ <b><see cref="Upload" /> must be called outside a render pass and <see cref="Extract" />
///         before the renderer sorts.</b> The first records a buffer-to-texture copy and two
///         barriers, which is the one thing a Vulkan command list may not do inside a pass; the
///         second only fills dictionaries. They are separate for that reason and not for tidiness.
///     </para>
///     <para>
///         <b>It owns a texture per player, not per entity.</b> Two entities showing the same player —
///         a video on a wall and the same video in a mirror — decode once, upload once and draw
///         twice, which is the whole reason <c>VideoSurface</c> lets a player be shared.
///     </para>
/// </remarks>
public sealed class VideoSurfaceUploader : IDisposable {
    readonly QueryDescription placed = new QueryDescription().WithAll<VideoSurface, VideoScreenPlacement>();
    readonly Dictionary<VideoPlayer, VideoTexture> textures = [];
    readonly Dictionary<Entity, RenderObjectId> objects = [];
    readonly HashSet<VideoPlayer> players = [];
    readonly List<(Entity Entity, RenderObjectId Id)> stale = [];
    readonly List<Entity> seen = [];
    readonly IGraphicsDevice device;
    readonly RenderStageMask stages;

    bool disposed;

    /// <summary>Sets up an uploader.</summary>
    /// <param name="device">Where the textures live.</param>
    /// <param name="stage">Which stage videos are drawn in.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The stage is a constructor argument rather than a default, because there is no
    ///     defensible default.</b> An object's stage mask is what decides whether anything draws it at
    ///     all, and a guess that happened to be right on the compositor it was written against is a
    ///     video that silently draws nothing on every other one.
    /// </remarks>
    public VideoSurfaceUploader(IGraphicsDevice device, RenderStage stage) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(stage);

        this.device = device;
        stages = stage.Mask;
    }

    /// <summary>How many textures are currently held, which is one per distinct player.</summary>
    public int TextureCount => textures.Count;

    /// <summary>How many copies the last <see cref="Upload" /> recorded.</summary>
    /// <remarks>
    ///     ⚠ The claim this makes checkable is the one <c>VideoPlayer.FrameVersion</c> exists for: a
    ///     25 fps video in a 144 fps game costs about six uploads a second and not a hundred and
    ///     forty-four. A number equal to the video count every frame means the version check is not
    ///     working, which is invisible in the picture.
    /// </remarks>
    public int Uploads { get; private set; }

    /// <summary>The texture a player's frames are going into, if one has been made.</summary>
    /// <param name="player">The player.</param>
    /// <returns>Its texture, or null before the first upload.</returns>
    public VideoTexture? TextureFor(VideoPlayer player) =>
        player is not null && textures.TryGetValue(player, out var texture) ? texture : null;

    /// <summary>Copies every changed picture into its textures. Called outside a render pass.</summary>
    /// <param name="world">The world.</param>
    /// <param name="commands">A list that is not inside a pass.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public void Upload(World world, ICommandList commands) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Uploads = 0;
        CollectPlayers(world);

        foreach (var player in players) {
            if (!textures.TryGetValue(player, out var texture)) {
                texture = new VideoTexture(device, "video surface");
                textures[player] = texture;
            }

            if (texture.Upload(commands, player)) {
                Uploads++;
            }
        }
    }

    /// <summary>Puts this frame's videos into the renderer. Called during extraction.</summary>
    /// <param name="world">The world.</param>
    /// <param name="system">The renderer.</param>
    /// <param name="feature">The feature that will draw them.</param>
    /// <param name="surface">The target's extent, in the units the feature draws in.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>An entity keeps its render object across frames and gives it back when it stops
    ///     drawing.</b> A new object per frame would churn the store — every feature's parallel array
    ///     is keyed on a dense id — and never reclaiming one would leak a slot per cutscene that
    ///     played.
    /// </remarks>
    public void Extract(World world, RenderSystem system, VideoRenderFeature feature, Int2 surface) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(feature);
        ObjectDisposedException.ThrowIf(disposed, this);

        feature.Surface = surface;
        seen.Clear();

        foreach (var chunk in world.Chunks(placed)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];
                var placement = world.Get<VideoScreenPlacement>(entity);

                if (world.Get<VideoSurface>(entity).Player is not { } player
                    || !textures.TryGetValue(player, out var texture)
                    || texture.PlaneCount == 0) {
                    continue;
                }

                seen.Add(entity);
                Place(system, feature, entity, player, texture, in placement, surface);
            }
        }

        Retire(system, feature);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var texture in textures.Values) {
            texture.Dispose();
        }

        textures.Clear();
        objects.Clear();
    }

    /// <summary>Where a normalised area lands on a surface.</summary>
    /// <param name="area">The area, as a fraction of the surface. Zero-sized means all of it.</param>
    /// <param name="surface">The surface's extent.</param>
    /// <returns>The rectangle, in the surface's units.</returns>
    internal static Rectangle AreaOf(Rectangle area, Int2 surface) =>
        area.Width <= 0 || area.Height <= 0
            ? new Rectangle(0, 0, surface.X, surface.Y)
            : new Rectangle(
                area.X * surface.X,
                area.Y * surface.Y,
                area.Width * surface.X,
                area.Height * surface.Y
            );

    void Place(
        RenderSystem system,
        VideoRenderFeature feature,
        Entity entity,
        VideoPlayer player,
        VideoTexture texture,
        in VideoScreenPlacement placement,
        Int2 surface
    ) {
        var target = AreaOf(placement.Area, surface);
        var located = VideoFit.Place(placement.Scaling, player, target);

        if (!objects.TryGetValue(entity, out var id)) {
            id = system.Objects.Add(
                new RenderObject {
                    // ⚠ A sphere that culling cannot reject. A screen-space video is not in the world
                    // and has no bounds worth testing — giving it real ones would mean a cutscene that
                    // disappeared when the camera turned round.
                    Bounds = new BoundingSphere(Vector3.Zero, float.MaxValue),
                    Stages = stages,
                    FeatureIndex = feature.Index,
                    SortGroup = placement.Order,
                    IsAlive = true
                }
            );

            objects[entity] = id;
        }

        system.Objects[id].SortGroup = placement.Order;

        feature.Set(
            id,
            new VideoDraw(
                texture,
                located.Target,
                located.TextureScale,
                located.TextureOffset,
                placement.Tint,
                placement.Order
            )
        );
    }

    /// <summary>Gives back the objects of entities that stopped drawing this frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The store's slot goes back too, not just the feature's entry.</b> Forgetting the draw
    ///     and leaving the object alive would leave a live object that every cull, every sort and
    ///     every stage walks and no feature draws — invisible, and paid for once per frame for the
    ///     rest of the process.
    /// </remarks>
    void Retire(RenderSystem system, VideoRenderFeature feature) {
        if (objects.Count == seen.Count) {
            return;
        }

        stale.Clear();

        foreach (var (entity, id) in objects) {
            if (!seen.Contains(entity)) {
                stale.Add((entity, id));
            }
        }

        foreach (var (entity, id) in stale) {
            feature.Remove(id);
            system.Objects.Remove(id);
            objects.Remove(entity);
        }

        stale.Clear();
    }

    /// <summary>Every distinct player being drawn, without allocating a set per frame.</summary>
    void CollectPlayers(World world) {
        players.Clear();

        foreach (var chunk in world.Chunks(placed)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (world.Get<VideoSurface>(entities[index]).Player is { } player) {
                    players.Add(player);
                }
            }
        }
    }
}
