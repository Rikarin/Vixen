// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Terrain;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Editor.Water;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>A contributed Create ▸ line and overlay header follow a live language switch (#1284).</summary>
/// <remarks>
///     <para>
///         <b>Decision (a) of #1284</b>: <c>NewAssetKind.Title</c> and <c>SceneOverlay.Title</c> carry
///         a <c>StringId</c>, because the editor switches language live and a string is frozen at
///         whatever language was in use when the kind registered. Each assertion is on what is
///         <i>rendered</i> — a menu line's label, a dialog's row, a pane's header — before and after
///         <c>Strings.Use</c>, and never on the record.
///     </para>
///     <para>
///         ⚠ <b>Not every half was frozen before, and saying which is the instrument check.</b> The
///         menu-bar line was already live: the editor wrapped the string in
///         <c>editor.command.</c> + the kind's id when it made the command, and a catalogue holding
///         that id relabelled it — but no declaration held the id, so no translator's template could.
///         What was frozen is everything that read the record's string: the New Asset… chooser's rows
///         and every overlay's header. Those two are red against the string records; the menu line is
///         the regression guard on the move.
///     </para>
///     <para>
///         ⚠ <b><c>Strings.Use(null)</c> in a <c>finally</c>, always</b> — the language is process-wide
///         (<c>StringCatalogChainTests</c> says why).
///     </para>
/// </remarks>
public sealed class ContributionTitleLanguageTests {
    /// <summary>Three Create ▸ lines — the editor's own, a module's in code, a module's by attribute — and the chooser.</summary>
    [Fact]
    public void Switching_the_language_relabels_the_create_lines_and_the_new_asset_chooser() {
        using var session = EditorSession.Start();

        var scene = EditorStrings.CreateAssetKinds["scene"];
        var sea = WaterStrings.Commands[WaterModule.CreateWavesCommand];
        var layer = TerrainStrings.CreateTerrainLayer;

        try {
            var before = CreateLines(session);

            Assert.Contains(scene.Source, before);
            Assert.Contains(sea.Source, before);
            Assert.Contains(layer.Source, before);

            Strings.Use(
                new StringCatalog("cs")
                    .Set(scene.Id, "Scéna")
                    .Set(sea.Id, "Stav moře")
                    .Set(layer.Id, "Vrstva terénu")
            );
            session.Settle();

            var after = CreateLines(session);

            Assert.Contains("Scéna", after);
            Assert.Contains("Stav moře", after);
            Assert.Contains("Vrstva terénu", after);
            Assert.DoesNotContain(scene.Source, after);
            Assert.DoesNotContain(sea.Source, after);
            Assert.DoesNotContain(layer.Source, after);

            // The chooser New Asset… opens, built from the same records — a string here was the one
            // place a Create ▸ title was read as text, and it stayed English.
            session.Run("assets.create").Settle();
            Assert.True(session.IsAsking);

            var rows = ChooserRows(session);

            Assert.Contains("Scéna", rows);
            Assert.Contains("Stav moře", rows);
            Assert.Contains("Vrstva terénu", rows);
            Assert.DoesNotContain(scene.Source, rows);

            session.Answer("Scéna");
            session.Settle();
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>A contributed overlay's header on the scene pane follows a live switch.</summary>
    [Fact]
    public void Switching_the_language_relabels_an_overlay_header_on_the_pane() {
        using var session = EditorSession.Start();

        var title = new StringId("test.overlay.compass", "Compass Rose");

        using var removal = session.Extensions.Add(new SceneOverlay("test.compass", title, static (_, _) => { }));

        try {
            session.Open("scene");
            session.Frames(2);

            session.Ui.Contains("Compass Rose").ShouldExist();

            Strings.Use(new StringCatalog("cs").Set(title.Id, "Větrná růžice"));
            session.Frames(2);

            session.Ui.Contains("Větrná růžice").ShouldExist();
            session.Ui.Contains("Compass Rose").ShouldNotExist();
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>What Assets ▸ Create says, line by line, as the menu bar renders it.</summary>
    static List<string> CreateLines(EditorSession session) {
        var assets = Assert.Single(session.Shell.MenuBar.Bar.Items, item => item.Label == EditorStrings.MenuAssets.Text);
        var create = Assert.Single(assets.Menu.Items, item => item.Label == EditorStrings.MenuCreate.Text);

        Assert.NotNull(create.Submenu);

        return [.. create.Submenu.Items.Select(static item => item.Label ?? string.Empty)];
    }

    static List<string> ChooserRows(EditorSession session) =>
        session.Shell.Dialogs.Current is { } dialog
            ? [.. Descendants(dialog).OfType<Button>().Where(static button => button.HasClass("choice")).Select(static button => button.Label ?? string.Empty)]
            : [];

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
