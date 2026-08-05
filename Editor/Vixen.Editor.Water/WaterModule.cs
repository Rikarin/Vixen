// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Water;

namespace Vixen.Editor.Water;

/// <summary>Doc 35's toolset, registering itself through the door a third party comes through.</summary>
/// <remarks>
///     <para>
///         <b>One mode, two panels, and the thing that turns a drawn curve into a scene entity.</b>
///         <c>TerrainModule</c>'s shape, one document on — see doc 36 § F2 for why a toolset that was
///         a partial of the editor's application could only exist in an editor compiled with it.
///     </para>
///     <para>
///         ⚠ <b>The gesture ends at a curve and the module is what makes it a body.</b>
///         <see cref="WaterMode.Drawn" /> hands over a <see cref="Spline" />; writing it beside the
///         scene as a <c>.vxspline</c> and creating the entity that names it needs a project and a
///         world, which is exactly what a mode must not need to be testable.
///     </para>
///     <para>
///         ⚠ <b>What it takes from the host, it asks for.</b> The project and the scene, through
///         <c>PluginServices.Require</c>. A host without one refuses the module by name rather than
///         throwing a null reference from inside <see cref="Activate" />.
///     </para>
/// </remarks>
public sealed partial class WaterModule : IEditorPlugin {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.water";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Water";

    /// <summary>What the Create ▸ entry for a sea state is registered as.</summary>
    public const string CreateWavesCommand = "water.create-waves";

    readonly WaterMode water = new();

    EditorProject project = null!;
    SceneDocument document = null!;
    EditorShell shell = null!;

    /// <summary>The project the curves are written into.</summary>
    EditorProject Project => project;

    /// <summary>The scene the bodies are placed in.</summary>
    SceneDocument Scene => document;

    /// <summary>The chrome the panel and the mode are registered into.</summary>
    EditorShell Shell => shell;

    /// <summary>The mode, so a host or a test can drive it.</summary>
    public WaterMode Mode => water;

    /// <summary>How many bodies the module has created this session.</summary>
    /// <remarks>
    ///     The reading behind the panel's "N bodies" line and the thing a session test asserts: a
    ///     gesture that laid a curve and created nothing is doc 31's "built and not yet reachable"
    ///     failure, and doc 35's W9 asks for it to be tested for explicitly.
    /// </remarks>
    public int BodiesCreated { get; private set; }

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        project = context.Services.Require<EditorProject>();
        document = context.Services.Require<SceneDocument>();
        shell = context.Shell;

        water.Document = Scene;
        water.Drawn += Placed;

        context.OnUnload(() => water.Drawn -= Placed);

        // ⚠ Doc 35 § D6's one new asset kind, and the Create ▸ entry is what makes it reachable at
        // all. A `.vxwaves` has an importer and a runtime source; without a way to make one, the only
        // route to a shared sea state is typing YAML by hand, which is the shape of a feature that is
        // built and not yet reachable.
        //
        // ⚠ `Build` rather than `Contents`, so the template is the panel's *current* sea state rather
        // than the one it held when the module loaded — see `NewAssetKind.Build`. An author who has
        // spent a minute finding a sea they like and then presses this means that sea.
        context.Owns(
            context.Services.Require<IEditorRegistry>().Add(
                new NewAssetKind(
                    CreateWavesCommand,
                    "Sea state",
                    WaterWavesAsset.Extension,
                    "New Sea State",
                    Opens: false
                ) {
                    Build = () => YamlWriter.Write(YamlSerializer.Serialize(Mode.Zone.Asset))
                }
            )
        );

        // ⚠ The half of the toolset that makes the other half visible, and it was missing entirely:
        // the gesture wrote a curve, the body named it, and nothing turned that name back into
        // geometry for a pane to draw. `TerrainModule` contributes an `ITerrainScene` through this
        // same door for the same reason — see `WaterModuleScene`.
        context.Owns(context.Services.Require<IEditorRegistry>().Add(WaterScene));

        WaterPanels();
        WaterDebugCommands(context);

        Shell.Modes.Add(water);
    }

    /// <summary>Turns a drawn curve into a spline asset and an entity that names it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The spline is written beside the scene rather than held in memory.</b> A body
    ///         names its curve by name — [§ D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline)
    ///         — because a handle names a slot in a world that issued it and a scene file is read by a
    ///         world that has not run yet. A curve that existed only in the editor's memory would be a
    ///         lake that disappears when the project is reopened, which is the worst kind of authoring
    ///         bug: it looks like it worked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is refused rather than half-done when there is nowhere to write it.</b> A
    ///         scene that has never been saved has no directory for a sidecar, so the body is not
    ///         created at all and the panel says why — an entity naming a spline nothing can supply is
    ///         counted into <c>WaterZoneSystem.UnresolvedBodies</c> and draws nothing, which is a
    ///         diagnostic an author would have to go looking for.
    ///     </para>
    /// </remarks>
    void Placed(Spline spline, WaterBodyKind kind) {
        if (SplinePathFor(kind) is not { } path) {
            Report($"Save the scene before drawing water: a body names its curve by name, and there is nowhere to write a {kind}'s yet.");

            return;
        }

        if (!Write(spline, path)) {
            return;
        }

        var settings = water.Body;
        var component = settings.ComponentFor(Path.GetFileNameWithoutExtension(path));

        // The surface height a closed body sits at is the height it was drawn at, which is the ground
        // the author was clicking on. A river ignores it and follows its own curve.
        var component2 = kind == WaterBodyKind.River
            ? component with { Kind = kind }
            : component with { Kind = kind, SurfaceHeight = spline.Evaluate(0f).Y };

        Scene.Create($"{kind} {BodiesCreated + 1}", default, default, entity => Scene.World.Add(entity, component2));

        BodiesCreated++;
        RefreshWaterFacts();
    }

    /// <summary>Where a new curve is written, or null when the scene has never been saved.</summary>
    string? SplinePathFor(WaterBodyKind kind) {
        if (Scene.Writer is not SceneFileWriter file || string.IsNullOrEmpty(file.Path)) {
            return null;
        }

        var directory = Path.GetDirectoryName(file.Path);

        if (string.IsNullOrEmpty(directory)) {
            return null;
        }

        var scene = Path.GetFileNameWithoutExtension(file.Path);

        for (var index = BodiesCreated + 1; ; index++) {
            var candidate = Path.Combine(directory, $"{scene}.{kind}{index}.vxspline");

            if (!File.Exists(candidate)) {
                return candidate;
            }
        }
    }

    /// <summary>Writes the curve, reporting rather than throwing when it cannot.</summary>
    /// <remarks>
    ///     ⚠ <b>A failed write must not leave an entity naming a file that is not there.</b> The body
    ///     would load, resolve to nothing, count into <c>UnresolvedBodies</c> and draw no water — and
    ///     the author would be looking at a lake in the outliner and dry ground in the viewport.
    /// </remarks>
    bool Write(Spline spline, string path) {
        try {
            var asset = new SplineAsset(Path.GetFileNameWithoutExtension(path), spline.Points, spline.IsClosed);

            File.WriteAllText(path, YamlWriter.Write(YamlSerializer.Serialize(asset)));

            return true;
        } catch (IOException error) {
            Report($"Could not write {Path.GetFileName(path)}: {error.Message}");

            return false;
        } catch (UnauthorizedAccessException error) {
            Report($"Could not write {Path.GetFileName(path)}: {error.Message}");

            return false;
        }
    }

    /// <summary>Makes a panel claim a command context when it is pressed in.</summary>
    /// <remarks>
    ///     ⚠ <b>On the capture leg, so it lands before anything inside the panel handles the
    ///     press.</b> Leaving a context matters as much as entering one: clicking a water slider has
    ///     to stop Delete meaning "delete the selected entity". <c>TerrainModule</c> has the same
    ///     eight lines for the same reason.
    /// </remarks>
    void Contextual(DockPanel panel, string context) =>
        panel.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    Shell.Context = context;
                }
            },
            RoutingStrategy.Capture,
            handledEventsToo: true
        );

    /// <summary>One "label: value" row in a derived-facts readout.</summary>
    /// <remarks>
    ///     <c>TerrainModule.Fact</c>'s four lines, copied for its stated reason: doc 20's
    ///     <c>(derived)</c> convention is a shape — a row, a name, a value — and a shared helper would
    ///     be an assembly reference for four lines of element building.
    /// </remarks>
    static void Fact(UiElement into, string label, string value) {
        var row = into.Add("fact-row");

        row.Add("fact-name").Text = label;
        row.Add("fact-value").Add("text").Text = value;
    }
}
