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
    static readonly (int First, int Last, int Stride)? Strip = ParseStrip();

    Arena? arena;
    PlayerRig? player;
    StreamWriter? shadowTrace;
    StreamWriter? lampTrace;
    StreamWriter? sunTrace;

    /// <summary>Reads <c>VIXEN_STRIP=first-last[/stride]</c>, or <see langword="null" /> if unset.</summary>
    /// <remarks>
    ///     ⚠ <b>The stride is what reaches the timescale a person complains about.</b> A blink
    ///     reported as "ten to twenty seconds" is not a frame-to-frame event, and a strip of thirty
    ///     consecutive frames covers half a second — it would answer a question nobody asked.
    ///     <c>0-1200/30</c> is forty-one pictures spread over twenty seconds of walking, which is the
    ///     shape of the thing being looked for.
    /// </remarks>
    static (int First, int Last, int Stride)? ParseStrip() {
        if (Environment.GetEnvironmentVariable("VIXEN_STRIP") is not { Length: > 0 } spec) {
            return null;
        }

        var parts = spec.Split('/');
        var stride = parts.Length > 1 && int.TryParse(parts[1], out var every) && every > 0 ? every : 1;

        return parts[0].Split('-') is { Length: 2 } range
            && int.TryParse(range[0], out var first)
            && int.TryParse(range[1], out var last)
            && last >= first
                ? (first, last, stride)
                : null;
    }

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

        // ⚠ `IsVisible` follows `Headless`. `AppConfig.Apply` reads the command line *before* this
        // hook, deliberately, so that a game can override an operator — which makes an unconditional
        // `true` here exactly that override, made without meaning to. It is not what would open a
        // window on a `--vixen-headless` run: `PlatformHost` has already chosen the headless platform
        // from the same flag, and a `HeadlessWindow` has no picture whatever this says. What it costs
        // is truthfulness — the sample this repository photographs most would be recorded as showing
        // a window on every capture run.
        config.Window = new() {
            Title = "Vixen — Third-Person Shooter",
            Size = new(1600, 900),
            IsVisible = !config.Headless
        };

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

        // Every caster, for the two shadow consumers that are not split in two: the lamps' atlas and
        // the virtual shadow map. A lamp's tile is cached whole and a page is a page of the world, so
        // neither has a static and a dynamic half — and the virtual map outranks the cascades wherever
        // it has a drawn page, so a level missing from it stops casting a sun shadow over most of the
        // screen with the cascades behind it perfectly correct. See the stage's note in the document.
        config.Graphics.CasterStages.Add("ShadowAll");

        // ── the sun's cache ────────────────────────────────────────────────────────────────────
        // ⚠ **These three lines replace the three above for an entity carrying `!StaticShadowCaster`,
        // and "replace" is the load-bearing word.** The !ShadowMap node draws `ShadowStatic` into a
        // cache of its own and copies it into the working atlas every frame, then draws `Shadow` on
        // top; an object in both is rasterised into the cache *and* over it, which is the uncached
        // frame plus a 64 MB copy — the cache costing rather than paying, with every counter reading
        // healthy. `Motion` and `ShadowAll` are named again because they are wanted either way: the
        // velocity pass draws the level whether or not it moves, and so does every lamp.
        //
        // Nothing here decides *which* entities are static. That is the scene's claim, and
        // Arena.MarkTheLevel is where this sample makes it — see `MeshExtractionSystem.StaticStages`
        // for why an empty list ignores the claim rather than obeying it with a mask of none.
        config.Graphics.StaticCasterStages.Add("ShadowStatic");
        config.Graphics.StaticCasterStages.Add("Motion");
        config.Graphics.StaticCasterStages.Add("ShadowAll");

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

        // The overlays, because the audio panel belongs to whoever holds the AudioEngine and that is
        // this project rather than the host — `IDiagnosticOverlay`'s whole seam. Null when the run
        // did not ask for panels, which the rig treats as "do not register one".
        player.Register(loop, services.Graphics?.Overlays);

        if (Environment.GetEnvironmentVariable("VIXEN_VSMTRACE") is { Length: > 0 } path) {
            shadowTrace = new(path, append: false);

            shadowTrace.WriteLine(
                "frame,marked,answered,absent,drawn,invalidated,refit,pending,resident,allocations,evictions"
            );
        }

        if (Environment.GetEnvironmentVariable("VIXEN_LAMPTRACE") is { Length: > 0 } lamps) {
            lampTrace = new(lamps, append: false);
            lampTrace.WriteLine("frame,tiles,drawn,kept,moved,disturbed,dropped");
        }

        if (Environment.GetEnvironmentVariable("VIXEN_SUNTRACE") is { Length: > 0 } sun) {
            sunTrace = new(sun, append: false);
            sunTrace.WriteLine("frame,tiles,rebuilds,refits,invalidations");
        }
    }

    /// <inheritdoc />
    protected override void OnUpdate(GameTime time) {
        // The per-frame half of the GI wiring. The compute fills read their composed sources out of
        // their own parameter collections, and the textures behind those names are made by
        // compositor nodes on their first record — objects a one-time wire-up cannot reach and a
        // document reload replaces. Re-asserting them every frame costs nothing when nothing
        // changed, and is what survives the reload. See ArenaIllumination.Feed.
        arena?.FeedIllumination();

        // ⚠ The lake used to need the same treatment here — Arena.FeedWater, calling
        // WaterRenderer.LightFrom once a frame — and it does not any more. !Water's sun and sky are
        // photometric quantities of the scene, so WaterRendererFactory now takes them from
        // CompositorBuilder.Sun and .SceneConstants the way !Fog and !VolumetricFog do, from the same
        // two objects this method was reaching for. A host that has to remember is a host that works
        // until somebody writes a second one: this sample was the only one that remembered, and the
        // fix for task #119 was in the tree while every other host's lake stayed black.

        CaptureStrip();
        TraceShadows();
        TraceLamps();
        TraceSun();
    }

    /// <summary>Writes one row per frame of what the sun's cascades had to rasterise.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ <c>TraceLamps</c>' argument, one atlas along: a correct cache renders identically
    ///         to no cache.</b> No picture this run can take says whether the level's geometry was
    ///         redrawn into the cascades or copied out of a cache, so the counters are the only
    ///         measurement there is.
    ///     </para>
    ///     <para>
    ///         <c>tiles</c> is what the node rasterised this frame — four on a kept frame, eight on a
    ///         rebuilt one, and four on every frame with the cache off, which is the honest way to
    ///         read the trade. <c>rebuilds</c> is cumulative and is the number the cache is judged by:
    ///         against the frame count it is the rate at which the whole level went back through the
    ///         caster pass. <c>refits</c> and <c>invalidations</c> say which of the two causes it was
    ///         — the cascade's own fit moving, or the host claiming the static content changed — and
    ///         they have opposite fixes, <c>slack:</c> for the first and the scene for the second.
    ///     </para>
    ///     <para>
    ///         ⚠ A run whose camera never moves settles at zero rebuilds and is measuring a frame no
    ///         game has: a cascade is fitted to the camera, so standing still is the one case where
    ///         its projection is trivially stable. Drive it with <c>VIXEN_WALK</c>.
    ///     </para>
    /// </remarks>
    void TraceSun() {
        if (sunTrace is not { } writer || Services.Graphics is not { } graphics) {
            return;
        }

        if (arena?.SunShadowNode is not { } node) {
            return;
        }

        writer.WriteLine(
            $"{graphics.FrameCount},{node.TilesDrawn},{node.StaticRebuilds},"
            + $"{node.StaticRefits},{node.StaticInvalidations}"
        );

        writer.Flush();
    }

    /// <summary>Writes one row per frame of how many lamp tiles the atlas had to redraw.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ The same argument <c>TraceShadows</c> makes, one atlas along: a correct cache
    ///         renders identically to no cache.</b> Every picture this run can take is the same
    ///         picture whether a tile was kept or redrawn, so a strip of frames cannot say whether
    ///         the cache did anything — only the counters can, and the number that matters is
    ///         <c>drawn</c> settling near zero while <c>kept</c> holds at the tile count.
    ///     </para>
    ///     <para>
    ///         This level is eighteen point lights, so 108 of the atlas's 121 tiles: <c>drawn</c> is
    ///         108 on the frame the lamps first pack, and thereafter is however many tiles the
    ///         player, the props and the streamer disturbed. A run whose camera never moves settles
    ///         at zero and is measuring a frame no game has; drive it with <c>VIXEN_WALK</c>.
    ///     </para>
    /// </remarks>
    void TraceLamps() {
        if (lampTrace is not { } writer || Services.Graphics is not { } graphics) {
            return;
        }

        if (arena?.PunctualShadowNode is not { } node) {
            return;
        }

        writer.WriteLine(
            $"{graphics.FrameCount},{node.Tiles.Count},{node.TilesDrawn},{node.TilesKept},"
            + $"{node.TilesMoved},{node.TilesDisturbed},{node.DroppedLights}"
        );

        writer.Flush();
    }

    /// <summary>Writes one row per frame of what the virtual shadow map answered.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ A picture cannot answer this question and no number of pictures can.</b> Every way
    ///         the virtual map fails is a pixel the cascades shade instead — <c>ClusteredShading</c>
    ///         falls through when the page table has nothing at the address — so a frame whose map
    ///         answered nothing at all renders, and renders plausibly. A strip of forty pictures over
    ///         twenty seconds therefore measures the camera, the exposure and the sun, and cannot
    ///         separate any of them from the shadow term. <c>VIXEN_VSMTRACE=&lt;file&gt;</c> writes the
    ///         counters instead, one row a frame, which is the measurement task #124 has wanted since
    ///         it was opened.
    ///     </para>
    ///     <para>
    ///         The column that matters is <c>absent</c> — marked pages the frame's own page table does
    ///         not answer, counted at the upload in <c>VirtualShadowAtlas.UploadTable</c>. The rest are
    ///         there to say <em>why</em>: <c>invalidated</c> and <c>refit</c> are the clipmap throwing
    ///         its own pages away, <c>pending</c> is the draw backlog the budget has not reached, and
    ///         <c>drawn</c> against <c>PagesPerFrame</c> says whether the budget is the binding
    ///         constraint.
    ///     </para>
    ///     <para>
    ///         Flushed every row rather than buffered: a run ended by <c>--vixen-frames</c> disposes
    ///         cleanly, but one killed mid-measurement should still leave the frames it reached.
    ///     </para>
    /// </remarks>
    void TraceShadows() {
        if (shadowTrace is not { } writer || Services.Graphics is not { } graphics) {
            return;
        }

        if (arena?.VirtualShadowNode is not { } node) {
            return;
        }

        var pages = arena.Illumination?.VirtualShadows;

        writer.WriteLine(
            $"{graphics.FrameCount},{node.MarkedPages},{node.AnsweredPages},{node.AbsentPages},"
            + $"{node.DrawnPages},{node.InvalidatedPages},{node.RefitLevels},"
            + $"{pages?.Pages.Pending.Count ?? 0},{pages?.Residency.ResidentPages ?? 0},"
            + $"{pages?.Pages.Allocations ?? 0},{pages?.Residency.Evictions ?? 0}"
        );

        writer.Flush();
    }

    /// <summary>Asks for a picture of every frame in <c>VIXEN_STRIP</c>'s range.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ A temporal artefact is a difference between two <em>consecutive</em> frames, and
    ///         until this there was no way to hold two.</b> <c>--vixen-capture</c> writes the last
    ///         frame only, so frame <i>N</i> and frame <i>N</i> + 1 were two whole runs — and two runs
    ///         differ by <em>when their textures finished loading</em> as well as by the thing being
    ///         looked for. Material loads and the streamer's mip swaps land on a wall-clock schedule,
    ///         so at a fixed frame index two runs of one binary hold different content: at frame 4
    ///         both runs have 15 of 17 textures and the pictures are byte-identical, at frame 5 one
    ///         has 16 and the other 17 and the foliage still waiting for its map draws as untextured
    ///         boxes. Streaming settles by about frame 64, but the difference it injected is carried
    ///         on by TAA, the probe history and the exposure meter — over grass a cross-run pair is
    ///         still a worst-case 0.071/255 mean absolute channel at 256 frames, which cannot answer
    ///         "did this pixel change between frames" at all. Two frames of <em>one</em> run share the
    ///         schedule, the streaming state and the probe history, and their difference is the frame
    ///         step and nothing else. See <c>docs/guide/rendering/capturing-a-frame.md</c> for the
    ///         measured floor and what it does and does not permit.
    ///     </para>
    ///     <para>
    ///         <c>VIXEN_STRIP=first-last</c>, inclusive, written as <c>frame-0512.png</c> beside the
    ///         ordinary <c>frame.png</c> in <c>--vixen-capture</c>'s directory. It is the sample's and
    ///         not the host's for <c>ScriptedWalk</c>'s reason: <c>AppGraphics.RequestCapture</c> is
    ///         already the public way to ask for one frame by hand, and a strip is a loop over it.
    ///     </para>
    ///     <para>
    ///         ⚠ Every frame in the range is a full readback and a PNG encode on the frame's own
    ///         thread, so a wide strip changes how long the run takes and nothing about what it
    ///         renders — the clock is fixed, which is exactly why that is safe to say.
    ///     </para>
    /// </remarks>
    void CaptureStrip() {
        if (Services.Graphics is not { } graphics || Strip is not var (first, last, stride)) {
            return;
        }

        var frame = graphics.FrameCount;

        if (frame >= first && frame <= last && (frame - first) % stride == 0) {
            graphics.RequestCapture($"frame-{frame:0000}");
        }
    }

    /// <inheritdoc />
    protected override void OnShutdown() {
        // Before anything is disposed, because the numbers are read out of the world.
        player?.Report(Services.Graphics?.FrameCount ?? 0);
        arena?.ReportFrame();

        player?.Dispose();
        arena?.Dispose();

        shadowTrace?.Dispose();
        shadowTrace = null;
        lampTrace?.Dispose();
        lampTrace = null;
        sunTrace?.Dispose();
        sunTrace = null;
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
