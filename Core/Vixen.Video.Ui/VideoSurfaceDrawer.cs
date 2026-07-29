// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Ui.Renderer;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;
using Vixen.Video.Rendering;

namespace Vixen.Video.Ui;

/// <summary>Draws a video where a user interface asked for a picture.</summary>
/// <remarks>
///     <para>
///         <b>The whole of what "a video in the UI" costs.</b> An element names a source, the draw
///         list carries it as an <see langword="object" />, and this is what recognises one — a
///         <see cref="VideoPlayer" /> or the <see cref="VideoTexture" /> behind it — and hands it to
///         <see cref="VideoRenderer" />. Everything else was already there.
///     </para>
///     <para>
///         ⚠ <b>It does no aspect fitting, deliberately.</b> <c>SurfaceView</c> has already decided
///         the rectangle — that is what <c>SurfaceFit</c> is — and doing it twice would either
///         letterbox inside a letterbox or, worse, disagree. What arrives here is the rectangle the
///         picture goes in, exactly.
///     </para>
///     <para>
///         ⚠ <b>Registered on <c>UiRenderer.SurfaceDrawers</c> and not owned by it.</b> The renderer
///         it draws through, and the textures behind the players, belong to whoever set up the frame
///         — usually a <see cref="VideoSurfaceUploader" />, whose <c>TextureFor</c> is exactly the
///         resolver this wants.
///     </para>
/// </remarks>
/// <param name="renderer">What draws the planes.</param>
/// <param name="textures">Where a player's uploaded planes are, or null if it has none yet.</param>
public sealed class VideoSurfaceDrawer(VideoRenderer renderer, Func<VideoPlayer, VideoTexture?> textures)
    : IUiSurfaceDrawer {
    readonly VideoRenderer renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    readonly Func<VideoPlayer, VideoTexture?> textures =
        textures ?? throw new ArgumentNullException(nameof(textures));

    /// <summary>How many surfaces this recognised but could not draw yet.</summary>
    /// <remarks>
    ///     A player whose first frame has not been uploaded is the ordinary case for one or two frames
    ///     after a cutscene starts, and it is not an error — the element draws nothing and the frame
    ///     after it draws a picture. A number that keeps climbing means the uploader is not being
    ///     run, which is otherwise indistinguishable from a video that never decoded.
    /// </remarks>
    public int NotReady { get; private set; }

    /// <inheritdoc />
    public bool Draw(ICommandList commands, in UiSurfaceDraw draw) {
        var texture = draw.Source switch {
            VideoTexture ready => ready,
            VideoPlayer player => textures(player),
            _ => null
        };

        if (texture is null) {
            // ⚠ False for a source this does not recognise — that is how several drawers chain — and
            // false for one it does recognise but cannot draw. The two are different situations and
            // the return value cannot distinguish them, which is what `NotReady` is for.
            if (draw.Source is VideoPlayer) {
                NotReady++;
            }

            return false;
        }

        return renderer.Record(
            commands,
            new VideoDraw(
                texture,
                draw.Rectangle,
                Vector2.One,
                Vector2.Zero,
                draw.Tint
            ),
            draw.Surface
        );
    }
}
