// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>What the sample logs, with the ids from <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     Its own ids rather than a sibling's, even where a message reads the same — an id is only
///     useful in a support log if it names exactly one call site.
/// </remarks>
static partial class SampleLog {
    [LoggerMessage(
        EventId = 14031,
        Level = LogLevel.Information,
        Message = "Loaded scene '{Scene}' with {Entities} entities."
    )]
    public static partial void SceneLoaded(ILogger logger, string scene, int entities);

    [LoggerMessage(
        EventId = 14032,
        Level = LogLevel.Warning,
        Message = "Nothing is published at '{Address}'. The level is empty; run the content build."
    )]
    public static partial void NoScene(ILogger logger, string address);

    [LoggerMessage(
        EventId = 14033,
        Level = LogLevel.Warning,
        Message = "This build shipped no content, so there is no level, no sound and no input map."
    )]
    public static partial void NoContent(ILogger logger);

    [LoggerMessage(
        EventId = 14034,
        Level = LogLevel.Information,
        Message = "Built {Colliders} collider(s) from the level's authored boxes, over {Shapes} registered shape(s)."
    )]
    public static partial void CollisionBuilt(ILogger logger, int colliders, int shapes);

    [LoggerMessage(
        EventId = 14035,
        Level = LogLevel.Information,
        Message = "Rebuilt '{Address}' with the distance field, the probe field and the virtualized path in it. "
            + "The first build ran before this game existed and every field node in it captured a null."
    )]
    public static partial void FrameRebuilt(ILogger logger, string address);

    [LoggerMessage(
        EventId = 14036,
        Level = LogLevel.Information,
        Message = "Player {Slot} spawned at {Position}, possessing its pawn."
    )]
    public static partial void PlayerSpawned(ILogger logger, int slot, Core.Mathematics.Vector3 position);

    [LoggerMessage(
        EventId = 14037,
        Level = LogLevel.Warning,
        Message = "No input map at '{Address}' ({Reason}). The player will stand still, which is what a "
            + "controller with no source does rather than a crash."
    )]
    public static partial void NoInput(ILogger logger, string address, string reason);

    [LoggerMessage(
        EventId = 14039,
        Level = LogLevel.Information,
        Message = "Ran {Frames} frame(s). The player finished at {Position}, {Mode}, having fired {Shots} shot(s) "
            + "and respawned {Respawns} time(s)."
    )]
    public static partial void RunSummary(
        ILogger logger,
        int frames,
        Core.Mathematics.Vector3 position,
        Physics.Characters.CharacterMoveMode mode,
        int shots,
        int respawns
    );

    [LoggerMessage(
        EventId = 14038,
        Level = LogLevel.Information,
        Message = "Loaded {Clips} sound(s); {Missing} were not published."
    )]
    public static partial void SoundsLoaded(ILogger logger, int clips, int missing);
}
