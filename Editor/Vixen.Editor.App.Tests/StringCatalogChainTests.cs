// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Issue 1229: export a template, translate it, and see the running editor change language.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every link of this chain was finished and none of them was called.</b>
///         <c>Strings.Use</c>, <c>StringCatalogYaml.Save</c>, <c>StringCatalogYaml.Load</c> and
///         <c>EditorStrings.Template</c> had zero production callers between them — the whole of it
///         was exercised end to end inside one test method and wired to nothing, which is the shape
///         <c>docs/plan/46</c> § A3 warns about. <c>Strings</c> carries a <c>Signal</c> and a
///         <c>Changed</c> event precisely so that a language change re-labels a running interface,
///         and nothing in this repository had ever changed one.
///     </para>
///     <para>
///         ⚠ <b>So the assertion is a label on a live menu, not a round trip through the format.</b>
///         A test that saved a catalog and loaded it back is what already existed, and it passes with
///         no editor attached to either end. What only this can say is that the preference reaches
///         <c>Strings.Use</c> and that <c>MenuPresenter</c>'s subscription rebuilds the bar.
///     </para>
///     <para>
///         ⚠ <b><c>Strings.Use(null)</c> in a <c>finally</c>, always.</b> The signal is static and
///         process-wide, so a test that left a catalog installed would re-label whatever suite ran
///         next — the same hazard <c>MenuPresenter</c> is <c>IDisposable</c> for, and the reason
///         this assembly disables collection parallelism.
///     </para>
/// </remarks>
public class StringCatalogChainTests {
    /// <summary>The export writes a catalog holding the editor's own words, under the project.</summary>
    /// <remarks>
    ///     The id and its source text are both asserted, because a template whose values were empty
    ///     is what a translator would call a broken export and what a round-trip test would call a
    ///     pass: <c>Save</c> writes an empty scalar for an id with no text, and <c>Load</c> reads it
    ///     back identically.
    /// </remarks>
    [Fact]
    public void Exporting_the_string_template_writes_the_editors_words_into_the_project() {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.CanRun("tools.export-strings"));

        fixture.Run("tools.export-strings").Settle();

        var path = Path.Combine(fixture.Project.Paths.Root, "Localization", "source.yaml");

        Assert.True(File.Exists(path), $"nothing was written to {path}");

        var catalog = StringCatalogYaml.Load(File.ReadAllText(path));

        Assert.Equal("source", catalog.Language);
        Assert.Equal(EditorStrings.MenuFile.Source, catalog.Find(EditorStrings.MenuFile.Id));
        Assert.Equal(EditorStrings.CommandToolsExportStrings.Source, catalog.Find(EditorStrings.CommandToolsExportStrings.Id));

        // Every id the editor declares, rather than a handful: `Template` is `All` plus whatever
        // toolsets have contributed, so a template short of `All` is one somebody would translate
        // fully and still meet English in.
        foreach (var declared in EditorStrings.All) {
            Assert.NotNull(catalog.Find(declared.Id));
        }
    }

    /// <summary>An existing catalog is left alone, because it is somebody's afternoon.</summary>
    [Fact]
    public void Exporting_over_a_translation_that_already_exists_does_not_overwrite_it() {
        using var fixture = EditorSession.Start();

        var directory = Path.Combine(fixture.Project.Paths.Root, "Localization");

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "source.yaml");

        File.WriteAllText(path, "language: source\nstrings:\n  editor.menu.file: \"Kept\"\n");

        fixture.Run("tools.export-strings").Settle();

        Assert.Equal("Kept", StringCatalogYaml.Load(File.ReadAllText(path)).Find(EditorStrings.MenuFile.Id));
    }

    /// <summary>
    ///     Naming a language in the preferences re-labels the menus of the editor that is running.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Through Apply rather than by writing the file and restarting</b>, which is the whole
    ///     claim: the file-and-restart arrangement is what an application with no reactive language
    ///     would need, and asserting it would pass against one. What is asserted is a bar that was
    ///     built before the catalog existed and says a different word afterwards.
    /// </remarks>
    [Fact]
    public void Choosing_a_language_relabels_the_menus_of_a_running_editor() {
        using var fixture = EditorSession.Start();

        var directory = Path.Combine(fixture.Project.Paths.Root, "Localization");

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "cs.yaml"),
            new StringCatalog("cs").Set(EditorStrings.MenuFile.Id, "Soubor").Save()
        );

        try {
            Assert.Contains(EditorStrings.MenuFile.Source, Labels(fixture));
            Assert.DoesNotContain("Soubor", Labels(fixture));

            Apply(fixture, "cs");

            Assert.Contains("Soubor", Labels(fixture));
            Assert.DoesNotContain(EditorStrings.MenuFile.Source, Labels(fixture));

            // And back, which is the edge a one-way switch passes and a language picker has to have.
            Apply(fixture, string.Empty);

            Assert.Contains(EditorStrings.MenuFile.Source, Labels(fixture));
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>A language whose catalog is not there says so rather than looking untranslated.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check.</b> Falling back to the source text is right — an editor that
    ///     would not open because a translation was not checked out is useless — and it is also
    ///     exactly what a working translation looks like to somebody whose file is in the wrong
    ///     folder. Without the notification the two states are one state.
    /// </remarks>
    [Fact]
    public void A_language_with_no_catalog_falls_back_and_says_it_did() {
        using var fixture = EditorSession.Start();

        try {
            Apply(fixture, "cs");

            Assert.Contains(EditorStrings.MenuFile.Source, Labels(fixture));

            var said = Assert.Single(
                fixture.Shell.Notifications.History,
                notice => notice.Severity == NotificationSeverity.Warning
                    && notice.Message.Contains("language", StringComparison.OrdinalIgnoreCase)
            );

            Assert.Contains("cs", said.Detail, StringComparison.Ordinal);
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>An editor whose preference names a language opens in it.</summary>
    /// <remarks>
    ///     ⚠ <b>The path the runtime test above cannot reach, and it is the one with a hazard in
    ///     it.</b> <c>LoadPreferences</c> runs from the application's constructor, so the load side
    ///     of the chain touches the shell's notification centre before anything has drawn — and
    ///     "the editor will not start when a translation is missing" would be a far worse failure
    ///     than an untranslated menu.
    /// </remarks>
    [Fact]
    public void An_editor_whose_preference_names_a_language_starts_in_it() {
        using var scope = new Scratch();
        using var fixture = EditorSession.Start(new EditorSessionOptions { DataDirectory = scope.Directory });

        var directory = Path.Combine(fixture.Project.Paths.Root, "Localization");

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "cs.yaml"),
            new StringCatalog("cs").Set(EditorStrings.MenuFile.Id, "Soubor").Save()
        );

        File.WriteAllText(Path.Combine(scope.Directory, EditorUserStore.PreferencesFile), "language: cs\n");

        try {
            fixture.Restart();

            Assert.Contains("Soubor", Labels(fixture));
        } finally {
            Strings.Use(null);
        }
    }

    /// <summary>Types a language into the General page and presses Apply, the way a person does.</summary>
    static void Apply(EditorSession fixture, string language) {
        var view = fixture.Control<SettingsView>("preferences");

        Assert.True(view.Select("general"));
        fixture.Settle();

        Type(view, nameof(EditorPreferences.Language), language);
        fixture.Settle();

        fixture.Click(view.Apply);
        fixture.Settle();
    }

    /// <summary>What the menu bar says right now.</summary>
    static List<string> Labels(EditorSession fixture) =>
        [.. fixture.Shell.MenuBar.Bar.Items.Select(item => item.Label ?? string.Empty)];

    /// <summary>Types into the inspector row for a member, the way a person does.</summary>
    static void Type(SettingsView view, string member, string value) {
        foreach (var row in Descendants(view.Pane).OfType<InspectorRow>()) {
            if (string.Equals(row.Field.Member.Name, member, StringComparison.Ordinal)) {
                row.Field.Write(value);
                return;
            }
        }

        throw new InvalidOperationException($"no row for '{member}'");
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
