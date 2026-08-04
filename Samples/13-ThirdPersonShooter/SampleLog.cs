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
        EventId = 14052,
        Level = LogLevel.Information,
        Message = "Composited {Instances} distance field(s) from the same boxes into the clipmap. "
            + "Zero is ambient occlusion that marches a clipmap holding nothing and answers "
            + "\"nothing is near\" everywhere \u2014 which is a frame with no occlusion in it, reported "
            + "by every counter as a success."
    )]
    public static partial void DistanceFieldsBuilt(ILogger logger, int instances);

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
        Message = "The shared geometry holds {Vertices} vertex(es) and {Indices} index(es) over {Slices} slice(s), of "
            + "which {Uploaded} byte(s) reached the device. Zero indices is a frame that draws nothing; zero "
            + "bytes uploaded is a frame that draws whatever the allocator last left in that memory."
    )]
    public static partial void GeometrySummary(ILogger logger, int vertices, int indices, int slices, int uploaded);

    [LoggerMessage(
        EventId = 14048,
        Level = LogLevel.Information,
        Message = "Set 1 was filled with {Bytes} byte(s); the matrix at offset zero is {Sent}. This is what the "
            + "vertex stage multiplied by, so a matrix here that is not the camera's is geometry projected "
            + "through something else — which draws, and draws nowhere."
    )]
    public static partial void ViewBlockSummary(ILogger logger, int bytes, System.Numerics.Matrix4x4 sent);

    [LoggerMessage(
        EventId = 14049,
        Level = LogLevel.Information,
        Message = "Lighting: {Lights} punctual light(s), sun {Sun}, sky L00 {Sky} at intensity {Intensity}, "
            + "{Bound} of {Slots} probe slot(s) filled. All of these at zero is a surface lit by nothing, "
            + "which draws in black on a black clear and looks exactly like geometry that never arrived."
    )]
    public static partial void LightingSummary(
        ILogger logger,
        int lights,
        string sun,
        System.Numerics.Vector3 sky,
        float intensity,
        int bound,
        int slots
    );

    [LoggerMessage(
        EventId = 14051,
        Level = LogLevel.Information,
        Message = "Post-process volumes: {Contributing} of {Total} reaching the camera, and the fold {State}. "
            + "⚠ A volume that is placed and not contributing is this feature's commonest failure — a zero "
            + "weight, zero extents, or a camera outside the blend radius — and it looks exactly like one that "
            + "is not wired up at all."
    )]
    public static partial void VolumeSummary(ILogger logger, int contributing, int total, string state);

    [LoggerMessage(
        EventId = 14050,
        Level = LogLevel.Information,
        Message = "The level holds {Renderables} entity(s) with a MeshRenderable, of which {Placed} also have a "
            + "WorldTransform, and {Lights} light(s) of which {LitPlaced} do. Extraction queries both, so an "
            + "entity with only a LocalTransform is one the frame cannot see."
    )]
    public static partial void LevelSummary(ILogger logger, int renderables, int placed, int lights, int litPlaced);

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

    [LoggerMessage(
        EventId = 14051,
        Level = LogLevel.Information,
        Message = "The sky says the sun is {Illuminance} lux, tinted ({Red}, {Green}, {Blue}), and the level's "
            + "directional light now says the same. A scene that named both a sun direction and a sun "
            + "brightness could be a sunset sky over a noon sun, and nothing would report it."
    )]
    public static partial void SunFromSky(ILogger logger, float illuminance, float red, float green, float blue);

    [LoggerMessage(
        EventId = 14052,
        Level = LogLevel.Information,
        Message = "{Effects} lamp(s) are drifting embers and {Waiting} are waiting for one; {Particles} "
            + "particle(s) were expanded last frame, through {Sets} particle material set(s). Zero running "
            + "with none waiting means no entity carries a !VfxEmitter; waiting that never falls means the "
            + "effect it names is one nothing shipped; running but no particles means the extraction is not "
            + "stepping them; particles but no sets means every draw was skipped — which is the one that "
            + "looks exactly like a level with no effects in it."
    )]
    public static partial void EmberSummary(ILogger logger, int effects, int particles, int waiting, int sets);

    [LoggerMessage(
        EventId = 14053,
        Level = LogLevel.Information,
        Message = "GI wired: {Cards} surface card(s) over the level's boxes ({Dropped} dropped by the atlas), "
            + "{Captured} texel(s) captured, and the load-time radiosity settled at a change of {Change}. "
            + "Zero captured is a cache of cards whose rays all missed — bounced light that quietly "
            + "reverts to the sky-only answer, with every later counter reporting success."
    )]
    public static partial void IlluminationWired(ILogger logger, int cards, int dropped, int captured, float change);

    [LoggerMessage(
        EventId = 14054,
        Level = LogLevel.Information,
        Message = "GI frame: {Bricks} irradiance brick(s) filled, {Bounces} cache bounce(s) recorded, culled "
            + "on the device: {CulledOnDevice}. Cache light: {Light}. Cache bounce: {Bounce}. Zero bricks "
            + "after a run of frames is a probe field going stale, and a skip reason here is the dispatch's "
            + "own explanation rather than a guess."
    )]
    public static partial void IlluminationSummary(
        ILogger logger,
        int bricks,
        int bounces,
        bool culledOnDevice,
        string light,
        string bounce
    );
}
