// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Controls;
using Vixen.Video.Playback;

namespace Vixen.Video.Ui;

/// <summary>A video, as an element of a user interface.</summary>
/// <remarks>
///     <para>
///         Almost nothing, and that is the measure of whether the seam under it is right: a video in
///         an interface is a <c>SurfaceView</c> whose source happens to be a player, so all this adds
///         is the one thing a generic surface cannot know — how big the picture is meant to look.
///     </para>
///     <para>
///         ⚠ <b>The display size is read when the player is set, not on every frame.</b> It is a
///         property of the stream rather than of the picture, so it does not change while a video
///         plays; a stream that changes shape mid-play — legal in WebM, rare outside adaptive
///         streaming — is a case for setting the player again, which is what the decoder's
///         <c>FormatChanged</c> is telling somebody about anyway.
///     </para>
///     <para>
///         <b>It does not advance the video and it does not own it.</b> A player is driven by
///         <c>VideoSystem</c> or by whoever made it, and may be on screen twice; an element that
///         called <c>Update</c> would advance it once per place it appeared.
///     </para>
/// </remarks>
public partial class VideoView : SurfaceView {
    VideoPlayer? player;

    /// <inheritdoc />
    protected override string TagName => "video";

    /// <summary>What is playing. Null draws nothing.</summary>
    public VideoPlayer? Player {
        get => player;
        set {
            player = value;
            Source = value;

            var size = value?.DisplaySize ?? Int2.Zero;
            SourceSize = new Vector2(size.X, size.Y);
        }
    }
}
