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
        EventId = 14042,
        Level = LogLevel.Error,
        Message = "The level's material would not compile, so every object will draw with nothing: {Diagnostics}"
    )]
    public static partial void NoMaterial(ILogger logger, string diagnostics);

    [LoggerMessage(
        EventId = 14040,
        Level = LogLevel.Warning,
        Message = "No Raven/Library above the binary and no baked shaders, so every material will resolve to a "
            + "miss and the screen will be black. This is a development build run from outside the repository."
    )]
    public static partial void NoShaderLibrary(ILogger logger);

    [LoggerMessage(
        EventId = 14041,
        Level = LogLevel.Information,
        Message = "Drew {Objects} object(s) from {Meshes} loaded mesh(es) ({FailedMeshes} unresolved) using "
            + "{Variants} shader variant(s), with {Misses} miss(es) and {BoundMaterials} material set(s) bound. "
            + "Any of those at zero is a black screen: a miss draws nothing, and so does a material whose "
            + "per-material descriptor set was never written."
    )]
    public static partial void FrameSummary(
        ILogger logger,
        int objects,
        int meshes,
        int failedMeshes,
        int variants,
        int misses,
        int boundMaterials
    );

    [LoggerMessage(
        EventId = 14043,
        Level = LogLevel.Information,
        Message = "The frame's set 0 was written {Writes} time(s), and was last {Completeness}. Zero writes is a "
            + "black screen whatever the rest of the summary says: ForwardPlus declares thirteen bindings in "
            + "its per-frame set, EffectSetWriter fills every one or none, and a pass that binds no set 0 has "
            + "every draw in it refused."
    )]
    public static partial void SceneSetSummary(ILogger logger, int writes, string completeness);

    [LoggerMessage(
        EventId = 14044,
        Level = LogLevel.Warning,
        Message = "Nothing filled the frame's {Bindings}, so set 0 never bound and every draw in the shading "
            + "pass was refused. Whoever owns those resources — a compositor node, the scene's lighting, "
            + "the project itself — is not writing them into SceneConstants.Parameters."
    )]
    public static partial void SceneSetMissing(ILogger logger, string bindings);

    [LoggerMessage(
        EventId = 14045,
        Level = LogLevel.Information,
        Message = "The frame drew from {Position}, through {ViewProjection}. A view-projection still at identity "
            + "is a camera nothing extracted into, which draws the clear colour and nothing else however "
            + "many objects the summary above counted."
    )]
    public static partial void CameraSummary(ILogger logger, Core.Mathematics.Vector3 position, System.Numerics.Matrix4x4 viewProjection);

    [LoggerMessage(
        EventId = 14046,
        Level = LogLevel.Information,
        Message = "The shared geometry holds {Vertices} vertex(es) and {Indices} index(es) over {Slices} slice(s). "
            + "Zero indices is a frame that issues draws of nothing: the meshes resolved, the pipelines were "
            + "built and the sets were bound, and the rasteriser was handed an empty range."
    )]
    public static partial void GeometrySummary(ILogger logger, int vertices, int indices, int slices);

    [LoggerMessage(
        EventId = 14047,
        Level = LogLevel.Information,
        Message = "The frame holds {Count} render object(s), the first two at {A} and {B}, and recorded {Draws} draw(s) "
            + "over {Indices} index(es). Objects without draws is a mesh or a variant the loop skipped; draws "
            + "without a picture is geometry that missed the screen."
    )]
    public static partial void TransformSummary(ILogger logger, int count, System.Numerics.Vector3 a, System.Numerics.Vector3 b, int draws, long indices);

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
