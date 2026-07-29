// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;

namespace Vixen.Video.Ecs;

/// <summary>An entity that is playing a video.</summary>
/// <remarks>
///     <para>
///         <b>A managed component, and unavoidably one.</b> A player owns a decoder, a thread and a
///         pool of three-megabyte buffers; none of that fits in a chunk, and pretending otherwise
///         would mean an index into a side table that the ECS already implements better than this
///         module would. It is the same call <c>Behavior</c> and <c>Material</c> make, for the same
///         reason, and it costs one indirection on a component that is touched once a frame per
///         video — not once a frame per thousand entities.
///     </para>
///     <para>
///         Adding this component is what makes an entity's video advance, and removing it or
///         destroying the entity is what stops it. The player is <em>not</em> disposed when the
///         component goes away: it was constructed by whoever put it here, may be shared between
///         entities, and disposing something the ECS did not create would be the ECS deciding a
///         lifetime it was never told about.
///     </para>
/// </remarks>
public struct VideoSurface {
    /// <summary>The player. Null is legal and means the entity is not playing anything yet.</summary>
    public VideoPlayer? Player;

    /// <summary>Whether to call <see cref="VideoPlayer.Play" /> the first time the system sees it.</summary>
    public bool PlayOnStart;

    /// <summary>
    ///     Whether the system should keep the player's own loop flag in step with
    ///     <see cref="Loop" />.
    /// </summary>
    /// <remarks>
    ///     Off by default, so a player configured in code is not overwritten by a component that was
    ///     never touched. On, and looping becomes something a prefab or a save file carries.
    /// </remarks>
    public bool OverridesLoop;

    /// <summary>Whether the video restarts when it ends, when <see cref="OverridesLoop" /> is set.</summary>
    public bool Loop;

    /// <summary>Set by the system once it has applied <see cref="PlayOnStart" />.</summary>
    public bool Started;
}

/// <summary>Where on the screen an entity's video is drawn.</summary>
/// <remarks>
///     <para>
///         <b>Separate from <see cref="VideoSurface" /> because playing and drawing are separate
///         things.</b> A video with no placement still decodes — which is what a game wants for the
///         one it is about to cut to, and for the one whose sound is playing under a menu — and a
///         placement with no player draws nothing. Adding this component is what puts the picture on
///         the screen.
///     </para>
///     <para>
///         ⚠ <b>Normalised, not pixels, and that is what makes it survive a resize.</b> A cutscene
///         written as <c>(0, 0, 1, 1)</c> is full-screen on every display; the same thing written in
///         pixels is full-screen on the display it was authored on. The <see cref="Area" /> is a
///         fraction of the target, with the origin at the top left.
///     </para>
/// </remarks>
public struct VideoScreenPlacement {
    /// <summary>The fraction of the target to draw in. A zero-sized area means the whole of it.</summary>
    /// <remarks>
    ///     ⚠ Zero meaning "everything" is a sentinel and it is the right one here: a component's
    ///     default is all-zeroes, an area of zero size draws nothing at all, and the overwhelmingly
    ///     common case for a video is that it covers the screen. A default that drew nothing would
    ///     make adding the component look like it had failed.
    /// </remarks>
    public Rectangle Area;

    /// <summary>What to do when the picture and the area are different shapes.</summary>
    public VideoScaling Scaling;

    /// <summary>Where it sits among the videos on screen, lowest drawn first.</summary>
    public uint Order;

    /// <summary>Multiplied into the colour. A default-constructed placement is fully transparent.</summary>
    /// <remarks>
    ///     ⚠ Unlike <see cref="Area" />, this has no sentinel: an alpha of zero means invisible and
    ///     that is a thing somebody wants to be able to say — a cutscene fading in says it once a
    ///     frame. <see cref="Opaque" /> is what a caller building one by hand should start from.
    /// </remarks>
    public Color4 Tint;

    /// <summary>A placement covering the whole target, untinted.</summary>
    public static VideoScreenPlacement Opaque =>
        new() { Scaling = VideoScaling.Contain, Tint = Color4.White };
}

/// <summary>What the player did this frame.</summary>
/// <remarks>
///     Written by <see cref="VideoSystem" /> and never read by it, which is the same shape
///     <c>NavigationState</c> has: it exists so that game code, a UI binding and an upload pass can
///     ask what is on screen without touching the player — and, more to the point, without needing a
///     reference to a managed component to find out whether a cutscene has finished.
/// </remarks>
public struct VideoPlaybackInfo {
    /// <summary>What the player is doing.</summary>
    public VideoPlaybackState State;

    /// <summary>Where playback has got to.</summary>
    public TimeSpan Position;

    /// <summary>Bumped whenever the picture changed. What an upload pass compares against.</summary>
    public uint FrameVersion;

    /// <summary>How many frames have been skipped for being late, since the video started.</summary>
    public long FramesDropped;

    /// <summary>How many updates found nothing decoded, since the video started.</summary>
    public long DecodeStalls;
}
