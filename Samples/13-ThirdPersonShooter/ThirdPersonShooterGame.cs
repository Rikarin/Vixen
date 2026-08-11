// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Physics.Ecs;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>A third-person shooter, assembled from parts the engine already has.</summary>
/// <remarks>
///     <para>
///         <b>What this sample is for.</b> Every other sample here opens a device and issues draws by
///         hand; this one is a <i>project</i> — a <c>.vxproj</c> the editor opens, an <c>Assets/</c>
///         the content build imports, and one line of <c>Program.cs</c>. It exists to prove the join
///         works end to end, which is a different claim from any one subsystem working.
///     </para>
///     <para>
///         The player half is [29](../../docs/plan/29-players-and-possession.md): a controller that
///         outlives its pawn, a <c>MoveIntent</c> that is the only thing input, physics and the wire
///         agree on, and a camera rig that follows the player through a death because the thing that
///         knows where they are looking did not die with them.
///     </para>
///     <para>
///         ⚠ <b>The content is placeholder and says so.</b> Boxes, synthesised WAVs and hand-keyed
///         transform curves, all generated and committed. <c>.obj</c> carries no rig, so the character
///         is <i>segmented</i> — parts parented into a skeleton rather than skinned to one — which is
///         how Quake 1 and Lego characters work. Swapping in a rigged glTF changes these files and
///         none of the code, which is the property worth having from a sample.
///     </para>
/// </remarks>
public sealed class ThirdPersonShooterGame : Game {
    Arena? arena;
    PlayerRig? player;

    /// <summary>What the frame is called in content, so the host loads it instead of its built-in.</summary>
    /// <remarks>
    ///     The address the content build publishes <c>Assets/Frame.vxcompositor</c> under.
    ///     <c>AppGraphics</c> falls back to its own one-pass frame if this does not resolve, and says
    ///     so in the log rather than drawing nothing — which is why a typo here is a warning and not a
    ///     black window.
    /// </remarks>
    public const string CompositorAddress = "Assets/Frame.vxcompositor";

    /// <summary>Where the level lives, by address.</summary>
    /// <remarks>
    ///     ⚠ <b>An address is the project-relative path, folder and extension and all.</b> A project
    ///     that has not written one into an asset's <c>.meta</c> gets its path —
    ///     <c>BuildPlanner.AddressOf</c> says why — so this is <c>Assets/Scenes/Arena.vxscene</c> and
    ///     not <c>Scenes/Arena</c>. Getting it wrong is a warning and an empty level rather than a
    ///     crash, which is exactly the kind of mistake that survives a code review.
    /// </remarks>
    public const string SceneAddress = "Assets/Scenes/Arena.vxscene";

    /// <summary>Where the bindings live, by address.</summary>
    public const string InputAddress = "Assets/Input/GameInput.vxinput";

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        config.Name = "ThirdPersonShooter";
        config.Organisation = "Vixen";
        config.Window = new() { Title = "Vixen — Third-Person Shooter", Size = new(1600, 900), IsVisible = true };

        // The project's own frame: doc 22's virtualized path and doc 19's global illumination, both
        // named in a file rather than assembled here. That is the whole point of the compositor being
        // an asset, and until the lit path had nodes it was not a point this sample could make.
        config.Graphics.Compositor = CompositorAddress;

        // ⚠ Everything casts, including the player, and this line is what makes that true. A frame
        // document decides where a stage is drawn; it cannot decide what an object is extracted as,
        // so the level and the character would be invisible to the !ShadowMap node however carefully
        // the document names it. The camera's own view keeps the Opaque mask alone — the shadow node
        // makes its own views, one per cascade.
        config.Graphics.CasterStages.Add("Shadow");

        // ⚠ The same mechanism, and the name is a lie only in the sense that "caster" now means "a
        // stage this object is also extracted into". The velocity pass draws the level and the
        // character with MotionVectors instead of their materials, and without this line it draws
        // nothing — which is a motion-vector target of zeroes, a motion blur that is a copy, and a
        // frame where every counter says the pass ran.
        config.Graphics.CasterStages.Add("Motion");

        // ⚠ Not a caster stage, and that is the whole distinction. The two above are stages every
        // *mesh* is extracted into as well as Opaque; this one is where the scene's `!VfxEmitter`s are
        // drawn and no mesh ever is. It is also why it must not be in that list: a billboard is
        // expanded once for the whole frame against the camera, so a cascade drawing the same quads
        // would draw them edge-on to the sun.
        config.Graphics.ParticleStage = "Embers";

        // ⚠ And this, which has to be here rather than in OnInitialise. The compositor is built
        // inside AppGraphics' own constructor, before OnInitialise runs, and a document naming
        // !DistanceFieldAo or !Bloom against a builder that has never heard of them throws from
        // inside that build. CompositorBuilder cannot know them itself: Vixen.Rendering.PostFx is
        // downstream of it and a case there would be a reference cycle.
        //
        // Constructing the factory is also what first touches that assembly, which runs the
        // [ModuleInitializer] registering its aliases with the type registry — so without this the
        // document does not bind either. One line, two failures.
        config.Graphics.Factories.Add(new Rendering.PostFx.PostEffectFactory());

        // The `!Terrain` node's host half, and it is exactly one line on the same terms as the one
        // above: constructing the factory registers the node alias with the type registry, and the
        // rest — the world renderer's terrain list, the extraction bridge that walks
        // TerrainComponent entities into it, the asset source turning the component's reference into
        // a heightfield and its grass rule — comes with the registration. Without this the document
        // names a type nothing in the build claims, which is a refusal from inside the compositor's
        // construction rather than a frame that quietly has no ground.
        //
        // ⚠ Deliberately not in CasterStages. The terrain does not cast into the cascades yet, and
        // adding the stage would extract nothing for it while every counter said the pass ran.
        config.Graphics.Factories.Add(new Rendering.Terrain.TerrainFactory());

        // And the lake's, on exactly the terrain's terms: the document names !WaterSurface, !Water
        // and !Underwater, and constructing the factory is what claims those three names and runs the
        // [ModuleInitializer] that registers !WaterZoneComponent and !WaterBodyComponent — the two
        // names Arena.vxscene carries. Without this the *scene* fails to load, before the frame does.
        //
        // ⚠ The factory's Zones property is deliberately left alone. AppGraphics recognises this type
        // and hands it the WaterZoneSystem it built for the world, which is an object that does not
        // exist yet at this point — the same arrangement TerrainFactory.Scene gets and for the same
        // reason. A !WaterSurface node whose Zones nobody set draws nothing at all and says nothing.
        config.Graphics.Factories.Add(new Rendering.Water.WaterRendererFactory());
    }

    /// <inheritdoc />
    protected override void OnInitialise() {
        var services = Services;

        if (services.Engine is not { } loop || services.Scenes is null) {
            // Headless with no engine is a legitimate way to run this — `--vixen-frames 1` on a
            // machine with no GPU — and it is not a failure to report.
            return;
        }

        arena = Arena.Load(services);
        player = PlayerRig.Spawn(services, arena);

        arena.Register(loop);
        player.Register(loop);
    }

    /// <inheritdoc />
    protected override void OnUpdate(GameTime time) {
        // The per-frame half of the GI wiring. The compute fills read their composed sources out of
        // their own parameter collections, and the textures behind those names are made by
        // compositor nodes on their first record — objects a one-time wire-up cannot reach and a
        // document reload replaces. Re-asserting them every frame costs nothing when nothing
        // changed, and is what survives the reload. See ArenaIllumination.Feed.
        arena?.FeedIllumination();

        // And the lake's, which is the same shape of wiring one subsystem over: !Water's sun and sky
        // are radiances in the frame's units, a document can only write a tint, and the difference
        // between the two is a lake that tonemaps to black. See Arena.FeedWater.
        arena?.FeedWater();
    }

    /// <inheritdoc />
    protected override void OnShutdown() {
        // Before anything is disposed, because the numbers are read out of the world.
        player?.Report(Services.Graphics?.FrameCount ?? 0);
        arena?.ReportFrame();

        player?.Dispose();
        arena?.Dispose();
    }

    /// <summary>The spawn point with a given index, or the origin if the level has none.</summary>
    /// <remarks>
    ///     ⚠ <b>Found by component rather than by name, and it has to be.</b> Entity names are a table
    ///     on the <c>SceneAsset</c> and never a component — see <c>Vixen.Engine</c>'s README for the
    ///     thirty-bytes-a-chunk argument — so a running game cannot look an entity up by what the
    ///     editor calls it. <see cref="SpawnPoint" /> is the game's own component saying so, which is
    ///     the ordinary way a level tells a game something.
    /// </remarks>
    internal static LocalTransform SpawnPointAt(World world, int index) {
        var query = new QueryDescription().WithAll<SpawnPoint, LocalTransform>();

        foreach (var chunk in world.Chunks(query)) {
            var points = chunk.ReadValues<SpawnPoint>();
            var transforms = chunk.ReadValues<LocalTransform>();

            for (var entry = 0; entry < chunk.Count; entry++) {
                if (points[entry].Index == index) {
                    return transforms[entry];
                }
            }
        }

        // A level being edited may have no spawn points yet. The origin is visible and recoverable,
        // which a throw here would not be.
        return LocalTransform.At(new(0f, 1f, 0f));
    }
}
