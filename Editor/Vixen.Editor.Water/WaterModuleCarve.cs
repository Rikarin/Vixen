// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Terrain;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Water;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Water;

/// <summary>The third verb: cutting the bodies into the ground, and looking at what they cut.</summary>
/// <remarks>
///     <para>
///         <b><c>WaterCarve</c> existed, <c>WaterCarveCommand</c> existed, and nothing constructed
///         either.</b> Doc 35 § D5's reserved layer was written, tested and never reached from a
///         running editor — so "Preview the carve" was a flag toggled by a command, ticked by a menu
///         and read by nobody, because there was never anything in the layer to hide.
///     </para>
///     <para>
///         ⚠ <b>The terrain comes through <c>ITerrainScene</c> and not through
///         <c>Vixen.Editor.Terrain</c>.</b> The two toolsets are independent plugins and either may be
///         absent — the module's README says so about <c>GroundAt</c> for the same reason — so what is
///         referenced here is the <em>contract</em> the terrain plugin contributes through, in
///         <c>Core</c>. A project with no terrain in it gets an empty list and a greyed-out verb,
///         which is what "either may be absent" has to mean in practice.
///     </para>
///     <para>
///         ⚠ <b>One entry for every terrain, because a carve is one thing an author did.</b> A scene
///         with two terrains would otherwise take two presses of Ctrl-Z to put back, and the second
///         one would look like it had undone something else.
///     </para>
/// </remarks>
public sealed partial class WaterModule {
    /// <summary>What the verb that cuts the bodies into the ground is called.</summary>
    public const string CarveTerrainCommand = "water.carve";

    /// <summary>The carve last issued per terrain, which is what tells the next one where to undo to.</summary>
    /// <remarks>
    ///     Keyed by the terrain object rather than by its name: <c>ITerrainScene</c> hands out the
    ///     very heightfields the viewport draws, and two references to one file are one object.
    /// </remarks>
    readonly Dictionary<TerrainMap, WaterCarveCommand> carves = [];

    /// <summary>How many terrains a carve would touch. What the panel says when it is none.</summary>
    public int CarvableTerrains => Terrains().Count;

    /// <summary>Registers the carve verb and joins the preview flag up to the layer it names.</summary>
    void WaterCarveCommands(PluginContext context) {
        Shell.Commands.Add(
            new EditorCommand(
                CarveTerrainCommand,
                new StringId("editor.command." + CarveTerrainCommand, "Carve Terrain From Water"),
                () => Carve()
            ) {
                Category = DebugCategory,

                // ⚠ Not gated on the mode being active, on `WaterMode.CreateZoneCommand`'s terms: "I
                // moved a river and the old bed is still there" is asked from whatever mode the
                // author happened to be in when they noticed.
                Enablement = () => Terrains().Count > 0
            }
        );

        context.OnUnload(() => Shell.Commands.Remove(CarveTerrainCommand));

        Mode.Editing.CarvePreviewChanged += ShowCarve;

        context.OnUnload(
            () => {
                Mode.Editing.CarvePreviewChanged -= ShowCarve;

                // ⚠ The layer goes back to visible when the toolset goes. `IsVisible` is saved with
                // the terrain, so a session that ended with the preview off would leave a project
                // whose riverbeds are on disk and invisible, with nothing anywhere saying why.
                ShowCarve(true);
            }
        );
    }

    /// <summary>Shows or hides the reserved water layer's contribution on every terrain.</summary>
    /// <param name="shown">Whether the carve contributes.</param>
    /// <remarks>
    ///     ⚠ <b>Invalidated and resolved, not just flagged.</b> The composite is cached per tile and
    ///     recomputed only when something says it is stale — see <c>Terrain.Resolve</c> — so a
    ///     visibility flag flipped on its own is a toggle that changes nothing on screen, which is
    ///     indistinguishable from the toggle not being wired at all.
    /// </remarks>
    void ShowCarve(bool shown) {
        foreach (var (terrain, _) in Terrains()) {
            var layer = WaterCarve.LayerOf(terrain);

            if (layer.IsVisible == shown) {
                continue;
            }

            layer.IsVisible = shown;
            terrain.InvalidateAll();
            terrain.Resolve();
        }
    }

    /// <summary>Cuts every body in the scene into every terrain in it, as one undo entry.</summary>
    /// <returns>How many terrains were carved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Wholesale rather than per body</b> —
    ///         [§ D5](../../docs/plan/35-water.md#d5-carving-is-a-reserved-edit-layer-and-the-machinery-exists).
    ///         The layer is emptied and every body laid down again, which is what makes moving a river
    ///         restore the old bank and cut the new one in one operation. Carving one body into a
    ///         layer that already holds the others is <c>WaterCarve.Carve</c>, and it is the wrong
    ///         operation here for exactly the case an author hits first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every body carries the same <c>WaterCarveProfile</c>, and that is a stated
    ///         limit.</b> <c>WaterBodyComponent</c> has no carve strength or bed layer on it — doc 35
    ///         § The body inspector asks for them and the component predates the ask — so what is used
    ///         is the draw settings' <c>Carve</c>, which is the strength the author set when they laid
    ///         the bodies. Putting the two fields on the component is a component <em>layout</em>
    ///         change and therefore a scene-compatibility decision, which is not this one.
    ///     </para>
    /// </remarks>
    public int Carve() {
        var terrains = Terrains();

        if (terrains.Count == 0) {
            Report("Nothing to carve into: this scene has no terrain in it.");

            return 0;
        }

        var profile = Mode.Body.Carve;
        var issued = new List<IEditorCommand>(terrains.Count);

        foreach (var (terrain, origin) in terrains) {
            var bodies = BodiesIn(origin, profile);
            var before = carves.TryGetValue(terrain, out var previous) ? previous.Current : [];
            var command = new WaterCarveCommand(terrain, before, bodies);

            carves[terrain] = command;
            issued.Add(command);
        }

        Scene.Stack.Execute(
            issued.Count == 1 ? issued[0] : new CompositeCommand("Carve Water", [.. issued])
        );

        // The carve has just changed the ground, so whatever the preview says has to be true of it
        // again — a hidden layer that was regenerated is a layer that came back visible otherwise.
        ShowCarve(Mode.Editing.CarvePreview);
        RefreshWaterFacts();

        return issued.Count;
    }

    /// <summary>Every body in the scene, built in one terrain's own space.</summary>
    /// <remarks>
    ///     ⚠ <b>In the terrain's space, which is world space less its origin.</b>
    ///     <c>WaterCarve.Carve</c> samples the body at <c>x * metresPerQuad</c> — the heightfield's
    ///     own grid — so a body handed over in world space carves a lake at the terrain's corner
    ///     wherever the author actually drew it.
    /// </remarks>
    IReadOnlyList<(WaterBody Body, WaterCarveProfile Profile)> BodiesIn(
        Vector3 origin,
        in WaterCarveProfile profile
    ) {
        var built = new List<(WaterBody, WaterCarveProfile)>();

        // ⚠ `WorldTransform` as well as the component, which is `WaterZoneSystem`'s own body query.
        // A body without one is a body the fold does not draw either, so carving it would cut ground
        // under water nobody can see.
        var query = new QueryDescription().WithAll<WaterBodyComponent, WorldTransform>();
        var local = Matrix4x4.FromTranslation(-origin);

        foreach (var chunk in Scene.World.Chunks(query)) {
            var components = chunk.ReadValues<WaterBodyComponent>();
            var placements = chunk.ReadValues<WorldTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                var component = components[index];

                if (component.Spline is not { Length: > 0 } name) {
                    continue;
                }

                if (WaterScene.SplineFor(name, placements[index].Value * local) is not { } curve) {
                    continue;
                }

                if (WaterZoneSystem.BodyOf(component, curve) is { } body) {
                    built.Add((body, profile));
                }
            }
        }

        return built;
    }

    /// <summary>Every terrain the scene draws, and where each one's low corner is.</summary>
    /// <remarks>
    ///     Asked each time rather than cached: a plugin activating three seconds after start-up is
    ///     always after anything that read the registry once — <c>IEditorRegistry.Changed</c>'s note —
    ///     and a terrain plugin loaded after this one is the ordinary case.
    /// </remarks>
    IReadOnlyList<(TerrainMap Terrain, Vector3 Origin)> Terrains() {
        if (registry is not { } contributions) {
            return [];
        }

        var sources = contributions.All<ITerrainScene>();

        if (sources.Count == 0) {
            return [];
        }

        var found = new List<(TerrainMap, Vector3)>();

        foreach (var source in sources) {
            found.AddRange(source.Terrains());
        }

        return found;
    }
}
