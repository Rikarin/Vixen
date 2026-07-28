// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Video.Playback;

namespace Vixen.Video.Ecs;

/// <summary>Advances every video in a world, once a frame.</summary>
/// <remarks>
///     <para>
///         Small on purpose. The decoding happens on the player's own thread and the choosing of a
///         frame happens in <see cref="VideoPlayer.Update" />; what this adds is that the set of
///         videos being advanced is a query rather than a list somebody maintains, so a cutscene
///         entity that is spawned starts playing and one that is destroyed stops.
///     </para>
///     <para>
///         <b>It runs in <see cref="SystemPhase.Update" />, before anything that draws.</b> The
///         picture chosen here is uploaded in <see cref="SystemPhase.PreRender" /> and drawn in
///         <see cref="SystemPhase.Render" />, all within the frame — which is what makes
///         <c>VideoPlayer.CurrentFrame</c>'s "valid until the next update" contract safe to rely on.
///     </para>
///     <para>
///         <b>The delta is the game's, not the wall clock's.</b> A video in a paused game pauses and
///         a video in a slow-motion game runs slowly, because the clock it advances is fed
///         <c>context.Time.DeltaSeconds</c>. A video with an audio track ignores all of this and
///         follows the sound — see <c>VideoClock.Master</c>.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Update)]
public sealed class VideoSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription surfaces = new QueryDescription().WithAll<VideoSurface>();

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>NavigationSystem</c> gives: naming a
    ///     component in a generic call is what assigns it an id, and an attribute can only look one
    ///     up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Write<VideoSurface>()
        .Write<VideoPlaybackInfo>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Step(context.World, TimeSpan.FromSeconds(context.Time.DeltaSeconds));

        return dependency;
    }

    /// <summary>Advances every video in a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="delta">How long the frame was.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can step the videos without standing up a runner.</remarks>
    public void Step(World world, TimeSpan delta) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(surfaces)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];

                // Managed components live in the world's store rather than in the chunk, so this is
                // one lookup per video rather than a span per chunk. At one video on screen and
                // rarely more, that is not a number worth designing around.
                ref var surface = ref world.Get<VideoSurface>(entity);

                if (surface.Player is not { } player) {
                    continue;
                }

                if (surface.OverridesLoop) {
                    player.Loop = surface.Loop;
                }

                if (surface.PlayOnStart && !surface.Started) {
                    player.Play();
                    surface.Started = true;
                }

                player.Update(delta);

                if (!world.Has<VideoPlaybackInfo>(entity)) {
                    continue;
                }

                ref var info = ref world.Get<VideoPlaybackInfo>(entity);

                info.State = player.State;
                info.Position = player.Position;
                info.FrameVersion = player.FrameVersion;
                info.FramesDropped = player.FramesDropped;
                info.DecodeStalls = player.DecodeStalls;
            }
        }
    }
}
