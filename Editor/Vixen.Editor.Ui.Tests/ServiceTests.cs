// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Notifications, theming, the string catalog and the user store.</summary>
public class ServiceTests : IDisposable {
    readonly UiDocument document = new(1280f, 800f);

    public ServiceTests() {
        ControlTheme.Install(document);
        EditorTheme.Install(document);
    }

    public void Dispose() {
        Strings.Use(null);

        document.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Notifications ───────────────────────────────────────────────────────

    [Fact]
    public void An_error_outlasts_an_ordinary_message_and_still_expires() {
        var toasts = document.Root.Add<ToastHost>();

        var centre = new NotificationCenter(toasts) {
            Duration = TimeSpan.FromSeconds(2),
            ErrorDuration = TimeSpan.FromSeconds(20)
        };

        centre.Show("Saved", NotificationSeverity.Success);
        centre.Error("Shader failed to compile", "Ui/Msdf.rvn(42): expected ';'");

        // ⚠ Not from zero. `ToastHost` uses a zero `Shown` as "has not started yet", so a first
        // tick at exactly zero leaves every toast unstarted and nothing ever expires. A host driven
        // by a stopwatch never sees exactly zero; a test can.
        centre.Tick(TimeSpan.FromSeconds(1));
        centre.Tick(TimeSpan.FromSeconds(10));

        // Long enough that the failure is still there after the success has gone…
        var live = Assert.Single(toasts.Live);
        Assert.Contains("Msdf.rvn", live.Message, StringComparison.Ordinal);

        // …and not so long that the corner of the window keeps it forever.
        centre.Tick(TimeSpan.FromSeconds(40));
        Assert.Empty(toasts.Live);
    }

    /// <summary>The one caller that wants the old behaviour can still have it.</summary>
    [Fact]
    public void An_error_can_be_asked_never_to_expire() {
        var toasts = document.Root.Add<ToastHost>();
        var centre = new NotificationCenter(toasts) { ErrorDuration = TimeSpan.MaxValue };

        centre.Error("Shader failed to compile");

        centre.Tick(TimeSpan.FromSeconds(1));
        centre.Tick(TimeSpan.FromDays(2));

        Assert.Single(toasts.Live);
    }

    [Fact]
    public void The_history_is_newest_first_and_bounded() {
        var centre = new NotificationCenter(document.Root.Add<ToastHost>()) { HistoryLimit = 3 };

        for (var i = 0; i < 10; i++) {
            centre.Show("message " + i);
        }

        Assert.Equal(3, centre.History.Count);
        Assert.Equal("message 9", centre.History[0].Message);
    }

    [Fact]
    public void Clearing_the_history_leaves_what_is_on_screen() {
        var toasts = document.Root.Add<ToastHost>();
        var centre = new NotificationCenter(toasts);

        centre.Show("Saved");
        centre.Clear();

        Assert.Empty(centre.History);
        Assert.Single(toasts.Live);
    }

    // ── Theming ─────────────────────────────────────────────────────────────

    [Fact]
    public void Switching_theme_is_one_class_on_the_root() {
        var theme = new ThemeService(document, ThemeMode.Light);
        Assert.False(document.Root.HasClass(ThemeService.DarkClass));

        theme.Toggle();

        Assert.Equal(ThemeMode.Dark, theme.Mode);
        Assert.True(document.Root.HasClass(ThemeService.DarkClass));
    }

    [Fact]
    public void A_user_theme_file_becomes_an_author_stylesheet() {
        var css = ThemeService.Compile(
            """
            light:
              accent: "#ff0000"
            dark:
              --accent: "#00ff00"
            """
        );

        // The quotes are the YAML's rather than the value's — `#` starts a comment unquoted — so
        // what reaches the stylesheet is the colour.
        Assert.Contains("root {", css, StringComparison.Ordinal);
        Assert.Contains("--accent: #ff0000;", css, StringComparison.Ordinal);

        // Written with or without the leading dashes, because a person editing this file will do
        // both.
        Assert.Contains("root.dark {", css, StringComparison.Ordinal);
        Assert.Contains("--accent: #00ff00;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flat_theme_file_is_read_as_the_light_theme() {
        var css = ThemeService.Compile("accent: blue");

        Assert.Contains("root {", css, StringComparison.Ordinal);
        Assert.DoesNotContain("root.dark", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_would_break_the_sheet_is_dropped() {
        var css = ThemeService.Compile(
            """
            light:
              accent: "blue} root { display: none"
              border: green
            """
        );

        // A value with a brace in it silently breaks every rule after it, which is a miserable
        // thing to debug in a file somebody hand-edited.
        Assert.DoesNotContain("display: none", css, StringComparison.Ordinal);
        Assert.Contains("--border: green;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Reloading_a_theme_replaces_the_sheet_rather_than_adding_one() {
        var theme = new ThemeService(document);

        theme.LoadTokens("light:\n  accent: red");
        theme.LoadTokens("light:\n  accent: blue");

        Assert.Equal("light:\n  accent: blue", theme.Tokens);
    }

    // ── Localisation ────────────────────────────────────────────────────────

    [Fact]
    public void A_string_with_no_translation_falls_back_to_the_source_text() {
        Strings.Use(new StringCatalog("cs"));

        Assert.Equal("Save", EditorStrings.CommandSave.Text);

        // The worst case is English rather than `editor.command.file.save`, which is what an editor
        // whose fallback is a file shows anybody with a missing install.
        Assert.Contains(EditorStrings.CommandSave.Id, Strings.Missing);
    }

    [Fact]
    public void A_translated_string_comes_from_the_catalog() {
        Strings.Use(new StringCatalog("cs").Set(EditorStrings.CommandSave.Id, "Uložit"));

        Assert.Equal("Uložit", EditorStrings.CommandSave.Text);
        Assert.DoesNotContain(EditorStrings.CommandSave.Id, Strings.Missing);
    }

    [Fact]
    public void A_catalog_round_trips_and_is_written_in_id_order() {
        var catalog = new StringCatalog("cs")
            .Set("z.last", "poslední")
            .Set("a.first", "první");

        var text = catalog.Save();
        Assert.True(text.IndexOf("a.first", StringComparison.Ordinal) < text.IndexOf("z.last", StringComparison.Ordinal));

        var reloaded = StringCatalogYaml.Load(text);

        Assert.Equal("cs", reloaded.Language);
        Assert.Equal("poslední", reloaded.Find("z.last"));
        Assert.Equal(text, reloaded.Save());
    }

    [Fact]
    public void A_template_holds_every_string_the_editor_declares() {
        var template = EditorStrings.Template("cs");
        Assert.Equal(EditorStrings.All.Count, template.Count);

        Strings.Use(template);

        foreach (var id in EditorStrings.All) {
            Assert.Equal(id.Source, id.Text);
        }

        Assert.Empty(Strings.Missing);
    }

    [Fact]
    public void Declared_ids_are_unique() {
        // Two strings answering to one id is a translation that changes both, and it is the sort of
        // thing a generator would catch and a hand-written table would not.
        var ids = EditorStrings.All.Select(id => id.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ── The user store ──────────────────────────────────────────────────────

    [Fact]
    public void Layouts_are_listed_by_name_without_the_one_the_editor_writes_itself() {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-editor-" + Guid.NewGuid().ToString("N"));
        var store = new EditorUserStore(directory);

        try {
            Assert.Empty(store.Layouts());
            Assert.Null(store.LoadLayout("Shading"));

            store.SaveLayout("Shading", "root:");
            store.SaveLayout("Animation", "root:");
            store.SaveLayout(EditorUserStore.CurrentLayout, "root:");

            Assert.Equal(["Animation", "Shading"], store.Layouts());
            Assert.Equal("root:", store.LoadLayout("Shading"));
            Assert.True(store.DeleteLayout("Shading"));
            Assert.Equal(["Animation"], store.Layouts());
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_layout_name_that_would_escape_the_directory_is_refused() =>
        // The name comes from a text box, and `../../keybindings.yaml` is a layout that overwrites
        // the keymap.
        Assert.Throws<ArgumentException>(() => EditorUserStore.FileFor("../../keybindings.yaml"));
}
