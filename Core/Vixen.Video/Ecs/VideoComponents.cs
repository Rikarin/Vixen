// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
