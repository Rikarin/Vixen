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
