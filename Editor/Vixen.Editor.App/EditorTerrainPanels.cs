// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector;
using Vixen.Editor.Terrain;
using Vixen.Editor.Ui;
using Vixen.Foliage;
using Vixen.Terrain;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.App;

/// <summary>Doc 31's Part 2: the chrome the terrain, foliage and spline tools are driven from.</summary>
/// <remarks>
///     <para>
///         <b>Four panels over settings objects, and no dialog code.</b> Every number here is an
///         <c>[Inspector]</c> member of a <c>[DataContract]</c> type that
///         <see cref="Vixen.Editor.Terrain" /> already owns — which is doc 20's B6 bargain for world
///         settings applied to a toolset. What a panel adds beside the rows is the verb that makes
///         the numbers mean something and the readout that says what they cost.
///     </para>
///     <para>
///         ⚠ <b>Two of them are <em>mode</em> panels, which is why they are not on a menu by
///         habit.</b> <see cref="TerrainMode.Panel" /> and <see cref="FoliageMode.Panel" /> name
///         them, so entering the mode opens the panel and leaving it closes it — a settings panel
///         left behind for a tool nobody is holding is the thing <c>IEditorMode.Panel</c> exists to
///         prevent. The growth and spline panels are ordinary panels, because they are batch verbs
///         rather than brushes.
///     </para>
///     <para>
///         ⚠ <b>The create form shows its derived numbers as it is filled in.</b>
///         <see cref="TerrainFacts" /> is world extent, vertex count, height storage, weightmap
///         storage per layer and Jolt shape count — and this is the dialog where a person
///         accidentally asks for eight gigabytes. Doc 20's <c>(derived)</c> convention belongs here
///         more than where it came from.
///     </para>
///     <para>
///         ⚠ <b>A verb with nothing to act on is visibly disabled rather than absent.</b> Doc 20's
///         first bar. Growing a forest with no volume, or laying a road with no terrain, says so
///         where the button is instead of leaving a panel that does nothing when pressed.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>What the terrain panel is called in an arrangement.</summary>
    internal const string TerrainPanel = TerrainMode.PanelId;

    /// <summary>And the foliage palette.</summary>
    internal const string FoliagePanel = FoliageMode.PanelId;

    /// <summary>And the growth simulation.</summary>
    internal const string GrowthPanel = "terrain.growth";

    /// <summary>And the spline profile.</summary>
    internal const string SplinePanel = "terrain.splines";

    /// <summary>And the grass rule, which is deliberately not a mode.</summary>
    internal const string GrassPanel = "terrain.grass";

    /// <summary>The sculpt and paint mode, which owns the terrain being edited.</summary>
    readonly TerrainMode terrain = new();

    /// <summary>And the foliage one, which owns the volume.</summary>
    readonly FoliageMode foliage = new();

    /// <summary>What the growth panel edits.</summary>
    readonly TerrainGrowthSettings growth = new();

    /// <summary>And the spline panel.</summary>
    readonly TerrainSplineSettings splines = new();

    /// <summary>And the grass panel.</summary>
    readonly TerrainGrassSettings grass = new();

    /// <summary>What the last growth run produced, so the panel can say rather than imply.</summary>
    FoliageGrowthResult grown;

    /// <summary>Whether a growth run has happened at all this session.</summary>
    bool hasGrown;

    /// <summary>The rows the create section's derived numbers are drawn into.</summary>
    UiElement? terrainFacts;

    /// <summary>And the layer stack's.</summary>
    UiElement? terrainLayers;

    /// <summary>And the palette's.</summary>
    UiElement? foliagePalette;

    /// <summary>And the growth report's.</summary>
    UiElement? growthReport;

    /// <summary>The four panels doc 31's Part 2 describes.</summary>
    void TerrainPanels() {
        Shell.RegisterPanel(
            TerrainPanel,
            new StringId("editor.panel.terrain", "Terrain"),
            panel => {
                Contextual(panel, TerrainMode.TerrainContext);

                Section(panel, "Create");

                var create = panel.Add<InspectorView>();

                create.EditedDocument = null;
                create.Inspect(terrain.Create);

                terrainFacts = panel.Add("terrain-facts");

                // ⚠ Redrawn on every change rather than on a button, because the whole point of the
                // readout is that it is there *while* the numbers are being typed. A "recompute"
                // button would put the eight-gigabyte answer one press away from the person who was
                // about to press Create.
                create.ValueChanged += (_, _) => RefreshTerrainFacts();

                var made = panel.Add<Button>();

                made.Label = "Create terrain";
                made.Clicked += _ => Shell.Commands.Execute(TerrainMode.CreateCommand);

                RefreshTerrainFacts();

                Section(panel, "Edit layers");

                terrainLayers = panel.Add("terrain-layers");

                RefreshTerrainLayers();

                Verbs(
                    panel,
                    ("Add", TerrainMode.AddLayerCommand),
                    ("Duplicate", TerrainMode.DuplicateLayerCommand),
                    ("Clear", TerrainMode.ClearLayerCommand),
                    ("Collapse down", TerrainMode.CollapseLayerCommand),
                    ("Remove", TerrainMode.RemoveLayerCommand)
                );

                Section(panel, "Target layers");

                Verbs(panel, ("Add target", TerrainMode.AddTargetCommand), ("Remove target", TerrainMode.RemoveTargetCommand));

                Section(panel, "Brush");

                var brush = panel.Add<InspectorView>();

                brush.EditedDocument = null;
                brush.Inspect(terrain.Editing.Brush);

                Section(panel, "Tool");

                var tools = panel.Add<InspectorView>();

                tools.EditedDocument = null;
                tools.Inspect(terrain.Editing.Tools);
            }
        );

        Shell.RegisterPanel(
            FoliagePanel,
            new StringId("editor.panel.foliage", "Foliage"),
            panel => {
                Contextual(panel, FoliageMode.FoliageContext);

                Section(panel, "Palette");

                foliagePalette = panel.Add("foliage-palette");

                RefreshPalette();

                Verbs(panel, ("Add type", FoliageMode.AddTypeCommand), ("Remove type", FoliageMode.RemoveTypeCommand));

                Section(panel, "Tool");

                var settings = panel.Add<InspectorView>();

                settings.EditedDocument = null;
                settings.Inspect(foliage.Editing.Settings);

                Section(panel, "Brush");

                var brush = panel.Add<InspectorView>();

                brush.EditedDocument = null;
                brush.Inspect(foliage.Editing.Brush);
            }
        );

        Shell.RegisterPanel(
            GrassPanel,
            new StringId("editor.panel.grass", "Grass"),
            panel => {
                Contextual(panel, TerrainMode.TerrainContext);

                Section(panel, "The rule");

                var settings = panel.Add<InspectorView>();

                settings.EditedDocument = null;
                settings.Inspect(grass);

                // ⚠ The ring's size is the number this panel exists to put in front of somebody. A
                // range doubled is four times the cells, and this is where that becomes a gigabyte —
                // the same argument the terrain create form makes about eight.
                var facts = panel.Add("terrain-facts");

                settings.ValueChanged += (_, _) => RefreshGrass(facts);

                RefreshGrass(facts);
            }
        );

        Shell.RegisterPanel(
            GrowthPanel,
            new StringId("editor.panel.growth", "Growth"),
            panel => {
                Contextual(panel, FoliageMode.FoliageContext);

                Section(panel, "Simulation");

                var settings = panel.Add<InspectorView>();

                settings.EditedDocument = null;
                settings.Inspect(growth);

                growthReport = panel.Add("terrain-facts");

                var run = panel.Add<Button>();

                run.Label = "Grow";
                run.Clicked += _ => Grow();

                RefreshGrowth();
            }
        );

        Shell.RegisterPanel(
            SplinePanel,
            new StringId("editor.panel.splines", "Splines"),
            panel => {
                Contextual(panel, TerrainMode.TerrainContext);

                Section(panel, "Profile");

                var profile = panel.Add<InspectorView>();

                profile.EditedDocument = null;
                profile.Inspect(splines);

                var facts = panel.Add("terrain-facts");

                Fact(facts, "Reach from centre line", $"{splines.Reach:0.##} m (derived)");
                Fact(facts, "Paints the ground", splines.Paints ? $"target {splines.PaintTarget}" : "no");
                Fact(facts, "Places meshes", splines.Places ? $"every {splines.MeshSpacing:0.##} m" : "no");

                profile.ValueChanged += (_, _) => {
                    Clear(facts);

                    Fact(facts, "Reach from centre line", $"{splines.Reach:0.##} m (derived)");
                    Fact(facts, "Paints the ground", splines.Paints ? $"target {splines.PaintTarget}" : "no");
                    Fact(facts, "Places meshes", splines.Places ? $"every {splines.MeshSpacing:0.##} m" : "no");
                };

                var regenerate = panel.Add<Button>();

                regenerate.Label = "Regenerate roads";
                regenerate.Clicked += _ => RegenerateSplines();

                // ⚠ Named as owed rather than left out, which is doc 20's first bar. The overlay that
                // edits a spline in the viewport is `SplineEdit`'s and it is not on the gizmo yet;
                // a panel that silently had no way to author a curve would read as a feature that
                // does not work rather than as one that is not finished.
                var owed = panel.Add("terrain-facts");

                Fact(owed, "Curve editing", "in the viewport — SplineEdit, not yet on the gizmo");
            }
        );
    }

    /// <summary>What the grass rule costs, derived and labelled as derived.</summary>
    /// <remarks>
    ///     ⚠ <b>Grass has no mode and no brush, so this is the only place the cost is visible.</b>
    ///     [§ D8]: a person does not paint grass, they change the rule that produces it — and a rule
    ///     whose memory is invisible is one somebody turns up until the editor stops.
    /// </remarks>
    void RefreshGrass(UiElement facts) {
        Clear(facts);

        var bytes = grass.RingBytes();

        Fact(facts, "Ring size", $"{bytes / (1024.0 * 1024.0):N1} MB (derived)");
        Fact(facts, "Cells resident", $"{grass.RingBytes(instanceBytes: 1) / grass.BladesPerCell:N0} (derived)");
        Fact(facts, "Effective density", $"{grass.DensityScale:P0} (derived)");

        if (!grass.IsEnabled) {
            // ⚠ Said rather than implied. A switch off and a density of zero produce the same field,
            // and somebody looking at an empty hillside needs to know which one they are in.
            Fact(facts, "Disabled", "no cell is scattered — this is the switch, not the density");
        }

        if (grass.Validate() is { } refusal) {
            Fact(facts, "Refused", refusal);
        }
    }

    /// <summary>Registers the two modes the panels belong to.</summary>
    /// <remarks>
    ///     ⚠ <b>The document is handed over rather than made, because a mode outlives every scene the
    ///     editor opens</b> — the same reason <c>BlockoutMode.Editing</c> is passed in. What the mode
    ///     needs from the application is where to push an undo record, and that is a property of the
    ///     scene rather than of the tool.
    /// </remarks>
    void RegisterTerrainModes() {
        terrain.Document = scene;
        foliage.Document = scene;

        // The layer stack is what the panel draws, so a verb that changed it has to redraw it.
        // Polling would be a rebuild of the list every frame for a change that happens twice a day.
        terrain.Committed += _ => RefreshTerrainLayers();
        terrain.Created += _ => {
            RefreshTerrainFacts();
            RefreshTerrainLayers();
        };

        foliage.Committed += _ => RefreshPalette();

        Shell.Modes.Add(terrain);
        Shell.Modes.Add(foliage);
    }

    /// <summary>What the create form's numbers cost, derived and labelled as derived.</summary>
    void RefreshTerrainFacts() {
        if (terrainFacts is not { } facts) {
            return;
        }

        Clear(facts);

        foreach (var (label, value) in terrain.Create.Facts.Rows()) {
            Fact(facts, label, value);
        }

        // ⚠ The refusal is shown beside the numbers rather than only when Create is pressed. A form
        // whose only feedback is a button that does nothing is the shape of dialog people describe
        // as "the editor is broken".
        if (terrain.Create.Validate() is { } refusal) {
            Fact(facts, "Refused", refusal);
        }
    }

    /// <summary>The edit-layer stack, top to bottom.</summary>
    /// <remarks>
    ///     ⚠ <b>A reserved layer says which tool owns it rather than simply refusing.</b> Splines and
    ///     Scatter are regenerated wholesale, so a brush stroke into one would be erased the next
    ///     time anything regenerated it — and "nothing happened" is the worst possible way to learn
    ///     that.
    /// </remarks>
    void RefreshTerrainLayers() {
        if (terrainLayers is not { } list) {
            return;
        }

        Clear(list);

        if (terrain.Editing.Terrain is not { } map) {
            Fact(list, "No terrain", "create one above, or select an entity that has one");

            return;
        }

        for (var index = map.Layers.Count - 1; index >= 0; index--) {
            var layer = map.Layers[index];
            var row = list.Add("fact-row");

            row.Add("fact-name").Text = layer.Name;

            var state = layer.Kind switch {
                TerrainLayerKind.Splines => "reserved — the spline tool owns it",
                TerrainLayerKind.Scatter => "reserved — the growth simulation owns it",
                _ => layer.IsVisible ? "visible" : "hidden"
            };

            row.Add("fact-value").Add("text").Text = layer.IsLocked ? state + ", locked" : state;
        }
    }

    /// <summary>The foliage palette: what is in it, and which entries a stroke would place.</summary>
    void RefreshPalette() {
        if (foliagePalette is not { } list) {
            return;
        }

        Clear(list);

        if (foliage.Editing.Volume is not { } volume || volume.Palette.Count == 0) {
            // ⚠ An empty palette shows *why* it is empty. Entering a mode that does nothing and says
            // nothing is the state every one of these toolsets puts a new user in.
            Fact(list, "No types", foliage.Editing.Refusal ?? "add a .vxfoliage or .vxgrass to the palette");

            return;
        }

        for (var index = 0; index < volume.Palette.Count; index++) {
            var slot = index;
            var type = volume.Palette[slot];
            var row = list.Add("fact-row");
            var chosen = row.Add<CheckBox>();

            chosen.Label = type.Name;
            chosen.IsChecked = foliage.Editing.Chosen.Contains(slot);
            chosen.CheckedChanged += (_, on) => Choose(slot, on);

            row.Add("fact-value").Add("text").Text = type.Storage == FoliageStorage.Derived
                ? "derived — nothing about it is in any file"
                : $"stored, spacing {type.Radius:0.##} m";
        }
    }

    /// <summary>Adds or removes a palette entry from what a stroke would place.</summary>
    /// <remarks>
    ///     Additive, because painting two species at once is what an artist wants and re-choosing one
    ///     at a time is what a single-selection palette forces.
    /// </remarks>
    void Choose(int type, bool on) {
        if (on) {
            foliage.Editing.Choose(type, add: true);
        } else if (foliage.Editing.Chosen.Contains(type)) {
            var rest = foliage.Editing.Chosen.Where(entry => entry != type).ToArray();

            foliage.Editing.Choose(-1);

            foreach (var entry in rest) {
                foliage.Editing.Choose(entry, add: true);
            }
        }
    }

    /// <summary>Runs the growth simulation over the region the panel describes.</summary>
    /// <remarks>
    ///     ⚠ <b>Failures are a notification and not an exception.</b> This runs from a button's own
    ///     handler, where an exception takes the frame down with the scene unsaved — and the two
    ///     things that go wrong here (a region with no area, a volume with no ecology) are ordinary
    ///     things to meet.
    /// </remarks>
    void Grow() {
        if (foliage.Editing.Volume is not { } volume) {
            Shell.Notifications.Show(
                "There is no foliage volume to grow into",
                NotificationSeverity.Warning,
                "Enter foliage mode on a scene that has one, or add a type to the palette."
            );

            return;
        }

        if (growth.Validate() is { } refusal) {
            Shell.Notifications.Show("The growth settings were refused", NotificationSeverity.Warning, refusal);

            return;
        }

        if (foliage.Editing.Surface is not { } surface) {
            Shell.Notifications.Show(
                "There is no surface to grow on",
                NotificationSeverity.Warning,
                "A growth run needs ground to sow onto — a terrain, or anything else that answers IFoliageSurface."
            );

            return;
        }

        try {
            grown = FoliageGrowth.Simulate(volume, surface, growth.ToSettings());
            hasGrown = true;
        } catch (ArgumentException exception) {
            Shell.Notifications.Show("The growth simulation was refused", NotificationSeverity.Error, exception.Message);

            return;
        }

        RefreshGrowth();
        RefreshPalette();
    }

    /// <summary>What the last run produced, said rather than implied.</summary>
    void RefreshGrowth() {
        if (growthReport is not { } report) {
            return;
        }

        Clear(report);

        if (!hasGrown) {
            Fact(report, "Not run yet", "press Grow to sow the region");

            return;
        }

        Fact(report, "Placed", grown.Placed.ToString(CultureInfo.InvariantCulture));
        Fact(report, "Sown", grown.Sown.ToString(CultureInfo.InvariantCulture));
        Fact(report, "Sprouted", grown.Sprouted.ToString(CultureInfo.InvariantCulture));
        Fact(report, "Refused", grown.Refused.ToString(CultureInfo.InvariantCulture));

        // ⚠ The cap is the one refusal that has to be shouted. Spread is exponential until shade
        // catches up with it, so a region an author made ten times too large is ten thousand times
        // the plants — and a simulation that quietly stopped sowing reads as a rule that stopped
        // working rather than as a limit that bit.
        if (grown.Capped > 0) {
            Fact(report, "Capped", $"{grown.Capped} — raise the plant cap or shrink the region");
        }
    }

    /// <summary>Re-lays every road on the terrain, which is what a profile change means.</summary>
    /// <remarks>
    ///     ⚠ <b>Regenerate rather than deform, and the difference is a moved road.</b> Deforming
    ///     clears only the rect it is about to write, so a road that moved leaves its old cutting
    ///     behind for ever. The layer is reserved precisely so that emptying it and laying every road
    ///     down again is safe.
    /// </remarks>
    void RegenerateSplines() {
        if (terrain.Editing.Terrain is not TerrainMap map) {
            Shell.Notifications.Show(
                "There is no terrain to lay a road across",
                NotificationSeverity.Warning,
                "Create one in the Terrain panel first."
            );

            return;
        }

        // ⚠ Every road gets the panel's profile, which is the simplification this build ships with:
        // a profile per road is a property of the *spline asset* and there is no curve editor to put
        // one on yet. `Roads` is where a spline source plugs in, and it answers nothing today — so
        // this empties the reserved layer, which is the correct behaviour for "no roads".
        try {
            TerrainSpline.Regenerate(
                map,
                TerrainSpline.LayerOf(map, splines.LayerName),
                Roads().Select(road => (road, splines.ToProfile()))
            );
        } catch (ArgumentException exception) {
            Shell.Notifications.Show("The roads could not be regenerated", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>The roads on the terrain, which is where a spline source plugs in.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty, and named rather than absent.</b> Doc 20's first bar: a verb that is not
    ///     implemented is <i>visibly</i> not implemented. <see cref="ISplineSource" /> is the seam the
    ///     camera dolly already resolves a track through, and the editor has nothing to put in it
    ///     until a curve can be authored in the viewport.
    /// </remarks>
    static IEnumerable<Spline> Roads() => [];

    /// <summary>A section heading, which is what separates one panel's four parts.</summary>
    static void Section(DockPanel panel, string title) => panel.Add("world-title").Text = title;

    /// <summary>A row of verbs, each running a registered command.</summary>
    /// <remarks>
    ///     Through the command registry rather than by calling the mode, so a button, a menu item and
    ///     a key chord are one implementation — and so a verb that is not reachable right now is
    ///     greyed by the command's own enablement rather than by a second copy of the rule.
    /// </remarks>
    void Verbs(DockPanel panel, params (string Label, string Command)[] verbs) {
        var row = panel.Add("verb-row");

        foreach (var (label, command) in verbs) {
            var button = row.Add<Button>();

            button.Label = label;
            button.Clicked += _ => Shell.Commands.Execute(command);
        }
    }

    /// <summary>Empties a rows container before it is refilled.</summary>
    static void Clear(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}
