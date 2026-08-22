// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Water;

/// <summary>The two panels doc 35's Part 2 describes.</summary>
public sealed partial class WaterModule {
    /// <summary>What the zone panel is registered as.</summary>
    public const string ZonePanel = "water.zone";

    /// <summary>And the mode's own, which is the body inspector and the draw settings.</summary>
    public const string BodyPanel = WaterMode.PanelId;

    UiElement? zoneFacts;
    UiElement? waterFacts;
    UiElement? notice;

    /// <summary>Registers them.</summary>
    void WaterPanels() {
        Shell.RegisterPanel(
            ZonePanel,
            new StringId("editor.panel.water.zone", "Water Zone"),
            panel => {
                Contextual(panel, WaterMode.WaterContext);

                Section(panel, "Window");

                var zone = panel.Add<InspectorView>();

                zone.EditedDocument = null;
                zone.Inspect(Mode.Zone);

                zoneFacts = panel.Add("water-zone-facts");

                // ⚠ Redrawn on every change rather than behind a button, because the whole point of
                // the readout is that it is there *while* the numbers are being typed. A "recompute"
                // button would put the four-megabyte answer one press away from the person who was
                // about to press Create.
                zone.ValueChanged += (_, _) => RefreshZoneFacts();

                var made = panel.Add<Button>();

                made.Label = "Create zone";
                made.Clicked += _ => Create();

                RefreshZoneFacts();
            }
        );

        Shell.RegisterPanel(
            BodyPanel,
            new StringId("editor.panel.water", "Water"),
            panel => {
                Contextual(panel, WaterMode.WaterContext);

                Section(panel, "Draw");

                var draw = panel.Add<InspectorView>();

                draw.EditedDocument = null;
                draw.Inspect(Mode.Body);

                Verbs(
                    panel,
                    ("Finish", WaterMode.FinishCommand),
                    ("Undo point", WaterMode.UndoPointCommand),
                    ("Cancel", WaterMode.CancelCommand)
                );

                Section(panel, "Terrain");

                // ⚠ Carve first and Preview second, which is the order they mean something in: there
                // is nothing to preview until a body has been cut into the ground, and a Preview
                // toggle offered on its own is what made this flag look wired for two phases.
                Verbs(
                    panel,
                    ("Carve terrain", CarveTerrainCommand),
                    ("Preview carve", WaterMode.PreviewCarveCommand)
                );

                waterFacts = panel.Add("water-facts");
                notice = panel.Add("water-notice");

                RefreshWaterFacts();
            }
        );
    }

    /// <summary>The zone's derived numbers — what a resolution actually buys.</summary>
    /// <remarks>
    ///     ⚠ <b>And what it refuses.</b> <see cref="WaterZoneSettings.Validate" /> is the kernel's own
    ///     rule, so the panel says no to exactly what the renderer would: a snap grid that is not a
    ///     whole number of texels is the shoreline crawl § D3 warns about, and it is much cheaper to
    ///     read here than to find in a flythrough.
    /// </remarks>
    void RefreshZoneFacts() {
        if (zoneFacts is not { } facts) {
            return;
        }

        Clear(facts);

        foreach (var (label, value) in Mode.Zone.Facts()) {
            Fact(facts, label, value);
        }

        if (Mode.Zone.Validate() is { } why) {
            facts.Add("water-refusal").Add("text").Text = why;
        }
    }

    /// <summary>What the session has made, and why the last gesture did not.</summary>
    void RefreshWaterFacts() {
        if (waterFacts is { } facts) {
            Clear(facts);

            Fact(facts, "Bodies drawn", BodiesCreated.ToString());
            Fact(facts, "Points laid", Mode.Editing.Points.Count.ToString());

            // ⚠ The number that answers "why did Carve terrain do nothing". A verb greyed out with no
            // reason beside it is a verb an author decides is broken — doc 20's first bar.
            Fact(facts, "Terrains to carve", CarvableTerrains.ToString());
        }

        if (notice is { } shown) {
            Clear(shown);
        }
    }

    /// <summary>Says why something did not happen, where the person who tried it is looking.</summary>
    /// <remarks>
    ///     ⚠ <b>In the panel rather than a log line.</b> "I drew a lake and nothing appeared" has
    ///     exactly two causes — an unsaved scene and a write that failed — and both are recoverable in
    ///     one action. A message in a log the author is not reading is the same as no message, which
    ///     is doc 20's first bar.
    /// </remarks>
    void Report(string message) {
        if (notice is not { } shown) {
            return;
        }

        Clear(shown);
        shown.Add("water-refusal").Add("text").Text = message;
    }

    /// <summary>Places a zone, and says so when the settings refuse.</summary>
    void Create() {
        if (Mode.Zone.Validate() is { } why) {
            Report(why);

            return;
        }

        // Through the command rather than the mode, so a button, a menu item and a key chord are one
        // implementation — and so a verb that is not reachable right now is greyed by the command's
        // own enablement rather than by a second copy of the rule.
        if (!Shell.Commands.Execute(WaterMode.CreateZoneCommand)) {
            Report("Create zone is not available: there is no scene open to put one in.");

            return;
        }

        RefreshWaterFacts();
    }

    /// <summary>A row of verbs, each running a registered command.</summary>
    void Verbs(DockPanel panel, params (string Label, string Command)[] verbs) {
        var row = panel.Add("verb-row");

        foreach (var (label, command) in verbs) {
            var button = row.Add<Button>();

            button.Label = label;
            button.Clicked += _ => Shell.Commands.Execute(command);
        }
    }

    static void Section(DockPanel panel, string title) => panel.Add("world-title").Text = title;

    static void Clear(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}
